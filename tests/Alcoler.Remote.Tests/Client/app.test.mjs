import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { SlashAutocomplete } from '../../../src/Alcoler.Remote/wwwroot/slash.js';
import { prepareSubmission } from '../../../src/Alcoler.Remote/wwwroot/submission.js';

const source = readFileSync(new URL('../../../src/Alcoler.Remote/wwwroot/app.js', import.meta.url), 'utf8');
const controllerStart = source.indexOf('const appEl =');
const startupStart = source.indexOf("window.addEventListener('hashchange'");
assert.ok(controllerStart >= 0 && startupStart > controllerStart);

const loadController = new Function('dependencies', `
  const { api, Composer, StreamClient, Transcript, SessionList, QueuedMessages,
    prepareSubmission, document, matchMedia, localStorage, location,
    setTimeout, clearTimeout, setInterval, clearInterval,
    requestAnimationFrame, cancelAnimationFrame, ResizeObserver,
    icon, uuid, workspaceTail, isBusy, normKey, phaseLabel, activityLabel, renderPending } = dependencies;
  ${source.slice(controllerStart, startupStart)}
  shell = createShell();
  return {
    get shell() { return shell; }, get convo() { return convo; },
    get pendingRestores() { return pendingRestores; }, route, sendDraft,
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

function createFixture() {
  let serial = 0;
  const nodes = new Map();
  const node = key => {
    if (!nodes.has(key)) nodes.set(key, {
      hidden: false, textContent: '', className: '', dataset: {},
      scrollHeight: 100, scrollTop: 0, clientHeight: 100,
      classList: { toggle: noop }, setAttribute: noop, addEventListener: noop,
      removeEventListener: noop, querySelector: () => null,
    });
    return nodes.get(key);
  };
  const storage = new Map([['alcoler.lastWorkspace', 'C:/workspace']]);
  const location = { hash: '#/new' };
  const calls = { creates: [], commands: [], catalogs: [] };
  const handlers = {
    createSession: async () => ({ id: 'created' }),
    sendCommand: async () => accepted(),
    getSlashCatalog: async () => catalog,
  };
  const api = {
    getToken: () => 'paired-token',
    async getSlashCatalog(query) { calls.catalogs.push(query); return handlers.getSlashCatalog(query); },
    async createSession(body) { calls.creates.push(structuredClone(body)); return handlers.createSession(body); },
    async sendCommand(id, command) {
      calls.commands.push(structuredClone(command));
      return handlers.sendCommand(id, command);
    },
  };
  const schedule = () => ++serial;
  const controller = loadController({
    api, Composer: FakeComposer, prepareSubmission,
    document: { getElementById: node, querySelector: node, hidden: true },
    matchMedia: () => ({ matches: true }), location,
    localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) },
    setTimeout: schedule, clearTimeout: noop, setInterval: schedule, clearInterval: noop,
    requestAnimationFrame: schedule, cancelAnimationFrame: noop,
    ResizeObserver: class { observe() {} disconnect() {} },
    StreamClient: class { constructor(opts) { this.opts = opts; } start() {} close() {} },
    Transcript: class { constructor() { this.els = new Map(); } rebuild() {} dispose() {} },
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
    open(id = 'original', overrides = {}) {
      location.hash = `#/session/${encodeURIComponent(id)}`;
      controller.route();
      const st = controller.convo;
      st.ws.opts.onStatus('online');
      st.ws.opts.onSnapshot({ revision: 10, sequence: 100, state: sessionState(overrides), rows: [] });
      return st;
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
