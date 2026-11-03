import { icon } from './util.js';

const MAX_IMAGE_EDGE = 1568;
const JPEG_QUALITY = 0.85;
const MAX_TEXTAREA_HEIGHT = 160;
const MODES = ['build', 'edit', 'yolo'];

export class Composer {
  constructor(root, options = {}) {
    if (!root) throw new Error('Composer root is required');
    this.root = root;
    this.opts = options;
    this.input = root.querySelector('.composer-input');
    this.sendBtn = root.querySelector('.composer-send');
    this.attachBtn = root.querySelector('.composer-attach');
    this.fileInput = root.querySelector('.composer-file');
    this.attachRow = root.querySelector('.attach-row');
    this.modeSelect = root.querySelector('.mode-select');
    this.modeIcon = root.querySelector('.mode-icon');
    this.planBadge = root.querySelector('.plan-badge');
    this.modelSelect = root.querySelector('.model-select');
    this.effortSelect = root.querySelector('.effort-select');
    this.effortPicker = root.querySelector('.effort-picker');
    this.models = [];
    this.defaultSelection = null;
    this.modelSelection = null;
    this.images = [];
    this.pendingFiles = 0;
    this.ready = false;
    this.disabled = false;
    this.disposed = false;

    if (this.attachBtn) this.attachBtn.innerHTML = icon('camera');
    if (this.modeIcon) this.modeIcon.innerHTML = icon('wrench');

    this._onInput = () => { this.autosize(); this.update(); };
    this._onKeyDown = e => {
      if (e.key !== 'Enter' || e.shiftKey || e.isComposing) return;
      if (!desktopKeyboard()) return;
      e.preventDefault();
      if (!this.canSend()) return;
      this.emitSend();
    };
    this._onSendClick = () => {
      if (this.disabled) return;
      const busy = this.isBusy();
      if (busy && !this.hasContent()) {
        if (this.opts.canStop && !this.opts.canStop()) return;
        if (this.opts.onStop) this.opts.onStop();
        return;
      }
      if (!this.canSend()) return;
      this.emitSend();
    };
    this._onAttachClick = () => {
      if (this.disabled || !this.fileInput) return;
      this.fileInput.click();
    };
    this._onFilesChange = async () => {
      if (!this.fileInput) return;
      const files = [...this.fileInput.files];
      this.fileInput.value = '';
      for (const file of files) await this.addFile(file);
    };
    this._onModeChange = () => {
      if (!this.modeSelect) return;
      const value = MODES.includes(this.modeSelect.value) ? this.modeSelect.value : 'build';
      this.applyModeStyle();
      if (this.opts.onModeChange) this.opts.onModeChange(value);
    };

    this._onModelChange = () => {
      const model = this.models[Number(this.modelSelect?.value)];
      if (!model || this.disabled) return;
      this.setModelSelection({
        providerId: model.providerId,
        modelId: model.modelId,
        options: { reasoningLevel: model.reasoningLevels.at(-1) ?? null },
      });
      if (this.opts.onModelSelectionChange) this.opts.onModelSelectionChange(this.getModelSelection());
    };
    this._onEffortChange = () => {
      const selection = this.getModelSelection();
      if (!selection || this.disabled) return;
      this.setModelSelection({ ...selection, options: { ...selection.options, reasoningLevel: this.effortSelect.value } });
      if (this.opts.onModelSelectionChange) this.opts.onModelSelectionChange(this.getModelSelection());
    };

    if (this.input) {
      this.input.addEventListener('input', this._onInput);
      this.input.addEventListener('keydown', this._onKeyDown);
    }
    if (this.sendBtn) this.sendBtn.addEventListener('click', this._onSendClick);
    if (this.attachBtn) this.attachBtn.addEventListener('click', this._onAttachClick);
    if (this.fileInput) this.fileInput.addEventListener('change', this._onFilesChange);
    if (this.modeSelect) this.modeSelect.addEventListener('change', this._onModeChange);
    if (this.modelSelect) this.modelSelect.addEventListener('change', this._onModelChange);
    if (this.effortSelect) this.effortSelect.addEventListener('change', this._onEffortChange);

    this.renderAttachRow();
    this.update();
  }

