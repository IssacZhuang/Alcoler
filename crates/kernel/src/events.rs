//! Per-turn event stamping and collection: every event is both streamed to
//! the sink (live) and recorded for the atomic `TurnRecord`.

use std::sync::Arc;

use alcoler_contracts::time::Clock;
use alcoler_contracts::wire::{EventSink, KernelEvent};

pub(crate) struct EventLog {
    clock: Arc<dyn Clock>,
    events: Vec<KernelEvent>,
}

impl EventLog {
    pub(crate) fn new(clock: Arc<dyn Clock>) -> Self {
        Self {
            clock,
            events: Vec::new(),
        }
    }

    pub(crate) fn now(&self) -> u64 {
        self.clock.monotonic_ms()
    }

    /// Stream to the sink and record. A wire error (overflow/closed exit)
    /// must not abort the turn: persistence via `TurnRecord.events` stays
    /// authoritative, so sink failures are swallowed here.
    pub(crate) async fn emit(&mut self, sink: &Arc<dyn EventSink>, event: KernelEvent) {
        self.events.push(event.clone());
        let _ = sink.emit(event).await;
    }

    /// Record without streaming (for events that must be persisted before
    /// they are surfaced, e.g. TurnDone is emitted only after `append_turn`).
    pub(crate) fn record(&mut self, event: KernelEvent) {
        self.events.push(event);
    }

    pub(crate) fn finish(self) -> Vec<KernelEvent> {
        self.events
    }
}
