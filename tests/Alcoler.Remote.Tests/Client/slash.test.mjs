import assert from 'node:assert/strict';
import { afterEach, beforeEach, test } from 'node:test';
import { Composer } from '../../../src/Alcoler.Remote/wwwroot/composer.js';
import {
  completeSlashToken, extractSlashToken, filterSlashEntries, normalizeSlashCatalog, parseSlashCommand,
} from '../../../src/Alcoler.Remote/wwwroot/slash.js';

class Events {
  constructor() { this.listeners = new Map(); }
  addEventListener(name, listener) {
    if (!this.listeners.has(name)) this.listeners.set(name, new Set());
    this.listeners.get(name).add(listener);
  }
  removeEventListener(name, listener) { this.listeners.get(name)?.delete(listener); }
  dispatch(type, properties = {}) {
    const event = { target: this, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; }, ...properties };
    for (const listener of this.listeners.get(type) || []) listener(event);
    return event;
  }
}

class Element extends Events {
  constructor(document) {
    super();
    this.ownerDocument = document;
    this.children = [];
    this.attributes = new Map();
    this.style = {};
    this.classList = { toggle() {} };
    this.value = '';
    this.hidden = false;
    this.selectionStart = 0;
    this.selectionEnd = 0;
    this.scrollHeight = 40;
  }
  set textContent(value) { this.text = String(value); this.children = []; }
  get textContent() { return (this.text || '') + this.children.map(child => child.textContent).join(''); }
  appendChild(child) { child.parentNode = this; this.children.push(child); }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  removeAttribute(name) { this.attributes.delete(name); }
  closest() { return null; }
  contains(target) { return target === this || this.children.some(child => child.contains(target)); }
  focus() {
    if (this.ownerDocument.activeElement === this) return;
    this.ownerDocument.activeElement?.blur();
    this.ownerDocument.activeElement = this;
    this.dispatch('focus');
  }
  blur() {
    this.ownerDocument.activeElement = null;
    this.dispatch('blur');
  }
  setSelectionRange(start, end) { this.selectionStart = start; this.selectionEnd = end; }
  scrollIntoView() { this.scrolled = true; }
  getBoundingClientRect() { return { top: 320 }; }
}

class Document extends Events {
  constructor() { super(); this.activeElement = null; }
  createElement() { return new Element(this); }
}

const globals = new Map(['document', 'window', 'matchMedia'].map(name => [name, globalThis[name]]));
const originalNow = Date.now;
let composers;
beforeEach(() => {
  composers = [];
  globalThis.document = new Document();
  globalThis.window = { innerWidth: 1200 };
  globalThis.matchMedia = () => ({ matches: true });
});
afterEach(() => {
  for (const composer of composers) composer.dispose();
  Date.now = originalNow;
  for (const [name, value] of globals) {
    if (value === undefined) delete globalThis[name];
    else globalThis[name] = value;
  }
});

const catalog = () => ({ entries: [
  { name: 'Design', description: 'Plan safely', kind: 'plan' },
  { name: 'Coordinate', description: 'Delegate the task', kind: 'orchestrator' },
  { name: 'CodeReview', description: 'Inspect changes', kind: 'skill' },
  { name: 'report', description: 'Research a topic', kind: 'skill' },
] });
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
};
const settle = () => new Promise(resolve => setImmediate(resolve));

function setup(options = {}, popup = true) {
  const root = new Element(document);
  const elements = new Map(['.composer-input', '.composer-send', '.plan-badge', '.workflow-badge', '.composer-status',
    ...(popup ? ['.slash-popup'] : [])].map(selector => [selector, new Element(document)]));
  for (const element of elements.values()) root.appendChild(element);
  root.querySelector = selector => elements.get(selector) || null;
  const composer = new Composer(root, options);
  composers.push(composer);
  composer.setReady(true);
  let loads = 0;
  composer.setSlashSource('workspace:first', async () => { loads++; return catalog(); });
  const input = elements.get('.composer-input');
  return {
    composer, root, elements, input, popup: elements.get('.slash-popup'), loads: () => loads,
    type(text, caret = text.length, end = caret) {
      input.value = text;
      input.setSelectionRange(caret, end);
      input.focus();
      input.dispatch('input');
    },
    key(key, properties = {}) { return input.dispatch('keydown', { key, ...properties }); },
  };
}

