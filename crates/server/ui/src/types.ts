// Wire shapes mirrored from crates/contracts (events, commands) and
// crates/server (projection rows, API payloads). Everything crossing the JSON
// boundary enters as unknown and is narrowed through the parse* guards below.

export interface ModelRef {
  readonly provider: string;
  readonly model: string;
}

export interface ModelEntry extends ModelRef {
  readonly available: boolean;
}

export interface ModelCatalog {
  readonly default: ModelEntry | null;
  readonly entries: readonly ModelEntry[];
}

export interface SessionSummary {
  readonly id: string;
  readonly turns_completed: number;
  readonly updated_at_unix_ms: number;
}

export type ToolStatusWire =
  | "ok"
  | "timed_out"
  | "cancelled"
  | { readonly failed: { readonly code: string; readonly message: string } };

export type KernelEvent =
  | { readonly type: "session_started"; readonly session: string; readonly ts_ms: number }
  | { readonly type: "session_recovered"; readonly session: string; readonly from_turn: number; readonly ts_ms: number }
  | { readonly type: "turn_started"; readonly session: string; readonly turn: number; readonly ts_ms: number }
  | { readonly type: "model_delta"; readonly session: string; readonly turn: number; readonly seq: number; readonly text: string; readonly bytes: number; readonly ts_ms: number }
  | { readonly type: "tool_call"; readonly session: string; readonly turn: number; readonly call: string; readonly tool: string; readonly input: unknown; readonly ts_ms: number }
  | { readonly type: "tool_result"; readonly session: string; readonly turn: number; readonly call: string; readonly status: ToolStatusWire; readonly bytes: number; readonly ts_ms: number }
  | { readonly type: "compacted"; readonly session: string; readonly kept_tokens: number; readonly ts_ms: number }
  | { readonly type: "turn_done"; readonly session: string; readonly turn: number; readonly ts_ms: number }
  | { readonly type: "error"; readonly session: string | null; readonly code: string; readonly fatal: boolean; readonly ts_ms: number };

export type RowStatus = "ok" | "failed" | "timed_out" | "cancelled" | null;

export type Row =
  | { readonly kind: "user_text"; readonly text: string }
  | { readonly kind: "assistant_text"; readonly turn: number; readonly text: string; readonly open: boolean; readonly last_seq: number }
  | { readonly kind: "tool_use"; readonly turn: number; readonly call: string; readonly tool: string; readonly input: unknown; readonly status: RowStatus; readonly bytes: number }
  | { readonly kind: "error"; readonly code: string; readonly fatal: boolean };

export interface HistoryResponse {
  readonly session: string;
  readonly turns_completed: number;
  readonly model: ModelRef | null;
  readonly rows: readonly Row[];
}

export type ToolUiStatus = "running" | "ok" | "failed" | "timed_out" | "cancelled";

export interface ToolRowData {
  readonly kind: "tool_use";
  readonly turn: number;
  readonly call: string;
  readonly tool: string;
  readonly input: unknown;
  readonly status: ToolUiStatus;
  readonly bytes: number;
  readonly startedTs: number | null;
  readonly durationMs: number | null;
  readonly failedCode: string | null;
}

export type UiRow =
  | { readonly kind: "user_text"; readonly text: string }
  | { readonly kind: "assistant_text"; readonly turn: number; readonly text: string; readonly open: boolean; readonly lastSeq: number }
  | ToolRowData
  | { readonly kind: "error"; readonly code: string; readonly fatal: boolean; readonly message: string | null }
  | { readonly kind: "info"; readonly text: string };

export interface KeyedRow {
  readonly key: string;
  readonly row: UiRow;
}

export function toJson(text: string): unknown {
  return JSON.parse(text) as unknown;
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null;
}

function isString(v: unknown): v is string {
  return typeof v === "string";
}

function isNumber(v: unknown): v is number {
  return typeof v === "number";
}

function isBoolean(v: unknown): v is boolean {
  return typeof v === "boolean";
}

function parseModelRef(v: unknown): ModelRef | null {
  if (!isRecord(v) || !isString(v.provider) || !isString(v.model)) return null;
  return { provider: v.provider, model: v.model };
}

function parseModelEntry(v: unknown): ModelEntry | null {
  const ref = parseModelRef(v);
  if (ref === null || !isRecord(v) || !isBoolean(v.available)) return null;
  return { ...ref, available: v.available };
}

export function parseModelCatalog(v: unknown): ModelCatalog | null {
  if (!isRecord(v) || !Array.isArray(v.entries)) return null;
  const entries: ModelEntry[] = [];
  for (const raw of v.entries) {
    const entry = parseModelEntry(raw);
    if (entry === null) return null;
    entries.push(entry);
  }
  const def = isRecord(v.default) ? parseModelEntry(v.default) : null;
  return { default: def, entries };
}

export function parseSessionSummaries(v: unknown): SessionSummary[] | null {
  if (!Array.isArray(v)) return null;
  const out: SessionSummary[] = [];
  for (const raw of v) {
    if (
      !isRecord(raw) ||
      !isString(raw.id) ||
      !isNumber(raw.turns_completed) ||
      !isNumber(raw.updated_at_unix_ms)
    ) {
      return null;
    }
    out.push({
      id: raw.id,
      turns_completed: raw.turns_completed,
      updated_at_unix_ms: raw.updated_at_unix_ms,
    });
  }
  return out;
}

const ROW_STATUSES: readonly string[] = ["ok", "failed", "timed_out", "cancelled"];

function parseRowStatus(v: unknown): RowStatus {
  if (v === null) return null;
  return isString(v) && ROW_STATUSES.includes(v) ? (v as RowStatus) : null;
}

