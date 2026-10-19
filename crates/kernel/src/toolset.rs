//! Tool scheduling and the single-call pipeline: validate → permission →
//! execute (timeout/cancel race) → output budget. Every declared tool call
//! lands on a paired result, whatever happens.

use std::sync::Arc;

use alcoler_contracts::ids::{SessionId, ToolCallId, TurnId};
use alcoler_contracts::message::{ContentBlock, Message, OutputBlock, Role};
use alcoler_contracts::permission::{PermissionDecision, PermissionRequest};
use alcoler_contracts::storage::{ToolCallPhase, ToolCallProgress, TurnProgress};
use alcoler_contracts::time::CancelToken;
use alcoler_contracts::tool::{
    ApprovalPolicy, Concurrency, RiskLevel, Tool, ToolExecContext, ToolOutput, ToolPolicy,
    ToolStatus,
};
use alcoler_contracts::wire::{EventSink, KernelEvent};
use futures_util::future::{Either, select};
use serde_json::Value;

use crate::KernelInner;
use crate::cancel::TurnCancel;
use crate::events::EventLog;
use crate::schema::Schema;
use crate::session::Session;
use crate::turn::AssembledCall;

pub(crate) struct ToolEntry {
    pub tool: Arc<dyn Tool>,
    pub schema: Schema,
}

/// Execute one batch of model tool calls. Returns true when the turn must
/// end (`stop_turn_on_success` hit).
#[allow(clippy::too_many_arguments)]
pub(crate) async fn execute_tool_calls(
    inner: &Arc<KernelInner>,
    session: &Arc<Session>,
    turn: TurnId,
    calls: &[AssembledCall],
    cancel: &Arc<TurnCancel>,
    sink: &Arc<dyn EventSink>,
    log: &mut EventLog,
    new_messages: &mut Vec<Message>,
) -> bool {
    let session_id = session.meta.id.clone();

    for call in calls {
        log.emit(
            sink,
            KernelEvent::ToolCall {
                session: session_id.clone(),
                turn,
                call: call.id.clone(),
                tool: call.name.clone(),
                input: call.arguments.clone(),
                ts_ms: log.now(),
            },
        )
        .await;
    }
    save_progress(inner, &session_id, turn, calls, |_| ToolCallPhase::Pending).await;

    let max_concurrent = session.meta.profile.max_concurrent_tools.max(1) as usize;
    let parallel_safe: Vec<bool> = calls
        .iter()
        .map(|call| {
            inner.registry.get(&call.name).is_some_and(|entry| {
                entry.tool.spec().policy.concurrency == Concurrency::ParallelSafe
            })
        })
        .collect();
    let groups = group_calls(&parallel_safe, max_concurrent);

    let mut outputs: Vec<Option<ToolOutput>> = vec![None; calls.len()];
    let mut stop_turn = false;

    for group in groups {
        if stop_turn || cancel.is_cancelled() {
            break;
        }
        save_progress(inner, &session_id, turn, calls, |i| {
            if group.contains(&i) {
                ToolCallPhase::Running
            } else {
                ToolCallPhase::Pending
            }
        })
        .await;

        let mut futures = Vec::with_capacity(group.len());
        for &index in &group {
            futures.push(run_single_call(inner, session, turn, &calls[index], cancel));
        }
        for (slot, output) in futures_util::future::join_all(futures)
            .await
            .into_iter()
            .enumerate()
        {
            let index = group[slot];
            let call = &calls[index];
            log.emit(
                sink,
                KernelEvent::ToolResult {
                    session: session_id.clone(),
                    turn,
                    call: call.id.clone(),
                    status: output.status.clone(),
                    bytes: output.bytes_emitted,
                    ts_ms: log.now(),
                },
            )
            .await;
            outputs[index] = Some(output);
        }
        save_progress(inner, &session_id, turn, calls, |i| {
            if group.contains(&i) {
                ToolCallPhase::Completed
            } else {
                ToolCallPhase::Pending
            }
        })
        .await;

        if !stop_turn {
            stop_turn = group.iter().any(|&index| {
                let output = outputs[index].as_ref().unwrap();
                output.status == ToolStatus::Ok
                    && inner
                        .registry
                        .get(&calls[index].name)
                        .is_some_and(|entry| entry.tool.spec().policy.stop_turn_on_success)
            });
        }
    }

    // Calls that never ran (turn stop, cancellation, group break) still get
    // a paired result — the pairing invariant has no exceptions.
    for (index, call) in calls.iter().enumerate() {
        if outputs[index].is_none() {
            let output = ToolOutput {
                status: ToolStatus::Cancelled,
                content: vec![OutputBlock::Text {
                    text: "skipped: turn ended before this call ran".to_string(),
                }],
                bytes_emitted: 0,
                truncated: false,
            };
            log.emit(
                sink,
                KernelEvent::ToolResult {
                    session: session_id.clone(),
                    turn,
                    call: call.id.clone(),
                    status: ToolStatus::Cancelled,
                    bytes: 0,
                    ts_ms: log.now(),
                },
            )
            .await;
            outputs[index] = Some(output);
        }
    }

    let blocks = calls
        .iter()
        .zip(outputs.iter().map(Option::as_ref))
        .map(|(call, output)| {
            let output = output.expect("every call has an output by now");
            // The model must always see why a call failed; an empty content
            // array is also rejected by some providers.
            let content = if output.content.is_empty() {
                match &output.status {
                    ToolStatus::Failed { code, message } => vec![OutputBlock::Text {
                        text: format!("tool error [{code}]: {message}"),
                    }],
                    _ => vec![OutputBlock::Text {
                        text: "[no output]".to_string(),
                    }],
                }
            } else {
                output.content.clone()
            };
            ContentBlock::ToolResult {
                tool_use_id: call.id.clone(),
                is_error: !matches!(output.status, ToolStatus::Ok),
                content,
            }
        })
        .collect();
    let results = Message {
        role: Role::User,
        blocks,
    };
    session.history.lock().await.push(results.clone());
    new_messages.push(results);

    stop_turn
}