const names = entries => entries.map(entry => entry.name);

test('token extraction uses the full whitespace-delimited token around the caret', () => {
  assert.deepEqual(extractSlashToken('Please\t/CodeReview  now', 10), { start: 7, end: 18, text: '/CodeReview' });
  assert.deepEqual(extractSlashToken('\n /de\t', 5), { start: 2, end: 5, text: '/de' });
  assert.deepEqual(extractSlashToken('/', 1), { start: 0, end: 1, text: '/' });
  assert.deepEqual(extractSlashToken('/Design trailing', 1), { start: 0, end: 7, text: '/Design' });
  assert.deepEqual(extractSlashToken(' /de\u2003next', 4), { start: 1, end: 4, text: '/de' });
});

test('tokens require a caret after an initial slash, no selection, and no secondary special markers', () => {
  for (const text of ['plain', 'x/de', 'https://host', '/de/sign', '/de@sign', '/de$sign', '/de#sign']) {
    assert.equal(extractSlashToken(text, text.length), null, text);
  }
  assert.equal(extractSlashToken('/Design', 0), null);
  assert.equal(extractSlashToken('/Design', 2, 4), null);
  assert.equal(extractSlashToken('/de ', 4), null);
  assert.equal(extractSlashToken('/de', -1), null);
  assert.equal(extractSlashToken('/de', 10), null);
  assert.equal(extractSlashToken('/de', null), null);
  assert.equal(extractSlashToken('/de@tail', 3), null);
});

test('catalog metadata is copied without bodies, ordered by kind, and deduplicated case-insensitively', () => {
  const data = catalog();
  data.entries.unshift({ name: 'DESIGN', description: 'Skill collision', kind: 'skill' });
  data.entries.push({ name: 'codereview', description: 'Duplicate skill', kind: 'skill' });
  Object.defineProperty(data.entries[1], 'body', { get() { throw new Error('Do not read bodies'); } });
  assert.deepEqual(normalizeSlashCatalog(data), catalog().entries);
  assert.deepEqual(normalizeSlashCatalog({ entries: [
    { name: '/invalid', kind: 'skill' }, { name: 'two words', kind: 'skill' },
    { name: 'unsupported', kind: 'unknown' }, { name: 'valid', kind: 'skill' },
  ] }), [{ name: 'valid', description: '', kind: 'skill' }]);
  assert.throws(() => normalizeSlashCatalog(null), /unavailable/);
  assert.throws(() => normalizeSlashCatalog({}), /unavailable/);
});

test('builtins match prefixes, skills match substrings of names only, and builtin collisions never appear', () => {
  const entries = [{ name: 'DESIGN', description: 'Collision', kind: 'skill' }, ...catalog().entries];
  assert.deepEqual(names(filterSlashEntries(entries, '/')), ['Design', 'Coordinate', 'CodeReview', 'report']);
  assert.deepEqual(names(filterSlashEntries(entries, '/CO')), ['Coordinate', 'CodeReview']);
  assert.deepEqual(names(filterSlashEntries(entries, '/Review')), ['CodeReview']);
  assert.deepEqual(names(filterSlashEntries(entries, '/sign')), []);
  assert.deepEqual(names(filterSlashEntries(entries, '/Inspect')), []);
  for (const token of ['re', ' /re', '/re ', '/re/port', '/re@', '/re$', '/re#']) {
    assert.deepEqual(filterSlashEntries(entries, token), [], token);
  }
});

test('parsing recognizes only exact leading server-provided builtins and trims the prompt', () => {
  const entries = normalizeSlashCatalog(catalog());
  assert.deepEqual(parseSlashCommand(' \t/dEsIgN\n  inspect\n  this  ', entries),
    { name: 'Design', kind: 'plan', prompt: 'inspect\n  this' });
  assert.deepEqual(parseSlashCommand('/Coordinate', entries), { name: 'Coordinate', kind: 'orchestrator', prompt: '' });
  for (const text of ['ordinary', '', 'Please /Design now', '/Des', '/unknown', '/CodeReview inspect', '/Design/next']) {
    assert.equal(parseSlashCommand(text, entries), null, text);
  }
  assert.equal(parseSlashCommand('/plan', []), null);
  assert.equal(parseSlashCommand('/orchestrator', []), null);
});

