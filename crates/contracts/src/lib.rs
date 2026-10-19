//! Alcoler kernel contracts.
//!
//! This crate defines only the boundary between the kernel and the outside
//! world: ports (traits), domain types, and error types — no implementations.
//! Dependency rules:
//!
//! - The kernel core (a future `kernel-core` crate) may depend only on this
//!   crate, never on any concrete technology;
//! - Adapters (SQLite / HTTP providers / process execution / MCP / browser
//!   plugin …) implement the ports defined here and are injected into the
//!   kernel after assembly at the composition root;
//! - The kernel programs against ports only — whether persistence is backed
//!   by SQLite, JSONL, or an in-memory test stub, and which provider backs
//!   the model, is invisible to consumers.
//!
//! Async shape: all port methods return [`BoxFuture`] / [`BoxStream`]
//! (hand-boxed `Send` futures / streams), keeping the traits object-safe
//! (dyn-dispatchable, multiple implementations registrable at runtime) and
//! the contract layer free of any specific async runtime; the runtime is
//! chosen by the composition root (e.g. tokio), and adapters map port types
//! onto runtime primitives.
//!
//! `unsafe` is forbidden in this crate; the only dependencies are
//! `serde`/`serde_json` (data formats) and `futures-core` (the `Stream`
//! trait) — runtime-agnostic foundation libraries.

#![forbid(unsafe_code)]

pub mod context;
pub mod ids;
pub mod message;
pub mod model;
pub mod permission;
pub mod storage;
pub mod time;
pub mod tool;
pub mod wire;

/// Boxed future: the uniform async return shape of port methods (equivalent
/// to what the `async-trait` macro produces, written by hand to keep zero
/// macro dependencies and clear diagnostics).
pub type BoxFuture<'a, T> = std::pin::Pin<Box<dyn std::future::Future<Output = T> + Send + 'a>>;

/// Boxed stream: the uniform return shape of streaming ports (model deltas,
/// ledger replay).
pub type BoxStream<'a, T> = std::pin::Pin<Box<dyn futures_core::Stream<Item = T> + Send + 'a>>;