/// Declaration-order grouping: consecutive parallel-safe calls share a batch
/// up to the concurrency limit; serial-only calls run alone.
fn group_calls(parallel_safe: &[bool], max_concurrent: usize) -> Vec<Vec<usize>> {
    let mut groups: Vec<Vec<usize>> = Vec::new();
    let mut current: Vec<usize> = Vec::new();
    for (index, &safe) in parallel_safe.iter().enumerate() {
        if safe && current.len() < max_concurrent {
            current.push(index);
        } else {
            if !current.is_empty() {
                groups.push(std::mem::take(&mut current));
            }
            if safe {
                current.push(index);
            } else {
                groups.push(vec![index]);
            }
        }
    }
    if !current.is_empty() {
        groups.push(current);
    }
    groups
}

async fn save_progress(
    inner: &Arc<KernelInner>,
    session_id: &SessionId,
    turn: TurnId,
    calls: &[AssembledCall],
    phase: impl Fn(usize) -> ToolCallPhase,
) {
    let tool_calls = calls
        .iter()
        .enumerate()
        .map(|(index, call)| ToolCallProgress {
            call: call.id.clone(),
            tool: call.name.clone(),
            phase: phase(index),
        })
        .collect();
    // In-flight ledger only; the completed TurnRecord is the durable unit.
    let _ = inner
        .deps
        .store
        .save_turn_progress(&TurnProgress {
            session: session_id.clone(),
            turn,
            tool_calls,
        })
        .await;
}

async fn run_single_call(
    inner: &Arc<KernelInner>,
    session: &Arc<Session>,
    turn: TurnId,
    call: &AssembledCall,
    cancel: &Arc<TurnCancel>,
) -> ToolOutput {
    if cancel.is_cancelled() {
        return cancelled_output();
    }
    if let Some(error) = &call.arguments_error {
        return ToolOutput::failed("invalid_arguments", error.clone());
    }
    let Some(entry) = inner.registry.get(&call.name) else {
        return ToolOutput::failed(
            "unknown_tool",
            format!("no tool named '{}' is registered", call.name),
        );
    };
    let spec = entry.tool.spec();
    let policy = spec.policy.clone();
    if let Err(message) = entry.schema.validate(&call.arguments) {
        return ToolOutput::failed("invalid_input", message);
    }

    let input = match adjudicate(inner, session, turn, call, entry, &policy).await {
        Ok(input) => input,
        Err(output) => return output,
    };

    let ctx = ToolExecContext {
        session: session.meta.id.clone(),
        turn,
        call: call.id.clone(),
        deadline_monotonic_ms: Some(
            inner
                .deps
                .clock
                .monotonic_ms()
                .saturating_add(policy.timeout_ms),
        ),
        cancel: cancel.clone(),
    };

    let execute = entry.tool.execute(input, &ctx);
    let timeout = inner.deps.clock.delay_ms(policy.timeout_ms);
    let raced = select(
        Box::pin(cancel.cancelled()),
        Box::pin(select(execute, Box::pin(timeout))),
    )
    .await;
    let output = match raced {
        Either::Left(((), _)) => cancelled_output(),
        Either::Right((Either::Left((output, _)), _)) => output,
        Either::Right((Either::Right(((), _)), _)) => ToolOutput {
            status: ToolStatus::TimedOut,
            content: vec![OutputBlock::Text {
                text: format!("tool execution timed out after {} ms", policy.timeout_ms),
            }],
            bytes_emitted: 0,
            truncated: false,
        },
    };

    apply_budget(
        inner,
        &session.meta.id,
        &call.id,
        policy.output_budget_bytes,
        output,
    )
    .await
}

