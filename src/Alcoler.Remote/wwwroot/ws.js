import { getToken } from './api.js';

const MAX_ATTEMPTS = 8;
const CONNECT_TIMEOUT = 15000;
const HEARTBEAT_TIMEOUT = 45000;
const HISTORY_TIMEOUT = 15000;

function validHistory(history) {
  return history && typeof history === 'object' && ['before', 'after'].every(key =>
    history[key] === null || (typeof history[key] === 'string' && history[key].length > 0));
}

const historyError = code => Object.assign(new Error(code), { code });

export class StreamClient {
  constructor({ sessionId, onSnapshot, onDelta, onStatus, resume, appendText = false, compact = false,
    history = false, anchor, onHistory, suspended = false }) {
    this.sessionId = sessionId;
    this.appendText = appendText;
    this.compact = compact;
    this.history = history;
    this.anchor = anchor;
    this.onSnapshot = onSnapshot;
    this.onDelta = onDelta;
    this.onStatus = onStatus;
    this.onHistory = onHistory;
    this.resume = resume;
    this.status = 'offline';
    this.attempts = 0;
    this.closed = false;
    this.suspended = suspended;
    this.ws = null;
    this.timer = 0;
    this.connectTimer = 0;
    this.heartbeatTimer = 0;
    this.lastMessageAt = 0;
    this.hasSnapshot = false;
    this.hasHistory = false;
    this.historyRequest = null;
    this.nextRequestId = 0;
    this.resuming = null;
  }

  start() {
    if (this.closed || this.suspended || this.ws || this.timer || this.resuming) return;
    this.setStatus('connecting');
    this.open();
  }

  open() {
    clearTimeout(this.timer);
    this.timer = 0;
    if (this.closed || this.suspended) return;
    this.disconnect();
    const token = getToken();
    if (!token) {
      this.setStatus('offline');
      return;
    }
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const around = this.history ? this.anchor?.() : null;
    const url = `${proto}//${location.host}/api/sessions/${encodeURIComponent(this.sessionId)}/stream?token=${encodeURIComponent(token)}${this.appendText ? '&appendText=1' : ''}${this.compact || this.history ? '&compact=1' : ''}${this.history ? '&history=1' : ''}${typeof around === 'string' && around ? '&around=' + encodeURIComponent(around) : ''}`;
    let ws;
    try {
      ws = new WebSocket(url);
    } catch {
      this.scheduleReconnect();
      return;
    }
    this.ws = ws;
    this.lastMessageAt = Date.now();
    this.connectTimer = setTimeout(() => this.reconnectSocket(ws), CONNECT_TIMEOUT);
    ws.onmessage = event => {
      if (this.ws !== ws || this.closed) return;
      this.handleMessage(event.data);
    };
    ws.onclose = () => this.reconnectSocket(ws);
    ws.onerror = () => {
      if (this.ws === ws && !this.closed) this.rejectHistory('history_disconnected');
    };
  }

  handleMessage(data) {
    let msg;
    try { msg = JSON.parse(data); } catch { return; }
    if (!msg || typeof msg !== 'object') return;
    this.lastMessageAt = Date.now();
    switch (msg.type) {
      case 'snapshot':
        if (!msg.snapshot) return;
        clearTimeout(this.connectTimer);
        this.connectTimer = 0;
        this.rejectHistory('stale_history');
        this.hasSnapshot = true;
        this.hasHistory = !!(this.history && validHistory(msg.snapshot.history));
        this.attempts = 0;
        if (this.onSnapshot) this.onSnapshot(msg.snapshot);
        if (this.closed || !this.ws) return;
        this.setStatus('online');
        this.watchHeartbeat();
        break;
      case 'delta':
        if (this.hasSnapshot && this.onDelta) this.onDelta(msg.delta);
        break;
      case 'history':
        this.handleHistory(msg);
        break;
      case 'error':
        this.rejectHistory(msg.code || 'history_error');
        if (msg.code === 'session_not_found') this.handleEnded();
        break;
      case 'ended':
        this.handleEnded();
        break;
      default:
        break;
    }
  }

  requestHistory(request) {
    const keys = request && typeof request === 'object' && !Array.isArray(request) ? Object.keys(request) : [];
    const direction = keys[0];
    const value = keys.length === 1 ? request[direction] : null;
    if (keys.length !== 1 || !(direction === 'latest' ? value === true
      : ['before', 'after'].includes(direction) && typeof value === 'string' && value.length > 0)) {
      return Promise.reject(historyError('invalid_history_request'));
    }
    const ws = this.ws;
    if (this.closed || !this.hasSnapshot || !this.hasHistory || !ws || ws.readyState !== WebSocket.OPEN) {
      return Promise.reject(historyError('history_unavailable'));
    }
    if (this.historyRequest) return Promise.reject(historyError('history_pending'));
    const requestId = ++this.nextRequestId;
    return new Promise((resolve, reject) => {
      const pending = { ws, requestId, request: { [direction]: value }, resolve, reject, timer: 0 };
      this.historyRequest = pending;
      pending.timer = setTimeout(() => {
        if (this.historyRequest === pending) this.rejectHistory('history_timeout');
      }, HISTORY_TIMEOUT);
      try { ws.send(JSON.stringify({ type: 'history', requestId, ...pending.request })); }
      catch (error) { this.rejectHistory(error); }
    });
  }

