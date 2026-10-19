//! HTTP surface: session management, the message entrance, and the SSE
//! event exit.

use std::convert::Infallible;
use std::sync::Arc;

use alcoler_contracts::ids::{CommandId, SessionId};
use alcoler_contracts::message::{ContentBlock, Message, OutputBlock, Role};
use alcoler_contracts::storage::{SessionProfile, SessionStore};
use alcoler_contracts::time::IdGenerator;
use alcoler_contracts::wire::{CancelScope, Command, UserInput};
use axum::extract::{Path, State};
use axum::http::{StatusCode, header};
use axum::response::sse::{Event, KeepAlive, Sse};
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use axum::{Json, Router};
use futures_util::StreamExt;
use futures_util::stream::{self, Stream};
use serde_json::{Value, json};
use tokio::sync::{broadcast, mpsc};
use tokio_stream::wrappers::BroadcastStream;

use crate::projection::{Projection, Row};

#[derive(Clone)]
pub struct AppState {
    pub cmd_tx: mpsc::Sender<Command>,
    pub events: broadcast::Sender<Arc<String>>,
    pub projection: Projection,
    pub store: Arc<dyn SessionStore>,
    pub ids: Arc<dyn IdGenerator>,
    pub profile: SessionProfile,
    pub models: ModelCatalog,
}

/// Selectable models for new sessions. `available` is a hint: providers need
/// credentials or a custom base URL to actually serve requests.
#[derive(Clone, serde::Serialize, serde::Deserialize)]
pub struct ModelEntry {
    pub provider: String,
    pub model: String,
    pub available: bool,
}

#[derive(Clone, serde::Serialize, serde::Deserialize)]
pub struct ModelCatalog {
    pub default: ModelEntry,
    pub entries: Vec<ModelEntry>,
}

pub fn router(state: AppState) -> Router {
    Router::new()
        .route("/", get(index))
        .route("/app.js", get(app_js))
        .route("/style.css", get(style_css))
        .route("/api/models", get(list_models))
        .route("/api/sessions", get(list_sessions).post(create_session))
        .route("/api/sessions/{id}/events", get(session_events))
        .route("/api/sessions/{id}/history", get(session_history))
        .route("/api/sessions/{id}/messages", post(post_message))
        .route("/api/sessions/{id}/cancel", post(cancel_session))
        .with_state(state)
}

async fn index() -> Response {
    (
        [(header::CONTENT_TYPE, "text/html; charset=utf-8")],
        include_str!("../static/index.html"),
    )
        .into_response()
}

async fn app_js() -> Response {
    (
        [(header::CONTENT_TYPE, "text/javascript; charset=utf-8")],
        include_str!("../static/app.js"),
    )
        .into_response()
}

async fn style_css() -> Response {
    (
        [(header::CONTENT_TYPE, "text/css; charset=utf-8")],
        include_str!("../static/style.css"),
    )
        .into_response()
}

async fn list_sessions(State(state): State<AppState>) -> Response {
    match state.store.list_sessions().await {
        Ok(list) => Json(list).into_response(),
        Err(e) => (StatusCode::INTERNAL_SERVER_ERROR, e.message).into_response(),
    }
}

async fn list_models(State(state): State<AppState>) -> Response {
    Json(state.models).into_response()
}

#[derive(serde::Deserialize, Default)]
struct CreateSession {
    provider: Option<String>,
    model: Option<String>,
}

async fn create_session(
    State(state): State<AppState>,
    body: Option<Json<CreateSession>>,
) -> Response {
    let mut profile = state.profile.clone();
    if let Some(Json(CreateSession { provider, model })) = body {
        let (Some(provider), Some(model)) = (provider, model) else {
            return (
                StatusCode::BAD_REQUEST,
                "provider and model must be given together",
            )
                .into_response();
        };
        if model.trim().is_empty() {
            return (StatusCode::BAD_REQUEST, "model must not be empty").into_response();
        }
        if !state
            .models
            .entries
            .iter()
            .any(|entry| entry.provider == provider)
        {
            return (
                StatusCode::BAD_REQUEST,
                format!("unknown provider '{provider}'"),
            )
                .into_response();
        }
        profile.model = alcoler_contracts::model::ModelRef { provider, model };
    }
    let id = SessionId(format!("s-{}", state.ids.next_id()));
    if state
        .cmd_tx
        .send(Command::StartSession {
            session: id.clone(),
            profile,
        })
        .await
        .is_err()
    {
        return (StatusCode::SERVICE_UNAVAILABLE, "kernel is shutting down").into_response();
    }
    // Wait until the kernel persisted the session so the list reflects it.
    for _ in 0..50 {
        if let Ok(list) = state.store.list_sessions().await
            && list.iter().any(|s| s.id == id)
        {
            break;
        }
        tokio::time::sleep(std::time::Duration::from_millis(100)).await;
    }
    (StatusCode::CREATED, Json(json!({ "id": id.0 }))).into_response()
}

