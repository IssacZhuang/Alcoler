import * as api from "./api";
import { closeEvents, loadHistory, openEvents, refreshSessions } from "./sse";
import {
  pushSendFailure,
  pushUserLive,
  setModels,
  store,
} from "./store";

let initialized = false;

export function init(): void {
  if (initialized) return;
  initialized = true;
  void loadModels();
  void refreshSessions().then(() => {
    const first = store.getSnapshot().sessions[0];
    if (first !== undefined) void selectSession(first.id);
  });
}

async function loadModels(): Promise<void> {
  try {
    setModels(await api.getModels());
  } catch (e) {
    console.error("model catalog load failed:", e);
  }
}

export async function selectSession(sessionId: string): Promise<void> {
  closeEvents();
  store.set({
    currentId: sessionId,
    base: [],
    live: [],
    running: false,
    runningTurn: null,
    model: null,
    globalError: null,
    conn: "connecting",
  });
  await loadHistory(sessionId);
  if (store.getSnapshot().currentId !== sessionId) return;
  openEvents(sessionId);
}

export type CreateResult = { ok: true; id: string } | { ok: false; error: string };

export async function createSession(provider: string, model: string): Promise<CreateResult> {
  try {
    const id = await api.createSession(provider, model);
    await refreshSessions();
    return { ok: true, id };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : String(e) };
  }
}

export async function send(text: string): Promise<boolean> {
  const id = store.getSnapshot().currentId;
  if (id === null) return false;
  pushUserLive(text);
  try {
    await api.postMessage(id, text);
    return true;
  } catch (e) {
    pushSendFailure(e instanceof Error ? e.message : String(e));
    return false;
  }
}

export function stop(): void {
  const id = store.getSnapshot().currentId;
  if (id === null) return;
  api.cancelSession(id).catch((e: unknown) => {
    console.error("cancel failed:", e);
  });
}