test('completion replaces the entire token and preserves exact metadata spelling and surrounding whitespace', () => {
  const text = 'Before\t/codereview\n  after';
  assert.deepEqual(completeSlashToken(text, extractSlashToken(text, 10), 'CodeReview'),
    { text: 'Before\t/CodeReview \n  after', caret: 19 });
  assert.deepEqual(completeSlashToken(' /de  next ', extractSlashToken(' /de  next ', 4), 'Design'),
    { text: ' /Design   next ', caret: 9 });
});

test('ordinary parsing never discovers metadata and slash failures preserve the draft', async () => {
  const view = setup();
  view.composer.setSlashSource('workspace:offline', async () => { throw new Error('Offline catalog'); });
  view.composer.setText('/Design inspect');
  assert.equal(await view.composer.parseCommand('ordinary message'), null);
  assert.equal(await view.composer.parseCommand('Please /Design'), null);
  await assert.rejects(view.composer.parseCommand('/Design inspect'), /Offline catalog/);
  assert.equal(view.composer.getText(), '/Design inspect');
  view.composer.setSlashSource('', null);
  assert.equal(await view.composer.parseCommand('still ordinary'), null);
  await assert.rejects(view.composer.parseCommand('/unknown'), /unavailable/);
});

test('same-key polling retains the captured loader, cache, and inflight request', async () => {
  const view = setup();
  const pending = deferred();
  let loads = 0;
  view.composer.setSlashSource('workspace:pending', () => { loads++; return pending.promise; });
  const parsed = view.composer.parseCommand('/Design task');
  await settle();
  view.composer.setSlashSource('workspace:pending', () => { throw new Error('New polling closure must not replace the source'); });
  const second = view.composer.parseCommand('/Coordinate task');
  pending.resolve(catalog());
  assert.deepEqual(await parsed, { name: 'Design', kind: 'plan', prompt: 'task' });
  assert.deepEqual(await second, { name: 'Coordinate', kind: 'orchestrator', prompt: 'task' });
  assert.equal(loads, 1);
  await view.composer.parseCommand('/Design');
  assert.equal(loads, 1);
});

test('parsing refreshes catalogs at thirty seconds and leaves skill and unknown text unchanged', async () => {
  const view = setup();
  let now = 1000;
  Date.now = () => now;
  assert.equal(await view.composer.parseCommand('/CodeReview inspect'), null);
  assert.equal(await view.composer.parseCommand('/unknown'), null);
  assert.equal(view.loads(), 1);
  now += 29_999;
  await view.composer.parseCommand('/Design');
  assert.equal(view.loads(), 1);
  now++;
  await view.composer.parseCommand('/Design');
  assert.equal(view.loads(), 2);
});

test('source changes and disposal reject pending slash parsing rather than sending stale commands', async () => {
  const view = setup();
  for (const dispose of [false, true]) {
    const pending = deferred();
    view.composer.setSlashSource(`session:${dispose}`, () => pending.promise);
    const parsed = view.composer.parseCommand('/Design task');
    await settle();
    const rejected = assert.rejects(parsed, /source changed/);
    if (dispose) view.composer.dispose();
    else view.composer.setSlashSource('workspace:new', async () => ({ entries: [] }));
    pending.resolve(catalog());
    await rejected;
    if (!dispose) assert.equal(await view.composer.parseCommand('/Design task'), null);
  }
});

