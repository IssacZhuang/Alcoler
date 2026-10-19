//! The turn loop: model-step streaming, message assembly, provider retry,
//! and turn finalization (atomic `append_turn`).

use std::sync::Arc;

use alcoler_contracts::ids::{SessionId, ToolCallId, TurnId};
use alcoler_contracts::message::{ContentBlock, Message, Role};
use alcoler_contracts::model::{
    CompletionRequest, ModelStreamEvent, ProviderError, ProviderErrorKind, ToolChoice,
};
use alcoler_contracts::storage::{SessionSnapshot, StoreError, StoreErrorKind, TurnRecord};
use alcoler_contracts::time::CancelToken;
use alcoler_contracts::wire::{EventSink, KernelEvent, UserInput};
use futures_util::StreamExt;
use futures_util::future::{Either, select};
use serde_json::Value;

use crate::KernelInner;
use crate::cancel::TurnCancel;
use crate::events::EventLog;
use crate::session::Session;

/// A model-emitted tool call with fully assembled arguments. `arguments`
/// degrades to `{`}` when assembly failed; `arguments_error` then carries the
/// reason into a paired business-error result (never a model-request
/// failure).
pub(crate) struct AssembledCall {
    pub id: ToolCallId,
    pub name: String,
    pub arguments: Value,
    pub arguments_error: Option<String>,
}

struct StepResult {
    reasoning: Option<(String, Option<String>)>,
    text: String,
    tool_calls: Vec<AssembledCall>,
    cancelled: bool,
}

impl StepResult {
    fn has_content(&self) -> bool {
        !self.text.is_empty()
            || self
                .reasoning
                .as_ref()
                .is_some_and(|(text, _)| !text.is_empty())
            || !self.tool_calls.is_empty()
    }

    /// The assistant message for this step. Only complete blocks are
    /// committed: tool calls with missing names are omitted entirely from
    /// the message (they were never executable), so no unpaired tool_use
    /// can enter history.
    fn assistant_message(&self) -> Message {
        let mut blocks = Vec::new();
        if let Some((text, signature)) = &self.reasoning
            && !text.is_empty()
        {
            blocks.push(ContentBlock::Reasoning {
                text: text.clone(),
                signature: signature.clone(),
            });
        }
        if !self.text.is_empty() {
            blocks.push(ContentBlock::Text {
                text: self.text.clone(),
            });
        }
        for call in &self.tool_calls {
            blocks.push(ContentBlock::ToolUse {
                id: call.id.clone(),
                name: call.name.clone(),
                arguments: call.arguments.clone(),
            });
        }
        Message {
            role: Role::Assistant,
            blocks,
        }
    }
}