async fn session_events(
    State(state): State<AppState>,
    Path(id): Path<String>,
) -> Sse<impl Stream<Item = Result<Event, Infallible>>> {
    let session = SessionId(id);
    // Idempotent on the kernel side: a live session no-ops; a session from a
    // previous run takes the recovery path so submissions are accepted.
    let _ = state
        .cmd_tx
        .send(Command::StartSession {
            session: session.clone(),
            profile: state.profile.clone(),
        })
        .await;
    let rx = state.events.subscribe();
    let rows = state.projection.snapshot(&session).await;
    let snapshot = serde_json::to_string(&rows).unwrap_or_else(|_| "[]".into());

    let session_key = session.0;
    let live = BroadcastStream::new(rx).filter_map(move |frame| {
        let out: Option<Result<Event, Infallible>> = (|| {
            let frame = frame.ok()?;
            let value: Value = serde_json::from_str(frame.as_str()).ok()?;
            let matches = match value.get("session") {
                None | Some(Value::Null) => true,
                Some(s) => s.as_str() == Some(session_key.as_str()),
            };
            if !matches {
                return None;
            }
            Some(Ok(Event::default().data(frame.as_str())))
        })();
        futures_util::future::ready(out)
    });

    let first = stream::once(async move {
        Ok::<_, Infallible>(Event::default().event("snapshot").data(snapshot))
    });
    Sse::new(first.chain(live)).keep_alive(KeepAlive::default())
}

async fn session_history(State(state): State<AppState>, Path(id): Path<String>) -> Response {
    let session = SessionId(id);
    match state.store.load_session(&session).await {
        Ok(Some(record)) => {
            let (turns, rows) = match &record.snapshot {
                Some(snapshot) => (snapshot.turns_completed, history_rows(&snapshot.messages)),
                None => (0, Vec::new()),
            };
            let model = &record.meta.profile.model;
            Json(json!({
                "session": session.0,
                "turns_completed": turns,
                "model": { "provider": model.provider, "model": model.model },
                "rows": rows,
            }))
            .into_response()
        }
        Ok(None) => (StatusCode::NOT_FOUND, "unknown session").into_response(),
        Err(e) => (StatusCode::INTERNAL_SERVER_ERROR, e.message).into_response(),
    }
}

/// Map persisted messages to transcript rows. `turn` stays 0: history rows
/// are closed, and only open-turn rows need the turn key.
fn history_rows(messages: &[Message]) -> Vec<Row> {
    let mut rows = Vec::new();
    for (i, message) in messages.iter().enumerate() {
        match message.role {
            Role::User => {
                for block in &message.blocks {
                    if let ContentBlock::Text { text } = block {
                        rows.push(Row::UserText { text: text.clone() });
                    }
                }
            }
            Role::Assistant => {
                for block in &message.blocks {
                    match block {
                        ContentBlock::Text { text } if !text.is_empty() => {
                            rows.push(Row::AssistantText {
                                turn: 0,
                                text: text.clone(),
                                open: false,
                                last_seq: u64::MAX,
                            });
                        }
                        ContentBlock::ToolUse {
                            id,
                            name,
                            arguments,
                        } => {
                            let result = messages.get(i + 1).and_then(|next| {
                                next.blocks.iter().find_map(|b| {
                                    if let ContentBlock::ToolResult {
                                        tool_use_id,
                                        is_error,
                                        content,
                                    } = b
                                    {
                                        (tool_use_id == id).then_some((*is_error, content))
                                    } else {
                                        None
                                    }
                                })
                            });
                            let (status, bytes) = match result {
                                Some((is_error, content)) => {
                                    let bytes = content
                                        .iter()
                                        .map(|c| match c {
                                            OutputBlock::Text { text } => text.len() as u64,
                                            OutputBlock::Json { value } => {
                                                value.to_string().len() as u64
                                            }
                                        })
                                        .sum();
                                    (
                                        Some(if is_error { "failed" } else { "ok" }.to_owned()),
                                        bytes,
                                    )
                                }
                                None => (None, 0),
                            };
                            rows.push(Row::ToolUse {
                                turn: 0,
                                call: id.0.clone(),
                                tool: name.clone(),
                                input: arguments.clone(),
                                status,
                                bytes,
                            });
                        }
                        ContentBlock::Text { .. }
                        | ContentBlock::Reasoning { .. }
                        | ContentBlock::ToolResult { .. } => {}
                    }
                }
            }
        }
    }
    rows
}

#[derive(serde::Deserialize)]
struct PostMessage {
    text: String,
}

async fn post_message(
    State(state): State<AppState>,
    Path(id): Path<String>,
    Json(body): Json<PostMessage>,
) -> Response {
    if body.text.trim().is_empty() {
        return (StatusCode::BAD_REQUEST, "empty text").into_response();
    }
    let session = SessionId(id);
    // The channel is FIFO: announcing first guarantees the kernel knows the
    // session (recovery path for pre-restart sessions) before the Submit.
    let _ = state
        .cmd_tx
        .send(Command::StartSession {
            session: session.clone(),
            profile: state.profile.clone(),
        })
        .await;
    let sent = state
        .cmd_tx
        .send(Command::Submit {
            session: session.clone(),
            command: CommandId(state.ids.next_id()),
            input: UserInput {
                text: body.text.clone(),
            },
        })
        .await;
    if sent.is_err() {
        return (StatusCode::SERVICE_UNAVAILABLE, "kernel is shutting down").into_response();
    }
    state.projection.push_user(&session, &body.text).await;
    StatusCode::ACCEPTED.into_response()
}

async fn cancel_session(State(state): State<AppState>, Path(id): Path<String>) -> Response {
    let sent = state
        .cmd_tx
        .send(Command::Cancel {
            session: SessionId(id),
            scope: CancelScope::Session,
        })
        .await;
    if sent.is_err() {
        return (StatusCode::SERVICE_UNAVAILABLE, "kernel is shutting down").into_response();
    }
    StatusCode::ACCEPTED.into_response()
}
