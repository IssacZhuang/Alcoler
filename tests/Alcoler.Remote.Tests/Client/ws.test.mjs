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
      sockets.push(this);
    }
    open() {
      this.readyState = Socket.OPEN;
      this.onopen?.();
    }
    message(message) { this.onmessage?.({ data: JSON.stringify(message) }); }
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

function snapshot(socket, sequence = 1) {
  socket.open();
  socket.message({ type: 'snapshot', snapshot: { sequence, rows: [], state: { phase: 'Streaming' } } });
}

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

test('older callers never advertise append support without a capable transcript', t => {
  const { client, sockets } = fixture(t, { appendText: undefined });
  client.start();
  assert.equal(new URL(sockets[0].url).searchParams.has('appendText'), false);
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

test('foregrounding a suspended stale socket replaces it immediately', t => {
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
