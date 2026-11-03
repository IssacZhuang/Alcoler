import assert from 'node:assert/strict';
import { afterEach, beforeEach, test } from 'node:test';
import { Composer } from '../../../src/Alcoler.Remote/wwwroot/composer.js';

class Element {
  constructor() {
    this.children = [];
    this.listeners = new Map();
    this.attributes = new Map();
    this.style = {};
    this.value = '';
    this.hidden = false;
    this.disabled = false;
    this.classList = { toggle() {} };
  }

  set textContent(value) { this.text = String(value); this.children = []; }
  get textContent() { return (this.text || '') + this.children.map(child => child.textContent).join(''); }
  appendChild(child) { this.children.push(child); }
  addEventListener(name, listener) { this.listeners.set(name, listener); }
  removeEventListener(name) { this.listeners.delete(name); }
  setAttribute(name, value) { this.attributes.set(name, value); }
  closest() { return null; }
  change(value) { this.value = value; this.listeners.get('change')?.(); }
}

const originalDocument = globalThis.document;
beforeEach(() => { globalThis.document = { createElement: () => new Element() }; });
afterEach(() => {
  if (originalDocument === undefined) delete globalThis.document;
  else globalThis.document = originalDocument;
});

function createComposer(options = {}) {
  const elements = new Map([
    '.composer-input', '.composer-send', '.mode-select', '.model-select', '.effort-select', '.effort-picker',
  ].map(selector => [selector, new Element()]));
  const composer = new Composer({ querySelector: selector => elements.get(selector) || null }, options);
  composer.setReady(true);
  return { composer, elements };
}

const selection = (providerId, modelId, reasoningLevel, maxOutputTokens = null) => ({
  providerId, modelId, options: { reasoningLevel, maxOutputTokens },
});
const catalog = () => ({
  models: [
    { providerId: 'first', providerLabel: 'First provider', modelId: 'shared', displayName: 'Reasoner', reasoningLevels: ['low', 'high'] },
    { providerId: 'second', providerLabel: 'Second provider', modelId: 'shared', displayName: 'Reasoner', reasoningLevels: ['off', 'medium', 'max'] },
    { providerId: 'first', modelId: 'plain', displayName: '<Plain & fast>', reasoningLevels: [] },
  ],
  defaultSelection: selection('first', 'shared', 'high'),
});

test('catalog populates model and thinking choices using the configured default', () => {
  const { composer, elements } = createComposer();
  composer.setModels(catalog());
  assert.deepEqual(composer.getModelSelection(), selection('first', 'shared', 'high'));
  const models = elements.get('.model-select');
  assert.equal(models.children.length, 3);
  assert.equal(models.children[0].textContent, 'Reasoner — First provider');
  assert.equal(models.children[1].textContent, 'Reasoner — Second provider');
  assert.equal(models.children[2].textContent, '<Plain & fast>');
  assert.equal(models.title, 'First provider: shared');
  assert.equal(models.disabled, false);
  assert.equal(elements.get('.effort-select').value, 'high');
  assert.equal(elements.get('.effort-picker').hidden, false);
});

test('session hydration can arrive before catalog metadata and preserves call options', () => {
  const { composer } = createComposer();
  composer.setModelSelection(selection('second', 'shared', 'medium', 4096));
  assert.equal(composer.getModelSelection(), null);
  composer.setModels(catalog());
  assert.deepEqual(composer.getModelSelection(), selection('second', 'shared', 'medium', 4096));
  composer.setModels(catalog());
  assert.deepEqual(composer.getModelSelection(), selection('second', 'shared', 'medium', 4096));
});

test('explicit model picks use the highest configured effort and distinguish providers', () => {
  const { composer, elements } = createComposer();
  composer.setModels(catalog());
  elements.get('.model-select').change('1');
  assert.deepEqual(composer.getModelSelection(), selection('second', 'shared', 'max'));
  elements.get('.model-select').change('0');
  assert.deepEqual(composer.getModelSelection(), selection('first', 'shared', 'high'));
});

test('changing effort preserves other call options and normalizes unsupported levels', () => {
  const { composer, elements } = createComposer();
  composer.setModels(catalog());
  composer.setModelSelection(selection('second', 'shared', 'medium', 2048));
  elements.get('.effort-select').change('off');
  assert.deepEqual(composer.getModelSelection(), selection('second', 'shared', 'off', 2048));
  composer.setModelSelection(selection('first', 'shared', 'off', -1));
  assert.deepEqual(composer.getModelSelection(), selection('first', 'shared', 'low'));
  assert.deepEqual(elements.get('.effort-select').children.map(option => option.value), ['low', 'high']);
});