pub(crate) async fn run_turn(
    inner: Arc<KernelInner>,
    session: Arc<Session>,
    turn: TurnId,
    input: UserInput,
    cancel: Arc<TurnCancel>,
    sink: Arc<dyn EventSink>,
) {
    let session_id = session.meta.id.clone();
    let mut log = EventLog::new(inner.deps.clock.clone());
    let mut new_messages: Vec<Message> = Vec::new();

    log.emit(
        &sink,
        KernelEvent::TurnStarted {
            session: session_id.clone(),
            turn,
            ts_ms: log.now(),
        },
    )
    .await;

    let user_message = Message::user_text(input.text);
    session.history.lock().await.push(user_message.clone());
    new_messages.push(user_message);

    loop {
        if cancel.is_cancelled() {
            break;
        }

        crate::context::compact_if_needed(&inner, &session, &mut log, &sink).await;

        let request = build_request(&inner, &session).await;
        let step = match stream_step(
            &inner,
            &request,
            &cancel,
            &sink,
            &mut log,
            &session_id,
            turn,
        )
        .await
        {
            Ok(step) => step,
            Err(_) => break, // provider failure surfaced as an Error event; nothing to salvage
        };

        let assistant = step.assistant_message();
        if !assistant.blocks.is_empty() {
            session.history.lock().await.push(assistant.clone());
            new_messages.push(assistant);
        }
        if step.tool_calls.is_empty() {
            break;
        }

        let stop = crate::toolset::execute_tool_calls(
            &inner,
            &session,
            turn,
            &step.tool_calls,
            &cancel,
            &sink,
            &mut log,
            &mut new_messages,
        )
        .await;
        if stop {
            break;
        }
    }

    let snapshot = {
        let history = session.history.lock().await;
        SessionSnapshot {
            messages: history.clone(),
            turns_completed: turn.0,
        }
    };
    let done = KernelEvent::TurnDone {
        session: session_id.clone(),
        turn,
        ts_ms: log.now(),
    };
    log.record(done.clone());
    let record = TurnRecord {
        session: session_id.clone(),
        turn,
        new_messages,
        events: log.finish(),
        snapshot,
    };
    match inner.deps.store.append_turn(record).await {
        // Conflict means this turn was already persisted (idempotent dedup).
        Ok(())
        | Err(StoreError {
            kind: StoreErrorKind::Conflict,
            ..
        }) => {}
        Err(error) => {
            let _ = error;
            let _ = sink
                .emit(KernelEvent::Error {
                    session: Some(session_id.clone()),
                    code: "store_write_failed".to_string(),
                    fatal: false,
                    ts_ms: inner.deps.clock.monotonic_ms(),
                })
                .await;
        }
    }
    let _ = sink.emit(done).await;

    // Release the active-turn slot, then chain the next queued input.
    let mut control = session.control.lock().await;
    control.turns_completed = control.turns_completed.max(turn.0);
    control.active = None;
    if let Some(next) = control.queue.pop_front() {
        inner
            .clone()
            .begin_turn(&session, &mut control, next.input, sink);
    }
}

async fn build_request(inner: &Arc<KernelInner>, session: &Arc<Session>) -> CompletionRequest {
    let tools = inner
        .registry
        .values()
        .filter(|entry| entry.tool.spec().policy.provider_visible)
        .map(|entry| entry.tool.spec().clone())
        .collect();
    CompletionRequest {
        model: session.meta.profile.model.clone(),
        system: inner.config.system_prompt.clone(),
        messages: session.history.lock().await.clone(),
        tools,
        tool_choice: ToolChoice::Auto,
        cache_breakpoints: Vec::new(),
        max_output_tokens: inner.config.max_output_tokens,
        temperature: inner.config.temperature,
        stop_sequences: Vec::new(),
    }
}

