// Conversation rendering: transcript rows, pending cards, and queue chips.
// All dynamic strings pass through esc() or renderMarkdown() before landing
// in innerHTML.

import { esc, fmtDuration, fmtNum, icon, truncate } from './util.js';
import { renderMarkdown } from './markdown.js';
import * as api from './api.js';

// A previously active shell worker can serve mixed module revisions during its update.
const acquireArtifactObjectUrl = api.acquireArtifactObjectUrl || ((uri, mime) => ({
  promise: api.artifactObjectUrl(uri, mime), release() {},
}));
const artifactUrl = api.artifactUrl || ((uri, mime) => {
  const token = api.getToken();
  if (!token) throw new Error('Device not paired');
  return `/api/artifacts?uri=${encodeURIComponent(uri)}&mime=${encodeURIComponent(mime || '')}&token=${encodeURIComponent(token)}`;
});

export const PHASE_LABEL = {
  idle: 'Idle',
  processingInput: 'Working',
  awaitingModel: 'Thinking',
  streaming: 'Streaming',
  executingTools: 'Running tools',
  awaitingPermission: 'Awaiting approval',
  awaitingInteraction: 'Awaiting reply',
  completing: 'Finishing',
  error: 'Error',
};

// The API emits PascalCase enum names (e.g. "AwaitingModel"); keys here are camelCase.
export function normKey(value) {
  return typeof value === 'string' && value ? value.charAt(0).toLowerCase() + value.slice(1) : value;
}

export function phaseLabel(phase) {
  return PHASE_LABEL[normKey(phase)] || (phase ? String(phase) : 'Idle');
}

const ACTIVITY_LABEL = {
  processingInput: 'Preparing request',
  awaitingModel: 'Waiting for model',
  streaming: 'Generating response',
  executingTools: 'Running tools',
  awaitingPermission: 'Waiting for permission',
  awaitingInteraction: 'Waiting for your answer',
  completing: 'Finishing up',
};

export function activityLabel(phase) {
  return ACTIVITY_LABEL[normKey(phase)] || '';
}

// Busy means everything except idle, error, and completing; the composer
// turns its send button into a stop button while busy.
export function isBusy(phase) {
  const key = normKey(phase);
  return key !== 'idle' && key !== 'error' && key !== 'completing';
}

const STATUS_ICON = {
  pending: 'clock',
  running: 'loader',
  awaitingPermission: 'shield',
  succeeded: 'check',
  failed: 'x',
  cancelled: 'minus',
};

const TOOL_STATE_LABEL = {
  pending: 'Pending',
  running: 'Running',
  awaitingPermission: 'Needs permission',
  cancelled: 'Cancelled',
  failed: 'Error',
};

const TEXT_PREVIEW = 1500;
const INPUT_PREVIEW = 4000;
const OUTPUT_PREVIEW = 6000;
const OUTPUT_BLOCKS = 24;
const STREAM_INTERVAL = 150;
const FCS_MAX_FILES = 12;
const rowSources = new WeakMap();

function rowState() {
  return { open: new Set(), all: new Set() };
}

export class Transcript {
  constructor(el, { onChange } = {}) {
    this.el = el;
    this.els = new Map();
    this.states = new Map();
    this.timers = new Map();
    this.textBuffers = new Map();
    this.disposed = false;
    this.notify = () => { if (!this.disposed) onChange?.(); };
    this.onToggle = event => this.toggle(event);
    this.onClick = event => this.showAll(event);
    el.addEventListener('toggle', this.onToggle, true);
    el.addEventListener('click', this.onClick);
  }

  state(rowId) {
    if (!this.states.has(rowId)) this.states.set(rowId, rowState());
    return this.states.get(rowId);
  }

  capture(rowId, el) {
    const state = this.state(rowId);
    for (const detail of el.querySelectorAll('details[data-disclosure]')) {
      if (detail.open) state.open.add(detail.dataset.disclosure);
      else state.open.delete(detail.dataset.disclosure);
    }
    return state;
  }

  rebuild(rows) {
    if (this.disposed) return;
    for (const [id, el] of this.els) this.capture(id, el);
    for (const t of this.timers.values()) clearTimeout(t.timer);
    this.timers.clear();
    this.textBuffers.clear();
    const changed = this.el.childNodes.length > 0;
    const previous = this.els;
    this.els = new Map();
    const states = new Map();
    const frag = document.createDocumentFragment();
    for (const row of rows || []) {
      if (!row || !row.rowId) continue;
      const state = this.state(row.rowId);
      const el = rowElement(row, state);
      if (previous.has(row.rowId)) preserveArtifacts(previous.get(row.rowId), el);
      states.set(row.rowId, state);
      this.els.set(row.rowId, el);
      frag.appendChild(el);
    }
    this.states = states;
    releaseArtifacts(this.el);
    this.el.replaceChildren(frag);
    hydrateThumbs(this.el, null, this.notify);
    if (changed || this.els.size) this.notify();
  }

  append(row) {
    if (!row || !row.rowId || this.disposed) return;
    if (this.els.has(row.rowId)) { this.upsert(row); return; }
    const el = rowElement(row, this.state(row.rowId));
    this.els.set(row.rowId, el);
    this.el.appendChild(el);
    hydrateThumbs(this.el, el, this.notify);
    this.notify();
  }