  setReady(ready) {
    this.ready = !!ready;
    this.update();
  }

  setDisabled(disabled) {
    this.disabled = !!disabled;
    if (this.input) this.input.disabled = this.disabled;
    if (this.attachBtn) this.attachBtn.disabled = this.disabled;
    if (this.modeSelect) this.modeSelect.disabled = this.disabled;
    if (this.sendBtn) this.sendBtn.disabled = this.disabled || !this.ready;
    this.update();
  }

  setText(text) {
    if (!this.input) return;
    this.input.value = String(text ?? '');
    this.autosize();
    this.update();
  }

  setImages(images) {
    this.revokeImages();
    this.images = [];
    for (const image of Array.isArray(images) ? images : []) {
      if (!image) continue;
      const mimeType = image.mimeType || 'image/jpeg';
      const base64Data = String(image.base64Data || '');
      let previewUrl = image.previewUrl || '';
      if (!previewUrl && base64Data) {
        try {
          previewUrl = URL.createObjectURL(base64ToBlob(base64Data, mimeType));
        } catch {
          previewUrl = '';
        }
      }
      if (!base64Data && !previewUrl) continue;
      this.images.push({ mimeType, base64Data, previewUrl });
    }
    this.renderAttachRow();
    this.update();
  }

  // Transfers preview URL ownership to the caller; the composer stops tracking them.
  takeImages() {
    const images = this.images.slice();
    this.images = [];
    this.renderAttachRow();
    this.update();
    return images;
  }

  setMode(mode) {
    if (!this.modeSelect) return;
    const value = String(mode || '').toLowerCase();
    this.modeSelect.value = MODES.includes(value) ? value : 'build';
    this.applyModeStyle();
  }

  applyModeStyle() {
    if (!this.modeSelect) return;
    const picker = this.modeSelect.closest('.mode-picker');
    if (picker) picker.classList.toggle('full-access', this.modeSelect.value === 'yolo');
  }

  setModels(catalog) {
    this.models = (Array.isArray(catalog?.models) ? catalog.models : [])
      .filter(model => typeof model?.providerId === 'string' && typeof model.modelId === 'string')
      .map(model => ({ ...model, reasoningLevels: Array.isArray(model.reasoningLevels)
        ? [...new Set(model.reasoningLevels.filter(level => typeof level === 'string' && level))] : [] }));
    this.defaultSelection = catalog?.defaultSelection || null;
    if (this.modelSelect) {
      this.modelSelect.textContent = '';
      if (!this.models.length) {
        this.addOption(this.modelSelect, '', catalog ? 'No models configured' : 'Models unavailable');
        this.modelSelect.title = catalog ? 'Configure models in the desktop app' : 'Refresh sessions to retry';
      }
      this.models.forEach((model, index) => {
        const label = model.displayName || model.modelId;
        this.addOption(this.modelSelect, String(index), model.providerLabel ? `${label} — ${model.providerLabel}` : label);
      });
    }
    this.setModelSelection(this.modelSelection);
  }

  addOption(select, value, text) {
    const option = document.createElement('option');
    option.value = value;
    option.textContent = text;
    select.appendChild(option);
  }

