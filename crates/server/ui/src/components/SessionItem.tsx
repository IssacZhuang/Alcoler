import { loadTitle } from "../prefs";
import { relativeTime } from "../format";
import { selectSession } from "../controller";
import { useApp } from "../store";
import type { SessionSummary } from "../types";

interface SessionItemProps {
  readonly session: SessionSummary;
  readonly active: boolean;
}

export function SessionItem({ session, active }: SessionItemProps) {
  const title =
    useApp((s) => s.titles[session.id]) ?? loadTitle(session.id) ?? session.id;
  const turns = `${session.turns_completed} ${
    session.turns_completed === 1 ? "turn" : "turns"
  }`;
  return (
    <button
      type="button"
      className={`w-full rounded-lg px-3 py-2 text-left ${
        active
          ? "bg-selected text-foreground"
          : "text-foreground-subtle hover:bg-hover hover:text-foreground"
      }`}
      onClick={() => {
        void selectSession(session.id);
      }}
    >
      <div className="truncate text-sm">{title}</div>
      <div className="mt-0.5 flex items-center gap-1.5 text-xs text-foreground-subtlest">
        <span className="truncate">{session.id}</span>
        <span aria-hidden="true">·</span>
        <span className="shrink-0">{turns}</span>
        <span aria-hidden="true">·</span>
        <span className="shrink-0">{relativeTime(session.updated_at_unix_ms)}</span>
      </div>
    </button>
  );
}