  upsert(row) {
    if (!row || !row.rowId || this.disposed) return;
    this.textBuffers.delete(row.rowId);
    const existing = this.els.get(row.rowId);
    if (!existing) { this.append(row); return; }
    const status = normKey(row.status);
    if ((row.$type === 'assistant_message' && row.isStreaming)
      || (row.$type === 'tool_call' && ['pending', 'running', 'awaitingPermission'].includes(status))) {
      this.upsertThrottled(row, existing);
      return;
    }
    this.cancelTimer(row.rowId);
    this.replace(existing, row);
  }

  appendText(op) {
    if (this.disposed || typeof op.delta !== 'string') return false;
    const existing = this.els.get(op.rowId);
    if (!existing) return false;
    let buffer = this.textBuffers.get(op.rowId);
    if (!buffer) {
      const row = this.timers.get(op.rowId)?.row || rowSources.get(existing);
      if (row?.$type !== 'assistant_message' || !row.isStreaming || !row.parts?.some(p => p?.$type === 'text')) return false;
      buffer = { row, chunks: [], at: row.at };
      this.textBuffers.set(op.rowId, buffer);
    }
    buffer.chunks.push(op.delta);
    buffer.at = op.at;
    this.upsertThrottled(buffer.row, existing);
    return true;
  }

  materializeText(rowId) {
    const buffer = this.textBuffers.get(rowId);
    if (!buffer || !buffer.chunks.length) return null;
    const parts = [...buffer.row.parts];
    const index = parts.findIndex(p => p?.$type === 'text');
    parts[index] = { ...parts[index], text: parts[index].text + buffer.chunks.join('') };
    buffer.row = { ...buffer.row, parts, at: buffer.at };
    buffer.chunks.length = 0;
    return buffer.row;
  }

  upsertThrottled(row, existing) {
    let t = this.timers.get(row.rowId);
    if (!t) {
      t = { last: -Infinity, timer: 0, row: null };
      this.timers.set(row.rowId, t);
    }
    t.row = row;
    if (t.timer) return;
    const wait = STREAM_INTERVAL - (performance.now() - t.last);
    if (wait <= 0) {
      t.last = performance.now();
      t.row = null;
      this.replace(existing, this.materializeText(row.rowId) || row);
      return;
    }
    t.timer = setTimeout(() => {
      const tt = this.timers.get(row.rowId);
      if (!tt) return;
      tt.timer = 0;
      const pending = tt.row;
      tt.row = null;
      tt.last = performance.now();
      const current = this.els.get(row.rowId);
      if (current && pending) this.replace(current, this.materializeText(row.rowId) || pending);
    }, wait);
  }

  replace(existing, row) {
    const el = rowElement(row, this.capture(row.rowId, existing));
    preserveArtifacts(existing, el);
    releaseArtifacts(existing);
    this.els.set(row.rowId, el);
    existing.replaceWith(el);
    hydrateThumbs(this.el, el, this.notify);
    this.notify();
  }

  toggle(event) {
    const detail = event.target;
    if (!detail.matches?.('details[data-disclosure]')) return;
    const el = detail.closest('.row');
    if (!el || this.els.get(el.dataset.rowId) !== el) return;
    const state = this.state(el.dataset.rowId);
    const key = detail.dataset.disclosure;
    let changed = state.open.has(key) !== detail.open;
    if (detail.open) state.open.add(key);
    else state.open.delete(key);
    if (key === 'tool') {
      const body = detail.querySelector('.tool-body');
      if (detail.open && !body.dataset.loaded) {
        body.innerHTML = toolBodyHtml(rowSources.get(el), state);
        body.dataset.loaded = '1';
        hydrateThumbs(this.el, body, this.notify);
        changed = true;
      } else if (!detail.open && body.dataset.loaded) {
        releaseArtifacts(body);
        body.replaceChildren();
        delete body.dataset.loaded;
        changed = true;
      }
    }
    if (changed) this.notify();
  }

  showAll(event) {
    const button = event.target.closest?.('.out-more[data-section]');
    if (!button) return;
    const el = button.closest('.row');
    if (!el || this.els.get(el.dataset.rowId) !== el) return;
    const row = rowSources.get(el);
    const state = this.state(el.dataset.rowId);
    const key = button.dataset.section;
    event.preventDefault();
    if (key === 'outputs') {
      state.all.add(key);
      const body = el.querySelector('.tool-body');
      const replacement = document.createElement('div');
      replacement.innerHTML = toolBodyHtml(row, state);
      preserveArtifacts(body, replacement);
      releaseArtifacts(body);
      body.replaceChildren(...replacement.childNodes);
      hydrateThumbs(this.el, body, this.notify);
    } else {
      const text = toolSectionText(row, key);
      const block = button.closest('.tool-text-block');
      if (text == null || !block) return;
      state.all.add(key);
      block.querySelector('.out-clip').textContent = text;
      block.querySelector('pre').classList.remove('is-clipped');
      button.remove();
    }
    this.notify();
  }

  remove(rowId) {
    this.cancelTimer(rowId);
    this.textBuffers.delete(rowId);
    const el = this.els.get(rowId);
    if (el) { releaseArtifacts(el); el.remove(); }
    this.els.delete(rowId);
    this.states.delete(rowId);
    if (el) this.notify();
  }