/// Permission gate with the core-side semantics: approve-timeout defaults to
/// deny; `Modify` re-validates and re-adjudicates (bounded); this kernel has
/// no stronger channel, so `Escalate` degrades to deny.
async fn adjudicate(
    inner: &Arc<KernelInner>,
    session: &Arc<Session>,
    turn: TurnId,
    call: &AssembledCall,
    entry: &ToolEntry,
    policy: &ToolPolicy,
) -> Result<Value, ToolOutput> {
    let mut input = call.arguments.clone();
    for _round in 0..3 {
        let needs_ask = match policy.approval {
            ApprovalPolicy::Never => false,
            ApprovalPolicy::Always => true,
            ApprovalPolicy::ByRisk => policy.risk >= RiskLevel::Medium,
        };
        let session_allowed = {
            let control = session.control.lock().await;
            control.allowed_tools.contains(&call.name)
        };
        if !needs_ask || session_allowed {
            return Ok(input);
        }

        let request = PermissionRequest {
            session: session.meta.id.clone(),
            turn,
            tool: call.name.clone(),
            effect: policy.effect,
            effect_scope: policy.effect_scope,
            risk: policy.risk,
            input_digest: digest_of(&input),
        };
        let decision = select(
            Box::pin(inner.deps.gate.decide(&request)),
            Box::pin(
                inner
                    .deps
                    .clock
                    .delay_ms(session.meta.profile.permission_timeout_ms),
            ),
        )
        .await;
        let decision = match decision {
            Either::Left((decision, _)) => decision,
            Either::Right(((), _)) => PermissionDecision::Deny {
                reason: Some("approval timed out; defaulting to deny".to_string()),
            },
        };
        match decision {
            PermissionDecision::Allow => return Ok(input),
            PermissionDecision::AllowSession { tool } => {
                session.control.lock().await.allowed_tools.insert(tool);
                return Ok(input);
            }
            PermissionDecision::Deny { reason } => {
                return Err(ToolOutput::failed(
                    "permission_denied",
                    reason.unwrap_or_else(|| "denied by permission gate".to_string()),
                ));
            }
            PermissionDecision::Escalate => {
                return Err(ToolOutput::failed(
                    "permission_denied",
                    "escalation channel unavailable in this kernel".to_string(),
                ));
            }
            PermissionDecision::Modify {
                input: modified, ..
            } => {
                if let Err(message) = entry.schema.validate(&modified) {
                    return Err(ToolOutput::failed("invalid_input", message));
                }
                input = modified;
            }
        }
    }
    Err(ToolOutput::failed(
        "permission_denied",
        "permission unresolved after repeated modification".to_string(),
    ))
}

fn cancelled_output() -> ToolOutput {
    ToolOutput {
        status: ToolStatus::Cancelled,
        content: vec![OutputBlock::Text {
            text: "cancelled".to_string(),
        }],
        bytes_emitted: 0,
        truncated: false,
    }
}

fn digest_of(input: &Value) -> String {
    let compact = serde_json::to_string(input).unwrap_or_default();
    let mut end = compact.len().min(200);
    while !compact.is_char_boundary(end) {
        end -= 1;
    }
    compact[..end].to_string()
}

pub(crate) fn output_block_bytes(block: &OutputBlock) -> usize {
    match block {
        OutputBlock::Text { text } => text.len(),
        OutputBlock::Json { value } => value.to_string().len(),
    }
}

/// Two-level output budget: within budget the model sees the full content;
/// beyond it the model sees a head preview plus a reference envelope, and
/// the full output is persisted as a blob.
async fn apply_budget(
    inner: &Arc<KernelInner>,
    session_id: &SessionId,
    call: &ToolCallId,
    budget_bytes: usize,
    mut output: ToolOutput,
) -> ToolOutput {
    let raw: usize = output.content.iter().map(output_block_bytes).sum();
    if output.bytes_emitted == 0 {
        output.bytes_emitted = raw as u64;
    }
    if raw <= budget_bytes {
        return output;
    }

    let key = format!("tool-output-{}", call.0);
    let full = serde_json::to_vec(&output.content).unwrap_or_default();
    let persisted = inner.deps.blobs.put(session_id, &key, full).await.is_ok();

    let mut preview: Vec<OutputBlock> = Vec::new();
    let mut remaining = budget_bytes;
    for block in &output.content {
        if remaining == 0 {
            break;
        }
        match block {
            OutputBlock::Text { text } => {
                let cut = cut_bytes(text, remaining);
                remaining -= cut.len();
                preview.push(OutputBlock::Text {
                    text: cut.to_string(),
                });
                if cut.len() < text.len() {
                    break;
                }
            }
            OutputBlock::Json { value } => {
                let serialized = value.to_string();
                let cut = cut_bytes(&serialized, remaining);
                remaining -= cut.len();
                preview.push(OutputBlock::Text {
                    text: cut.to_string(),
                });
                if cut.len() < serialized.len() {
                    break;
                }
            }
        }
    }
    let note = if persisted {
        format!("[output truncated: {raw} bytes; full output persisted as blob {key}]")
    } else {
        format!("[output truncated: {raw} bytes]")
    };
    preview.push(OutputBlock::Text { text: note });

    output.content = preview;
    output.truncated = true;
    output
}

fn cut_bytes(text: &str, max: usize) -> &str {
    if text.len() <= max {
        return text;
    }
    let mut end = max;
    while !text.is_char_boundary(end) {
        end -= 1;
    }
    &text[..end]
}
