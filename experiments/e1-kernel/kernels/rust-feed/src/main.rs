// E1 feed kernel (Rust). Same source compiled natively AND to wasm32-wasip1:
// the A/B pair isolates the WASM-sandbox runtime overhead.
//
// Contract v1.1 with feed transport: model events arrive as stdin commands
// {"cmd":"model_event","session","turn","event":{...}} paced by the driver
// (WASI preview 1 has no sockets). Tools: fs.read only (WASI p1 has no
// process spawning; exec is a host-extension concern outside this variant).
// Persistence: JSONL append (no C toolchain for bundled SQLite on wasm).
use serde_json::{json, Value};
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::time::{SystemTime, UNIX_EPOCH};

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

#[derive(Clone)]
struct CtxEntry {
    kind: &'static str,
    bytes: usize,
}

struct Session {
    id: String,
    turns: u64,
    budget_tokens: usize,
    ctx: Vec<CtxEntry>,
    pending_tools: Vec<(i64, String, String)>, // (seq, tool, path)
    cur_text_bytes: usize,
    turns_done: u64,
    last_turn_announced: u64,
    db_path: String,
}

impl Session {
    fn finalize_turn(&mut self, turn: u64) {
        for (seq, tool, path) in std::mem::take(&mut self.pending_tools) {
            if tool != "fs.read" {
                emit(json!({"type": "tool_call", "session": self.id, "turn": turn, "tool": tool, "seq": seq, "ts": now()}));
                emit(json!({"type": "tool_result", "session": self.id, "turn": turn, "tool": tool, "seq": seq,
                    "status": "unsupported", "bytes": 0, "durationMs": 0, "ts": now()}));
                continue;
            }
            emit(json!({"type": "tool_call", "session": self.id, "turn": turn, "tool": "fs.read", "seq": seq, "ts": now()}));
            let t0 = SystemTime::now();
            let (status, bytes) = match std::fs::read(&path) {
                Ok(d) => ("ok", d.len().min(65536)),
                Err(_) => ("error", 0),
            };
            let dur = t0.elapsed().map(|e| e.as_secs_f64() * 1000.0).unwrap_or(0.0);
            emit(json!({"type": "tool_result", "session": self.id, "turn": turn, "tool": "fs.read", "seq": seq,
                "status": status, "bytes": bytes, "durationMs": (dur * 10.0).round() / 10.0, "ts": now()}));
            self.ctx.push(CtxEntry { kind: "tool_result", bytes });
        }
        if self.cur_text_bytes > 0 {
            self.ctx.push(CtxEntry { kind: "text", bytes: self.cur_text_bytes });
            self.cur_text_bytes = 0;
        }
        self.truncate();
        self.persist(turn);
        emit(json!({"type": "turn_done", "session": self.id, "turn": turn, "ts": now()}));
        self.turns_done += 1;
        if self.turns_done >= self.turns {
            emit(json!({"type": "session_done", "session": self.id, "ts": now()}));
        }
    }

    fn truncate(&mut self) {
        let budget_bytes = self.budget_tokens * 4;
        let mut total: usize = self.ctx.iter().map(|e| e.bytes).sum();
        let mut removed = false;
        while total > budget_bytes {
            let mut idx = self.ctx.iter().position(|e| e.kind == "tool_result" && e.bytes > 0);
            if idx.is_none() {
                idx = self.ctx.iter().position(|e| e.bytes > 0);
            }
            match idx {
                Some(i) => {
                    total -= self.ctx[i].bytes;
                    self.ctx[i].bytes = 0;
                    removed = true;
                }
                None => break,
            }
        }
        if removed {
            emit(json!({"type": "compacted", "session": self.id, "kept_tokens": total / 4, "ts": now()}));
        }
    }

    fn persist(&self, turn: u64) {
        if self.db_path.is_empty() {
            return;
        }
        let entries: Vec<Value> = self.ctx.iter().map(|e| json!({"kind": e.kind, "bytes": e.bytes})).collect();
        let total: usize = self.ctx.iter().map(|e| e.bytes).sum();
        let line = json!({"session": self.id, "turn": turn, "tokens": total / 4, "entries": entries}).to_string();
        if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(&self.db_path) {
            let _ = writeln!(f, "{}", line);
        }
    }
}

fn main() {
    let db_path = std::env::var("E1_DB").unwrap_or_default();
    let mut sessions: HashMap<String, Session> = HashMap::new();

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
                let sess = Session {
                    id: id.clone(),
                    turns: p.get("turns").and_then(|v| v.as_u64()).unwrap_or(0),
                    budget_tokens: p.get("budgetTokens").and_then(|v| v.as_u64()).unwrap_or(12000) as usize,
                    ctx: Vec::new(),
                    pending_tools: Vec::new(),
                    cur_text_bytes: 0,
                    turns_done: 0,
                    last_turn_announced: 0,
                    db_path: db_path.clone(),
                };
                sessions.insert(id.clone(), sess);
                emit(json!({"type": "session_started", "session": id, "ts": now()}));
            }
            "model_event" => {
                let id = cmd.get("session").and_then(|v| v.as_str()).unwrap_or("").to_string();
                let turn = cmd.get("turn").and_then(|v| v.as_u64()).unwrap_or(0);
                let ev = match cmd.get("event") {
                    Some(e) => e,
                    None => continue,
                };
                let Some(sess) = sessions.get_mut(&id) else { continue };
                if turn != sess.last_turn_announced {
                    sess.last_turn_announced = turn;
                    emit(json!({"type": "turn_started", "session": id, "turn": turn, "ts": now()}));
                }
                match ev.get("type").and_then(|v| v.as_str()).unwrap_or("") {
                    "text_delta" => {
                        let text = ev.get("text").and_then(|v| v.as_str()).unwrap_or("");
                        let b = text.len();
                        sess.cur_text_bytes += b;
                        let seq = ev.get("seq").and_then(|v| v.as_i64()).unwrap_or(0);
                        emit(json!({"type": "model_delta", "session": id, "turn": turn, "seq": seq, "bytes": b, "ts": now()}));
                    }
                    "tool_call" => {
                        let seq = ev.get("seq").and_then(|v| v.as_i64()).unwrap_or(0);
                        let tool = ev.get("tool").and_then(|v| v.as_str()).unwrap_or("").to_string();
                        let path = ev.pointer("/args/path").and_then(|v| v.as_str()).unwrap_or("").to_string();
                        sess.pending_tools.push((seq, tool, path));
                    }
                    "turn_end" => {
                        sess.finalize_turn(turn);
                    }
                    _ => {}
                }
            }
            "cancel" => { /* feed variant has no exec; cancel is a no-op */ }
            "gc" => {
                emit(json!({"type": "gc_done", "forced": false, "ts": now()}));
            }
            "shutdown" => {
                std::process::exit(0);
            }
            _ => {}
        }
    }
    std::process::exit(0);
}