/// One model step with transparent retry. Ok(step) always returns whatever
/// was assembled (partial content included on interruption); Err(code) means
/// the step produced nothing worth committing and the turn must end.
async fn stream_step(
    inner: &Arc<KernelInner>,
    request: &CompletionRequest,
    cancel: &Arc<TurnCancel>,
    sink: &Arc<dyn EventSink>,
    log: &mut EventLog,
    session_id: &SessionId,
    turn: TurnId,
) -> Result<StepResult, String> {
    let mut attempt: u32 = 0;
    'attempts: loop {
        attempt += 1;

        let stream = match inner.deps.provider.stream(request.clone()).await {
            Ok(stream) => stream,
            Err(error) => {
                // emitted_output blocks transparent retry: history-visible
                // output may already exist, retrying risks duplication.
                if error.retryable
                    && !error.emitted_output
                    && attempt < inner.config.max_provider_attempts
                    && !cancel.is_cancelled()
                {
                    if wait_backoff(inner, cancel, backoff_ms(inner, attempt)).await {
                        return Ok(cancelled_empty_step());
                    }
                    continue 'attempts;
                }
                return Err(surface_provider_error(sink, log, session_id, &error).await);
            }
        };

        let mut stream = stream;
        let mut acc = Accumulator::new();

        'consume: loop {
            let cancel_future = cancel.cancelled();
            let item = match select(stream.next(), cancel_future).await {
                Either::Left((item, _)) => item,
                Either::Right(((), _)) => {
                    // Drop of the stream (loop exit) cancels the upstream
                    // request; the kernel fills in the interruption.
                    acc.cancelled = true;
                    break 'consume;
                }
            };
            match item {
                None => break 'consume,
                Some(Err(error)) => {
                    let surfaced = error.emitted_output || acc.content_seen;
                    if error.retryable
                        && !surfaced
                        && attempt < inner.config.max_provider_attempts
                        && !cancel.is_cancelled()
                    {
                        if wait_backoff(inner, cancel, backoff_ms(inner, attempt)).await {
                            return Ok(cancelled_empty_step());
                        }
                        continue 'attempts;
                    }
                    let code = surface_provider_error(sink, log, session_id, &error).await;
                    if !acc.has_content() {
                        return Err(code);
                    }
                    break 'consume;
                }
                Some(Ok(event)) => {
                    acc.apply(event);
                    if acc.text_buf.len() >= inner.config.text_flush_bytes {
                        acc.flush(sink, log, session_id, turn).await;
                    }
                }
            }
        }

        acc.flush(sink, log, session_id, turn).await;
        let completed_seen = acc.completed_seen;
        let step = acc.finish();

        // Abrupt end without Completed and without any content is a protocol
        // failure worth one clean retry.
        if !completed_seen && !step.cancelled && !step.has_content() {
            let error = ProviderError {
                kind: ProviderErrorKind::Protocol,
                retryable: true,
                emitted_output: false,
                message: "stream ended without a completion event".to_string(),
            };
            if attempt < inner.config.max_provider_attempts && !cancel.is_cancelled() {
                if wait_backoff(inner, cancel, backoff_ms(inner, attempt)).await {
                    return Ok(cancelled_empty_step());
                }
                continue 'attempts;
            }
            return Err(surface_provider_error(sink, log, session_id, &error).await);
        }
        return Ok(step);
    }
}

fn cancelled_empty_step() -> StepResult {
    StepResult {
        reasoning: None,
        text: String::new(),
        tool_calls: Vec::new(),
        cancelled: true,
    }
}

fn backoff_ms(inner: &Arc<KernelInner>, failed_attempt: u32) -> u64 {
    inner
        .config
        .retry_base_ms
        .saturating_mul(1u64 << (failed_attempt - 1).min(4))
        .min(8000)
}

/// Wait the backoff, remaining abortable by cancellation. Returns true when
/// cancelled during the wait.
async fn wait_backoff(inner: &Arc<KernelInner>, cancel: &Arc<TurnCancel>, ms: u64) -> bool {
    matches!(
        select(
            Box::pin(inner.deps.clock.delay_ms(ms)),
            Box::pin(cancel.cancelled()),
        )
        .await,
        Either::Right(_)
    )
}

async fn surface_provider_error(
    sink: &Arc<dyn EventSink>,
    log: &mut EventLog,
    session_id: &SessionId,
    error: &ProviderError,
) -> String {
    let code = format!("provider_{}", kind_tag(&error.kind));
    log.emit(
        sink,
        KernelEvent::Error {
            session: Some(session_id.clone()),
            code: code.clone(),
            fatal: false,
            ts_ms: log.now(),
        },
    )
    .await;
    code
}

fn kind_tag(kind: &ProviderErrorKind) -> &'static str {
    match kind {
        ProviderErrorKind::Network => "network",
        ProviderErrorKind::Auth => "auth",
        ProviderErrorKind::RateLimited => "rate_limited",
        ProviderErrorKind::InvalidRequest => "invalid_request",
        ProviderErrorKind::Overloaded => "overloaded",
        ProviderErrorKind::Protocol => "protocol",
    }
}

struct CallAccumulator {
    id: ToolCallId,
    name: Option<String>,
    arguments: String,
}

