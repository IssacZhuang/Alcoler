import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { SlashAutocomplete } from '../../../src/Alcoler.Remote/wwwroot/slash.js';
import { prepareSubmission } from '../../../src/Alcoler.Remote/wwwroot/submission.js';

const source = readFileSync(new URL('../../../src/Alcoler.Remote/wwwroot/app.js', import.meta.url), 'utf8');
const controllerStart = source.indexOf('const appEl =');
const startupStart = source.indexOf("window.addEventListener('hashchange'");
const keyboardStart = source.indexOf("document.addEventListener('keydown'");
const keyboardEnd = source.indexOf("window.addEventListener('online'", keyboardStart);
assert.ok(controllerStart >= 0 && startupStart > controllerStart);
assert.ok(keyboardStart > startupStart && keyboardEnd > keyboardStart);

const loadController = new Function('dependencies', `
  const { api, Composer, StreamClient, Transcript, SessionList, QueuedMessages,
    prepareSubmission, document, matchMedia, localStorage, location,
    setTimeout, clearTimeout, setInterval, clearInterval,
    requestAnimationFrame, cancelAnimationFrame, ResizeObserver,
    icon, uuid, workspaceTail, isBusy, normKey, phaseLabel, activityLabel, renderPending } = dependencies;
  ${source.slice(controllerStart, startupStart)}
  shell = createShell();
  ${source.slice(keyboardStart, keyboardEnd)}
  return {
    get shell() { return shell; }, get convo() { return convo; },
    get pendingRestores() { return pendingRestores; },
    get readingStates() { return readingStates; }, route, sendDraft,
  };
`);

const image = { mimeType: 'image/png', base64Data: 'aW1hZ2U=' };
const shownModel = { providerId: 'provider', modelId: 'shown-model', options: { reasoningLevel: 'high' } };
const explicitModel = { providerId: 'provider', modelId: 'queued-model', options: { maxOutputTokens: 2048 } };
const catalog = { entries: [
  ...['plan', 'orchestrator'].map(name => ({ name, kind: name })), { name: 'report', kind: 'skill' },
] };
const accepted = (revision = 11) => ({ accepted: true, revision });
const noop = () => {};

function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

class FakeComposer {
  constructor(root, opts) {
    Object.assign(this, { root, opts, text: '', images: [], modelSelection: null,
      ready: false, disabled: false, pendingFiles: 0, parseCalls: [] });
    this.slash = new SlashAutocomplete(null, null);
  }
  setSlashSource(key, loader) { this.slash.setSource(key, loader); }
  parseCommand(text) { this.parseCalls.push(text); return this.slash.parseCommand(text); }
  getText() { return this.text; }
  getImages() { return structuredClone(this.images); }
  getModelSelection() { return structuredClone(this.modelSelection); }
  hasContent() { return !!this.text.trim() || this.images.length > 0; }
  setText(text) { this.text = text; this.update(); }
  setImages(images) { this.images = structuredClone(images); this.update(); }
  setModelSelection(selection) { this.modelSelection = structuredClone(selection ?? null); this.update(); }
  setReady(ready) { this.ready = ready; this.update(); }
  setDisabled(disabled) { this.disabled = disabled; this.update(); }
  setMode(mode) { this.mode = mode; }
  setPlan(enabled) { this.planEnabled = enabled; }
  setWorkflows(active) { this.workflows = [...(active || [])]; }
  takeImages() { const images = this.images; this.images = []; this.update(); return images; }
  clear() { this.text = ''; this.images = []; this.update(); }
  update() { this.opts.onChange?.(); }
  focus() {}
}

function sessionState(overrides = {}) {
  return { title: 'Session', phase: 'idle', mode: 'Build', planEnabled: false,
    activeWorkflows: ['review'], modelSelection: shownModel, queue: [], ...structuredClone(overrides) };
}

function withdrawnDraft(text, modelSelection = null) {
  return { text, images: [image], modelSelection,
    intent: { mode: 'Edit', planEnabled: false, modelSelection, delivery: 'Guide', workflows: ['review', 'audit'] } };
}

function assertSentIntent(command, intent) {
  for (const key of ['mode', 'planEnabled', 'delivery', 'workflows']) {
    assert.deepEqual(command[key], intent[key], key);
  }
  assert.deepEqual(command.modelSelection ?? null, intent.modelSelection);
  assert.deepEqual(command.images, [image]);
}

