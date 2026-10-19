//! Process/host boundary: the command entrance and the event exit.
//!
//! Event JSON shapes are name-compatible with the E1 black-box wire contract:
//! the `type` tags match (`session_started` / `turn_started` / `model_delta` /
//! `tool_call` / `tool_result` / `compacted` / `turn_done` / `error`) and the
//! fields are a superset — locked by the regression tests in `tests/ports.rs`.
//! Commands likewise use `cmd` as their tag.
//!
//! The entrance/exit are ports too: stdin JSONL, IPC channels, and the command
//! bus of an embedding host (desktop UI) are all adapters of
//! [`CommandSource`] / [`EventSink`] — the kernel is transport-agnostic.

use crate::BoxFuture;
use crate::ids::{CommandId, SessionId, ToolCallId, TurnId};
use crate::storage::SessionProfile;
use crate::tool::ToolStatus;
use serde::{Deserialize, Serialize};
use serde_json::Value;

/// User-submitted input.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct UserInput {
    pub text: String,
}

/// Cancel scope: a single turn or the whole session.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum CancelScope {
    Turn(TurnId),
    Session,
}

/// Input commands (the JSONL entrance deserializes into this type).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "cmd", rename_all = "snake_case")]
pub enum Command {
    /// Create a session. If the session already exists, the core side handles
    /// it idempotently (replay during recovery).
    StartSession {
        session: SessionId,
        profile: SessionProfile,
    },
    /// Submit one user input, driving a turn. The `command` identifier backs
    /// command dedup after crash recovery: a replayed command already counted
    /// into a completed turn is dropped on recovery.
    Submit {
        session: SessionId,
        command: CommandId,
        input: UserInput,
    },
    Cancel {
        session: SessionId,
        scope: CancelScope,
    },
    /// Graceful exit: the whole process tree exits within 2 seconds with
    /// exit code 0.
    Shutdown,
}

/// Kernel events (the JSONL exit serializes this type; `ts_ms` is a
/// monotonic timestamp).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum KernelEvent {
    SessionStarted {
        session: SessionId,
        ts_ms: u64,
    },
    /// Recovery from crash/interruption completed.
    SessionRecovered {
        session: SessionId,
        from_turn: TurnId,
        ts_ms: u64,
    },
    TurnStarted {
        session: SessionId,
        turn: TurnId,
        ts_ms: u64,
    },
    /// Streaming text delta. Aggregation granularity is decided by the core
    /// side (a `bytes`-only summary is the special case: empty `text`,
    /// `bytes` only).
    ModelDelta {
        session: SessionId,
        turn: TurnId,
        seq: u64,
        text: String,
        bytes: u64,
        ts_ms: u64,
    },
    ToolCall {
        session: SessionId,
        turn: TurnId,
        call: ToolCallId,
        tool: String,
        input: Value,
        ts_ms: u64,
    },
    ToolResult {
        session: SessionId,
        turn: TurnId,
        call: ToolCallId,
        status: ToolStatus,
        bytes: u64,
        ts_ms: u64,
    },
    /// Context compaction/truncation completed.
    Compacted {
        session: SessionId,
        kept_tokens: u64,
        ts_ms: u64,
    },
    TurnDone {
        session: SessionId,
        turn: TurnId,
        ts_ms: u64,
    },
    Error {
        session: Option<SessionId>,
        code: String,
        fatal: bool,
        ts_ms: u64,
    },
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum WireError {
    /// The exit is closed (host quitting, downstream disconnected).
    Closed,
    /// Bounded buffer overflow under an error policy (drop/merge policies do
    /// not produce this error).
    Overflow,
}

/// Command source port.
pub trait CommandSource: Send {
    /// Block until the next command; `None` means the entrance is closed and
    /// the kernel should enter its shutdown flow.
    fn next_command(&mut self) -> BoxFuture<'_, Option<Command>>;
}

/// Event sink port.
///
/// Boundedness is the adapter's responsibility: when the buffer is full,
/// `emit` suspends to form backpressure against the core side, or drops/
/// merges per the policy chosen by the composition root and returns
/// [`WireError::Overflow`] — the kernel core only assumes "emit never
/// occupies unbounded memory".
pub trait EventSink: Send + Sync {
    fn emit(&self, event: KernelEvent) -> BoxFuture<'_, Result<(), WireError>>;
}
