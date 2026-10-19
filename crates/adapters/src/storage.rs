//! File-backed session store and blob store.
//!
//! Layout under the root directory:
//!
//! ```text
//! sessions/<id>/meta.json      session metadata (creation)
//! sessions/<id>/turns.jsonl    one TurnRecord per line (durable ledger)
//! sessions/<id>/snapshot.json  latest checkpoint (cache; replay repairs)
//! sessions/<id>/progress.json  in-flight tool ledger (overwrite)
//! sessions/<id>/blobs/<key>    oversized tool outputs
//! ```
//!
//! Durability contract: `turns.jsonl` is the source of truth; the snapshot
//! is a rebuildable cache. A crash between the ledger append and the
//! snapshot rewrite is repaired by `replay_turns`.

use std::collections::{BTreeSet, HashMap};
use std::path::{Path, PathBuf};
use std::sync::Arc;

use alcoler_contracts::ids::{SessionId, TurnId};
use alcoler_contracts::storage::{
    BlobRef, BlobStore, RetainReport, RetentionPolicy, SessionMeta, SessionRecord, SessionSnapshot,
    SessionStore, SessionSummary, StoreError, StoreErrorKind, TurnProgress, TurnRecord,
};
use alcoler_contracts::{BoxFuture, BoxStream};
use tokio::sync::Mutex;
use tokio_stream::wrappers::ReceiverStream;

struct SessionEntry {
    meta: SessionMeta,
    dir: PathBuf,
    turns: BTreeSet<u64>,
    snapshot: Option<SessionSnapshot>,
    updated_at_unix_ms: i64,
}

#[derive(Default)]
struct Index {
    sessions: HashMap<String, SessionEntry>,
}

fn io_error(context: &str, error: impl std::fmt::Display) -> StoreError {
    StoreError {
        kind: StoreErrorKind::Io,
        message: format!("{context}: {error}"),
    }
}

fn sanitize(component: &str) -> String {
    component
        .chars()
        .map(|c| {
            if c.is_ascii_alphanumeric() || c == '-' || c == '_' {
                c
            } else {
                '_'
            }
        })
        .collect()
}

fn write_atomic(path: &Path, bytes: &[u8]) -> Result<(), StoreError> {
    let tmp = path.with_extension("tmp");
    std::fs::write(&tmp, bytes).map_err(|e| io_error("write temp", e))?;
    std::fs::rename(&tmp, path).map_err(|e| io_error("rename", e))
}

fn append_line(path: &Path, line: &str) -> Result<(), StoreError> {
    use std::io::Write;
    let mut file = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(path)
        .map_err(|e| io_error("open ledger", e))?;
    file.write_all(line.as_bytes())
        .and_then(|_| file.write_all(b"\n"))
        .and_then(|_| file.sync_data())
        .map_err(|e| io_error("append ledger", e))
}

pub struct FileSessionStore {
    root: PathBuf,
    index: Arc<Mutex<Index>>,
}

impl FileSessionStore {
    pub fn new(root: impl Into<PathBuf>) -> Self {
        Self {
            root: root.into(),
            index: Arc::new(Mutex::new(Index::default())),
        }
    }

    fn sessions_dir(&self) -> PathBuf {
        self.root.join("sessions")
    }