  setModelSelection(selection) {
    this.modelSelection = selection ? { ...selection, options: { ...selection.options } } : null;
    const matches = candidate => this.models.findIndex(model =>
      model.providerId === candidate?.providerId && model.modelId === candidate?.modelId);
    let index = matches(this.modelSelection);
    if (index < 0) {
      index = matches(this.defaultSelection);
      this.modelSelection = index >= 0 ? this.defaultSelection : null;
    }
    if (index < 0 && this.models.length) index = 0;
    const model = this.models[index];
    if (model) {
      const options = this.modelSelection?.options || {};
      this.modelSelection = {
        providerId: model.providerId,
        modelId: model.modelId,
        options: {
          reasoningLevel: model.reasoningLevels.includes(options.reasoningLevel)
            ? options.reasoningLevel : model.reasoningLevels[0] ?? null,
          maxOutputTokens: Number.isInteger(options.maxOutputTokens) && options.maxOutputTokens > 0
            ? options.maxOutputTokens : null,
        },
      };
      if (this.modelSelect) {
        this.modelSelect.value = String(index);
        this.modelSelect.title = `${model.providerLabel || model.providerId}: ${model.modelId}`;
      }
    } else if (selection) {
      this.modelSelection = { ...selection, options: { ...selection.options } };
    }
    if (this.effortSelect) {
      this.effortSelect.textContent = '';
      for (const level of model?.reasoningLevels || []) {
        this.addOption(this.effortSelect, level, level.charAt(0).toUpperCase() + level.slice(1));
      }
      this.effortSelect.value = this.modelSelection?.options?.reasoningLevel ?? '';
    }
    if (this.effortPicker) this.effortPicker.hidden = !model?.reasoningLevels.length;
    this.update();
  }

  getModelSelection() {
    if (!this.models.length || !this.modelSelection) return null;
    return { ...this.modelSelection, options: { ...this.modelSelection.options } };
  }

  setPlan(enabled) {
    if (this.planBadge) this.planBadge.hidden = !enabled;
  }

  getText() {
    return this.input ? this.input.value : '';
  }

  getImages() {
    return this.images.map(image => ({
      mimeType: image.mimeType,
      base64Data: image.base64Data,
      previewUrl: image.previewUrl,
    }));
  }

  clear() {
    if (this.input) this.input.value = '';
    this.autosize();
    this.revokeImages();
    this.images = [];
    this.renderAttachRow();
    this.update();
  }

  focus() {
    if (this.input) this.input.focus();
  }

  update() {
    if (this.disposed) return;
    if (this.modelSelect) this.modelSelect.disabled = this.disabled || !this.ready || !this.models.length;
    if (this.effortSelect) this.effortSelect.disabled = this.disabled || !this.ready || !this.models.length || !!this.effortPicker?.hidden;
    if (!this.sendBtn) return;
    const busy = this.isBusy();
    const hasContent = this.hasContent();
    const wantsStop = busy && !hasContent;
    const canStop = !this.opts.canStop || !!this.opts.canStop();
    const showStop = wantsStop && canStop;
    this.sendBtn.classList.toggle('stop', showStop);
    this.sendBtn.innerHTML = icon(showStop ? 'stop' : 'send');
    this.sendBtn.setAttribute('aria-label', showStop ? 'Stop' : 'Send');
    this.sendBtn.title = showStop ? 'Stop' : 'Send';
    let disabled = this.disabled || !this.ready;
    if (!disabled && wantsStop) disabled = !canStop;
    else if (!disabled && !showStop) disabled = !hasContent;
    this.sendBtn.disabled = disabled || this.pendingFiles > 0;
    this.autosize();
    if (this.opts.onChange) this.opts.onChange();
  }

  dispose() {
    this.disposed = true;
    if (this.input) {
      this.input.removeEventListener('input', this._onInput);
      this.input.removeEventListener('keydown', this._onKeyDown);
    }
    if (this.sendBtn) this.sendBtn.removeEventListener('click', this._onSendClick);
    if (this.attachBtn) this.attachBtn.removeEventListener('click', this._onAttachClick);
    if (this.fileInput) this.fileInput.removeEventListener('change', this._onFilesChange);
    if (this.modeSelect) this.modeSelect.removeEventListener('change', this._onModeChange);
    if (this.modelSelect) this.modelSelect.removeEventListener('change', this._onModelChange);
    if (this.effortSelect) this.effortSelect.removeEventListener('change', this._onEffortChange);
    this.revokeImages();
    this.images = [];
  }

  isBusy() {
    return !!(this.opts.getBusy && this.opts.getBusy());
  }

  hasContent() {
    return !!(this.input && this.input.value.trim()) || this.images.length > 0;
  }

  canSend() {
    return this.ready && !this.disabled && this.pendingFiles === 0 && this.hasContent();
  }

