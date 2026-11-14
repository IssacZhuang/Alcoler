import assert from 'node:assert/strict';
import test from 'node:test';
import { StreamClient } from '../../../src/Alcoler.Remote/wwwroot/ws.js';

function fixture(t, options = {}) {
  t.mock.timers.enable({ apis: ['setTimeout', 'Date'], now: 1000000 });
  t.mock.method(Math, 'random', () => 0.5);
  const sockets = [];
  class Socket {
    static CONNECTING = 0;
    static OPEN = 1;
    static CLOSED = 3;
    constructor(url) {
      this.url = url;
      this.readyState = Socket.CONNECTING;
      this.closeCount = 0;
      this.sent = [];
      sockets.push(this);
    }
    open() {
      this.readyState = Socket.OPEN;
      this.onopen?.();
    }
    message(message) { this.onmessage?.({ data: JSON.stringify(message) }); }
    send(data) {
      assert.equal(this.readyState, Socket.OPEN);
      this.sent.push(JSON.parse(data));
    }
    close() {
      this.closeCount++;
      this.readyState = Socket.CLOSED;
      this.onclose?.();
    }
  }
  const previous = Object.fromEntries(['WebSocket', 'location', 'localStorage'].map(key => [key, globalThis[key]]));
  globalThis.WebSocket = Socket;
  globalThis.location = { protocol: 'http:', host: 'localhost:8757' };
  globalThis.localStorage = { getItem: () => 'device-token' };
  const snapshots = [];
  const deltas = [];
  const statuses = [];
  const client = new StreamClient({
    sessionId: 'sess_running',
    onSnapshot: value => snapshots.push(value),
    onDelta: value => deltas.push(value),
    onStatus: value => statuses.push(value),
    appendText: true,
    ...options,
  });
  t.after(() => {
    client.close();
    for (const [key, value] of Object.entries(previous)) {
      if (value === undefined) delete globalThis[key];
      else globalThis[key] = value;
    }
  });
  return { client, sockets, snapshots, deltas, statuses };
}

function snapshot(socket, sequence = 1, history) {
  socket.open();
  socket.message({ type: 'snapshot', snapshot: { sequence, rows: [], state: { phase: 'Streaming' },
    ...(history ? { history } : {}) } });
}

const bounds = { before: 'turn-first', after: 'turn-last' };
const historyPage = (direction, overrides = {}) => ({ rows: [{ $type: 'turn_summary', rowId: 'turn-page' }],
  sequence: 2, history: { before: null, after: null }, direction, ...overrides });

test('an already-running session receives subsequent chunks only after its snapshot', t => {
  const { client, sockets, snapshots, deltas, statuses } = fixture(t);
  client.start();
  const socket = sockets[0];
  assert.equal(new URL(socket.url).searchParams.get('appendText'), '1');
  socket.open();
  assert.equal(client.status, 'connecting');
  socket.message({ type: 'delta', delta: { sequence: 1 } });
  assert.equal(deltas.length, 0);
  snapshot(socket, 20);
  socket.message({ type: 'delta', delta: { sequence: 21, ops: ['next chunk'] } });
  socket.message({ type: 'delta', delta: { sequence: 22, ops: ['final chunk'] } });
  assert.equal(snapshots[0].sequence, 20);
  assert.deepEqual(deltas.map(delta => delta.sequence), [21, 22]);
  assert.deepEqual(statuses, ['connecting', 'online']);
});

test('compact transcript negotiation is opt-in and survives reconnect independently of append support', t => {
  const { client, sockets } = fixture(t, { compact: true, appendText: false });
  client.start();
  let url = new URL(sockets[0].url);
  assert.equal(url.searchParams.get('compact'), '1');
  assert.equal(url.searchParams.has('appendText'), false);
  snapshot(sockets[0]);
  client.manualReconnect();
  url = new URL(sockets[1].url);
  assert.equal(url.searchParams.get('compact'), '1');
  assert.equal(url.searchParams.has('appendText'), false);
});

test('older callers never advertise append support without a capable transcript', t => {
  const { client, sockets } = fixture(t, { appendText: undefined });
  client.start();
  assert.equal(new URL(sockets[0].url).searchParams.has('appendText'), false);
  assert.equal(new URL(sockets[0].url).searchParams.has('compact'), false);
});