  cancelTimer(rowId) {
    const t = this.timers.get(rowId);
    if (t && t.timer) clearTimeout(t.timer);
    this.timers.delete(rowId);
  }

  dispose() {
    this.disposed = true;
    this.el.removeEventListener('toggle', this.onToggle, true);
    this.el.removeEventListener('click', this.onClick);
    for (const t of this.timers.values()) clearTimeout(t.timer);
    this.timers.clear();
    this.textBuffers.clear();
    releaseArtifacts(this.el);
    this.els.clear();
    this.states.clear();
  }
}

export function rowElement(row, state = rowState()) {
  const div = document.createElement('div');
  div.dataset.rowId = row.rowId;
  rowSources.set(div, row);
  switch (row.$type) {
    case 'user_message':
      div.className = 'row row-user';
      div.innerHTML = userHtml(row);
      break;
    case 'assistant_message':
      div.className = 'row row-assistant';
      div.innerHTML = assistantHtml(row, state);
      div.hidden = !div.innerHTML;
      break;
    case 'tool_call':
      div.className = 'row row-tool';
      div.innerHTML = toolCallHtml(row, state);
      break;
    case 'file_change_summary':
      div.className = 'row row-fcs';
      div.innerHTML = fileChangeHtml(row);
      break;
    case 'system_notice':
      div.className = 'row row-sys';
      div.innerHTML = `<div class="sysnote k-${sanitizeToken(row.kind)}">${row.subagent ? '<span class="sys-sub">subagent</span>' : ''}${esc(row.text || '')}</div>`;
      break;
    default:
      // Unknown row types keep their slot but stay invisible.
      div.className = 'row row-unknown';
      break;
  }
  return div;
}

function sanitizeToken(s) {
  return String(s || '').replace(/[^a-zA-Z0-9_-]/g, '');
}

function imgThumb(im) {
  const name = truncate(im.fileName || 'image', 256);
  return `<button type="button" class="thumb" data-artifact="${esc(im.artifactUri || '')}" data-mime="${esc(im.mimeType || '')}" data-name="${esc(name)}" aria-label="${esc('View image: ' + name)}"><span class="thumb-loading">${icon('camera')}</span></button>`;
}

function artifactHtml(media) {
  const mime = String(media.mimeType || '').split(';', 1)[0].trim().toLowerCase();
  if (mime.startsWith('image/')) return imgThumb(media);
  const name = truncate(media.fileName || 'attachment', 256);
  const attrs = `data-artifact="${esc(media.artifactUri || '')}" data-mime="${esc(media.mimeType || '')}"`;
  const link = `<a class="artifact-link" href="#" ${attrs} download="${esc(name)}" referrerpolicy="no-referrer">${esc(name)}<span class="artifact-mime">${esc(mime || 'Download file')}</span></a>`;
  const kind = mime.startsWith('audio/') ? 'audio' : mime.startsWith('video/') ? 'video' : '';
  if (!kind) return link;
  return `<figure class="media-attachment"><${kind} controls preload="none" ${attrs} aria-label="${esc(name)}"></${kind}><button type="button" class="link-btn media-play" aria-label="${esc('Play ' + name)}">Play ${kind}</button><figcaption>${link}</figcaption></figure>`;
}

function userHtml(row) {
  let h = row.text ? `<div class="user-text">${esc(row.text)}</div>` : '';
  const imgs = row.images || [];
  if (imgs.length) h += `<div class="msg-images">${imgs.map(imgThumb).join('')}</div>`;
  return h;
}

function assistantHtml(row, state) {
  const texts = [];
  const thinks = [];
  const files = [];
  for (const p of row.parts || []) {
    if (!p) continue;
    if (p.$type === 'text' && typeof p.text === 'string' && p.text.trim()) texts.push(p.text);
    else if (p.$type === 'reasoning' && typeof p.text === 'string' && p.text.trim()) thinks.push(p.text);
    else if (p.$type === 'file' && p.artifactUri) files.push(p);
  }
  let h = '';
  if (thinks.length) {
    const joined = thinks.join('\n\n');
    h += `<details class="thinking" data-disclosure="thinking"${state.open.has('thinking') ? ' open' : ''}><summary><span class="thk-label">Thinking</span><span class="thk-len">${fmtNum(joined.length)} chars</span>${icon('chevron-down')}</summary><div class="thk-body">${esc(joined)}</div></details>`;
  }
  if (texts.length) {
    h += `<div class="md-body">${renderMarkdown(texts.join('\n\n'))}${row.isStreaming ? '<span class="stream-cursor" aria-hidden="true"></span>' : ''}</div>`;
  } else if (row.isStreaming) {
    h += '<div class="md-body"><span class="stream-cursor" aria-hidden="true"></span></div>';
  }
  if (files.length) h += `<div class="msg-images">${files.map(artifactHtml).join('')}</div>`;
  return h;
}

function safeJson(v) {
  try { return JSON.stringify(v, null, 2); } catch { return String(v); }
}

