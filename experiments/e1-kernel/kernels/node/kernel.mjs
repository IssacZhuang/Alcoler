// E1 kernel spike (Node). Contract v1.1: JSONL commands on stdin,
// JSONL events (epoch-ms ts) on stdout. See experiments/e1-kernel/README.md.

// Runtime-agnostic SQLite handle: Node's built-in node:sqlite, or Bun's
// bun:sqlite which exposes the same exec/prepare/run/close surface.
let DatabaseSync;
try {
  ({ DatabaseSync } = await import('node:sqlite'));
} catch {
  ({ Database: DatabaseSync } = await import('bun:sqlite'));
}
import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import readline from 'node:readline';

const now = () => Date.now();
const r1 = (x) => Math.round(x * 10) / 10;
const emit = (ev) => process.stdout.write(JSON.stringify(ev) + '\n');

const dbPath = process.env.E1_DB || path.join(os.tmpdir(), `e1-node-${process.pid}.sqlite`);
const db = new DatabaseSync(dbPath);
db.exec('PRAGMA journal_mode=WAL;');
db.exec('PRAGMA synchronous=NORMAL;');
db.exec('CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, turn INTEGER, state TEXT, updated_at INTEGER)');
const upsert = db.prepare(
  'INSERT INTO sessions(id, turn, state, updated_at) VALUES(?, ?, ?, ?) ' +
  'ON CONFLICT(id) DO UPDATE SET turn=excluded.turn, state=excluded.state, updated_at=excluded.updated_at'
);

const TOOLS = {
  'fs.read': { readonly: true, destructive: false, needsApproval: false, timeoutMs: 5000, maxOutputBytes: 65536 },
  'exec': { readonly: false, destructive: true, needsApproval: true, timeoutMs: 30000, maxOutputBytes: 8192 },
};
const approve = async () => true; // permission stub: auto-approve destructive tools

const sessions = new Map();
let shuttingDown = false;

function killChildren() {
  for (const s of sessions.values()) {
    if (s.active) { try { s.active.child.kill(); } catch {} }
  }
}

function shutdown() {
  if (shuttingDown) return;
  shuttingDown = true;
  killChildren();
  try { db.close(); } catch {}
  process.exit(0);
}

async function readSse(url, onEvent) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`provider http ${res.status}`);
  const reader = res.body.getReader();
  const dec = new TextDecoder();
  let buf = '';
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buf += dec.decode(value, { stream: true });
    let i;
    while ((i = buf.indexOf('\n\n')) >= 0) {
      const frame = buf.slice(0, i);
      buf = buf.slice(i + 2);
      if (frame.startsWith('data: ')) onEvent(JSON.parse(frame.slice(6)));
    }
  }
}

function truncate(sess) {
  const budgetBytes = sess.profile.budgetTokens * 4;
  let total = 0;
  for (const e of sess.ctx) total += e.bytes;
  let removed = false;
  while (total > budgetBytes) {
    let idx = sess.ctx.findIndex((e) => e.kind === 'tool_result' && e.bytes > 0);
    if (idx < 0) idx = sess.ctx.findIndex((e) => e.bytes > 0);
    if (idx < 0) break;
    total -= sess.ctx[idx].bytes;
    sess.ctx[idx].bytes = 0;
    removed = true;
  }
  if (removed) emit({ type: 'compacted', session: sess.id, kept_tokens: Math.floor(total / 4), ts: now() });
  return Math.floor(total / 4);
}

