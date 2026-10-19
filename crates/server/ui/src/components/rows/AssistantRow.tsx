import { Markdown } from "./Markdown";

interface AssistantRowProps {
  readonly text: string;
  readonly open: boolean;
}

export function AssistantRow({ text, open }: AssistantRowProps) {
  return (
    <div className="min-w-0">
      <div className="mb-1 text-xs font-medium text-assistant">Assistant</div>
      <div className="break-words text-sm leading-relaxed">
        {text.length > 0 && <Markdown text={text} />}
        {open && <span className="cursor-blink" aria-hidden="true" />}
      </div>
    </div>
  );
}
