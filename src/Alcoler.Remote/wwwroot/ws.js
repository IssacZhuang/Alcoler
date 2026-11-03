import { getToken } from './api.js';

const MAX_ATTEMPTS = 8;
const CONNECT_TIMEOUT = 15000;
const HEARTBEAT_TIMEOUT = 45000;

export class StreamClient {
  constructor({ sessionId, onSnapshot, onDelta, onStatus, resume, appendText = false }) {
    this.sessionId = sessionId;
    this.appendText = appendText;
    this.onSnapshot = onSnapshot;
    this.onDelta = onDelta;
    this.onStatus = onStatus;
    this.resume = resume;
    this.status = 'offline';
    this.attempts = 0;
    this.closed = false;
    this.ws = null;
    this.timer = 0;
    this.connectTimer = 0;
    this.heartbeatTimer = 0;
    this.lastMessageAt = 0;
    this.hasSnapshot = false;
    this.resuming = null;
  }

  start() {
    if (this.closed || this.ws || this.timer || this.resuming) return;
    this.setStatus('connecting');
    this.open();
  }

  open() {
    clearTimeout(this.timer);
    this.timer = 0;
    if (this.closed) return;
    this.disconnect();
    const token = getToken();
    if (!token) {
      this.setStatus('offline');
      return;
    }
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const url = `${proto}//${location.host}/api/sessions/${encodeURIComponent(this.sessionId)}/stream?token=${encodeURIComponent(token)}${this.appendText ? '&appendText=1' : ''}`;
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
    ws.onerror = () => { };
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
        this.hasSnapshot = true;
        this.attempts = 0;
        if (this.onSnapshot) this.onSnapshot(msg.snapshot);
        if (this.closed || !this.ws) return;
        this.setStatus('online');
        this.watchHeartbeat();
        break;
      case 'delta':
        if (this.hasSnapshot && this.onDelta) this.onDelta(msg.delta);
        break;
      case 'error':
        if (msg.code === 'session_not_found') this.handleEnded();
        break;
      case 'ended':
        this.handleEnded();
        break;
      default:
        break;
    }
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
    if (this.closed || this.resuming) return;
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
    if (this.closed || this.resuming) return;
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

  ensureConnected() {
    if (this.closed) return;
    if (this.ws && (this.ws.readyState === WebSocket.OPEN || this.ws.readyState === WebSocket.CONNECTING)) {
      const timeout = this.hasSnapshot ? HEARTBEAT_TIMEOUT : CONNECT_TIMEOUT;
      if (Date.now() - this.lastMessageAt < timeout) return;
    }
    this.manualReconnect();
  }

  manualReconnect() {
    if (this.closed) return;
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