function createFixture({ storedMode, compactCapable = true, historyCapable = true, detailsAvailable = true } = {}) {
  let serial = 0;
  const nodes = new Map();
  const frames = new Map();
  const node = key => {
    if (!nodes.has(key)) nodes.set(key, {
      hidden: ['img-overlay', 'overlay-img', 'overlay-status', 'overlay-retry'].includes(key),
      textContent: '', className: '', dataset: {}, src: '', onload: null, onerror: null, listeners: new Map(),
      scrollHeight: 100, scrollTop: 0, clientHeight: 100,
      classList: { toggle: noop }, setAttribute: noop, querySelector: () => null,
      addEventListener(name, handler) {
        if (!this.listeners.has(name)) this.listeners.set(name, new Set());
        this.listeners.get(name).add(handler);
      },
      removeEventListener(name, handler) { this.listeners.get(name)?.delete(handler); },
      fire(name, event) { for (const handler of this.listeners.get(name) || []) handler(event); },
    });
    return nodes.get(key);
  };
  const storage = new Map([['alcoler.lastWorkspace', 'C:/workspace']]);
  if (storedMode !== undefined) storage.set('alcoler.lastMode', storedMode);
  const location = { hash: '#/new' };
  const calls = { creates: [], commands: [], catalogs: [], transcript: [], details: [], history: [], artifacts: [], images: [], reconnects: 0 };
  const handlers = {
    acquireArtifactObjectUrl: () => { throw new Error('Unexpected original image request'); },
    createSession: async () => ({ id: 'created' }),
    sendCommand: async () => accepted(),
    getSlashCatalog: async () => catalog,
    getTurnDetails: async () => ({ rows: [], version: 'turn-version', sequence: 100 }),
    getRowDetails: async () => ({ row: {}, version: 'row-version', sequence: 100 }),
    history: async request => ({ rows: [], sequence: 100, history: { before: null, after: null },
      direction: request.latest ? 'latest' : request.before ? 'before' : 'after' }),
  };
  const api = {
    getToken: () => 'paired-token',
    acquireArtifactObjectUrl(...args) {
      const lease = handlers.acquireArtifactObjectUrl(...args);
      calls.artifacts.push({ args, lease });
      return lease;
    },
    async getSlashCatalog(query) { calls.catalogs.push(query); return handlers.getSlashCatalog(query); },
    async createSession(body) { calls.creates.push(structuredClone(body)); return handlers.createSession(body); },
    async sendCommand(id, command) {
      calls.commands.push(structuredClone(command));
      return handlers.sendCommand(id, command);
    },
  };
  if (detailsAvailable) {
    for (const method of ['getTurnDetails', 'getRowDetails']) {
      api[method] = async (id, rowId, options) => {
        calls.details.push({ method, id, rowId, options });
        return handlers[method](id, rowId, options);
      };
    }
  }
  const schedule = () => ++serial;
  const controller = loadController({
    api, Composer: FakeComposer, prepareSubmission,
    document: { getElementById: node, querySelector: node, hidden: true,
      addEventListener: (name, handler) => node('document').addEventListener(name, handler) },
    matchMedia: () => ({ matches: true }), location,
    localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) },
    setTimeout: schedule, clearTimeout: noop, setInterval: schedule, clearInterval: noop,
    requestAnimationFrame: fn => { const id = ++serial; frames.set(id, fn); return id; },
    cancelAnimationFrame: id => frames.delete(id),
    ResizeObserver: class { observe() {} disconnect() {} },
    StreamClient: class {
      constructor(opts) { this.opts = opts; }
      start() {} close() {}
      manualReconnect() { calls.reconnects++; }
      async requestHistory(request) {
        calls.history.push(structuredClone(request));
        const page = await handlers.history(request);
        this.opts.onHistory(page, request);
        return page;
      }
    },
    Transcript: class {
      constructor(_el, opts) {
        this.els = new Map(); this.opts = opts; this.supportsCompact = compactCapable;
        this.supportsHistory = historyCapable; this.viewState = [];
      }
      element(row) {
        const transcript = this;
        return { dataset: { rowId: row.rowId }, hidden: false, getBoundingClientRect() {
          const scroll = node('transcript-scroll');
          const index = [...transcript.els.keys()].indexOf(row.rowId);
          const top = (scroll.getBoundingClientRect?.().top || 0) + (row.layoutY ?? index * 100) - scroll.scrollTop;
          return { top, bottom: top + (row.layoutHeight ?? 100), height: row.layoutHeight ?? 100 };
        } };
      }
      rebuild(rows) {
        this.opts.onBeforeChange?.();
        calls.transcript.push(['snapshot', structuredClone(rows)]);
        this.els.clear();
        for (const row of rows) this.els.set(row.rowId, this.element(row));
        this.opts.onChange?.();
      }
      prepend(rows) {
        this.opts.onBeforeChange?.();
        calls.transcript.push(['before', structuredClone(rows)]);
        const added = rows.filter(row => !this.els.has(row.rowId)).map(row => [row.rowId, this.element(row)]);
        this.els = new Map([...added, ...this.els]);
        node('transcript-scroll').scrollHeight += added.length * 100;
        this.opts.onChange?.();
      }
      appendHistory(rows) {
        this.opts.onBeforeChange?.();
        calls.transcript.push(['after', structuredClone(rows)]);
        for (const row of rows) if (!this.els.has(row.rowId)) {
          this.els.set(row.rowId, this.element(row));
          node('transcript-scroll').scrollHeight += 100;
        }
        this.opts.onChange?.();
      }
      exportState() { return structuredClone(this.viewState); }
      restoreState(state) { this.viewState = structuredClone(state); }
      hasPendingDetails() { return this.pendingDetails === true; }
      resumeImages() {
        assert.notEqual(this.disposed, true);
        calls.images.push({ transcript: this, scrollTop: node('transcript-scroll').scrollTop });
      }
      append(row) { calls.transcript.push(['append', structuredClone(row)]); }
      upsert(row) { calls.transcript.push(['upsert', structuredClone(row)]); }
      appendText(op) { calls.transcript.push(['text', structuredClone(op)]); return op.rowId !== 'missing'; }
      remove(rowId) { calls.transcript.push(['remove', rowId]); }
      dispose() { this.disposed = true; }
    },
    SessionList: class { setActive() {} updateTitle() {} updateActivity() {} },
    QueuedMessages: class { constructor(_root, opts) { this.opts = opts; } update() {} refresh() {} dispose() {} },
    icon: () => '', uuid: () => `command-${++serial}`, workspaceTail: root => root,
    isBusy: phase => phase === 'running', normKey: value => value,
    phaseLabel: value => value, activityLabel: () => '', renderPending: noop,
  });
  const fixture = {
    controller, calls, handlers, location, node,
    get draft() { return controller.shell.draftComposer; },
    get composer() { return controller.shell.sessionComposer; },
    get convo() { return controller.convo; },
    open(id = 'original', overrides = {}, rows = [], history) {
      location.hash = `#/session/${encodeURIComponent(id)}`;
      controller.route();
      const st = controller.convo;
      st.ws.opts.onStatus('online');
      st.ws.opts.onSnapshot({ revision: 10, sequence: 100, state: sessionState(overrides), rows, history });
      return st;
    },
    flushLayout() {
      const pending = [...frames];
      frames.clear();
      for (const [, fn] of pending) fn();
    },
    withdraw(draft) { controller.convo.queueView.opts.onRestore(structuredClone(draft)); },
    sendSession() {
      const composer = fixture.composer;
      return controller.convo.sendMessage(composer.getText(), composer.getImages(), composer.getModelSelection());
    },
    sendDraft() {
      const composer = fixture.draft;
      return controller.sendDraft(controller.shell, composer.getText(), composer.getImages(), composer.getModelSelection());
    },
  };
  return fixture;
}

const flushImages = () => new Promise(resolve => setImmediate(resolve));

function originalImageLease() {
  const pending = deferred();
  return { ...pending, releases: 0, release() { this.releases++; } };
}

function imageThumb(name, { error = false, url } = {}) {
  const thumb = {
    dataset: { artifact: `alcoler-artifact://overlay/${name}`, mime: 'image/png', name: `${name}.png` },
    classList: { contains: value => value === 'thumb-error' && error },
    closest: selector => selector === '.thumb[data-artifact]' ? thumb : null,
  };
  if (url !== undefined) thumb.dataset.url = url;
  return thumb;
}