test('manual reconnect closes the old socket and ignores its queued callbacks', t => {
  const { client, sockets, snapshots, deltas } = fixture(t);
  client.start();
  const old = sockets[0];
  snapshot(old);
  const staleMessage = old.onmessage;
  const staleClose = old.onclose;
  client.manualReconnect();
  assert.equal(old.closeCount, 1);
  assert.equal(sockets.length, 2);
  staleMessage({ data: JSON.stringify({ type: 'snapshot', snapshot: { sequence: 999 } }) });
  staleClose();
  t.mock.timers.tick(1000);
  assert.equal(sockets.length, 2);
  snapshot(sockets[1], 30);
  sockets[1].message({ type: 'delta', delta: { sequence: 31 } });
  assert.deepEqual(snapshots.map(value => value.sequence), [1, 30]);
  assert.deepEqual(deltas.map(value => value.sequence), [31]);
});

test('suspending discards queued stream events and resumes directly from a fresh snapshot', async t => {
  let anchor = 'turn-reading';
  const { client, sockets, snapshots, deltas, statuses } = fixture(t, { history: true, compact: true,
    anchor: () => anchor });
  client.start();
  const old = sockets[0];
  snapshot(old, 10, bounds);
  const staleMessage = old.onmessage, staleClose = old.onclose, staleError = old.onerror;
  const pending = assert.rejects(client.requestHistory({ before: 'turn-first' }), { code: 'history_disconnected' });
  client.setSuspended(true);
  await pending;
  assert.equal(client.status, 'offline');
  assert.equal(old.closeCount, 1);
  assert.equal(old.onmessage, null);
  assert.equal(client.hasSnapshot, false);
  assert.equal(client.hasHistory, false);
  let reads = 0;
  for (let i = 0; i < 10000; i++) staleMessage({ get data() { reads++; return '{"type":"delta"}'; } });
  staleClose();
  staleError();
  client.start();
  client.ensureConnected();
  client.manualReconnect();
  t.mock.timers.tick(600000);
  assert.equal(reads, 0);
  assert.equal(sockets.length, 1);
  assert.deepEqual(deltas, []);
  await assert.rejects(client.requestHistory({ latest: true }), { code: 'history_unavailable' });

  anchor = 'turn-updated';
  client.setSuspended(false);
  client.ensureConnected();
  client.setSuspended(false);
  assert.equal(sockets.length, 2);
  assert.equal(new URL(sockets[1].url).searchParams.get('around'), anchor);
  sockets[1].open();
  sockets[1].message({ type: 'delta', delta: { sequence: 9999 } });
  assert.deepEqual(deltas, []);
  snapshot(sockets[1], 10010, bounds);
  sockets[1].message({ type: 'delta', delta: { sequence: 10011 } });
  staleMessage({ get data() { assert.fail('the discarded connection must not be parsed'); } });
  assert.deepEqual(snapshots.map(value => value.sequence), [10, 10010]);
  assert.deepEqual(deltas.map(value => value.sequence), [10011]);
  assert.deepEqual(statuses, ['connecting', 'online', 'offline', 'connecting', 'online']);
});

test('a client created in the background waits until visible and cannot resume after closing', t => {
  const { client, sockets } = fixture(t, { suspended: true });
  client.start();
  client.ensureConnected();
  client.manualReconnect();
  t.mock.timers.tick(600000);
  assert.equal(sockets.length, 0);
  client.setSuspended(false);
  client.start();
  client.ensureConnected();
  assert.equal(sockets.length, 1);
  client.setSuspended(true);
  client.close();
  client.setSuspended(false);
  t.mock.timers.tick(600000);
  assert.equal(sockets.length, 1);
});

for (const phase of ['connecting', 'reconnecting']) {
  test(`suspending cancels ${phase} timers until an immediate foreground reconnect`, t => {
    const { client, sockets } = fixture(t);
    client.start();
    if (phase === 'reconnecting') sockets[0].close();
    assert.equal(client.status, phase);
    client.setSuspended(true);
    client.setSuspended(true);
    t.mock.timers.tick(600000);
    assert.equal(sockets.length, 1);
    client.setSuspended(false);
    assert.equal(sockets.length, 2);
    assert.equal(client.status, 'connecting');
    snapshot(sockets[1]);
    t.mock.timers.tick(1000);
    assert.equal(sockets.length, 2);
  });
}

