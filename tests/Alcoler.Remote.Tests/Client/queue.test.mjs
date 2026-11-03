import assert from 'node:assert/strict';
import { afterEach, beforeEach, test } from 'node:test';
import { QueuedMessages, loadQueuedImages } from '../../../src/Alcoler.Remote/wwwroot/queue.js';

class Element {
  constructor() {
    this.children = [];
    this.dataset = {};
    this.attributes = new Map();
    this.listeners = new Map();
    this.disabled = false;
    this.innerHTML = '';
  }
  set textContent(value) { this.text = String(value); this.children = []; }
  get textContent() { return (this.text || '') + this.children.map(child => child.textContent).join(''); }
  appendChild(child) { this.children.push(child); }
  setAttribute(name, value) { this.attributes.set(name, value); }
  addEventListener(name, listener) { this.listeners.set(name, listener); }
}

const originalDocument = globalThis.document;
beforeEach(() => { globalThis.document = { createElement: () => new Element() }; });
afterEach(() => {
  if (originalDocument === undefined) delete globalThis.document;
  else globalThis.document = originalDocument;
});

const deferred = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
};
const queued = (overrides = {}) => ({ commandId: 'queued-1', text: 'Queued message', ...overrides });

function setup(options = {}) {
  const container = new Element();
  const commands = [], restores = [], errors = [], acks = [];
  const composer = {
    text: '', images: [], disabled: false, pendingFiles: 0,
    hasContent() { return !!this.text.trim() || this.images.length > 0; },
    setDisabled(value) { this.disabled = value; },
  };
  const view = new QueuedMessages(container, {
    composer,
    sendCommand: async (...args) => { commands.push(args); return { accepted: true, revision: 3 }; },
    loadImages: async images => images ? images.map(image => ({ mimeType: image.mimeType, base64Data: 'aW1hZ2U=' })) : [],
    onRestore: draft => restores.push(draft),
    onError: error => errors.push(error),
    onAck: ack => acks.push(ack),
    ...options,
  });
  view.update([queued()], true);
  const buttons = (index = 0) => container.children[index].children[1].children;
  return { view, container, composer, commands, restores, errors, acks, buttons };
}

test('queue rows render safe single-line text and accessible steer, edit, delete controls', async () => {
  const { view, container, buttons, commands } = setup();
  const text = '<img src=x onerror="boom"> & Unicode 中文';
  view.update([queued({ text }), queued({ commandId: 'images', text: '', images: [{}, {}] })], true);
  assert.equal(container.children[0].children[0].textContent, text);
  assert.equal(container.children[0].children[0].innerHTML, '');
  assert.equal(container.children[1].children[0].textContent, '2 images');
  assert.deepEqual(buttons().map(button => button.attributes.get('aria-label')), [
    'Steer queued message', 'Edit queued message', 'Delete queued message',
  ]);
  assert.match(buttons()[0].title, /Stop the current turn/);
  assert.match(buttons()[1].title, /back to the composer/);
  await buttons()[0].listeners.get('click')();
  assert.deepEqual(commands, [['send_queued_now', 'queued-1']]);
});

test('unchanged state preserves queue DOM and long text is bounded with a full tooltip', () => {
  const { view, container } = setup();
  const text = 'x'.repeat(10000);
  const item = queued({ text });
  view.update([item], true);
  const row = container.children[0];
  assert.equal(row.children[0].textContent.length, 161);
  assert.equal(row.children[0].title, text);
  view.update([item], true);
  assert.equal(container.children[0], row);
});

test('steer and delete reuse target commands and never restore or resend the message', async () => {
  const { view, commands, restores, acks } = setup();
  await view.act('steer', 'queued-1');
  await view.act('delete', 'queued-1');
  assert.deepEqual(commands, [['send_queued_now', 'queued-1'], ['remove_queued_input', 'queued-1']]);
  assert.equal(restores.length, 0);
  assert.equal(acks.length, 2);
});

test('editing loads attachments before withdrawal and restores full intent only after acceptance', async () => {
  const images = deferred(), ack = deferred();
  const commands = [];
  const { view, composer, restores } = setup({
    loadImages: () => images.promise,
    sendCommand: (...args) => { commands.push(args); return ack.promise; },
  });
  const modelSelection = { providerId: 'p', modelId: 'm', options: { reasoningLevel: 'high', maxOutputTokens: 8192 } };
  const item = queued({
    text: '  Keep whitespace\n中文  ',
    images: [{ artifactUri: 'alcoler-artifact://image', mimeType: 'image/png' }],
    modelSelection, mode: 'Edit', planEnabled: false, delivery: 'Guide', workflows: ['plan'],
  });
  view.update([item], true);
  const operation = view.act('edit', item.commandId);
  assert.equal(composer.disabled, true);
  assert.equal(commands.length, 0);
  const uploads = [{ mimeType: 'image/png', base64Data: 'aW1hZ2U=' }];
  images.resolve(uploads);
  await Promise.resolve();
  assert.deepEqual(commands, [['remove_queued_input', item.commandId]]);
  assert.equal(restores.length, 0);
  ack.resolve({ accepted: true });
  await operation;
  assert.equal(composer.disabled, false);
  assert.deepEqual(restores, [{
    text: item.text, images: uploads, modelSelection,
    intent: { mode: 'Edit', planEnabled: false, modelSelection, delivery: 'Guide', workflows: ['plan'] },
  }]);
});

