//! Persistence ports: the storage boundary for sessions, turn ledgers, and
//! blobs.
//!
//! The backend may be SQLite (WAL, atomic per-turn flush), JSONL append (the
//! wasm variant proved the same contract is swappable), or an in-memory test
//! stub — the kernel core is unaware of the backend. Key semantics:
//!
//! - **Atomic per-turn flush**: a turn's messages, events, and snapshot are
//!   written in one transaction ([`TurnRecord`]);
//! - **Idempotent dedup**: re-persisting the same turn returns
//!   [`StoreErrorKind::Conflict`], backing command dedup and stale-write
//!   protection after crash recovery;
//! - **In-flight ledger**: while a turn is running, [`TurnProgress`] is
//!   overwritten periodically, so tool_call / tool_result pairing can be
//!   repaired after a crash;
//! - **Bounded growth**: [`RetentionPolicy`] prunes history.

use crate::ids::{SessionId, ToolCallId, TurnId};
use crate::message::Message;
use crate::model::ModelRef;
use crate::wire::KernelEvent;
use crate::{BoxFuture, BoxStream};
use serde::{Deserialize, Serialize};

/// Session run parameters (carried by the `StartSession` command, persisted
/// in [`SessionMeta`]).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SessionProfile {
    /// Profile name ("standard" / "medium" …).
    pub name: String,
    pub model: ModelRef,
    /// Context token budget: the upper bound that triggers truncation and
    /// compaction.
    pub context_budget_tokens: u64,
    /// Tool concurrency limit.
    pub max_concurrent_tools: u32,
    /// Event sink buffer bound.
    pub event_buffer_bound: usize,
    /// Approval timeout (milliseconds); timeout defaults to deny.
    pub permission_timeout_ms: u64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SessionMeta {
    pub id: SessionId,
    pub profile: SessionProfile,
    pub created_at_unix_ms: i64,
}

/// Recovery checkpoint: the most recently fully persisted session state.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SessionSnapshot {
    pub messages: Vec<Message>,
    /// Number of fully persisted turns.
    pub turns_completed: u64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SessionRecord {
    pub meta: SessionMeta,
    pub snapshot: Option<SessionSnapshot>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SessionSummary {
    pub id: SessionId,
    pub turns_completed: u64,
    pub updated_at_unix_ms: i64,
}

/// In-flight phase of a single tool call (the pairing-repair basis for crash
/// recovery).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ToolCallPhase {
    Pending,
    Running,
    Completed,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ToolCallProgress {
    pub call: ToolCallId,
    pub tool: String,
    pub phase: ToolCallPhase,
}

/// State of a turn in progress (streaming tool ledger + recovery anchor):
/// overwritten by the core side; implementations must be idempotent.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TurnProgress {
    pub session: SessionId,
    pub turn: TurnId,
    pub tool_calls: Vec<ToolCallProgress>,
}

/// The atomic persistence unit of one turn.
///
/// Implementations must guarantee: messages, events, and snapshot are written
/// in one transaction; if the turn has already been persisted, return
/// [`StoreErrorKind::Conflict`] (idempotent dedup).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct TurnRecord {
    pub session: SessionId,
    pub turn: TurnId,
    /// Complete messages appended this turn (incl. paired tool results).
    pub new_messages: Vec<Message>,
    /// This turn's events (for replay and audit).
    pub events: Vec<KernelEvent>,
    /// Session snapshot after persisting.
    pub snapshot: SessionSnapshot,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct RetentionPolicy {
    pub keep_last_turns_per_session: Option<u64>,
    pub keep_sessions: Option<u64>,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct RetainReport {
    pub turns_removed: u64,
    pub sessions_removed: u64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum StoreErrorKind {
    Io,
    Corrupted,
    /// Stale/duplicate write: turn already persisted, session already
    /// exists, etc.
    Conflict,
    Unavailable,
    InvalidData,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct StoreError {
    pub kind: StoreErrorKind,
    pub message: String,
}

/// Session store port.
///
/// Borrowed parameters use explicit `'a` lifetimes throughout: an
/// implementation may borrow metadata/keys directly inside the returned
/// future instead of cloning first.
pub trait SessionStore: Send + Sync {
    /// Open/initialize the backend (create schema, migrate, open files).
    /// Called once at process startup.
    fn open(&self) -> BoxFuture<'_, Result<(), StoreError>>;

    fn create_session<'a>(&'a self, meta: &'a SessionMeta)
    -> BoxFuture<'a, Result<(), StoreError>>;

    /// Load session metadata and the latest checkpoint; `None` if absent.
    fn load_session<'a>(
        &'a self,
        id: &'a SessionId,
    ) -> BoxFuture<'a, Result<Option<SessionRecord>, StoreError>>;

    fn list_sessions(&self) -> BoxFuture<'_, Result<Vec<SessionSummary>, StoreError>>;

    /// Overwrite the in-flight state of a running turn. After a crash the
    /// core side repairs tool_call/tool_result pairing from this (unfinished
    /// calls get `Cancelled` results).
    fn save_turn_progress<'a>(
        &'a self,
        progress: &'a TurnProgress,
    ) -> BoxFuture<'a, Result<(), StoreError>>;

    /// Atomically persist one completed turn.
    fn append_turn(&self, record: TurnRecord) -> BoxFuture<'_, Result<(), StoreError>>;

    /// Replay the ledger after a given turn (the recovery path when no
    /// snapshot exists). Streams, so memory stays bounded.
    fn replay_turns<'a>(
        &'a self,
        id: &'a SessionId,
        after: TurnId,
    ) -> BoxFuture<'a, Result<BoxStream<'static, Result<TurnRecord, StoreError>>, StoreError>>;

    /// Prune history per the retention policy (bounded growth).
    fn apply_retention<'a>(
        &'a self,
        policy: &'a RetentionPolicy,
    ) -> BoxFuture<'a, Result<RetainReport, StoreError>>;

    /// Close cleanly and release resources.
    fn close(&self) -> BoxFuture<'_, Result<(), StoreError>>;
}

/// Blob reference.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct BlobRef {
    pub session: SessionId,
    pub key: String,
}

/// Blob store port: oversized tool results are persisted in full while the
/// model side receives only a "preview + reference" envelope (the artifact
/// strategy of the two-level result budget; the result body and its model
/// projection are separate).
pub trait BlobStore: Send + Sync {
    fn put<'a>(
        &'a self,
        session: &'a SessionId,
        key: &'a str,
        bytes: Vec<u8>,
    ) -> BoxFuture<'a, Result<BlobRef, StoreError>>;

    fn get<'a>(
        &'a self,
        session: &'a SessionId,
        key: &'a str,
    ) -> BoxFuture<'a, Result<Option<Vec<u8>>, StoreError>>;
}
