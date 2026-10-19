//! Session state and the command-loop side of the kernel: session
//! lifecycle, submit queuing, cancel routing, and shutdown.

use std::collections::{HashSet, VecDeque};
use std::sync::Arc;

use alcoler_contracts::ids::{CommandId, SessionId, TurnId};
use alcoler_contracts::message::Message;
use alcoler_contracts::storage::{
    SessionMeta, SessionProfile, SessionRecord, StoreError, StoreErrorKind,
};
use alcoler_contracts::time::CancelToken;
use alcoler_contracts::wire::{CancelScope, EventSink, KernelEvent, UserInput};
use futures_util::StreamExt;
use tokio::sync::Mutex;

use crate::KernelInner;
use crate::cancel::TurnCancel;
use crate::events::EventLog;

pub(crate) struct Session {
    pub meta: SessionMeta,
    pub history: Mutex<Vec<Message>>,
    pub control: Mutex<Control>,
}

/// Per-session mutable scheduling state. `active`, `queue`, `seen`, and
/// `allowed_tools` are only touched under this lock; the turn loop itself
/// works on `history` so submits/cancels never block on model I/O.
pub(crate) struct Control {
    pub active: Option<ActiveTurn>,
    pub queue: VecDeque<QueuedInput>,
    /// Command dedup, in-memory only: the turn ledger has no field to carry
    /// command ids, so dedup cannot survive a process crash within this
    /// contract.
    pub seen: HashSet<CommandId>,
    pub allowed_tools: HashSet<String>,
    pub turns_completed: u64,
}

pub(crate) struct ActiveTurn {
    pub turn: TurnId,
    pub cancel: Arc<TurnCancel>,
}

pub(crate) struct QueuedInput {
    pub input: UserInput,
}

impl KernelInner {
    pub(crate) async fn start_session(
        self: Arc<Self>,
        id: SessionId,
        profile: SessionProfile,
        sink: &Arc<dyn EventSink>,
    ) {
        let mut sessions = self.sessions.lock().await;
        if sessions.contains_key(&id) {
            return;
        }

        let meta = SessionMeta {
            id: id.clone(),
            profile,
            created_at_unix_ms: self.deps.clock.unix_ms(),
        };
        let mut log = EventLog::new(self.deps.clock.clone());
        let mut history: Vec<Message> = Vec::new();
        let mut turns_completed: u64 = 0;
        let mut recovered = false;

        match self.deps.store.create_session(&meta).await {
            Ok(()) => {}
            Err(StoreError {
                kind: StoreErrorKind::Conflict,
                ..
            }) => {
                recovered = self
                    .recover_history(&id, &mut history, &mut turns_completed)
                    .await;
                if !recovered {
                    // Conflict without a loadable record: treat as fresh.
                    let _ = self.deps.store.create_session(&meta).await;
                }
            }
            Err(error) => {
                log.emit(
                    sink,
                    KernelEvent::Error {
                        session: Some(id.clone()),
                        code: format!("store_{}", kind_tag(&error.kind)),
                        fatal: false,
                        ts_ms: log.now(),
                    },
                )
                .await;
                return;
            }
        }

        let session = Arc::new(Session {
            meta,
            history: Mutex::new(history),
            control: Mutex::new(Control {
                active: None,
                queue: VecDeque::new(),
                seen: HashSet::new(),
                allowed_tools: HashSet::new(),
                turns_completed,
            }),
        });
        sessions.insert(id.clone(), session);

        let event = if recovered {
            KernelEvent::SessionRecovered {
                session: id,
                from_turn: TurnId(turns_completed),
                ts_ms: log.now(),
            }
        } else {
            KernelEvent::SessionStarted {
                session: id,
                ts_ms: log.now(),
            }
        };
        log.emit(sink, event).await;
    }

    /// Rebuild history from the latest snapshot plus the turn ledger after
    /// it. A turn that never completed (no `append_turn`) simply never
    /// happened; `save_turn_progress` has no read port, so in-flight repair
    /// is bounded to what replay can reconstruct.
    async fn recover_history(
        &self,
        id: &SessionId,
        history: &mut Vec<Message>,
        turns_completed: &mut u64,
    ) -> bool {
        let record: Option<SessionRecord> = match self.deps.store.load_session(id).await {
            Ok(record) => record,
            Err(_) => return false,
        };
        let Some(record) = record else {
            return false;
        };

        let mut turns = 0u64;
        let mut messages = Vec::new();
        if let Some(snapshot) = &record.snapshot {
            messages = snapshot.messages.clone();
            turns = snapshot.turns_completed;
        }
        if let Ok(stream) = self.deps.store.replay_turns(id, TurnId(turns)).await {
            let mut stream = stream;
            while let Some(item) = stream.next().await {
                match item {
                    Ok(record) => {
                        turns = turns.max(record.turn.0);
                        messages.extend(record.new_messages);
                    }
                    Err(_) => break,
                }
            }
        }

        *history = messages;
        *turns_completed = turns;
        true
    }

