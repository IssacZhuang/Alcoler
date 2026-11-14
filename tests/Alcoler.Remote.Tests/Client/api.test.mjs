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

test('thumbnail leases encode authenticated thumbnail requests and never alias an original or another MIME', async t => {
  const api = await artifacts(t);
  const uri = 'alcoler-artifact://fixture/中文 & full image.png';
  const thumbnail = new Blob(['thumbnail webp bytes'], { type: 'image/webp' });
  const original = new Blob(['lossless original png bytes'], { type: 'image/png' });
  globalThis.fetch = async (url, options) => {
    requests.push({ url, options });
    return blobResponse(new URL(url, 'https://paired.example').searchParams.has('thumbnail') ? thumbnail : original);
  };
  const thumb = api.acquireThumbnailObjectUrl(uri, 'image/png');
  const shared = api.acquireArtifactObjectUrl(uri, 'image/png', { thumbnail: true });
  const full = api.acquireArtifactObjectUrl(uri, 'image/png');
  assert.equal(shared.promise, thumb.promise);
  assert.notEqual(full.promise, thumb.promise);
  const [thumbUrl, fullUrl] = await Promise.all([thumb.promise, full.promise]);
  assert.notEqual(thumbUrl, fullUrl);
  assert.equal(api.created.get(thumbUrl), thumbnail);
  assert.equal(api.created.get(fullUrl), original);
  assert.equal(requests.length, 2);
  for (const request of requests) {
    const url = new URL(request.url, 'https://paired.example');
    assert.equal(url.pathname, '/api/artifacts');
    assert.equal(url.searchParams.get('uri'), uri);
    assert.equal(url.searchParams.get('mime'), 'image/png');
    assert.equal(url.searchParams.has('token'), false);
    assert.equal(request.options.headers.Authorization, 'Bearer paired-device-token');
    assert.equal(request.options.cache, 'no-store');
    assert.equal(request.options.method, 'GET');
  }
  assert.equal(new URL(requests[0].url, 'https://paired.example').searchParams.get('thumbnail'), '1');
  assert.equal(new URL(requests[1].url, 'https://paired.example').searchParams.has('thumbnail'), false);
  const otherMime = api.acquireThumbnailObjectUrl(uri, 'image/jpeg');
  assert.notEqual(otherMime.promise, thumb.promise);
  await otherMime.promise;
  assert.equal(requests.length, 3);
  thumb.release(); shared.release(); full.release(); otherMime.release();
});

test('thumbnail and original body consumption share one four-wide scheduler and queued cancellation', async t => {
  const api = await artifacts(t), bodies = [];
  globalThis.fetch = async (url, options) => {
    const body = deferred();
    options.signal.addEventListener('abort', () => body.reject(options.signal.reason), { once: true });
    bodies.push({ url, options, ...body });
    return { ok: true, status: 200, blob: () => body.promise };
  };
  const leases = Array.from({ length: 12 }, (_, index) => index % 2
    ? api.acquireArtifactObjectUrl(`mixed-limit-${index}`, 'image/png')
    : api.acquireThumbnailObjectUrl(`mixed-limit-${index}`, 'image/png'));
  const shared = api.acquireThumbnailObjectUrl('mixed-limit-0', 'image/png');
  const settled = Promise.allSettled(leases.map(lease => lease.promise));
  await flush();
  assert.equal(bodies.length, 4);
  assert.equal(shared.promise, leases[0].promise);
  assert.equal(bodies.filter(body => body.url.includes('thumbnail=1')).length, 2);
  leases[0].release();
  leases[10].release();
  assert.equal(bodies[0].options.signal.aborted, false);
  bodies[0].resolve(new Blob(['thumb'], { type: 'image/webp' }));
  await shared.promise;
  await flush();
  assert.equal(bodies.length, 5);
  assert.match(bodies[4].url, /mixed-limit-4/);
  assert.match(bodies[4].url, /thumbnail=1/);
  for (const lease of leases) lease.release();
  shared.release();
  await settled;
  await flush();
  assert.equal(bodies.length, 5);
  assert.equal(bodies.some(body => body.url.includes('mixed-limit-10')), false);
  assert.ok(bodies.slice(1).every(body => body.options.signal.aborted));
});

test('thumbnail failures stay retryable without fetching full originals as a fallback', async t => {
  const api = await artifacts(t);
  globalThis.fetch = async (url, options) => {
    requests.push({ url, options });
    return requests.length === 1
      ? { ok: false, status: 404, json: async () => ({ error: 'thumbnail_unavailable' }) }
      : blobResponse(new Blob(['retry thumbnail'], { type: 'image/webp' }));
  };
  const first = api.acquireThumbnailObjectUrl('thumbnail-retry', 'image/png');
  await assert.rejects(first.promise, { status: 404, code: 'thumbnail_unavailable' });
  first.release();
  assert.equal(api.created.size, 0);
  assert.equal(requests.length, 1);
  const retry = api.acquireThumbnailObjectUrl('thumbnail-retry', 'image/png');
  await retry.promise;
  retry.release();
  assert.equal(requests.length, 2);
  assert.ok(requests.every(request => request.url.includes('thumbnail=1')));
  assert.equal(api.created.size, 1);
});

