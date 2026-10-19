import { useEffect, useRef } from "react";
import { clearEvents, useApp } from "../store";
import { clockOf } from "../format";

function typeClass(type: string): string {
  if (type === "model_delta" || type === "snapshot") return "text-foreground-subtlest";
  if (type === "tool_call") return "text-tool";
  if (type === "tool_result") return "text-tool-result";
  if (type === "error") return "text-destructive";
  if (type === "compacted") return "text-reasoning";
  if (type === "turn_started" || type === "turn_done") return "text-assistant";
  if (type === "session_started" || type === "session_recovered") return "text-user";
  return "text-foreground-subtle";
}

function compact(raw: string): string {
  return raw.length > 200 ? `${raw.slice(0, 200)}…` : raw;
}

export function EventLog() {
  const events = useApp((s) => s.events);
  const listRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    const el = listRef.current;
    if (el !== null) el.scrollTop = el.scrollHeight;
  }, [events]);

  return (
    <div className="flex h-48 shrink-0 flex-col border-t border-border bg-panel">
      <div className="flex shrink-0 items-center gap-2 border-b border-border px-3 py-1.5">
        <span className="text-xs font-medium">Raw events</span>
        <span className="text-xs text-foreground-subtlest">{events.length}</span>
        <button
          type="button"
          className="btn btn-ghost ml-auto px-2 py-0.5 text-xs"
          onClick={() => clearEvents()}
        >
          Clear
        </button>
      </div>
      <div ref={listRef} className="min-h-0 flex-1 overflow-y-auto px-3 py-1.5">
        {events.length === 0 && (
          <div className="py-4 text-center font-mono text-[11px] text-foreground-subtlest">
            waiting for events…
          </div>
        )}
        {events.map((event, i) => (
          <div key={i} className="flex gap-2 font-mono text-[11px] leading-relaxed">
            <span className="shrink-0 text-foreground-subtlest">
              {clockOf(event.receivedAt)}
            </span>
            <span className={`shrink-0 ${typeClass(event.type)}`}>{event.type}</span>
            <span className="truncate text-foreground-subtle" title={event.raw}>
              {compact(event.raw)}
            </span>
          </div>
        ))}
      </div>
    </div>
  );
}
