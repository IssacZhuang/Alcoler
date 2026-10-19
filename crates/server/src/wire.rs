//! Channel adapters for the command entrance and the event exit.

use std::sync::Arc;

use alcoler_contracts::BoxFuture;
use alcoler_contracts::wire::{Command, CommandSource, EventSink, KernelEvent, WireError};
use tokio::sync::{broadcast, mpsc};

/// Command entrance backed by an mpsc channel.
pub struct ChannelSource {
    rx: mpsc::Receiver<Command>,
}

impl ChannelSource {
    pub fn new(rx: mpsc::Receiver<Command>) -> Self {
        Self { rx }
    }
}

impl CommandSource for ChannelSource {
    fn next_command(&mut self) -> BoxFuture<'_, Option<Command>> {
        Box::pin(self.rx.recv())
    }
}

/// Event exit: serialized frames on a broadcast channel. Having zero live
/// receivers is not an error — SSE exits and the projection subscribe after
/// startup, and per-subscriber lag is a drop policy (bounded by contract).
pub struct BroadcastSink {
    tx: broadcast::Sender<Arc<String>>,
}

impl BroadcastSink {
    pub fn new(tx: broadcast::Sender<Arc<String>>) -> Self {
        Self { tx }
    }
}

impl EventSink for BroadcastSink {
    fn emit(&self, event: KernelEvent) -> BoxFuture<'_, Result<(), WireError>> {
        Box::pin(async move {
            // KernelEvent serialization is infallible for these shapes; if it
            // ever failed, dropping the frame is preferable to failing the turn.
            if let Ok(frame) = serde_json::to_string(&event) {
                let _ = self.tx.send(Arc::new(frame));
            }
            Ok(())
        })
    }
}