  emitSend() {
    if (!this.canSend()) return;
    const text = this.input ? this.input.value.trim() : '';
    const images = this.getImages().map(image => ({
      mimeType: image.mimeType,
      base64Data: image.base64Data,
      previewUrl: image.previewUrl,
    }));
    if (this.opts.onSend) this.opts.onSend(text, images, this.getModelSelection());
  }

  autosize() {
    if (!this.input) return;
    this.input.style.height = 'auto';
    this.input.style.height = Math.min(this.input.scrollHeight, MAX_TEXTAREA_HEIGHT) + 'px';
  }

  renderAttachRow() {
    if (!this.attachRow) return;
    this.attachRow.textContent = '';
    this.attachRow.hidden = this.images.length === 0;
    this.images.forEach(image => {
      const thumb = document.createElement('div');
      thumb.className = 'attach-thumb';
      const img = new Image();
      img.alt = '';
      img.src = image.previewUrl;
      const remove = document.createElement('button');
      remove.type = 'button';
      remove.className = 'attach-remove';
      remove.setAttribute('aria-label', 'Remove attachment');
      remove.innerHTML = icon('close');
      remove.addEventListener('click', () => this.removeImage(image));
      thumb.append(img, remove);
      this.attachRow.appendChild(thumb);
    });
  }

  removeImage(image) {
    const index = this.images.indexOf(image);
    if (index < 0) return;
    this.images.splice(index, 1);
    revokeUrl(image.previewUrl);
    this.renderAttachRow();
    this.update();
  }

  revokeImages() {
    for (const image of this.images) revokeUrl(image.previewUrl);
  }

  async addFile(file) {
    if (this.disabled || !file || !String(file.type || '').startsWith('image/')) return;
    this.pendingFiles++;
    this.update();
    try {
      const blob = await downscaleImage(file);
      const base64Data = await blobToBase64(blob);
      if (this.disposed) return;
      this.images.push({
        mimeType: 'image/jpeg',
        base64Data,
        previewUrl: URL.createObjectURL(blob),
      });
      this.renderAttachRow();
      this.update();
    } catch { /* unreadable image: skip it */ }
    finally {
      this.pendingFiles--;
      this.update();
    }
  }
}

function desktopKeyboard() {
  return matchMedia('(pointer: fine)').matches && window.innerWidth >= 700;
}

function revokeUrl(url) {
  if (url) URL.revokeObjectURL(url);
}

function base64ToBlob(base64Data, mimeType) {
  const raw = String(base64Data || '');
  const body = raw.includes(',') ? raw.slice(raw.indexOf(',') + 1) : raw;
  const binary = atob(body);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return new Blob([bytes], { type: mimeType || 'image/jpeg' });
}

export function downscaleImage(file) {
  return loadImage(file).then(img => {
    const scale = Math.min(1, MAX_IMAGE_EDGE / Math.max(img.width, img.height));
    const w = Math.max(1, Math.round(img.width * scale));
    const h = Math.max(1, Math.round(img.height * scale));
    const canvas = document.createElement('canvas');
    canvas.width = w;
    canvas.height = h;
    canvas.getContext('2d').drawImage(img, 0, 0, w, h);
    return new Promise((resolve, reject) => {
      canvas.toBlob(blob => {
        if (blob) resolve(blob);
        else reject(new Error('encode failed'));
      }, 'image/jpeg', JPEG_QUALITY);
    });
  });
}

function loadImage(file) {
  if (window.createImageBitmap) {
    return createImageBitmap(file, { imageOrientation: 'from-image' })
      .catch(() => loadViaElement(file));
  }
  return loadViaElement(file);
}

function loadViaElement(file) {
  return new Promise((resolve, reject) => {
    const url = URL.createObjectURL(file);
    const img = new Image();
    img.onload = () => {
      resolve(img);
      setTimeout(() => URL.revokeObjectURL(url), 5000);
    };
    img.onerror = () => {
      URL.revokeObjectURL(url);
      reject(new Error('decode failed'));
    };
    img.src = url;
  });
}

export function blobToBase64(blob) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result).split(',')[1] || '');
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(blob);
  });
}
