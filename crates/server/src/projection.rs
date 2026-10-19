//! Live transcript projection of the open turn.
//!
//! Completed turns are served from the store (history endpoint); this
//! projection reduces broadcast events into rows only while a turn is open
//! and clears them on `TurnDone`, so the two sources never overlap. An SSE
//! client connecting mid-turn gets an up-to-the-second snapshot.

use std::collections::HashMap;
use std::sync::Arc;

use alcoler_contracts::ids::SessionId;
use alcoler_contracts::tool::ToolStatus;
use alcoler_contracts::wire::KernelEvent;
use serde::Serialize;
use serde_json::Value;
use tokio::sync::{Mutex, broadcast};
use tokio::task::JoinHandle;

/// One transcript row (the wire shape shared by snapshot and history).
#[derive(Debug, Clone, Serialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum Row {
    UserText {
        text: String,
    },
    AssistantText {
        turn: u64,
        text: String,
        open: bool,
        last_seq: u64,
    },
    ToolUse {
        turn: u64,
        call: String,
        tool: String,
        input: Value,
        /// `None` while the call is running.
        status: Option<String>,
        bytes: u64,
    },
    Error {
        code: String,
        fatal: bool,
    },
}

impl Row {
    pub fn status_label(status: &ToolStatus) -> String {
        match status {
            ToolStatus::Ok => "ok".into(),
            ToolStatus::Failed { .. } => "failed".into(),
            ToolStatus::TimedOut => "timed_out".into(),
            ToolStatus::Cancelled => "cancelled".into(),
        }
    }
}

#[derive(Clone, Default)]
pub struct Projection {
    sessions: Arc<Mutex<HashMap<SessionId, Vec<Row>>>>,
}

impl Projection {
    pub fn new() -> Self {
        Self::default()
    }

    pub async fn snapshot(&self, id: &SessionId) -> Vec<Row> {
        self.sessions
            .lock()
            .await
            .get(id)
            .cloned()
            .unwrap_or_default()
    }

    /// User input is not a kernel event; the message entrance pushes the row
    /// directly. It is cleared together with the turn's rows.
    pub async fn push_user(&self, id: &SessionId, text: &str) {
        self.sessions
            .lock()
            .await
            .entry(id.clone())
            .or_default()
            .push(Row::UserText {
                text: text.to_owned(),
            });
    }

    /// Reduce the broadcast feed until the channel closes (process exit).
    pub fn spawn(self, events: broadcast::Sender<Arc<String>>) -> JoinHandle<()> {
        tokio::spawn(self.run(events.subscribe()))
    }

    async fn run(self, mut rx: broadcast::Receiver<Arc<String>>) {
        loop {
            match rx.recv().await {
                Ok(frame) => {
                    if let Ok(event) = serde_json::from_str::<KernelEvent>(&frame) {
                        self.apply(event).await;
                    }
                }
                Err(broadcast::error::RecvError::Lagged(_)) => continue,
                Err(broadcast::error::RecvError::Closed) => break,
            }
        }
    }

    async fn apply(&self, event: KernelEvent) {
        match event {
            KernelEvent::TurnStarted { session, turn, .. } => {
                self.sessions
                    .lock()
                    .await
                    .entry(session)
                    .or_default()
                    .push(Row::AssistantText {
                        turn: turn.0,
                        text: String::new(),
                        open: true,
                        last_seq: 0,
                    });
            }
            KernelEvent::ModelDelta {
                session,
                turn,
                seq,
                text,
                ..
            } => {
                let mut sessions = self.sessions.lock().await;
                let rows = sessions.entry(session).or_default();
                let needs_row = !rows.iter().any(
                    |r| matches!(r, Row::AssistantText { turn: t, open: true, .. } if *t == turn.0),
                );
                if needs_row {
                    rows.push(Row::AssistantText {
                        turn: turn.0,
                        text: String::new(),
                        open: true,
                        last_seq: 0,
                    });
                }
                if let Some(Row::AssistantText {
                    text: buffer,
                    last_seq: seen,
                    ..
                }) = rows.iter_mut().rev().find(
                    |r| matches!(r, Row::AssistantText { turn: t, open: true, .. } if *t == turn.0),
                ) && seq > *seen
                {
                    buffer.push_str(&text);
                    *seen = seq;
                }
            }
            KernelEvent::ToolCall {
                session,
                turn,
                call,
                tool,
                input,
                ..
            } => {
                self.sessions
                    .lock()
                    .await
                    .entry(session)
                    .or_default()
                    .push(Row::ToolUse {
                        turn: turn.0,
                        call: call.0,
                        tool,
                        input,
                        status: None,
                        bytes: 0,
                    });
            }
            KernelEvent::ToolResult {
                session,
                call,
                status,
                bytes,
                ..
            } => {
                let mut sessions = self.sessions.lock().await;
                let Some(rows) = sessions.get_mut(&session) else {
                    return;
                };
                if let Some(Row::ToolUse {
                    status: slot,
                    bytes: total,
                    ..
                }) = rows.iter_mut().rev().find(
                    |r| matches!(r, Row::ToolUse { call: c, status: None, .. } if *c == call.0),
                ) {
                    *slot = Some(Row::status_label(&status));
                    *total = bytes;
                }
            }
            KernelEvent::TurnDone { session, .. } => {
                // The turn is persisted; history is authoritative from here.
                if let Some(rows) = self.sessions.lock().await.get_mut(&session) {
                    rows.clear();
                }
            }
            KernelEvent::Error {
                session: Some(session),
                code,
                fatal,
                ..
            } => {
                self.sessions
                    .lock()
                    .await
                    .entry(session)
                    .or_default()
                    .push(Row::Error { code, fatal });
            }
            KernelEvent::SessionStarted { .. }
            | KernelEvent::SessionRecovered { .. }
            | KernelEvent::Compacted { .. }
            | KernelEvent::Error { session: None, .. } => {}
        }
    }
}
