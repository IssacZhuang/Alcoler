// E1 measurement runner: interleaved scenario runs across language kernels,
// whole-process-tree memory sampling (PowerShell sampler), provider-log joins
// for TTFT/stream lag, per-run CSV rows and a Markdown report.
//
// Usage:
//   node harness/runner.mjs --profile medium --langs node,go,rust,dotnet \
//        [--reps 1] [--sessions 1,4,16] [--idle-sec 5] [--keep-db] [--cancel-every 20]
// Profiles: smoke | medium | standard (see README).
import { spawn } from 'node:child_process';
import { appendFileSync, existsSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import readline from 'node:readline';
import { fileURLToPath } from 'node:url';
import { runKernelScenario } from './driver.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..');

const KERNELS = {
  node: { cmd: 'node', args: ['--expose-gc', path.join(ROOT, 'kernels/node/kernel.mjs')] },
  bun: { cmd: 'bun', args: [path.join(ROOT, 'kernels/node/kernel.mjs')] },
  go: { cmd: path.join(ROOT, 'kernels/go/e1kernel.exe') },
  rust: { cmd: path.join(ROOT, 'kernels/rust/target/release/e1kernel.exe') },
  dotnet: { cmd: 'dotnet', args: [path.join(ROOT, 'kernels/dotnet/bin/Release/net8.0/e1kernel.dll')] },
  rustfeed: { cmd: path.join(ROOT, 'kernels/rust-feed/target/release/e1feed.exe'), feed: true },
  wasm: {
    cmd: path.join(ROOT, 'bin/wasmtime.exe'),
    feed: true, wasm: true,
    module: path.join(ROOT, 'kernels/rust-feed/target/wasm32-wasip1/release/e1feed.wasm'),
  },
};
const INTERPRETED = new Set(['node', 'dotnet', 'bun']);

const PRESETS = {
  smoke: { turns: 20, eventsPerTurn: 50, intervalMs: 5 },
  medium: { turns: 60, eventsPerTurn: 150, intervalMs: 5 },
  standard: { turns: 200, eventsPerTurn: 500, intervalMs: 10 },
};

function parseCli() {
  const o = {
    profile: 'medium', langs: ['node', 'go', 'rust', 'dotnet'], reps: 1,
    sessions: null, idleSec: null, keepDb: false, cancelEvery: 20, out: null,
  };
  const argv = process.argv.slice(2);
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--profile') o.profile = argv[++i];
    else if (a === '--langs') o.langs = argv[++i].split(',');
    else if (a === '--reps') o.reps = +argv[++i];
    else if (a === '--sessions') o.sessions = argv[++i].split(',').map(Number);
    else if (a === '--idle-sec') o.idleSec = +argv[++i];
    else if (a === '--keep-db') o.keepDb = true;
    else if (a === '--cancel-every') o.cancelEvery = +argv[++i];
    else if (a === '--out') o.out = argv[++i];
    else if (a === '--turns') o.turns = +argv[++i];
    else if (a === '--events') o.eventsPerTurn = +argv[++i];
    else if (a === '--interval') o.intervalMs = +argv[++i];
  }
  return o;
}

const opts = parseCli();
const preset = { ...PRESETS[opts.profile] };
if (opts.turns) preset.turns = opts.turns;
if (opts.eventsPerTurn) preset.eventsPerTurn = opts.eventsPerTurn;
if (opts.intervalMs) preset.intervalMs = opts.intervalMs;
const sessionsList = opts.sessions || (opts.profile === 'smoke' ? [1] : [1, 4, 16]);
const idleSec = opts.idleSec ?? (opts.profile === 'standard' ? 60 : 5);

const outDir = opts.out || path.join(ROOT, 'results', `${opts.profile}-${new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19)}`);
mkdirSync(path.join(outDir, 'tmp'), { recursive: true });
mkdirSync(path.join(outDir, 'events'), { recursive: true });

const SLEEPER = path.join(ROOT, 'bin/e1-sleeper.exe');
if (!existsSync(SLEEPER)) { console.error(`missing ${SLEEPER}; build it first (see README)`); process.exit(1); }
for (const l of opts.langs) {
  const k = KERNELS[l];
  if (!k) { console.error(`unknown lang ${l}`); process.exit(1); }
  const missing = !INTERPRETED.has(l) && (k.wasm ? (!existsSync(k.cmd) || !existsSync(k.module)) : !existsSync(k.cmd));
  if (missing) {
    console.error(`missing kernel binary for ${l}: ${k.wasm ? k.module : k.cmd}; build first (see README)`);
    process.exit(1);
  }
}