test('models without reasoning hide the thinking selector and clear stale effort', () => {
  const { composer, elements } = createComposer();
  composer.setModels(catalog());
  elements.get('.model-select').change('2');
  assert.deepEqual(composer.getModelSelection(), selection('first', 'plain', null));
  assert.equal(elements.get('.effort-picker').hidden, true);
  assert.equal(elements.get('.effort-select').disabled, true);
  assert.equal(elements.get('.effort-select').children.length, 0);
});

test('removed models fall back to the current configured default', () => {
  const { composer } = createComposer();
  composer.setModels(catalog());
  composer.setModelSelection(selection('second', 'shared', 'medium'));
  const updated = catalog();
  updated.models.splice(1, 1);
  composer.setModels(updated);
  assert.deepEqual(composer.getModelSelection(), updated.defaultSelection);
  composer.setModels({ models: updated.models, defaultSelection: null });
  composer.setModelSelection(selection('missing', 'missing', 'max'));
  assert.deepEqual(composer.getModelSelection(), selection('first', 'shared', 'low'));
});

test('empty catalog and failed requests disable choices without blocking ordinary messages', () => {
  const sends = [];
  const { composer, elements } = createComposer({ onSend: (...args) => sends.push(args) });
  for (const metadata of [{ models: [], defaultSelection: null }, null]) {
    composer.setModels(metadata);
    assert.equal(elements.get('.model-select').disabled, true);
    assert.equal(elements.get('.effort-picker').hidden, true);
    assert.equal(composer.getModelSelection(), null);
  }
  assert.equal(elements.get('.model-select').textContent, 'Models unavailable');
  composer.setText('still send using the desktop default');
  composer.emitSend();
  assert.equal(sends[0][2], null);
  composer.setModels(catalog());
  assert.equal(elements.get('.model-select').disabled, false);
});

test('sending carries a copied selection and clearing input does not reset the model', () => {
  const sends = [];
  const { composer, elements } = createComposer({ onSend: (...args) => sends.push(args) });
  composer.setModels(catalog());
  composer.setText('  run tests  ');
  elements.get('.effort-select').change('low');
  composer.emitSend();
  assert.deepEqual(sends[0], ['run tests', [], selection('first', 'shared', 'low')]);
  sends[0][2].options.reasoningLevel = 'changed';
  composer.clear();
  composer.setMode('yolo');
  assert.deepEqual(composer.getModelSelection(), selection('first', 'shared', 'low'));
});

test('only explicit model and effort changes replace a withdrawn message model intent', () => {
  const changes = [];
  const { composer, elements } = createComposer({ onModelSelectionChange: selection => changes.push(selection) });
  composer.setModels(catalog());
  composer.setModelSelection(selection('second', 'shared', 'medium', 4096));
  composer.setModels(catalog());
  assert.deepEqual(changes, []);
  elements.get('.effort-select').change('max');
  assert.deepEqual(changes, [selection('second', 'shared', 'max', 4096)]);
  elements.get('.model-select').change('0');
  assert.deepEqual(changes[1], selection('first', 'shared', 'high'));
});

test('attachment preparation blocks sending and notifies queue controls about draft changes', () => {
  let changes = 0;
  const { composer, elements } = createComposer({ onChange: () => changes++ });
  const before = changes;
  composer.setText('draft');
  assert.ok(changes > before);
  assert.equal(composer.canSend(), true);
  composer.pendingFiles = 1;
  composer.update();
  assert.equal(composer.canSend(), false);
  assert.equal(elements.get('.composer-send').disabled, true);
  composer.pendingFiles = 0;
  composer.update();
  assert.equal(composer.canSend(), true);
  assert.equal(elements.get('.composer-send').disabled, false);
});

test('readiness and sending disable both selectors and disposal removes their listeners', () => {
  const { composer, elements } = createComposer();
  composer.setModels(catalog());
  for (const blocked of [true, false]) {
    composer.setDisabled(blocked);
    assert.equal(elements.get('.model-select').disabled, blocked);
    assert.equal(elements.get('.effort-select').disabled, blocked);
  }
  composer.setReady(false);
  assert.equal(elements.get('.model-select').disabled, true);
  assert.equal(elements.get('.effort-select').disabled, true);
  composer.setReady(true);
  assert.equal(elements.get('.model-select').disabled, false);
  composer.dispose();
  assert.equal(elements.get('.model-select').listeners.size, 0);
  assert.equal(elements.get('.effort-select').listeners.size, 0);
});
