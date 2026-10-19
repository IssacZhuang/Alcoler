// E1 kernel spike (Rust). Contract v1.1: JSONL commands on stdin,
// JSONL events (epoch-ms ts) on stdout. See experiments/e1-kernel/README.md.
// std-only networking: one thread per session, raw TCP SSE client.
// SQLite via rusqlite "bundled" (sqlite3.c compiled with the local gcc).
use rusqlite::Connection;
use serde_json::{json, Value};
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::net::TcpStream;
use std::process::{Child, Command, Stdio};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

fn now() -> u128 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_millis()
}

fn emit(v: Value) {
    let stdout = std::io::stdout();
    let mut lock = stdout.lock();
    if writeln!(lock, "{}", v).is_ok() {
        let _ = lock.flush();
    }
}

#[derive(Clone, Copy)]
struct ToolMeta {
    readonly: bool,
    destructive: bool,
    approval: bool,
    max_out_bytes: usize,
}

fn tool_meta(name: &str) -> Option<ToolMeta> {
    match name {
        "fs.read" => Some(ToolMeta { readonly: true, destructive: false, approval: false, max_out_bytes: 65536 }),
        "exec" => Some(ToolMeta { readonly: false, destructive: true, approval: true, max_out_bytes: 8192 }),
        _ => None,
    }
}

fn approve(_tool: &str) -> bool {
    true // permission stub
}

#[derive(Clone)]
struct CtxEntry {
    kind: &'static str,
    bytes: usize,
}

struct ActiveExec {
    turn: u64,
    cancelled: bool,
    child: Arc<Mutex<Child>>,
}

#[derive(Clone)]
struct Profile {
    turns: u64,
    events_per_turn: u64,
    interval_ms: u64,
    budget_tokens: usize,
    provider: String,
}

struct Session {
    id: String,
    profile: Profile,
    ctx: Mutex<Vec<CtxEntry>>,
    active: Arc<Mutex<Option<ActiveExec>>>,
}

struct Shared {
    sessions: Mutex<HashMap<String, Arc<Session>>>,
    db: Mutex<Connection>,
}

fn parse_provider(url: &str) -> (String, u16) {
    let rest = url.trim_start_matches("http://").trim_start_matches("https://");
    let rest = rest.trim_end_matches('/');
    let (host, port) = match rest.rsplit_once(':') {
        Some((h, p)) => (h.to_string(), p.parse().unwrap_or(80)),
        None => (rest.to_string(), 80),
    };
    (host, port)
}

fn sse_stream(
    profile: &Profile,
    session: &str,
    turn: u64,
    on_event: &mut dyn FnMut(Value),
) -> std::io::Result<()> {
    let (host, port) = parse_provider(&profile.provider);
    let path = format!(
        "/sse?session={}&turn={}&n={}&interval={}",
        session, turn, profile.events_per_turn, profile.interval_ms
    );
    let mut stream = TcpStream::connect((host.as_str(), port))?;
    stream.set_nodelay(true).ok();
    let req = format!(
        "GET {} HTTP/1.1\r\nHost: {}:{}\r\nAccept: text/event-stream\r\nConnection: close\r\n\r\n",
        path, host, port
    );
    stream.write_all(req.as_bytes())?;
    let mut reader = BufReader::new(stream);
    let mut line = String::new();
    // Skip HTTP headers (close-delimited body follows).
    loop {
        line.clear();
        if reader.read_line(&mut line)? == 0 {
            return Err(std::io::Error::new(std::io::ErrorKind::UnexpectedEof, "eof in headers"));
        }
        let t = line.trim_end();
        if t.is_empty() {
            break;
        }
    }
    loop {
        line.clear();
        if reader.read_line(&mut line)? == 0 {
            break;
        }
        let t = line.trim_end();
        if let Some(payload) = t.strip_prefix("data: ") {
            let v: Value = match serde_json::from_str(payload) {
                Ok(v) => v,
                Err(_) => continue,
            };
            let is_end = v.get("type").and_then(|x| x.as_str()) == Some("turn_end");
            on_event(v);
            if is_end {
                return Ok(());
            }
        }
    }
    Err(std::io::Error::new(std::io::ErrorKind::UnexpectedEof, "stream ended without turn_end"))
}

fn truncate(sess: &Session) {
    let budget_bytes = sess.profile.budget_tokens * 4;
    let mut ctx = sess.ctx.lock().unwrap();
    let mut total: usize = ctx.iter().map(|e| e.bytes).sum();
    let mut removed = false;
    while total > budget_bytes {
        let mut idx: Option<usize> = ctx.iter().position(|e| e.kind == "tool_result" && e.bytes > 0);
        if idx.is_none() {
            idx = ctx.iter().position(|e| e.bytes > 0);
        }
        match idx {
            Some(i) => {
                total -= ctx[i].bytes;
                ctx[i].bytes = 0;
                removed = true;
            }
            None => break,
        }
    }
    if removed {
        emit(json!({"type": "compacted", "session": sess.id, "kept_tokens": total / 4, "ts": now()}));
    }
}