test('both composers render accessible independent text-only listboxes from metadata', async () => {
  const first = setup();
  const second = setup();
  const data = catalog();
  data.entries[0].description = '<img src=x onerror="boom"> & details';
  first.composer.setSlashSource('workspace:markup', async () => data);
  first.type('/');
  await settle();
  assert.equal(first.popup.hidden, false);
  assert.equal(first.popup.attributes.get('role'), 'listbox');
  assert.equal(first.input.attributes.get('aria-autocomplete'), 'list');
  assert.equal(first.input.attributes.get('aria-expanded'), 'true');
  assert.equal(first.input.attributes.get('aria-controls'), first.popup.id);
  assert.notEqual(first.popup.id, second.popup.id);
  const option = first.popup.children[0];
  assert.equal(option.attributes.get('role'), 'option');
  assert.equal(option.attributes.get('aria-selected'), 'true');
  assert.equal(option.children[0].textContent, '/Design');
  assert.equal(option.children[1].textContent, data.entries[0].description);
  assert.equal(option.children[1].innerHTML, undefined);
  assert.equal(option.title, `/Design — ${data.entries[0].description}`);
  assert.equal(first.input.attributes.get('aria-activedescendant'), option.id);
  assert.equal(second.popup.hidden, true);
});

test('keystrokes filter locally without network floods, including empty results', async () => {
  const view = setup();
  view.type('/');
  view.type('/c');
  view.type('/co');
  await settle();
  assert.equal(view.loads(), 1);
  assert.deepEqual(view.popup.children.map(row => row.children[0].textContent), ['/Coordinate', '/CodeReview']);
  view.type('/no-match');
  assert.equal(view.popup.hidden, true);
  view.type('/Review');
  assert.equal(view.popup.hidden, false);
  assert.equal(view.popup.children[0].children[0].textContent, '/CodeReview');
  await settle();
  assert.equal(view.loads(), 1);
});

test('an open popup refreshes expired metadata once rather than remaining stale or flooding requests', async () => {
  const view = setup();
  let now = 1000;
  Date.now = () => now;
  view.type('/');
  await settle();
  assert.equal(view.loads(), 1);
  now += 30_000;
  view.type('/c');
  view.type('/co');
  await settle();
  assert.equal(view.loads(), 2);
  assert.deepEqual(view.popup.children.map(row => row.children[0].textContent), ['/Coordinate', '/CodeReview']);
  view.type('/cod');
  await settle();
  assert.equal(view.loads(), 2);
});

test('Up and Down clamp selection and plain Enter or Tab complete without sending', async () => {
  const sends = [];
  const view = setup({ onSend: (...args) => sends.push(args) });
  view.type('/');
  await settle();
  assert.equal(view.key('ArrowUp').defaultPrevented, true);
  assert.equal(view.popup.children[0].attributes.get('aria-selected'), 'true');
  for (let index = 0; index < 8; index++) view.key('ArrowDown');
  assert.equal(view.popup.children.at(-1).attributes.get('aria-selected'), 'true');
  assert.equal(view.input.attributes.get('aria-activedescendant'), view.popup.children.at(-1).id);
  assert.equal(view.key('Enter').defaultPrevented, true);
  assert.equal(view.composer.getText(), '/report ');
  assert.equal(view.input.selectionStart, 8);
  assert.equal(view.popup.hidden, true);
  assert.equal(document.activeElement, view.input);
  view.type('Before\t/CodeReview\n  after', 10);
  await settle();
  assert.equal(view.key('Tab').defaultPrevented, true);
  assert.equal(view.composer.getText(), 'Before\t/CodeReview \n  after');
  assert.equal(view.input.selectionStart, 19);
  assert.deepEqual(sends, []);
});

test('modifier keys and Shift Enter are not consumed by autocomplete', async () => {
  const view = setup();
  view.type('/');
  await settle();
  for (const modifier of ['shiftKey', 'ctrlKey', 'altKey', 'metaKey']) {
    for (const key of ['Enter', 'Tab', 'ArrowDown', 'Escape']) {
      const event = { key, [modifier]: true, preventDefault() { throw new Error('Modifier key consumed'); } };
      assert.equal(view.composer.slash.handleKeyDown(event), false);
    }
  }
  assert.equal(view.key('Enter', { shiftKey: true }).defaultPrevented, false);
  assert.equal(view.composer.getText(), '/');
});

test('mobile Enter still inserts a newline outside suggestions and completes inside them', async () => {
  window.innerWidth = 320;
  globalThis.matchMedia = () => ({ matches: false });
  const sends = [];
  const view = setup({ onSend: (...args) => sends.push(args) });
  view.type('ordinary message');
  assert.equal(view.key('Enter').defaultPrevented, false);
  view.type('/des');
  await settle();
  assert.equal(view.key('Enter').defaultPrevented, true);
  assert.equal(view.composer.getText(), '/Design ');
  assert.equal(view.key('Enter').defaultPrevented, false);
  assert.deepEqual(sends, []);
});

