//! Alcoler kernel core: the agent turn loop, driven entirely by the ports in
//! `alcoler-contracts`.
//!
//! Composition: the kernel owns one command loop ([`Kernel::run`]) that drains
//! a [`CommandSource`]. Each session runs at most one turn at a time; extra
//! submissions queue behind it (FIFO). Turns run as spawned tasks so cancel
//! commands reach the loop while a turn is in flight.
//!
//! This crate depends on tokio only for task spawning and sync primitives;
//! every timer goes through the `Clock` port so tests stay deterministic.

#![forbid(unsafe_code)]

mod cancel;
mod context;
mod events;
mod schema;
mod session;
mod toolset;
mod turn;

use std::collections::HashMap;
use std::sync::Arc;

use alcoler_contracts::context::TokenCounter;
use alcoler_contracts::ids::SessionId;
use alcoler_contracts::model::ModelProvider;
use alcoler_contracts::permission::PermissionGate;
use alcoler_contracts::storage::{BlobStore, SessionStore};
use alcoler_contracts::time::{Clock, IdGenerator};
use alcoler_contracts::tool::Tool;
use alcoler_contracts::wire::{Command, CommandSource, EventSink};
use tokio::sync::Mutex;
use tokio::task::JoinHandle;

/// Ports the kernel is assembled from (injected by the composition root).
pub struct KernelDeps {
    pub store: Arc<dyn SessionStore>,
    pub blobs: Arc<dyn BlobStore>,
    pub provider: Arc<dyn ModelProvider>,
    pub tools: Vec<Arc<dyn Tool>>,
    pub gate: Arc<dyn PermissionGate>,
    pub counter: Arc<dyn TokenCounter>,
    pub clock: Arc<dyn Clock>,
    pub ids: Arc<dyn IdGenerator>,
}

/// Kernel tuning knobs. Semantics that the contracts pin down (pairing,
/// stream-drop cancellation, dedup, retry-on-emitted-output) are not
/// configurable here.
#[derive(Debug, Clone)]
pub struct KernelConfig {
    pub system_prompt: Option<String>,
    pub max_output_tokens: Option<u64>,
    pub temperature: Option<f32>,
    /// Upper bound on attempts per model step (1 = never retry).
    pub max_provider_attempts: u32,
    /// First retry backoff in ms; doubles per attempt, capped at 8 s.
    pub retry_base_ms: u64,
    /// ModelDelta flush threshold: buffered text is emitted once it reaches
    /// this many bytes (block boundaries always flush).
    pub text_flush_bytes: usize,
    /// Compaction keeps the contents of at most this many recent tool
    /// results; older ones are replaced with a placeholder.
    pub keep_tool_results: usize,
}

impl Default for KernelConfig {
    fn default() -> Self {
        Self {
            system_prompt: None,
            max_output_tokens: None,
            temperature: None,
            max_provider_attempts: 5,
            retry_base_ms: 500,
            text_flush_bytes: 512,
            keep_tool_results: 5,
        }
    }
}

pub(crate) struct KernelInner {
    pub deps: KernelDeps,
    pub config: KernelConfig,
    pub registry: HashMap<String, toolset::ToolEntry>,
    pub sessions: Mutex<HashMap<SessionId, Arc<session::Session>>>,
    pub tasks: std::sync::Mutex<Vec<JoinHandle<()>>>,
}

/// The kernel: a thin handle over the shared inner state.
pub struct Kernel(Arc<KernelInner>);

impl Kernel {
    pub fn new(deps: KernelDeps, config: KernelConfig) -> Self {
        let mut registry = HashMap::new();
        for tool in &deps.tools {
            let spec = tool.spec();
            registry.insert(
                spec.name.clone(),
                toolset::ToolEntry {
                    tool: tool.clone(),
                    schema: schema::Schema::new(&spec.input_schema),
                },
            );
        }
        Self(Arc::new(KernelInner {
            deps,
            config,
            registry,
            sessions: Mutex::new(HashMap::new()),
            tasks: std::sync::Mutex::new(Vec::new()),
        }))
    }

    /// Drain the command source until it closes or `Shutdown` arrives, then
    /// run the shutdown flow (cancel active turns, bounded drain). Takes `self`
    /// by value (an `Arc` handle) so the loop can be spawned as a task.
    pub async fn run(self, source: Box<dyn CommandSource>, sink: Arc<dyn EventSink>) {
        let mut source = source;
        while let Some(command) = source.next_command().await {
            match command {
                Command::StartSession { session, profile } => {
                    self.0.clone().start_session(session, profile, &sink).await;
                }
                Command::Submit {
                    session,
                    command,
                    input,
                } => {
                    self.0.clone().submit(session, command, input, &sink).await;
                }
                Command::Cancel { session, scope } => {
                    self.0.clone().cancel_session(session, scope).await;
                }
                Command::Shutdown => break,
            }
        }
        self.0.clone().shutdown().await;
    }
}