    async fn scan(&self) -> Result<(), StoreError> {
        let sessions_dir = self.sessions_dir();
        std::fs::create_dir_all(&sessions_dir).map_err(|e| io_error("create root", e))?;
        let mut sessions = HashMap::new();
        let entries = std::fs::read_dir(&sessions_dir).map_err(|e| io_error("read root", e))?;
        for entry in entries.flatten() {
            let dir = entry.path();
            if !dir.is_dir() {
                continue;
            }
            let Some(name) = dir.file_name().and_then(|n| n.to_str()) else {
                continue;
            };
            let meta_path = dir.join("meta.json");
            let Ok(meta_bytes) = std::fs::read(&meta_path) else {
                continue;
            };
            let Ok(meta) = serde_json::from_slice::<SessionMeta>(&meta_bytes) else {
                continue;
            };
            let mut turns = BTreeSet::new();
            let ledger = dir.join("turns.jsonl");
            if let Ok(content) = std::fs::read_to_string(&ledger) {
                for line in content.lines() {
                    if let Ok(record) = serde_json::from_str::<TurnRecord>(line) {
                        turns.insert(record.turn.0);
                    }
                }
            }
            let snapshot = std::fs::read(dir.join("snapshot.json"))
                .ok()
                .and_then(|bytes| serde_json::from_slice::<SessionSnapshot>(&bytes).ok());
            let updated_at_unix_ms = std::fs::metadata(&ledger)
                .and_then(|m| m.modified())
                .ok()
                .and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok())
                .map(|d| d.as_millis() as i64)
                .unwrap_or(meta.created_at_unix_ms);
            sessions.insert(
                name.to_string(),
                SessionEntry {
                    meta,
                    dir,
                    turns,
                    snapshot,
                    updated_at_unix_ms,
                },
            );
        }
        let mut index = self.index.lock().await;
        index.sessions = sessions;
        Ok(())
    }
}

