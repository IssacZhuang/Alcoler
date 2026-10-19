export function UserRow({ text }: { text: string }) {
  return (
    <div className="min-w-0">
      <div className="mb-1 text-xs font-medium text-user">You</div>
      <div className="max-w-[85%] whitespace-pre-wrap break-words rounded-2xl bg-surface px-3.5 py-2.5 text-sm leading-relaxed">
        {text}
      </div>
    </div>
  );
}
