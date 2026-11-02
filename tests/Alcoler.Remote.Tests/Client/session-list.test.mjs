import assert from 'node:assert/strict';
import { afterEach, beforeEach, test } from 'node:test';
import { readFile } from 'node:fs/promises';
import { SessionList } from '../../../src/Alcoler.Remote/wwwroot/session-list.js';

class Element {
  constructor(tagName) {
    this.tagName = tagName;
    this.children = [];
    this.dataset = {};
    this.attributes = new Map();
    this.listeners = new Map();
    this.className = '';
    this.hidden = false;
    this.classList = {
      contains: name => this.className.split(/\s+/).includes(name),
      add: name => { if (!this.classList.contains(name)) this.className += ` ${name}`; },
    };
  }

  get childNodes() { return this.children; }
  get textContent() { return (this.text || '') + this.children.map(child => child.textContent).join(''); }
  set textContent(value) { this.text = String(value); this.html = ''; this.children = []; }
  get innerHTML() { return this.html || ''; }
  set innerHTML(value) { this.html = String(value); this.text = ''; this.children = []; }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  getAttribute(name) { return this.attributes.get(name) ?? null; }
  appendChild(child) { this.children.push(child); return child; }
  append(...children) { for (const child of children) this.appendChild(child); }
  addEventListener(type, listener) { this.listeners.set(type, listener); }
  click() { this.listeners.get('click')?.({ stopPropagation() {} }); }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  querySelectorAll(selector) {
    const matches = [];
    for (const child of this.children) {
      if (selector.startsWith('.') && child.classList.contains(selector.slice(1))) matches.push(child);
      matches.push(...child.querySelectorAll(selector));
    }
    return matches;
  }
}

const originalDocument = globalThis.document;
const originalConfirm = globalThis.confirm;
beforeEach(() => {
  globalThis.document = { createElement: name => new Element(name) };
  globalThis.confirm = () => true;
});
afterEach(() => {
  if (originalDocument === undefined) delete globalThis.document;
  else globalThis.document = originalDocument;
  if (originalConfirm === undefined) delete globalThis.confirm;
  else globalThis.confirm = originalConfirm;
});

function session(id, activity = null, extra = {}) {
  return { id, title: `Task ${id}`, workspaceRoot: 'C:\\Projects\\Alcoler', updatedAt: '2026-11-03T12:00:00Z', activity, ...extra };
}

function createList(sessions, callbacks = {}) {
  const container = new Element('div');
  const list = new SessionList({ container, ...callbacks });
  list.setSessions(sessions);
  return { list, container };
}

function row(container, id) {
  return container.querySelectorAll('.session-row').find(element => element.dataset.id === id);
}

const phases = ['ProcessingInput', 'AwaitingModel', 'Streaming', 'ExecutingTools', 'Completing'];
for (const phase of phases) {
  for (const value of [phase, phase[0].toLowerCase() + phase.slice(1)]) {
    test(`${value} shows an accessible Running badge instead of the previous failure`, () => {
      const { container } = createList([session('one', { phase: value, lastOutcome: 'Failed' })]);
      const element = row(container, 'one');
      assert.equal(element.dataset.status, 'running');
      assert.equal(element.querySelector('.sl-status').textContent, 'Running');
      assert.equal(element.querySelector('.sl-status').hidden, false);
      assert.equal(element.querySelector('.sr-time').hidden, true);
      assert.match(element.getAttribute('aria-label'), /Task one, Running:/);
      assert.ok(element.querySelector('.sl-spinner'));
    });
  }
}

for (const [phase, description] of [
  ['AwaitingPermission', 'Waiting for approval'],
  ['awaitingInteraction', 'Waiting for your answer'],
]) {
  test(`${phase} shows Waiting with a specific accessible explanation`, () => {
    const { container } = createList([session('one', { phase, lastOutcome: 'Failed' })]);
    const element = row(container, 'one');
    assert.equal(element.querySelector('.sl-status').textContent, 'Waiting');
    assert.equal(element.getAttribute('aria-label'), `Task one, ${description}`);
    assert.equal(element.title, `Task one\n${description}`);
    assert.equal(element.querySelector('.sl-spinner'), null);
  });
}

for (const activity of [{ phase: 'Error', lastOutcome: 'Completed' }, { phase: 'Idle', lastOutcome: 'Failed' }]) {
  test(`${JSON.stringify(activity)} shows Failed, never Running`, () => {
    const { container } = createList([session('one', activity)]);
    assert.equal(row(container, 'one').dataset.status, 'failed');
    assert.equal(row(container, 'one').querySelector('.sl-status').textContent, 'Failed');
    assert.equal(row(container, 'one').getAttribute('aria-label'), 'Task one, Failed');
    assert.equal(container.querySelector('.sl-spinner'), null);
  });
}

test('nonresident, idle, and cancelled sessions do not imply running', () => {
  const { container, list } = createList([
    session('one'),
    session('two', { phase: 'Idle', lastOutcome: 'Cancelled' }),
    session('three', { phase: 'Idle', lastOutcome: 'ForcedStop' }),
    session('four', { phase: 'Unknown' }),
  ]);
  for (const element of container.querySelectorAll('.session-row')) {
    assert.equal(element.dataset.status, 'idle');
    assert.equal(element.querySelector('.sl-status').hidden, true);
    assert.equal(element.querySelector('.sr-time').hidden, false);
  }
  list.setActive('one');
  assert.equal(row(container, 'one').dataset.status, 'active');
  assert.equal(row(container, 'one').getAttribute('aria-current'), 'true');
  assert.equal(row(container, 'one').getAttribute('aria-label'), 'Task one, Idle');
});