impl SessionStore for FileSessionStore {
    fn open(&self) -> BoxFuture<'_, Result<(), StoreError>> {
        Box::pin(async move { self.scan().await })
    }

    fn create_session<'a>(
        &'a self,
        meta: &'a SessionMeta,
    ) -> BoxFuture<'a, Result<(), StoreError>> {
        Box::pin(async move {
            let key = sanitize(&meta.id.0);
            let mut index = self.index.lock().await;
            if index.sessions.contains_key(&key) {
                return Err(StoreError {
                    kind: StoreErrorKind::Conflict,
                    message: "session already exists".into(),
                });
            }
            let dir = self.sessions_dir().join(&key);
            std::fs::create_dir_all(&dir).map_err(|e| io_error("create session dir", e))?;
            let bytes = serde_json::to_vec(meta).map_err(|e| io_error("serialize meta", e))?;
            write_atomic(&dir.join("meta.json"), &bytes)?;
            std::fs::create_dir_all(dir.join("blobs"))
                .map_err(|e| io_error("create blobs dir", e))?;
            index.sessions.insert(
                key,
                SessionEntry {
                    meta: meta.clone(),
                    dir,
                    turns: BTreeSet::new(),
                    snapshot: None,
                    updated_at_unix_ms: meta.created_at_unix_ms,
                },
            );
            Ok(())
        })
    }

    fn load_session<'a>(
        &'a self,
        id: &'a SessionId,
    ) -> BoxFuture<'a, Result<Option<SessionRecord>, StoreError>> {
        Box::pin(async move {
            let index = self.index.lock().await;
            let Some(entry) = index.sessions.get(&sanitize(&id.0)) else {
                return Ok(None);
            };
            Ok(Some(SessionRecord {
                meta: entry.meta.clone(),
                snapshot: entry.snapshot.clone(),
            }))
        })
    }

    fn list_sessions(&self) -> BoxFuture<'_, Result<Vec<SessionSummary>, StoreError>> {
        Box::pin(async {
            let index = self.index.lock().await;
            Ok(index
                .sessions
                .values()
                .map(|entry| SessionSummary {
                    id: entry.meta.id.clone(),
                    turns_completed: entry.turns.last().copied().unwrap_or(0),
                    updated_at_unix_ms: entry.updated_at_unix_ms,
                })
                .collect())
        })
    }

    fn save_turn_progress<'a>(
        &'a self,
        progress: &'a TurnProgress,
    ) -> BoxFuture<'a, Result<(), StoreError>> {
        Box::pin(async move {
            let index = self.index.lock().await;
            let Some(entry) = index.sessions.get(&sanitize(&progress.session.0)) else {
                return Err(StoreError {
                    kind: StoreErrorKind::InvalidData,
                    message: "unknown session".into(),
                });
            };
            let bytes =
                serde_json::to_vec(progress).map_err(|e| io_error("serialize progress", e))?;
            write_atomic(&entry.dir.join("progress.json"), &bytes)
        })
    }

    fn append_turn(&self, record: TurnRecord) -> BoxFuture<'_, Result<(), StoreError>> {
        Box::pin(async move {
            let mut index = self.index.lock().await;
            let key = sanitize(&record.session.0);
            let Some(entry) = index.sessions.get_mut(&key) else {
                return Err(StoreError {
                    kind: StoreErrorKind::InvalidData,
                    message: "unknown session".into(),
                });
            };
            if entry.turns.contains(&record.turn.0) {
                return Err(StoreError {
                    kind: StoreErrorKind::Conflict,
                    message: format!("turn {} already persisted", record.turn.0),
                });
            }
            let line = serde_json::to_string(&record).map_err(|e| io_error("serialize turn", e))?;
            append_line(&entry.dir.join("turns.jsonl"), &line)?;

            // Rebuild the snapshot by folding this turn over the previous
            // checkpoint so the cache can never drift from the ledger.
            let mut messages = entry
                .snapshot
                .clone()
                .map(|s| s.messages)
                .unwrap_or_default();
            let mut turns_completed = entry
                .snapshot
                .as_ref()
                .map(|s| s.turns_completed)
                .unwrap_or(0);
            turns_completed = turns_completed.max(record.turn.0);
            messages.extend(record.new_messages.iter().cloned());
            let snapshot = SessionSnapshot {
                messages,
                turns_completed,
            };
            let bytes =
                serde_json::to_vec(&snapshot).map_err(|e| io_error("serialize snapshot", e))?;
            write_atomic(&entry.dir.join("snapshot.json"), &bytes)?;
            let _ = std::fs::remove_file(entry.dir.join("progress.json"));

            entry.turns.insert(record.turn.0);
            entry.snapshot = Some(snapshot);
            // Wall-clock stamp: list ordering only needs rough recency.
            entry.updated_at_unix_ms = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_millis() as i64)
                .unwrap_or(0);
            Ok(())
        })
    }

    fn replay_turns<'a>(
        &'a self,
        id: &'a SessionId,
        after: TurnId,
    ) -> BoxFuture<'a, Result<BoxStream<'static, Result<TurnRecord, StoreError>>, StoreError>> {
        Box::pin(async move {
            let index = self.index.lock().await;
            let Some(entry) = index.sessions.get(&sanitize(&id.0)) else {
                return Ok(Box::pin(futures_util::stream::empty())
                    as BoxStream<'static, Result<TurnRecord, StoreError>>);
            };
            let path = entry.dir.join("turns.jsonl");
            drop(index);

            let (tx, rx) = tokio::sync::mpsc::channel::<Result<TurnRecord, StoreError>>(32);
            tokio::task::spawn_blocking(move || {
                let Ok(content) = std::fs::read_to_string(&path) else {
                    return;
                };
                for line in content.lines() {
                    let Ok(record) = serde_json::from_str::<TurnRecord>(line) else {
                        continue;
                    };
                    if record.turn <= after {
                        continue;
                    }
                    if tx.blocking_send(Ok(record)).is_err() {
                        return;
                    }
                }
            });
            Ok(Box::pin(ReceiverStream::new(rx)))
        })
    }

    fn apply_retention<'a>(
        &'a self,
        policy: &'a RetentionPolicy,
    ) -> BoxFuture<'a, Result<RetainReport, StoreError>> {
        Box::pin(async move {
            let mut report = RetainReport::default();
            let mut index = self.index.lock().await;

            if let Some(keep_sessions) = policy.keep_sessions {
                let mut ordered: Vec<(i64, String)> = index
                    .sessions
                    .iter()
                    .map(|(key, entry)| (entry.updated_at_unix_ms, key.clone()))
                    .collect();
                ordered.sort();
                let remove_count = ordered.len().saturating_sub(keep_sessions as usize);
                for (_, key) in ordered.into_iter().take(remove_count) {
                    if let Some(entry) = index.sessions.remove(&key) {
                        let _ = std::fs::remove_dir_all(&entry.dir);
                        report.sessions_removed += 1;
                    }
                }
            }

            if let Some(keep_turns) = policy.keep_last_turns_per_session {
                for entry in index.sessions.values_mut() {
                    let cutoff = entry
                        .turns
                        .iter()
                        .rev()
                        .take(keep_turns as usize)
                        .min()
                        .copied();
                    let Some(cutoff) = cutoff else {
                        continue;
                    };
                    let removed: Vec<u64> = entry
                        .turns
                        .iter()
                        .filter(|&&turn| turn < cutoff)
                        .copied()
                        .collect();
                    if removed.is_empty() {
                        continue;
                    }
                    // Rewrite the ledger keeping turns >= cutoff; the
                    // snapshot restarts from the retained history.
                    let ledger = entry.dir.join("turns.jsonl");
                    if let Ok(content) = std::fs::read_to_string(&ledger) {
                        let mut kept = String::new();
                        let mut messages = Vec::new();
                        let mut turns_completed = 0;
                        for line in content.lines() {
                            let Ok(record) = serde_json::from_str::<TurnRecord>(line) else {
                                continue;
                            };
                            if record.turn.0 < cutoff {
                                continue;
                            }
                            turns_completed = turns_completed.max(record.turn.0);
                            messages.extend(record.new_messages.iter().cloned());
                            kept.push_str(line);
                            kept.push('\n');
                        }
                        write_atomic(&ledger, kept.as_bytes())?;
                        let snapshot = SessionSnapshot {
                            messages,
                            turns_completed,
                        };
                        let bytes = serde_json::to_vec(&snapshot)
                            .map_err(|e| io_error("serialize snapshot", e))?;
                        write_atomic(&entry.dir.join("snapshot.json"), &bytes)?;
                        entry.turns.retain(|&turn| turn >= cutoff);
                        entry.snapshot = Some(snapshot);
                        report.turns_removed += removed.len() as u64;
                    }
                }
            }

            Ok(report)
        })
    }

    fn close(&self) -> BoxFuture<'_, Result<(), StoreError>> {
        Box::pin(async { Ok(()) })
    }
}