test('suspending invalidates an asynchronous session resume without delaying the new snapshot', async t => {
  let finishResume;
  const { client, sockets } = fixture(t, {
    resume: () => new Promise(resolve => { finishResume = resolve; }),
  });
  client.start();
  snapshot(sockets[0]);
  sockets[0].message({ type: 'ended' });
  client.setSuspended(true);
  client.setSuspended(false);
  assert.equal(sockets.length, 2);
  snapshot(sockets[1], 100);
  finishResume();
  await Promise.resolve();
  t.mock.timers.tick(1000);
  assert.equal(sockets.length, 2);
  assert.equal(client.status, 'online');
});

test('a socket that opens without a snapshot times out instead of appearing online', t => {
  const { client, sockets } = fixture(t);
  client.start();
  sockets[0].open();
  t.mock.timers.tick(15000);
  assert.equal(sockets[0].closeCount, 1);
  assert.equal(client.status, 'reconnecting');
  t.mock.timers.tick(500);
  assert.equal(sockets.length, 2);
});

test('heartbeat frames keep a quiet stream alive and silent sockets reconnect', t => {
  const { client, sockets } = fixture(t);
  client.start();
  snapshot(sockets[0]);
  t.mock.timers.tick(40000);
  sockets[0].message({ type: 'ping' });
  t.mock.timers.tick(5000);
  assert.equal(client.status, 'online');
  assert.equal(sockets.length, 1);
  t.mock.timers.tick(40000);
  assert.equal(sockets[0].closeCount, 1);
  assert.equal(client.status, 'reconnecting');
  t.mock.timers.tick(500);
  assert.equal(sockets.length, 2);
});

test('foregrounding a stale socket replaces it immediately', t => {
  const { client, sockets } = fixture(t);
  client.start();
  snapshot(sockets[0]);
  client.ensureConnected();
  assert.equal(sockets.length, 1);
  t.mock.timers.setTime(Date.now() + 60000);
  client.ensureConnected();
  assert.equal(sockets[0].closeCount, 1);
  assert.equal(sockets.length, 2);
  assert.equal(client.status, 'connecting');
});

test('an ended session resumes once and does not race its close callback', async t => {
  let finishResume;
  let resumeCount = 0;
  const { client, sockets } = fixture(t, {
    resume: () => {
      resumeCount++;
      return new Promise(resolve => { finishResume = resolve; });
    },
  });
  client.start();
  snapshot(sockets[0]);
  const staleClose = sockets[0].onclose;
  sockets[0].message({ type: 'ended' });
  staleClose();
  assert.equal(sockets[0].closeCount, 1);
  assert.equal(resumeCount, 1);
  assert.equal(client.status, 'reconnecting');
  finishResume();
  await Promise.resolve();
  t.mock.timers.tick(500);
  assert.equal(sockets.length, 2);
  snapshot(sockets[1], 10);
  assert.equal(client.status, 'online');
});

test('closing during an asynchronous resume prevents a late reconnect', async t => {
  let finishResume;
  const { client, sockets } = fixture(t, {
    resume: () => new Promise(resolve => { finishResume = resolve; }),
  });
  client.start();
  snapshot(sockets[0]);
  sockets[0].message({ type: 'ended' });
  client.close();
  finishResume();
  await Promise.resolve();
  t.mock.timers.tick(60000);
  assert.equal(sockets.length, 1);
});

test('failed initial snapshots have a bounded retry budget even when handshakes succeed', t => {
  const { client, sockets } = fixture(t);
  client.start();
  for (let attempt = 0; attempt < 9; attempt++) {
    const socket = sockets.at(-1);
    socket.open();
    socket.close();
    if (attempt < 8) t.mock.timers.tick(Math.min(8000, 500 * 2 ** attempt));
  }
  assert.equal(sockets.length, 9);
  assert.equal(client.status, 'offline');
  t.mock.timers.tick(60000);
  assert.equal(sockets.length, 9);
  client.ensureConnected();
  assert.equal(sockets.length, 10);
});

test('closing removes all socket callbacks and scheduled work', t => {
  const { client, sockets } = fixture(t);
  client.start();
  snapshot(sockets[0]);
  client.close();
  assert.equal(sockets[0].onmessage, null);
  assert.equal(sockets[0].onclose, null);
  t.mock.timers.tick(120000);
  assert.equal(sockets.length, 1);
});

