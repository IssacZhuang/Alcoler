import * as api from './api.js';
import { StreamClient } from './ws.js';
import {
  Transcript,
  renderPending,
  phaseLabel,
  activityLabel,
  isBusy,
  normKey,
} from './render.js';
import { SessionList, sessionListErrorMessage } from './session-list.js';
import { Composer } from './composer.js';
import { QueuedMessages } from './queue.js';
import { prepareSubmission } from './submission.js';
import { icon, uuid, copyText, workspaceTail } from './util.js';

const appEl = document.getElementById('app');
const desktopMq = matchMedia('(min-width: 900px)');
const WORKSPACE_KEY = 'alcoler.lastWorkspace';
const MODE_KEY = 'alcoler.lastMode';

const sessionTitles = new Map();
const pendingRestores = new Map();

let pairingToken = null;
let shell = null;
let convo = null;
let pendingDraftFocus = false;

function $(id) { return document.getElementById(id); }

function grabPairEls() {
  return {
    status: $('pair-status'), auto: $('pair-auto'), msg: $('pair-msg'),
    retry: $('pair-retry'), form: $('pair-form'), code: $('pair-code'), err: $('pair-error'),
  };
}

function mountPairView() {
  if (shell) disposeShell();
  const template = $('view-pair');
  if (!template) return;
  appEl.replaceChildren(template.content.cloneNode(true));
  document.title = 'Alcoler Remote';
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
  mountPairView();
  const els = grabPairEls();
  if (!els.status) return;
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
  mountPairView();
  const els = grabPairEls();
  if (!els.status) return;
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
    ensureShell();
    if (location.hash !== '#/') location.hash = '#/';
    route();
    return true;
  } catch (e) {
    if (els && els.err && els.err.isConnected) {
      els.err.textContent = pairErrorMessage(e);
      els.err.hidden = false;
      if (els.auto && !els.auto.hidden) els.retry.hidden = false;
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

function ensureShell() {
  if (shell) return shell;
  const template = $('view-shell');
  if (!template) return null;
  appEl.replaceChildren(template.content.cloneNode(true));
  shell = createShell();
  if (shell) refreshSessions({ silent: false });
  return shell;
}

function createShell() {
  const sh = {
    sessions: [],
    roots: [],
    listState: 'idle',
    lastSignature: '',
    pollTimer: 0,
    listRefreshTimer: 0,
    refreshSeq: 0,
    refreshInFlight: false,
    modelRefreshInFlight: false,
    modelsFetchedAt: 0,
    modelCatalogSignature: '',
    draftWorkspace: '',
    draftMode: 'yolo',
    draftPlanEnabled: false,
    draftWorkflows: [],
    draftLiteralText: null,
    draftSending: false,
    workspaceMenuOpen: false,
    draftComposer: null,
    sessionComposer: null,
    list: null,
  };

  wireShellIcons();
  wireWorkspaceMenu(sh);

  sh.list = new SessionList({
    container: $('session-list'),
    onOpen: id => navigateToSession(id),
    onArchive: async id => {
      try {
        await api.archiveSession(id, true);
      } catch { /* the following refresh reflects the real state */ }
      scheduleListRefresh(0);
    },
    onNewTask: root => goNewTask(root, true),
    onRefresh: () => refreshSessions({ silent: false }),
  });

  sh.draftComposer = new Composer(document.querySelector('[data-composer="draft"]'), {
    getBusy: () => false,
    canStop: () => false,
    onSend: (text, images, selection) => sendDraft(sh, text, images, selection),
    onStop: () => { },
    onModeChange: mode => {
      sh.draftMode = mode;
      storageSet(MODE_KEY, mode);
    },
    onChange: () => {
      if (sh.draftComposer && sh.draftComposer.getText() !== sh.draftLiteralText) sh.draftLiteralText = null;
    },
  });

  sh.sessionComposer = new Composer(document.querySelector('[data-composer="session"]'), {
    getBusy: () => !!(convo && convo.state && isBusy(convo.state.phase)),
    canStop: () => !!(convo && convo.snapshotReady),
    onSend: (text, images, selection) => sendSessionMessage(text, images, selection),
    onStop: () => sendSessionStop(),
    onModeChange: mode => sendSessionMode(mode),
    onModelSelectionChange: selection => {
      if (convo?.draftIntent) convo.draftIntent.modelSelection = selection;
    },
    onChange: () => {
      if (!convo) return;
      if (sh.sessionComposer && sh.sessionComposer.getText() !== convo.literalText) convo.literalText = null;
      if (convo.draftIntent && sh.sessionComposer && !sh.sessionComposer.hasContent() && !sh.sessionComposer.disabled) {
        convo.draftIntent = null;
        sh.sessionComposer.setMode(modeValue(convo.state?.mode));
        sh.sessionComposer.setPlan(convo.localPlanEnabled ?? !!convo.state?.planEnabled);
        sh.sessionComposer.setWorkflows(convo.state?.activeWorkflows);
      }
      if (convo.queueView) convo.queueView.refresh();
      if (convo.updateConn) convo.updateConn();
    },
  });

  const storedMode = storageGet(MODE_KEY);
  sh.draftMode = ['build', 'edit', 'yolo'].includes(storedMode) ? storedMode : 'yolo';
  sh.draftWorkspace = storageGet(WORKSPACE_KEY).trim();
  sh.draftComposer.setMode(sh.draftMode);
  sh.draftComposer.setReady(true);
  sh.draftComposer.setPlan(false);
  sh.sessionComposer.setReady(false);
  renderWorkspace(sh);

  startPolling(sh);
  return sh;
}

function wireShellIcons() {
  const refreshButtons = [$('btn-sb-refresh'), $('btn-m-refresh')];
  for (const button of refreshButtons) {
    if (!button) continue;
    button.innerHTML = icon('refresh');
    button.addEventListener('click', () => refreshSessions({ silent: false }));
  }
  const mobileNew = $('btn-m-new');
  if (mobileNew) {
    mobileNew.innerHTML = icon('plus');
    mobileNew.addEventListener('click', () => goNewTask(null, true));
  }
  const newTask = $('btn-new-task');
  if (newTask) {
    const iconEl = newTask.querySelector('.new-task-icon');
    if (iconEl) iconEl.innerHTML = icon('plus');
    newTask.addEventListener('click', () => goNewTask(null, true));
  }
  const draftBack = $('btn-draft-back');
  if (draftBack) {
    draftBack.innerHTML = icon('back');
    draftBack.addEventListener('click', () => { location.hash = '#/'; });
  }
  const back = $('btn-back');
  if (back) {
    back.innerHTML = icon('back');
    back.addEventListener('click', () => { location.hash = '#/'; });
  }
  const jump = $('jump-latest');
  if (jump) jump.innerHTML = icon('chevron-down') + '<span>Jump to latest</span>';
  const overlayClose = $('overlay-close');
  if (overlayClose) overlayClose.innerHTML = icon('close');
  const workspaceButton = $('btn-workspace');
  if (workspaceButton) {
    const iconEl = workspaceButton.querySelector('.workspace-icon');
    if (iconEl) iconEl.innerHTML = icon('folder');
    const chevron = workspaceButton.querySelector('.workspace-chevron');
    if (chevron) chevron.innerHTML = icon('chevron-down');
  }
}

function disposeShell() {
  const sh = shell;
  if (!sh) {
    document.title = 'Alcoler Remote';
    return;
  }
  stopPolling(sh);
  if (sh.listRefreshTimer) clearTimeout(sh.listRefreshTimer);
  disposeConversation();
  for (const drafts of pendingRestores.values()) {
    for (const restore of drafts) {
      for (const image of restore.images || []) {
        if (image?.previewUrl) URL.revokeObjectURL(image.previewUrl);
      }
    }
  }
  pendingRestores.clear();
  if (sh.draftComposer) sh.draftComposer.dispose();
  if (sh.sessionComposer) sh.sessionComposer.dispose();
  if (sh.list) sh.list.dispose();
  shell = null;
  pendingDraftFocus = false;
  document.title = 'Alcoler Remote';
}

function isRootHash() {
  const hash = location.hash;
  return hash === '' || hash === '#' || hash === '#/';
}

function route() {
  if (location.hash.startsWith('#pair=')) return;
  if (!api.getToken()) {
    showPair();
    return;
  }
  if (!ensureShell()) return;
  const match = location.hash.match(/^#\/session\/(.+)$/);
  if (match) {
    let id = '';
    try { id = decodeURIComponent(match[1]); } catch { id = ''; }
    if (id) {
      openSession(id, takePendingRestore(id));
      return;
    }
  }
  if (location.hash === '#/new') {
    showDraft();
    return;
  }
  if (desktopMq.matches) showDraft();
  else showList();
}

function setShellRoute(name) {
  const shellEl = $('shell');
  if (shellEl) shellEl.dataset.route = name;
}

function showDraft() {
  const sh = shell;
  if (!sh) return;
  disposeConversation();
  setShellRoute('draft');
  sh.list.setActive(null);
  if (pendingDraftFocus) {
    pendingDraftFocus = false;
    setTimeout(() => {
      if (shell === sh && sh.draftComposer) sh.draftComposer.focus();
    }, 20);
  }
}

function showList() {
  const sh = shell;
  if (!sh) return;
  disposeConversation();
  setShellRoute('list');
  sh.list.setActive(null);
}

function goNewTask(workspaceRoot, focus) {
  if (!api.getToken()) return;
  const sh = ensureShell();
  if (!sh) return;
  if (workspaceRoot) setDraftWorkspace(sh, workspaceRoot);
  pendingDraftFocus = !!focus;
  if (location.hash === '#/new') showDraft();
  else location.hash = '#/new';
}

function navigateToSession(id, restore) {
  if (restore) savePendingRestore(id, restore);
  const hash = '#/session/' + encodeURIComponent(id);
  if (location.hash === hash) route();
  else location.hash = hash;
}

function savePendingRestore(id, restore) {
  const drafts = pendingRestores.get(id) || [];
  drafts.push(restore);
  pendingRestores.set(id, drafts);
  if (convo?.sessionId === id && convo.updateConn) convo.updateConn();
}

function takePendingRestore(id) {
  const composer = shell?.sessionComposer;
  if (convo?.sessionId === id && composer &&
      (composer.hasContent() || composer.pendingFiles > 0 || composer.disabled)) return null;
  const drafts = pendingRestores.get(id);
  const restore = drafts?.shift();
  if (!drafts?.length) pendingRestores.delete(id);
  return restore;
}

function onViewportChange() {
  if (!shell) return;
  if (isRootHash()) route();
}

function onVisibilityChange() {
  if (!shell) return;
  if (document.hidden) {
    stopPolling(shell);
    return;
  }
  startPolling(shell);
  refreshSessions({ silent: true });
  if (convo && convo.ws) convo.ws.ensureConnected();
  if (convo && convo.refreshLayout) convo.refreshLayout();
}

function onOnline() {
  if (convo && convo.ws) convo.ws.ensureConnected();
  if (shell) refreshSessions({ silent: true });
}

async function refreshModels(sh, force = false) {
  if (sh.modelRefreshInFlight || (!force && Date.now() - sh.modelsFetchedAt < 30_000)) return;
  sh.modelRefreshInFlight = true;
  try {
    const catalog = await api.getModels();
    if (shell !== sh) return;
    sh.modelsFetchedAt = Date.now();
    const signature = JSON.stringify(catalog);
    if (signature === sh.modelCatalogSignature) return;
    sh.modelCatalogSignature = signature;
    sh.draftComposer.setModels(catalog);
    sh.sessionComposer.setModels(catalog);
  } catch {
    if (shell === sh && !sh.modelCatalogSignature) {
      sh.draftComposer.setModels(null);
      sh.sessionComposer.setModels(null);
    }
  } finally {
    sh.modelRefreshInFlight = false;
  }
}

async function refreshSessions({ silent = false } = {}) {
  const sh = shell;
  if (!sh || !api.getToken()) return;
  refreshModels(sh, !silent);
  if (sh.refreshInFlight) return;
  sh.refreshInFlight = true;
  const seq = ++sh.refreshSeq;
  if (!silent || sh.listState !== 'ready') {
    sh.listState = 'loading';
    sh.lastSignature = '';
    sh.list.showLoading();
  }
  let sessions;
  try {
    sessions = await api.listSessions();
  } catch (e) {
    if (!shell || shell !== sh || seq !== sh.refreshSeq) return;
    if (!silent || sh.listState !== 'ready') {
      sh.listState = 'error';
      sh.lastSignature = '';
      sh.list.showError(sessionListErrorMessage(e));
    }
    return;
  } finally {
    sh.refreshInFlight = false;
  }
  if (!shell || shell !== sh || seq !== sh.refreshSeq) return;
  sessions = Array.isArray(sessions) ? sessions : [];
  sh.sessions = sessions;
  updateSessionTitles(sessions);
  updateRoots(sh, sessions);
  const signature = sessionSignature(sessions);
  if (signature !== sh.lastSignature || sh.listState !== 'ready') {
    sh.lastSignature = signature;
    sh.listState = 'ready';
    sh.list.setSessions(sessions);
    sh.list.setActive(convo ? convo.sessionId : null);
  }
}

function sessionSignature(sessions) {
  return JSON.stringify(sessions.map(session => [
    session && session.id,
    session && session.updatedAt,
    session && session.title,
    session && session.workspaceRoot,
    session && session.activity && session.activity.phase,
    session && session.activity && session.activity.lastOutcome,
  ]));
}

function updateSessionTitles(sessions) {
  for (const session of sessions) {
    if (!session || !session.id) continue;
    sessionTitles.set(session.id, String(session.title || '').trim() || 'Session');
  }
}

function scheduleListRefresh(delay = 400) {
  const sh = shell;
  if (!sh) return;
  if (sh.listRefreshTimer) clearTimeout(sh.listRefreshTimer);
  sh.listRefreshTimer = setTimeout(() => {
    sh.listRefreshTimer = 0;
    if (shell === sh) refreshSessions({ silent: true });
  }, delay);
}

function startPolling(sh) {
  if (!sh || sh.pollTimer || document.hidden) return;
  sh.pollTimer = setInterval(() => {
    if (document.hidden) return;
    refreshSessions({ silent: true });
  }, 5000);
}

function stopPolling(sh) {
  if (!sh || !sh.pollTimer) return;
  clearInterval(sh.pollTimer);
  sh.pollTimer = 0;
}

function storageGet(key) {
  try { return localStorage.getItem(key) || ''; } catch { return ''; }
}

function storageSet(key, value) {
  try { localStorage.setItem(key, value); } catch { /* storage may be unavailable */ }
}

function updateRoots(sh, sessions) {
  const seen = new Set();
  const roots = [];
  for (const session of sessions) {
    const root = String((session && session.workspaceRoot) || '').trim();
    if (!root) continue;
    const key = root.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    roots.push(root);
  }
  roots.sort((a, b) => {
    const an = (workspaceTail(a) || a).toLowerCase();
    const bn = (workspaceTail(b) || b).toLowerCase();
    return an.localeCompare(bn) || a.toLowerCase().localeCompare(b.toLowerCase());
  });
  sh.roots = roots;
  if (!sh.draftWorkspace && roots.length) {
    setDraftWorkspace(sh, roots[0]);
  } else {
    renderWorkspace(sh);
  }
  if (sh.workspaceMenuOpen) renderWorkspaceOptions(sh);
}

function setDraftWorkspace(sh, root) {
  sh.draftWorkspace = String(root || '').trim();
  storageSet(WORKSPACE_KEY, sh.draftWorkspace);
  renderWorkspace(sh);
  hideDraftError();
}

function renderWorkspace(sh) {
  const workspaceRoot = sh.draftWorkspace;
  sh.draftComposer.setSlashSource(`draft:${workspaceRoot}`, () => api.getSlashCatalog({ workspaceRoot }));
  const button = $('btn-workspace');
  const name = $('workspace-name');
  if (!button || !name) return;
  const root = sh.draftWorkspace;
  name.textContent = root ? (workspaceTail(root) || root) : 'Choose workspace';
  name.title = root || 'Choose a workspace folder';
  button.title = root || 'Choose a workspace folder';
  button.classList.toggle('is-unset', !root);
  button.setAttribute('aria-label', root ? `Workspace: ${root}` : 'Choose workspace');
}

function wireWorkspaceMenu(sh) {
  const button = $('btn-workspace');
  const menu = $('workspace-menu');
  const close = $('workspace-close');
  const form = $('workspace-manual-form');
  const input = $('workspace-manual-input');
  if (button) button.addEventListener('click', () => openWorkspaceMenu(sh));
  if (close) close.addEventListener('click', () => closeWorkspaceMenu(sh));
  if (menu) {
    menu.addEventListener('click', event => {
      if (event.target === menu) closeWorkspaceMenu(sh);
    });
  }
  if (form) {
    form.addEventListener('submit', event => {
      event.preventDefault();
      const value = input ? input.value.trim() : '';
      if (!value) {
        showDraftError('Enter a workspace folder path.');
        if (input) input.focus();
        return;
      }
      setDraftWorkspace(sh, value);
      closeWorkspaceMenu(sh);
    });
  }
}

function openWorkspaceMenu(sh) {
  const menu = $('workspace-menu');
  if (!menu) return;
  renderWorkspaceOptions(sh);
  menu.hidden = false;
  sh.workspaceMenuOpen = true;
}

function closeWorkspaceMenu(sh) {
  const menu = $('workspace-menu');
  if (menu) menu.hidden = true;
  sh.workspaceMenuOpen = false;
}

function renderWorkspaceOptions(sh) {
  const container = $('workspace-options');
  if (!container) return;
  container.textContent = '';
  if (!sh.roots.length) {
    const empty = document.createElement('p');
    empty.className = 'workspace-empty';
    empty.textContent = 'No known workspaces yet. Enter a folder path below.';
    container.appendChild(empty);
    return;
  }
  for (const root of sh.roots) {
    const option = document.createElement('button');
    option.type = 'button';
    option.className = 'workspace-option' + (root === sh.draftWorkspace ? ' is-selected' : '');
    const folder = document.createElement('span');
    folder.className = 'workspace-option-icon';
    folder.innerHTML = icon('folder');
    const body = document.createElement('span');
    body.className = 'workspace-option-body';
    const name = document.createElement('span');
    name.className = 'workspace-option-name';
    name.textContent = workspaceTail(root) || root;
    const path = document.createElement('span');
    path.className = 'workspace-option-path';
    path.textContent = root;
    body.append(name, path);
    option.append(folder, body);
    option.addEventListener('click', () => {
      setDraftWorkspace(sh, root);
      closeWorkspaceMenu(sh);
      if (sh.draftComposer) sh.draftComposer.focus();
    });
    container.appendChild(option);
  }
}

function showDraftError(message) {
  const el = $('draft-error');
  if (!el) return;
  el.textContent = message;
  el.hidden = false;
}

function hideDraftError() {
  const el = $('draft-error');
  if (!el) return;
  el.hidden = true;
  el.textContent = '';
}

function setDraftSending(sh, sending) {
  const overlay = $('draft-sending');
  if (overlay) overlay.hidden = !sending;
  if (sh.draftComposer) sh.draftComposer.setDisabled(sending);
}

async function sendDraft(sh, text, images, modelSelection) {
  if (sh.draftSending) return;
  let message = String(text || '').trim();
  const imageList = Array.isArray(images) ? images : [];
  if (!message && !imageList.length) return;
  const workspaceRoot = sh.draftWorkspace;
  hideDraftError();
  sh.draftSending = true;
  setDraftSending(sh, true);
  try {
    const parsed = sh.draftLiteralText === message ? null : await sh.draftComposer.parseCommand(message);
    if (shell !== sh || sh.draftWorkspace !== workspaceRoot) return;
    const prepared = prepareSubmission(message, parsed, {
      planEnabled: sh.draftPlanEnabled,
      workflows: sh.draftWorkflows,
    });
    if (parsed) {
      message = prepared.text;
      sh.draftPlanEnabled = prepared.planEnabled;
      sh.draftWorkflows = prepared.workflows || [];
      sh.draftComposer.setPlan(sh.draftPlanEnabled);
      sh.draftComposer.setWorkflows(sh.draftWorkflows);
      sh.draftComposer.setText(message);
      sh.draftLiteralText = message || null;
      if (prepared.action !== 'send') return;
    }
    if (!workspaceRoot) {
      showDraftError('Choose a workspace before sending.');
      openWorkspaceMenu(sh);
      return;
    }

    let handle;
    try {
      handle = await api.createSession({
        workspaceRoot,
        mode: sh.draftMode || 'yolo',
        planEnabled: sh.draftPlanEnabled,
        activeWorkflows: sh.draftWorkflows,
      });
    } catch (e) {
      if (shell === sh) showDraftError(e?.status === 0 ? 'Cannot reach the desktop app.' : 'Could not create the session.');
      return;
    }
    if (shell !== sh) return;
    if (!handle?.id) {
      showDraftError('Could not create the session.');
      return;
    }

    sessionTitles.set(handle.id, String(handle.title || '').trim() || 'Session');
    const uploads = imageList
      .map(image => ({ mimeType: image.mimeType || 'image/jpeg', base64Data: image.base64Data || '' }))
      .filter(image => image.base64Data);
    const command = {
      $type: 'send_text',
      commandId: uuid(),
      sessionId: handle.id,
      baseRevision: 0,
      text: message,
      planEnabled: sh.draftPlanEnabled,
    };
    if (modelSelection) command.modelSelection = modelSelection;
    if (uploads.length) command.images = uploads;

    let ack = null;
    let sendError = null;
    try {
      ack = await api.sendCommand(handle.id, command);
    } catch (e) {
      sendError = e;
    }
    if (shell !== sh) return;
    const planEnabled = sh.draftPlanEnabled;
    const workflows = sh.draftWorkflows;
    sh.draftPlanEnabled = false;
    sh.draftWorkflows = [];
    sh.draftComposer.setPlan(false);
    sh.draftComposer.setWorkflows([]);

    if (ack && ack.accepted !== false) {
      sh.draftComposer.clear();
      hideDraftError();
      scheduleListRefresh(200);
      navigateToSession(handle.id);
      return;
    }

    const literalText = sh.draftLiteralText;
    const restorableImages = sh.draftComposer.takeImages();
    sh.draftComposer.setText('');
    const error = sendError?.status === 0
      ? 'Could not reach the desktop app. Your message was restored here so you can retry.'
      : 'The desktop did not accept the message. It was restored here so you can retry.';
    navigateToSession(handle.id, {
      text: message, images: restorableImages, modelSelection, error, literalText,
      intent: { mode: null, planEnabled, modelSelection, delivery: 'Queue', workflows },
    });
  } catch (e) {
    if (shell === sh) showDraftError(e?.message || 'Commands are unavailable. Refresh and try again.');
  } finally {
    if (shell === sh) {
      sh.draftSending = false;
      setDraftSending(sh, false);
    }
  }
}

function openSession(id, restore) {
  const sh = ensureShell();
  if (!sh) return;
  if (convo && convo.sessionId === id) {
    setShellRoute('session');
    sh.list.setActive(id);
    if (restore) applyRestore(convo, restore);
    return;
  }
  disposeConversation();
  setShellRoute('session');
  sh.list.setActive(id);
  startConversation(sh, id, restore);
}

function startConversation(sh, id, restore) {
  const els = {
    scroll: $('transcript-scroll'),
    transcript: $('transcript'),
    loading: $('transcript-loading'),
    title: $('sess-title'),
    phase: $('phase-text'),
    dot: $('conn-dot'),
    banner: $('conn-banner'),
    jump: $('jump-latest'),
    queueBadge: $('queue-badge'),
    queueStrip: $('queue-strip'),
    queueChips: $('queue-chips'),
    pending: $('pending-cards'),
    activityBar: $('activity-bar'),
    activityText: $('activity-text'),
    activityDots: document.querySelector('.activity-dots'),
    overlay: $('img-overlay'),
    overlayImg: $('overlay-img'),
    overlayClose: $('overlay-close'),
  };
  if (!els.scroll || !els.transcript) return;

  const st = {
    sessionId: id,
    state: null,
    revision: 0,
    lastSeq: null,
    snapshotReady: false,
    sent: new Set(),
    titleFallback: sessionTitles.get(id) || 'Session',
    transcript: null,
    ws: null,
    followLatest: true,
    layoutFrame: 0,
    lastPendingKey: null,
    queueView: null,
    draftIntent: null,
    localPlanEnabled: null,
    literalText: null,
    lastTitle: null,
    lastPhase: null,
    lastModelSelectionKey: null,
    restoreModelSelection: restore?.modelSelection || null,
    restoreError: '',
    connStatus: 'offline',
    disposed: false,
    els,
    pendingCtx: null,
    updateConn: null,
  };
  convo = st;

  els.loading.hidden = false;
  els.transcript.textContent = '';
  els.title.textContent = st.titleFallback;
  document.title = st.titleFallback + ' — Alcoler';
  els.phase.textContent = 'Idle';
  els.phase.className = 'phase-text phase-idle';
  els.queueBadge.hidden = true;
  els.queueBadge.textContent = '';
  els.queueStrip.hidden = true;
  els.pending.textContent = '';
  els.pending.hidden = true;
  els.activityBar.hidden = true;
  els.activityText.textContent = '';
  els.banner.hidden = true;
  els.banner.textContent = '';
  els.banner.className = 'conn-banner';
  els.jump.hidden = true;

  sh.sessionComposer.root.hidden = false;
  sh.sessionComposer.clear();
  sh.sessionComposer.setReady(false);
  sh.sessionComposer.setPlan(false);
  sh.sessionComposer.setWorkflows([]);
  sh.sessionComposer.setSlashSource(`session:${id}`, () => api.getSlashCatalog({ sessionId: id }));
  sh.sessionComposer.setModelSelection(null);

  const nearBottom = () => els.scroll.scrollHeight - els.scroll.scrollTop - els.scroll.clientHeight < 80;
  const toBottom = () => {
    st.followLatest = true;
    els.scroll.scrollTop = els.scroll.scrollHeight;
  };
  const updateJump = () => { els.jump.hidden = nearBottom(); };
  const refreshLayout = () => {
    if (st.disposed || convo !== st || st.layoutFrame) return;
    st.layoutFrame = requestAnimationFrame(() => {
      st.layoutFrame = 0;
      if (st.disposed || convo !== st) return;
      if (st.followLatest) toBottom();
      updateJump();
    });
  };
  st.refreshLayout = refreshLayout;
  st.transcript = new Transcript(els.transcript, { onChange: refreshLayout });
  st.resizeObserver = new ResizeObserver(refreshLayout);
  st.resizeObserver.observe(els.transcript);

  const closeOverlay = () => {
    els.overlay.hidden = true;
    els.overlayImg.src = '';
    st.overlayLease?.release();
    st.overlayLease = null;
  };
  st.closeOverlay = closeOverlay;

  const onTranscriptClick = async event => {
    const copyBtn = event.target.closest('[data-copy]');
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
    const thumb = event.target.closest('.thumb[data-url]');
    if (thumb) {
      closeOverlay();
      st.overlayLease = api.acquireArtifactObjectUrl?.(thumb.dataset.artifact, thumb.dataset.mime) || null;
      els.overlayImg.src = thumb.dataset.url;
      els.overlay.hidden = false;
    }
  };

  const onScroll = () => {
    st.followLatest = nearBottom();
    updateJump();
  };
  els.transcript.addEventListener('click', onTranscriptClick);
  els.scroll.addEventListener('scroll', onScroll, { passive: true });
  st.onTranscriptClick = onTranscriptClick;
  st.onScroll = onScroll;
  els.jump.onclick = () => {
    toBottom();
    updateJump();
  };
  els.overlay.onclick = event => { if (event.target === els.overlay) closeOverlay(); };
  els.overlayClose.onclick = closeOverlay;

  st.updateConn = () => {
    els.dot.className = 'dot ' + st.connStatus;
    if (st.restoreError) {
      els.banner.hidden = false;
      els.banner.className = 'conn-banner error';
      els.banner.textContent = st.restoreError;
      els.banner.onclick = () => {
        st.restoreError = '';
        st.updateConn();
      };
      return;
    }
    const recovered = pendingRestores.get(id)?.length || 0;
    if (recovered) {
      const occupied = sh.sessionComposer.hasContent() || sh.sessionComposer.pendingFiles > 0 || sh.sessionComposer.disabled;
      els.banner.hidden = false;
      els.banner.className = 'conn-banner';
      els.banner.textContent = occupied
        ? `${recovered} recovered draft${recovered === 1 ? '' : 's'} kept — send or clear the current draft first`
        : `${recovered} recovered draft${recovered === 1 ? '' : 's'} ready — tap to restore`;
      els.banner.onclick = () => {
        const draft = takePendingRestore(id);
        if (draft) applyRestore(st, draft);
        st.updateConn();
      };
      return;
    }
    if (st.connStatus === 'online') {
      els.banner.hidden = true;
      els.banner.textContent = '';
      els.banner.onclick = null;
      return;
    }
    els.banner.hidden = false;
    els.banner.className = 'conn-banner ' + st.connStatus;
    if (st.connStatus === 'offline') {
      els.banner.textContent = 'Offline — tap to reconnect';
      els.banner.onclick = () => { if (st.ws) st.ws.manualReconnect(); };
    } else {
      els.banner.textContent = st.connStatus === 'connecting' ? 'Connecting...' : 'Reconnecting...';
      els.banner.onclick = null;
    }
  };

  st.setConn = status => {
    if (st.disposed || convo !== st) return;
    st.connStatus = status;
    els.dot.title = status === 'online' ? 'Live updates connected' : status === 'offline' ? 'Disconnected' : 'Connecting to live updates';
    els.dot.setAttribute('aria-label', els.dot.title);
    sh.sessionComposer.setReady(st.snapshotReady && status === 'online');
    updateQueue();
    st.updateConn();
  };

  st.resolveInteraction = async (interactionId, extra) => {
    if (!interactionId || st.sent.has(interactionId)) return;
    st.sent.add(interactionId);
    updatePending();
    let ack = null;
    try {
      ack = await api.sendCommand(id, {
        $type: 'resolve_interaction',
        commandId: uuid(),
        sessionId: id,
        baseRevision: st.revision,
        interactionId,
        ...extra,
      });
    } catch { /* the next state patch clears the cards */ }
    if (!st.disposed) applyAckRevision(st, ack);
    scheduleListRefresh(600);
  };

  st.queueView = new QueuedMessages(els.queueChips, {
    composer: sh.sessionComposer,
    sendCommand: (type, targetCommandId) => api.sendCommand(id, {
      $type: type,
      commandId: uuid(),
      sessionId: id,
      baseRevision: st.revision,
      targetCommandId,
    }),
    onAck: ack => {
      if (st.disposed || convo !== st) return;
      applyAckRevision(st, ack);
      if (ack?.accepted) {
        st.restoreError = '';
        st.updateConn();
        scheduleListRefresh(400);
      }
    },
    onError: message => {
      st.restoreError = message;
      st.updateConn();
    },
    onRestore: draft => {
      if (shell === sh && convo === st && !st.disposed) {
        applyRestore(st, draft);
        sh.sessionComposer.focus();
      } else {
        savePendingRestore(id, draft);
      }
    },
  });

  function updateQueue() {
    const state = st.state || {};
    const queue = Array.isArray(state.queue) ? state.queue : [];
    els.queueBadge.hidden = queue.length === 0;
    els.queueBadge.textContent = queue.length ? `${queue.length} queued` : '';
    els.queueStrip.hidden = queue.length === 0;
    st.queueView.update(queue, st.snapshotReady && st.connStatus === 'online', !state.pendingInteraction);
  }

  st.pendingCtx = {
    sent: st.sent,
    onPermission: (interactionId, outcome, feedback) => {
      const permissionDecision = { outcome };
      if (feedback !== undefined) permissionDecision.feedback = feedback;
      st.resolveInteraction(interactionId, { permissionDecision });
    },
    onAnswer: (interactionId, answers) => {
      st.resolveInteraction(interactionId, { interactionAnswer: { answers } });
    },
  };

  function updatePending() {
    const state = st.state || {};
    const permissionId = state.pendingPermission ? (state.pendingPermission.interactionId || '') : '';
    const interactionId = state.pendingInteraction ? (state.pendingInteraction.interactionId || '') : '';
    const has = !!(permissionId || interactionId);
    const key = has ? [permissionId, interactionId, [...st.sent].join(',')].join('|') : '';
    sh.sessionComposer.root.hidden = !!state.pendingInteraction;
    if (key === st.lastPendingKey) return;
    st.lastPendingKey = key;
    renderPending(els.pending, state, st.pendingCtx);
  }

  function updateChrome() {
    const state = st.state || {};
    const title = state.title || st.titleFallback || 'Session';
    if (st.lastTitle !== title) {
      st.lastTitle = title;
      els.title.textContent = title;
      document.title = title + ' — Alcoler';
      sessionTitles.set(id, title);
      sh.list.updateTitle(id, title);
    }

    const phase = normKey(state.phase || 'idle') || 'idle';
    if (st.lastPhase !== phase) {
      els.phase.textContent = phaseLabel(phase);
      els.phase.className = 'phase-text phase-' + sanitizeToken(phase);
      sh.list.updateActivity(id, { phase });
    }

    updateQueue();

    const label = activityLabel(phase);
    const showActivity = !!label;
    els.activityBar.hidden = !showActivity;
    els.activityBar.className = 'activity-bar phase-' + sanitizeToken(phase);
    els.activityText.textContent = showActivity ? label : '';
    const paused = phase === 'awaitingPermission' || phase === 'awaitingInteraction';
    if (els.activityDots) els.activityDots.className = 'activity-dots' + (paused ? ' paused' : '');

    sh.sessionComposer.setPlan(st.draftIntent?.planEnabled ?? st.localPlanEnabled ?? !!state.planEnabled);
    sh.sessionComposer.setWorkflows(st.draftIntent?.workflows ?? state.activeWorkflows);
    sh.sessionComposer.setMode(modeValue(st.draftIntent?.mode ?? state.mode));
    const selectionKey = JSON.stringify(state.modelSelection ?? null);
    if (st.lastModelSelectionKey !== selectionKey) {
      const restored = st.lastModelSelectionKey === null ? st.restoreModelSelection : null;
      if (!st.draftIntent?.modelSelection) sh.sessionComposer.setModelSelection(restored || state.modelSelection);
      st.lastModelSelectionKey = selectionKey;
    }
    sh.sessionComposer.update();
    updatePending();

    if (st.lastPhase !== phase &&
        (phase === 'idle' || phase === 'awaitingPermission' || phase === 'awaitingInteraction')) {
      scheduleListRefresh(500);
    }
    st.lastPhase = phase;
  }

  st.sendMessage = async (text, images, modelSelection) => {
    if (!st.snapshotReady || st.disposed || sh.sessionComposer.disabled) return;
    let message = String(text || '').trim();
    const imageList = Array.isArray(images) ? images : [];
    if (!message && !imageList.length) return;
    const originalIntent = st.draftIntent ? { ...st.draftIntent } : null;
    const catalogDraft = {
      text: message,
      literalText: st.literalText,
      images: imageList.map(image => ({ mimeType: image.mimeType, base64Data: image.base64Data })),
      modelSelection: originalIntent ? originalIntent.modelSelection : modelSelection,
      intent: originalIntent || (st.localPlanEnabled !== null ? {
        mode: null, planEnabled: st.localPlanEnabled, modelSelection, delivery: 'Queue', workflows: null,
      } : null),
    };
    let waitingForCatalog = true;
    sh.sessionComposer.setDisabled(true);
    try {
      const parsed = st.literalText === message ? null : await sh.sessionComposer.parseCommand(message);
      if (shell !== sh || convo !== st || st.disposed) return;
      waitingForCatalog = false;
      const intent = st.draftIntent ? { ...st.draftIntent } : null;
      const busy = isBusy(st.state?.phase);
      const prepared = prepareSubmission(message, parsed, {
        planEnabled: intent?.planEnabled ?? st.localPlanEnabled ?? !!st.state?.planEnabled,
        workflows: intent?.workflows ?? st.state?.activeWorkflows,
        busy,
      });
      if (parsed) {
        message = prepared.text;
        sh.sessionComposer.setText(message);
        st.literalText = message || null;
        if (parsed.kind === 'plan') {
          st.localPlanEnabled = true;
          if (intent) {
            intent.planEnabled = true;
            st.draftIntent = intent;
          }
          sh.sessionComposer.setPlan(true);
        }
        if (prepared.action === 'local') return;
        if (prepared.action === 'workflows') {
          const workflowSequence = st.lastSeq;
          let ack = null;
          try {
            ack = await api.sendCommand(id, {
              $type: 'set_workflows', commandId: uuid(), sessionId: id,
              baseRevision: st.revision, active: prepared.workflows,
            });
          } catch { /* A workflow toggle is never retried after uncertain delivery. */ }
          if (shell !== sh || convo !== st || st.disposed) {
            if (shell === sh && (imageList.length || catalogDraft.intent)) {
              savePendingRestore(id, {
                ...catalogDraft, text: '', literalText: null,
                intent: ack?.accepted && catalogDraft.intent
                  ? { ...catalogDraft.intent, workflows: prepared.workflows } : catalogDraft.intent,
                error: ack?.accepted ? '' : ack?.rejectionReason || 'Workflow change could not be confirmed. Check the session before toggling again.',
              });
            }
            return;
          }
          if (ack?.accepted) {
            if (st.lastSeq === workflowSequence) st.state = { ...st.state, activeWorkflows: prepared.workflows };
            if (intent) st.draftIntent = { ...intent, workflows: prepared.workflows };
            sh.sessionComposer.setWorkflows(st.draftIntent?.workflows ?? st.state?.activeWorkflows);
            st.restoreError = '';
          } else {
            st.restoreError = ack?.rejectionReason || 'Workflow change could not be confirmed. Check the session before toggling again.';
          }
          applyAckRevision(st, ack);
          st.updateConn();
          return;
        }
      }

      const uploads = imageList
        .map(image => ({ mimeType: image.mimeType || 'image/jpeg', base64Data: image.base64Data || '' }))
        .filter(image => image.base64Data);
      const command = {
        $type: 'send_text', commandId: uuid(), sessionId: id,
        baseRevision: st.revision, text: message,
      };
      if (intent) {
        command.mode = intent.mode;
        command.planEnabled = intent.planEnabled;
        command.delivery = intent.delivery;
        command.workflows = intent.workflows;
      }
      if (st.localPlanEnabled !== null && !busy && intent?.planEnabled == null) command.planEnabled = st.localPlanEnabled;
      if (prepared.workflowsChanged) command.workflows = prepared.workflows;
      const selection = intent ? intent.modelSelection : modelSelection;
      if (selection) command.modelSelection = selection;
      if (uploads.length) command.images = uploads;
      const recoveryIntent = intent || (command.planEnabled != null || command.workflows != null ? {
        mode: null, planEnabled: command.planEnabled ?? null, modelSelection: selection,
        delivery: 'Queue', workflows: command.workflows ?? null,
      } : null);
      if (recoveryIntent) {
        recoveryIntent.planEnabled = command.planEnabled ?? null;
        recoveryIntent.workflows = command.workflows ?? null;
      }
      const literalText = st.literalText;
      st.draftIntent = null;
      sh.sessionComposer.clear();
      let ack = null;
      try {
        ack = await api.sendCommand(id, command);
      } catch { }
      const failedDraft = !ack || ack.accepted === false ? {
        text: message, images: uploads, modelSelection: selection, intent: recoveryIntent, literalText,
        error: ack?.rejectionReason || 'Delivery could not be confirmed. Your draft was kept; check the conversation before sending again.',
      } : null;
      if (shell !== sh || convo !== st || st.disposed) {
        if (failedDraft) savePendingRestore(id, failedDraft);
        return;
      }
      applyAckRevision(st, ack);
      if (failedDraft) {
        applyRestore(st, failedDraft);
        return;
      }
      if (command.planEnabled != null) st.localPlanEnabled = null;
      st.restoreError = '';
      st.updateConn();
      scheduleListRefresh(400);
    } catch (e) {
      if (shell === sh && convo === st && !st.disposed) {
        st.restoreError = e?.message || 'Commands are unavailable. Refresh and try again.';
        st.updateConn();
      }
    } finally {
      if (shell === sh && convo === st && !st.disposed) sh.sessionComposer.setDisabled(false);
      else if (shell === sh && waitingForCatalog) savePendingRestore(id, catalogDraft);
    }
  };

  st.sendStop = async () => {
    let ack = null;
    try {
      ack = await api.sendCommand(id, {
        $type: 'stop',
        commandId: uuid(),
        sessionId: id,
        baseRevision: st.revision,
      });
    } catch { /* the connection banner reflects reachability */ }
    if (shell !== sh || convo !== st || st.disposed) return;
    applyAckRevision(st, ack);
    if (ack && ack.accepted !== false) scheduleListRefresh(400);
  };

  st.sendMode = async mode => {
    const wireMode = mode === 'edit' ? 'Edit' : mode === 'yolo' ? 'Yolo' : 'Build';
    if (st.draftIntent) {
      st.draftIntent.mode = wireMode;
      return;
    }
    let ack = null;
    try {
      ack = await api.sendCommand(id, {
        $type: 'set_mode',
        commandId: uuid(),
        sessionId: id,
        baseRevision: st.revision,
        mode: wireMode,
      });
    } catch {
      ack = null;
    }
    if (shell !== sh || convo !== st || st.disposed) return;
    applyAckRevision(st, ack);
    if (!ack || ack.accepted === false) {
      sh.sessionComposer.setMode(modeValue(st.state && st.state.mode));
    } else {
      scheduleListRefresh(400);
    }
  };

  function applyAckRevision(target, ack) {
    if (ack && typeof ack.revision === 'number' && ack.revision > target.revision) {
      target.revision = ack.revision;
    }
  }

  const applySnapshot = snapshot => {
    if (!snapshot || st.disposed || convo !== st) return;
    st.snapshotReady = true;
    els.loading.hidden = true;
    if (typeof snapshot.revision === 'number') st.revision = snapshot.revision;
    if (typeof snapshot.sequence === 'number') st.lastSeq = snapshot.sequence;
    st.state = snapshot.state || {};
    const stick = st.transcript.els.size === 0 || st.followLatest;
    const scrollTop = els.scroll.scrollTop;
    st.followLatest = stick;
    st.transcript.rebuild(snapshot.rows || []);
    sh.sessionComposer.setReady(st.connStatus === 'online');
    updateChrome();
    st.updateConn();
    if (stick) toBottom();
    else els.scroll.scrollTop = scrollTop;
    refreshLayout();
  };

  const applyDelta = delta => {
    if (!delta || st.disposed || convo !== st) return;
    // Stale deltas are dropped; the snapshot after a reconnect is authoritative.
    if (typeof delta.sequence === 'number' && st.lastSeq != null && delta.sequence <= st.lastSeq) return;
    if (typeof delta.sequence === 'number') st.lastSeq = delta.sequence;
    let stateChanged = false;
    for (const op of delta.ops || []) {
      if (!op) continue;
      if (op.$type === 'row_appended' && op.row) st.transcript.append(op.row);
      else if (op.$type === 'row_upserted' && op.row) st.transcript.upsert(op.row);
      else if (op.$type === 'row_text_appended' && !st.transcript.appendText?.(op)) {
        st.ws.manualReconnect();
        return;
      }
      else if (op.$type === 'row_removed') st.transcript.remove(op.rowId);
      else if (op.$type === 'state_patch' && op.state) {
        st.state = op.state;
        stateChanged = true;
      }
    }
    if (stateChanged) updateChrome();
    refreshLayout();
  };

  st.ws = new StreamClient({
    sessionId: id,
    onSnapshot: applySnapshot,
    onDelta: applyDelta,
    onStatus: status => st.setConn(status),
    resume: () => api.resumeSession(id),
    appendText: typeof st.transcript.appendText === 'function',
  });
  st.ws.start();

  if (restore) applyRestore(st, restore);
}

function applyRestore(st, restore) {
  const sh = shell;
  if (!sh || !restore || st.disposed) return;
  const composer = sh.sessionComposer;
  if (composer.hasContent() || composer.pendingFiles > 0) {
    savePendingRestore(st.sessionId, restore);
    return;
  }
  composer.clear();
  if (typeof restore.text === 'string') composer.setText(restore.text);
  st.literalText = restore.literalText ?? null;
  if (restore.modelSelection) {
    st.restoreModelSelection = restore.modelSelection;
    composer.setModelSelection(restore.modelSelection);
  }
  if (Array.isArray(restore.images) && restore.images.length) composer.setImages(restore.images);
  st.draftIntent = restore.intent || null;
  if (st.draftIntent?.mode != null) composer.setMode(modeValue(st.draftIntent.mode));
  if (st.draftIntent?.planEnabled != null) composer.setPlan(st.draftIntent.planEnabled);
  composer.setWorkflows(st.draftIntent?.workflows ?? st.state?.activeWorkflows);
  if (restore.error) st.restoreError = String(restore.error);
  st.updateConn();
}

function disposeConversation() {
  const st = convo;
  if (!st) return;
  convo = null;
  st.disposed = true;
  if (st.queueView) st.queueView.dispose();
  if (st.ws) st.ws.close();
  if (st.layoutFrame) cancelAnimationFrame(st.layoutFrame);
  if (st.resizeObserver) st.resizeObserver.disconnect();
  st.closeOverlay?.();
  st.transcript.dispose();
  if (st.onTranscriptClick && st.els.transcript) {
    st.els.transcript.removeEventListener('click', st.onTranscriptClick);
  }
  if (st.onScroll && st.els.scroll) {
    st.els.scroll.removeEventListener('scroll', st.onScroll);
  }
  if (shell && shell.sessionComposer) {
    shell.sessionComposer.setDisabled(false);
    shell.sessionComposer.clear();
    shell.sessionComposer.setReady(false);
    shell.sessionComposer.setPlan(false);
    shell.sessionComposer.setWorkflows([]);
    shell.sessionComposer.setSlashSource('', null);
    if (shell.sessionComposer.root) shell.sessionComposer.root.hidden = false;
  }
  const els = st.els;
  if (els) {
    els.transcript.textContent = '';
    els.loading.hidden = true;
    els.title.textContent = 'Session';
    els.phase.textContent = 'Idle';
    els.phase.className = 'phase-text phase-idle';
    els.queueBadge.hidden = true;
    els.queueBadge.textContent = '';
    els.queueStrip.hidden = true;
    els.queueChips.textContent = '';
    els.pending.textContent = '';
    els.pending.hidden = true;
    els.activityBar.hidden = true;
    els.activityText.textContent = '';
    els.activityBar.className = 'activity-bar';
    if (els.activityDots) els.activityDots.className = 'activity-dots';
    els.banner.hidden = true;
    els.banner.textContent = '';
    els.banner.className = 'conn-banner';
    els.dot.className = 'dot offline';
    els.jump.hidden = true;
    els.overlay.hidden = true;
    els.overlayImg.src = '';
  }
  document.title = 'Alcoler Remote';
}

function sendSessionMessage(text, images, selection) {
  if (convo && convo.sendMessage) convo.sendMessage(text, images, selection);
}

function sendSessionStop() {
  if (convo && convo.sendStop) convo.sendStop();
}

function sendSessionMode(mode) {
  if (convo && convo.sendMode) convo.sendMode(mode);
}

function modeValue(mode) {
  if (typeof mode === 'number') return mode === 1 ? 'edit' : mode === 3 ? 'yolo' : 'build';
  const key = String(mode || '').toLowerCase();
  return key === 'edit' || key === 'yolo' ? key : 'build';
}

function sanitizeToken(value) {
  return String(value || '').replace(/[^a-zA-Z0-9_-]/g, '');
}

window.addEventListener('hashchange', () => route());

if (desktopMq.addEventListener) desktopMq.addEventListener('change', onViewportChange);
else if (desktopMq.addListener) desktopMq.addListener(onViewportChange);

document.addEventListener('keydown', event => {
  if (event.key === 'Escape') {
    const overlay = $('img-overlay');
    if (overlay && !overlay.hidden) {
      overlay.hidden = true;
      const img = overlay.querySelector('img');
      if (img) img.src = '';
      return;
    }
    if (shell && shell.workspaceMenuOpen) closeWorkspaceMenu(shell);
    return;
  }
  if (!(event.ctrlKey || event.metaKey) || event.altKey) return;
  if (String(event.key).toLowerCase() !== 'n') return;
  const target = event.target;
  if (target && (target.closest('input, textarea, select') || target.isContentEditable)) return;
  if (!shell) return;
  event.preventDefault();
  goNewTask(null, true);
});

window.addEventListener('online', onOnline);
document.addEventListener('visibilitychange', onVisibilityChange);

window.addEventListener('load', () => {
  // Service workers require a secure context; plain-HTTP LAN origins keep
  // running online-only when registration fails.
  if (!('serviceWorker' in navigator)) return;
  navigator.serviceWorker.register('sw.js').catch(() => { });
});

api.onUnauthorized(() => {
  pairingToken = null;
  disposeShell();
  showPair('Device pairing expired. Scan the QR code again.');
});

(function init() {
  const match = location.hash.match(/^#pair=([^&]*)/);
  if (match) {
    let token = '';
    try { token = decodeURIComponent(match[1]); } catch { token = ''; }
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