struct Accumulator {
    content_seen: bool,
    completed_seen: bool,
    cancelled: bool,
    reasoning: String,
    signature: Option<String>,
    text: String,
    text_buf: String,
    seq: u64,
    calls: Vec<CallAccumulator>,
    call_index: std::collections::HashMap<ToolCallId, usize>,
}

impl Accumulator {
    fn new() -> Self {
        Self {
            content_seen: false,
            completed_seen: false,
            cancelled: false,
            reasoning: String::new(),
            signature: None,
            text: String::new(),
            text_buf: String::new(),
            seq: 0,
            calls: Vec::new(),
            call_index: std::collections::HashMap::new(),
        }
    }

    fn has_content(&self) -> bool {
        !self.text.is_empty() || !self.reasoning.is_empty() || !self.calls.is_empty()
    }

    fn apply(&mut self, event: ModelStreamEvent) {
        match event {
            ModelStreamEvent::Opened { .. } => {}
            ModelStreamEvent::TextDelta { delta } => {
                self.content_seen = true;
                self.text.push_str(&delta);
                self.text_buf.push_str(&delta);
            }
            ModelStreamEvent::ReasoningDelta { delta } => {
                self.content_seen = true;
                self.reasoning.push_str(&delta);
            }
            ModelStreamEvent::ToolCallDelta {
                id,
                name,
                arguments_delta,
            } => {
                self.content_seen = true;
                let index = match self.call_index.get(&id) {
                    Some(&index) => index,
                    None => {
                        let index = self.calls.len();
                        self.calls.push(CallAccumulator {
                            id: id.clone(),
                            name: None,
                            arguments: String::new(),
                        });
                        self.call_index.insert(id, index);
                        index
                    }
                };
                let call = &mut self.calls[index];
                if let Some(name) = name {
                    call.name = Some(name);
                }
                call.arguments.push_str(&arguments_delta);
            }
            ModelStreamEvent::Completed {
                reasoning_signature,
                ..
            } => {
                self.completed_seen = true;
                if reasoning_signature.is_some() {
                    self.signature = reasoning_signature;
                }
            }
        }
    }

    async fn flush(
        &mut self,
        sink: &Arc<dyn EventSink>,
        log: &mut EventLog,
        session_id: &SessionId,
        turn: TurnId,
    ) {
        if self.text_buf.is_empty() {
            return;
        }
        let text = std::mem::take(&mut self.text_buf);
        let bytes = text.len() as u64;
        let seq = self.seq;
        self.seq += 1;
        log.emit(
            sink,
            KernelEvent::ModelDelta {
                session: session_id.clone(),
                turn,
                seq,
                text,
                bytes,
                ts_ms: log.now(),
            },
        )
        .await;
    }

    fn finish(mut self) -> StepResult {
        let tool_calls = self
            .calls
            .into_iter()
            .map(|call| {
                let name = match call.name {
                    Some(name) => name,
                    None => "(unknown)".to_string(),
                };
                let trimmed = call.arguments.trim();
                let (arguments, arguments_error) = if trimmed.is_empty() {
                    (serde_json::json!({}), None)
                } else {
                    match serde_json::from_str::<Value>(trimmed) {
                        Ok(Value::Object(map)) => (Value::Object(map), None),
                        Ok(_) => (
                            serde_json::json!({}),
                            Some("tool arguments are not a JSON object".to_string()),
                        ),
                        Err(error) => (
                            serde_json::json!({}),
                            Some(format!("incomplete tool arguments: {error}")),
                        ),
                    }
                };
                let arguments_error = if name == "(unknown)" && arguments_error.is_none() {
                    Some("tool call arrived without a name".to_string())
                } else {
                    arguments_error
                };
                AssembledCall {
                    id: call.id,
                    name,
                    arguments,
                    arguments_error,
                }
            })
            .collect();
        let reasoning = if self.reasoning.is_empty() {
            None
        } else {
            Some((std::mem::take(&mut self.reasoning), self.signature))
        };
        StepResult {
            reasoning,
            text: self.text,
            tool_calls,
            cancelled: self.cancelled,
        }
    }
}
