// Grouped session sidebar list. Group expansion and per-group visible counts
// survive re-renders; callers own the polling and title cache.

import { icon, relTime, workspaceTail } from './util.js';
import { normKey, phaseLabel } from './render.js';

const PAGE_SIZE = 5;

export function sessionListErrorMessage(error) {
  return error && error.status === 0 ? 'Cannot reach the desktop app.' : 'Could not load sessions.';
}

export function sessionDisplayTitle(session) {
  const title = String((session && session.title) || '').trim();
  return title || 'Untitled session';
}

export class SessionList {
  constructor({ container, onOpen, onArchive, onNewTask, onRefresh } = {}) {
    this.container = container;
    this.onOpen = onOpen;
    this.onArchive = onArchive;
    this.onNewTask = onNewTask;
    this.onRefresh = onRefresh;
    this.sessions = [];
    this.activeId = null;
    this.loading = false;
    this.error = null;
    this.expanded = new Map();
    this.visible = new Map();
    this.rendered = false;
  }

  setSessions(sessions) {
    const next = Array.isArray(sessions) ? sessions.slice() : [];
    const unchanged = this.rendered && !this.loading && !this.error && sameSessions(this.sessions, next);
    this.sessions = next;
    this.loading = false;
    this.error = null;
    if (!unchanged) this.render();
  }

  updateActivity(sessionId, activity) {
    const session = this.sessions.find(item => item.id === sessionId);
    if (!session) return;
    const next = activity ? { ...session.activity, ...activity } : null;
    if (sameActivity(session.activity, next)) return;
    session.activity = next;
    if (!this.container || this.loading || this.error) return;
    const row = [...this.container.querySelectorAll('.session-row')].find(item => item.dataset.id === sessionId);
    if (row) this.applyActivity(row, session);
    else if (needsAttention(session)) this.render();
  }

  setActive(sessionId) {
    const next = sessionId || null;
    if (this.activeId === next) return;
    this.activeId = next;
    this.render();
  }

  updateTitle(sessionId, title) {
    const display = String(title || '').trim() || 'Untitled session';
    for (const session of this.sessions) {
      if (session.id === sessionId) session.title = display;
    }
    if (!this.container) return;
    for (const row of this.container.querySelectorAll('.session-row')) {
      if (row.dataset.id !== sessionId) continue;
      const titleEl = row.querySelector('.sr-title');
      if (titleEl) titleEl.textContent = display;
      const session = this.sessions.find(item => item.id === sessionId);
      if (session) this.applyActivity(row, session);
      break;
    }
  }

  getRoots() {
    const seen = new Set();
    const roots = [];
    for (const session of this.sessions) {
      const root = String((session && session.workspaceRoot) || '');
      if (!root) continue;
      const key = root.toLowerCase();
      if (seen.has(key)) continue;
      seen.add(key);
      roots.push(root);
    }
    return roots;
  }

  showLoading() {
    this.loading = true;
    this.error = null;
    this.render();
  }

  showError(message) {
    this.loading = false;
    this.error = String(message || 'Could not load sessions.');
    this.render();
  }

  render() {
    if (!this.container) return;
    this.rendered = true;
    this.container.textContent = '';
    if (this.loading) {
      const wrap = document.createElement('div');
      wrap.className = 'list-loading sl-loading';
      const spinner = document.createElement('span');
      spinner.className = 'spinner';
      spinner.setAttribute('aria-hidden', 'true');
      wrap.appendChild(spinner);
      this.container.appendChild(wrap);
      return;
    }
    if (this.error) {
      const wrap = document.createElement('div');
      wrap.className = 'list-error sl-error';
      const text = document.createElement('p');
      text.textContent = this.error;
      const retry = document.createElement('button');
      retry.type = 'button';
      retry.className = 'btn sl-retry';
      retry.textContent = 'Retry';
      retry.addEventListener('click', () => {
        if (this.onRefresh) this.onRefresh();
      });
      wrap.append(text, retry);
      this.container.appendChild(wrap);
      return;
    }
    if (!this.sessions.length) {
      const wrap = document.createElement('div');
      wrap.className = 'list-empty sl-empty';
      const title = document.createElement('p');
      title.textContent = 'No active sessions';
      const hint = document.createElement('p');
      hint.className = 'hint';
      hint.textContent = 'Create a new task to start driving Alcoler from this device.';
      wrap.append(title, hint);
      this.container.appendChild(wrap);
      return;
    }
    for (const group of this.groupSessions()) {
      this.container.appendChild(this.groupElement(group));
    }
  }

