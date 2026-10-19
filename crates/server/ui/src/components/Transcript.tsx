import { useEffect, useMemo, useRef, useState } from "react";
import { ChevronDown } from "lucide-react";
import { useApp } from "../store";
import { RowView } from "./rows/RowView";

export function Transcript() {
  const base = useApp((s) => s.base);
  const live = useApp((s) => s.live);
  const rows = useMemo(() => [...base, ...live], [base, live]);
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const [stick, setStick] = useState(true);

  const onScroll = () => {
    const el = scrollRef.current;
    if (el === null) return;
    setStick(el.scrollHeight - el.scrollTop - el.clientHeight < 48);
  };

  useEffect(() => {
    const el = scrollRef.current;
    if (el !== null && stick) el.scrollTop = el.scrollHeight;
  }, [rows, stick]);

  const jumpToLatest = () => {
    const el = scrollRef.current;
    if (el === null) return;
    el.scrollTop = el.scrollHeight;
    setStick(true);
  };

  return (
    <div className="relative min-h-0 flex-1">
      <div ref={scrollRef} onScroll={onScroll} className="h-full overflow-y-auto">
        <div className="mx-auto flex max-w-3xl flex-col gap-4 px-6 py-5">
          {rows.length === 0 && (
            <div className="py-16 text-center text-sm text-foreground-subtlest">
              No turns yet. Send a message below.
            </div>
          )}
          {rows.map((keyed) => (
            <RowView key={keyed.key} row={keyed.row} />
          ))}
        </div>
      </div>
      {!stick && (
        <button
          type="button"
          className="absolute bottom-4 left-1/2 -translate-x-1/2 rounded-full border border-border bg-card p-2 text-foreground-subtle shadow-lg hover:border-border-hover hover:text-foreground"
          aria-label="Jump to latest"
          onClick={jumpToLatest}
        >
          <ChevronDown className="size-4" />
        </button>
      )}
    </div>
  );
}
