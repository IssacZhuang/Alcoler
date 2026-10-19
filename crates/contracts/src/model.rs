//! Model provider port: the single model-facing interface of the kernel.
//!
//! All providers (Anthropic / OpenAI / openai-compatible / local mocks)
//! converge here onto the same semantics. The contract deliberately
//! preserves: tool-call IDs, stop reasons, reasoning signatures, cache
//! breakpoints, usage accounting, and the stream-completion event —
//! semantics are never dropped for the sake of uniformity.

use crate::ids::{ProviderId, ToolCallId};
use crate::message::Message;
use crate::tool::ToolSpec;
use crate::{BoxFuture, BoxStream};
use serde::{Deserialize, Serialize};

/// Model reference: provider + model name.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ModelRef {
    pub provider: String,
    pub model: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ToolChoice {
    Auto,
    None,
    Required,
    Named(String),
}

/// Cache breakpoint: a position in the request that should hit / establish
/// the prompt cache; the adapter layer maps this onto each provider's
/// cache_control semantics.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct CacheBreakpoint {
    pub after_message: u32,
}

/// A streaming completion request.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CompletionRequest {
    pub model: ModelRef,
    pub system: Option<String>,
    pub messages: Vec<Message>,
    /// Tools visible to this request (those with `provider_visible == false`
    /// have already been filtered out).
    pub tools: Vec<ToolSpec>,
    pub tool_choice: ToolChoice,
    pub cache_breakpoints: Vec<CacheBreakpoint>,
    pub max_output_tokens: Option<u64>,
    pub temperature: Option<f32>,
    pub stop_sequences: Vec<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum StopReason {
    EndTurn,
    ToolUse,
    MaxTokens,
    StopSequence,
    Refusal,
}

/// Token usage accounting (incl. cache reads/writes, for cost tracking and
/// compaction decisions).
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct Usage {
    pub input_tokens: u64,
    pub output_tokens: u64,
    pub cache_read_tokens: u64,
    pub cache_write_tokens: u64,
}

/// Model stream events: consumed in order by the core side, which assembles
/// messages and triggers tool scheduling.
#[derive(Debug, Clone, PartialEq)]
pub enum ModelStreamEvent {
    /// Stream established; `model` is the name of the model actually serving
    /// the request (the provider may rewrite the requested model name).
    Opened {
        model: String,
    },
    TextDelta {
        delta: String,
    },
    ReasoningDelta {
        delta: String,
    },
    /// Tool-call argument delta: the first appearance of `id` marks the start
    /// of the call; `name` arrives with the first delta. Execution uses only
    /// the fully assembled final arguments — never guesses from deltas.
    ToolCallDelta {
        id: ToolCallId,
        name: Option<String>,
        arguments_delta: String,
    },
    /// Stream completed normally. stop_reason and usage must not be lost;
    /// `reasoning_signature` is the signature of this turn's reasoning blocks
    /// (present when the provider requires echoing it back later).
    Completed {
        stop_reason: StopReason,
        usage: Usage,
        reasoning_signature: Option<String>,
    },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ProviderErrorKind {
    Network,
    Auth,
    RateLimited,
    InvalidRequest,
    Overloaded,
    Protocol,
}

/// Provider error.
///
/// `emitted_output` is the key to retry semantics: whether any visible output
/// or output already recorded in history was emitted before the error
/// occurred — the core side uses this to decide between a transparent
/// automatic retry and surfacing the interruption upstream
/// (duplicate-side-effect risk).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ProviderError {
    pub kind: ProviderErrorKind,
    pub retryable: bool,
    pub emitted_output: bool,
    pub message: String,
}

/// Model provider port.
///
/// Cancellation semantics: dropping the returned stream cancels the upstream
/// request and releases the connection — adapters must implement this; a
/// cancelled request emits no further events (the core side fills in the
/// interruption semantics itself).
pub trait ModelProvider: Send + Sync {
    fn provider_id(&self) -> &ProviderId;

    /// Start one streaming completion.
    fn stream(
        &self,
        req: CompletionRequest,
    ) -> BoxFuture<
        '_,
        Result<BoxStream<'static, Result<ModelStreamEvent, ProviderError>>, ProviderError>,
    >;
}
