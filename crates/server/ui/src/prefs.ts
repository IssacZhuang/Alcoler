import { toJson, type ModelEntry } from "./types";

const CUSTOM_MODELS_KEY = "alcoler.custom-models";
const SELECTED_MODEL_KEY = "alcoler.selected-model";
const TITLES_KEY = "alcoler.session-titles";
const SIDEBAR_WIDTH_KEY = "alcoler.sidebar-width";

export const DEFAULT_SIDEBAR_WIDTH = 264;
export const MIN_SIDEBAR_WIDTH = 200;
export const MAX_SIDEBAR_WIDTH = 480;

export function modelKey(provider: string, model: string): string {
  return `${provider}::${model}`;
}

function readStorage(key: string): unknown {
  try {
    const raw = localStorage.getItem(key);
    return raw === null ? null : toJson(raw);
  } catch {
    return null;
  }
}

function writeStorage(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    // storage may be unavailable; preferences are best-effort
  }
}

export function loadCustomModels(): ModelEntry[] {
  const v = readStorage(CUSTOM_MODELS_KEY);
  if (!Array.isArray(v)) return [];
  const out: ModelEntry[] = [];
  for (const raw of v) {
    if (typeof raw !== "object" || raw === null) continue;
    const rec = raw as Record<string, unknown>;
    if (typeof rec.provider === "string" && typeof rec.model === "string") {
      out.push({ provider: rec.provider, model: rec.model, available: true });
    }
  }
  return out;
}

export function saveCustomModels(models: readonly ModelEntry[]): void {
  writeStorage(
    CUSTOM_MODELS_KEY,
    JSON.stringify(models.map((m) => ({ provider: m.provider, model: m.model }))),
  );
}

export function loadSelectedModelKey(): string | null {
  const v = readStorage(SELECTED_MODEL_KEY);
  return typeof v === "string" && v.length > 0 ? v : null;
}

export function saveSelectedModelKey(key: string): void {
  writeStorage(SELECTED_MODEL_KEY, JSON.stringify(key));
}

export function loadTitle(sessionId: string): string | null {
  const v = readStorage(TITLES_KEY);
  if (typeof v !== "object" || v === null) return null;
  const title = (v as Record<string, unknown>)[sessionId];
  return typeof title === "string" && title.length > 0 ? title : null;
}

export function saveTitle(sessionId: string, title: string): void {
  const v = readStorage(TITLES_KEY);
  const base = typeof v === "object" && v !== null ? (v as Record<string, unknown>) : {};
  base[sessionId] = title;
  writeStorage(TITLES_KEY, JSON.stringify(base));
}

export function loadSidebarWidth(): number | null {
  const v = readStorage(SIDEBAR_WIDTH_KEY);
  return typeof v === "number" && v >= MIN_SIDEBAR_WIDTH && v <= MAX_SIDEBAR_WIDTH
    ? v
    : null;
}

export function saveSidebarWidth(px: number): void {
  writeStorage(SIDEBAR_WIDTH_KEY, JSON.stringify(px));
}