test('unchanged polls keep the DOM, focusable rows, and spinner identity', () => {
  const items = [session('one', { phase: 'Streaming', lastOutcome: null })];
  const { container, list } = createList(items);
  const group = container.children[0];
  const element = row(container, 'one');
  const spinner = element.querySelector('.sl-spinner');
  list.setSessions(structuredClone(items));
  assert.equal(container.children[0], group);
  assert.equal(row(container, 'one'), element);
  assert.equal(element.querySelector('.sl-spinner'), spinner);
  list.updateActivity('one', { phase: 'ExecutingTools' });
  assert.equal(row(container, 'one'), element);
  assert.equal(element.querySelector('.sl-spinner'), spinner);
  assert.equal(element.getAttribute('aria-label'), 'Task one, Running: Running tools');
});

test('live updates preserve omitted outcomes and update title/status without rebuilding rows', () => {
  const { container, list } = createList([session('one', { phase: 'Idle', lastOutcome: 'Failed' })]);
  const element = row(container, 'one');
  list.updateActivity('one', { phase: 'AwaitingModel' });
  assert.equal(list.sessions[0].activity.lastOutcome, 'Failed');
  assert.equal(element.dataset.status, 'running');
  list.updateActivity('one', { phase: 'AwaitingPermission' });
  assert.equal(element.dataset.status, 'waiting');
  list.updateTitle('one', '<Renamed>');
  assert.equal(element.querySelector('.sr-title').textContent, '<Renamed>');
  assert.equal(element.getAttribute('aria-label'), '<Renamed>, Waiting for approval');
  list.updateActivity('one', { phase: 'Idle', lastOutcome: 'Completed' });
  assert.equal(element.dataset.status, 'completed');
  assert.equal(element.querySelector('.sl-status').hidden, true);
  assert.equal(element.querySelector('.sr-time').hidden, false);
  list.updateActivity('one', null);
  assert.equal(element.dataset.status, 'idle');
  assert.equal(row(container, 'one'), element);
});

test('default paging keeps running, waiting, and selected sessions visible without changing sort order', () => {
  const items = Array.from({ length: 10 }, (_, index) => session(String(index), null, {
    updatedAt: new Date(Date.UTC(2026, 10, 3, 12, 0, 10 - index)).toISOString(),
  }));
  items[7].activity = { phase: 'ExecutingTools' };
  items[8].activity = { phase: 'AwaitingInteraction' };
  const { container, list } = createList(items);
  assert.deepEqual(container.querySelectorAll('.session-row').map(element => element.dataset.id), ['0', '1', '2', '3', '4', '7', '8']);
  list.setActive('9');
  assert.ok(row(container, '9'));
  assert.equal(row(container, '6'), undefined);
  list.updateActivity('6', { phase: 'Streaming' });
  assert.ok(row(container, '6'));
  container.querySelector('.sl-group-toggle').click();
  assert.equal(container.querySelectorAll('.session-row').length, 0);
  list.setSessions(structuredClone(list.sessions));
  assert.equal(container.querySelectorAll('.session-row').length, 0);
});

test('show more, show fewer, open, archive, and project creation keep their existing behavior', () => {
  const calls = [];
  const { container } = createList(Array.from({ length: 8 }, (_, index) => session(String(index))), {
    onOpen: id => calls.push(['open', id]),
    onArchive: (id, title) => calls.push(['archive', id, title]),
    onNewTask: root => calls.push(['new', root]),
  });
  assert.equal(container.querySelectorAll('.session-row').length, 5);
  container.querySelector('.sl-show-more').click();
  assert.equal(container.querySelectorAll('.session-row').length, 8);
  container.querySelector('.sl-collapse').click();
  assert.equal(container.querySelectorAll('.session-row').length, 5);
  row(container, '0').click();
  container.querySelector('.sl-archive').click();
  container.querySelector('.sl-group-add').click();
  assert.deepEqual(calls, [['open', '0'], ['archive', '0', 'Task 0'], ['new', 'C:\\Projects\\Alcoler']]);
});

test('error/loading recovery and empty lists still render after equivalent data', () => {
  const { container, list } = createList([]);
  assert.ok(container.querySelector('.sl-empty'));
  list.showLoading();
  assert.ok(container.querySelector('.sl-loading'));
  list.setSessions([]);
  assert.ok(container.querySelector('.sl-empty'));
  list.showError('Unavailable');
  assert.ok(container.querySelector('.sl-error'));
  list.setSessions([]);
  assert.ok(container.querySelector('.sl-empty'));
});

test('favicon uses the original Alcoler bottle and terminal artwork', async () => {
  const root = new URL('../../../', import.meta.url);
  const original = await readFile(new URL('src/Alcoler/Assets/Icons/Alcoler.svg', root), 'utf8');
  const served = await readFile(new URL('src/Alcoler.Remote/wwwroot/icons/favicon.svg', root), 'utf8');
  assert.equal(served.replace(/\r\n/g, '\n'), original.replace(/\r\n/g, '\n'));
});