export function parseRow(v: unknown): Row | null {
  if (!isRecord(v) || !isString(v.kind)) return null;
  switch (v.kind) {
    case "user_text":
      return isString(v.text) ? { kind: "user_text", text: v.text } : null;
    case "assistant_text":
      if (
        !isNumber(v.turn) ||
        !isString(v.text) ||
        !isBoolean(v.open) ||
        !isNumber(v.last_seq)
      ) {
        return null;
      }
      return {
        kind: "assistant_text",
        turn: v.turn,
        text: v.text,
        open: v.open,
        last_seq: v.last_seq,
      };
    case "tool_use":
      if (
        !isNumber(v.turn) ||
        !isString(v.call) ||
        !isString(v.tool) ||
        !isNumber(v.bytes)
      ) {
        return null;
      }
      return {
        kind: "tool_use",
        turn: v.turn,
        call: v.call,
        tool: v.tool,
        input: v.input,
        status: parseRowStatus(v.status),
        bytes: v.bytes,
      };
    case "error":
      if (!isString(v.code) || !isBoolean(v.fatal)) return null;
      return { kind: "error", code: v.code, fatal: v.fatal };
    default:
      return null;
  }
}

export function parseRows(v: unknown): Row[] {
  if (!Array.isArray(v)) return [];
  const out: Row[] = [];
  for (const raw of v) {
    const row = parseRow(raw);
    if (row !== null) out.push(row);
  }
  return out;
}

export function parseHistoryResponse(v: unknown): HistoryResponse | null {
  if (
    !isRecord(v) ||
    !isString(v.session) ||
    !isNumber(v.turns_completed) ||
    !Array.isArray(v.rows)
  ) {
    return null;
  }
  const rows = parseRows(v.rows);
  const model = v.model === null ? null : parseModelRef(v.model);
  return { session: v.session, turns_completed: v.turns_completed, model, rows };
}

function parseToolStatusWire(v: unknown): ToolStatusWire | null {
  if (isString(v) && (v === "ok" || v === "timed_out" || v === "cancelled")) {
    return v;
  }
  if (isRecord(v) && isRecord(v.failed)) {
    const failed = v.failed;
    if (isString(failed.code) && isString(failed.message)) {
      return { failed: { code: failed.code, message: failed.message } };
    }
  }
  return null;
}

function sessionOf(v: Record<string, unknown>): string | null {
  return isString(v.session) ? v.session : null;
}

export function parseKernelEvent(v: unknown): KernelEvent | null {
  if (!isRecord(v) || !isString(v.type)) return null;
  const session = sessionOf(v);
  switch (v.type) {
    case "session_started":
      if (session === null || !isNumber(v.ts_ms)) return null;
      return { type: "session_started", session, ts_ms: v.ts_ms };
    case "session_recovered":
      if (session === null || !isNumber(v.from_turn) || !isNumber(v.ts_ms)) return null;
      return { type: "session_recovered", session, from_turn: v.from_turn, ts_ms: v.ts_ms };
    case "turn_started":
    case "turn_done":
      if (session === null || !isNumber(v.turn) || !isNumber(v.ts_ms)) return null;
      return v.type === "turn_started"
        ? { type: "turn_started", session, turn: v.turn, ts_ms: v.ts_ms }
        : { type: "turn_done", session, turn: v.turn, ts_ms: v.ts_ms };
    case "model_delta":
      if (
        session === null ||
        !isNumber(v.turn) ||
        !isNumber(v.seq) ||
        !isString(v.text) ||
        !isNumber(v.bytes) ||
        !isNumber(v.ts_ms)
      ) {
        return null;
      }
      return {
        type: "model_delta",
        session,
        turn: v.turn,
        seq: v.seq,
        text: v.text,
        bytes: v.bytes,
        ts_ms: v.ts_ms,
      };
    case "tool_call":
      if (
        session === null ||
        !isNumber(v.turn) ||
        !isString(v.call) ||
        !isString(v.tool) ||
        !isNumber(v.ts_ms)
      ) {
        return null;
      }
      return {
        type: "tool_call",
        session,
        turn: v.turn,
        call: v.call,
        tool: v.tool,
        input: v.input,
        ts_ms: v.ts_ms,
      };
    case "tool_result": {
      if (
        session === null ||
        !isNumber(v.turn) ||
        !isString(v.call) ||
        !isNumber(v.bytes) ||
        !isNumber(v.ts_ms)
      ) {
        return null;
      }
      const status = parseToolStatusWire(v.status);
      if (status === null) return null;
      return {
        type: "tool_result",
        session,
        turn: v.turn,
        call: v.call,
        status,
        bytes: v.bytes,
        ts_ms: v.ts_ms,
      };
    }
    case "compacted":
      if (session === null || !isNumber(v.kept_tokens) || !isNumber(v.ts_ms)) return null;
      return { type: "compacted", session, kept_tokens: v.kept_tokens, ts_ms: v.ts_ms };
    case "error":
      if (!isString(v.code) || !isBoolean(v.fatal) || !isNumber(v.ts_ms)) return null;
      return {
        type: "error",
        session: v.session === null ? null : session,
        code: v.code,
        fatal: v.fatal,
        ts_ms: v.ts_ms,
      };
    default:
      return null;
  }
}

export function statusFromWire(status: ToolStatusWire): {
  ui: ToolUiStatus;
  failedCode: string | null;
} {
  if (typeof status === "string") return { ui: status, failedCode: null };
  return { ui: "failed", failedCode: status.failed.code };
}
