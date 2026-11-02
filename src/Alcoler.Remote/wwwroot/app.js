// Application shell: hash routing (#/, #/session/{id}, #pair={token}),
// pairing, the session list, and the conversation controller.

import * as api from './api.js';
import { StreamClient } from './ws.js';
import { Transcript, renderPending, renderQueue, phaseLabel, isBusy, normKey } from './render.js';
import { icon, esc, uuid, relTime, copyText } from './util.js';

const appEl = document.getElementById('app');
let active = { name: '', key: '', dispose: null };
let pairingToken = null;
let convo = null;
const sessionTitles = new Map();

function $(id) { return document.getElementById(id); }

function mountView(name, key = name) {
  if (active.key === key) return false;
  if (active.dispose) active.dispose();
  active.dispose = null;
  const tpl = $(('view-') + name);
  appEl.replaceChildren(tpl.content.cloneNode(true));
  active = { name, key, dispose: null };
  return true;
}

function route() {
  if (location.hash.startsWith('#pair=')) return;
  if (!api.getToken()) {
    showPair();
    return;
  }
  const m = location.hash.match(/^#\/session\/(.+)$/);
  if (m) {
    const id = decodeURIComponent(m[1]);
    if (id) {
      openSession(id);
      return;
    }
  }
  showList();
}

// ---------- Pairing ----------

function grabPairEls() {
  return {
    status: $('pair-status'), auto: $('pair-auto'), msg: $('pair-msg'),
    retry: $('pair-retry'), form: $('pair-form'), code: $('pair-code'), err: $('pair-error'),
  };
}

function fetchPairStatus(els) {
  api.getInfo().then(info => {
    if (!els.status.isConnected) return;
    const name = (info && info.product) || 'Alcoler';
    const version = info && info.version ? ` ${info.version}` : '';
    els.status.textContent = `${name}${version} — desktop reachable`;
  }).catch(() => {
    if (els.status.isConnected) els.status.textContent = 'Cannot reach the Alcoler desktop app';
  });
}

function showPair(message) {
  mountView('pair');
  const els = grabPairEls();
  els.auto.hidden = true;
  els.retry.hidden = true;
  els.form.hidden = false;
  if (message) {
    els.err.textContent = message;
    els.err.hidden = false;
  } else {
    els.err.hidden = true;
  }
  fetchPairStatus(els);
  els.form.onsubmit = e => {
    e.preventDefault();
    const raw = els.code.value.trim();
    if (!raw) return;
    els.err.hidden = true;
    pairWith(extractToken(raw), els, e.target.querySelector('button[type=submit]'));
  };
}

function extractToken(raw) {
  const m = raw.match(/#pair=([^&\s]+)/);
  return m ? decodeURIComponent(m[1]) : raw;
}

function startAutoPair(token) {
  mountView('pair');
  const els = grabPairEls();
  pairingToken = token;
  els.form.hidden = true;
  els.auto.hidden = false;
  fetchPairStatus(els);
  const attempt = async () => {
    els.msg.textContent = 'Pairing with desktop...';
    els.err.hidden = true;
    els.retry.hidden = true;
    const ok = await pairWith(token, els, null);
    if (pairingToken !== token) return;
    if (ok) return;
    els.msg.textContent = 'Pairing did not complete.';
    els.form.hidden = false;
    els.code.value = token;
  };
  els.retry.onclick = attempt;
  attempt();
}

async function pairWith(token, els, button) {
  const restore = button ? button.textContent : '';
  if (button) {
    button.disabled = true;
    button.textContent = 'Pairing...';
  }
  try {
    const res = await api.pair(token, deviceName());
    pairingToken = null;
    api.setAuth(res.deviceToken, res.deviceId);
    history.replaceState(null, '', location.pathname + location.search);
    location.hash = '#/';
    showList();
    return true;
  } catch (e) {
    if (els.err.isConnected) {
      els.err.textContent = pairErrorMessage(e);
      els.err.hidden = false;
      if (!els.auto.hidden) els.retry.hidden = false;
    }
    return false;
  } finally {
    if (button) {
      button.disabled = false;
      button.textContent = restore;
    }
  }
}

function pairErrorMessage(e) {
  if (e && e.status === 401) return 'That pairing code is invalid or expired.';
  if (e && e.status === 429) return 'Too many pairing attempts. Wait a moment and try again.';
  if (e && e.status === 0) return 'Could not reach the desktop app. Check the connection and try again.';
  return 'Pairing failed. Try again.';
}

function deviceName() {
  const stored = api.getStoredName();
  if (stored) return stored;
  const ua = navigator.userAgent;
  let name = 'Device';
  if (/iPhone/.test(ua)) name = 'iPhone';
  else if (/iPad/.test(ua) || (/Macintosh/.test(ua) && 'ontouchend' in document)) name = 'iPad';
  else if (/Android/.test(ua)) name = 'Android';
  else if (/Windows/.test(ua)) name = 'Windows';
  else if (/Macintosh/.test(ua)) name = 'Mac';
  else if (/Linux/.test(ua)) name = 'Linux';
  api.setStoredName(name);
  return name;
}

// ---------- Session list ----------

function workspaceTail(root) {
  return String(root || '').split(/[\\/]+/).filter(Boolean).pop() || '';
}

function showList() {
  mountView('list');
  wireList();
  refreshList();
}

function wireList() {
  const refresh = $('btn-refresh');
  const newBtn = $('btn-new');
  refresh.innerHTML = icon('refresh');
  newBtn.innerHTML = icon('plus');
  refresh.onclick = refreshList;
  newBtn.onclick = openNewSheet;
  $('new-cancel').onclick = closeNewSheet;
  $('new-sheet').addEventListener('click', e => {
    if (e.target === $('new-sheet')) closeNewSheet();
  });
  $('new-form').onsubmit = submitNewSession;
}

async function refreshList() {
  const content = $('list-content');
  if (!content) return;
  content.innerHTML = '<div class="list-loading"><span class="spinner" aria-hidden="true"></span></div>';
  let sessions;
  try {
    sessions = await api.listSessions();
  } catch (e) {
    if (!content.isConnected) return;
    content.innerHTML = `<div class="list-error"><p>${esc(listErrorMessage(e))}</p><button type="button" class="btn" id="list-retry">Retry</button></div>`;
    const retry = $('list-retry');
    if (retry) retry.onclick = refreshList;
    return;
  }
  if (!content.isConnected) return;
  renderSessions(sessions || []);
}

function listErrorMessage(e) {
  if (e && e.status === 0) return 'Cannot reach the desktop app.';
  return 'Could not load sessions.';
}

function renderSessions(sessions) {
  const content = $('list-content');
  const roots = $('workspace-roots');
  content.textContent = '';
  if (!sessions.length) {
    content.innerHTML = '<div class="list-empty"><p>No active sessions</p><p class="hint">Create a session to start driving Alcoler from this device.</p></div>';
    if (roots) roots.innerHTML = '';
    return;
  }
  sessions.sort((a, b) => (Date.parse(b.updatedAt || 0) || 0) - (Date.parse(a.updatedAt || 0) || 0));
  for (const s of sessions) content.appendChild(sessionRowEl(s));
  if (roots) {
    const distinct = [...new Set(sessions.map(s => s.workspaceRoot).filter(Boolean))];
    roots.innerHTML = distinct.map(r => `<option value="${esc(r)}"></option>`).join('');
  }
}

function sessionRowEl(s) {
  const tail = workspaceTail(s.workspaceRoot);
  const title = s.title || tail || 'Session';
  sessionTitles.set(s.id, title);
  const wrap = document.createElement('div');
  wrap.className = 'session-row-wrap';
  wrap.innerHTML = `
    <button type="button" class="session-row">
      <span class="sr-main"><span class="sr-title">${esc(title)}</span><span class="sr-time">${esc(relTime(s.updatedAt))}</span></span>
      ${tail ? `<span class="sr-tail">${esc(tail)}</span>` : ''}
      ${s.workspaceRoot ? `<span class="sr-path">${esc(s.workspaceRoot)}</span>` : ''}
    </button>
    <button type="button" class="icon-btn sr-archive" aria-label="Archive session" title="Archive">${icon('archive')}</button>`;
  wrap.querySelector('.session-row').addEventListener('click', () => {
    location.hash = '#/session/' + encodeURIComponent(s.id);
  });
  wrap.querySelector('.sr-archive').addEventListener('click', async () => {
    if (!confirm(`Archive "${title}"?`)) return;
    try {
      await api.archiveSession(s.id, true);
    } catch { /* the refresh below surfaces persistent failures */ }
    refreshList();
  });
  return wrap;
}

// ---------- New session sheet ----------

function openNewSheet() {
  $('new-sheet').hidden = false;
  $('new-error').hidden = true;
  setTimeout(() => { $('new-workspace').focus(); }, 50);
}

function closeNewSheet() {
  const sheet = $('new-sheet');
  if (sheet) sheet.hidden = true;
}

async function submitNewSession(e) {
  e.preventDefault();
  const workspace = $('new-workspace').value.trim();
  const title = $('new-title').value.trim();
  const modeEl = document.querySelector('input[name="mode"]:checked');
  const errEl = $('new-error');
  errEl.hidden = true;
  if (!workspace) {
    errEl.textContent = 'Workspace root is required.';
    errEl.hidden = false;
    return;
  }
  const create = $('new-create');
  create.disabled = true;
  try {
    const handle = await api.createSession({ workspaceRoot: workspace, title: title || undefined, mode: modeEl ? modeEl.value : 'build' });
    $('new-workspace').value = '';
    $('new-title').value = '';
    closeNewSheet();
    location.hash = '#/session/' + encodeURIComponent(handle.id);
  } catch (err) {
    errEl.textContent = err && err.status === 0 ? 'Cannot reach the desktop app.' : 'Could not create the session.';
    errEl.hidden = false;
  } finally {
    create.disabled = false;
  }
}

// ---------- Conversation ----------

function openSession(id) {
  if (active.key === 'session:' + id && convo && convo.sessionId === id) return;
  mountView('session', 'session:' + id);
  startConversation(id);
}

function startConversation(id) {
  const els = {
    scroll: $('transcript-scroll'), transcript: $('transcript'),
    title: $('sess-title'), chip: $('phase-chip'), dot: $('conn-dot'), banner: $('conn-banner'),
    jump: $('jump-latest'), queue: $('queue-chips'), pending: $('pending-cards'),
    input: $('composer-input'), send: $('btn-send'), attach: $('btn-attach'), files: $('file-input'),
    attachRow: $('attach-row'), overlay: $('img-overlay'), overlayImg: $('overlay-img'),
    overlayClose: $('overlay-close'), back: $('btn-back'),
  };

  const st = {
    sessionId: id,
    state: null,
    revision: 0,
    lastSeq: null,
    snapshotReady: false,
    images: [],
    sent: new Set(),
    titleFallback: sessionTitles.get(id) || 'Session',
    transcript: new Transcript(els.transcript),
    ws: null,
    lastPendingKey: null,
  };
  convo = st;

  els.back.innerHTML = icon('back');
  els.attach.innerHTML = icon('camera');
  els.overlayClose.innerHTML = icon('close');
  els.jump.innerHTML = icon('chevron-down') + '<span>Jump to latest</span>';
  els.back.onclick = () => { location.hash = '#/'; };

  // Transcript delegation: copy buttons, show-all toggles, image overlays.
  els.transcript.addEventListener('click', async e => {
    const copyBtn = e.target.closest('[data-copy]');
    if (copyBtn) {
      const code = copyBtn.closest('.md-code');
      const pre = code && code.querySelector('code');
      if (!pre) return;
      const ok = await copyText(pre.textContent);
      const old = copyBtn.textContent;
      copyBtn.textContent = ok ? 'Copied' : 'Failed';
      setTimeout(() => { copyBtn.textContent = old; }, 1200);
      return;
    }
    const more = e.target.closest('.out-more');
    if (more) {
      const pre = more.closest('pre');
      const clip = pre && pre.querySelector('.out-clip');
      if (clip && more.dataset.full != null) clip.textContent = more.dataset.full;
      more.remove();
      if (pre) pre.classList.remove('is-clipped');
      return;
    }
    const thumb = e.target.closest('.thumb[data-url]');
    if (thumb) {
      els.overlayImg.src = thumb.dataset.url;
      els.overlay.hidden = false;
    }
  });

  const closeOverlay = () => {
    els.overlay.hidden = true;
    els.overlayImg.src = '';
  };
  els.overlay.addEventListener('click', e => { if (e.target === els.overlay) closeOverlay(); });
  els.overlayClose.onclick = closeOverlay;

  const nearBottom = () => els.scroll.scrollHeight - els.scroll.scrollTop - els.scroll.clientHeight < 80;
  const toBottom = () => { els.scroll.scrollTop = els.scroll.scrollHeight; };
  const updateJump = () => { els.jump.hidden = nearBottom(); };
  els.jump.onclick = toBottom;
  els.scroll.addEventListener('scroll', updateJump, { passive: true });

  // Composer
  const autosize = () => {
    els.input.style.height = 'auto';
    els.input.style.height = Math.min(els.input.scrollHeight, 132) + 'px';
  };
  const desktopKeyboard = () => matchMedia('(pointer: fine)').matches && window.innerWidth >= 700;
  const sendIsStop = () => (st.state ? isBusy(st.state.phase) : false);

  const updateSendMode = () => {
    const stop = sendIsStop();
    els.send.classList.toggle('stop', stop);
    els.send.innerHTML = icon(stop ? 'stop' : 'send');
    els.send.setAttribute('aria-label', stop ? 'Stop' : 'Send');
    const hasContent = els.input.value.trim().length > 0 || st.images.length > 0;
    els.send.disabled = stop ? !st.snapshotReady : (!st.snapshotReady || !hasContent);
  };

  const renderAttachRow = () => {
    els.attachRow.textContent = '';
    els.attachRow.hidden = st.images.length === 0;
    st.images.forEach((p, idx) => {
      const d = document.createElement('div');
      d.className = 'attach-thumb';
      d.innerHTML = `<img src="${esc(p.previewUrl)}" alt=""><button type="button" class="attach-remove" aria-label="Remove attachment">${icon('close')}</button>`;
      d.querySelector('.attach-remove').addEventListener('click', () => {
        URL.revokeObjectURL(p.previewUrl);
        st.images.splice(idx, 1);
        renderAttachRow();
        updateSendMode();
      });
      els.attachRow.appendChild(d);
    });
  };

  const clearImages = () => {
    for (const p of st.images) URL.revokeObjectURL(p.previewUrl);
    st.images = [];
    renderAttachRow();
  };

  const restoreComposer = (text, images) => {
    els.input.value = text;
    autosize();
    st.images = images;
    renderAttachRow();
    updateSendMode();
  };

  const sendCurrent = async () => {
    const text = els.input.value.trim();
    if (!text && !st.images.length) return;
    const images = st.images.map(p => ({ mimeType: p.mimeType, base64Data: p.base64Data }));
    const backupText = els.input.value;
    const backupImages = st.images.slice();
    els.input.value = '';
    autosize();
    clearImages();
    updateSendMode();
    const cmd = { $type: 'send_text', commandId: uuid(), sessionId: id, baseRevision: st.revision, text };
    if (images.length) cmd.images = images;
    let ack = null;
    try {
      ack = await api.sendCommand(id, cmd);
    } catch { ack = null; }
    // On rejection or transport failure the next snapshot is authoritative;
    // the composer is restored so no input is ever lost silently.
    if (!ack || ack.accepted === false) restoreComposer(backupText, backupImages);
  };

  const sendStop = async () => {
    try {
      await api.sendCommand(id, { $type: 'stop', commandId: uuid(), sessionId: id, baseRevision: st.revision });
    } catch { /* the connection banner already reflects reachability */ }
  };

  els.input.addEventListener('input', () => { autosize(); updateSendMode(); });
  els.input.addEventListener('keydown', e => {
    if (e.key === 'Enter' && !e.shiftKey && !e.isComposing && desktopKeyboard()) {
      e.preventDefault();
      sendCurrent();
    }
  });
  els.send.onclick = () => { if (sendIsStop()) sendStop(); else sendCurrent(); };
  els.attach.onclick = () => els.files.click();
  els.files.addEventListener('change', async () => {
    const files = [...els.files.files];
    els.files.value = '';
    for (const f of files) await addImage(f);
    updateSendMode();
  });

  // Photos are downscaled to at most 1568px on the long edge before upload.
  const addImage = async file => {
    if (!file.type.startsWith('image/')) return;
    try {
      const blob = await downscaleImage(file);
      const base64 = await blobToBase64(blob);
      st.images.push({ mimeType: 'image/jpeg', base64Data: base64, previewUrl: URL.createObjectURL(blob) });
      renderAttachRow();
    } catch { /* unreadable file: skip */ }
  };

  const setConn = s => {
    els.dot.className = 'dot ' + s;
    if (s === 'online') {
      els.banner.hidden = true;
      return;
    }
    els.banner.hidden = false;
    els.banner.className = 'conn-banner ' + s;
    if (s === 'offline') {
      els.banner.textContent = 'Offline — tap to reconnect';
      els.banner.onclick = () => { if (st.ws) st.ws.manualReconnect(); };
    } else {
      els.banner.textContent = s === 'connecting' ? 'Connecting...' : 'Reconnecting...';
      els.banner.onclick = null;
    }
  };

  const pendingCtx = {
    sent: st.sent,
    onPermission: (interactionId, outcome) => resolveInteraction(interactionId, { permissionDecision: { outcome } }),
    onAnswer: (interactionId, answers) => resolveInteraction(interactionId, { interactionAnswer: { answers } }),
  };

  const updatePending = () => {
    const s = st.state || {};
    const has = !!(s.pendingPermission || s.pendingInteraction);
    const key = has
      ? JSON.stringify([s.pendingPermission || 0, s.pendingInteraction || 0, [...st.sent].join(',')])
      : '';
    if (key === st.lastPendingKey) return;
    st.lastPendingKey = key;
    renderPending(els.pending, s, pendingCtx);
  };

  const updateChrome = () => {
    const s = st.state || {};
    const title = s.title || st.titleFallback;
    els.title.textContent = title;
    document.title = title + ' — Alcoler';
    const phase = normKey(s.phase || 'idle');
    els.chip.textContent = phaseLabel(phase);
    els.chip.className = 'chip phase-' + String(phase).replace(/[^a-zA-Z0-9_-]/g, '');
    renderQueue(els.queue, s.queue);
    updatePending();
  };

  const applySnapshot = snap => {
    if (!snap || convo !== st) return;
    st.snapshotReady = true;
    if (typeof snap.revision === 'number') st.revision = snap.revision;
    if (typeof snap.sequence === 'number') st.lastSeq = snap.sequence;
    st.state = snap.state || {};
    const stick = st.transcript.els.size === 0 || nearBottom();
    st.transcript.rebuild(snap.rows || []);
    updateChrome();
    if (stick) toBottom();
    updateJump();
    updateSendMode();
  };

  const applyDelta = d => {
    if (!d || convo !== st) return;
    // Stale deltas (sequence already reflected) are dropped; the fresh
    // snapshot after any reconnect is the truth.
    if (typeof d.sequence === 'number' && st.lastSeq != null && d.sequence <= st.lastSeq) return;
    if (typeof d.sequence === 'number') st.lastSeq = d.sequence;
    const stick = nearBottom();
    for (const op of d.ops || []) {
      if (!op) continue;
      if (op.$type === 'row_appended' && op.row) st.transcript.append(op.row);
      else if (op.$type === 'row_upserted' && op.row) st.transcript.upsert(op.row);
      else if (op.$type === 'row_removed') st.transcript.remove(op.rowId);
      else if (op.$type === 'state_patch') {
        st.state = op.state || st.state;
        updateChrome();
        updateSendMode();
      }
    }
    if (stick) toBottom();
    updateJump();
  };

  const resolveInteraction = async (interactionId, extra) => {
    if (st.sent.has(interactionId)) return;
    st.sent.add(interactionId);
    updatePending();
    try {
      await api.sendCommand(id, {
        $type: 'resolve_interaction', commandId: uuid(), sessionId: id,
        baseRevision: st.revision, interactionId, ...extra,
      });
    } catch { /* the cards clear when the next state patch lands */ }
  };

  st.ws = new StreamClient({
    sessionId: id,
    onSnapshot: applySnapshot,
    onDelta: applyDelta,
    onStatus: setConn,
    resume: () => api.resumeSession(id),
  });
  st.ws.start();

  updateSendMode();

  active.dispose = () => {
    if (convo === st) convo = null;
    if (st.ws) st.ws.close();
    st.transcript.dispose();
    for (const p of st.images) URL.revokeObjectURL(p.previewUrl);
    document.title = 'Alcoler Remote';
  };
}

function downscaleImage(file) {
  const MAX = 1568;
  return loadImage(file).then(img => {
    const scale = Math.min(1, MAX / Math.max(img.width, img.height));
    const w = Math.max(1, Math.round(img.width * scale));
    const h = Math.max(1, Math.round(img.height * scale));
    const canvas = document.createElement('canvas');
    canvas.width = w;
    canvas.height = h;
    canvas.getContext('2d').drawImage(img, 0, 0, w, h);
    return new Promise((resolve, reject) => {
      canvas.toBlob(b => { b ? resolve(b) : reject(new Error('encode failed')); }, 'image/jpeg', 0.85);
    });
  });
}

function loadImage(file) {
  if (window.createImageBitmap) {
    return createImageBitmap(file, { imageOrientation: 'from-image' })
      .catch(() => loadViaElement(file));
  }
  return loadViaElement(file);
}

function loadViaElement(file) {
  return new Promise((resolve, reject) => {
    const url = URL.createObjectURL(file);
    const img = new Image();
    img.onload = () => {
      resolve(img);
      setTimeout(() => URL.revokeObjectURL(url), 5000);
    };
    img.onerror = () => {
      URL.revokeObjectURL(url);
      reject(new Error('decode failed'));
    };
    img.src = url;
  });
}

function blobToBase64(blob) {
  return new Promise((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(String(r.result).split(',')[1] || '');
    r.onerror = () => reject(r.error);
    r.readAsDataURL(blob);
  });
}

// ---------- Global wiring ----------

window.addEventListener('hashchange', route);

document.addEventListener('keydown', e => {
  if (e.key !== 'Escape') return;
  const overlay = $('img-overlay');
  if (overlay && !overlay.hidden) {
    overlay.hidden = true;
    const img = overlay.querySelector('img');
    if (img) img.src = '';
    return;
  }
  closeNewSheet();
});

window.addEventListener('online', () => {
  if (convo && convo.ws && convo.ws.status === 'offline') convo.ws.manualReconnect();
});

document.addEventListener('visibilitychange', () => {
  if (!document.hidden && convo && convo.ws && convo.ws.status === 'offline') convo.ws.manualReconnect();
});

window.addEventListener('load', () => {
  // Service workers require a secure context; on plain-HTTP LAN origins the
  // registration simply fails and the app keeps running online-only.
  if (!('serviceWorker' in navigator)) return;
  navigator.serviceWorker.register('sw.js').catch(() => { });
});

(function init() {
  api.onUnauthorized(() => {
    pairingToken = null;
    showPair('Device pairing expired. Scan the QR code again.');
  });
  const m = location.hash.match(/^#pair=([^&]*)/);
  if (m) {
    const token = decodeURIComponent(m[1]);
    if (token) {
      startAutoPair(token);
      return;
    }
    history.replaceState(null, '', location.pathname + location.search);
  }
  if (!api.getToken()) {
    showPair();
    return;
  }
  route();
})();