fn persist(shared: &Shared, sess: &Session, turn: u64) {
    let state = {
        let ctx = sess.ctx.lock().unwrap();
        let entries: Vec<Value> = ctx
            .iter()
            .map(|e| json!({"kind": e.kind, "bytes": e.bytes}))
            .collect();
        let total: usize = ctx.iter().map(|e| e.bytes).sum();
        json!({"turn": turn, "tokens": total / 4, "entries": entries}).to_string()
    };
    let db = shared.db.lock().unwrap();
    let _ = db.execute(
        "INSERT INTO sessions(id, turn, state, updated_at) VALUES(?1, ?2, ?3, ?4) \
         ON CONFLICT(id) DO UPDATE SET turn=excluded.turn, state=excluded.state, updated_at=excluded.updated_at",
        rusqlite::params![sess.id, turn as i64, state, now() as i64],
    );
}

fn run_session(shared: Arc<Shared>, sess: Arc<Session>) {
    emit(json!({"type": "session_started", "session": sess.id, "ts": now()}));
    for turn in 1..=sess.profile.turns {
        emit(json!({"type": "turn_started", "session": sess.id, "turn": turn, "ts": now()}));
        let mut text_bytes: usize = 0;
        let mut tool_calls: Vec<Value> = Vec::new();
        let res = sse_stream(&sess.profile, &sess.id, turn, &mut |ev| {
            let typ = ev.get("type").and_then(|v| v.as_str()).unwrap_or("");
            match typ {
                "text_delta" => {
                    let text = ev.get("text").and_then(|v| v.as_str()).unwrap_or("");
                    let b = text.len();
                    text_bytes += b;
                    let seq = ev.get("seq").and_then(|v| v.as_i64()).unwrap_or(0);
                    emit(json!({"type": "model_delta", "session": sess.id, "turn": turn, "seq": seq, "bytes": b, "ts": now()}));
                }
                "tool_call" => tool_calls.push(ev),
                _ => {}
            }
        });
        if let Err(e) = res {
            emit(json!({"type": "error", "session": sess.id, "code": e.to_string(), "fatal": true, "ts": now()}));
            return;
        }
        for tc in tool_calls {
            let tool = tc.get("tool").and_then(|v| v.as_str()).unwrap_or("");
            let seq = tc.get("seq").and_then(|v| v.as_i64()).unwrap_or(0);
            let meta = match tool_meta(tool) {
                Some(m) => m,
                None => continue,
            };
            if meta.approval && !approve(tool) {
                continue;
            }
            if tool == "fs.read" {
                emit(json!({"type": "tool_call", "session": sess.id, "turn": turn, "tool": "fs.read", "seq": seq, "ts": now()}));
                let t0 = Instant::now();
                let path = tc.pointer("/args/path").and_then(|v| v.as_str()).unwrap_or("");
                let status;
                let mut bytes = 0usize;
                match std::fs::read(path) {
                    Ok(data) => {
                        bytes = data.len().min(meta.max_out_bytes);
                        status = "ok";
                    }
                    Err(_) => {
                        status = "error";
                    }
                }
                let dur = t0.elapsed().as_secs_f64() * 1000.0;
                emit(json!({"type": "tool_result", "session": sess.id, "turn": turn, "tool": "fs.read", "seq": seq,
                    "status": status, "bytes": bytes, "durationMs": (dur * 10.0).round() / 10.0, "ts": now()}));
                sess.ctx.lock().unwrap().push(CtxEntry { kind: "tool_result", bytes });
            } else if tool == "exec" {
                emit(json!({"type": "tool_call", "session": sess.id, "turn": turn, "tool": "exec", "seq": seq, "ts": now()}));
                let t0 = Instant::now();
                let argv: Vec<String> = tc
                    .pointer("/args/argv")
                    .and_then(|v| v.as_array())
                    .map(|a| a.iter().filter_map(|x| x.as_str().map(String::from)).collect())
                    .unwrap_or_default();
                let child = Command::new(&argv[0])
                    .args(&argv[1..])
                    .stdout(Stdio::null())
                    .stderr(Stdio::null())
                    .spawn();
                let child = match child {
                    Ok(c) => Arc::new(Mutex::new(c)),
                    Err(e) => {
                        emit(json!({"type": "tool_result", "session": sess.id, "turn": turn, "tool": "exec", "seq": seq,
                            "status": "error", "bytes": 0, "durationMs": 0, "ts": now(), "detail": e.to_string()}));
                        continue;
                    }
                };
                {
                    let mut active = sess.active.lock().unwrap();
                    *active = Some(ActiveExec { turn, cancelled: false, child: child.clone() });
                }
                loop {
                    {
                        let mut active = sess.active.lock().unwrap();
                        if let Some(a) = active.as_mut() {
                            if a.turn == turn && a.cancelled {
                                let mut c = a.child.lock().unwrap();
                                let _ = c.kill();
                            }
                        }
                    }
                    let exited = {
                        let mut c = child.lock().unwrap();
                        match c.try_wait() {
                            Ok(Some(_)) => true,
                            Ok(None) => false,
                            Err(_) => true,
                        }
                    };
                    if exited {
                        break;
                    }
                    std::thread::sleep(Duration::from_millis(5));
                }
                {
                    let mut c = child.lock().unwrap();
                    let _ = c.wait();
                }
                let cancelled = {
                    let mut active = sess.active.lock().unwrap();
                    let c = active.as_ref().map(|a| a.turn == turn && a.cancelled).unwrap_or(false);
                    *active = None;
                    c
                };
                let dur = t0.elapsed().as_secs_f64() * 1000.0;
                let status = if cancelled { "cancelled" } else { "ok" };
                emit(json!({"type": "tool_result", "session": sess.id, "turn": turn, "tool": "exec", "seq": seq,
                    "status": status, "bytes": 24, "durationMs": (dur * 10.0).round() / 10.0, "ts": now()}));
                sess.ctx.lock().unwrap().push(CtxEntry { kind: "tool_result", bytes: 24 });
            }
        }
        if text_bytes > 0 {
            sess.ctx.lock().unwrap().push(CtxEntry { kind: "text", bytes: text_bytes });
        }
        truncate(&sess);
        persist(&shared, &sess, turn);
        emit(json!({"type": "turn_done", "session": sess.id, "turn": turn, "ts": now()}));
    }
    emit(json!({"type": "session_done", "session": sess.id, "ts": now()}));
}