test('the shared sixteen-MiB byte cache evicts the least-recently-used unpinned variant before its entry-count bound', async t => {
  const api = await artifacts(t), chunk = new Uint8Array(4 * 1024 * 1024);
  globalThis.fetch = async (url, options) => {
    requests.push({ url, options });
    return blobResponse(new Blob([chunk], { type: url.includes('thumbnail=1') ? 'image/webp' : 'image/png' }));
  };
  const acquire = (uri, thumbnail) => api.acquireArtifactObjectUrl(uri, 'image/png', { thumbnail });
  const entries = [];
  for (const [uri, thumbnail] of [['bytes-a', true], ['bytes-a', false], ['bytes-b', true], ['bytes-b', false]]) {
    const lease = acquire(uri, thumbnail), url = await lease.promise;
    entries.push({ lease, url });
    lease.release();
  }
  assert.equal(api.created.size, 4);
  assert.equal(api.revoked.length, 0);
  const touched = api.acquireThumbnailObjectUrl('bytes-a', 'image/png');
  assert.equal(touched.promise, entries[0].lease.promise);
  touched.release();
  const pressure = api.acquireThumbnailObjectUrl('bytes-c', 'image/png');
  await pressure.promise;
  pressure.release();
  assert.equal(api.created.size, 5);
  assert.deepEqual(api.revoked, [entries[1].url]);
  const cachedThumb = api.acquireThumbnailObjectUrl('bytes-a', 'image/png');
  assert.equal(cachedThumb.promise, entries[0].lease.promise);
  assert.equal(await cachedThumb.promise, entries[0].url);
  cachedThumb.release();
  const evictedOriginal = api.acquireArtifactObjectUrl('bytes-a', 'image/png');
  assert.notEqual(evictedOriginal.promise, entries[1].lease.promise);
  assert.notEqual(await evictedOriginal.promise, entries[1].url);
  evictedOriginal.release();
  assert.equal(requests.length, 6);
  assert.equal(new Set(api.revoked).size, api.revoked.length);
});

test('oversized originals stay pinned alongside thumbnails and are evicted as soon as their last lease releases', async t => {
  const api = await artifacts(t);
  const large = new Blob([new Uint8Array(16 * 1024 * 1024 + 1)], { type: 'image/png' });
  const small = new Blob(['small thumbnail'], { type: 'image/webp' });
  globalThis.fetch = async url => blobResponse(url.includes('thumbnail=1') ? small : large);
  const original = api.acquireArtifactObjectUrl('oversized-pinned', 'image/png');
  const fullUrl = await original.promise;
  const overlay = api.acquireArtifactObjectUrl('oversized-pinned', 'image/png');
  const thumb = api.acquireThumbnailObjectUrl('oversized-pinned', 'image/png');
  const thumbUrl = await thumb.promise;
  assert.equal(overlay.promise, original.promise);
  assert.equal(api.revoked.length, 0);
  original.release();
  assert.equal(api.revoked.length, 0);
  overlay.release();
  overlay.release();
  assert.deepEqual(api.revoked, [fullUrl]);
  const sharedThumb = api.acquireThumbnailObjectUrl('oversized-pinned', 'image/png');
  assert.equal(sharedThumb.promise, thumb.promise);
  assert.equal(await sharedThumb.promise, thumbUrl);
  thumb.release(); sharedThumb.release();
  assert.equal(api.revoked.includes(thumbUrl), false);
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
  for (const path of ['/api', '/api/sessions/fixture/rows/tool', '/api/artifacts?uri=encoded&token=private-device-token',
    '/api/artifacts?uri=encoded&mime=image%2Fpng&thumbnail=1']) {
    const request = { method: 'GET', url: `https://paired.example${path}` };
    let response;
    offline = false;
    handlers.get('fetch')({ request, respondWith: value => { response = value; } });
    assert.equal(await (await response).text(), 'original-bytes');
    assert.equal(fetched.at(-1), request);
    offline = true;
    handlers.get('fetch')({ request, respondWith: value => { response = value; } });
    assert.equal((await response).status, 503);
    assert.deepEqual(await (await response).json(), { error: 'offline' });
  }
});

test('service worker v17 installs the current shell and removes older caches on activation', async () => {
  const handlers = new Map(), opened = [], removed = [], shell = [];
  const source = readFileSync(new URL('../../../src/Alcoler.Remote/wwwroot/sw.js', import.meta.url), 'utf8');
  let skipped = 0, claimed = 0;
  runInNewContext(source, {
    URL, Response, JSON,
    self: { location: { origin: 'https://paired.example' }, addEventListener: (name, fn) => handlers.set(name, fn),
      skipWaiting: async () => skipped++, clients: { claim: async () => claimed++ } },
    caches: {
      open: async name => { opened.push(name); return { addAll: async paths => shell.push(...paths) }; },
      keys: async () => ['alcoler-shell-v15', 'alcoler-shell-v16', 'alcoler-shell-v17'],
      delete: async name => { removed.push(name); return true; },
    },
  });
  let work;
  handlers.get('install')({ waitUntil: promise => { work = promise; } });
  await work;
  assert.deepEqual(opened, ['alcoler-shell-v17']);
  assert.equal(skipped, 1);
  for (const asset of ['./app.js', './api.js', './ws.js', './render.js', './index.html', './styles.css']) assert.ok(shell.includes(asset));
  assert.equal(shell.some(path => path.startsWith('/api')), false);
  handlers.get('activate')({ waitUntil: promise => { work = promise; } });
  await work;
  assert.deepEqual(removed, ['alcoler-shell-v15', 'alcoler-shell-v16']);
  assert.equal(claimed, 1);
});