pub struct FileBlobStore {
    root: PathBuf,
}

impl FileBlobStore {
    pub fn new(root: impl Into<PathBuf>) -> Self {
        Self { root: root.into() }
    }

    fn blob_path(&self, session: &SessionId, key: &str) -> PathBuf {
        self.root
            .join("sessions")
            .join(sanitize(&session.0))
            .join("blobs")
            .join(sanitize(key))
    }
}

impl BlobStore for FileBlobStore {
    fn put<'a>(
        &'a self,
        session: &'a SessionId,
        key: &'a str,
        bytes: Vec<u8>,
    ) -> BoxFuture<'a, Result<BlobRef, StoreError>> {
        Box::pin(async move {
            let path = self.blob_path(session, key);
            if let Some(parent) = path.parent() {
                std::fs::create_dir_all(parent).map_err(|e| io_error("create blobs dir", e))?;
            }
            write_atomic(&path, &bytes)?;
            Ok(BlobRef {
                session: session.clone(),
                key: key.to_string(),
            })
        })
    }

    fn get<'a>(
        &'a self,
        session: &'a SessionId,
        key: &'a str,
    ) -> BoxFuture<'a, Result<Option<Vec<u8>>, StoreError>> {
        Box::pin(async move {
            let path = self.blob_path(session, key);
            match std::fs::read(&path) {
                Ok(bytes) => Ok(Some(bytes)),
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
                Err(e) => Err(io_error("read blob", e)),
            }
        })
    }
}