fn kill_children(shared: &Shared) {
    let sessions = shared.sessions.lock().unwrap();
    for sess in sessions.values() {
        let mut active = sess.active.lock().unwrap();
        if let Some(a) = active.as_mut() {
            let mut c = a.child.lock().unwrap();
            let _ = c.kill();
        }
    }
}

fn main() {
    let db_path = std::env::var("E1_DB").unwrap_or_else(|_| format!("e1-rust-{}.sqlite", std::process::id()));
    let conn = Connection::open(&db_path).expect("open sqlite");
    conn.execute_batch("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;").ok();
    conn.execute_batch("CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, turn INTEGER, state TEXT, updated_at INTEGER)").ok();
    let shared = Arc::new(Shared {
        sessions: Mutex::new(HashMap::new()),
        db: Mutex::new(conn),
    });
    emit(json!({"type": "ready", "ts": now()}));

    let stdin = std::io::stdin();
    let mut reader = BufReader::new(stdin.lock());
    let mut line = String::new();
    loop {
        line.clear();
        match reader.read_line(&mut line) {
            Ok(0) => break,
            Ok(_) => {}
            Err(_) => break,
        }
        let t = line.trim();
        if t.is_empty() {
            continue;
        }
        let cmd: Value = match serde_json::from_str(t) {
            Ok(v) => v,
            Err(_) => continue,
        };
        match cmd.get("cmd").and_then(|v| v.as_str()).unwrap_or("") {
            "start_session" => {
                let id = cmd.get("id").and_then(|v| v.as_str()).unwrap_or("").to_string();
                let p = cmd.get("profile").cloned().unwrap_or(Value::Null);
                let profile = Profile {
                    turns: p.get("turns").and_then(|v| v.as_u64()).unwrap_or(0),
                    events_per_turn: p.get("eventsPerTurn").and_then(|v| v.as_u64()).unwrap_or(0),
                    interval_ms: p.get("intervalMs").and_then(|v| v.as_u64()).unwrap_or(10),
                    budget_tokens: p.get("budgetTokens").and_then(|v| v.as_u64()).unwrap_or(12000) as usize,
                    provider: p.get("provider").and_then(|v| v.as_str()).unwrap_or("").to_string(),
                };
                let sess = Arc::new(Session {
                    id: id.clone(),
                    profile,
                    ctx: Mutex::new(Vec::new()),
                    active: Arc::new(Mutex::new(None)),
                });
                shared.sessions.lock().unwrap().insert(id, sess.clone());
                let sh = shared.clone();
                std::thread::spawn(move || run_session(sh, sess));
            }
            "cancel" => {
                let id = cmd.get("session").and_then(|v| v.as_str()).unwrap_or("").to_string();
                let turn = cmd.get("turn").and_then(|v| v.as_u64()).unwrap_or(0);
                let sessions = shared.sessions.lock().unwrap();
                if let Some(sess) = sessions.get(&id) {
                    let mut active = sess.active.lock().unwrap();
                    if let Some(a) = active.as_mut() {
                        if a.turn == turn && !a.cancelled {
                            a.cancelled = true;
                            let mut c = a.child.lock().unwrap();
                            let _ = c.kill();
                        }
                    }
                }
            }
            "gc" => {
                // No forced trim available for the default Windows heap allocator;
                // report gc_done as unforced (memory already released on drop).
                emit(json!({"type": "gc_done", "forced": false, "ts": now()}));
            }
            "shutdown" => {
                kill_children(&shared);
                std::process::exit(0);
            }
            _ => {}
        }
    }
    kill_children(&shared);
    std::process::exit(0);
}