  groupSessions() {
    const map = new Map();
    for (const session of this.sessions) {
      const root = String((session && session.workspaceRoot) || '');
      const key = root.toLowerCase() || '\u0000none';
      let group = map.get(key);
      if (!group) {
        group = { key, root, sessions: [] };
        map.set(key, group);
      }
      group.sessions.push(session);
    }
    const groups = [...map.values()];
    for (const group of groups) {
      group.name = workspaceTail(group.root) || group.root || 'No workspace';
      group.sessions.sort((a, b) => timeValue(b.updatedAt) - timeValue(a.updatedAt));
    }
    groups.sort((a, b) => {
      const byName = a.name.toLowerCase().localeCompare(b.name.toLowerCase());
      return byName !== 0 ? byName : a.root.toLowerCase().localeCompare(b.root.toLowerCase());
    });
    return groups;
  }

  groupElement(group) {
    const rootEl = document.createElement('div');
    rootEl.className = 'session-group sl-group';
    rootEl.dataset.root = group.root;

    const expanded = this.isExpanded(group.key);
    const head = document.createElement('div');
    head.className = 'session-group-head sl-group-head';

    const toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'icon-btn session-group-toggle sl-group-toggle';
    toggle.innerHTML = icon(expanded ? 'chevron-down' : 'chevron-right');
    toggle.setAttribute('aria-expanded', expanded ? 'true' : 'false');
    toggle.setAttribute('aria-label', expanded ? 'Collapse project' : 'Expand project');
    toggle.title = expanded ? 'Collapse project' : 'Expand project';
    toggle.addEventListener('click', () => {
      this.expanded.set(group.key, !this.isExpanded(group.key));
      this.render();
    });

    const folder = document.createElement('span');
    folder.className = 'session-group-folder sl-group-folder';
    folder.setAttribute('aria-hidden', 'true');
    folder.innerHTML = icon('folder');

    const title = document.createElement('span');
    title.className = 'session-group-title sl-group-title';
    title.textContent = group.name;
    title.title = group.root || group.name;

    const add = document.createElement('button');
    add.type = 'button';
    add.className = 'icon-btn session-group-add sl-group-add';
    add.setAttribute('aria-label', `New task in ${group.name}`);
    add.title = `New task in ${group.name}`;
    add.innerHTML = icon('plus');
    add.addEventListener('click', () => {
      if (this.onNewTask) this.onNewTask(group.root);
    });

    head.append(toggle, folder, title, add);
    rootEl.appendChild(head);
    if (!expanded) return rootEl;

    const body = document.createElement('div');
    body.className = 'session-group-body sl-group-body';
    const total = group.sessions.length;
    const visibleCount = this.visibleCount(group.key, total);
    const shown = group.sessions.filter((session, index) =>
      index < visibleCount || session.id === this.activeId || needsAttention(session));
    for (const session of shown) {
      body.appendChild(this.sessionElement(session));
    }
    rootEl.appendChild(body);

    if (total > PAGE_SIZE) {
      const more = document.createElement('div');
      more.className = 'session-group-more sl-group-more';
      if (shown.length < total) {
        const showMore = document.createElement('button');
        showMore.type = 'button';
        showMore.className = 'link-btn sl-show-more';
        showMore.textContent = 'Show more sessions';
        showMore.addEventListener('click', () => {
          this.visible.set(group.key, Math.min(total, visibleCount + PAGE_SIZE));
          this.render();
        });
        more.appendChild(showMore);
      }
      if (visibleCount > PAGE_SIZE) {
        const collapse = document.createElement('button');
        collapse.type = 'button';
        collapse.className = 'icon-btn sl-collapse';
        collapse.setAttribute('aria-label', 'Show fewer sessions');
        collapse.title = 'Show fewer sessions';
        collapse.innerHTML = icon('chevron-up');
        collapse.addEventListener('click', () => {
          this.visible.set(group.key, PAGE_SIZE);
          this.render();
        });
        more.appendChild(collapse);
      }
      if (more.childNodes.length) rootEl.appendChild(more);
    }
    return rootEl;
  }