function clickImage(fixture, thumb) {
  const event = { target: thumb, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
  return { event, done: fixture.convo.onTranscriptClick(event) };
}

function assertOverlayClosed(fixture) {
  assert.equal(fixture.node('img-overlay').hidden, true);
  assert.equal(fixture.node('overlay-img').hidden, true);
  assert.equal(fixture.node('overlay-img').src, '');
  assert.equal(fixture.node('overlay-img').onload, null);
  assert.equal(fixture.node('overlay-img').onerror, null);
  assert.equal(fixture.node('overlay-status').hidden, true);
  assert.equal(fixture.node('overlay-retry').hidden, true);
}

test('the image viewer shell provides separate accessible loading status and local retry nodes', () => {
  const html = readFileSync(new URL('../../../src/Alcoler.Remote/wwwroot/index.html', import.meta.url), 'utf8');
  assert.match(html, /id="img-overlay"[^>]*role="dialog"[^>]*aria-modal="true"[^>]*hidden/);
  assert.match(html, /id="overlay-status"[^>]*role="status"[^>]*hidden/);
  assert.match(html, /<button[^>]*id="overlay-retry"[^>]*type="button"[^>]*hidden>Retry<\/button>/);
  assert.match(html, /<img[^>]*id="overlay-img"[^>]*hidden/);
});

test('image observation resumes only in the first coalesced layout after the latest scroll position is applied', () => {
  const f = createFixture(), scroll = f.node('transcript-scroll');
  Object.assign(scroll, { scrollHeight: 2400, clientHeight: 200 });
  f.location.hash = '#/session/first';
  f.controller.route();
  const st = f.convo;
  assert.equal(st.transcript.opts.scrollRoot, scroll);
  assert.equal(st.transcript.opts.deferImages, true);
  st.refreshLayout();
  f.flushLayout();
  assert.equal(f.calls.images.length, 0);
  st.ws.opts.onSnapshot({ revision: 10, sequence: 100, state: sessionState(), rows: [{ rowId: 'last' }] });
  st.refreshLayout();
  st.transcript.opts.onChange();
  assert.equal(f.calls.images.length, 0);
  assert.equal(f.calls.details.length, 0);
  assert.equal(f.calls.artifacts.length, 0);
  f.flushLayout();
  assert.deepEqual(f.calls.images, [{ transcript: st.transcript, scrollTop: 2400 }]);
  assert.equal(st.snapshotReady, true);
});

test('restored nested reading positions defer images through pending details and resume only at the restored anchor', () => {
  const f = createFixture(), scroll = f.node('transcript-scroll');
  Object.assign(scroll, { scrollHeight: 2000, clientHeight: 200, getBoundingClientRect: () => ({ top: 0 }) });
  const rows = [{ rowId: 'parent', layoutY: 0 }, { rowId: 'nested', layoutY: 500 }, { rowId: 'after', layoutY: 1000 }];
  const original = f.open('first', {}, rows);
  f.flushLayout();
  scroll.scrollTop = 550;
  original.onScroll();
  f.open('other');
  f.location.hash = '#/session/first';
  f.controller.route();
  const st = f.convo;
  st.transcript.pendingDetails = true;
  st.ws.opts.onSnapshot({ revision: 10, sequence: 100, state: sessionState(), rows: [rows[0], { rowId: 'after', layoutY: 200 }] });
  f.calls.images.length = 0;
  f.flushLayout();
  assert.equal(st.restoringReading, true);
  assert.equal(st.readingAnchor.rowId, 'nested');
  assert.equal(f.calls.images.length, 0);
  st.transcript.pendingDetails = false;
  st.transcript.rebuild(rows.map(row => ({ ...row, layoutY: row.layoutY + 200 })));
  assert.equal(f.calls.images.length, 0);
  f.flushLayout();
  assert.equal(scroll.scrollTop, 750);
  assert.equal(st.restoringReading, false);
  assert.deepEqual(f.calls.images, [{ transcript: st.transcript, scrollTop: 750 }]);
});

test('switching sessions before the first layout cannot resume the disposed transcript viewport', () => {
  const f = createFixture(), first = f.open('first'), second = f.open('second');
  first.refreshLayout();
  assert.equal(first.transcript.disposed, true);
  assert.equal(f.calls.images.length, 0);
  f.flushLayout();
  assert.equal(f.calls.images.length, 1);
  assert.equal(f.calls.images[0].transcript, second.transcript);
});

test('clicking an unhydrated thumbnail leases the original and keeps the overlay loading until image decode succeeds', async () => {
  const f = createFixture(), lease = originalImageLease();
  f.handlers.acquireArtifactObjectUrl = () => lease;
  f.open();
  const thumb = imageThumb('not-hydrated');
  assert.equal(thumb.dataset.url, undefined);
  const click = clickImage(f, thumb), image = f.node('overlay-img');
  assert.equal(click.event.defaultPrevented, true);
  assert.deepEqual(f.calls.artifacts[0].args, [thumb.dataset.artifact, 'image/png']);
  assert.equal(f.node('img-overlay').hidden, false);
  assert.equal(image.hidden, true);
  assert.equal(image.src, '');
  assert.equal(image.alt, 'not-hydrated.png');
  assert.equal(f.node('overlay-status').hidden, false);
  assert.match(f.node('overlay-status').textContent, /Loading image/);
  assert.equal(f.node('overlay-retry').hidden, true);
  lease.resolve('blob:original-not-thumbnail');
  await click.done;
  assert.equal(image.src, 'blob:original-not-thumbnail');
  assert.equal(image.hidden, true);
  assert.equal(f.node('overlay-status').hidden, false);
  assert.equal(typeof image.onload, 'function');
  assert.equal(typeof image.onerror, 'function');
  image.onload();
  assert.equal(image.hidden, false);
  assert.equal(f.node('overlay-status').hidden, true);
  assert.equal(f.node('overlay-retry').hidden, true);
  assert.equal(lease.releases, 0);
  f.node('overlay-close').onclick();
  assertOverlayClosed(f);
  assert.equal(lease.releases, 1);
});

test('the overlay ignores thumbnail blob URLs and leaves error-thumbnail clicks available for their own retry binding', async () => {
  const f = createFixture(), lease = originalImageLease();
  f.handlers.acquireArtifactObjectUrl = () => lease;
  f.open();
  const thumb = imageThumb('loaded-thumb', { url: 'blob:small-preview-must-not-open' });
  const click = clickImage(f, thumb);
  assert.deepEqual(f.calls.artifacts[0].args, [thumb.dataset.artifact, 'image/png']);
  lease.resolve('blob:full-size');
  await click.done;
  assert.equal(f.node('overlay-img').src, 'blob:full-size');
  f.node('overlay-img').onload();
  const failed = clickImage(f, imageThumb('failed-thumb', { error: true, url: 'blob:failed-preview' }));
  await failed.done;
  assert.equal(failed.event.defaultPrevented, false);
  assert.equal(f.calls.artifacts.length, 1);
  assert.equal(lease.releases, 0);
  assert.equal(f.node('overlay-img').src, 'blob:full-size');
  f.convo.closeOverlay();
});

for (const method of ['button', 'backdrop', 'escape']) {
  test(`${method} close releases a pending original, clears handlers, and ignores its late completion`, async () => {
    const f = createFixture(), lease = originalImageLease();
    f.handlers.acquireArtifactObjectUrl = () => lease;
    f.open();
    const click = clickImage(f, imageThumb(`pending-${method}`));
    let closes = 0;
    const close = f.convo.closeOverlay;
    f.convo.closeOverlay = () => { closes++; close(); };
    if (method === 'button') f.node('overlay-close').onclick();
    else if (method === 'backdrop') {
      f.node('img-overlay').onclick({ target: f.node('overlay-img') });
      assert.equal(lease.releases, 0);
      f.node('img-overlay').onclick({ target: f.node('img-overlay') });
    } else f.node('document').fire('keydown', { key: 'Escape' });
    if (method === 'escape') assert.equal(closes, 1);
    assertOverlayClosed(f);
    assert.equal(lease.releases, 1);
    assert.equal(f.convo.overlaySource, null);
    lease.resolve('blob:late-after-close');
    await click.done;
    assertOverlayClosed(f);
    f.node('overlay-retry').onclick();
    assert.equal(f.calls.artifacts.length, 1);
  });
}

for (const completion of ['resolve', 'reject']) {
  test(`switching overlay sources releases the old original and ignores its late ${completion}`, async () => {
    const f = createFixture(), first = originalImageLease(), second = originalImageLease();
    const leases = [first, second];
    f.handlers.acquireArtifactObjectUrl = () => leases.shift();
    f.open();
    const old = clickImage(f, imageThumb('old-source'));
    const current = clickImage(f, imageThumb('current-source'));
    assert.equal(first.releases, 1);
    assert.equal(second.releases, 0);
    assert.equal(f.node('overlay-img').alt, 'current-source.png');
    if (completion === 'resolve') first.resolve('blob:obsolete-original');
    else first.reject(new Error('obsolete request failure'));
    await old.done;
    assert.equal(f.node('overlay-img').src, '');
    assert.equal(f.node('overlay-status').hidden, false);
    assert.match(f.node('overlay-status').textContent, /Loading image/);
    assert.equal(f.node('overlay-retry').hidden, true);
    second.resolve('blob:current-original');
    await current.done;
    f.node('overlay-img').onload();
    assert.equal(f.node('overlay-img').src, 'blob:current-original');
    assert.equal(f.node('overlay-img').hidden, false);
    assert.equal(first.releases, 1);
    f.convo.closeOverlay();
    assert.equal(second.releases, 1);
  });
}

test('stale image onload and onerror callbacks cannot hide or fail another image, including after close', async () => {
  const f = createFixture(), first = originalImageLease(), second = originalImageLease(), leases = [first, second];
  f.handlers.acquireArtifactObjectUrl = () => leases.shift();
  f.open();
  const old = clickImage(f, imageThumb('decoded-old'));
  first.resolve('blob:decoded-old');
  await old.done;
  const image = f.node('overlay-img'), oldLoad = image.onload, oldError = image.onerror;
  const current = clickImage(f, imageThumb('decoded-current'));
  assert.equal(image.onload, null);
  assert.equal(image.onerror, null);
  assert.equal(image.src, '');
  oldLoad(); oldError();
  assert.match(f.node('overlay-status').textContent, /Loading image/);
  assert.equal(f.node('overlay-retry').hidden, true);
  second.resolve('blob:decoded-current');
  await current.done;
  const currentLoad = image.onload, currentError = image.onerror;
  oldLoad(); oldError();
  assert.equal(image.hidden, true);
  assert.equal(image.src, 'blob:decoded-current');
  currentLoad();
  assert.equal(image.hidden, false);
  oldError();
  assert.equal(image.hidden, false);
  f.convo.closeOverlay();
  currentLoad(); currentError(); oldLoad(); oldError();
  assertOverlayClosed(f);
  assert.equal(first.releases, 1);
  assert.equal(second.releases, 1);
});

for (const failure of ['fetch', 'decode']) {
  test(`an original ${failure} failure offers a local retry of the same URI without changing conversation state`, async () => {
    const f = createFixture(), first = originalImageLease(), retry = originalImageLease(), leases = [first, retry];
    f.handlers.acquireArtifactObjectUrl = () => leases.shift();
    const st = f.open();
    const thumb = imageThumb(`retry-${failure}`), click = clickImage(f, thumb);
    if (failure === 'fetch') first.reject(new Error('image network failure'));
    else first.resolve('blob:invalid-original');
    await click.done;
    if (failure === 'decode') f.node('overlay-img').onerror();
    assert.equal(f.node('img-overlay').hidden, false);
    assert.equal(f.node('overlay-img').src, '');
    assert.equal(f.node('overlay-img').hidden, true);
    assert.equal(f.node('overlay-img').onload, null);
    assert.equal(f.node('overlay-img').onerror, null);
    assert.equal(f.node('overlay-status').textContent, 'Image unavailable');
    assert.equal(f.node('overlay-status').hidden, false);
    assert.equal(f.node('overlay-retry').hidden, false);
    assert.equal(first.releases, 1);
    assert.equal(st.overlayLease, null);
    assert.deepEqual(st.overlaySource, { uri: thumb.dataset.artifact, mime: 'image/png', name: thumb.dataset.name });
    assert.equal(f.composer.ready, true);
    assert.equal(f.calls.reconnects, 0);
    assert.equal(f.calls.details.length, 0);
    f.node('overlay-retry').onclick();
    assert.equal(f.calls.artifacts.length, 2);
    assert.deepEqual(f.calls.artifacts[1].args, f.calls.artifacts[0].args);
    assert.equal(f.node('overlay-retry').hidden, true);
    assert.match(f.node('overlay-status').textContent, /Loading image/);
    retry.resolve('blob:retried-original');
    await flushImages();
    f.node('overlay-img').onload();
    assert.equal(f.node('overlay-img').hidden, false);
    assert.equal(f.node('overlay-img').src, 'blob:retried-original');
    assert.equal(f.node('overlay-status').hidden, true);
    f.convo.closeOverlay();
    assert.equal(first.releases, 1);
    assert.equal(retry.releases, 1);
  });
}

test('navigation releases the original lease and an obsolete rejection cannot fail the next session image', async () => {
  const f = createFixture(), first = originalImageLease(), second = originalImageLease(), leases = [first, second];
  f.handlers.acquireArtifactObjectUrl = () => leases.shift();
  const original = f.open('first'), old = clickImage(f, imageThumb('departing-session'));
  f.open('second');
  assert.equal(original.disposed, true);
  assert.equal(first.releases, 1);
  assertOverlayClosed(f);
  const current = clickImage(f, imageThumb('arriving-session'));
  first.reject(new Error('late disconnected image'));
  await old.done;
  assert.equal(f.node('overlay-retry').hidden, true);
  assert.match(f.node('overlay-status').textContent, /Loading image/);
  second.resolve('blob:active-session-original');
  await current.done;
  f.node('overlay-img').onload();
  assert.equal(f.node('overlay-img').src, 'blob:active-session-original');
  f.node('document').fire('keydown', { key: 'Escape' });
  assertOverlayClosed(f);
  assert.equal(second.releases, 1);
});

test('switching sessions restores disclosure metadata and a row-relative reading position', () => {
  const f = createFixture();
  const scroll = f.node('transcript-scroll');
  Object.assign(scroll, { scrollHeight: 2000, clientHeight: 200, getBoundingClientRect: () => ({ top: 20 }) });
  const rows = [{ rowId: 'a', layoutY: 0 }, { rowId: 'b', layoutY: 500 }, { rowId: 'c', layoutY: 1000 }];
  const first = f.open('first', {}, rows);
  f.flushLayout();
  scroll.scrollTop = 550;
  first.onScroll();
  first.transcript.viewState = [{ rowId: 'b', open: ['turn'], all: ['input'] }];
  f.open('other');
  const saved = f.controller.readingStates.get('first');
  assert.equal(saved.anchor.rowId, 'b');
  assert.equal(saved.anchor.offset, -50);
  assert.equal(saved.followLatest, false);
  assert.deepEqual(Object.keys(saved).sort(), ['anchor', 'followLatest', 'transcript']);
  const restored = f.open('first', {}, rows.map(row => ({ ...row, layoutY: row.layoutY + 200 })));
  f.flushLayout();
  assert.equal(restored.followLatest, false);
  assert.equal(scroll.scrollTop, 750);
  assert.deepEqual(restored.transcript.viewState, first.transcript.viewState);
});

test('reconnect snapshots preserve the reading anchor when preceding content changes height', () => {
  const f = createFixture();
  const scroll = f.node('transcript-scroll');
  Object.assign(scroll, { scrollHeight: 2000, clientHeight: 200, getBoundingClientRect: () => ({ top: 0 }) });
  const rows = [{ rowId: 'a', layoutY: 0 }, { rowId: 'b', layoutY: 500 }, { rowId: 'c', layoutY: 1000 }];
  const st = f.open('first', {}, rows);
  f.flushLayout();
  scroll.scrollTop = 550;
  st.onScroll();
  st.ws.opts.onSnapshot({ revision: 11, sequence: 200, state: sessionState(), rows: rows.map(row => ({ ...row, layoutY: row.layoutY + 300 })) });
  f.flushLayout();
  assert.equal(st.followLatest, false);
  assert.equal(scroll.scrollTop, 850);
  assert.equal(st.readingAnchor.rowId, 'b');
});

test('asynchronously expanded content keeps the current reading anchor without following the tail', () => {
  const f = createFixture();
  const scroll = f.node('transcript-scroll');
  Object.assign(scroll, { scrollHeight: 2000, clientHeight: 200, getBoundingClientRect: () => ({ top: 0 }) });
  const rows = [{ rowId: 'a', layoutY: 0 }, { rowId: 'b', layoutY: 500 }];
  const st = f.open('first', {}, rows);
  f.flushLayout();
  scroll.scrollTop = 550;
  st.onScroll();
  st.transcript.opts.onBeforeChange();
  rows[1].layoutY += 120;
  st.transcript.opts.onChange();
  f.flushLayout();
  assert.equal(scroll.scrollTop, 670);
  assert.equal(st.followLatest, false);
});

test('layout callbacks before the first snapshot cannot overwrite a saved reading anchor', () => {
  const f = createFixture();
  const scroll = f.node('transcript-scroll');
  Object.assign(scroll, { scrollHeight: 2000, clientHeight: 200, getBoundingClientRect: () => ({ top: 0 }) });
  const rows = [{ rowId: 'a', layoutY: 0 }, { rowId: 'b', layoutY: 500 }];
  const st = f.open('first', {}, rows);
  f.flushLayout();
  scroll.scrollTop = 550;
  st.onScroll();
  f.open('other');
  f.location.hash = '#/session/first';
  f.controller.route();
  const waiting = f.convo;
  waiting.refreshLayout();
  f.flushLayout();
  assert.equal(waiting.readingAnchor.rowId, 'b');
  waiting.ws.opts.onSnapshot({ revision: 10, sequence: 100, state: sessionState(), rows });
  f.flushLayout();
  assert.equal(scroll.scrollTop, 550);
});

test('nested reading anchors survive the placeholder layout until restored details finish loading', () => {
  const f = createFixture();
  const scroll = f.node('transcript-scroll');
  Object.assign(scroll, { scrollHeight: 2000, clientHeight: 200, getBoundingClientRect: () => ({ top: 0 }) });
  const full = [{ rowId: 'parent', layoutY: 0 }, { rowId: 'detail', layoutY: 500 }, { rowId: 'after', layoutY: 1000 }];
  const first = f.open('first', {}, full);
  f.flushLayout();
  scroll.scrollTop = 550;
  first.onScroll();
  f.open('other');
  f.location.hash = '#/session/first';
  f.controller.route();
  const restoring = f.convo;
  restoring.transcript.pendingDetails = true;
  restoring.ws.opts.onSnapshot({ revision: 10, sequence: 100, state: sessionState(), rows: [full[0], { rowId: 'after', layoutY: 200 }] });
  f.flushLayout();
  assert.equal(restoring.readingAnchor.rowId, 'detail');
  assert.equal(restoring.restoringReading, true);
  restoring.transcript.pendingDetails = false;
  restoring.transcript.rebuild(full.map(row => ({ ...row, layoutY: row.layoutY + 200 })));
  f.flushLayout();
  assert.equal(scroll.scrollTop, 750);
  assert.equal(restoring.readingAnchor.rowId, 'detail');
  assert.equal(restoring.restoringReading, false);
});

test('deliberate scrolling while details load cancels automatic return to the old reading anchor', () => {
  const f = createFixture();
  const scroll = f.node('transcript-scroll');
  Object.assign(scroll, { scrollHeight: 2000, clientHeight: 200, getBoundingClientRect: () => ({ top: 0 }) });
  const full = [{ rowId: 'parent', layoutY: 0 }, { rowId: 'detail', layoutY: 500 }, { rowId: 'after', layoutY: 1000 }];
  const first = f.open('first', {}, full);
  f.flushLayout();
  scroll.scrollTop = 550;
  first.onScroll();
  f.open('other');
  f.location.hash = '#/session/first';
  f.controller.route();
  const restoring = f.convo;
  restoring.transcript.pendingDetails = true;
  restoring.ws.opts.onSnapshot({ revision: 10, sequence: 100, state: sessionState(), rows: [full[0], { rowId: 'after', layoutY: 200 }] });
  f.flushLayout();
  scroll.scrollTop = 200;
  restoring.onScroll();
  assert.equal(restoring.readingAnchor.rowId, 'after');
  assert.equal(restoring.restoringReading, false);
  restoring.transcript.pendingDetails = false;
  restoring.transcript.rebuild(full);
  f.flushLayout();
  assert.equal(restoring.readingAnchor.rowId, 'after');
  assert.equal(scroll.scrollTop, 1000);
});

test('reading state retention is bounded and new sessions continue to follow the latest content', () => {
  const f = createFixture();
  f.node('transcript-scroll').scrollHeight = 2000;
  for (let i = 0; i < 35; i++) f.open(`session-${i}`);
  f.flushLayout();
  assert.equal(f.controller.readingStates.size, 32);
  assert.equal(f.controller.readingStates.has('session-0'), false);
  assert.equal(f.controller.readingStates.has('session-33'), true);
  assert.equal(f.convo.followLatest, true);
  assert.equal(f.node('transcript-scroll').scrollTop, 2000);
});

test('compact streams require both renderer and detail API support', () => {
  const capable = createFixture();
  assert.equal(capable.open().ws.opts.compact, true);
  const oldRenderer = createFixture({ compactCapable: false });
  assert.equal(oldRenderer.open().ws.opts.compact, false);
  const oldApi = createFixture({ detailsAvailable: false });
  const st = oldApi.open();
  assert.equal(st.ws.opts.compact, false);
  assert.equal(st.transcript.opts.loadTurn, undefined);
});

test('detail loaders forward version and cancellation and stale content reconnects only its active session', async () => {
  const f = createFixture();
  const st = f.open();
  const options = { version: 'turn-version', signal: new AbortController().signal };
  assert.deepEqual(await st.transcript.opts.loadTurn('user-row', options), { rows: [], version: 'turn-version', sequence: 100 });
  assert.equal(f.calls.details[0].id, 'original');
  assert.equal(f.calls.details[0].rowId, 'user-row');
  assert.equal(f.calls.details[0].options, options);
  f.handlers.getRowDetails = async () => { throw Object.assign(new Error('Changed'), { status: 409 }); };
  await assert.rejects(st.transcript.opts.loadRow('tool-row', { version: 'old' }), { status: 409 });
  assert.equal(f.calls.reconnects, 1);
  f.open('other');
  await assert.rejects(st.transcript.opts.loadRow('tool-row', { version: 'old' }), { status: 409 });
  assert.equal(f.calls.reconnects, 1);
});

test('history negotiation requires the renderer and compact details, and legacy snapshots keep paging hidden', () => {
  const capable = createFixture();
  const st = capable.open();
  assert.equal(st.ws.opts.history, true);
  assert.equal(capable.node('history-before').hidden, true);
  assert.equal(capable.node('history-after').hidden, true);
  assert.equal(createFixture({ historyCapable: false }).open().ws.opts.history, false);
  assert.equal(createFixture({ compactCapable: false }).open().ws.opts.history, false);
  assert.equal(createFixture({ detailsAvailable: false }).open().ws.opts.history, false);
});

test('older-page prefetch preserves the visible row and does not block live updates or sending', async () => {
  const f = createFixture();
  const scroll = f.node('transcript-scroll');
  Object.assign(scroll, { scrollHeight: 2000, clientHeight: 200, getBoundingClientRect: () => ({ top: 20 }) });
  const rows = Array.from({ length: 20 }, (_, i) => ({ rowId: `row-${i}` }));
  const st = f.open('first', {}, rows, { before: 'row-0', after: null });
  f.flushLayout();
  const page = deferred();
  f.handlers.history = () => page.promise;
  scroll.scrollTop = 150;
  st.onScroll();
  assert.deepEqual(f.calls.history, [{ before: 'row-0' }]);
  assert.equal(f.node('history-before').disabled, true);
  assert.equal(f.composer.ready, true);
  const visible = st.transcript.els.get('row-1');
  st.ws.opts.onDelta({ sequence: 101, ops: [{ $type: 'row_text_appended', rowId: 'live', delta: 'still live' }] });
  f.composer.setText('continue');
  await f.sendSession();
  assert.equal(f.calls.commands[0].$type, 'send_text');
  page.resolve({ direction: 'before', sequence: 100,
    rows: Array.from({ length: 5 }, (_, i) => ({ rowId: `older-${i}` })), history: { before: null, after: null } });
  await new Promise(setImmediate);
  f.flushLayout();
  assert.equal(scroll.scrollTop, 650);
  assert.equal(st.readingAnchor.rowId, 'row-1');
  assert.equal(st.transcript.els.get('row-1'), visible);
  assert.equal(st.followLatest, false);
  assert.equal(st.lastSeq, 101);
  assert.equal(f.node('history-before').hidden, true);
  assert.equal(f.composer.ready, true);
  assert.equal(f.calls.transcript.filter(([type]) => type === 'snapshot').length, 1);
});

test('a historical window prefetches newer messages without mistaking its bottom for the live tail', async () => {
  const f = createFixture();
  const scroll = f.node('transcript-scroll');
  Object.assign(scroll, { scrollHeight: 2000, clientHeight: 200, getBoundingClientRect: () => ({ top: 0 }) });
  const st = f.open('old', {}, Array.from({ length: 20 }, (_, i) => ({ rowId: `row-${i}` })),
    { before: null, after: 'row-19' });
  f.flushLayout();
  const page = deferred();
  f.handlers.history = () => page.promise;
  scroll.scrollTop = 1800;
  st.onScroll();
  assert.equal(st.followLatest, false);
  assert.equal(f.node('jump-latest').hidden, false);
  assert.deepEqual(f.calls.history, [{ after: 'row-19' }]);
  page.resolve({ direction: 'after', sequence: 100, rows: [{ rowId: 'newer-1' }, { rowId: 'newer-2' }, { rowId: 'newer-3' }],
    history: { before: null, after: null } });
  await new Promise(setImmediate);
  f.flushLayout();
  assert.equal(scroll.scrollTop, 1800);
  assert.equal(st.followLatest, false);
  assert.equal(f.node('history-after').hidden, true);
  assert.equal([...st.transcript.els.keys()].at(-1), 'newer-3');
});

test('jump to latest requests only the latest window and replaces the historical view', async () => {
  const f = createFixture();
  const scroll = f.node('transcript-scroll');
  Object.assign(scroll, { scrollHeight: 2000, clientHeight: 200 });
  const st = f.open('old', {}, [{ rowId: 'old' }], { before: null, after: 'old' });
  f.flushLayout();
  f.handlers.history = async () => ({ direction: 'latest', sequence: 100,
    rows: [{ rowId: 'latest' }], history: { before: 'latest', after: null } });
  f.node('jump-latest').onclick();
  await new Promise(setImmediate);
  f.flushLayout();
  assert.deepEqual(f.calls.history, [{ latest: true }]);
  assert.deepEqual([...st.transcript.els.keys()], ['latest']);
  assert.equal(st.followLatest, true);
  assert.equal(st.readingAnchor, null);
  assert.equal(scroll.scrollTop, scroll.scrollHeight);
});

test('jump to latest waits for an in-flight page without issuing overlapping history requests', async () => {
  const f = createFixture();
  const scroll = f.node('transcript-scroll');
  Object.assign(scroll, { scrollHeight: 2000, clientHeight: 200 });
  const st = f.open('old', {}, [{ rowId: 'old' }], { before: 'old', after: 'old' });
  const older = deferred();
  f.handlers.history = request => request.latest
    ? Promise.resolve({ direction: 'latest', sequence: 100, rows: [{ rowId: 'latest' }], history: { before: 'latest', after: null } })
    : older.promise;
  f.node('history-before').onclick();
  f.node('jump-latest').onclick();
  assert.deepEqual(f.calls.history, [{ before: 'old' }]);
  older.resolve({ direction: 'before', sequence: 100, rows: [{ rowId: 'older' }], history: { before: null, after: 'old' } });
  await new Promise(setImmediate);
  f.flushLayout();
  assert.deepEqual(f.calls.history, [{ before: 'old' }, { latest: true }]);
  assert.deepEqual([...st.transcript.els.keys()], ['latest']);
  assert.equal(st.followLatest, true);
  assert.equal(st.historyRequest, null);
});

test('history reconnects target the saved containing row instead of replaying all earlier pages', () => {
  const f = createFixture();
  const scroll = f.node('transcript-scroll');
  Object.assign(scroll, { scrollHeight: 2000, clientHeight: 200, getBoundingClientRect: () => ({ top: 0 }) });
  const rows = [{ rowId: 'a', layoutY: 0 }, { rowId: 'b', layoutY: 500 }];
  const first = f.open('first', {}, rows);
  f.flushLayout();
  scroll.scrollTop = 550;
  first.onScroll();
  assert.equal(first.ws.opts.anchor(), 'b');
  first.transcript.els.get('b').parentElement = { closest: () => ({ dataset: { rowId: 'containing-turn' },
    getBoundingClientRect: () => ({ top: -100 }) }) };
  first.onScroll();
  assert.equal(first.ws.opts.anchor(), 'containing-turn');
  f.open('other');
  const restored = f.open('first', {}, rows);
  assert.equal(restored.ws.opts.anchor(), 'containing-turn');
  restored.followLatest = true;
  assert.equal(restored.ws.opts.anchor(), null);
});

test('history errors are retryable without disabling the composer or creating a retry loop', async () => {
  const f = createFixture();
  const scroll = f.node('transcript-scroll');
  Object.assign(scroll, { scrollHeight: 2000, clientHeight: 200 });
  const st = f.open('first', {}, [{ rowId: 'first' }], { before: 'first', after: null });
  f.flushLayout();
  f.handlers.history = async () => { throw new Error('Network interrupted'); };
  scroll.scrollTop = 150;
  st.onScroll();
  await new Promise(setImmediate);
  f.flushLayout();
  assert.equal(f.node('history-before').textContent, 'Retry loading older messages');
  assert.equal(f.node('history-before').disabled, false);
  assert.equal(f.composer.ready, true);
  assert.equal(f.calls.history.length, 1);
  f.handlers.history = async () => ({ direction: 'before', sequence: 100, rows: [], history: { before: null, after: null } });
  f.node('history-before').onclick();
  await new Promise(setImmediate);
  assert.equal(f.calls.history.length, 2);
  assert.equal(f.node('history-before').hidden, true);
});

test('an authoritative reconnect snapshot supersedes an in-flight historical page', async () => {
  const f = createFixture();
  const scroll = f.node('transcript-scroll');
  Object.assign(scroll, { scrollHeight: 2000, clientHeight: 200 });
  const st = f.open('first', {}, [{ rowId: 'first' }], { before: 'first', after: null });
  f.flushLayout();
  const page = deferred();
  f.handlers.history = () => page.promise;
  scroll.scrollTop = 150;
  st.onScroll();
  st.ws.opts.onSnapshot({ revision: 20, sequence: 200, state: sessionState(), rows: [{ rowId: 'current' }],
    history: { before: null, after: null } });
  page.resolve({ direction: 'before', sequence: 100, rows: [{ rowId: 'stale' }], history: { before: null, after: null } });
  await new Promise(setImmediate);
  assert.deepEqual([...st.transcript.els.keys()], ['current']);
  assert.equal(st.lastSeq, 200);
  assert.equal(st.revision, 20);
  assert.equal(st.historyRequest, null);
});

test('leaving a session ignores its delayed page and stale cursors reconnect only the active session', async () => {
  const f = createFixture();
  const scroll = f.node('transcript-scroll');
  Object.assign(scroll, { scrollHeight: 2000, clientHeight: 200 });
  const st = f.open('first', {}, [{ rowId: 'first' }], { before: 'first', after: null });
  f.flushLayout();
  const page = deferred();
  f.handlers.history = () => page.promise;
  scroll.scrollTop = 150;
  st.onScroll();
  f.open('other', {}, [{ rowId: 'other' }]);
  page.reject(Object.assign(new Error('Changed'), { code: 'stale_history' }));
  await new Promise(setImmediate);
  assert.deepEqual([...f.convo.transcript.els.keys()], ['other']);
  assert.equal(f.calls.reconnects, 0);
  f.handlers.history = async () => { throw Object.assign(new Error('Changed'), { code: 'stale_history' }); };
  const current = f.open('current', {}, [{ rowId: 'current' }], { before: 'current', after: null });
  f.flushLayout();
  scroll.scrollTop = 150;
  current.onScroll();
  await new Promise(setImmediate);
  assert.equal(f.calls.reconnects, 1);
  assert.equal(f.composer.ready, true);
});

test('append operations apply once above the snapshot watermark and reconnect snapshots replace the baseline', () => {
  const f = createFixture();
  const st = f.open();
  f.calls.transcript.length = 0;
  const appended = { $type: 'row_text_appended', rowId: 'assistant', delta: ' exact\n中文 ', at: 'new-at' };
  st.ws.opts.onDelta({ sequence: 100, ops: [appended] });
  st.ws.opts.onDelta({ sequence: 101, ops: [appended] });
  st.ws.opts.onDelta({ sequence: 101, ops: [appended] });
  assert.deepEqual(f.calls.transcript, [['text', appended]]);
  const final = { $type: 'assistant_message', rowId: 'assistant', isStreaming: false, parts: [{ $type: 'text', text: 'authoritative' }] };
  st.ws.opts.onDelta({ sequence: 102, ops: [{ $type: 'row_upserted', row: final }, { $type: 'row_removed', rowId: 'obsolete' }] });
  st.ws.opts.onSnapshot({ revision: 11, sequence: 200, state: sessionState(), rows: [final] });
  st.ws.opts.onDelta({ sequence: 199, ops: [appended] });
  assert.deepEqual(f.calls.transcript.slice(1), [['upsert', final], ['remove', 'obsolete'], ['snapshot', [final]]]);
  st.ws.opts.onDelta({ sequence: 201, ops: [{ ...appended, rowId: 'missing' }] });
  assert.equal(f.calls.reconnects, 1);
});

test('new tasks default to full access when no valid mode was saved', async () => {
  for (const storedMode of [undefined, '', 'unknown']) {
    const f = createFixture({ storedMode });
    assert.equal(f.controller.shell.draftMode, 'yolo');
    assert.equal(f.draft.mode, 'yolo');
    f.draft.setText('new task');
    await f.sendDraft();
    assert.equal(f.calls.creates[0].mode, 'yolo');
  }
});

test('new tasks preserve explicitly saved permission modes', async () => {
  for (const storedMode of ['build', 'edit', 'yolo']) {
    const f = createFixture({ storedMode });
    assert.equal(f.draft.mode, storedMode);
    f.draft.setText('new task');
    await f.sendDraft();
    assert.equal(f.calls.creates[0].mode, storedMode);
  }
});

test('existing sessions keep their authoritative permission mode instead of the new-task default', () => {
  for (const mode of ['Build', 'Edit', 'Yolo']) {
    const f = createFixture();
    f.open('existing', { mode });
    assert.equal(f.composer.mode, mode.toLowerCase());
    assert.equal(f.calls.commands.length, 0);
  }
});

for (const outcome of ['accepted', 'rejected', 'unconfirmed']) {
  test(`navigation during a bare workflow toggle (${outcome}) recovers images and full queued intent without retrying the toggle`, async () => {
    const f = createFixture();
    f.open();
    const original = withdrawnDraft('/orchestrator', outcome === 'rejected' ? explicitModel : null);
    f.withdraw(original);
    const dispatched = deferred();
    const reply = deferred();
    f.handlers.sendCommand = (_id, command) => {
      if (command.$type !== 'set_workflows') return accepted();
      dispatched.resolve(command);
      return reply.promise;
    };
    const sending = f.sendSession();
    assert.deepEqual((await dispatched.promise).active, ['review', 'audit', 'orchestrator']);
    assert.equal(f.composer.getText(), '');
    assert.deepEqual(f.composer.getImages(), [image]);
    f.open('other');
    if (outcome === 'unconfirmed') reply.reject(new Error('Disconnected'));
    else reply.resolve({ accepted: outcome === 'accepted', revision: 11, rejectionReason: 'Rejected' });
    await sending;

    const expectedIntent = { ...original.intent,
      workflows: outcome === 'accepted' ? ['review', 'audit', 'orchestrator'] : original.intent.workflows };
    const [recovered] = f.controller.pendingRestores.get('original');
    assert.equal(recovered.text, '');
    assert.equal(recovered.literalText, null);
    assert.deepEqual(recovered.images, [image]);
    assert.deepEqual(recovered.intent, expectedIntent);
    assert.deepEqual(recovered.modelSelection, original.modelSelection);
    if (outcome !== 'accepted') assert.match(recovered.error, /Rejected|could not be confirmed/);

    f.open('original');
    assert.equal(f.controller.pendingRestores.has('original'), false);
    assert.equal(f.calls.commands.length, 1);
    assert.equal(f.composer.getText(), '');
    assert.deepEqual(f.convo.draftIntent, expectedIntent);
    if (original.modelSelection === null) assert.deepEqual(f.composer.getModelSelection(), shownModel);
    await f.sendSession();
    assert.deepEqual(f.calls.commands.map(command => command.$type), ['set_workflows', 'send_text']);
    assert.equal(f.calls.commands[1].text, '');
    assertSentIntent(f.calls.commands[1], expectedIntent);
  });
}

test('a newer WebSocket workflow state wins over a delayed toggle ack and survives the next full-set toggle', async () => {
  const f = createFixture();
  const st = f.open();
  const dispatched = deferred();
  const reply = deferred();
  f.handlers.sendCommand = (_id, command) => { dispatched.resolve(command); return reply.promise; };
  f.composer.setText('/orchestrator');
  const sending = f.sendSession();
  assert.deepEqual((await dispatched.promise).active, ['review', 'orchestrator']);
  const newerWorkflows = ['review', 'orchestrator', 'desktop-workflow'];
  st.ws.opts.onDelta({ sequence: 101, ops: [{ $type: 'state_patch', state: sessionState({ activeWorkflows: newerWorkflows }) }] });
  reply.resolve(accepted());
  await sending;
  assert.deepEqual(st.state.activeWorkflows, newerWorkflows);
  assert.deepEqual(f.composer.workflows, newerWorkflows);

  f.handlers.sendCommand = async () => accepted(12);
  f.composer.setText('/orchestrator');
  await f.sendSession();
  assert.deepEqual(f.calls.commands[1].active, ['review', 'desktop-workflow']);
  assert.deepEqual(st.state.activeWorkflows, ['review', 'desktop-workflow']);
});

test('draft session-creation failure retries the consumed nested slash literally without losing images or plan intent', async () => {
  const f = createFixture();
  f.draft.opts.onModeChange('edit');
  f.draft.setText('/PLAN /orchestrator task');
  f.draft.setImages([image]);
  f.handlers.createSession = async () => { throw Object.assign(new Error('Offline'), { status: 0 }); };
  await f.sendDraft();
  assert.equal(f.draft.getText(), '/orchestrator task');
  assert.equal(f.controller.shell.draftLiteralText, '/orchestrator task');
  assert.deepEqual(f.draft.getImages(), [image]);
  assert.equal(f.calls.commands.length, 0);
  assert.equal(f.draft.disabled, false);

  f.handlers.createSession = async () => ({ id: 'created' });
  await f.sendDraft();
  assert.deepEqual(f.draft.parseCalls, ['/PLAN /orchestrator task']);
  assert.deepEqual(f.calls.creates, Array.from({ length: 2 }, () => ({
    workspaceRoot: 'C:/workspace', mode: 'edit', planEnabled: true, activeWorkflows: [],
  })));
  assert.equal(f.calls.commands[0].text, '/orchestrator task');
  assert.equal(f.calls.commands[0].planEnabled, true);
  assert.deepEqual(f.calls.commands[0].images, [image]);
  assert.equal(f.calls.commands[0].modelSelection ?? null, null);
  assert.equal(f.controller.shell.draftLiteralText, null);
});

test('a failed first send carries its literal nested slash and null model intent from the draft into the created session', async () => {
  const f = createFixture();
  f.draft.setText('/plan /orchestrator task');
  f.draft.setImages([image]);
  f.handlers.sendCommand = async () => { throw new Error('Disconnected'); };
  await f.sendDraft();
  f.open('created', { planEnabled: true, activeWorkflows: [] });
  assert.equal(f.composer.getText(), '/orchestrator task');
  assert.equal(f.convo.literalText, '/orchestrator task');
  const expectedIntent = { mode: null, planEnabled: true, modelSelection: null, delivery: 'Queue', workflows: [] };
  assert.deepEqual(f.convo.draftIntent, expectedIntent);
  f.handlers.sendCommand = async () => accepted();
  await f.sendSession();
  assert.equal(f.calls.commands[1].text, '/orchestrator task');
  assertSentIntent(f.calls.commands[1], expectedIntent);
  assert.deepEqual(f.composer.parseCalls, []);
});

test('rejected existing-session nested slash sends retain the transformed literal and full withdrawn intent on retry', async () => {
  const f = createFixture();
  f.open();
  const original = withdrawnDraft('/plan /orchestrator task');
  f.withdraw(original);
  f.handlers.sendCommand = async () => ({ accepted: false, revision: 10, rejectionReason: 'Try again' });
  await f.sendSession();
  const expectedIntent = { ...original.intent, planEnabled: true };
  assert.equal(f.composer.getText(), '/orchestrator task');
  assert.equal(f.convo.literalText, '/orchestrator task');
  assert.deepEqual(f.convo.draftIntent, expectedIntent);
  assertSentIntent(f.calls.commands[0], expectedIntent);

  f.composer.setImages(f.composer.getImages());
  f.composer.setModelSelection(shownModel);
  assert.equal(f.convo.literalText, '/orchestrator task');
  assert.equal(f.convo.draftIntent.modelSelection, null);
  f.handlers.sendCommand = async () => accepted();
  await f.sendSession();
  assert.deepEqual(f.composer.parseCalls, ['/plan /orchestrator task']);
  assert.deepEqual(f.calls.commands.map(command => command.text), ['/orchestrator task', '/orchestrator task']);
  assertSentIntent(f.calls.commands[1], expectedIntent);
});

test('navigation during a rejected nested slash send preserves the literal marker and withdrawn intent until retry', async () => {
  const f = createFixture();
  f.open();
  const original = withdrawnDraft('/plan /orchestrator task');
  f.withdraw(original);
  const dispatched = deferred();
  const reply = deferred();
  f.handlers.sendCommand = (_id, command) => { dispatched.resolve(command); return reply.promise; };
  const sending = f.sendSession();
  await dispatched.promise;
  f.open('other');
  reply.resolve({ accepted: false, revision: 10 });
  await sending;
  f.open('original');
  assert.equal(f.convo.literalText, '/orchestrator task');
  assert.deepEqual(f.convo.draftIntent, { ...original.intent, planEnabled: true });
  f.handlers.sendCommand = async () => accepted();
  await f.sendSession();
  assert.equal(f.calls.commands[1].text, '/orchestrator task');
  assertSentIntent(f.calls.commands[1], { ...original.intent, planEnabled: true });
});

for (const context of ['draft', 'session']) {
  test(`editing the transformed ${context} text invalidates the literal marker and parses the new builtin`, async () => {
    const f = createFixture();
    if (context === 'draft') {
      f.draft.setText('/plan /orchestrator original');
      f.draft.setImages([image]);
      f.handlers.createSession = async () => { throw new Error('Unavailable'); };
      await f.sendDraft();
      f.draft.setText('/orchestrator revised');
      assert.equal(f.controller.shell.draftLiteralText, null);
      f.handlers.createSession = async () => ({ id: 'created' });
      await f.sendDraft();
      assert.deepEqual(f.calls.creates[1].activeWorkflows, ['orchestrator']);
      assert.equal(f.calls.commands[0].text, 'revised');
      assert.deepEqual(f.calls.commands[0].images, [image]);
    } else {
      f.open();
      const original = withdrawnDraft('/plan /orchestrator original', explicitModel);
      f.withdraw(original);
      f.handlers.sendCommand = async () => ({ accepted: false, revision: 10 });
      await f.sendSession();
      f.composer.setText('/orchestrator revised');
      assert.equal(f.convo.literalText, null);
      f.handlers.sendCommand = async () => accepted();
      await f.sendSession();
      assert.equal(f.calls.commands[1].text, 'revised');
      assertSentIntent(f.calls.commands[1], { ...original.intent, planEnabled: true,
        workflows: ['review', 'audit', 'orchestrator'] });
    }
  });
}

test('a session source switch during catalog loading retains the unconsumed withdrawn draft without sending to either session', async () => {
  const f = createFixture();
  f.open();
  const original = withdrawnDraft('/plan task');
  f.withdraw(original);
  const requested = deferred();
  const reply = deferred();
  f.handlers.getSlashCatalog = () => { requested.resolve(); return reply.promise; };
  const sending = f.sendSession();
  await requested.promise;
  f.open('other');
  reply.resolve(catalog);
  await sending;
  assert.equal(f.calls.commands.length, 0);
  f.open('original');
  assert.equal(f.composer.getText(), original.text);
  assert.equal(f.convo.literalText, null);
  assert.deepEqual(f.convo.draftIntent, original.intent);
  assert.deepEqual(f.composer.getImages(), [image]);
  f.handlers.getSlashCatalog = async () => catalog;
  await f.sendSession();
  assert.equal(f.calls.commands[0].sessionId, 'original');
  assert.equal(f.calls.commands[0].text, 'task');
  assertSentIntent(f.calls.commands[0], { ...original.intent, planEnabled: true });
});

for (const context of ['draft', 'session']) {
  test(`catalog failure keeps the ${context} slash draft and images intact without creating or sending`, async () => {
    const f = createFixture();
    const original = withdrawnDraft('/plan task');
    if (context === 'session') { f.open(); f.withdraw(original); }
    else { f.draft.setText(original.text); f.draft.setImages(original.images); }
    f.handlers.getSlashCatalog = async () => { throw new Error('Catalog unavailable'); };
    await (context === 'session' ? f.sendSession() : f.sendDraft());
    const composer = context === 'session' ? f.composer : f.draft;
    assert.equal(f.calls.creates.length, 0);
    assert.equal(f.calls.commands.length, 0);
    assert.equal(composer.getText(), original.text);
    assert.deepEqual(composer.getImages(), original.images);
    assert.equal(composer.disabled, false);
    if (context === 'session') {
      assert.deepEqual(f.convo.draftIntent, original.intent);
      assert.match(f.convo.restoreError, /Catalog unavailable/);
    } else assert.match(f.node('draft-error').textContent, /Catalog unavailable/);
  });
}

test('skills and ordinary slash-like text remain queueable verbatim while recognized builtins retain the busy draft', async () => {
  for (const text of ['/report 中文\nkeep "quotes"', '/planning', 'Before /plan After']) {
    const f = createFixture();
    f.open('original', { phase: 'running' });
    f.composer.setText(text);
    f.composer.setImages([image]);
    await f.sendSession();
    assert.equal(f.calls.commands[0].text, text);
    assert.deepEqual(f.calls.commands[0].images, [image]);
  }
  for (const text of ['/plan', '/plan task', '/orchestrator', '/orchestrator task']) {
    const f = createFixture();
    f.open('original', { phase: 'running' });
    f.composer.setText(text);
    f.composer.setImages([image]);
    await f.sendSession();
    assert.equal(f.calls.commands.length, 0);
    assert.equal(f.composer.getText(), text);
    assert.deepEqual(f.composer.getImages(), [image]);
    assert.match(f.convo.restoreError, /Stop the current turn/);
    assert.equal(f.composer.disabled, false);
  }
});
