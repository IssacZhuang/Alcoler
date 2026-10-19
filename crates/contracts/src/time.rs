//! Time, identifiers, and cancellation: the foundation ports for
//! deterministic testing and cancellation propagation.

use crate::BoxFuture;

/// Clock port.
///
/// Event timestamps, timeout computation, and approval timeouts all go
/// through this port; tests inject a fixed clock for fully deterministic
/// scheduling behavior. `delay_ms` lets the core side implement timeout
/// wrappers without depending directly on runtime timer primitives.
pub trait Clock: Send + Sync {
    /// Wall-clock time (Unix milliseconds), for metadata and logs.
    fn unix_ms(&self) -> i64;

    /// Monotonic clock (milliseconds), for event timestamps (the wire
    /// contract requires monotonic timestamps) and deadline computation.
    fn monotonic_ms(&self) -> u64;

    /// Timer: completes after at least `ms` milliseconds.
    fn delay_ms(&self, ms: u64) -> BoxFuture<'static, ()>;
}

/// Identifier generation port: globally unique ids for sessions, commands,
/// blob keys, …
///
/// Production implementations use ULID/UUID; test implementations return
/// increasing sequences so replay and assertions stay deterministic.
pub trait IdGenerator: Send + Sync {
    fn next_id(&self) -> String;
}

/// Cancel token port.
///
/// The cancellation propagation chain: turn cancel → event sink stops
/// aggregating → model stream (dropping the stream cancels) → each in-flight
/// tool observes via [`crate::tool::ToolExecContext::cancel`] and lands on a
/// paired `ToolStatus::Cancelled` result.
pub trait CancelToken: Send + Sync {
    /// Trigger cancellation. Idempotent; callable from any thread.
    fn cancel(&self);

    fn is_cancelled(&self) -> bool;

    /// A future that completes once cancelled: tool implementations should
    /// `await` it inside long operations as a cooperative cancellation
    /// point, not merely check at the end.
    fn cancelled(&self) -> BoxFuture<'static, ()>;
}
