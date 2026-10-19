//! Conversation message domain types: the kernel's authoritative,
//! provider-neutral message format.
//!
//! Content blocks are isomorphic to the message semantics of mainstream
//! providers (tool_use / tool_result / reasoning blocks), so the adapter
//! layer loses no semantics when mapping protocols.

use crate::ids::ToolCallId;
use serde::{Deserialize, Serialize};
use serde_json::Value;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Role {
    User,
    Assistant,
}

/// A message content block.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum ContentBlock {
    Text {
        text: String,
    },
    /// Model reasoning content. `signature` is the reasoning signature some
    /// providers require to be echoed back in later requests; never drop it.
    Reasoning {
        text: String,
        signature: Option<String>,
    },
    /// A tool call initiated by the model. `arguments` is the complete JSON
    /// object (streaming deltas are assembled by the core side).
    ToolUse {
        id: ToolCallId,
        name: String,
        arguments: Value,
    },
    /// A tool result returned to the model. `tool_use_id` pairs with the
    /// `id` of [`ContentBlock::ToolUse`].
    ///
    /// Kernel invariant: every tool_use eventually has a paired tool_result —
    /// cancellation and timeout produce result blocks too (see
    /// [`crate::tool::ToolStatus`]), keeping message-history pairing intact.
    ToolResult {
        tool_use_id: ToolCallId,
        is_error: bool,
        content: Vec<OutputBlock>,
    },
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Message {
    pub role: Role,
    pub blocks: Vec<ContentBlock>,
}

impl Message {
    pub fn new(role: Role, blocks: Vec<ContentBlock>) -> Self {
        Self { role, blocks }
    }

    pub fn user_text(text: impl Into<String>) -> Self {
        Self::new(Role::User, vec![ContentBlock::Text { text: text.into() }])
    }

    pub fn assistant_text(text: impl Into<String>) -> Self {
        Self::new(
            Role::Assistant,
            vec![ContentBlock::Text { text: text.into() }],
        )
    }
}

/// Output block of a tool/model. Large results may be truncated by budget
/// governance, or replaced wholesale by a persisted-blob envelope (see
/// [`crate::storage::BlobStore`]).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum OutputBlock {
    Text { text: String },
    Json { value: Value },
}