const SUMMARY_KEYS = ['description', 'file_path', 'path', 'command', 'pattern', 'query', 'url'];
const CONTEXT_LABELS = {
  description: 'Description', file_path: 'File path', path: 'Path', command: 'Command',
  pattern: 'Pattern', query: 'Query', url: 'URL', action: 'Action',
};
const TOOL_PRESENTATION = {
  bash: ['Terminal', 'M4 17l6-6-6-6M12 19h8'],
  read: ['Read', 'M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8ZM14 2v6h6M8 13h8M8 17h6'],
  edit: ['Edit', 'M12 20h9M16.5 3.5a2.12 2.12 0 0 1 3 3L9 17l-4 1 1-4ZM15 5l3 3'],
  write: ['Write', 'M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8ZM14 2v6h6M12 12v6M9 15h6'],
  grep: ['Search', 'M21 21l-4.34-4.34M19 11a8 8 0 1 1-16 0 8 8 0 0 1 16 0Z'],
  browser: ['Browser', 'M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20ZM2 12h20M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1 4-10Z'],
  todoread: ['Tasks', 'm3 6 2 2 4-4M13 6h8m-18 6 2 2 4-4M13 12h8m-18 6 2 2 4-4M13 18h8'],
  agent: ['SubAgent', 'M12 8V4H8M5 8h14a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V10a2 2 0 0 1 2-2ZM9 13v2M15 13v2M1 13v4M23 13v4M9 18h6'],
  exitplanmode: ['Plan', 'M9 5H5a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2h-4M9 3h6v4H9ZM8 12h8M8 16h5'],
};
const TOOL_ALIASES = {
  shell: 'bash', terminal: 'bash', runcommand: 'bash', readfile: 'read', editfile: 'edit',
  writefile: 'write', glob: 'grep', todowrite: 'todoread', task: 'agent', subagent: 'agent',
};

function inputField(input, key) {
  const value = input && typeof input === 'object' && !Array.isArray(input) ? input[key] : null;
  return typeof value === 'string' && /\S/.test(value) ? { key, value } : null;
}

function summaryField(input) {
  for (const key of SUMMARY_KEYS) {
    const field = inputField(input, key);
    if (field) return field;
  }
  return null;
}

export function toolSummary(input) {
  const field = summaryField(input);
  if (!field) return '';
  const line = field.value.slice(0, 161).split(/[\r\n]/, 1)[0];
  return line.length > 160 ? line.slice(0, 157) + '...' : line;
}

function toolFields(row) {
  const first = summaryField(row.input) || inputField(row.input, 'action');
  const fields = first ? [first] : [];
  const add = key => {
    const field = inputField(row.input, key);
    if (field && !fields.some(f => f.key === key)) fields.push(field);
  };
  add(inputField(row.input, 'file_path') ? 'file_path' : 'path');
  const name = String(row.toolName || '').toLowerCase();
  if (name === 'grep' || name === 'glob') {
    add('pattern');
    add('query');
  } else if (['bash', 'shell', 'terminal', 'runcommand'].includes(name)) add('command');
  else if (name === 'browser') add('action');
  return fields.slice(0, 3);
}

function isPathField(field) {
  return field && (field.key === 'file_path' || field.key === 'path');
}

function pathParts(value) {
  const end = value.search(/[\r\n]/);
  const line = end < 0 ? value : value.slice(0, end);
  const trimmed = line.replace(/[\\/]+$/, '');
  if (!trimmed || /^[a-zA-Z]:$/.test(trimmed)) return { name: line, parent: '' };
  const split = Math.max(trimmed.lastIndexOf('/'), trimmed.lastIndexOf('\\'));
  const parent = trimmed.slice(0, split + 1);
  return { name: trimmed.slice(split + 1), parent: parent.length > 36 ? '...' + parent.slice(-33) : parent };
}

function toolGlyph(path, strokeWidth = 1.8) {
  return `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="${strokeWidth}" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="${path}"/></svg>`;
}

function toolDisclosureHtml(row, head, state, error) {
  const open = state.open.has('tool');
  return `<details class="tool${error ? ' is-error' : ''}" data-disclosure="tool"${open ? ' open' : ''}>${head}<div class="tool-body"${open ? ' data-loaded="1"' : ''}>${open ? toolBodyHtml(row, state) : ''}</div></details>`;
}

function agentCallHtml(row, state, status, error, name) {
  const [label, glyph] = TOOL_PRESENTATION.agent;
  const type = truncate(inputField(row.input, 'subagent_type')?.value || 'general-purpose', 160);
  const title = truncate(inputField(row.input, 'description')?.value || 'Agent task', INPUT_PREVIEW);
  const running = !error && (status === 'pending' || status === 'running');
  const head = `<summary class="tool-head is-compact is-agent${running ? ' is-running' : ''}"><span class="tool-icon">${toolGlyph(glyph, 1.5)}</span><span class="tool-title"><span class="tool-label"><span class="tool-name" title="${esc(name)}">${label}</span><span class="tool-agent-type${type.toLowerCase() === 'explore' ? ' is-explore' : ''}" title="${esc(type)}">${esc(type)}</span><span class="tool-agent-separator" aria-hidden="true">·</span><span class="tool-summary" title="${esc(title)}">${esc(title)}</span></span></span>${error ? '<span class="tool-meta"><span class="tool-state failed">Failed</span></span>' : ''}</summary>`;
  return toolDisclosureHtml(row, head, state, error);
}

