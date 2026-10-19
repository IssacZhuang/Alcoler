import * as api from "./api";
import { loadTitle, saveTitle } from "./prefs";
import {
  applyKernelEvent,
  applySnapshot,
  pushEvent,
  setBase,
  setConn,
  setSessions,
  setTitle,
  store,
  turnFinished,
} from "./store";
import { parseKernelEvent, parseRows, toJson, type KernelEvent, type Row } from "./types";

let source: EventSource | null = null;
let streamSession: string | null = null;

export function closeEvents(): void {
  if (source !== null) {
    source.close();
    source = null;
  }
  streamSession = null;
  setConn("closed");
}

export async function refreshSessions(): Promise<void> {
  try {
    const sessions = await api.listSessions();
    sessions.sort((a, b) => b.updated_at_unix_ms - a.updated_at_unix_ms);
    setSessions(sessions);
  } catch (e) {
    console.error("session list load failed:", e);
  }
}

export async function loadHistory(sessionId: string): Promise<void> {
  try {
    const history = await api.getHistory(sessionId);
    if (store.getSnapshot().currentId !== sessionId) return;
    const rows = history === null ? [] : history.rows;
    setBase(rows, history === null ? null : history.model);
    cacheTitle(sessionId, rows);
  } catch (e) {
    console.error("history load failed:", e);
  }
}

// The sidebar title is the first user message; localStorage is the
// cross-reload copy and the store map is the reactive one.
function cacheTitle(sessionId: string, rows: readonly Row[]): void {
  if (loadTitle(sessionId) !== null) return;
  for (const row of rows) {
    if (row.kind === "user_text" && row.text.trim().length > 0) {
      const firstLine = row.text.split("\n")[0] ?? row.text;
      const title = firstLine.slice(0, 80).trim();
      if (title.length > 0) {
        saveTitle(sessionId, title);
        setTitle(sessionId, title);
      }
      return;
    }
  }
}

async function refreshAfterTurn(sessionId: string): Promise<void> {
  await Promise.all([loadHistory(sessionId), refreshSessions()]);
}

function safeEvent(text: string): KernelEvent | null {
  try {
    return parseKernelEvent(toJson(text));
  } catch {
    return null;
  }
}

function safeRows(text: string): Row[] {
  try {
    return parseRows(toJson(text));
  } catch {
    return [];
  }
}

export function openEvents(sessionId: string): void {
  closeEvents();
  streamSession = sessionId;
  store.set({ conn: "connecting", events: [] });
  const es = new EventSource(`/api/sessions/${encodeURIComponent(sessionId)}/events`);
  source = es;
  es.onopen = () => {
    if (streamSession === sessionId) setConn("open");
  };
  // EventSource reconnects on its own; the server replays a fresh snapshot.
  es.onerror = () => {
    if (streamSession === sessionId) setConn("connecting");
  };
  es.addEventListener("snapshot", (ev) => {
    const text = typeof ev.data === "string" ? ev.data : "";
    const rows = safeRows(text);
    pushEvent({ receivedAt: Date.now(), type: "snapshot", raw: `${rows.length} rows` });
    if (streamSession === sessionId) applySnapshot(rows);
  });
  es.onmessage = (ev) => {
    const text = typeof ev.data === "string" ? ev.data : "";
    const event = safeEvent(text);
    if (event === null) return;
    pushEvent({ receivedAt: Date.now(), type: event.type, raw: text });
    if (streamSession !== sessionId) return;
    if (event.type === "turn_done") {
      turnFinished();
      void refreshAfterTurn(sessionId);
      return;
    }
    applyKernelEvent(event, sessionId);
  };
}
