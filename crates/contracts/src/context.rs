//! Context governance port.
//!
//! Truncation, micro-compaction, and summary-compaction policies belong to
//! the kernel core (ContextGovernor); only the external capability those
//! policies depend on is abstracted here — token counting. Counting is
//! strongly tied to the model tokenizer, hence dispatch by [`ModelRef`];
//! test implementations may approximate by characters to stay deterministic.

use crate::message::Message;
use crate::model::ModelRef;

/// Token counting port.
pub trait TokenCounter: Send + Sync {
    fn count_text(&self, model: &ModelRef, text: &str) -> u64;

    /// Token count of a full message (approximating the protocol overhead of
    /// the message structure and content blocks).
    fn count_message(&self, model: &ModelRef, message: &Message) -> u64;
}