export function toolCallHtml(row, state = rowState()) {
  const statusKey = normKey(row.status);
  const status = Object.hasOwn(STATUS_ICON, statusKey) ? statusKey : 'pending';
  const error = row.isError || status === 'failed';
  const statusCls = error ? 'failed' : status;
  const stateLabel = error ? 'Error' : TOOL_STATE_LABEL[status];
  const statusLabel = stateLabel || 'Succeeded';
  const name = truncate(row.toolName || 'tool', 80);
  const key = String(row.toolName || '').toLowerCase();
  const canonical = Object.hasOwn(TOOL_ALIASES, key) ? TOOL_ALIASES[key] : key;
  if (canonical === 'agent') return agentCallHtml(row, state, status, error, name);
  const presentation = Object.hasOwn(TOOL_PRESENTATION, canonical) ? TOOL_PRESENTATION[canonical] : null;
  const fileTool = ['read', 'edit', 'write'].includes(canonical);
  const searchTool = canonical === 'grep';
  const summary = (fileTool && (inputField(row.input, 'file_path') || inputField(row.input, 'path')))
    || summaryField(row.input) || (!searchTool && inputField(row.input, 'action'));
  const path = !searchTool && isPathField(summary) ? pathParts(summary.value) : null;
  const summaryText = path ? truncate(path.name, 160) : toolSummary({ description: summary?.value });
  const dur = fmtDuration(row.duration);
  let head = `<summary class="tool-head is-compact${searchTool ? ' is-search' : ''}"><span class="tool-icon">${presentation ? toolGlyph(presentation[1], searchTool ? 1.5 : 1.8) : icon('wrench')}</span><span class="tool-title"><span class="tool-label"><span class="tool-name" title="${esc(name)}">${esc(presentation ? presentation[0] : name)}</span>${summary ? `<span class="tool-summary${path ? ' is-path' : ''}"${searchTool ? ` title="${esc(summaryText)}"` : ''}>${esc(summaryText)}</span>` : ''}</span></span><span class="tool-meta">`;
  const change = row.fileChange;
  if (Number(change?.added) > 0) head += `<span class="tool-add" aria-label="${esc(fmtNum(change.added))} lines added">+${fmtNum(change.added)}</span>`;
  if (Number(change?.removed) > 0) head += `<span class="tool-del" aria-label="${esc(fmtNum(change.removed))} lines removed">\u2212${fmtNum(change.removed)}</span>`;
  if (!searchTool) head += `<span class="tool-status ${statusCls}" role="img" aria-label="${statusLabel}">${icon(error ? 'x' : STATUS_ICON[status])}</span>`;
  if (stateLabel) head += `<span class="tool-state ${statusCls}">${stateLabel}</span>`;
  if (dur) head += `<span class="tool-dur" aria-label="Elapsed time: ${esc(dur)}">${esc(dur)}</span>`;
  head += `</span><span class="tool-chev">${icon('chevron-right')}</span></summary>`;
  return toolDisclosureHtml(row, head, state, error);
}

export function jsonPreview(value, limit = INPUT_PREVIEW) {
  const chunks = [];
  const ancestors = new Set();
  const stop = {};
  let length = 0;
  let clipped = false;
  const write = text => {
    const left = limit - length;
    chunks.push(text.slice(0, left));
    length += Math.min(text.length, left);
    if (text.length > left) { clipped = true; throw stop; }
  };
  const string = text => write(JSON.stringify(text.slice(0, limit - length + 1)));
  const visit = (v, depth) => {
    if (v === null || typeof v !== 'object') {
      if (typeof v === 'string') string(v);
      else write(JSON.stringify(v) ?? 'null');
      return;
    }
    if (ancestors.has(v) || depth >= 32) {
      clipped = true;
      write(ancestors.has(v) ? '"[Circular]"' : '"[Depth limit]"');
      return;
    }
    ancestors.add(v);
    const array = Array.isArray(v);
    write(array ? '[' : '{');
    let count = 0;
    const entry = (key, item) => {
      write((count++ ? ',\n' : '\n') + '  '.repeat(depth + 1));
      if (!array) { string(key); write(': '); }
      visit(item, depth + 1);
    };
    if (array) {
      for (let i = 0; i < v.length; i++) entry('', v[i]);
    } else {
      for (const key in v) {
        if (!Object.hasOwn(v, key)) continue;
        const item = v[key];
        if (item === undefined || typeof item === 'function' || typeof item === 'symbol') continue;
        entry(key, item);
      }
    }
    if (count) write('\n' + '  '.repeat(depth));
    write(array ? ']' : '}');
    ancestors.delete(v);
  };
  try { visit(value, 0); }
  catch (error) {
    if (error !== stop) return { text: 'Input preview unavailable', clipped: true };
  }
  return { text: chunks.join(''), clipped };
}

function textPreview(text, all, limit = TEXT_PREVIEW) {
  return { text: all ? text : text.slice(0, limit), clipped: !all && text.length > limit };
}

function textBlockHtml(label, key, preview, cls = '') {
  return `<div class="tool-text-block" data-section="${esc(key)}"><div class="tool-sec">${esc(label)}</div><pre class="tool-io ${cls}${preview.clipped ? ' is-clipped' : ''}"><span class="out-clip">${esc(preview.text)}${preview.clipped ? '\u2026' : ''}</span></pre>${preview.clipped ? `<button type="button" class="link-btn out-more" data-section="${esc(key)}" aria-label="${esc('Show all ' + label.toLowerCase())}">Show all</button>` : ''}</div>`;
}