async function runSession(sess) {
  const p = sess.profile;
  emit({ type: 'session_started', session: sess.id, ts: now() });
  for (let turn = 1; turn <= p.turns; turn++) {
    emit({ type: 'turn_started', session: sess.id, turn, ts: now() });
    let textBytes = 0;
    const toolCalls = [];
    const url = `${p.provider}/sse?session=${encodeURIComponent(sess.id)}&turn=${turn}&n=${p.eventsPerTurn}&interval=${p.intervalMs}`;
    await readSse(url, (ev) => {
      if (ev.type === 'text_delta') {
        const b = Buffer.byteLength(ev.text);
        textBytes += b;
        emit({ type: 'model_delta', session: sess.id, turn, seq: ev.seq, bytes: b, ts: now() });
      } else if (ev.type === 'tool_call') {
        toolCalls.push(ev);
      }
    });

    for (const tc of toolCalls) {
      const meta = TOOLS[tc.tool];
      if (meta.needsApproval && !(await approve())) {
        emit({ type: 'tool_result', session: sess.id, turn, tool: tc.tool, seq: tc.seq, status: 'denied', bytes: 0, durationMs: 0, ts: now() });
        continue;
      }
      if (tc.tool === 'fs.read') {
        emit({ type: 'tool_call', session: sess.id, turn, tool: 'fs.read', seq: tc.seq, ts: now() });
        const t0 = performance.now();
        const buf = await readFile(tc.args.path);
        const bytes = Math.min(buf.length, meta.maxOutputBytes);
        emit({ type: 'tool_result', session: sess.id, turn, tool: 'fs.read', seq: tc.seq, status: 'ok', bytes, durationMs: r1(performance.now() - t0), ts: now() });
        sess.ctx.push({ kind: 'tool_result', bytes });
      } else if (tc.tool === 'exec') {
        emit({ type: 'tool_call', session: sess.id, turn, tool: 'exec', seq: tc.seq, ts: now() });
        const t0 = performance.now();
        const child = spawn(tc.args.argv[0], tc.args.argv.slice(1), { stdio: 'ignore' });
        sess.active = { turn, child, cancelled: false };
        await new Promise((resolve) => child.on('exit', resolve));
        const cancelled = sess.active.cancelled;
        sess.active = null;
        emit({ type: 'tool_result', session: sess.id, turn, tool: 'exec', seq: tc.seq, status: cancelled ? 'cancelled' : 'ok', bytes: 24, durationMs: r1(performance.now() - t0), ts: now() });
        sess.ctx.push({ kind: 'tool_result', bytes: 24 });
      }
    }

    if (textBytes > 0) sess.ctx.push({ kind: 'text', bytes: textBytes });
    sess.tokens = truncate(sess);
    const state = JSON.stringify({ turn, tokens: sess.tokens, entries: sess.ctx });
    upsert.run(sess.id, turn, state, now());
    emit({ type: 'turn_done', session: sess.id, turn, ts: now() });
  }
  emit({ type: 'session_done', session: sess.id, ts: now() });
}

emit({ type: 'ready', ts: now() });

const rl = readline.createInterface({ input: process.stdin });
rl.on('line', (line) => {
  line = line.trim();
  if (!line) return;
  let cmd;
  try { cmd = JSON.parse(line); } catch { return; }
  if (cmd.cmd === 'start_session') {
    const sess = {
      id: cmd.id,
      profile: cmd.profile,
      ctx: [],
      tokens: 0,
      active: null,
    };
    sessions.set(sess.id, sess);
    runSession(sess).catch((e) => emit({ type: 'error', session: sess.id, code: String(e && e.message || e), fatal: true, ts: now() }));
  } else if (cmd.cmd === 'cancel') {
    const s = sessions.get(cmd.session);
    if (s && s.active && s.active.turn === cmd.turn && !s.active.cancelled) {
      s.active.cancelled = true;
      try { s.active.child.kill(); } catch {}
    }
  } else if (cmd.cmd === 'gc') {
    let forced = false;
    if (global.gc) { global.gc(); forced = true; }
    else if (typeof Bun !== 'undefined' && typeof Bun.gc === 'function') { Bun.gc(true); forced = true; }
    emit({ type: 'gc_done', forced, ts: now() });
  } else if (cmd.cmd === 'shutdown') {
    shutdown();
  }
});
rl.on('close', () => shutdown());
process.on('exit', killChildren);