  handleHistory(msg) {
    const pending = this.historyRequest;
    if (!pending || pending.ws !== this.ws || msg.requestId !== pending.requestId) return;
    if (msg.error) { this.rejectHistory(msg.error); return; }
    const page = msg.page;
    if (!page || !Array.isArray(page.rows) || page.rows.some(row => typeof row?.rowId !== 'string' || !row.rowId)
      || !Number.isSafeInteger(page.sequence) || page.sequence < 0 || !validHistory(page.history)
      || page.direction !== Object.keys(pending.request)[0]) {
      this.rejectHistory('invalid_history_response');
      return;
    }
    try { this.onHistory?.(page, pending.request); }
    catch (error) { this.rejectHistory(error); return; }
    if (this.historyRequest !== pending) return;
    clearTimeout(pending.timer);
    this.historyRequest = null;
    pending.resolve(page);
  }

  rejectHistory(error) {
    const pending = this.historyRequest;
    if (!pending) return;
    this.historyRequest = null;
    clearTimeout(pending.timer);
    pending.reject(typeof error === 'string' ? historyError(error) : error);
  }

  watchHeartbeat() {
    if (this.heartbeatTimer || !this.ws || this.closed) return;
    const ws = this.ws;
    const check = () => {
      this.heartbeatTimer = 0;
      if (this.ws !== ws || this.closed) return;
      const remaining = HEARTBEAT_TIMEOUT - (Date.now() - this.lastMessageAt);
      if (remaining <= 0) {
        this.reconnectSocket(ws);
        return;
      }
      this.heartbeatTimer = setTimeout(check, remaining);
    };
    this.heartbeatTimer = setTimeout(check, HEARTBEAT_TIMEOUT);
  }

  reconnectSocket(ws) {
    if (this.closed || this.ws !== ws) return;
    this.disconnect();
    this.scheduleReconnect();
  }

  async handleEnded() {
    if (this.closed || this.suspended || this.resuming) return;
    this.disconnect();
    clearTimeout(this.timer);
    this.timer = 0;
    this.setStatus('reconnecting');
    const attempt = {};
    this.resuming = attempt;
    try {
      if (this.resume) await this.resume();
    } catch { }
    if (this.closed || this.resuming !== attempt) return;
    this.resuming = null;
    this.scheduleReconnect();
  }

  scheduleReconnect() {
    if (this.closed || this.suspended || this.resuming) return;
    clearTimeout(this.timer);
    this.timer = 0;
    this.attempts++;
    if (this.attempts > MAX_ATTEMPTS) {
      this.setStatus('offline');
      return;
    }
    const base = Math.min(8000, 500 * Math.pow(2, Math.min(this.attempts - 1, 4)));
    const delay = base * (0.75 + Math.random() * 0.5);
    this.setStatus('reconnecting');
    this.timer = setTimeout(() => this.open(), delay);
  }

  setSuspended(suspended) {
    if (this.closed || this.suspended === suspended) return;
    this.suspended = suspended;
    if (!suspended) {
      this.ensureConnected();
      return;
    }
    clearTimeout(this.timer);
    this.timer = 0;
    this.resuming = null;
    this.disconnect();
    this.setStatus('offline');
  }

  ensureConnected() {
    if (this.closed || this.suspended) return;
    if (this.ws && (this.ws.readyState === WebSocket.OPEN || this.ws.readyState === WebSocket.CONNECTING)) {
      const timeout = this.hasSnapshot ? HEARTBEAT_TIMEOUT : CONNECT_TIMEOUT;
      if (Date.now() - this.lastMessageAt < timeout) return;
    }
    this.manualReconnect();
  }

  manualReconnect() {
    if (this.closed || this.suspended) return;
    clearTimeout(this.timer);
    this.timer = 0;
    this.resuming = null;
    this.disconnect();
    this.attempts = 0;
    this.setStatus('connecting');
    this.open();
  }

  disconnect() {
    clearTimeout(this.connectTimer);
    clearTimeout(this.heartbeatTimer);
    this.connectTimer = 0;
    this.heartbeatTimer = 0;
    this.hasSnapshot = false;
    this.hasHistory = false;
    this.rejectHistory('history_disconnected');
    const ws = this.ws;
    this.ws = null;
    if (!ws) return;
    ws.onopen = null;
    ws.onclose = null;
    ws.onmessage = null;
    ws.onerror = null;
    try { ws.close(); } catch { }
  }

  setStatus(status) {
    if (this.status === status) return;
    this.status = status;
    if (this.onStatus) this.onStatus(status);
  }

  close() {
    this.closed = true;
    this.resuming = null;
    clearTimeout(this.timer);
    this.timer = 0;
    this.disconnect();
  }
}
