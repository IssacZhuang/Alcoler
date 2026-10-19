//! Turn-scoped cancellation token (kernel-internal implementation of the
//! `CancelToken` port).

use alcoler_contracts::BoxFuture;
use alcoler_contracts::time::CancelToken;
use tokio::sync::watch;

/// Cancellation token for one turn. `watch` is used (not `Notify`) because a
/// waiter that subscribes after `cancel()` must still observe the cancelled
/// state — there is no lost-wakeup window.
pub(crate) struct TurnCancel {
    tx: watch::Sender<bool>,
}

impl TurnCancel {
    pub(crate) fn new() -> Self {
        let (tx, _rx) = watch::channel(false);
        Self { tx }
    }
}

impl CancelToken for TurnCancel {
    fn cancel(&self) {
        let _ = self.tx.send(true);
    }

    fn is_cancelled(&self) -> bool {
        *self.tx.borrow()
    }

    fn cancelled(&self) -> BoxFuture<'static, ()> {
        let mut rx = self.tx.subscribe();
        Box::pin(async move {
            loop {
                if *rx.borrow() {
                    return;
                }
                if rx.changed().await.is_err() {
                    return;
                }
            }
        })
    }
}
