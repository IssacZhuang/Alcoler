const CATALOG_MAX_AGE = 30_000;
const KINDS = new Set(['plan', 'orchestrator', 'skill']);
let nextPopupId = 0;

export function extractSlashToken(text, selectionStart, selectionEnd = selectionStart) {
  const value = String(text ?? '');
  if (!Number.isInteger(selectionStart) || selectionStart < 0 || selectionStart > value.length
      || selectionEnd !== selectionStart) return null;
  let start = selectionStart;
  let end = selectionStart;
  while (start > 0 && !/\s/u.test(value[start - 1])) start--;
  while (end < value.length && !/\s/u.test(value[end])) end++;
  if (start === end || value[start] !== '/' || selectionStart <= start) return null;
  const token = value.slice(start, end);
  if (/[/@$#]/u.test(token.slice(1))) return null;
  return { start, end, text: token };
}

export function normalizeSlashCatalog(catalog) {
  if (!Array.isArray(catalog?.entries)) throw new Error('Slash command catalog is unavailable');
  const entries = catalog.entries.filter(entry => typeof entry?.name === 'string'
    && entry.name && !/[\s/@$#]/u.test(entry.name) && KINDS.has(entry.kind))
    .map(entry => ({ name: entry.name, description: typeof entry.description === 'string' ? entry.description : '', kind: entry.kind }));
  const seen = new Set();
  return [...entries.filter(entry => entry.kind !== 'skill'), ...entries.filter(entry => entry.kind === 'skill')]
    .filter(entry => {
      const name = entry.name.toLowerCase();
      if (seen.has(name)) return false;
      seen.add(name);
      return true;
    });
}

export function filterSlashEntries(entries, token) {
  if (typeof token !== 'string' || !/^\/[^\s/@$#]*$/u.test(token)) return [];
  const query = token.slice(1).toLowerCase();
  const matches = entry => entry.kind === 'skill'
    ? entry.name.toLowerCase().includes(query) : entry.name.toLowerCase().startsWith(query);
  const builtins = entries.filter(entry => entry.kind !== 'skill');
  const names = new Set(builtins.map(entry => entry.name.toLowerCase()));
  return [...builtins.filter(matches), ...entries.filter(entry => entry.kind === 'skill'
    && !names.has(entry.name.toLowerCase()) && matches(entry))];
}

export function parseSlashCommand(text, entries) {
  const value = String(text ?? '').trim();
  if (!value.startsWith('/')) return null;
  const separator = value.search(/\s/u);
  const name = value.slice(1, separator < 0 ? undefined : separator).toLowerCase();
  const entry = entries.find(candidate => candidate.kind !== 'skill' && candidate.name.toLowerCase() === name);
  return entry ? { name: entry.name, kind: entry.kind, prompt: separator < 0 ? '' : value.slice(separator).trim() } : null;
}

export function completeSlashToken(text, token, name) {
  const replacement = `/${name} `;
  return { text: text.slice(0, token.start) + replacement + text.slice(token.end), caret: token.start + replacement.length };
}

export class SlashAutocomplete {
  constructor(input, popup, options = {}) {
    this.input = input;
    this.popup = popup;
    this.options = options;
    this.document = input?.ownerDocument || globalThis.document;
    this.sourceKey = '';
    this.loadCatalog = null;
    this.generation = 0;
    this.uiGeneration = 0;
    this.catalog = null;
    this.loadedAt = 0;
    this.pending = null;
    this.items = [];
    this.selectedIndex = 0;
    this.enabled = false;
    this.focused = false;
    this.composing = false;
    this.active = false;
    this.disposed = false;
    this.token = null;
    this.dismissedToken = null;
    this.popupId = `slash-suggestions-${++nextPopupId}`;

    if (popup) {
      popup.id = this.popupId;
      popup.setAttribute?.('role', 'listbox');
      popup.setAttribute?.('aria-label', 'Slash commands');
      input?.setAttribute?.('aria-autocomplete', 'list');
      input?.setAttribute?.('aria-haspopup', 'listbox');
      input?.setAttribute?.('aria-controls', this.popupId);
    }
    this.hide();

    this.onFocus = () => { this.focused = true; this.refresh(); };
    this.onBlur = () => { this.focused = false; this.close(); };
    this.onCaret = () => this.refresh();
    this.onCompositionStart = () => { this.composing = true; this.close(); };
    this.onCompositionEnd = () => { this.composing = false; this.refresh(); };
    this.onOutside = event => {
      if (event.target !== input && !popup?.contains?.(event.target)) this.close();
    };
    this.onPopupPress = event => {
      if (event.button === undefined || event.button === 0) event.preventDefault();
    };
    this.inputEvents = [
      ['focus', this.onFocus], ['blur', this.onBlur], ['click', this.onCaret],
      ['keyup', this.onCaret], ['select', this.onCaret],
      ['compositionstart', this.onCompositionStart], ['compositionend', this.onCompositionEnd],
    ];
    for (const [name, listener] of this.inputEvents) input?.addEventListener?.(name, listener);
    this.document?.addEventListener?.('selectionchange', this.onCaret);
    this.document?.addEventListener?.('pointerdown', this.onOutside, true);
    popup?.addEventListener?.('pointerdown', this.onPopupPress);
    popup?.addEventListener?.('mousedown', this.onPopupPress);
  }

  setSource(key, loadCatalog) {
    const nextKey = String(key ?? '');
    if (this.disposed || nextKey === this.sourceKey) return;
    this.generation++;
    this.sourceKey = nextKey;
    this.loadCatalog = typeof loadCatalog === 'function' ? loadCatalog : null;
    this.catalog = null;
    this.loadedAt = 0;
    this.pending = null;
    this.reset();
  }

  async getCatalog(force = false) {
    if (this.disposed || !this.sourceKey || !this.loadCatalog) throw new Error('Slash command source is unavailable');
    if (this.pending) return this.pending;
    if (!force && this.catalog && Date.now() - this.loadedAt < CATALOG_MAX_AGE) return this.catalog;
    const generation = this.generation;
    const loadCatalog = this.loadCatalog;
    const request = Promise.resolve().then(() => {
      this.assertSource(generation);
      return loadCatalog();
    }).then(catalog => {
      this.assertSource(generation);
      const entries = normalizeSlashCatalog(catalog);
      this.catalog = entries;
      this.loadedAt = Date.now();
      return entries;
    }).catch(error => {
      if (generation === this.generation) {
        this.catalog = null;
        this.loadedAt = 0;
      }
      throw error;
    }).finally(() => {
      if (this.pending === request) this.pending = null;
    });
    this.pending = request;
    return request;
  }

  assertSource(generation) {
    if (this.disposed || generation !== this.generation) throw new Error('Slash command source changed; retry the message');
  }

  async parseCommand(text) {
    if (!String(text ?? '').trimStart().startsWith('/')) return null;
    const generation = this.generation;
    const entries = await this.getCatalog();
    this.assertSource(generation);
    return parseSlashCommand(text, entries);
  }

  setEnabled(enabled) {
    this.enabled = !!enabled;
    if (!this.enabled) this.close();
  }

  hasFocus() {
    return this.document && 'activeElement' in this.document
      ? this.document.activeElement === this.input : this.focused;
  }

  refresh() {
    if (this.disposed) return;
    const token = this.input && extractSlashToken(this.input.value, this.input.selectionStart, this.input.selectionEnd);
    if (!this.enabled || !this.popup || !this.hasFocus() || this.composing || !this.loadCatalog
        || !token || token.text === this.dismissedToken) {
      this.close();
      return;
    }
    this.dismissedToken = null;
    const opening = !this.active;
    this.active = true;
    if (token.text !== this.token?.text || token.start !== this.token?.start) this.selectedIndex = 0;
    this.token = token;
    this.render();
    if (!opening && (this.pending || !this.catalog || Date.now() - this.loadedAt < CATALOG_MAX_AGE)) return;
    const uiGeneration = this.uiGeneration;
    this.getCatalog(true).then(() => {
      if (!this.disposed && uiGeneration === this.uiGeneration) this.refresh();
    }).catch(() => {
      if (!this.disposed && uiGeneration === this.uiGeneration) this.hide();
    });
  }

  handleKeyDown(event) {
    if (this.disposed || this.composing || event.isComposing || event.keyCode === 229
        || event.shiftKey || event.ctrlKey || event.altKey || event.metaKey) return false;
    if (event.key === 'Escape' && this.active) {
      this.dismissedToken = this.token?.text || null;
      this.close();
      event.preventDefault();
      return true;
    }
    if (!this.active) return false;
    const token = extractSlashToken(this.input.value, this.input.selectionStart, this.input.selectionEnd);
    if (!token || token.text !== this.token?.text || token.start !== this.token?.start) {
      this.refresh();
      return false;
    }
    if (this.pending && (event.key === 'Enter' || event.key === 'Tab')) {
      event.preventDefault();
      return true;
    }
    if (!this.popup || this.popup.hidden || !this.items.length) return false;
    if (event.key === 'ArrowUp' || event.key === 'ArrowDown') {
      this.selectedIndex = Math.max(0, Math.min(this.items.length - 1,
        this.selectedIndex + (event.key === 'ArrowUp' ? -1 : 1)));
      this.updateSelection();
    } else if (event.key === 'Enter' || event.key === 'Tab') {
      this.complete(this.selectedIndex);
    } else return false;
    event.preventDefault();
    return true;
  }

  complete(index) {
    const entry = this.items[index];
    const token = this.input && extractSlashToken(this.input.value, this.input.selectionStart, this.input.selectionEnd);
    if (!this.enabled || this.disposed || !entry || !token
        || token.text !== this.token?.text || token.start !== this.token?.start) return;
    const result = completeSlashToken(this.input.value, token, entry.name);
    this.input.value = result.text;
    this.dismissedToken = `/${entry.name}`;
    this.close();
    this.input.focus?.();
    this.input.setSelectionRange?.(result.caret, result.caret);
    this.options.onComplete?.();
  }

  render() {
    const items = filterSlashEntries(this.catalog || [], this.token.text);
    if (!items.length) {
      this.hide();
      return;
    }
    const unchanged = !this.popup.hidden && items.length === this.items.length
      && items.every((entry, index) => entry === this.items[index]);
    this.items = items;
    this.selectedIndex = Math.min(this.selectedIndex, items.length - 1);
    if (!unchanged) {
      this.popup.textContent = '';
      items.forEach((entry, index) => {
        const option = this.document.createElement('div');
        option.className = 'slash-option';
        option.id = `${this.popupId}-${index}`;
        option.setAttribute('role', 'option');
        option.title = `/${entry.name}${entry.description ? ` — ${entry.description}` : ''}`;
        const name = this.document.createElement('span');
        name.className = 'slash-name';
        name.textContent = `/${entry.name}`;
        const description = this.document.createElement('span');
        description.className = 'slash-description';
        description.textContent = entry.description;
        option.appendChild(name);
        option.appendChild(description);
        option.addEventListener('click', event => { event.preventDefault(); this.complete(index); });
        this.popup.appendChild(option);
      });
    }
    this.popup.hidden = false;
    this.input.setAttribute?.('aria-expanded', 'true');
    this.fitPopup();
    this.updateSelection();
  }

  updateSelection() {
    for (let index = 0; index < this.popup.children.length; index++) {
      this.popup.children[index].setAttribute('aria-selected', String(index === this.selectedIndex));
    }
    const selected = this.popup.children[this.selectedIndex];
    if (selected) {
      this.input.setAttribute?.('aria-activedescendant', selected.id);
      selected.scrollIntoView?.({ block: 'nearest' });
    }
  }

  fitPopup() {
    const root = this.options.root;
    const rect = root?.getBoundingClientRect?.();
    if (!rect || !this.popup.style) return;
    const top = Math.max(globalThis.visualViewport?.offsetTop || 0,
      root.closest?.('.draft-body')?.getBoundingClientRect?.().top || 0);
    this.popup.style.maxHeight = `${Math.max(44, Math.min(272, rect.top - top - 12))}px`;
  }

  hide() {
    this.items = [];
    if (this.popup) {
      this.popup.hidden = true;
      this.popup.textContent = '';
    }
    this.input?.setAttribute?.('aria-expanded', 'false');
    this.input?.removeAttribute?.('aria-activedescendant');
  }

  close() {
    this.uiGeneration++;
    this.active = false;
    this.token = null;
    this.hide();
  }

  reset() {
    this.dismissedToken = null;
    this.selectedIndex = 0;
    this.close();
  }

  dispose() {
    if (this.disposed) return;
    this.disposed = true;
    this.generation++;
    this.loadCatalog = null;
    this.catalog = null;
    this.pending = null;
    this.reset();
    for (const [name, listener] of this.inputEvents) this.input?.removeEventListener?.(name, listener);
    this.document?.removeEventListener?.('selectionchange', this.onCaret);
    this.document?.removeEventListener?.('pointerdown', this.onOutside, true);
    this.popup?.removeEventListener?.('pointerdown', this.onPopupPress);
    this.popup?.removeEventListener?.('mousedown', this.onPopupPress);
  }
}
