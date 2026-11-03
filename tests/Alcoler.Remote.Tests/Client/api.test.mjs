import assert from 'node:assert/strict';
import { afterEach, beforeEach, test } from 'node:test';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { getSlashCatalog, getTurnDetails, getRowDetails } from '../../../src/Alcoler.Remote/wwwroot/api.js';

const originalFetch = globalThis.fetch;
const originalStorage = globalThis.localStorage;
let requests;
beforeEach(() => {
  requests = [];
  globalThis.localStorage = { getItem: key => key === 'alcoler.deviceToken' ? 'paired-device-token' : null };
  globalThis.fetch = async (url, options) => {
    requests.push({ url, options });
    return { ok: true, status: 200, json: async () => ({ entries: [] }) };
  };
});
afterEach(() => {
  globalThis.fetch = originalFetch;
  if (originalStorage === undefined) delete globalThis.localStorage;
  else globalThis.localStorage = originalStorage;
});

test('draft discovery encodes workspace roots and uses authenticated uncached requests', async () => {
  const workspaceRoot = 'C:/Projects/中文 project & test';
  assert.deepEqual(await getSlashCatalog({ workspaceRoot }), { entries: [] });
  assert.equal(requests[0].url, `/api/slash-catalog?workspaceRoot=${encodeURIComponent(workspaceRoot)}`);
  assert.equal(requests[0].options.headers.Authorization, 'Bearer paired-device-token');
  assert.equal(requests[0].options.method, 'GET');
  assert.equal(requests[0].options.cache, 'no-store');
  assert.equal(requests[0].options.body, undefined);
});

test('session discovery never sends a client workspace override', async () => {
  await getSlashCatalog({ sessionId: 'sess_name/with ? symbols', workspaceRoot: 'C:/untrusted-override' });
  assert.equal(requests[0].url, `/api/sessions/${encodeURIComponent('sess_name/with ? symbols')}/slash-catalog`);
  assert.doesNotMatch(requests[0].url, /workspace|untrusted/);
});

test('an unset workspace can discover built-ins without a session', async () => {
  await getSlashCatalog();
  assert.equal(requests[0].url, '/api/slash-catalog?workspaceRoot=');
});

for (const [kind, load] of [['turns', getTurnDetails], ['rows', getRowDetails]]) {
  test(`${kind} detail requests encode identifiers and versions and propagate cancellation`, async () => {
    const controller = new AbortController();
    const session = 'sess_中文 & session';
    const row = 'row/? symbols';
    const version = 'opaque+/= version';
    await load(session, row, { signal: controller.signal, version });
    assert.equal(requests[0].url, `/api/sessions/${encodeURIComponent(session)}/${kind}/${encodeURIComponent(row)}?version=${encodeURIComponent(version)}`);
    assert.equal(requests[0].options.headers.Authorization, 'Bearer paired-device-token');
    assert.equal(requests[0].options.cache, 'no-store');
    assert.equal(requests[0].options.signal, controller.signal);
    await load('session', 'row');
    assert.equal(requests[1].url, `/api/sessions/session/${kind}/row`);
  });
}

test('detail version conflicts remain distinguishable from missing content', async () => {
  globalThis.fetch = async () => ({ ok: false, status: 409, json: async () => ({ error: 'stale_details' }) });
  await assert.rejects(getTurnDetails('session', 'row', { version: 'old' }), { status: 409, code: 'stale_details' });
  globalThis.fetch = async () => ({ ok: false, status: 404, json: async () => ({ error: 'not_found' }) });
  await assert.rejects(getRowDetails('session', 'removed'), { status: 404, code: 'not_found' });
});

let artifactFixtureId = 0;
const flush = () => new Promise(resolve => setImmediate(resolve));
function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
async function artifacts(t) {
  const api = await import(`../../../src/Alcoler.Remote/wwwroot/api.js?artifacts=${++artifactFixtureId}`);
  const created = new Map();
  const revoked = [];
  t.mock.method(URL, 'createObjectURL', blob => {
    const url = `blob:test-${created.size}`;
    created.set(url, blob);
    return url;
  });
  t.mock.method(URL, 'revokeObjectURL', url => revoked.push(url));
  return { ...api, created, revoked };
}
function blobResponse(blob = new Blob(['fixture'], { type: 'image/png' })) {
  return { ok: true, status: 200, blob: async () => blob };
}

