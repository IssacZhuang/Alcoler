// E1 offline aggregation: rebuild runs.csv and report.md from a results
// directory's artifacts (events/*.meta.json + sampler.csv). Memory checkpoints
// use the last sampler snapshot at or before each checkpoint time.
//
// Usage: node harness/aggregate.mjs <resultsDir>
import { existsSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export function aggregateDir(outDir) {

// ---------- sampler ----------
const rows = [];
{
  const csv = readFileSync(path.join(outDir, 'sampler.csv'), 'utf8').split('\n');
  for (const l of csv) {
    const p = l.split(',');
    if (p.length === 6 && +p[0] > 0) {
      rows.push({ ts: +p[0], pid: +p[1], ppid: +p[2], name: p[3], private: +p[4], ws: +p[5] });
    }
  }
}

function treeSum(snapshot, rootPid) {
  const byPid = new Map();
  const kids = new Map();
  for (const r of snapshot) {
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

const byTs = new Map();
for (const r of rows) {
  if (!byTs.has(r.ts)) byTs.set(r.ts, []);
  byTs.get(r.ts).push(r);
}
const tsSorted = [...byTs.keys()].sort((a, b) => a - b);

function latestSnapshotAtOrBefore(ts) {
  let lo = 0, hi = tsSorted.length - 1, best = -1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (tsSorted[mid] <= ts) { best = mid; lo = mid + 1; } else { hi = mid - 1; }
  }
  return best;
}
// The Get-Process/Win32_Process join occasionally misses a live pid in one
// snapshot; walk backward (<=5s) to the most recent snapshot that has it.
function treeAt(pid, ts) {
  for (let k = latestSnapshotAtOrBefore(ts); k >= 0 && ts - tsSorted[k] <= 5000; k--) {
    const s = treeSum(byTs.get(tsSorted[k]), pid);
    if (s) return s;
  }
  return null;
}
function peakTree(pid, fromTs, toTs) {
  let peak = null;
  for (const ts of tsSorted) {
    if (ts < fromTs || ts > toTs) continue;
    const s = treeSum(byTs.get(ts), pid);
    if (s && (peak === null || s.priv > peak.priv)) peak = s;
  }
  return peak;
}
const mb = (b) => (b == null ? null : Math.round(b / 10485.76) / 100);
const med = (a) => (a && a.length ? a.slice().sort((x, y) => x - y)[Math.floor(a.length / 2)] : null);
const p95 = (a) => (a && a.length ? a.slice().sort((x, y) => x - y)[Math.min(a.length - 1, Math.floor(a.length * 0.95))] : null);
const r1 = (x) => (x == null ? null : Math.round(x * 10) / 10);

// ---------- per-run rebuild ----------
const metas = readdirSync(path.join(outDir, 'events'))
  .filter((f) => f.endsWith('.meta.json'))
  .map((f) => JSON.parse(readFileSync(path.join(outDir, 'events', f), 'utf8')))
  .sort((a, b) => a.timings.spawn - b.timings.spawn);

const runs = [];
for (const m of metas) {
  const t = m.timings;
  const nameMatch = m.run.match(/^(\w+)-s(\d+)-r(\d+)$/);
  if (!nameMatch) continue;
  const [, lang, sess, rep] = nameMatch;
  const idleMem = treeAt(m.pid, t.idleEnd);
  const settle1 = treeAt(m.pid, t.settle1);
  const settle2 = treeAt(m.pid, t.settle2);
  const peak = peakTree(m.pid, t.spawn, t.exit);
  runs.push({
    run: m.run, lang, sessions: sess, rep,
    error: '',
    exit_code: m.exitCode, orphans: m.orphans, errors: (m.counts && m.counts.error) || 0,
    ready_ms: r1(t.ready - t.spawn),
    idle_priv_mb: mb(idleMem?.priv), idle_ws_mb: mb(idleMem?.ws), idle_procs: idleMem?.n,
    settle1_priv_mb: mb(settle1?.priv),
    settle2_priv_mb: mb(settle2?.priv), settle2_ws_mb: mb(settle2?.ws),
    peak_priv_mb: mb(peak?.priv), peak_ws_mb: mb(peak?.ws), peak_procs: peak?.n,
    ttft_med_ms: r1(med(m.ttft_raw)), ttft_p95_ms: r1(p95(m.ttft_raw)),
    lag_med_ms: r1(med(m.lag_raw)), lag_p95_ms: r1(p95(m.lag_raw)),
    fs_med_ms: r1(med(m.toolDurations?.['fs.read'])),
    exec_med_ms: r1(med(m.toolDurations?.['exec-ok'])),
    cancelled_med_ms: r1(med(m.toolDurations?.['exec-cancelled'])),
    cancel_to_result_p95_ms: r1(p95(m.cancelToResult)),
    compacted: m.compacted,
    model_deltas: (m.counts && m.counts.model_delta) || 0,
  });
}

// ---------- CSV ----------
const cols = [...new Set(runs.flatMap((r) => Object.keys(r)))];
const csv = [cols.join(',')];
for (const r of runs) csv.push(cols.map((c) => r[c] ?? '').join(','));
writeFileSync(path.join(outDir, 'runs.csv'), csv.join('\n'));

// ---------- report ----------
const numCols = ['idle_priv_mb', 'settle1_priv_mb', 'settle2_priv_mb', 'peak_priv_mb', 'peak_ws_mb', 'ready_ms', 'ttft_med_ms', 'ttft_p95_ms', 'lag_med_ms', 'lag_p95_ms', 'fs_med_ms', 'exec_med_ms', 'cancelled_med_ms', 'cancel_to_result_p95_ms'];
const langs = [...new Set(runs.map((r) => r.lang))];
const sessionsList = [...new Set(runs.map((r) => r.sessions))].sort((a, b) => a - b);
function agg(lang, sessions) {
  const rs = runs.filter((r) => r.lang === lang && r.sessions === sessions);
  if (!rs.length) return null;
  const o = {
    n: rs.length,
    errors: rs.reduce((a, r) => a + (+r.errors || 0), 0),
    orphans: rs.reduce((a, r) => a + (+r.orphans || 0), 0),
    exitBad: rs.filter((r) => r.exit_code !== 0 && r.exit_code !== undefined).length,
  };
  for (const c of numCols) {
    const vals = rs.map((r) => r[c]).filter((v) => v != null);
    o[c] = vals.length ? med(vals) : null;
  }
  return o;
}

let md = `# E1 kernel measurement — ${path.basename(outDir)}\n\n`;
md += `- date: ${new Date().toISOString()}\n`;
md += `- machine: ${os.hostname()} / ${os.cpus()[0]?.model?.trim()} / ${Math.round(os.totalmem() / 2 ** 30)} GB RAM\n`;
md += `- runs: ${runs.length} (aggregated offline from sampler.csv + per-run meta)\n`;
md += `- memory metric: process-tree Private Bytes sum (MB); ws columns are Working Set sums (shared pages double-counted, reference only)\n\n`;

md += `## Memory (private MB, median)\n\n| lang | sessions | idle | settle(post-task) | settle(post-gc) | peak | peak ws |\n|---|---|---|---|---|---|---|\n`;
for (const s of sessionsList) for (const l of langs) {
  const a = agg(l, s);
  if (a) md += `| ${l} | ${s} | ${a.idle_priv_mb} | ${a.settle1_priv_mb} | ${a.settle2_priv_mb} | ${a.peak_priv_mb} | ${a.peak_ws_mb} |\n`;
}
md += `\n## Timing (median / p95, ms)\n\n| lang | sessions | ready | ttft med | ttft p95 | stream-lag med | lag p95 | fs.read med | exec med | exec cancelled med | cancel->result p95 |\n|---|---|---|---|---|---|---|---|---|---|---|\n`;
for (const s of sessionsList) for (const l of langs) {
  const a = agg(l, s);
  if (a) md += `| ${l} | ${s} | ${a.ready_ms} | ${a.ttft_med_ms} | ${a.ttft_p95_ms} | ${a.lag_med_ms} | ${a.lag_p95_ms} | ${a.fs_med_ms} | ${a.exec_med_ms} | ${a.cancelled_med_ms} | ${a.cancel_to_result_p95_ms} |\n`;
}
md += `\n## Correctness / hygiene\n\n| lang | sessions | runs | error-events | orphans | non-zero-exit |\n|---|---|---|---|---|---|\n`;
for (const s of sessionsList) for (const l of langs) {
  const a = agg(l, s);
  if (a) md += `| ${l} | ${s} | ${a.n} | ${a.errors} | ${a.orphans} | ${a.exitBad} |\n`;
}
md += `\nNotes: settle2 is after the contract \`gc\` command (Node --expose-gc, Go FreeOSMemory, .NET full blocking GC; Rust reports gc_done unforced). TTFT = kernel first model_delta ts minus provider first SSE event ts (same host clock, ms resolution). Stream-lag = kernel last delta ts minus provider last event ts per turn. Checkpoint memory uses the last sampler snapshot at or before the checkpoint.\n`;
writeFileSync(path.join(outDir, 'report.md'), md);
  console.log(`aggregated ${runs.length} runs -> ${path.join(outDir, 'report.md')}`);
}

const invokedDirectly = process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href;
if (invokedDirectly) {
  const outDir = process.argv[2];
  if (!outDir || !existsSync(path.join(outDir, 'events'))) {
    console.error('usage: node harness/aggregate.mjs <resultsDir>');
    process.exit(1);
  }
  aggregateDir(outDir);
}
