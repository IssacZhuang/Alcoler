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

async function raw(path, { method = 'GET', body, auth = true } = {}) {
  const headers = {};
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (auth) {
    const t = getToken();
    if (t) headers['Authorization'] = `Bearer ${t}`;
  }
  let res;
  try {
    res = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), cache: 'no-store' });
  } catch {
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

export function archiveSession(id, archived = true) {
  return json(`/api/sessions/${encodeURIComponent(id)}/archive`, { method: 'POST', body: { archived } });
}

export function sendCommand(id, command) {
  return json(`/api/sessions/${encodeURIComponent(id)}/commands`, { method: 'POST', body: command });
}

// Artifact bytes need the Bearer header, so <img src> cannot be used directly.
// Fetch with auth, cache the blob object URL, and evict oldest entries at a
// bounded size so long conversations cannot accumulate without end.
const artifactCache = new Map();

export function artifactObjectUrl(uri, mime) {
  if (!uri) return Promise.reject(new ApiError(0, 'invalid', 'Missing artifact URI'));
  let p = artifactCache.get(uri);
  if (!p) {
    if (artifactCache.size >= 80) {
      for (const [key, promise] of artifactCache) {
        artifactCache.delete(key);
        promise.then(u => URL.revokeObjectURL(u)).catch(() => { });
        if (artifactCache.size < 50) break;
      }
    }
    p = (async () => {
      const res = await raw(`/api/artifacts?uri=${encodeURIComponent(uri)}&mime=${encodeURIComponent(mime || '')}`);
      if (!res.ok) throw await errorFrom(res);
      return URL.createObjectURL(await res.blob());
    })();
    p.catch(() => artifactCache.delete(uri));
    artifactCache.set(uri, p);
  }
  return p;
}