test('history capabilities and saved anchors are encoded and re-read on every connection', t => {
  let anchor = 'turn/ +?&=#雪';
  let reads = 0;
  const { client, sockets } = fixture(t, { sessionId: 'sess/ +?雪', history: true,
    anchor: () => { reads++; return anchor; } });
  globalThis.location.protocol = 'https:';
  globalThis.localStorage.getItem = () => 'token/ +?&=#雪';
  client.start();
  let url = new URL(sockets[0].url);
  assert.equal(url.protocol, 'wss:');
  assert.equal(url.pathname, '/api/sessions/' + encodeURIComponent(client.sessionId) + '/stream');
  assert.equal(url.searchParams.get('token'), 'token/ +?&=#雪');
  assert.equal(url.searchParams.get('appendText'), '1');
  assert.equal(url.searchParams.get('compact'), '1');
  assert.equal(url.searchParams.get('history'), '1');
  assert.equal(url.searchParams.get('around'), anchor);
  assert.equal(client.hasHistory, false);
  snapshot(sockets[0], 1, bounds);
  anchor = 'updated-anchor';
  client.manualReconnect();
  url = new URL(sockets[1].url);
  assert.equal(url.searchParams.get('around'), anchor);
  assert.equal(client.hasHistory, false);
  snapshot(sockets[1], 2, bounds);
  anchor = null;
  sockets[1].close();
  t.mock.timers.tick(500);
  url = new URL(sockets[2].url);
  assert.equal(url.searchParams.has('around'), false);
  assert.equal(url.searchParams.get('history'), '1');
  assert.equal(reads, 3);
});

test('legacy default URLs remain unchanged and never read a saved history anchor', async t => {
  const { client, sockets } = fixture(t, { appendText: undefined,
    anchor: () => assert.fail('legacy connections must not read the anchor') });
  client.start();
  assert.equal(sockets[0].url, 'ws://localhost:8757/api/sessions/sess_running/stream?token=device-token');
  snapshot(sockets[0], 1, bounds);
  assert.equal(client.hasHistory, false);
  await assert.rejects(client.requestHistory({ latest: true }), { code: 'history_unavailable' });
  assert.deepEqual(sockets[0].sent, []);
});

test('history requests require an authoritative history snapshot rather than an open socket', async t => {
  const snapshots = [], statuses = [];
  const { client, sockets } = fixture(t, { history: true,
    onSnapshot: value => snapshots.push(value), onStatus: value => statuses.push(value) });
  await assert.rejects(client.requestHistory({ latest: true }), { code: 'history_unavailable' });
  client.start();
  sockets[0].open();
  await assert.rejects(client.requestHistory({ latest: true }), { code: 'history_unavailable' });
  snapshot(sockets[0]);
  assert.equal(client.status, 'online');
  assert.equal(client.hasHistory, false);
  await assert.rejects(client.requestHistory({ latest: true }), { code: 'history_unavailable' });
  snapshot(sockets[0], 2, { before: null, after: null });
  assert.equal(client.hasHistory, true);
  const request = client.requestHistory({ latest: true });
  sockets[0].message({ type: 'history', requestId: 1, page: historyPage('latest') });
  await request;
  assert.deepEqual(snapshots.map(value => value.sequence), [1, 2]);
  assert.deepEqual(statuses, ['connecting', 'online']);
});

test('history requests serialize one direction, match request IDs, and deliver an immutable request copy', async t => {
  const received = [];
  const { client, sockets } = fixture(t, { history: true,
    onHistory: (page, request) => received.push({ page, request }) });
  client.start();
  snapshot(sockets[0], 1, bounds);
  for (const [index, request] of [{ before: 'before/ +?雪' }, { after: 'after/ +?雪' }, { latest: true }].entries()) {
    const original = { ...request };
    const direction = Object.keys(original)[0];
    const promise = client.requestHistory(request);
    const sent = sockets[0].sent.at(-1);
    assert.deepEqual(sent, { type: 'history', requestId: index + 1, ...original });
    assert.ok(Number.isSafeInteger(sent.requestId) && sent.requestId > 0);
    request[direction] = 'caller mutation';
    sockets[0].message({ type: 'history', requestId: String(sent.requestId), page: historyPage(direction) });
    sockets[0].message({ type: 'history', requestId: sent.requestId + 10, page: historyPage(direction) });
    assert.equal(received.length, index);
    const page = historyPage(direction, { sequence: index + 2 });
    sockets[0].message({ type: 'history', requestId: sent.requestId, page });
    const result = await promise;
    assert.deepEqual(result, page);
    assert.equal(received[index].page, result);
    assert.deepEqual(received[index].request, original);
    sockets[0].message({ type: 'history', requestId: sent.requestId, page });
    assert.equal(received.length, index + 1);
  }
});