    pub(crate) async fn submit(
        self: Arc<Self>,
        id: SessionId,
        command: CommandId,
        input: UserInput,
        sink: &Arc<dyn EventSink>,
    ) {
        let session = self.sessions.lock().await.get(&id).cloned();
        let Some(session) = session else {
            let mut log = EventLog::new(self.deps.clock.clone());
            log.emit(
                sink,
                KernelEvent::Error {
                    session: Some(id),
                    code: "unknown_session".to_string(),
                    fatal: false,
                    ts_ms: log.now(),
                },
            )
            .await;
            return;
        };

        let mut control = session.control.lock().await;
        if !control.seen.insert(command.clone()) {
            return;
        }
        if control.active.is_some() {
            control.queue.push_back(QueuedInput { input });
            return;
        }
        self.clone()
            .begin_turn(&session, &mut control, input, sink.clone());
    }

    /// Reserve the next turn id and spawn the turn task. Called with
    /// `control` locked, from either the command loop or the finishing turn.
    /// Synchronous on purpose: awaiting it from `run_turn` would create a
    /// recursive async type the compiler cannot prove `Send`.
    pub(crate) fn begin_turn(
        self: Arc<Self>,
        session: &Arc<Session>,
        control: &mut Control,
        input: UserInput,
        sink: Arc<dyn EventSink>,
    ) {
        let turn = TurnId(control.turns_completed + 1);
        let cancel = Arc::new(TurnCancel::new());
        control.active = Some(ActiveTurn {
            turn,
            cancel: cancel.clone(),
        });

        let inner = self.clone();
        let session_for_task = session.clone();
        let handle = tokio::spawn(async move {
            crate::turn::run_turn(inner, session_for_task, turn, input, cancel, sink).await;
        });

        let mut tasks = lock_tasks(&self.tasks);
        tasks.retain(|task| !task.is_finished());
        tasks.push(handle);
    }

    pub(crate) async fn cancel_session(self: Arc<Self>, id: SessionId, scope: CancelScope) {
        let session = self.sessions.lock().await.get(&id).cloned();
        let Some(session) = session else {
            return;
        };
        let mut control = session.control.lock().await;
        match scope {
            CancelScope::Turn(turn) => {
                if let Some(active) = &control.active
                    && active.turn == turn
                {
                    active.cancel.cancel();
                }
            }
            CancelScope::Session => {
                control.queue.clear();
                if let Some(active) = &control.active {
                    active.cancel.cancel();
                }
            }
        }
    }

    /// Contract: the whole process settles within 2 s of `Shutdown`. Cancel
    /// every active turn, then drain turn tasks against a 1.5 s budget; a
    /// turn task that ignores cancellation is abandoned (process exit
    /// reclaims it).
    pub(crate) async fn shutdown(self: Arc<Self>) {
        {
            let sessions = self.sessions.lock().await;
            for session in sessions.values() {
                let mut control = session.control.lock().await;
                control.queue.clear();
                if let Some(active) = &control.active {
                    active.cancel.cancel();
                }
            }
        }

        let mut pending = {
            let mut tasks = lock_tasks(&self.tasks);
            tasks.retain(|task| !task.is_finished());
            std::mem::take(&mut *tasks)
        };

        let drain = async {
            for task in pending.drain(..) {
                let _ = task.await;
            }
        };
        let _ =
            futures_util::future::select(Box::pin(drain), Box::pin(self.deps.clock.delay_ms(1500)))
                .await;
    }
}

fn kind_tag(kind: &StoreErrorKind) -> &'static str {
    match kind {
        StoreErrorKind::Io => "io",
        StoreErrorKind::Corrupted => "corrupted",
        StoreErrorKind::Conflict => "conflict",
        StoreErrorKind::Unavailable => "unavailable",
        StoreErrorKind::InvalidData => "invalid_data",
    }
}

/// Std-mutex guard for the task list; the lock is never held across awaits.
fn lock_tasks(
    tasks: &std::sync::Mutex<Vec<tokio::task::JoinHandle<()>>>,
) -> std::sync::MutexGuard<'_, Vec<tokio::task::JoinHandle<()>>> {
    tasks
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}