// ---------- shared services ----------
const fixture = path.join(outDir, 'tmp', 'fixture4096.bin');
writeFileSync(fixture, Buffer.alloc(4096, 0x61));

const providerLog = path.join(outDir, 'provider.log');
const port = 8960 + Math.floor(Math.random() * 200);
const provider = spawn('node', [path.join(ROOT, 'provider/provider.mjs'),
  '--port', String(port), '--log', providerLog, '--fixture', fixture, '--sleeper', SLEEPER],
  { stdio: ['ignore', 'pipe', 'inherit'] });
await new Promise((res, rej) => {
  provider.stdout.once('data', (d) => String(d).includes('listening') ? res() : rej(new Error(String(d))));
  setTimeout(() => rej(new Error('provider start timeout')), 15000);
});

const samplerCsv = path.join(outDir, 'sampler.csv');
const sampler = spawn('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass',
  '-File', path.join(__dirname, 'sampler.ps1'), '-IntervalMs', '1000'],
  { stdio: ['ignore', 'pipe', 'inherit'] });
let samplerRows = [];
let samplerCsvStream = null;
// Append when re-running into an existing results dir, so earlier runs'
// snapshots survive for offline aggregation (a duplicate 'ts,...' header row
// is harmless: the parser only accepts rows whose first field is numeric).
let samplerCsvNew = true;
{
  const fs = await import('node:fs');
  samplerCsvNew = !existsSync(samplerCsv);
  samplerCsvStream = fs.createWriteStream(samplerCsv, { flags: samplerCsvNew ? 'w' : 'a' });
}
const samplerReady = new Promise((res) => {
  const rlS = readline.createInterface({ input: sampler.stdout });
  let first = true;
  rlS.on('line', (l) => {
    if (first && l.startsWith('ts,')) {
      first = false;
      if (samplerCsvNew) samplerCsvStream.write(l + '\n');
      res(); return;
    }
    if (first) return;
    samplerCsvStream.write(l + '\n');
    const p = l.split(',');
    if (p.length === 6) {
      samplerRows.push({ ts: +p[0], pid: +p[1], ppid: +p[2], name: p[3], private: +p[4], ws: +p[5] });
    }
  });
});
await Promise.race([samplerReady, new Promise((_, rej) => setTimeout(() => rej(new Error('sampler start timeout')), 20000))]);

// ---------- memory helpers ----------
// Checkpoint lookups walk snapshots backward from the checkpoint time (<=5s):
// a single snapshot may miss a live pid (Get-Process/CIM enumeration race),
// and later snapshots may miss the kernel, which exits right after shutdown.
function treeAt(rootPid, ts) {
  const stamps = [...new Set(samplerRows.map((r) => r.ts))].sort((a, b) => a - b);
  for (let i = stamps.length - 1; i >= 0; i--) {
    if (stamps[i] > ts) continue;
    if (ts - stamps[i] > 5000) break;
    const s = treeSum(samplerRows.filter((r) => r.ts === stamps[i]), rootPid);
    if (s) return s;
  }
  return null;
}
function treeSum(rows, rootPid) {
  const byPid = new Map();
  const kids = new Map();
  for (const r of rows) {
    byPid.set(r.pid, r);
    if (!kids.has(r.ppid)) kids.set(r.ppid, []);
    kids.get(r.ppid).push(r.pid);
  }
  if (!byPid.has(rootPid)) return null;
  const seen = new Set();
  const stack = [rootPid];
  let priv = 0, ws = 0, n = 0;
  while (stack.length) {
    const pid = stack.pop();
    if (seen.has(pid)) continue;
    seen.add(pid);
    const r = byPid.get(pid);
    if (!r) continue;
    priv += r.private; ws += r.ws; n++;
    for (const k of kids.get(pid) || []) stack.push(k);
  }
  return { priv, ws, n };
}
function peakTree(rootPid, fromTs, toTs) {
  const rows = samplerRows.filter((r) => r.ts >= fromTs && r.ts <= toTs);
  const byTs = new Map();
  for (const r of rows) {
    if (!byTs.has(r.ts)) byTs.set(r.ts, []);
    byTs.get(r.ts).push(r);
  }
  let peak = null;
  for (const group of byTs.values()) {
    const s = treeSum(group, rootPid);
    if (s && (peak === null || s.priv > peak.priv)) peak = s;
  }
  return peak;
}
const mb = (b) => (b == null ? null : Math.round(b / 10485.76) / 100);

