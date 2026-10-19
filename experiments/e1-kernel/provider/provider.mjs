// E1 mock provider: deterministic SSE replay over HTTP.
// Responses are close-delimited (no chunked encoding) so that minimal
// HTTP clients (e.g. the Rust std-only kernel) can read them to EOF.
import http from 'node:http';
import { appendFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

function parseArgs() {
  const a = { port: 8931, log: null, fixture: null, sleeper: null };
  const argv = process.argv.slice(2);
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--port') a.port = +argv[++i];
    else if (argv[i] === '--log') a.log = resolve(argv[++i]);
    else if (argv[i] === '--fixture') a.fixture = resolve(argv[++i]);
    else if (argv[i] === '--sleeper') a.sleeper = resolve(argv[++i]);
  }
  return a;
}

function fnv(s) {
  let h = 0x811c9dc5;
  for (const b of Buffer.from(s)) { h ^= b; h = Math.imul(h, 0x01000193) >>> 0; }
  return h >>> 0;
}

function mulberry32(a) {
  return function () {
    a |= 0; a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const WORDS = ['lorem', 'ipsum', 'dolor', 'sit', 'amet', 'consectetur', 'adipiscing',
  'elit', 'sed', 'do', 'eiusmod', 'tempor', 'incididunt', 'ut', 'labore',
  'et', 'dolore', 'magna', 'aliqua', ' enim ', 'ad', 'minim', 'veniam'];

// Content is a pure function of (session, turn, n): every kernel, every run,
// every language receives byte-identical streams. Exported so the driver can
// feed the same events over stdin (feed transport) for WASM variants.
export function genEvents(session, turn, n, fixture, sleeper) {
  const rng = mulberry32(fnv(`${session}:${turn}`));
  const evs = [];
  const fsAt = Math.floor(n / 3);
  const execAt = Math.floor(n / 2);
  for (let i = 0; i < n; i++) {
    if (i === fsAt) {
      evs.push({ type: 'tool_call', seq: i, tool: 'fs.read', args: { path: fixture, bytes: 4096 } });
      continue;
    }
    if (turn % 2 === 0 && i === execAt) {
      evs.push({ type: 'tool_call', seq: i, tool: 'exec', args: { argv: [sleeper, '500'] } });
      continue;
    }
    evs.push({ type: 'text_delta', seq: i, text: WORDS[Math.floor(rng() * WORDS.length)] + ' ' });
  }
  return evs;
}

function log(obj) {
  if (ARGS.log) appendFileSync(ARGS.log, JSON.stringify(obj) + '\n');
}

const ARGS = parseArgs();

const invokedDirectly = import.meta.url === pathToFileURL(resolve(process.argv[1])).href;
if (invokedDirectly) {
  const server = http.createServer((req, res) => {
  const u = new URL(req.url, 'http://localhost');
  if (u.pathname === '/mark') {
    log({ kind: 'mark', marker: u.searchParams.get('name'), ts: Date.now() });
    res.statusCode = 204; res.end();
    return;
  }
  if (u.pathname === '/health') { res.statusCode = 204; res.end(); return; }
  if (u.pathname !== '/sse') { res.statusCode = 404; res.end(); return; }

  const session = u.searchParams.get('session');
  const turn = +u.searchParams.get('turn');
  const n = +u.searchParams.get('n');
  const interval = Math.max(1, +u.searchParams.get('interval') || 10);
  const evs = genEvents(session, turn, n, ARGS.fixture, ARGS.sleeper);

  // Frames are pre-generated, so the full body length is known upfront. An
  // explicit Content-Length keeps the response readable by every client,
  // including Bun's fetch which hangs on close-delimited bodies.
  const frames = evs.map((e) => `data: ${JSON.stringify(e)}\n\n`);
  frames.push(`data: ${JSON.stringify({ type: 'turn_end', turn })}\n\n`);
  const body = frames.join('');

  res.setHeader('Content-Type', 'text/event-stream');
  res.setHeader('Cache-Control', 'no-cache');
  res.setHeader('Connection', 'close');
  res.setHeader('Content-Length', Buffer.byteLength(body));
  res.useChunkedEncodingByDefault = false;
  res.flushHeaders();

  let first = null, last = null, count = 0, logged = false, closed = false;
  const finish = () => {
    if (logged || first === null) return;
    logged = true;
    log({ kind: 'stream', session, turn, first_ts: first, last_ts: last, count });
  };
  req.on('close', () => { closed = true; finish(); });

  let i = 0;
  const tick = () => {
    if (closed) return;
    if (i >= evs.length) {
      res.write(`data: ${JSON.stringify({ type: 'turn_end', turn })}\n\n`);
      res.end();
      return;
    }
    const ts = Date.now();
    if (first === null) first = ts;
    last = ts; count++;
    res.write(`data: ${JSON.stringify(evs[i])}\n\n`);
    i++;
    setTimeout(tick, interval);
  };
  setTimeout(tick, interval);
  });

  server.listen(ARGS.port, '127.0.0.1', () => {
    console.log(`listening ${ARGS.port}`);
  });
}