function toolSectionText(row, key) {
  if (key === 'input') return safeJson(row.input);
  if (key.startsWith('context:')) return inputField(row.input, key.slice(8))?.value ?? null;
  const match = /^output-(\d+)$/.exec(key);
  return match && typeof row.output?.[Number(match[1])]?.text === 'string'
    ? row.output[Number(match[1])].text : null;
}

export function toolBodyHtml(row, state = rowState()) {
  let body = toolFields(row).map(field => {
    const key = 'context:' + field.key;
    return textBlockHtml(CONTEXT_LABELS[field.key], key, textPreview(field.value, state.all.has(key)), 'tool-context-detail');
  }).join('');
  if (row.input != null) {
    const preview = state.all.has('input') ? { text: safeJson(row.input), clipped: false } : jsonPreview(row.input);
    body += textBlockHtml('Input', 'input', preview);
  }
  const outs = Array.isArray(row.output) ? row.output : [];
  const count = state.all.has('outputs') ? outs.length : Math.min(outs.length, OUTPUT_BLOCKS);
  let budget = OUTPUT_PREVIEW;
  for (let i = 0; i < count; i++) {
    const out = outs[i];
    if (!out) continue;
    if (typeof out.text === 'string') {
      const key = 'output-' + i;
      const preview = textPreview(out.text, state.all.has(key), Math.min(TEXT_PREVIEW, budget));
      budget = Math.max(0, budget - preview.text.length);
      body += textBlockHtml(outs.length > 1 ? 'Output ' + (i + 1) : 'Output', key, preview, 'out-text');
    } else if (out.artifactUri) body += `<div class="msg-images tool-media">${artifactHtml(out)}</div>`;
  }
  if (count < outs.length) body += `<button type="button" class="link-btn out-more" data-section="outputs">Show all output blocks (${fmtNum(outs.length - count)} more)</button>`;
  if (!outs.length) body += '<div class="tool-empty">No output yet</div>';
  return body;
}

function fileChangeHtml(row) {
  const files = row.files || [];
  const added = row.added ?? files.reduce((a, f) => a + (f.added || 0), 0);
  const removed = row.removed ?? files.reduce((a, f) => a + (f.removed || 0), 0);
  const shown = files.slice(0, FCS_MAX_FILES);
  const rest = files.length - shown.length;
  let listHtml = shown.map(f =>
    `<li><span class="fcs-path">${esc(f.path || '')}</span><span class="fcs-delta"><span class="fcs-add">+${fmtNum(f.added)}</span> <span class="fcs-del">\u2212${fmtNum(f.removed)}</span></span></li>`).join('');
  if (rest > 0) listHtml += `<li class="fcs-more">and ${rest} more</li>`;
  return `<div class="fcs"><div class="fcs-total"><span class="fcs-add">+${fmtNum(added)}</span> <span class="fcs-del">\u2212${fmtNum(removed)}</span><span class="fcs-count">across ${files.length} ${files.length === 1 ? 'file' : 'files'}</span></div>${listHtml ? `<ul class="fcs-list">${listHtml}</ul>` : ''}</div>`;
}

const artifactBindings = new WeakMap();
const artifactSelector = '.thumb[data-artifact],.artifact-link[data-artifact],audio[data-artifact],video[data-artifact]';

function preserveArtifacts(previous, next) {
  const available = new Map();
  const key = target => {
    const source = target.matches('.media-attachment') ? target.querySelector('audio,video') : target;
    return JSON.stringify([source.tagName, source.dataset.artifact, source.dataset.mime, source.dataset.name, source.getAttribute('aria-label')]);
  };
  const selector = '.thumb[data-artifact],.media-attachment';
  for (const target of previous.querySelectorAll(selector)) {
    const id = key(target);
    if (!available.has(id)) available.set(id, []);
    available.get(id).push(target);
  }
  for (const target of next.querySelectorAll(selector)) {
    const retained = available.get(key(target))?.shift();
    if (retained) target.replaceWith(retained);
  }
}

export function releaseArtifacts(root) {
  for (const target of root.querySelectorAll(artifactSelector)) artifactBindings.get(target)?.dispose();
}