// ---------- provider log join ----------
function providerWindow(marker) {
  if (!existsSync(providerLog)) return [];
  const lines = readFileSync(providerLog, 'utf8').split('\n').filter(Boolean);
  const start = lines.findIndex((l) => l.includes(`"marker":"${marker}"`));
  if (start < 0) return [];
  const out = [];
  for (let i = start + 1; i < lines.length; i++) {
    try {
      const v = JSON.parse(lines[i]);
      if (v.kind === 'mark') break;
      if (v.kind === 'stream') out.push(v);
    } catch { /* ignore */ }
  }
  return out;
}

// ---------- stats ----------
const med = (a) => (a && a.length ? a.slice().sort((x, y) => x - y)[Math.floor(a.length / 2)] : null);
const p95 = (a) => (a && a.length ? a.slice().sort((x, y) => x - y)[Math.min(a.length - 1, Math.floor(a.length * 0.95))] : null);
const r1 = (x) => (x == null ? null : Math.round(x * 10) / 10);

// ---------- run matrix (interleaved) ----------
const runs = [];
for (let rep = 0; rep < opts.reps; rep++) {
  for (const s of sessionsList) {
    for (const lang of opts.langs) {
      runs.push({ rep, lang, sessions: s });
    }
  }
}
console.log(`profile=${opts.profile} turns=${preset.turns} events=${preset.eventsPerTurn} interval=${preset.intervalMs}ms idle=${idleSec}s`);
console.log(`runs: ${runs.length} -> ${outDir}`);

