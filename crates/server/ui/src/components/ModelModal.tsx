import { useEffect, useMemo, useState } from "react";
import { Circle, CircleCheck, X } from "lucide-react";
import { createSession, selectSession } from "../controller";
import {
  loadCustomModels,
  loadSelectedModelKey,
  modelKey,
  saveCustomModels,
  saveSelectedModelKey,
} from "../prefs";
import { setModalOpen, useApp } from "../store";

interface ModalEntry {
  readonly provider: string;
  readonly model: string;
  readonly available: boolean;
  readonly custom: boolean;
}

export function ModelModal() {
  const open = useApp((s) => s.modalOpen);
  const models = useApp((s) => s.models);
  const [customs, setCustoms] = useState<readonly ModalEntry[]>([]);
  const [selected, setSelected] = useState("");
  const [provider, setProvider] = useState("");
  const [modelName, setModelName] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!open) return;
    const stored: ModalEntry[] = loadCustomModels().map((m) => ({
      provider: m.provider,
      model: m.model,
      available: true,
      custom: true,
    }));
    setCustoms(stored);
    setError(null);
    setBusy(false);
    const combined = [...models.entries, ...stored];
    const valid = (key: string | null): boolean =>
      key !== null && combined.some((e) => modelKey(e.provider, e.model) === key);
    const def = models.default;
    setSelected((prev) => {
      if (valid(prev)) return prev;
      if (valid(loadSelectedModelKey())) {
        const saved = loadSelectedModelKey();
        return saved === null ? "" : saved;
      }
      if (def !== null) return modelKey(def.provider, def.model);
      const first = combined[0];
      return first === undefined ? "" : modelKey(first.provider, first.model);
    });
    const providers = [...new Set(models.entries.map((e) => e.provider))];
    setProvider((prev) =>
      prev !== "" && providers.includes(prev) ? prev : (providers[0] ?? ""),
    );
  }, [open, models]);

  useEffect(() => {
    if (!open) return;
    const onKey = (ev: KeyboardEvent) => {
      if (ev.key === "Escape") setModalOpen(false);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open]);

  const all = useMemo<readonly ModalEntry[]>(
    () => [...models.entries.map((e) => ({ ...e, custom: false })), ...customs],
    [models, customs],
  );

  const groups = useMemo(() => {
    const map = new Map<string, ModalEntry[]>();
    for (const entry of all) {
      const list = map.get(entry.provider) ?? [];
      list.push(entry);
      map.set(entry.provider, list);
    }
    return [...map.entries()];
  }, [all]);

  const providers = useMemo(
    () => [...new Set(models.entries.map((e) => e.provider))],
    [models],
  );

  if (!open) return null;

  const addCustom = () => {
    const name = modelName.trim();
    if (provider === "" || name === "") return;
    if (all.some((m) => m.provider === provider && m.model === name)) return;
    const next: ModalEntry[] = [
      ...customs,
      { provider, model: name, available: true, custom: true },
    ];
    setCustoms(next);
    saveCustomModels(next);
    setModelName("");
    setSelected(modelKey(provider, name));
  };

  const removeCustom = (target: ModalEntry) => {
    const next = customs.filter(
      (c) => !(c.provider === target.provider && c.model === target.model),
    );
    setCustoms(next);
    saveCustomModels(next);
    if (selected === modelKey(target.provider, target.model)) {
      const def = models.default;
      const fallback =
        def !== null && next.every((c) => modelKey(c.provider, c.model) !== modelKey(def.provider, def.model))
          ? modelKey(def.provider, def.model)
          : next[0] === undefined
            ? models.entries[0] === undefined
              ? ""
              : modelKey(models.entries[0].provider, models.entries[0].model)
            : modelKey(next[0].provider, next[0].model);
      setSelected(fallback);
    }
  };

  const create = async () => {
    const entry = all.find((e) => modelKey(e.provider, e.model) === selected);
    if (entry === undefined || busy) return;
    setBusy(true);
    setError(null);
    const result = await createSession(entry.provider, entry.model);
    if (result.ok) {
      saveSelectedModelKey(selected);
      setModalOpen(false);
      void selectSession(result.id);
    } else {
      setError(result.error);
      setBusy(false);
    }
  };

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4"
      onClick={(ev) => {
        if (ev.target === ev.currentTarget) setModalOpen(false);
      }}
    >
      <div
        role="dialog"
        aria-modal="true"
        aria-label="Model settings"
        className="flex max-h-[80vh] w-full max-w-md flex-col rounded-xl border border-border bg-card shadow-2xl"
      >
        <div className="flex shrink-0 items-center justify-between border-b border-border px-4 py-3">
          <div>
            <h2 className="text-sm font-semibold">New Session</h2>
            <p className="mt-0.5 text-xs text-foreground-subtlest">
              Each session is pinned to the model it starts with.
            </p>
          </div>
          <button
            type="button"
            className="btn btn-ghost btn-icon"
            aria-label="Close"
            onClick={() => setModalOpen(false)}
          >
            <X className="size-4" />
          </button>
        </div>

        <div className="min-h-0 flex-1 space-y-4 overflow-y-auto px-4 py-3">
          {groups.length === 0 && (
            <div className="py-6 text-center text-xs text-foreground-subtlest">
              Loading model catalog…
            </div>
          )}
          {groups.map(([groupProvider, entries]) => (
            <section key={groupProvider}>
              <div className="mb-1 text-xs font-medium uppercase tracking-wide text-foreground-subtlest">
                {groupProvider}
              </div>
              <div className="space-y-1">
                {entries.map((entry) => {
                  const key = modelKey(entry.provider, entry.model);
                  const active = key === selected;
                  return (
                    <div
                      key={key}
                      className={`flex w-full items-center gap-2 rounded-lg px-2.5 py-2 text-left ${
                        active ? "bg-selected" : "hover:bg-hover"
                      }`}
                    >
                      <button
                        type="button"
                        className="flex min-w-0 flex-1 items-center gap-2 text-left"
                        onClick={() => setSelected(key)}
                      >
                        {active ? (
                          <CircleCheck className="size-4 shrink-0 text-brand" />
                        ) : (
                          <Circle className="size-4 shrink-0 text-foreground-subtlest" />
                        )}
                        <span className="truncate font-mono text-sm">{entry.model}</span>
                        {!entry.available && (
                          <span className="pill shrink-0 border-warning/40 text-warning">
                            no API key
                          </span>
                        )}
                      </button>
                      {entry.custom && (
                        <button
                          type="button"
                          className="btn btn-ghost btn-icon shrink-0"
                          aria-label={`Remove ${entry.model}`}
                          onClick={() => removeCustom(entry)}
                        >
                          <X className="size-3.5" />
                        </button>
                      )}
                    </div>
                  );
                })}
              </div>
            </section>
          ))}

          <section className="border-t border-border pt-3">
            <div className="mb-1.5 text-xs font-medium uppercase tracking-wide text-foreground-subtlest">
              Add custom model
            </div>
            <form
              className="flex items-center gap-2"
              onSubmit={(ev) => {
                ev.preventDefault();
                addCustom();
              }}
            >
              <select
                value={provider}
                onChange={(ev) => setProvider(ev.target.value)}
                className="w-28 shrink-0 rounded-lg border border-border bg-card px-2 py-1.5 text-xs focus:border-border-hover focus:outline-none"
              >
                {providers.map((p) => (
                  <option key={p} value={p}>
                    {p}
                  </option>
                ))}
              </select>
              <input
                value={modelName}
                onChange={(ev) => setModelName(ev.target.value)}
                type="text"
                placeholder="model name"
                className="min-w-0 flex-1 rounded-lg border border-border bg-card px-2.5 py-1.5 text-xs placeholder:text-foreground-subtlest focus:border-border-hover focus:outline-none"
              />
              <button type="submit" className="btn btn-outline shrink-0 px-2.5 py-1 text-xs">
                Add
              </button>
            </form>
          </section>
        </div>

        <div className="flex shrink-0 items-center justify-end gap-2 border-t border-border px-4 py-3">
          {error !== null && (
            <span className="mr-auto max-w-[50%] truncate text-xs text-destructive" title={error}>
              {error}
            </span>
          )}
          <button type="button" className="btn btn-ghost" onClick={() => setModalOpen(false)}>
            Cancel
          </button>
          <button
            type="button"
            className="btn btn-primary"
            disabled={selected === "" || busy}
            onClick={() => {
              void create();
            }}
          >
            {busy ? "Creating…" : "Create Session"}
          </button>
        </div>
      </div>
    </div>
  );
}