test('invalid and overlapping history requests are rejected without disturbing the outstanding request', async t => {
  const { client, sockets } = fixture(t, { history: true });
  client.start();
  snapshot(sockets[0], 1, bounds);
  for (const request of [null, [], {}, { before: '' }, { before: 1 }, { after: null }, { latest: false },
    { latest: 1 }, { before: 'a', after: 'b' }, { before: 'a', latest: true },
    { after: 'b', requestId: 9 }, { direction: 'latest' }, Object.create({ before: 'inherited' })]) {
    await assert.rejects(client.requestHistory(request), { code: 'invalid_history_request' });
  }
  assert.equal(sockets[0].sent.length, 0);
  const pending = client.requestHistory({ before: 'first' });
  await assert.rejects(client.requestHistory({ after: 'last' }), { code: 'history_pending' });
  assert.equal(sockets[0].sent.length, 1);
  sockets[0].message({ type: 'history', requestId: 1, page: historyPage('before') });
  await pending;
});

test('history rendering runs synchronously before the next delta and before promise settlement', async t => {
  const events = [];
  const { client, sockets } = fixture(t, { history: true,
    onHistory: (page, request) => events.push(['history', page.sequence, request.before]),
    onDelta: delta => events.push(['delta', delta.sequence]) });
  client.start();
  snapshot(sockets[0], 1, bounds);
  const promise = client.requestHistory({ before: 'first' }).then(() => events.push(['settled']));
  sockets[0].message({ type: 'history', requestId: 1, page: historyPage('before', { sequence: 2 }) });
  sockets[0].message({ type: 'delta', delta: { sequence: 3 } });
  assert.deepEqual(events, [['history', 2, 'first'], ['delta', 3]]);
  await promise;
  assert.deepEqual(events, [['history', 2, 'first'], ['delta', 3], ['settled']]);
  assert.equal(client.status, 'online');
});

test('history timeouts clear the request and late replies cannot fulfill its replacement', async t => {
  const received = [];
  const { client, sockets } = fixture(t, { history: true, onHistory: page => received.push(page) });
  client.start();
  snapshot(sockets[0], 1, bounds);
  const expired = assert.rejects(client.requestHistory({ before: 'first' }), { code: 'history_timeout' });
  t.mock.timers.tick(14999);
  assert.ok(client.historyRequest);
  t.mock.timers.tick(1);
  await expired;
  assert.equal(client.historyRequest, null);
  assert.equal(client.status, 'online');
  const next = client.requestHistory({ before: 'first' });
  sockets[0].message({ type: 'history', requestId: 1, page: historyPage('before') });
  assert.equal(received.length, 0);
  sockets[0].message({ type: 'history', requestId: 2, page: historyPage('before') });
  await next;
  assert.equal(received.length, 1);
});

for (const [name, finish, code] of [
  ['socket disconnects', ({ sockets }) => sockets[0].close(), 'history_disconnected'],
  ['manual reconnects', ({ client }) => client.manualReconnect(), 'history_disconnected'],
  ['client closes', ({ client }) => client.close(), 'history_disconnected'],
  ['socket errors', ({ sockets }) => sockets[0].onerror(), 'history_disconnected'],
  ['stream errors', ({ sockets }) => sockets[0].message({ type: 'error', code: 'stream_failed' }), 'stream_failed'],
  ['ended sessions', ({ sockets }) => sockets[0].message({ type: 'ended' }), 'history_disconnected'],
]) {
  test(`pending history rejects and clears when ${name}`, async t => {
    const context = fixture(t, { history: true });
    const { client, sockets } = context;
    client.start();
    snapshot(sockets[0], 1, bounds);
    const pending = assert.rejects(client.requestHistory({ latest: true }), { code });
    finish(context);
    await pending;
    assert.equal(client.historyRequest, null);
    client.close();
    t.mock.timers.tick(120000);
    assert.equal(client.historyRequest, null);
  });
}