export function hydrateThumbs(scope, root, onChange = () => {}) {
  const el = root || scope;
  el.querySelectorAll(artifactSelector).forEach(target => {
    if (artifactBindings.has(target)) return;
    let disposed = false;
    let lease = null;
    const listeners = [];
    const listen = (element, name, handler) => {
      element.addEventListener(name, handler);
      listeners.push(() => element.removeEventListener(name, handler));
    };
    const binding = { dispose() {
      if (disposed) return;
      disposed = true;
      for (const remove of listeners) remove();
      listeners.length = 0;
      if (target.matches('.thumb')) {
        for (const image of target.querySelectorAll('img')) image.removeAttribute('src');
      }
      lease?.release();
      lease = null;
      if (target.matches('audio,video') && target.getAttribute('src')) {
        target.pause();
        target.removeAttribute('src');
        target.load();
      }
      delete target.dataset.url;
      artifactBindings.delete(target);
    } };
    artifactBindings.set(target, binding);
    const alive = () => !disposed && target.isConnected;
    const notify = () => { if (alive()) onChange(); };
    const play = target.matches('audio,video') ? target.closest('.media-attachment')?.querySelector('.media-play') : null;
    const fail = () => {
      if (!alive()) return;
      if (target.matches('.thumb')) {
        target.classList.add('thumb-error');
        const span = document.createElement('span');
        span.className = 'thumb-fail';
        span.textContent = 'unavailable';
        target.replaceChildren(span);
      } else {
        target.classList.add('artifact-error');
        target.setAttribute('title', 'Attachment unavailable');
        const hint = target.querySelector('.artifact-mime');
        if (hint) hint.textContent = 'Unavailable — tap to retry';
        if (play) { play.hidden = false; play.textContent = 'Retry playback'; }
      }
      notify();
    };
    const uri = target.dataset.artifact;
    if (!uri) { fail(); return; }
    if (target.matches('a')) {
      listen(target, 'click', event => {
        try {
          target.href = artifactUrl(uri, target.dataset.mime);
          target.classList.remove('artifact-error');
          target.removeAttribute('title');
          const hint = target.querySelector('.artifact-mime');
          if (hint) hint.textContent = target.dataset.mime || 'Download file';
        } catch { event.preventDefault(); fail(); }
      });
    } else if (play) {
      listen(target, 'loadedmetadata', notify);
      listen(target, 'playing', () => {
        if (!alive()) return;
        play.hidden = true;
        target.classList.remove('artifact-error');
        target.removeAttribute('title');
      });
      listen(target, 'error', fail);
      listen(play, 'click', () => {
        if (!alive()) return;
        try {
          if (!target.getAttribute('src') || target.error) target.src = artifactUrl(uri, target.dataset.mime);
          // Playback must start inside the trusted gesture, before any asynchronous work.
          const started = target.play();
          started?.catch(() => { if (alive()) { play.hidden = false; notify(); } });
        } catch { fail(); }
      });
    } else if (target.matches('.thumb')) {
      lease = acquireArtifactObjectUrl(uri, target.dataset.mime);
      lease.promise.then(url => {
        if (!alive()) { binding.dispose(); return; }
        target.dataset.url = url;
        const img = new Image();
        img.alt = target.dataset.name || 'image';
        img.decoding = 'async';
        listen(img, 'load', notify);
        listen(img, 'error', fail);
        img.src = url;
        target.replaceChildren(img);
        notify();
      }).catch(fail);
    }
  });
}

export function renderQueue(container, queue, ctx) {
  container.textContent = '';
  for (const q of queue) {
    const item = document.createElement('div');
    item.className = 'qitem';
    item.dataset.commandId = q.commandId;
    const text = document.createElement('span');
    text.className = 'qtext';
    const images = q.images?.length || 0;
    const label = q.text || (images ? `${images} image${images === 1 ? '' : 's'}` : 'Empty message');
    text.textContent = truncate(label, 160);
    text.title = label;
    item.appendChild(text);
    const actions = document.createElement('div');
    actions.className = 'qactions';
    for (const [action, name, glyph, title] of [
      ['steer', 'Steer queued message', 'send', 'Stop the current turn and send this message now'],
      ['edit', 'Edit queued message', 'edit', ctx.editHint || 'Move this message back to the composer for editing'],
      ['delete', 'Delete queued message', 'close', 'Delete this queued message'],
    ]) {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'qaction';
      button.dataset.queueAction = action;
      button.setAttribute('aria-label', name);
      button.title = title;
      button.innerHTML = icon(glyph);
      button.disabled = !ctx.ready || ctx.pending.has(q.commandId) || (action === 'edit' && !ctx.canEdit);
      button.addEventListener('click', () => ctx.onAction(action, q.commandId));
      actions.appendChild(button);
    }
    item.appendChild(actions);
    container.appendChild(item);
  }
}

// ctx: { sent: Set<interactionId>, onPermission(id, outcome, feedback), onAnswer(id, answers) }
export function renderPending(container, state, ctx) {
  const perm = state && state.pendingPermission;
  const inter = state && state.pendingInteraction;
  container.textContent = '';
  if (!perm && !inter) {
    container.hidden = true;
    return;
  }
  container.hidden = false;
  if (perm) container.insertAdjacentHTML('beforeend', permissionCardHtml(perm, ctx));
  if (inter) container.insertAdjacentHTML('beforeend', interactionCardHtml(inter, ctx));
  wirePending(container, ctx);
}

function riskChip(risk) {
  const lvl = String(risk || '').toLowerCase();
  const cls = ['low', 'medium', 'high'].includes(lvl) ? lvl : 'unknown';
  return `<span class="risk risk-${cls}">${esc(lvl || 'unknown')}</span>`;
}

function inputSummary(input) {
  if (input == null) return '';
  let entries;
  let more = false;
  if (typeof input === 'object' && !Array.isArray(input)) {
    const all = Object.entries(input);
    entries = all.slice(0, 8);
    more = all.length > 8;
  } else {
    entries = [['input', input]];
  }
  const rows = entries.map(([k, v]) => {
    const vs = typeof v === 'object' && v !== null ? safeJson(v) : String(v);
    return `<div class="kv"><span class="kv-k">${esc(k)}</span><span class="kv-v">${esc(truncate(vs, 140))}</span></div>`;
  }).join('');
  return `<div class="pcard-input">${rows}${more ? '<div class="kv kv-more">\u2026</div>' : ''}</div>`;
}

