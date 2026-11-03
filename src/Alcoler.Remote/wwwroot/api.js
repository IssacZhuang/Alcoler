// API client for the Alcoler remote gateway. All endpoints are same-origin;
// the device token is carried as a Bearer header everywhere except /api/info
// and /api/pair.

const TOKEN_KEY = 'alcoler.deviceToken';
const DEVICE_ID_KEY = 'alcoler.deviceId';
const NAME_KEY = 'alcoler.deviceName';

export class ApiError extends Error {
  constructor(status, code, message) {
    super(message || (status === 0 ? 'Network unreachable' : `Request failed (${status})`));
    this.status = status;
    this.code = code || null;
  }
}

let unauthorizedListener = null;
export function onUnauthorized(fn) { unauthorizedListener = fn; }

export function getToken() {
  try { return localStorage.getItem(TOKEN_KEY); } catch { return null; }
}

export function getStoredName() {
  try { return localStorage.getItem(NAME_KEY); } catch { return null; }
}

export function setStoredName(name) {
  try { localStorage.setItem(NAME_KEY, name); } catch { /* pairing proceeds without a stored name */ }
}

export function setAuth(deviceToken, deviceId) {
  try {
    localStorage.setItem(TOKEN_KEY, deviceToken);
    if (deviceId) localStorage.setItem(DEVICE_ID_KEY, deviceId);
  } catch { /* storage may be unavailable in private modes; the session just will not survive a reload */ }
}

export function clearAuth() {
  try { localStorage.removeItem(TOKEN_KEY); localStorage.removeItem(DEVICE_ID_KEY); } catch { }
}

async function errorFrom(res) {
  let code = null;
  let message = null;
  try {
    const j = await res.json();
    code = j.code ?? j.error ?? null;
    message = j.message ?? null;
  } catch { /* non-JSON error body */ }
  return new ApiError(res.status, code, message);
}

async function raw(path, { method = 'GET', body, auth = true, signal } = {}) {
  const headers = {};
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (auth) {
    const t = getToken();
    if (t) headers['Authorization'] = `Bearer ${t}`;
  }
  let res;
  try {
    res = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), cache: 'no-store', signal });
  } catch {
    if (signal?.aborted) throw signal.reason;
    throw new ApiError(0, 'network', 'Network unreachable');
  }
  if (res.status === 401 && auth) {
    clearAuth();
    if (unauthorizedListener) unauthorizedListener();
    throw new ApiError(401, 'unauthorized', 'Device not paired');
  }
  return res;
}

async function json(path, opts) {
  const res = await raw(path, opts);
  if (!res.ok) throw await errorFrom(res);
  if (res.status === 204) return null;
  return res.json();
}

export const getInfo = () => json('/api/info', { auth: false });
export const getModels = () => json('/api/models');

export function getSlashCatalog({ workspaceRoot = '', sessionId = null } = {}) {
  return sessionId
    ? json(`/api/sessions/${encodeURIComponent(sessionId)}/slash-catalog`)
    : json(`/api/slash-catalog?workspaceRoot=${encodeURIComponent(workspaceRoot)}`);
}

export function pair(pairingToken, deviceName) {
  return json('/api/pair', { method: 'POST', auth: false, body: { pairingToken, deviceName } });
}

export function listSessions() {
  return json('/api/sessions?limit=100&kind=active');
}

export function createSession(body) {
  return json('/api/sessions', { method: 'POST', body });
}

export function resumeSession(id) {
  return json(`/api/sessions/${encodeURIComponent(id)}/resume`, { method: 'POST' });
}

export function getTurnDetails(id, rowId, options = {}) {
  return transcriptDetails(id, 'turns', rowId, options);
}

export function getRowDetails(id, rowId, options = {}) {
  return transcriptDetails(id, 'rows', rowId, options);
}

function transcriptDetails(id, kind, rowId, { signal, version } = {}) {
  const query = version ? `?version=${encodeURIComponent(version)}` : '';
  return json(`/api/sessions/${encodeURIComponent(id)}/${kind}/${encodeURIComponent(rowId)}${query}`, { signal });
}