  sessionElement(session) {
    const id = String(session.id || '');
    const title = sessionDisplayTitle(session);
    const active = this.activeId === id;

    const wrap = document.createElement('div');
    wrap.className = 'session-row-wrap sl-row-wrap';

    const row = document.createElement('button');
    row.type = 'button';
    row.className = 'session-row sl-session';
    row.dataset.id = id;
    if (active) {
      row.classList.add('active');
      row.setAttribute('aria-current', 'true');
    }

    const indicator = document.createElement('span');
    indicator.className = 'sl-indicator';
    indicator.setAttribute('aria-hidden', 'true');

    const main = document.createElement('span');
    main.className = 'sr-main sl-main';
    const titleEl = document.createElement('span');
    titleEl.className = 'sr-title sl-title';
    titleEl.textContent = title;
    const status = document.createElement('span');
    status.className = 'sl-status';
    const time = document.createElement('span');
    time.className = 'sr-time sl-time';
    time.textContent = relTime(session.updatedAt);
    main.append(titleEl, status, time);
    row.append(indicator, main);
    this.applyActivity(row, session);
    row.addEventListener('click', () => {
      if (this.onOpen) this.onOpen(id);
    });

    const archive = document.createElement('button');
    archive.type = 'button';
    archive.className = 'icon-btn sr-archive sl-archive';
    archive.setAttribute('aria-label', `Archive ${title}`);
    archive.title = 'Archive';
    archive.innerHTML = icon('archive');
    archive.addEventListener('click', event => {
      event.stopPropagation();
      if (!confirm(`Archive "${title}"?`)) return;
      if (this.onArchive) this.onArchive(id, title);
    });

    wrap.append(row, archive);
    return wrap;
  }

  applyActivity(row, session) {
    const state = activityState(session.activity, this.activeId === session.id);
    const indicator = row.querySelector('.sl-indicator');
    if (indicator.dataset.kind !== state.kind) {
      indicator.dataset.kind = state.kind;
      indicator.className = 'sl-indicator ' + state.kind;
      indicator.textContent = '';
      if (state.kind === 'running') {
        const spinner = document.createElement('span');
        spinner.className = 'sl-spinner';
        spinner.innerHTML = icon('loader');
        indicator.appendChild(spinner);
      } else if (state.kind === 'waiting' || state.kind === 'failed') {
        indicator.innerHTML = icon(state.kind === 'waiting' ? 'clock' : 'x');
      } else if (state.kind === 'completed' || state.kind === 'active') {
        const dot = document.createElement('span');
        dot.className = 'sl-dot ' + state.kind;
        indicator.appendChild(dot);
      }
    }
    const status = row.querySelector('.sl-status');
    status.className = 'sl-status ' + state.kind;
    status.textContent = state.label;
    status.title = state.description;
    status.hidden = !state.label;
    row.querySelector('.sr-time').hidden = !!state.label;
    row.dataset.status = state.kind;
    row.setAttribute('aria-label', `${sessionDisplayTitle(session)}, ${state.description}`);
    row.title = `${sessionDisplayTitle(session)}\n${state.description}`;
  }

  isExpanded(key) {
    if (!this.expanded.has(key)) this.expanded.set(key, true);
    return this.expanded.get(key);
  }

  visibleCount(key, total) {
    let count = this.visible.has(key) ? this.visible.get(key) : PAGE_SIZE;
    if (!Number.isFinite(count) || count < PAGE_SIZE) count = PAGE_SIZE;
    return Math.min(count, total);
  }

  dispose() {
    this.sessions = [];
    this.activeId = null;
    this.expanded.clear();
    this.visible.clear();
    this.rendered = false;
    if (this.container) this.container.textContent = '';
    this.onOpen = null;
    this.onArchive = null;
    this.onNewTask = null;
    this.onRefresh = null;
  }
}

function activityState(activity, active) {
  const phase = normKey(activity && activity.phase);
  const outcome = normKey(activity && activity.lastOutcome);
  if (phase === 'awaitingPermission' || phase === 'awaitingInteraction') {
    return {
      kind: 'waiting', label: 'Waiting',
      description: phase === 'awaitingPermission' ? 'Waiting for approval' : 'Waiting for your answer',
    };
  }
  if (phase === 'error') return { kind: 'failed', label: 'Failed', description: 'Failed' };
  if (['processingInput', 'awaitingModel', 'streaming', 'executingTools', 'completing'].includes(phase)) {
    return { kind: 'running', label: 'Running', description: `Running: ${phaseLabel(phase)}` };
  }
  if (outcome === 'failed') return { kind: 'failed', label: 'Failed', description: 'Failed' };
  if (outcome === 'completed' && !active) return { kind: 'completed', label: '', description: 'Completed' };
  return { kind: active ? 'active' : 'idle', label: '', description: 'Idle' };
}

function needsAttention(session) {
  const state = activityState(session.activity, false);
  return state.kind === 'running' || state.kind === 'waiting';
}

function sameActivity(a, b) {
  return normKey(a && a.phase) === normKey(b && b.phase) &&
    normKey(a && a.lastOutcome) === normKey(b && b.lastOutcome);
}

function sameSessions(a, b) {
  return a.length === b.length && a.every((session, index) => {
    const other = b[index];
    return session.id === other.id && session.title === other.title &&
      session.workspaceRoot === other.workspaceRoot && session.updatedAt === other.updatedAt &&
      sameActivity(session.activity, other.activity);
  });
}

function timeValue(value) {
  return Date.parse(value || 0) || 0;
}
