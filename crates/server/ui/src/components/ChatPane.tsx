import { MessageSquare, TriangleAlert, X } from "lucide-react";
import { setGlobalError, setModalOpen, useApp } from "../store";
import { Composer } from "./Composer";
import { EventLog } from "./EventLog";
import { Header } from "./Header";
import { Transcript } from "./Transcript";

export function ChatPane() {
  const currentId = useApp((s) => s.currentId);
  const globalError = useApp((s) => s.globalError);
  const eventLogOpen = useApp((s) => s.eventLogOpen);

  if (currentId === null) {
    return (
      <main className="flex min-w-0 flex-1 flex-col items-center justify-center gap-3">
        <MessageSquare className="size-8 text-foreground-subtlest" />
        <div className="text-sm text-foreground-subtle">
          Create a session to start testing
        </div>
        <button
          type="button"
          className="btn btn-primary"
          onClick={() => setModalOpen(true)}
        >
          New Session
        </button>
      </main>
    );
  }

  return (
    <main className="flex min-w-0 flex-1 flex-col">
      <Header />
      {globalError !== null && (
        <div className="flex items-center gap-2 border-b border-destructive/30 bg-destructive/10 px-4 py-2 text-sm text-destructive">
          <TriangleAlert className="size-4 shrink-0" />
          <span className="truncate">
            {globalError.code}
            {globalError.fatal ? " · fatal" : ""}
          </span>
          <button
            type="button"
            className="btn btn-ghost btn-icon ml-auto shrink-0"
            aria-label="Dismiss error"
            onClick={() => setGlobalError(null)}
          >
            <X className="size-4" />
          </button>
        </div>
      )}
      <Transcript />
      {eventLogOpen && <EventLog />}
      <Composer />
    </main>
  );
}
