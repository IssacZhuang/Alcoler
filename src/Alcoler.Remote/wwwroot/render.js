// Conversation rendering: transcript rows, pending cards, and queue chips.
// All dynamic strings pass through esc() or renderMarkdown() before landing
// in innerHTML.

import { esc, fmtDuration, fmtNum, icon, truncate } from './util.js';
import { renderMarkdown } from './markdown.js';
import { artifactObjectUrl } from './api.js';

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

const OUT_TRUNC = 1500;
const FCS_MAX_FILES = 12;

export class Transcript {
  constructor(el) {
    this.el = el;
    this.els = new Map();
    this.timers = new Map();
  }

  rebuild(rows) {
    for (const t of this.timers.values()) clearTimeout(t.timer);
    this.timers.clear();
    this.els.clear();
    this.el.textContent = '';
    const frag = document.createDocumentFragment();
    for (const row of rows || []) {
      if (!row || !row.rowId) continue;
      const el = rowElement(row);
      this.els.set(row.rowId, el);
      frag.appendChild(el);
    }
    this.el.appendChild(frag);
    hydrateThumbs(this.el);
  }

  append(row) {
    if (!row || !row.rowId) return;
    if (this.els.has(row.rowId)) { this.upsert(row); return; }
    const el = rowElement(row);
    this.els.set(row.rowId, el);
    this.el.appendChild(el);
    hydrateThumbs(this.el, el);
  }

  upsert(row) {
    if (!row || !row.rowId) return;
    const existing = this.els.get(row.rowId);
    if (!existing) { this.append(row); return; }
    if (row.$type === 'assistant_message' && row.isStreaming) {
      this.upsertThrottled(row, existing);
      return;
    }
    this.cancelTimer(row.rowId);
    this.replace(existing, row);
  }

  // Streaming rows are re-rendered at most 4 times per second.
  upsertThrottled(row, existing) {
    let t = this.timers.get(row.rowId);
    if (!t) {
      t = { last: 0, timer: 0, row: null };
      this.timers.set(row.rowId, t);
    }
    t.row = row;
    if (t.timer) return;
    const wait = 250 - (performance.now() - t.last);
    if (wait <= 0) {
      t.last = performance.now();
      t.row = null;
      this.replace(existing, row);
      return;
    }
    t.timer = setTimeout(() => {
      const tt = this.timers.get(row.rowId);
      if (!tt) return;
      tt.timer = 0;
      if (!tt.row) return;
      const current = this.els.get(row.rowId);
      const pending = tt.row;
      tt.row = null;
      tt.last = performance.now();
      if (current) this.replace(current, pending);
    }, wait);
  }

  replace(existing, row) {
    const el = rowElement(row);
    carryDetailsOpen(existing, el);
    this.els.set(row.rowId, el);
    existing.replaceWith(el);
    hydrateThumbs(this.el, el);
  }

  remove(rowId) {
    this.cancelTimer(rowId);
    const el = this.els.get(rowId);
    if (el) el.remove();
    this.els.delete(rowId);
  }

  cancelTimer(rowId) {
    const t = this.timers.get(rowId);
    if (t && t.timer) clearTimeout(t.timer);
    this.timers.delete(rowId);
  }

  dispose() {
    for (const t of this.timers.values()) clearTimeout(t.timer);
    this.timers.clear();
    this.els.clear();
  }
}

function carryDetailsOpen(oldEl, newEl) {
  const a = oldEl.querySelectorAll('details');
  const b = newEl.querySelectorAll('details');
  const min = Math.min(a.length, b.length);
  for (let i = 0; i < min; i++) b[i].open = a[i].open;
}