test('Escape dismisses the same token until its text changes, including blur and caret updates', async () => {
  const view = setup();
  view.type('/co');
  await settle();
  assert.equal(view.key('Escape').defaultPrevented, true);
  view.input.dispatch('keyup', { key: 'Escape' });
  view.input.dispatch('click');
  view.input.blur();
  view.input.focus();
  await settle();
  assert.equal(view.popup.hidden, true);
  assert.equal(view.loads(), 1);
  view.type('/cod');
  await settle();
  assert.equal(view.popup.hidden, false);
  assert.equal(view.loads(), 2);
});

test('IME composition suppresses suggestions, completion, and desktop sending', async () => {
  const sends = [];
  const view = setup({ onSend: (...args) => sends.push(args) });
  view.type('/');
  await settle();
  view.input.dispatch('compositionstart');
  assert.equal(view.popup.hidden, true);
  view.type('/co');
  assert.equal(view.key('Enter').defaultPrevented, false);
  view.input.dispatch('compositionend');
  await settle();
  assert.equal(view.popup.hidden, false);
  assert.equal(view.key('Enter', { isComposing: true }).defaultPrevented, false);
  assert.equal(view.key('Enter', { keyCode: 229 }).defaultPrevented, false);
  assert.equal(view.composer.getText(), '/co');
  assert.deepEqual(sends, []);
});

test('pointer and touch completion preserve textarea focus until click and choose the clicked row', async () => {
  for (const pointerType of ['mouse', 'touch']) {
    const view = setup();
    view.type('/');
    await settle();
    const option = view.popup.children[2];
    document.dispatch('pointerdown', { target: option, pointerType });
    const press = view.popup.dispatch('pointerdown', { target: option, pointerType, button: 0 });
    assert.equal(press.defaultPrevented, true);
    const mouse = view.popup.dispatch('mousedown', { target: option, button: 0 });
    if (!mouse.defaultPrevented) view.input.blur();
    assert.equal(view.popup.hidden, false);
    option.dispatch('click');
    assert.equal(view.composer.getText(), '/CodeReview ');
    assert.equal(document.activeElement, view.input);
    assert.equal(view.popup.hidden, true);
  }
});

test('outside presses and blur dismiss and reopening refreshes even a fresh catalog', async () => {
  const view = setup();
  view.type('/');
  await settle();
  document.dispatch('pointerdown', { target: document.createElement('div') });
  assert.equal(view.popup.hidden, true);
  view.input.dispatch('click');
  await settle();
  assert.equal(view.loads(), 2);
  view.input.blur();
  assert.equal(view.popup.hidden, true);
  view.input.focus();
  await settle();
  assert.equal(view.loads(), 3);
});

test('caret and selection changes refresh full-token suggestions without creating or submitting sessions', async () => {
  const sends = [];
  const view = setup({ onSend: (...args) => sends.push(args) });
  view.type('Try /Design then /Review', 7);
  await settle();
  assert.equal(view.popup.children[0].children[0].textContent, '/Design');
  view.input.setSelectionRange(17, 17);
  document.dispatch('selectionchange');
  assert.equal(view.popup.hidden, true);
  view.input.setSelectionRange(20, 20);
  view.input.dispatch('select');
  await settle();
  assert.equal(view.popup.children[0].children[0].textContent, '/CodeReview');
  view.input.setSelectionRange(20, 22);
  view.input.dispatch('keyup', { key: 'ArrowRight' });
  assert.equal(view.popup.hidden, true);
  assert.deepEqual(sends, []);
});

