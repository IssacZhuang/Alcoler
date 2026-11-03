import assert from 'node:assert/strict';
import { afterEach, beforeEach, test } from 'node:test';
import { getSlashCatalog } from '../../../src/Alcoler.Remote/wwwroot/api.js';

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