test('image-only queue items restore attachments and nullable intent without inventing overrides', async () => {
  const { view, restores } = setup();
  view.update([queued({ text: '', images: [{ mimeType: 'image/webp' }] })], true);
  await view.act('edit', 'queued-1');
  assert.deepEqual(restores[0], {
    text: '', images: [{ mimeType: 'image/webp', base64Data: 'aW1hZ2U=' }], modelSelection: null,
    intent: { mode: null, planEnabled: null, modelSelection: null, delivery: 'Queue', workflows: null },
  });
});

test('existing text, images, and loading attachments block edit but not steer or delete', async () => {
  const { view, composer, buttons, commands, errors } = setup();
  for (const draft of [
    { text: 'Unsent draft', images: [], pendingFiles: 0 },
    { text: '', images: [{}], pendingFiles: 0 },
    { text: '', images: [], pendingFiles: 1 },
  ]) {
    Object.assign(composer, draft);
    view.refresh();
    assert.deepEqual(buttons().map(button => button.disabled), [false, true, false]);
    assert.match(buttons()[1].title, /current draft/);
    await view.act('edit', 'queued-1');
    assert.equal(commands.length, 0);
  }
  assert.equal(errors.length, 3);
  await view.act('delete', 'queued-1');
  assert.deepEqual(commands, [['remove_queued_input', 'queued-1']]);
});

test('pending interactions and a disabled composer prevent withdrawal without blocking other actions', async () => {
  const { view, composer, buttons, commands } = setup();
  view.update([queued()], true, false);
  assert.deepEqual(buttons().map(button => button.disabled), [false, true, false]);
  await view.act('edit', 'queued-1');
  view.update([queued()], true);
  composer.disabled = true;
  view.refresh();
  assert.equal(buttons()[1].disabled, true);
  await view.act('edit', 'queued-1');
  assert.equal(commands.length, 0);
});

test('duplicate actions on a target are blocked until the acknowledgement arrives', async () => {
  const pending = deferred();
  const commands = [];
  const { view, buttons } = setup({ sendCommand: (...args) => { commands.push(args); return pending.promise; } });
  const operation = view.act('steer', 'queued-1');
  assert.deepEqual(buttons().map(button => button.disabled), [true, true, true]);
  await view.act('steer', 'queued-1');
  await view.act('edit', 'queued-1');
  await view.act('delete', 'queued-1');
  assert.equal(commands.length, 1);
  pending.resolve({ accepted: true });
  await operation;
  assert.deepEqual(buttons().map(button => button.disabled), [false, false, false]);
});

test('attachment failures leave the item queued and release the composer', async () => {
  const { view, composer, commands, restores, errors } = setup({ loadImages: async () => { throw new Error('not found'); } });
  await view.act('edit', 'queued-1');
  assert.equal(commands.length, 0);
  assert.equal(restores.length, 0);
  assert.equal(composer.disabled, false);
  assert.equal(view.items.length, 1);
  assert.match(errors[0], /attachments.*not withdrawn.*not found/);
});

test('an item consumed while attachments load is not removed or resurrected as a draft', async () => {
  const images = deferred();
  const { view, composer, commands, restores, errors } = setup({ loadImages: () => images.promise });
  const operation = view.act('edit', 'queued-1');
  view.update([], true);
  images.resolve([]);
  await operation;
  assert.equal(commands.length, 0);
  assert.equal(restores.length, 0);
  assert.equal(composer.disabled, false);
  assert.match(errors[0], /queue changed/);
});

test('a draft arriving asynchronously during preload is not overwritten or removed from the queue', async () => {
  const images = deferred();
  const { view, composer, commands, restores, errors } = setup({ loadImages: () => images.promise });
  const operation = view.act('edit', 'queued-1');
  composer.text = 'Arrived while loading';
  images.resolve([]);
  await operation;
  assert.equal(commands.length, 0);
  assert.equal(restores.length, 0);
  assert.equal(composer.text, 'Arrived while loading');
  assert.match(errors[0], /current draft was kept/);
});

