import type { ToolRowData, ToolUiStatus } from "../../types";
import { formatBytes, formatDuration, formatInput } from "../../format";

const STATUS_META: Record<ToolUiStatus, { label: string; cls: string }> = {
  running: { label: "running", cls: "text-warning border-warning/40" },
  ok: { label: "ok", cls: "text-success border-success/40" },
  failed: { label: "failed", cls: "text-destructive border-destructive/40" },
  timed_out: { label: "timed out", cls: "text-destructive border-destructive/40" },
  cancelled: { label: "cancelled", cls: "text-foreground-subtlest border-border" },
};

export function ToolRow({ row }: { row: ToolRowData }) {
  const meta = STATUS_META[row.status];
  const label = row.failedCode === null ? meta.label : `failed · ${row.failedCode}`;
  return (
    <div className="rounded-lg border border-border border-l-2 border-l-tool/70 bg-panel px-3 py-2">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-mono text-xs font-medium text-tool">{row.tool}</span>
        <span
          className={`pill ${meta.cls} ${row.status === "running" ? "animate-pulse" : ""}`}
        >
          {label}
        </span>
        {row.bytes > 0 && (
          <span className="text-xs text-foreground-subtlest">{formatBytes(row.bytes)}</span>
        )}
        {row.durationMs !== null && (
          <span className="text-xs text-foreground-subtlest">
            {formatDuration(row.durationMs)}
          </span>
        )}
      </div>
      <details className="mt-1.5">
        <summary className="cursor-pointer select-none text-xs text-foreground-subtlest transition-colors hover:text-foreground-subtle">
          input
        </summary>
        <pre className="mt-1.5 overflow-x-auto rounded-md bg-black/30 p-2 font-mono text-xs leading-relaxed text-foreground-subtle">
          {formatInput(row.input)}
        </pre>
      </details>
    </div>
  );
}