export function archiveSession(id, archived = true) {
  return json(`/api/sessions/${encodeURIComponent(id)}/archive`, { method: 'POST', body: { archived } });
}

export function sendCommand(id, command) {
  return json(`/api/sessions/${encodeURIComponent(id)}/commands`, { method: 'POST', body: command });
}

const artifactCache = new Map();
const artifactQueue = [];
const ARTIFACT_CONCURRENCY = 4;
let activeArtifacts = 0;

function artifactPath(uri, mime) {
  if (!uri) throw new ApiError(0, 'invalid', 'Missing artifact URI');
  return `/api/artifacts?uri=${encodeURIComponent(uri)}&mime=${encodeURIComponent(mime || '')}`;
}

export function artifactUrl(uri, mime) {
  const token = getToken();
  if (!token) throw new ApiError(401, 'unauthorized', 'Device not paired');
  return `${artifactPath(uri, mime)}&token=${encodeURIComponent(token)}`;
}

function drainArtifacts() {
  while (activeArtifacts < ARTIFACT_CONCURRENCY && artifactQueue.length) {
    const job = artifactQueue.shift();
    job.signal?.removeEventListener('abort', job.cancel);
    if (job.signal?.aborted) { job.reject(job.signal.reason); continue; }
    activeArtifacts++;
    Promise.resolve().then(job.run).then(job.resolve, job.reject).finally(() => {
      activeArtifacts--;
      drainArtifacts();
    });
  }
}

export function artifactBlob(uri, mime, { signal } = {}) {
  return new Promise((resolve, reject) => {
    const job = { signal, resolve, reject, run: async () => {
      signal?.throwIfAborted();
      const res = await raw(artifactPath(uri, mime), { signal });
      if (!res.ok) throw await errorFrom(res);
      const blob = await res.blob();
      signal?.throwIfAborted();
      return blob;
    } };
    job.cancel = () => {
      const index = artifactQueue.indexOf(job);
      if (index < 0) return;
      artifactQueue.splice(index, 1);
      reject(signal.reason);
    };
    if (signal?.aborted) { reject(signal.reason); return; }
    signal?.addEventListener('abort', job.cancel, { once: true });
    artifactQueue.push(job);
    drainArtifacts();
  });
}

function retireArtifact(uri, entry) {
  if (artifactCache.get(uri) === entry) artifactCache.delete(uri);
  if (!entry.settled) entry.controller.abort();
  if (entry.url) { URL.revokeObjectURL(entry.url); entry.url = null; }
}

function trimArtifacts() {
  if (artifactCache.size <= 80) return;
  for (const [uri, entry] of artifactCache) {
    if (entry.consumers) continue;
    retireArtifact(uri, entry);
    if (artifactCache.size <= 50) break;
  }
}

export function artifactObjectUrl(uri, mime) {
  const lease = acquireArtifactObjectUrl(uri, mime);
  lease.promise.then(lease.release, lease.release);
  return lease.promise;
}

export function acquireArtifactObjectUrl(uri, mime) {
  let entry = artifactCache.get(uri);
  if (!entry) {
    entry = { consumers: 0, settled: false, url: null, controller: new AbortController() };
    entry.promise = artifactBlob(uri, mime, { signal: entry.controller.signal }).then(blob => {
      entry.controller.signal.throwIfAborted();
      entry.url = URL.createObjectURL(blob);
      entry.settled = true;
      return entry.url;
    }).catch(error => {
      entry.settled = true;
      if (artifactCache.get(uri) === entry) artifactCache.delete(uri);
      throw error;
    });
    entry.promise.catch(() => {});
    artifactCache.set(uri, entry);
  }
  entry.consumers++;
  trimArtifacts();
  let released = false;
  return {
    promise: entry.promise,
    release() {
      if (released) return;
      released = true;
      entry.consumers--;
      // A cache slot cannot keep an abandoned request or a detached decoder alive.
      if (!entry.consumers && !entry.settled) retireArtifact(uri, entry);
      else trimArtifacts();
    },
  };
}
