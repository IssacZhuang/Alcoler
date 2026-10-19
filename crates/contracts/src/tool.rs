//! Tool port: the unified interface for built-in tools, external MCP tools,
//! and browser-plugin capabilities.
//!
//! Core mechanism: scheduling,
//! concurrency, approval, timeout, and output budget are all driven by
//! [`ToolPolicy`] metadata — no special-casing at call sites; external tools
//! are wrapped as [`Tool`] implementations isomorphic to built-in ones —
//! external capability is a registration concern, not an architectural one.

use crate::BoxFuture;
use crate::ids::{SessionId, ToolCallId, TurnId};
use crate::message::OutputBlock;
use crate::time::CancelToken;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::sync::Arc;

/// Tool effect classification: read-only / side-effecting / destructive.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ToolEffect {
    ReadOnly,
    Mutating,
    Destructive,
}

/// Concurrency-safety declaration: the scheduler derives parallel grouping
/// from this (destructive → forced serial; parallel_safe → may share a
/// parallel group).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Concurrency {
    ParallelSafe,
    SerialOnly,
}

/// Side-effect scope: the basis for approval-UI display and permission-rule
/// matching.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum EffectScope {
    None,
    Workspace,
    Git,
    Network,
    System,
    Session,
    UserInteraction,
}

/// Risk classification: the default approval policy (by_risk) adjudicates
/// on this.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum RiskLevel {
    Low,
    Medium,
    High,
    Critical,
}

/// Approval requirement declaration.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ApprovalPolicy {
    /// Never requires approval (pairs with read_only).
    Never,
    /// Routed by risk level: low risk is allowed, medium/high risk is asked
    /// (the default policy).
    ByRisk,
    /// Must be confirmed under every permission mode (the always-ask hard
    /// gate).
    Always,
}

/// Security and execution metadata of a tool.
///
/// Defaults are "read-only, parallel-safe, no side effects, low risk, no
/// approval, 30s timeout, 100KB output budget"; tools with side effects must
/// declare every item explicitly.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ToolPolicy {
    pub effect: ToolEffect,
    pub concurrency: Concurrency,
    pub effect_scope: EffectScope,
    pub risk: RiskLevel,
    pub approval: ApprovalPolicy,
    /// Default execution timeout (milliseconds).
    pub timeout_ms: u64,
    /// Whether the model may override the timeout in call parameters
    /// (long-running tools like Bash).
    pub timeout_overridable: bool,
    /// Result output budget (bytes): beyond it the executor truncates or
    /// persists the full result as a blob (two-level policy).
    pub output_budget_bytes: usize,
    /// Tools requiring user interaction (AskUserQuestion-like): the scheduler
    /// must reserve an interaction channel for them.
    pub requires_user_interaction: bool,
    /// Whether the tool is visible to the model. Tools with `false` never
    /// enter provider requests (hidden-alias kinds).
    pub provider_visible: bool,
    /// Terminal tool: success ends the current turn (submit_result-like).
    pub stop_turn_on_success: bool,
}

impl Default for ToolPolicy {
    fn default() -> Self {
        Self {
            effect: ToolEffect::ReadOnly,
            concurrency: Concurrency::ParallelSafe,
            effect_scope: EffectScope::None,
            risk: RiskLevel::Low,
            approval: ApprovalPolicy::Never,
            timeout_ms: 30_000,
            timeout_overridable: false,
            output_budget_bytes: 100 * 1024,
            requires_user_interaction: false,
            provider_visible: true,
            stop_turn_on_success: false,
        }
    }
}

/// Tool declaration: the spec sent to the model + the metadata the kernel
/// schedules on.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ToolSpec {
    pub name: String,
    pub description: String,
    /// Input JSON Schema. Model-given parameters are re-validated against
    /// this at the execution entrance; parameter parse failures degrade to
    /// `{}` and produce a business error from this schema — never escalate
    /// into a model-request failure.
    pub input_schema: Value,
    pub policy: ToolPolicy,
}

/// Tool execution context, constructed by the executor at call time.
pub struct ToolExecContext {
    pub session: SessionId,
    pub turn: TurnId,
    pub call: ToolCallId,
    /// Deadline (monotonic milliseconds). Timeouts are enforced by the
    /// executor wrapper; provided here for the tool's own checks only.
    pub deadline_monotonic_ms: Option<u64>,
    /// Cancel token: triggered on turn cancel / session cancel / shutdown.
    pub cancel: Arc<dyn CancelToken>,
}

/// Tool terminal status. Failure is a domain result, not an exception;
/// cancellation and timeout produce paired results too.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ToolStatus {
    Ok,
    Failed { code: String, message: String },
    TimedOut,
    Cancelled,
}

/// Tool output.
///
/// `content` is the projection shown to the model (already budget-governed);
/// `bytes_emitted` is the raw byte count before governance (for events and
/// stats); when the full text must be kept, the executor persists it as a
/// blob and substitutes an envelope.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ToolOutput {
    pub status: ToolStatus,
    pub content: Vec<OutputBlock>,
    pub bytes_emitted: u64,
    pub truncated: bool,
}

impl ToolOutput {
    pub fn ok(content: Vec<OutputBlock>) -> Self {
        Self {
            status: ToolStatus::Ok,
            content,
            bytes_emitted: 0,
            truncated: false,
        }
    }

    pub fn failed(code: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            status: ToolStatus::Failed {
                code: code.into(),
                message: message.into(),
            },
            content: Vec::new(),
            bytes_emitted: 0,
            truncated: false,
        }
    }
}

/// Tool port.
///
/// Built-in fs/exec, external MCP tools, and browser-plugin capabilities all
/// implement this interface and go through the same "validate → permission →
/// timeout wrapper → execute → result budget" pipeline.
pub trait Tool: Send + Sync {
    /// Tool declaration. Implementations should cache the spec; the scheduler
    /// and request assembly read it at high frequency.
    fn spec(&self) -> &ToolSpec;

    /// Execute one call. `input` has already passed schema validation.
    ///
    /// Implementation requirements:
    /// - respond cooperatively to `ctx.cancel` inside long operations;
    /// - return [`ToolStatus::Failed`] on business failure — do not panic;
    /// - process-spawning tools own their process-tree cleanup (e.g. Windows
    ///   Job Objects).
    fn execute<'a>(&'a self, input: Value, ctx: &'a ToolExecContext) -> BoxFuture<'a, ToolOutput>;
}
