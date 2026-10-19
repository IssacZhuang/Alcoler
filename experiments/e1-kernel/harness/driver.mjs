// E1 kernel driver: spawns a kernel, speaks contract v1.1 over stdin/stdout,
// drives one scenario (N concurrent sessions, scheduled exec cancels), and
// returns raw per-run metrics. Memory sampling and provider-log joins are
// done by the runner; this module only drives and times.
import { spawn, spawnSync } from 'node:child_process';
import readline from 'node:readline';
import { genEvents } from '../provider/provider.mjs';

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export function countSleepers(name = 'e1-sleeper.exe') {
  const r = spawnSync('tasklist', ['/FI', `IMAGENAME eq ${name}`, '/FO', 'CSV', '/NH'], { encoding: 'utf8' });
  const out = r.stdout || '';
  return out.toLowerCase().includes(name.toLowerCase())
    ? out.split('\n').filter((l) => l.toLowerCase().includes(name.toLowerCase())).length
    : 0;
}

export async function runKernelScenario(opts) {
  const {
    cmd, args = [], env = {},
    dbPath, stderrPath = null,
    profile, sessionIds,
    cancelEveryExecTurns = 20,
    idleSec = 3,
    onEvent = null,
    feed = null, // { fixture, sleeper, skipTools, rewrites } -> stdin feed transport (WASM variants)
  } = opts;

  const t = {
    spawn: null, ready: null, idleEnd: null, startSent: null, allDone: null,
    settle1: null, gcSent: null, gcDone: null, settle2: null,
    shutdownSent: null, exit: null,
  };
  t.spawn = Date.now();

  const child = spawn(cmd, args, {
    env: { ...process.env, E1_DB: dbPath, ...env },
    stdio: ['pipe', 'pipe', 'pipe'],
    windowsHide: true,
  });

  let earlyExit = null;
  let stderrTail = '';
  child.on('error', (e) => { earlyExit = earlyExit ?? `spawn-error:${e.message}`; });
  child.once('exit', (c) => { earlyExit = earlyExit ?? c; });
  child.stderr.setEncoding('utf8');
  child.stderr.on('data', (d) => {
    stderrTail = (stderrTail + d).slice(-4000);
    if (stderrPath) { /* kept in memory only; tail reported on failure */ }
  });

  const rl = readline.createInterface({ input: child.stdout });
  const waiters = [];
  const waitFor = (pred, timeoutMs, label) => new Promise((res, rej) => {
    const w = { pred, resolve: res };
    waiters.push(w);
    setTimeout(() => {
      const i = waiters.indexOf(w);
      if (i >= 0) {
        waiters.splice(i, 1);
        rej(new Error(`waitFor timeout: ${label} (earlyExit=${earlyExit})\nstderr tail:\n${stderrTail}`));
      }
    }, timeoutMs).unref?.();
  });

  const counts = Object.create(null);
  const errors = [];
  const deltaFirst = new Map();
  const deltaLast = new Map();
  const toolDurations = { 'fs.read': [], 'exec-ok': [], 'exec-cancelled': [], 'exec-error': [] };
  const cancelSentTs = new Map();
  const cancelToResult = [];
  const sessionDone = new Set();
  const turnsDone = new Map();
  let compactedCount = 0;
  const cancelTimers = [];
  const feedFirst = new Map();
  const feedLast = new Map();
  const feedTtft = [];
  const feedLag = [];

  const isCancelTurn = (turn) =>
    !feed && cancelEveryExecTurns > 0 && turn % cancelEveryExecTurns === 0 && turn % 2 === 0;

  const send = (obj) => child.stdin.write(JSON.stringify(obj) + '\n');

  const readyP = waitFor((v) => v.type === 'ready', 30000, 'ready');

  rl.on('line', (l) => {
    l = l.trim();
    if (!l) return;
    let v;
    try { v = JSON.parse(l); } catch { return; }
    if (t.ready === null) t.ready = Date.now();
    if (onEvent) onEvent(v);
    counts[v.type] = (counts[v.type] || 0) + 1;
    switch (v.type) {
      case 'model_delta': {
        const k = `${v.session}:${v.turn}`;
        if (!deltaFirst.has(k)) {
          deltaFirst.set(k, v.ts);
          // ttft is first-kernel-delta minus first-send only; pushing on every
          // delta would just measure half the turn's wall time.
          const st = feedFirst.get(k);
          if (st != null) feedTtft.push(v.ts - st);
        }
        deltaLast.set(k, v.ts);
        break;
      }
      case 'tool_call':
        if (v.tool === 'exec' && isCancelTurn(v.turn)) {
          cancelTimers.push(setTimeout(() => {
            cancelSentTs.set(`${v.session}:${v.turn}`, Date.now());
            send({ cmd: 'cancel', session: v.session, turn: v.turn });
          }, 100));
        }
        break;
      case 'tool_result': {
        if (v.tool === 'fs.read') toolDurations['fs.read'].push(v.durationMs);
        else if (v.tool === 'exec') {
          toolDurations[v.status === 'cancelled' ? 'exec-cancelled' : v.status === 'error' ? 'exec-error' : 'exec-ok'].push(v.durationMs);
        }
        if (v.status === 'cancelled') {
          const sent = cancelSentTs.get(`${v.session}:${v.turn}`);
          if (sent) cancelToResult.push(v.ts - sent);
        }
        break;
      }
      case 'compacted': compactedCount++; break;
      case 'session_done': sessionDone.add(v.session); break;
      case 'turn_done': {
        turnsDone.set(v.session, (turnsDone.get(v.session) || 0) + 1);
        if (feed) {
          const k = `${v.session}:${v.turn}`;
          const lt = deltaLast.get(k);
          const sl = feedLast.get(k);
          if (lt != null && sl != null) feedLag.push(lt - sl);
        }
        break;
      }
      case 'error': if (errors.length < 20) errors.push(v); break;
      default: break;
    }
    for (let i = waiters.length - 1; i >= 0; i--) {
      if (waiters[i].pred(v)) { waiters[i].resolve(v); waiters.splice(i, 1); }
    }
  });
  rl.on('close', () => { if (earlyExit === null) earlyExit = 'stdout-closed'; });

  const result = { pid: child.pid, timings: t, counts, errors, deltaFirst, deltaLast, toolDurations, cancelToResult, sessionDoneCount: 0, turnsDone, compactedCount, exitCode: null, orphans: null, stderrTail: '' };

  try {
    await readyP;
    if (idleSec > 0) await sleep(idleSec * 1000);
    t.idleEnd = Date.now();

    t.startSent = Date.now();
    let i = 0;
    for (const id of sessionIds) {
      send({ cmd: 'start_session', id, profile });
      if (++i % 4 === 0) await sleep(50);
    }

    if (feed) {
      const { fixture, sleeper, skipTools = ['exec'], rewrites = [] } = feed;
      const pumps = sessionIds.map(async (id) => {
        try {
          for (let turn = 1; turn <= profile.turns; turn++) {
            const evs = genEvents(id, turn, profile.eventsPerTurn, fixture, sleeper)
              .filter((e) => !(e.type === 'tool_call' && skipTools.includes(e.tool)));
            for (const ev of evs) {
              if (ev.type === 'tool_call' && ev.args && typeof ev.args.path === 'string') {
                for (const r of rewrites) ev.args.path = ev.args.path.replaceAll(r.from, r.to);
              }
              if (ev.type === 'text_delta') {
                const k = `${id}:${turn}`;
                const tsS = Date.now();
                if (!feedFirst.has(k)) feedFirst.set(k, tsS);
                feedLast.set(k, tsS);
              }
              send({ cmd: 'model_event', session: id, turn, event: ev });
              await sleep(profile.intervalMs);
            }
            send({ cmd: 'model_event', session: id, turn, event: { type: 'turn_end', turn } });
            await sleep(profile.intervalMs);
          }
        } catch (e) {
          errors.push({ type: 'error', code: `feed-pump: ${e.message}`, fatal: true });
        }
      });
      await Promise.all(pumps);
    }

    const runTimeoutMs = sessionIds.length * profile.turns * (profile.eventsPerTurn * profile.intervalMs + 1200) + 60000;
    const deadline = Date.now() + runTimeoutMs;
    while (sessionDone.size < sessionIds.length) {
      if (earlyExit !== null) throw new Error(`kernel exited early: ${earlyExit}\nstderr tail:\n${stderrTail}`);
      if (Date.now() > deadline) throw new Error(`run timeout (${runTimeoutMs}ms); sessions done ${sessionDone.size}/${sessionIds.length}`);
      await sleep(200);
    }
    t.allDone = Date.now();

    // settle gaps must exceed the ~1.7s sampler cycle so each checkpoint
    // gets its own snapshot (post-gc especially).
    await sleep(2500);
    t.settle1 = Date.now();

    t.gcSent = Date.now();
    send({ cmd: 'gc' });
    await waitFor((v) => v.type === 'gc_done', 10000, 'gc_done');
    t.gcDone = Date.now();
    await sleep(3000);
    t.settle2 = Date.now();

    t.shutdownSent = Date.now();
    send({ cmd: 'shutdown' });
    const code = await new Promise((res) => {
      const to = setTimeout(() => res('shutdown-timeout'), 2500);
      child.once('exit', (c) => { clearTimeout(to); res(c); });
    });
    result.exitCode = code;
    if (code === 'shutdown-timeout') {
      spawnSync('taskkill', ['/F', '/T', '/PID', String(child.pid)]);
    }
    t.exit = Date.now();

    await sleep(2000);
    result.orphans = countSleepers();
    result.sessionDoneCount = sessionDone.size;
    result.compactedCount = compactedCount;
    result.stderrTail = stderrTail;
    if (feed) result.feedTiming = { ttft: feedTtft, lag: feedLag };
    return result;
  } finally {
    for (const ti of cancelTimers) clearTimeout(ti);
    try { child.kill(); } catch { /* already exited */ }
    try { spawnSync('taskkill', ['/F', '/T', '/PID', String(child.pid)]); } catch { /* noop */ }
  }
}