test('programmatic drafts, clear, readiness, and disabled states prevent stale UI responses', async () => {
  const view = setup();
  view.composer.setText('/Design');
  await settle();
  assert.equal(view.loads(), 0);
  assert.equal(view.popup.hidden, true);
  for (const stop of [
    () => view.composer.setText('restored draft'), () => view.composer.clear(),
    () => view.composer.setReady(false), () => view.composer.setDisabled(true),
  ]) {
    const pending = deferred();
    view.composer.setSlashSource(`workspace:${Math.random()}`, () => pending.promise);
    view.composer.setReady(true);
    view.composer.setDisabled(false);
    view.type('/');
    await settle();
    stop();
    pending.resolve(catalog());
    await settle();
    assert.equal(view.popup.hidden, true);
    assert.equal(view.input.attributes.get('aria-expanded'), 'false');
    assert.equal(view.input.attributes.has('aria-activedescendant'), false);
  }
});

test('source switches discard old popup responses and metadata and dispose removes listeners', async () => {
  const view = setup();
  const old = deferred(), current = deferred();
  view.composer.setSlashSource('workspace:old', () => old.promise);
  view.type('/');
  await settle();
  view.composer.setSlashSource('workspace:current', () => current.promise);
  assert.equal(view.popup.hidden, true);
  view.type('/');
  await settle();
  current.resolve({ entries: [{ name: 'Current', description: 'Current only', kind: 'skill' }] });
  await settle();
  old.resolve(catalog());
  await settle();
  assert.deepEqual(view.popup.children.map(row => row.children[0].textContent), ['/Current']);
  view.composer.dispose();
  for (const listeners of view.input.listeners.values()) assert.equal(listeners.size, 0);
  for (const listeners of view.popup.listeners.values()) assert.equal(listeners.size, 0);
  for (const listeners of document.listeners.values()) assert.equal(listeners.size, 0);
  assert.equal(view.popup.hidden, true);
});

test('failed discovery does not flood retries per keystroke and can retry on reopening', async () => {
  const view = setup();
  let attempts = 0;
  view.composer.setSlashSource('workspace:offline', async () => { attempts++; throw new Error('Offline'); });
  view.type('/');
  await settle();
  view.type('/d');
  view.type('/de');
  await settle();
  assert.equal(attempts, 1);
  assert.equal(view.popup.hidden, true);
  view.input.blur();
  view.input.focus();
  await settle();
  assert.equal(attempts, 2);
  await assert.rejects(view.composer.parseCommand('/Design'), /Offline/);
  assert.equal(view.composer.getText(), '/de');
});

test('popup loading after disposal never changes DOM or reopens the list', async () => {
  const view = setup();
  const pending = deferred();
  view.composer.setSlashSource('session:pending', () => pending.promise);
  view.type('/');
  await settle();
  view.composer.dispose();
  pending.resolve(catalog());
  await settle();
  assert.equal(view.popup.hidden, true);
  assert.equal(view.popup.children.length, 0);
});

test('first-load Enter and Tab wait for discovery instead of sending or completing prematurely', async () => {
  const sends = [];
  const view = setup({ onSend: (...args) => sends.push(args) });
  const pending = deferred();
  view.composer.setSlashSource('workspace:loading', () => pending.promise);
  view.type('/');
  assert.equal(view.key('Enter').defaultPrevented, true);
  assert.equal(view.key('Tab').defaultPrevented, true);
  assert.equal(view.composer.getText(), '/');
  view.type('/Design');
  assert.equal(view.key('Enter').defaultPrevented, true);
  assert.deepEqual(sends, []);
  pending.resolve(catalog());
  await settle();
  assert.equal(view.key('Enter').defaultPrevented, true);
  assert.equal(view.composer.getText(), '/Design ');
  assert.deepEqual(sends, []);
  view.type('ordinary message');
  assert.equal(view.key('Enter').defaultPrevented, true);
  assert.equal(sends[0][0], 'ordinary message');
});

test('catalog failures after a successful refresh invalidate cached builtin recognition', async () => {
  const view = setup();
  let fail = false;
  view.composer.setSlashSource('workspace:changing', async () => {
    if (fail) throw new Error('Catalog lost');
    return catalog();
  });
  assert.deepEqual(await view.composer.parseCommand('/Design'), { name: 'Design', kind: 'plan', prompt: '' });
  fail = true;
  view.type('/');
  await settle();
  assert.equal(view.popup.hidden, true);
  await assert.rejects(view.composer.parseCommand('/Design'), /Catalog lost/);
});
