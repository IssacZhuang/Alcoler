//! Permission adjudication port.
//!
//! The port expresses only "adjudicate one tool call", not "how long to
//! wait" — the approve-timeout-defaults-to-deny semantics are implemented by
//! the core side wrapping calls with [`crate::time::Clock::delay_ms`], so
//! every adapter (rule engine, CLI confirmation, desktop approval UI,
//! unattended yolo mode) inherits the same timeout semantics.

use crate::BoxFuture;
use crate::ids::{SessionId, TurnId};
use crate::tool::{EffectScope, RiskLevel, ToolEffect};
use serde::{Deserialize, Serialize};
use serde_json::Value;

/// A tool call awaiting adjudication.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct PermissionRequest {
    pub session: SessionId,
    pub turn: TurnId,
    pub tool: String,
    pub effect: ToolEffect,
    pub effect_scope: EffectScope,
    pub risk: RiskLevel,
    /// Input digest (truncated, human-readable): keeps the approval UI from
    /// being flooded by large inputs; the full input is fetched separately
    /// only when the adjudicator needs [`PermissionDecision::Modify`].
    pub input_digest: String,
}

/// Adjudication outcome (polymorphic verdict semantics:
/// allow / deny / modify / escalate).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum PermissionDecision {
    /// Allow this call.
    Allow,
    /// Allow and remember this tool for the current session (session-level
    /// allowlist); later calls with the same name skip approval.
    AllowSession {
        tool: String,
    },
    Deny {
        reason: Option<String>,
    },
    /// Allow after modifying the input: the core side must re-validate the
    /// modified value against the schema and re-adjudicate if necessary.
    Modify {
        input: Value,
        note: Option<String>,
    },
    /// Escalate to a stronger approval channel (e.g. system-level
    /// confirmation, human takeover); the core side re-runs adjudication on
    /// the new channel.
    Escalate,
}

/// Permission adjudication port.
pub trait PermissionGate: Send + Sync {
    fn decide<'a>(&'a self, request: &'a PermissionRequest) -> BoxFuture<'a, PermissionDecision>;
}
