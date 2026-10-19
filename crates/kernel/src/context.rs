//! Context governance: token estimation and microcompact-style compaction.
//! No model-summarization round trip; old tool-result contents are
//! placeholder-cleared first, and only then whole leading exchanges dropped.

use std::sync::Arc;

use alcoler_contracts::message::{ContentBlock, Message, OutputBlock, Role};
use alcoler_contracts::wire::{EventSink, KernelEvent};

use crate::KernelInner;
use crate::events::EventLog;
use crate::session::Session;
use crate::toolset::output_block_bytes;

pub(crate) async fn compact_if_needed(
    inner: &Arc<KernelInner>,
    session: &Arc<Session>,
    log: &mut EventLog,
    sink: &Arc<dyn EventSink>,
) {
    let model = session.meta.profile.model.clone();
    let budget = session.meta.profile.context_budget_tokens;
    let mut history = session.history.lock().await;

    if estimate(inner, &model, &history) <= budget {
        return;
    }

    // Stage 1: clear old tool-result contents, keeping the most recent ones.
    let positions: Vec<(usize, usize)> = history
        .iter()
        .enumerate()
        .flat_map(|(mi, message)| {
            message
                .blocks
                .iter()
                .enumerate()
                .filter_map(move |(bi, block)| match block {
                    ContentBlock::ToolResult { .. } => Some((mi, bi)),
                    _ => None,
                })
        })
        .collect();
    let clear_count = positions
        .len()
        .saturating_sub(inner.config.keep_tool_results);
    let mut changed = false;
    for &(mi, bi) in positions.iter().take(clear_count) {
        if let ContentBlock::ToolResult { content, .. } = &mut history[mi].blocks[bi]
            && content.iter().map(output_block_bytes).sum::<usize>() > 32
        {
            *content = vec![OutputBlock::Text {
                text: "[old tool result content cleared]".to_string(),
            }];
            changed = true;
        }
    }

    // Stage 2: drop oldest whole exchanges (user + assistant + its tool
    // results) until within budget; the final exchange is never dropped.
    while estimate(inner, &model, &history) > budget {
        let Some(end) = exchange_end(&history) else {
            break;
        };
        if end >= history.len() {
            break;
        }
        history.drain(..end);
        changed = true;
    }

    if changed {
        let kept_tokens = estimate(inner, &model, &history);
        let session_id = session.meta.id.clone();
        log.emit(
            sink,
            KernelEvent::Compacted {
                session: session_id,
                kept_tokens,
                ts_ms: log.now(),
            },
        )
        .await;
    }
}

fn estimate(
    inner: &Arc<KernelInner>,
    model: &alcoler_contracts::model::ModelRef,
    history: &[Message],
) -> u64 {
    history
        .iter()
        .map(|message| inner.deps.counter.count_message(model, message))
        .sum()
}

/// Length of the first whole exchange: the leading user message, the
/// assistant messages that follow, and — when the last of those carries tool
/// calls — the user message holding their results. Dropping anything less
/// would orphan a tool_use.
fn exchange_end(history: &[Message]) -> Option<usize> {
    if history.is_empty() {
        return None;
    }
    let mut end = 1;
    let mut last_assistant_has_tool_use = false;
    while end < history.len() && history[end].role == Role::Assistant {
        last_assistant_has_tool_use = history[end]
            .blocks
            .iter()
            .any(|block| matches!(block, ContentBlock::ToolUse { .. }));
        end += 1;
    }
    if last_assistant_has_tool_use && end < history.len() && history[end].role == Role::User {
        end += 1;
    }
    Some(end)
}
