import { artifactBlob } from './api.js';
import { blobToBase64 } from './composer.js';
import { renderQueue } from './render.js';

export async function loadQueuedImages(images, { signal } = {}) {
  return Promise.all((images || []).map(async image => {
    const blob = await artifactBlob(image.artifactUri, image.mimeType, { signal });
    signal?.throwIfAborted();
    return { mimeType: image.mimeType || blob.type, base64Data: await blobToBase64(blob) };
  }));
}

export class QueuedMessages {
  constructor(container, options) {
    this.container = container;
    this.opts = options;
    this.composer = options.composer;
    this.items = [];
    this.pending = new Set();
    this.ready = false;
    this.editable = true;
    this.editing = false;
    this.disposed = false;
    this.lastKey = null;
  }

  update(items, ready, editable = true) {
    if (this.disposed) return;
    this.items = Array.isArray(items) ? items : [];
    this.ready = !!ready;
    this.editable = !!editable;
    this.refresh();
  }

  refresh() {
    if (this.disposed) return;
    const draftBusy = this.composer.hasContent() || this.composer.pendingFiles > 0;
    const canEdit = this.editable && !this.editing && !this.composer.disabled && !draftBusy;
    const key = JSON.stringify([this.items, this.ready, canEdit, [...this.pending]]);
    if (key === this.lastKey) return;
    this.lastKey = key;
    renderQueue(this.container, this.items, {
      ready: this.ready,
      canEdit,
      pending: this.pending,
      editHint: draftBusy ? 'Send or clear your current draft before editing a queued message' : '',
      onAction: (action, id) => this.act(action, id),
    });
  }

  async act(action, id) {
    if (this.disposed || !this.ready || this.pending.has(id)) return;
    if (!['steer', 'edit', 'delete'].includes(action)) return;
    const queued = this.items.find(item => item.commandId === id);
    if (!queued) {
      this.opts.onError('This queued message was already consumed or removed.');
      return;
    }
    const edit = action === 'edit';
    if (edit && (!this.editable || this.editing || this.composer.disabled)) return;
    if (edit && (this.composer.hasContent() || this.composer.pendingFiles > 0)) {
      this.opts.onError('Send or clear your current draft before editing a queued message.');
      return;
    }
    this.pending.add(id);
    if (edit) {
      this.editing = true;
      this.composer.setDisabled(true);
    }
    this.refresh();
    let removing = false;
    const imageLoad = edit ? new AbortController() : null;
    if (imageLoad) this.imageLoad = imageLoad;
    try {
      const images = edit ? await (this.opts.loadImages || loadQueuedImages)(queued.images, { signal: imageLoad.signal }) : [];
      if (this.disposed) return;
      if (!this.ready || (edit && !this.editable) || !this.items.some(item => item.commandId === id)) {
        this.opts.onError('The queue changed or disconnected. This message was not withdrawn for editing.');
        return;
      }
      if (edit && this.composer.hasContent()) {
        this.opts.onError('Your current draft was kept. Clear it before editing a queued message.');
        return;
      }
      removing = true;
      const ack = await this.opts.sendCommand(action === 'steer' ? 'send_queued_now' : 'remove_queued_input', id);
      this.opts.onAck(ack);
      if (!ack?.accepted) {
        if (!this.disposed) this.opts.onError(ack?.rejectionReason || 'The queued message could not be changed.');
        return;
      }
      if (edit) {
        this.opts.onRestore({
          text: queued.text || '',
          images,
          modelSelection: queued.modelSelection || null,
          intent: {
            mode: queued.mode ?? null,
            planEnabled: queued.planEnabled ?? null,
            modelSelection: queued.modelSelection || null,
            delivery: queued.delivery ?? 'Queue',
            workflows: queued.workflows ?? null,
          },
        });
      }
    } catch (error) {
      if (!this.disposed) {
        const message = !removing && edit
          ? 'Could not load the attachments. The queued message was not withdrawn.'
          : 'Could not confirm the queue operation. Check the conversation before trying again.';
        this.opts.onError(`${message}${error?.message ? ` ${error.message}` : ''}`);
      }
    } finally {
      imageLoad?.abort();
      if (this.imageLoad === imageLoad) this.imageLoad = null;
      this.pending.delete(id);
      if (edit && !this.disposed) {
        this.editing = false;
        this.composer.setDisabled(false);
      }
      this.refresh();
    }
  }

  dispose() {
    this.disposed = true;
    this.imageLoad?.abort();
    this.imageLoad = null;
    if (this.editing) this.composer.setDisabled(false);
    this.items = [];
    this.container.textContent = '';
  }
}