test('a stale aborted rejection cannot delete a replacement cache entry or orphan its URL', async t => {
  const api = await artifacts(t);
  const pending = [];
  globalThis.fetch = (url, options) => {
    if (!url.includes('race-artifact')) return Promise.resolve(blobResponse());
    const reply = deferred();
    pending.push({ ...reply, options });
    return reply.promise;
  };
  const first = api.acquireArtifactObjectUrl('race-artifact', 'image/png');
  await flush();
  first.release();
  assert.equal(pending[0].options.signal.aborted, true);
  const second = api.acquireArtifactObjectUrl('race-artifact', 'image/png');
  await flush();
  assert.equal(pending.length, 2);
  pending[0].reject(new Error('late first rejection'));
  await assert.rejects(first.promise, { name: 'AbortError' });
  pending[1].resolve(blobResponse());
  const url = await second.promise;
  const shared = api.acquireArtifactObjectUrl('race-artifact', 'image/png');
  assert.equal(shared.promise, second.promise);
  assert.equal(await shared.promise, url);
  assert.equal(pending.length, 2);
  second.release();
  shared.release();
  for (let i = 0; i < 81; i++) {
    const lease = api.acquireArtifactObjectUrl(`pressure-${i}`, 'image/png');
    await lease.promise;
    lease.release();
  }
  assert.ok(api.revoked.includes(url));
  assert.equal(new Set(api.revoked).size, api.revoked.length);
});

test('artifact hydration is four-wide through body consumption and shares pending consumers', async t => {
  const api = await artifacts(t);
  const bodies = [];
  globalThis.fetch = async (url, options) => {
    const body = deferred();
    options.signal.addEventListener('abort', () => body.reject(options.signal.reason), { once: true });
    bodies.push({ ...body, url, options });
    return { ok: true, status: 200, blob: () => body.promise };
  };
  const leases = Array.from({ length: 10 }, (_, i) => api.acquireArtifactObjectUrl(`concurrency-${i}`, 'image/png'));
  const shared = api.acquireArtifactObjectUrl('concurrency-0', 'image/png');
  const settled = Promise.allSettled(leases.map(lease => lease.promise));
  await flush();
  assert.equal(bodies.length, 4);
  assert.equal(shared.promise, leases[0].promise);
  leases[0].release();
  assert.equal(bodies[0].options.signal.aborted, false);
  leases[8].release();
  assert.equal(bodies.length, 4);
  bodies[0].resolve(new Blob(['first'], { type: 'image/png' }));
  await shared.promise;
  await flush();
  assert.equal(bodies.length, 5);
  for (const lease of leases) lease.release();
  shared.release();
  await settled;
  await flush();
  assert.equal(bodies.length, 5);
  assert.equal(bodies.some(body => body.url.includes('concurrency-8')), false);
  for (const body of bodies.slice(1)) assert.equal(body.options.signal.aborted, true);
});

test('last-consumer release aborts queued and in-flight hydration without creating stale URLs', async t => {
  const api = await artifacts(t);
  const replies = [];
  globalThis.fetch = (url, options) => {
    const reply = deferred();
    replies.push({ ...reply, url, options });
    return reply.promise;
  };
  const leases = Array.from({ length: 6 }, (_, i) => api.acquireArtifactObjectUrl(`dispose-${i}`, 'image/png'));
  const settled = Promise.allSettled(leases.map(lease => lease.promise));
  await flush();
  assert.equal(replies.length, 4);
  for (const lease of leases) { lease.release(); lease.release(); }
  for (const reply of replies) {
    assert.equal(reply.options.signal.aborted, true);
    reply.resolve(blobResponse());
  }
  assert.ok((await settled).every(result => result.status === 'rejected'));
  await flush();
  assert.equal(replies.length, 4);
  assert.equal(api.created.size, 0);
});

