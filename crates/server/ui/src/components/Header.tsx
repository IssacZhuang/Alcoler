import { Activity, Square } from "lucide-react";
import { stop } from "../controller";
import { loadTitle } from "../prefs";
import { setEventLogOpen, useApp } from "../store";

const CONN_META = {
  open: { label: "live", dot: "bg-success" },
  connecting: { label: "connecting", dot: "bg-warning" },
  closed: { label: "offline", dot: "bg-destructive" },
} as const;

export function Header() {
  const currentId = useApp((s) => s.currentId);
  const model = useApp((s) => s.model);
  const providers = useApp((s) => s.providers);
  const running = useApp((s) => s.running);
  const runningTurn = useApp((s) => s.runningTurn);
  const conn = useApp((s) => s.conn);
  const eventLogOpen = useApp((s) => s.eventLogOpen);
  const storedTitle = useApp((s) =>
    currentId === null ? null : (s.titles[currentId] ?? null),
  );

  const title =
    currentId === null ? null : (storedTitle ?? loadTitle(currentId) ?? currentId);
  const connMeta = CONN_META[conn];

  return (
    <header className="flex h-12 shrink-0 items-center gap-3 border-b border-border bg-panel px-4">
      <span className="min-w-0 truncate text-sm font-medium">{title}</span>
      {model !== null && (
        <span className="pill shrink-0 font-mono text-foreground-subtle">
          {providers.find((p) => p.id === model.provider)?.name ?? model.provider} /{" "}
          {model.model}
        </span>
      )}
      <div className="ml-auto flex shrink-0 items-center gap-3">
        <span
          className="flex items-center gap-1.5 text-xs text-foreground-subtlest"
          title={`SSE stream ${conn}`}
        >
          <span className={`size-2 rounded-full ${connMeta.dot}`} />
          {connMeta.label}
        </span>
        {running ? (
          <span className="flex items-center gap-1.5 text-xs text-assistant">
            <span className="size-2 animate-pulse rounded-full bg-assistant" />
            Running{runningTurn === null ? "" : ` · turn ${runningTurn}`}
          </span>
        ) : (
          <span className="text-xs text-foreground-subtlest">Idle</span>
        )}
        <button
          type="button"
          className={`btn btn-outline ${
            eventLogOpen ? "border-border-hover text-foreground" : ""
          }`}
          onClick={() => setEventLogOpen(!eventLogOpen)}
        >
          <Activity className="size-3.5" />
          Events
        </button>
        {running && (
          <button type="button" className="btn btn-danger" onClick={() => stop()}>
            <Square className="size-3.5" />
            Stop
          </button>
        )}
      </div>
    </header>
  );
}
