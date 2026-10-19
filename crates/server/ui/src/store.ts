import { useSyncExternalStore } from "react";
import {
  statusFromWire,
  type KernelEvent,
  type KeyedRow,
  type ModelCatalog,
  type ModelRef,
  type Row,
  type RowStatus,
  type SessionSummary,
  type ToolUiStatus,
  type UiRow,
} from "./types";

export type ConnState = "connecting" | "open" | "closed";

export interface RawEvent {
  readonly receivedAt: number;
  readonly type: string;
  readonly raw: string;
}

export interface GlobalError {
  readonly code: string;
  readonly fatal: boolean;
}

export interface AppState {
  readonly models: ModelCatalog;
  readonly sessions: readonly SessionSummary[];
  readonly titles: Readonly<Record<string, string>>;
  readonly currentId: string | null;
  readonly base: readonly KeyedRow[];
  readonly live: readonly KeyedRow[];
  readonly running: boolean;
  readonly runningTurn: number | null;
  readonly model: ModelRef | null;
  readonly conn: ConnState;
  readonly events: readonly RawEvent[];
  readonly eventLogOpen: boolean;
  readonly modalOpen: boolean;
  readonly globalError: GlobalError | null;
}

const EVENT_BUFFER = 500;

const initialState: AppState = {
  models: { default: null, entries: [] },
  sessions: [],
  titles: {},
  currentId: null,
  base: [],
  live: [],
  running: false,
  runningTurn: null,
  model: null,
  conn: "closed",
  events: [],
  eventLogOpen: false,
  modalOpen: false,
  globalError: null,
};

class Store {
  private state: AppState = initialState;

  private readonly listeners = new Set<() => void>();

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };

  getSnapshot = (): AppState => this.state;

  set(patch: Partial<AppState>): void {
    this.state = { ...this.state, ...patch };
    for (const listener of this.listeners) listener();
  }
}

export const store = new Store();

export function useApp<T>(select: (s: AppState) => T): T {
  return useSyncExternalStore(store.subscribe, () => select(store.getSnapshot()));
}

let userSeq = 0;
let infoSeq = 0;
let errSeq = 0;

export function fromWireRow(row: Row, key: string): KeyedRow {
  if (row.kind === "tool_use") {
    const ui: UiRow = {
      kind: "tool_use",
      turn: row.turn,
      call: row.call,
      tool: row.tool,
      input: row.input,
      status: rowStatusToUi(row.status),
      bytes: row.bytes,
      startedTs: null,
      durationMs: null,
      failedCode: null,
    };
    return { key, row: ui };
  }
  if (row.kind === "assistant_text") {
    return { key, row: { ...row, lastSeq: row.last_seq } };
  }
  if (row.kind === "error") {
    return { key, row: { kind: "error", code: row.code, fatal: row.fatal, message: null } };
  }
  return { key, row };
}

function rowStatusToUi(status: RowStatus): ToolUiStatus {
  if (status === null) return "running";
  return status;
}

export function setModels(models: ModelCatalog): void {
  store.set({ models });
}

export function setSessions(sessions: readonly SessionSummary[]): void {
  store.set({ sessions });
}

export function setTitle(sessionId: string, title: string): void {
  store.set({ titles: { ...store.getSnapshot().titles, [sessionId]: title } });
}

export function setBase(rows: readonly Row[], model: ModelRef | null): void {
  store.set({ base: rows.map((row, i) => fromWireRow(row, `b${i}`)), model });
}

export function setConn(conn: ConnState): void {
  store.set({ conn });
}

export function setModalOpen(open: boolean): void {
  store.set({ modalOpen: open });
}

export function setEventLogOpen(open: boolean): void {
  store.set({ eventLogOpen: open });
}

export function setGlobalError(error: GlobalError | null): void {
  store.set({ globalError: error });
}

export function pushEvent(event: RawEvent): void {
  const events = [...store.getSnapshot().events, event];
  store.set({ events: events.length > EVENT_BUFFER ? events.slice(-EVENT_BUFFER) : events });
}

export function clearEvents(): void {
  store.set({ events: [] });
}

