// E1 conformance check: verifies one kernel against contract v1.1 using a
// small deterministic scenario. Usage:
//   node harness/conform.mjs node|go|rust|dotnet|all
import { spawn } from 'node:child_process';
import { mkdtempSync, writeFileSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { runKernelScenario, countSleepers } from './driver.mjs';

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
    cmd: path.join(ROOT, 'bin/wasmtime.exe'), feed: true, wasm: true,
    module: path.join(ROOT, 'kernels/rust-feed/target/wasm32-wasip1/release/e1feed.wasm'),
  },
};

const PROFILE = {
  turns: 20, eventsPerTurn: 50, intervalMs: 5, budgetTokens: 12000,
};
const SLEEPER = path.join(ROOT, 'bin/e1-sleeper.exe');

async function startProvider(port, logPath, fixture) {
  const child = spawn('node', [path.join(ROOT, 'provider/provider.mjs'),
    '--port', String(port), '--log', logPath, '--fixture', fixture, '--sleeper', SLEEPER],
    { stdio: ['ignore', 'pipe', 'inherit'] });
  await new Promise((res, rej) => {
    child.stdout.once('data', (d) => String(d).includes('listening') ? res() : rej(new Error('provider: ' + d)));
    setTimeout(() => rej(new Error('provider start timeout')), 10000);
  });
  return child;
}

function assert(cond, msg) {
  if (!cond) throw new Error('ASSERT FAIL: ' + msg);
}

async function conformOne(lang, providerPort) {
  const cfg = KERNELS[lang];
  if (cfg.cmd !== 'node' && cfg.cmd !== 'bun' && cfg.cmd !== 'dotnet' && !existsSync(cfg.wasm ? cfg.module : cfg.cmd)) {
    throw new Error(`kernel binary missing: ${cfg.wasm ? cfg.module : cfg.cmd} (build first)`);
  }
  const dir = mkdtempSync(path.join(tmpdir(), `e1-conform-${lang}-`));
  const dbPath = cfg.feed ? path.join(dir, 'db.jsonl') : path.join(dir, 'db.sqlite');
  const fixture = path.join(dir, 'fixture4096.bin');
  writeFileSync(fixture, Buffer.alloc(4096, 0x61));

  let spawnCmd = cfg.cmd;
  let spawnArgs = cfg.args || [];
  let kernelDbPath = dbPath;
  let feed = null;
  if (cfg.feed) {
    feed = { fixture, sleeper: SLEEPER, skipTools: ['exec'], rewrites: [] };
    if (cfg.wasm) {
      spawnArgs = ['run', '--dir', `${dir}::/data`, cfg.module];
      feed.rewrites = [{ from: fixture, to: '/data/fixture4096.bin' }];
      kernelDbPath = '/data/db.jsonl';
    }
  }

  const profile = { ...PROFILE, provider: `http://127.0.0.1:${providerPort}` };
  const events = [];
  const res = await runKernelScenario({
    cmd: spawnCmd, args: spawnArgs, dbPath: kernelDbPath,
    profile, sessionIds: ['s0'], cancelEveryExecTurns: 20, idleSec: 1, feed,
    onEvent: (v) => { if (v.type !== 'model_delta') events.push(v); },
  });

  const errs = [];
  const check = (c, m) => { if (!c) errs.push(m); };

  check(res.exitCode === 0, `exitCode=${res.exitCode}`);
  check(res.orphans === 0, `orphans=${res.orphans}`);
  check(res.errors.length === 0, `error events: ${JSON.stringify(res.errors.slice(0, 3))}`);
  check(events[0]?.type === 'ready', 'first event is ready');
  check(res.counts.session_started === 1, 'one session_started');
  check(res.counts.session_done === 1, 'one session_done');
  check(res.counts.turn_started === PROFILE.turns, `turn_started=${res.counts.turn_started}`);
  check(res.counts.turn_done === PROFILE.turns, `turn_done=${res.counts.turn_done}`);
  // model deltas: eventsPerTurn minus tool frames (1 fs, +1 exec on even turns)
  const expectedDeltas = Array.from({ length: PROFILE.turns }, (_, i) =>
    PROFILE.eventsPerTurn - 1 - (i % 2 === 0 ? 1 : 0)).reduce((a, b) => a + b, 0);
  check(res.counts.model_delta === expectedDeltas,
    `model_delta=${res.counts.model_delta} expected=${expectedDeltas}`);
  const execTurns = Math.floor(PROFILE.turns / 2);
  const expectedToolCalls = cfg.feed ? PROFILE.turns : PROFILE.turns + execTurns;
  check(res.counts.tool_call === expectedToolCalls,
    `tool_call=${res.counts.tool_call} expected=${expectedToolCalls}`);
  check(res.counts.tool_result === res.counts.tool_call,
    `tool_result=${res.counts.tool_result}`);
  if (!cfg.feed) {
    // turn 20 is an exec turn and a cancel turn -> exactly one cancelled exec
    const cancelled = events.filter((e) => e.type === 'tool_result' && e.status === 'cancelled');
    check(cancelled.length === 1, `cancelled results=${cancelled.length}`);
    check(cancelled[0]?.tool === 'exec' && cancelled[0]?.turn === 20,
      `cancelled at wrong place: ${JSON.stringify(cancelled[0])}`);
  }
  check(res.compactedCount >= 1, `compacted=${res.compactedCount} (budget must be exceeded)`);
  check(res.counts.gc_done === 1, `gc_done=${res.counts.gc_done}`);

  const med = (a) => a.length ? a.slice().sort((x, y) => x - y)[Math.floor(a.length / 2)] : null;
  let okExec = null;
  if (!cfg.feed) {
    const cancelledDur = res.toolDurations['exec-cancelled'];
    check(cancelledDur.length === 1 && cancelledDur[0] < 480,
      `cancelled exec duration should be ~100-200ms, got ${JSON.stringify(cancelledDur)}`);
    okExec = med(res.toolDurations['exec-ok']) || 0;
    check(okExec > 400, `exec-ok median should be ~500ms, got ${okExec}`);
  }
  check((med(res.toolDurations['fs.read']) ?? 0) < 100, 'fs.read median < 100ms');

  rmSync(dir, { recursive: true, force: true });
  return { lang, ok: errs.length === 0, errs, execMed: okExec, fsMed: med(res.toolDurations['fs.read']) };
}

const which = process.argv[2] || 'all';
const langs = which === 'all' ? Object.keys(KERNELS) : [which];
const port = 8949 + Math.floor(Math.random() * 100);
const tmp = mkdtempSync(path.join(tmpdir(), 'e1-conform-'));
const prov = await startProvider(port, path.join(tmp, 'prov.log'), path.join(tmp, 'fix.bin'));
writeFileSync(path.join(tmp, 'fix.bin'), Buffer.alloc(4096, 0x61));

let fail = 0;
for (const lang of langs) {
  try {
    const r = await conformOne(lang, port);
    if (r.ok) console.log(`PASS ${lang}  (exec med ${r.execMed ?? 'n/a'}ms, fs.read med ${r.fsMed}ms)`);
    else { fail++; console.log(`FAIL ${lang}\n  - ${r.errs.join('\n  - ')}`); }
  } catch (e) {
    fail++;
    console.log(`ERROR ${lang}: ${e.message}`);
  }
}
prov.kill();
rmSync(tmp, { recursive: true, force: true });
if (await new Promise((r) => setTimeout(() => r(countSleepers() > 0), 1500))) {
  fail++;
  console.log('FAIL orphan sleeper processes remain');
}
process.exit(fail ? 1 : 0);
