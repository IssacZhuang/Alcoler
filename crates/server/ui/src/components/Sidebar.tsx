import { useMemo, useRef, useState } from "react";
import { Plus, Settings } from "lucide-react";
import { useApp, setModalOpen } from "../store";
import {
  DEFAULT_SIDEBAR_WIDTH,
  loadSelectedModelKey,
  loadSidebarWidth,
  MAX_SIDEBAR_WIDTH,
  MIN_SIDEBAR_WIDTH,
  modelKey,
  saveSidebarWidth,
} from "../prefs";
import { SessionItem } from "./SessionItem";

export function Sidebar() {
  const sessions = useApp((s) => s.sessions);
  const currentId = useApp((s) => s.currentId);
  const models = useApp((s) => s.models);
  const [width, setWidth] = useState<number>(
    () => loadSidebarWidth() ?? DEFAULT_SIDEBAR_WIDTH,
  );
  const [dragging, setDragging] = useState(false);
  const dragStart = useRef<{ x: number; width: number } | null>(null);

  const footerModel = useMemo(() => {
    const saved = loadSelectedModelKey();
    const match =
      models.entries.find((e) => modelKey(e.provider, e.model) === saved) ??
      models.default ??
      models.entries[0];
    return match === undefined ? null : `${match.provider} / ${match.model}`;
  }, [models]);

  return (
    <aside
      style={{ width: `${width}px` }}
      className="relative flex shrink-0 flex-col border-r border-border bg-panel"
    >
      <div className="flex h-12 shrink-0 items-center gap-2 px-4">
        <span className="text-sm font-semibold tracking-wide">Alcoler</span>
        <button
          type="button"
          className="btn btn-ghost btn-icon ml-auto"
          aria-label="Model settings"
          onClick={() => setModalOpen(true)}
        >
          <Settings className="size-4" />
        </button>
      </div>
      <div className="px-3 pb-3">
        <button
          type="button"
          className="btn btn-primary w-full"
          onClick={() => setModalOpen(true)}
        >
          <Plus className="size-4" />
          New Session
        </button>
      </div>
      <nav className="min-h-0 flex-1 space-y-0.5 overflow-y-auto px-2 pb-2">
        {sessions.length === 0 && (
          <div className="px-3 py-6 text-center text-xs text-foreground-subtlest">
            No sessions yet
          </div>
        )}
        {sessions.map((session) => (
          <SessionItem
            key={session.id}
            session={session}
            active={session.id === currentId}
          />
        ))}
      </nav>
      <button
        type="button"
        className="flex items-center gap-2 border-t border-border px-4 py-2.5 text-left text-xs text-foreground-subtle hover:bg-hover hover:text-foreground"
        onClick={() => setModalOpen(true)}
        title="Model for new sessions"
      >
        <span className="truncate font-mono">
          {footerModel === null ? "no model" : footerModel}
        </span>
      </button>
      <div
        role="separator"
        aria-orientation="vertical"
        className={`absolute right-0 top-0 z-10 h-full w-1 cursor-col-resize touch-none select-none transition-colors ${
          dragging ? "bg-border-hover" : "hover:bg-border-hover"
        }`}
        onPointerDown={(ev) => {
          dragStart.current = { x: ev.clientX, width };
          setDragging(true);
          ev.currentTarget.setPointerCapture(ev.pointerId);
        }}
        onPointerMove={(ev) => {
          const start = dragStart.current;
          if (start === null) return;
          const next = start.width + (ev.clientX - start.x);
          setWidth(Math.min(MAX_SIDEBAR_WIDTH, Math.max(MIN_SIDEBAR_WIDTH, next)));
        }}
        onPointerUp={() => {
          if (dragStart.current !== null) {
            dragStart.current = null;
            setDragging(false);
            saveSidebarWidth(width);
          }
        }}
      />
    </aside>
  );
}
