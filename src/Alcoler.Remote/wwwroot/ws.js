// Session stream client. Reconnects with exponential backoff and jitter
// (0.5s -> 1 -> 2 -> 4 -> 8s cap). Every reconnect receives a fresh snapshot,
// which the caller must treat as the truth.

import { getToken } from './api.js';

const MAX_ATTEMPTS = 8;

export class StreamClient {
  constructor({ sessionId, onSnapshot, onDelta, onStatus, resume }) {
    this.sessionId = sessionId;
    this.onSnapshot = onSnapshot;
    this.onDelta = onDelta;
    this.onStatus = onStatus;
    this.resume = resume;
    this.status = 'offline';
    this.attempts = 0;
    this.closed = false;
    this.ws = null;
    this.timer = 0;
    this.resuming = false;
  }

  start() {
    this.setStatus('connecting');
    this.open();
  }

  open() {
    const token = getToken();
    if (!token || this.closed) {
      this.setStatus('offline');
      return;
    }
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const url = `${proto}//${location.host}/api/sessions/${encodeURIComponent(this.sessionId)}/stream?token=${encodeURIComponent(token)}`;
    let ws;
    try {
      ws = new WebSocket(url);
    } catch {
      this.scheduleReconnect();
      return;
    }
    this.ws = ws;
    const openTimer = setTimeout(() => { if (ws.readyState === WebSocket.CONNECTING) ws.close(); }, 15000);
    ws.onopen = () => {
      clearTimeout(openTimer);
      this.attempts = 0;
      this.setStatus('online');
    };
    ws.onmessage = ev => this.handleMessage(ev.data);
    ws.onclose = () => {
      clearTimeout(openTimer);
      if (!this.closed) this.scheduleReconnect();
    };
    ws.onerror = () => { /* onclose always follows */ };
  }

  handleMessage(data) {
    let msg;
    try {
      msg = JSON.parse(data);
    } catch {
      return;
    }
    switch (msg.type) {
      case 'snapshot':
        this.attempts = 0;
        if (this.onSnapshot) this.onSnapshot(msg.snapshot);
        break;
      case 'delta':
        if (this.onDelta) this.onDelta(msg.delta);
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

  // The server ended or lost the session: resume it, then reconnect so a
  // fresh snapshot arrives.
  async handleEnded() {
    if (this.closed || this.resuming) return;
    this.resuming = true;
    this.setStatus('reconnecting');
    try {
      if (this.resume) await this.resume();
    } catch { /* the next snapshot attempt surfaces any real failure */ }
    this.resuming = false;
    if (this.closed) return;
    this.attempts = 0;
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.open(), 400);
  }

  scheduleReconnect() {
    if (this.closed) return;
    this.attempts++;
    if (this.attempts > MAX_ATTEMPTS) {
      // Give up until the user taps the banner or the app foregrounds.
      this.setStatus('offline');
      return;
    }
    const base = Math.min(8000, 500 * Math.pow(2, Math.min(this.attempts - 1, 4)));
    const delay = base * (0.75 + Math.random() * 0.5);
    this.setStatus('reconnecting');
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.open(), delay);
  }

  manualReconnect() {
    if (this.closed) return;
    clearTimeout(this.timer);
    this.attempts = 0;
    this.setStatus('connecting');
    this.open();
  }

  setStatus(s) {
    if (this.status === s) return;
    this.status = s;
    if (this.onStatus) this.onStatus(s);
  }

  close() {
    this.closed = true;
    clearTimeout(this.timer);
    if (this.ws) {
      this.ws.onclose = null;
      this.ws.onmessage = null;
      this.ws.onerror = null;
      try { this.ws.close(); } catch { }
      this.ws = null;
    }
  }
}