export function pushUserLive(text: string): void {
  userSeq += 1;
  const entry: KeyedRow = { key: `u${userSeq}`, row: { kind: "user_text", text } };
  store.set({ live: [...store.getSnapshot().live, entry] });
}

function pushInfo(text: string): void {
  infoSeq += 1;
  const entry: KeyedRow = { key: `i${infoSeq}`, row: { kind: "info", text } };
  store.set({ live: [...store.getSnapshot().live, entry] });
}

function pushErrorRow(code: string, fatal: boolean, message: string | null): void {
  errSeq += 1;
  const entry: KeyedRow = { key: `e${errSeq}`, row: { kind: "error", code, fatal, message } };
  store.set({ live: [...store.getSnapshot().live, entry] });
}

function setLive(live: readonly KeyedRow[]): void {
  store.set({ live });
}

export function applySnapshot(rows: readonly Row[]): void {
  const live = rows.map((row, i) => fromWireRow(row, `s${i}`));
  let runningTurn: number | null = null;
  let running = false;
  for (const keyed of live) {
    const row = keyed.row;
    if (row.kind === "assistant_text" && row.open) {
      running = true;
      runningTurn = row.turn;
    }
    if (row.kind === "tool_use" && row.status === "running") running = true;
  }
  store.set({ live, running, runningTurn });
}

export function turnFinished(): void {
  // History is authoritative after the turn is persisted; live rows drop until
  // the history refetch lands.
  store.set({ live: [], running: false, runningTurn: null });
}

export function applyKernelEvent(event: KernelEvent, sessionId: string): void {
  if (event.type === "error" && event.session === null) {
    setGlobalError({ code: event.code, fatal: event.fatal });
    return;
  }
  if (event.type === "error") {
    pushErrorRow(event.code, event.fatal, null);
    return;
  }
  if (event.session !== sessionId) return;

  switch (event.type) {
    case "session_started":
      pushInfo("session started");
      break;
    case "session_recovered":
      pushInfo(`session recovered from turn ${event.from_turn}`);
      break;
    case "compacted":
      pushInfo(`context compacted · kept ${event.kept_tokens.toLocaleString("en-US")} tokens`);
      break;
    case "turn_started": {
      const live = [...store.getSnapshot().live];
      if (!live.some((k) => k.row.kind === "assistant_text" && k.row.turn === event.turn)) {
        live.push({
          key: `a${event.turn}`,
          row: { kind: "assistant_text", turn: event.turn, text: "", open: true, lastSeq: 0 },
        });
      }
      setLive(live);
      store.set({ running: true, runningTurn: event.turn });
      break;
    }
    case "model_delta": {
      const live = store.getSnapshot().live.map((k) => {
        if (k.row.kind === "assistant_text" && k.row.open && k.row.turn === event.turn) {
          return {
            key: k.key,
            row: { ...k.row, text: k.row.text + event.text, lastSeq: event.seq },
          };
        }
        return k;
      });
      setLive(live);
      break;
    }
    case "tool_call": {
      const live = [...store.getSnapshot().live];
      if (!live.some((k) => k.row.kind === "tool_use" && k.row.call === event.call)) {
        live.push({
          key: `t${event.call}`,
          row: {
            kind: "tool_use",
            turn: event.turn,
            call: event.call,
            tool: event.tool,
            input: event.input,
            status: "running",
            bytes: 0,
            startedTs: event.ts_ms,
            durationMs: null,
            failedCode: null,
          },
        });
      }
      setLive(live);
      store.set({ running: true });
      break;
    }
    case "tool_result": {
      const { ui, failedCode } = statusFromWire(event.status);
      const live = store.getSnapshot().live.map((k) => {
        if (k.row.kind === "tool_use" && k.row.call === event.call) {
          return {
            key: k.key,
            row: {
              ...k.row,
              status: ui,
              bytes: event.bytes,
              failedCode,
              durationMs: k.row.startedTs === null ? null : event.ts_ms - k.row.startedTs,
            },
          };
        }
        return k;
      });
      setLive(live);
      break;
    }
    default:
      break;
  }
}

export function pushSendFailure(message: string): void {
  pushErrorRow("send_failed", false, message);
}