test('server rejection exposes the reason and does not restore an already-consumed or promoted item', async () => {
  const { view, composer, restores, errors, acks } = setup({
    sendCommand: async () => ({ accepted: false, revision: 7, rejectionReason: 'queued input was already consumed' }),
  });
  await view.act('edit', 'queued-1');
  assert.equal(restores.length, 0);
  assert.equal(composer.disabled, false);
  assert.equal(acks[0].revision, 7);
  assert.match(errors[0], /already consumed/);
});

test('transport errors do not report success, automatically retry, or overwrite the composer', async () => {
  let sends = 0;
  const { view, composer, restores, errors } = setup({ sendCommand: async () => { sends++; throw new Error('network'); } });
  await view.act('edit', 'queued-1');
  assert.equal(sends, 1);
  assert.equal(restores.length, 0);
  assert.equal(composer.disabled, false);
  assert.match(errors[0], /Could not confirm.*Check the conversation/);
});

test('offline queue actions are disabled and a disconnect during preload aborts withdrawal', async () => {
  const images = deferred();
  const { view, composer, commands, restores, buttons } = setup({ loadImages: () => images.promise });
  const operation = view.act('edit', 'queued-1');
  view.update([queued()], false);
  assert.deepEqual(buttons().map(button => button.disabled), [true, true, true]);
  images.resolve([]);
  await operation;
  await view.act('steer', 'queued-1');
  await view.act('delete', 'queued-1');
  assert.equal(commands.length, 0);
  assert.equal(restores.length, 0);
  assert.equal(composer.disabled, false);
});

test('a question arriving during preload defers withdrawal until the composer is available', async () => {
  const images = deferred();
  const { view, composer, commands, restores } = setup({ loadImages: () => images.promise });
  const operation = view.act('edit', 'queued-1');
  view.update([queued()], true, false);
  images.resolve([]);
  await operation;
  assert.equal(commands.length, 0);
  assert.equal(restores.length, 0);
  assert.equal(composer.disabled, false);
});

test('disposing during preload cancels withdrawal and removes its UI', async () => {
  const images = deferred();
  const { view, composer, container, commands, restores } = setup({ loadImages: () => images.promise });
  const operation = view.act('edit', 'queued-1');
  view.dispose();
  images.resolve([]);
  await operation;
  assert.equal(composer.disabled, false);
  assert.equal(container.children.length, 0);
  assert.equal(commands.length, 0);
  assert.equal(restores.length, 0);
});

test('accepted withdrawal after navigation retains the draft without unlocking another conversation', async () => {
  const ack = deferred();
  const { view, composer, restores } = setup({ sendCommand: () => ack.promise });
  const operation = view.act('edit', 'queued-1');
  await Promise.resolve();
  view.dispose();
  composer.disabled = true;
  ack.resolve({ accepted: true });
  await operation;
  assert.equal(restores[0].text, 'Queued message');
  assert.equal(composer.disabled, true);
});

test('unknown actions and stale targets cannot submit commands', async () => {
  const { view, commands, errors } = setup();
  await view.act('unknown', 'queued-1');
  await view.act('delete', 'missing');
  assert.equal(commands.length, 0);
  assert.match(errors[0], /already consumed or removed/);
});

test('queued image loading uses authenticated artifacts and retains original bytes and MIME types', async () => {
  const previous = { fetch: globalThis.fetch, localStorage: globalThis.localStorage, FileReader: globalThis.FileReader };
  const requests = [];
  globalThis.localStorage = { getItem: key => key === 'alcoler.deviceToken' ? 'device-token' : null };
  globalThis.fetch = async (url, options) => {
    requests.push({ url, options });
    return { ok: true, status: 200, blob: async () => new Blob(['original bytes'], { type: 'image/png' }) };
  };
  globalThis.FileReader = class {
    readAsDataURL(blob) {
      blob.arrayBuffer().then(bytes => {
        this.result = `data:${blob.type};base64,${Buffer.from(bytes).toString('base64')}`;
        this.onload();
      });
    }
  };
  try {
    assert.deepEqual(await loadQueuedImages([{ artifactUri: 'alcoler-artifact://image/with space', mimeType: 'image/png' }]), [
      { mimeType: 'image/png', base64Data: Buffer.from('original bytes').toString('base64') },
    ]);
    assert.match(requests[0].url, /uri=alcoler-artifact%3A%2F%2Fimage%2Fwith%20space&mime=image%2Fpng/);
    assert.equal(requests[0].options.headers.Authorization, 'Bearer device-token');
    assert.equal(requests[0].options.cache, 'no-store');
  } finally {
    for (const [name, value] of Object.entries(previous)) {
      if (value === undefined) delete globalThis[name];
      else globalThis[name] = value;
    }
  }
});
