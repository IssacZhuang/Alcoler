//! Runtime primitives: wall/monotonic clock, id generation, heuristic token
//! counting, and the auto-approve permission gate.

use std::sync::OnceLock;
use std::time::{SystemTime, UNIX_EPOCH};

use alcoler_contracts::BoxFuture;
use alcoler_contracts::context::TokenCounter;
use alcoler_contracts::message::{ContentBlock, Message, OutputBlock};
use alcoler_contracts::model::ModelRef;
use alcoler_contracts::permission::{PermissionDecision, PermissionGate, PermissionRequest};
use alcoler_contracts::time::{Clock, IdGenerator};

pub struct SystemClock;

impl Clock for SystemClock {
    fn unix_ms(&self) -> i64 {
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_millis() as i64)
            .unwrap_or(0)
    }

    fn monotonic_ms(&self) -> u64 {
        static ANCHOR: OnceLock<std::time::Instant> = OnceLock::new();
        ANCHOR
            .get_or_init(std::time::Instant::now)
            .elapsed()
            .as_millis() as u64
    }

    fn delay_ms(&self, ms: u64) -> BoxFuture<'static, ()> {
        Box::pin(tokio::time::sleep(std::time::Duration::from_millis(ms)))
    }
}

pub struct TimeIdGenerator {
    counter: std::sync::atomic::AtomicU64,
}

impl Default for TimeIdGenerator {
    fn default() -> Self {
        Self {
            counter: std::sync::atomic::AtomicU64::new(0),
        }
    }
}

impl TimeIdGenerator {
    pub fn new() -> Self {
        Self::default()
    }
}

impl IdGenerator for TimeIdGenerator {
    fn next_id(&self) -> String {
        let seq = self
            .counter
            .fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        format!(
            "{:x}-{:04}",
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .map(|d| d.as_nanos() as u64)
                .unwrap_or(0),
            seq
        )
    }
}

/// ≈4 chars per token plus per-block overhead; exact enough for budget
/// triggering without shipping a tokenizer per model.
pub struct HeuristicTokenCounter;

impl TokenCounter for HeuristicTokenCounter {
    fn count_text(&self, _model: &ModelRef, text: &str) -> u64 {
        text.len() as u64 / 4 + 1
    }

    fn count_message(&self, model: &ModelRef, message: &Message) -> u64 {
        let mut total = 8;
        for block in &message.blocks {
            match block {
                ContentBlock::Text { text } => total += self.count_text(model, text),
                ContentBlock::Reasoning { text, .. } => total += self.count_text(model, text),
                ContentBlock::ToolUse {
                    name, arguments, ..
                } => {
                    total += self.count_text(model, name)
                        + self.count_text(model, &arguments.to_string())
                }
                ContentBlock::ToolResult { content, .. } => {
                    for block in content {
                        match block {
                            OutputBlock::Text { text } => total += self.count_text(model, text),
                            OutputBlock::Json { value } => {
                                total += self.count_text(model, &value.to_string())
                            }
                        }
                    }
                }
            }
        }
        total
    }
}

/// Auto-approve gate ("unattended" mode). Tools still declare their real
/// risk/approval policies, so a stricter gate can replace this adapter
/// without touching the kernel.
pub struct AllowGate;

impl PermissionGate for AllowGate {
    fn decide<'a>(&'a self, _request: &'a PermissionRequest) -> BoxFuture<'a, PermissionDecision> {
        Box::pin(async { PermissionDecision::Allow })
    }
}
