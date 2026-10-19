import { useEffect, useRef, useState } from "react";
import { ArrowUp } from "lucide-react";
import { send } from "../controller";
import { useApp } from "../store";

export function Composer() {
  const currentId = useApp((s) => s.currentId);
  const [value, setValue] = useState("");
  const inputRef = useRef<HTMLTextAreaElement | null>(null);

  useEffect(() => {
    const el = inputRef.current;
    if (el === null) return;
    el.style.height = "0px";
    el.style.height = `${Math.min(el.scrollHeight, 200)}px`;
  }, [value]);

  const submit = async () => {
    const text = value.trim();
    if (text.length === 0 || currentId === null) return;
    setValue("");
    const ok = await send(text);
    if (!ok) setValue(text);
  };

  return (
    <div className="shrink-0 border-t border-border bg-panel px-4 py-3">
      <div className="mx-auto flex max-w-3xl items-end gap-2">
        <textarea
          ref={inputRef}
          rows={1}
          value={value}
          disabled={currentId === null}
          placeholder={
            currentId === null
              ? "Create a session first"
              : "Send a message · Enter to send, Shift+Enter for newline"
          }
          className="max-h-[200px] min-h-[38px] flex-1 resize-none rounded-xl border border-border bg-card px-3.5 py-2 text-sm leading-relaxed text-foreground placeholder:text-foreground-subtlest focus:border-border-hover focus:outline-none disabled:opacity-50"
          onChange={(ev) => setValue(ev.target.value)}
          onKeyDown={(ev) => {
            if (ev.key === "Enter" && !ev.shiftKey) {
              ev.preventDefault();
              void submit();
            }
          }}
        />
        <button
          type="button"
          className="btn btn-primary h-[38px] w-[38px] shrink-0 rounded-xl p-0"
          disabled={currentId === null || value.trim().length === 0}
          aria-label="Send message"
          onClick={() => {
            void submit();
          }}
        >
          <ArrowUp className="size-4" />
        </button>
      </div>
    </div>
  );
}