export function rowElement(row) {
  const div = document.createElement('div');
  div.dataset.rowId = row.rowId;
  switch (row.$type) {
    case 'user_message':
      div.className = 'row row-user';
      div.innerHTML = userHtml(row);
      break;
    case 'assistant_message':
      div.className = 'row row-assistant';
      div.innerHTML = assistantHtml(row);
      break;
    case 'tool_call':
      div.className = 'row row-tool';
      div.innerHTML = toolCallHtml(row);
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
  return `<button type="button" class="thumb" data-artifact="${esc(im.artifactUri || '')}" data-mime="${esc(im.mimeType || '')}" data-name="${esc(im.fileName || 'image')}" aria-label="View image"><span class="thumb-loading">${icon('camera')}</span></button>`;
}

function userHtml(row) {
  let h = row.text ? `<div class="user-text">${esc(row.text)}</div>` : '';
  const imgs = row.images || [];
  if (imgs.length) h += `<div class="msg-images">${imgs.map(imgThumb).join('')}</div>`;
  return h;
}

function assistantHtml(row) {
  const texts = [];
  const thinks = [];
  const files = [];
  for (const p of row.parts || []) {
    if (!p) continue;
    if (p.$type === 'text' && p.text) texts.push(p.text);
    else if (p.$type === 'reasoning' && p.text) thinks.push(p.text);
    else if (p.$type === 'file' && p.artifactUri) files.push(p);
    // tool_call / tool_result parts are shown by tool_call rows; attachments stay unrendered.
  }
  let h = '';
  if (thinks.length) {
    const joined = thinks.join('\n\n');
    h += `<details class="thinking"><summary><span class="thk-label">Thinking</span><span class="thk-len">${fmtNum(joined.length)} chars</span>${icon('chevron-down')}</summary><div class="thk-body">${esc(joined)}</div></details>`;
  }
  if (texts.length) {
    h += `<div class="md-body">${renderMarkdown(texts.join('\n\n'))}${row.isStreaming ? '<span class="stream-cursor" aria-hidden="true"></span>' : ''}</div>`;
  } else if (row.isStreaming) {
    h += '<div class="md-body"><span class="stream-cursor" aria-hidden="true"></span></div>';
  }
  if (files.length) h += `<div class="msg-images">${files.map(imgThumb).join('')}</div>`;
  return h;
}

function safeJson(v) {
  try { return JSON.stringify(v, null, 2); } catch { return String(v); }
}

function toolCallHtml(row) {
  const statusKey = normKey(row.status);
  const status = STATUS_ICON[statusKey] ? statusKey : 'pending';
  const statusCls = row.isError ? 'failed' : status;
  const statusIco = row.isError ? 'x' : STATUS_ICON[status];
  const stateLabel = row.isError ? 'Error' : TOOL_STATE_LABEL[status];
  const dur = fmtDuration(row.duration);
  let head = `<summary class="tool-head"><span class="tool-status ${statusCls}">${icon(statusIco)}</span><span class="tool-name">${esc(row.toolName || 'tool')}</span>`;
  if (stateLabel) head += `<span class="tool-state ${statusCls}">${stateLabel}</span>`;
  if (dur) head += `<span class="tool-dur">${dur}</span>`;
  head += `<span class="tool-chev">${icon('chevron-right')}</span></summary>`;

  let body = '';
  if (row.input != null) {
    body += `<div class="tool-sec">Input</div><pre class="tool-io">${esc(safeJson(row.input))}</pre>`;
  }
  const outs = row.output || [];
  const textOuts = outs.filter(o => o && typeof o.text === 'string');
  const mediaOuts = outs.filter(o => o && o.artifactUri);
  if (textOuts.length) body += outputTextHtml(textOuts[textOuts.length - 1].text);
  if (mediaOuts.length) body += `<div class="msg-images">${mediaOuts.map(imgThumb).join('')}</div>`;
  if (!body) body = '<div class="tool-empty">No output yet</div>';

  return `<details class="tool${row.isError ? ' is-error' : ''}">${head}<div class="tool-body">${body}</div></details>`;
}

function outputTextHtml(text) {
  const head = '<div class="tool-sec">Output</div>';
  if (text.length <= OUT_TRUNC) {
    return `${head}<pre class="tool-io out-text">${esc(text)}</pre>`;
  }
  return `${head}<pre class="tool-io out-text is-clipped"><span class="out-clip">${esc(text.slice(0, OUT_TRUNC))}\u2026</span><button type="button" class="link-btn out-more" data-full="${esc(text)}">Show all</button></pre>`;
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

// Fill [data-artifact] thumbs with authenticated blob object URLs.
export function hydrateThumbs(scope, root) {
  const el = root || scope;
  el.querySelectorAll('.thumb[data-artifact]:not([data-hydrated])').forEach(btn => {
    btn.dataset.hydrated = '1';
    const uri = btn.dataset.artifact;
    if (!uri) {
      btn.classList.add('thumb-error');
      const s = document.createElement('span');
      s.className = 'thumb-fail';
      s.textContent = 'unavailable';
      btn.replaceChildren(s);
      return;
    }
    artifactObjectUrl(uri, btn.dataset.mime).then(url => {
      if (!btn.isConnected) return;
      btn.dataset.url = url;
      const img = new Image();
      img.alt = btn.dataset.name || 'image';
      img.decoding = 'async';
      img.src = url;
      btn.replaceChildren(img);
    }).catch(() => {
      if (!btn.isConnected) return;
      btn.classList.add('thumb-error');
      const s = document.createElement('span');
      s.className = 'thumb-fail';
      s.textContent = 'unavailable';
      btn.replaceChildren(s);
    });
  });
}

export function renderQueue(container, queue) {
  const items = Array.isArray(queue) ? queue : [];
  container.textContent = '';
  for (const q of items) {
    const chip = document.createElement('span');
    chip.className = 'qchip';
    chip.textContent = 'queued: ' + truncate(q.text || '', 60);
    container.appendChild(chip);
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