const rows = [];
for (const run of runs) {
  const runId = `${run.lang}-s${run.sessions}-r${run.rep}`;
  const kcfg = KERNELS[run.lang];
  const isFeed = !!kcfg.feed;
  const hostDbPath = path.join(outDir, 'tmp', `db-${runId}.${isFeed ? 'jsonl' : 'sqlite'}`);
  const dbFiles = isFeed ? [hostDbPath] : ['', '-wal', '-shm'].map((s) => hostDbPath + s);
  for (const f of dbFiles) {
    try { rmSync(f, { force: true }); } catch { /* noop */ }
  }
  await fetch(`http://127.0.0.1:${port}/mark?name=${runId}`).catch(() => {});
  const minTs = Date.now() - 5000;
  samplerRows = samplerRows.filter((r) => r.ts >= minTs);

  const profile = {
    ...preset, budgetTokens: 12000, provider: `http://127.0.0.1:${port}`,
  };
  const sessionIds = Array.from({ length: run.sessions }, (_, i) => `s${i}`);
  const eventsFile = path.join(outDir, 'events', `${runId}.jsonl`);
  const eventsOut = [];
  console.log(`>> ${runId} starting`);

  // Feed variants (WASM p1 has no sockets): driver paces events over stdin.
  // The wasm variant preopens the run's tmp dir as /data inside the guest.
  let spawnCmd = kcfg.cmd;
  let spawnArgs = kcfg.args || [];
  let kernelDbPath = hostDbPath;
  let feedCfg = null;
  if (isFeed) {
    feedCfg = { fixture, sleeper: SLEEPER, skipTools: ['exec'], rewrites: [] };
    if (kcfg.wasm) {
      spawnArgs = ['run', '--dir', `${path.dirname(hostDbPath)}::/data`, kcfg.module];
      feedCfg.rewrites = [{ from: fixture, to: '/data/fixture4096.bin' }];
      kernelDbPath = `/data/${path.basename(hostDbPath)}`;
    }
  }

  let res;
  try {
    res = await runKernelScenario({
      cmd: spawnCmd, args: spawnArgs, dbPath: kernelDbPath,
      profile, sessionIds, cancelEveryExecTurns: opts.cancelEvery, idleSec, feed: feedCfg,
      onEvent: (v) => {
        if (v.type === 'model_delta') return;
        eventsOut.push(JSON.stringify(v));
        if (eventsOut.length % 500 === 0) {
          appendFileSync(eventsFile, eventsOut.splice(0).join('\n') + '\n');
        }
      },
    });
  } catch (e) {
    appendFileSync(eventsFile, eventsOut.join('\n') + '\n');
    rows.push({
      run: runId, lang: run.lang, sessions: run.sessions, rep: run.rep,
      error: e.message.split('\n')[0], exit_code: 'n/a',
    });
    console.log(`!! ${runId} FAILED: ${e.message.split('\n')[0]}`);
    continue;
  }
  if (eventsOut.length) appendFileSync(eventsFile, eventsOut.join('\n') + '\n');

  const t = res.timings;
  const idleMem = treeAt(res.pid, t.idleEnd);
  const settle1 = treeAt(res.pid, t.settle1);
  const settle2 = treeAt(res.pid, t.settle2);
  const peak = peakTree(res.pid, t.spawn, t.exit);

  // TTFT / lag: feed variants use driver-side send timestamps; TCP variants
  // join against the provider log.
  const ttft = res.feedTiming ? res.feedTiming.ttft : providerWindow(runId).map((s) => {
    const kf = res.deltaFirst.get(`${s.session}:${s.turn}`);
    return kf != null ? kf - s.first_ts : null;
  }).filter((x) => x != null);
  const lag = res.feedTiming ? res.feedTiming.lag : providerWindow(runId).map((s) => {
    const kl = res.deltaLast.get(`${s.session}:${s.turn}`);
    return (kl != null && s.count > 1) ? kl - s.last_ts : null;
  }).filter((x) => x != null);

  const row = {
    run: runId, lang: run.lang, sessions: run.sessions, rep: run.rep,
    error: '',
    exit_code: res.exitCode, orphans: res.orphans, errors: res.errors.length,
    ready_ms: r1(t.ready - t.spawn),
    idle_priv_mb: mb(idleMem?.priv), idle_ws_mb: mb(idleMem?.ws), idle_procs: idleMem?.n,
    settle1_priv_mb: mb(settle1?.priv),
    settle2_priv_mb: mb(settle2?.priv), settle2_ws_mb: mb(settle2?.ws),
    peak_priv_mb: mb(peak?.priv), peak_ws_mb: mb(peak?.ws), peak_procs: peak?.n,
    ttft_med_ms: r1(med(ttft)), ttft_p95_ms: r1(p95(ttft)),
    lag_med_ms: r1(med(lag)), lag_p95_ms: r1(p95(lag)),
    fs_med_ms: r1(med(res.toolDurations['fs.read'])),
    exec_med_ms: r1(med(res.toolDurations['exec-ok'])),
    cancelled_med_ms: r1(med(res.toolDurations['exec-cancelled'])),
    cancel_to_result_p95_ms: r1(p95(res.cancelToResult)),
    compacted: res.compactedCount,
    model_deltas: res.counts.model_delta || 0,
  };
  // Persist run metadata so aggregation can be recomputed from artifacts.
  writeFileSync(path.join(outDir, 'events', `${runId}.meta.json`), JSON.stringify({
    run: runId, pid: res.pid, timings: t, row,
    ttft_raw: ttft, lag_raw: lag,
    toolDurations: res.toolDurations, cancelToResult: res.cancelToResult,
    counts: res.counts, exitCode: res.exitCode, orphans: res.orphans,
    compacted: res.compactedCount,
  }));
  rows.push(row);
  console.log(`<< ${runId} exit=${res.exitCode} idle=${row.idle_priv_mb}MB peak=${row.peak_priv_mb}MB settle2=${row.settle2_priv_mb}MB ttft=${row.ttft_med_ms}ms ready=${row.ready_ms}ms`);

  if (!opts.keepDb) {
    for (const f of dbFiles) {
      try { rmSync(f, { force: true }); } catch { /* noop */ }
    }
  }
}

// ---------- final aggregation (single source of truth: aggregate.mjs) ----------
const { aggregateDir } = await import('./aggregate.mjs');
aggregateDir(outDir);

sampler.kill();
provider.kill();
console.log(`\ndone. report: ${path.join(outDir, 'report.md')}`);
process.exit(0);