test('cache pressure cannot revoke a pinned URL and releases eventually permit eviction', async t => {
  const api = await artifacts(t);
  globalThis.fetch = async () => blobResponse();
  const pinned = api.acquireArtifactObjectUrl('pinned', 'image/png');
  const url = await pinned.promise;
  for (let i = 0; i < 100; i++) {
    const lease = api.acquireArtifactObjectUrl(`pinned-pressure-${i}`, 'image/png');
    await lease.promise;
    lease.release();
  }
  assert.equal(api.revoked.includes(url), false);
  const overlay = api.acquireArtifactObjectUrl('pinned', 'image/png');
  assert.equal(overlay.promise, pinned.promise);
  pinned.release();
  assert.equal(api.revoked.includes(url), false);
  overlay.release();
  for (let i = 0; i < 81; i++) {
    const lease = api.acquireArtifactObjectUrl(`released-pressure-${i}`, 'image/png');
    await lease.promise;
    lease.release();
  }
  assert.ok(api.revoked.includes(url));
});

test('large blobs retain their exact original bytes and MIME with authenticated uncached GETs', async t => {
  const api = await artifacts(t);
  const bytes = Uint8Array.from({ length: 2 * 1024 * 1024 + 17 }, (_, i) => (i * 131 + 19) & 255);
  const blob = new Blob([bytes], { type: 'video/webm; codecs=vp9' });
  globalThis.fetch = async (url, options) => { requests.push({ url, options }); return blobResponse(blob); };
  const lease = api.acquireArtifactObjectUrl('alcoler-artifact://fixture/large & original.webm', blob.type);
  const url = await lease.promise;
  assert.equal(api.created.get(url), blob);
  assert.equal(api.created.get(url).type, 'video/webm; codecs=vp9');
  assert.deepEqual(new Uint8Array(await api.created.get(url).arrayBuffer()), bytes);
  assert.equal(requests[0].options.headers.Authorization, 'Bearer paired-device-token');
  assert.equal(requests[0].options.method, 'GET');
  assert.equal(requests[0].options.cache, 'no-store');
  assert.match(requests[0].url, /mime=video%2Fwebm%3B%20codecs%3Dvp9/);
  lease.release();
});

test('older cached render modules retain promise-based object URL imports and deduplication', async t => {
  const api = await artifacts(t);
  globalThis.fetch = async () => blobResponse();
  const first = api.artifactObjectUrl('legacy-render', 'image/png');
  const second = api.artifactObjectUrl('legacy-render', 'image/png');
  assert.equal(first, second);
  assert.equal(await first, await second);
  assert.equal(api.created.size, 1);
});

test('native artifact URLs remain same-origin and are constructed without fetching', async t => {
  const api = await artifacts(t);
  const uri = 'https://untrusted.example/redirect?token=foreign & path';
  const path = api.artifactUrl(uri, 'audio/wav');
  assert.ok(path.startsWith('/api/artifacts?'));
  const url = new URL(path, 'https://paired.example');
  assert.equal(url.origin, 'https://paired.example');
  assert.equal(url.searchParams.get('uri'), uri);
  assert.equal(url.searchParams.get('mime'), 'audio/wav');
  assert.equal(url.searchParams.get('token'), 'paired-device-token');
  assert.equal(requests.length, 0);
});

test('service worker never caches native credential-bearing artifact GETs, including offline failures', async () => {
  const handlers = new Map();
  const source = readFileSync(new URL('../../../src/Alcoler.Remote/wwwroot/sw.js', import.meta.url), 'utf8');
  let offline = false;
  const fetched = [];
  runInNewContext(source, {
    URL, Response, JSON,
    self: { location: { origin: 'https://paired.example' }, addEventListener: (name, fn) => handlers.set(name, fn) },
    caches: { open() { assert.fail('artifact credentials reached the shell cache'); } },
    fetch: async request => { fetched.push(request); if (offline) throw new Error('offline'); return new Response('original-bytes'); },
  });
  const request = { method: 'GET', url: 'https://paired.example/api/artifacts?uri=encoded&token=private-device-token' };
  let response;
  handlers.get('fetch')({ request, respondWith: value => { response = value; } });
  assert.equal(await (await response).text(), 'original-bytes');
  assert.equal(fetched[0], request);
  offline = true;
  handlers.get('fetch')({ request, respondWith: value => { response = value; } });
  assert.equal((await response).status, 503);
  assert.deepEqual(await (await response).json(), { error: 'offline' });
});