function permissionCardHtml(p, ctx) {
  const interactionId = p.interactionId ?? p.InteractionId ?? '';
  const sent = ctx.sent.has(interactionId);
  const allowsFeedback = !!(p.allowsFeedback ?? p.AllowsFeedback);
  const input = p.input ?? p.Input;
  const plan = allowsFeedback && input && typeof input.plan === 'string' ? input.plan : '';
  const planTitle = input && typeof input.summary === 'string' ? input.summary : '';
  const denyLabel = allowsFeedback ? 'Request changes' : 'Deny';
  const allowLabel = allowsFeedback ? 'Approve' : 'Allow';
  return `<div class="pcard pcard-perm" data-interaction="${esc(interactionId)}">
    <div class="pcard-label">${icon(allowsFeedback ? 'info' : 'shield')}<span>${allowsFeedback ? 'Plan ready' : 'Permission required'}</span></div>
    <div class="pcard-head"><span class="pcard-tool">${esc(p.toolName || 'tool')}</span>${riskChip(p.riskLevel)}</div>
    ${p.reason ? `<p class="pcard-reason">${esc(p.reason)}</p>` : ''}
    ${plan ? `<div class="pcard-plan-label">${esc(planTitle || 'Proposed plan')}</div><div class="pcard-plan">${renderMarkdown(plan)}</div>` : ''}
    ${inputSummary(input)}
    ${allowsFeedback ? `<div class="pcard-feedback-label">Feedback</div><textarea class="pcard-feedback" rows="3" placeholder="Describe the changes you want" aria-label="Feedback"${sent ? ' disabled' : ''}></textarea>` : ''}
    <div class="pcard-actions">
      <button type="button" class="btn danger" data-outcome="denied"${sent ? ' disabled' : ''}>${denyLabel}</button>
      <button type="button" class="btn primary" data-outcome="allowed"${sent ? ' disabled' : ''}>${sent ? 'Sent' : allowLabel}</button>
    </div>
  </div>`;
}

function optionSmall(o) {
  return o.description ? `<small>${esc(o.description)}</small>` : '';
}

function interactionCardHtml(it, ctx) {
  const sent = ctx.sent.has(it.interactionId);
  const qs = it.questions || [];
  const needsSubmit = qs.some(q => q.multiSelect) || qs.length > 1;
  const questions = qs.map(q => {
    const opts = q.options || [];
    let optsHtml;
    if (q.multiSelect || qs.length > 1) {
      const type = q.multiSelect ? 'checkbox' : 'radio';
      optsHtml = opts.map(o =>
        `<label class="opt-check"><input type="${type}" name="q-${esc(q.header)}" value="${esc(o.label)}"${sent ? ' disabled' : ''}><span>${esc(o.label)}${optionSmall(o)}</span></label>`).join('');
    } else {
      optsHtml = opts.map(o =>
        `<button type="button" class="opt-btn" data-header="${esc(q.header)}" data-label="${esc(o.label)}"${sent ? ' disabled' : ''}>${esc(o.label)}${optionSmall(o)}</button>`).join('');
    }
    return `<div class="q" data-header="${esc(q.header)}">
      <div class="q-head">${esc(q.header)}</div>
      ${q.text ? `<p class="q-text">${esc(q.text)}</p>` : ''}
      <div class="q-opts">${optsHtml}</div>
    </div>`;
  }).join('');
  return `<div class="pcard pcard-interact" data-interaction="${esc(it.interactionId)}">
    <div class="pcard-label">${icon('info')}<span>Your answer is needed</span></div>
    ${it.prompt ? `<p class="pcard-prompt">${esc(it.prompt)}</p>` : ''}
    ${questions}
    ${needsSubmit ? `<button type="button" class="btn primary q-submit"${sent ? ' disabled' : ''}>${sent ? 'Sent' : 'Submit'}</button>` : ''}
  </div>`;
}

function wirePending(container, ctx) {
  container.querySelectorAll('.pcard-perm [data-outcome]').forEach(btn => {
    btn.addEventListener('click', () => {
      const card = btn.closest('.pcard');
      if (ctx.sent.has(card.dataset.interaction)) return;
      const feedback = card.querySelector('.pcard-feedback');
      ctx.onPermission(
        card.dataset.interaction,
        btn.dataset.outcome,
        (feedback && feedback.value.trim()) || undefined);
    });
  });
  container.querySelectorAll('.pcard-interact .opt-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      const card = btn.closest('.pcard');
      if (ctx.sent.has(card.dataset.interaction)) return;
      ctx.onAnswer(card.dataset.interaction, { [btn.dataset.header]: btn.dataset.label });
    });
  });
  container.querySelectorAll('.pcard-interact .q-submit').forEach(btn => {
    btn.addEventListener('click', () => {
      const card = btn.closest('.pcard');
      if (ctx.sent.has(card.dataset.interaction)) return;
      const answers = {};
      let complete = true;
      card.querySelectorAll('.q').forEach(q => {
        const header = q.dataset.header;
        const picked = [...q.querySelectorAll('input:checked')].map(i => i.value);
        answers[header] = picked.join(', ');
        if (!picked.length) complete = false;
      });
      if (complete) ctx.onAnswer(card.dataset.interaction, answers);
    });
  });
}
