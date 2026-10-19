import { TriangleAlert } from "lucide-react";

interface ErrorRowProps {
  readonly code: string;
  readonly fatal: boolean;
  readonly message: string | null;
}

export function ErrorRow({ code, fatal, message }: ErrorRowProps) {
  return (
    <div className="rounded-lg border border-destructive/40 bg-destructive/10 px-3 py-2">
      <div className="flex items-center gap-2 text-sm font-medium text-destructive">
        <TriangleAlert className="size-3.5 shrink-0" />
        <span className="font-mono text-xs">{code}</span>
        {fatal && <span className="pill border-destructive/40 text-destructive">fatal</span>}
      </div>
      {message !== null && (
        <div className="mt-1 break-words text-xs text-foreground-subtle">{message}</div>
      )}
    </div>
  );
}