test('old socket responses and errors cannot touch a new connection or its history request', async t => {
  const received = [];
  const { client, sockets } = fixture(t, { history: true, onHistory: page => received.push(page) });
  client.start();
  snapshot(sockets[0], 1, bounds);
  const oldMessage = sockets[0].onmessage, oldError = sockets[0].onerror;
  const old = assert.rejects(client.requestHistory({ before: 'first' }), { code: 'history_disconnected' });
  client.manualReconnect();
  await old;
  snapshot(sockets[1], 10, bounds);
  const next = client.requestHistory({ after: 'last' });
  oldMessage({ data: JSON.stringify({ type: 'history', requestId: 2, page: historyPage('after') }) });
  oldError();
  sockets[1].message({ type: 'history', requestId: 1, page: historyPage('after') });
  assert.equal(received.length, 0);
  assert.equal(client.historyRequest.requestId, 2);
  sockets[1].message({ type: 'history', requestId: 2, page: historyPage('after', { sequence: 11 }) });
  assert.equal((await next).sequence, 11);
  assert.equal(received.length, 1);
});

test('authoritative replacement snapshots retire pending history and can return to legacy mode', async t => {
  const received = [];
  const { client, sockets } = fixture(t, { history: true, onHistory: page => received.push(page) });
  client.start();
  snapshot(sockets[0], 1, bounds);
  const pending = assert.rejects(client.requestHistory({ before: 'first' }), { code: 'stale_history' });
  snapshot(sockets[0], 10);
  await pending;
  assert.equal(client.hasHistory, false);
  sockets[0].message({ type: 'history', requestId: 1, page: historyPage('before') });
  assert.equal(received.length, 0);
  await assert.rejects(client.requestHistory({ latest: true }), { code: 'history_unavailable' });
});

test('stale history errors and malformed pages reject locally without invoking the renderer', async t => {
  const { client, sockets } = fixture(t, { history: true,
    onHistory: () => assert.fail('invalid pages must not render') });
  client.start();
  snapshot(sockets[0], 1, bounds);
  const stale = assert.rejects(client.requestHistory({ latest: true }), { code: 'stale_history' });
  sockets[0].message({ type: 'history', requestId: 1, error: 'stale_history' });
  await stale;
  for (const page of [null, historyPage('after'), historyPage('latest', { history: {} }),
    historyPage('latest', { rows: [null] }), historyPage('latest', { sequence: -1 })]) {
    const pending = assert.rejects(client.requestHistory({ latest: true }), { code: 'invalid_history_response' });
    sockets[0].message({ type: 'history', requestId: sockets[0].sent.at(-1).requestId, page });
    await pending;
  }
  assert.equal(client.historyRequest, null);
  assert.equal(client.status, 'online');
});

test('send and synchronous render failures reject history and allow subsequent requests', async t => {
  const failure = new Error('render failed');
  const { client, sockets } = fixture(t, { history: true, onHistory: () => { throw failure; } });
  client.start();
  snapshot(sockets[0], 1, bounds);
  const send = sockets[0].send;
  sockets[0].send = () => { throw new Error('send failed'); };
  await assert.rejects(client.requestHistory({ latest: true }), /send failed/);
  assert.equal(client.historyRequest, null);
  sockets[0].send = send;
  const pending = assert.rejects(client.requestHistory({ latest: true }), error => error === failure);
  sockets[0].message({ type: 'history', requestId: 2, page: historyPage('latest') });
  await pending;
  client.onHistory = () => {};
  const next = client.requestHistory({ latest: true });
  sockets[0].message({ type: 'history', requestId: 3, page: historyPage('latest') });
  await next;
});

test('closing from the synchronous history callback rejects instead of settling after disconnect', async t => {
  const { client, sockets } = fixture(t, { history: true, onHistory: () => client.close() });
  client.start();
  snapshot(sockets[0], 1, bounds);
  const pending = assert.rejects(client.requestHistory({ latest: true }), { code: 'history_disconnected' });
  sockets[0].message({ type: 'history', requestId: 1, page: historyPage('latest') });
  await pending;
  assert.equal(client.historyRequest, null);
});
