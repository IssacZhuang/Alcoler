import { useEffect, useMemo, useState } from "react";
import {
  Circle,
  CircleCheck,
  Eye,
  EyeOff,
  Plus,
  Trash2,
  X,
} from "lucide-react";
import * as api from "../api";
import { createSession, refreshProviders, selectSession } from "../controller";
import { loadSelectedModelKey, modelKey, saveSelectedModelKey } from "../prefs";
import { setModalOpen, useApp } from "../store";
import type { ApiFormat, ProviderConfig, ProviderInput } from "../types";

const BUILTIN_NAV = "builtin:mock";
const NEW_NAV = "__new__";

const FORMAT_LABEL: Readonly<Record<ApiFormat, string>> = {
  "anthropic-messages": "Anthropic Messages",
  "openai-chat-completions": "Chat Completions",
};

interface Draft {
  readonly name: string;
  readonly baseUrl: string;
  readonly apiKey: string;
  readonly apiFormat: ApiFormat;
  readonly models: readonly string[];
}

function draftOf(config: ProviderConfig): Draft {
  return {
    name: config.name,
    baseUrl: config.base_url,
    apiKey: config.api_key,
    apiFormat: config.api_format,
    models: config.models,
  };
}

function inputOf(draft: Draft): ProviderInput {
  return {
    name: draft.name.trim(),
    api_format: draft.apiFormat,
    base_url: draft.baseUrl.trim(),
    api_key: draft.apiKey,
    models: draft.models,
  };
}

const sameDraft = (a: Draft, b: Draft): boolean =>
  a.name === b.name &&
  a.baseUrl === b.baseUrl &&
  a.apiKey === b.apiKey &&
  a.apiFormat === b.apiFormat &&
  a.models.length === b.models.length &&
  a.models.every((m, i) => m === b.models[i]);

type SaveState =
  | { readonly kind: "idle" }
  | { readonly kind: "saving" }
  | { readonly kind: "saved" }
  | { readonly kind: "error"; readonly message: string };

function ProviderCard({
  config,
  selected,
  onSelectModel,
  onDeleted,
}: {
  readonly config: ProviderConfig;
  readonly selected: string;
  readonly onSelectModel: (providerId: string, model: string) => void;
  readonly onDeleted: (providerId: string) => void;
}) {
  const [draft, setDraft] = useState<Draft>(() => draftOf(config));
  const [saved, setSaved] = useState<Draft>(() => draftOf(config));
  const [save, setSave] = useState<SaveState>({ kind: "idle" });
  const [showKey, setShowKey] = useState(false);
  const [newModel, setNewModel] = useState("");
  const [armDelete, setArmDelete] = useState(false);

  useEffect(() => {
    setArmDelete(false);
  }, [config.id]);

  const commit = async (next: Draft): Promise<void> => {
    if (sameDraft(next, saved)) return;
    setDraft(next);
    setSave({ kind: "saving" });
    try {
      await api.updateProvider(config.id, inputOf(next));
      setSaved(next);
      setSave({ kind: "saved" });
      await refreshProviders();
    } catch (e) {
      setSave({
        kind: "error",
        message: e instanceof Error ? e.message : String(e),
      });
    }
  };

  const addModel = () => {
    const name = newModel.trim();
    if (name === "" || draft.models.includes(name)) return;
    setNewModel("");
    void commit({ ...draft, models: [...draft.models, name] });
  };

  const removeModel = (model: string) => {
    void commit({ ...draft, models: draft.models.filter((m) => m !== model) });
  };

  const remove = async () => {
    if (!armDelete) {
      setArmDelete(true);
      return;
    }
    try {
      await api.deleteProvider(config.id);
      await refreshProviders();
      onDeleted(config.id);
    } catch (e) {
      setSave({
        kind: "error",
        message: e instanceof Error ? e.message : String(e),
      });
    }
  };

  const saveLabel =
    save.kind === "saving"
      ? "Saving…"
      : save.kind === "saved"
        ? "Saved"
        : save.kind === "error"
          ? `Save failed: ${save.message}`
          : "";

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto p-4">
      <div className="flex items-center gap-2">
        <input
          value={draft.name}
          onChange={(ev) => setDraft({ ...draft, name: ev.target.value })}
          onBlur={() => void commit(draft)}
          onKeyDown={(ev) => {
            if (ev.key === "Enter") ev.currentTarget.blur();
          }}
          aria-label="Provider name"
          className="min-w-0 flex-1 rounded-lg border border-transparent bg-transparent px-2 py-1 text-sm font-medium hover:border-border focus:border-border-hover focus:bg-card focus:outline-none"
        />
        <span className="pill shrink-0 text-foreground-subtlest">
          {FORMAT_LABEL[draft.apiFormat]}
        </span>
        <button
          type="button"
          className={`btn btn-icon shrink-0 ${armDelete ? "btn-danger" : "btn-ghost"}`}
          aria-label={armDelete ? "Confirm delete provider" : "Delete provider"}
          title={armDelete ? "Click again to delete" : "Delete provider"}
          onClick={() => {
            void remove();
          }}
        >
          <Trash2 className="size-4" />
        </button>
      </div>
      {armDelete && (
        <div className="rounded-lg border border-destructive/40 px-3 py-1.5 text-xs text-destructive">
          Delete this provider? Sessions pinned to it lose their route. Click
          the trash icon again to confirm.
        </div>
      )}

      <section>
        <label className="mb-1 block text-xs text-foreground-subtle">Base URL</label>
        <input
          value={draft.baseUrl}
          onChange={(ev) => setDraft({ ...draft, baseUrl: ev.target.value })}
          onBlur={() => void commit(draft)}
          onKeyDown={(ev) => {
            if (ev.key === "Enter") ev.currentTarget.blur();
          }}
          type="text"
          placeholder="https://api.example.com/v1"
          spellCheck={false}
          className="w-full rounded-lg border border-border bg-card px-3 py-2 font-mono text-xs placeholder:text-foreground-subtlest focus:border-border-hover focus:outline-none"
        />
      </section>

      <section>
        <label className="mb-1 block text-xs text-foreground-subtle">API format</label>
        <select
          value={draft.apiFormat}
          onChange={(ev) =>
            void commit({
              ...draft,
              apiFormat:
                ev.target.value === "anthropic-messages"
                  ? "anthropic-messages"
                  : "openai-chat-completions",
            })
          }
          className="w-full rounded-lg border border-border bg-card px-3 py-2 text-xs focus:border-border-hover focus:outline-none"
        >
          <option value="anthropic-messages">
            Anthropic Messages (/v1/messages)
          </option>
          <option value="openai-chat-completions">
            Chat Completions (/v1/chat/completions)
          </option>
        </select>
      </section>

      <section>
        <label className="mb-1 block text-xs text-foreground-subtle">API key</label>
        <div className="flex items-center gap-2">
          <input
            value={draft.apiKey}
            onChange={(ev) => setDraft({ ...draft, apiKey: ev.target.value })}
            onBlur={() => void commit(draft)}
            onKeyDown={(ev) => {
              if (ev.key === "Enter") ev.currentTarget.blur();
            }}
            type={showKey ? "text" : "password"}
            placeholder="sk-…"
            spellCheck={false}
            autoComplete="off"
            className="min-w-0 flex-1 rounded-lg border border-border bg-card px-3 py-2 font-mono text-xs placeholder:text-foreground-subtlest focus:border-border-hover focus:outline-none"
          />
          <button
            type="button"
            className="btn btn-ghost btn-icon shrink-0"
            aria-label={showKey ? "Hide API key" : "Show API key"}
            onClick={() => setShowKey(!showKey)}
          >
            {showKey ? <EyeOff className="size-4" /> : <Eye className="size-4" />}
          </button>
        </div>
      </section>

      <section className="min-h-0">
        <div className="mb-1.5 flex items-center justify-between">
          <span className="text-xs text-foreground-subtle">
            Models{" "}
            <span className="text-foreground-subtlest">
              ({draft.models.length})
            </span>
          </span>
          {saveLabel !== "" && (
            <span
              className={`text-xs ${
                save.kind === "error" ? "text-destructive" : "text-foreground-subtlest"
              }`}
            >
              {saveLabel}
            </span>
          )}
        </div>
        <div className="space-y-1">
          {draft.models.length === 0 && (
            <div className="rounded-lg border border-dashed border-border px-3 py-4 text-center text-xs text-foreground-subtlest">
              No models yet. Add a model to start sessions on this provider.
            </div>
          )}
          {draft.models.map((model) => {
            const key = modelKey(config.id, model);
            const active = key === selected;
            return (
              <div
                key={model}
                className={`flex w-full items-center gap-2 rounded-lg px-2.5 py-2 ${
                  active ? "bg-selected" : "hover:bg-hover"
                }`}
              >
                <button
                  type="button"
                  className="flex min-w-0 flex-1 items-center gap-2 text-left"
                  onClick={() => onSelectModel(config.id, model)}
                >
                  {active ? (
                    <CircleCheck className="size-4 shrink-0 text-brand" />
                  ) : (
                    <Circle className="size-4 shrink-0 text-foreground-subtlest" />
                  )}
                  <span className="truncate font-mono text-sm">{model}</span>
                </button>
                <button
                  type="button"
                  className="btn btn-ghost btn-icon shrink-0"
                  aria-label={`Remove ${model}`}
                  onClick={() => removeModel(model)}
                >
                  <X className="size-3.5" />
                </button>
              </div>
            );
          })}
        </div>
        <form
          className="mt-2 flex items-center gap-2"
          onSubmit={(ev) => {
            ev.preventDefault();
            addModel();
          }}
        >
          <input
            value={newModel}
            onChange={(ev) => setNewModel(ev.target.value)}
            type="text"
            placeholder="model id"
            spellCheck={false}
            aria-label="New model id"
            className="min-w-0 flex-1 rounded-lg border border-border bg-card px-2.5 py-1.5 font-mono text-xs placeholder:text-foreground-subtlest focus:border-border-hover focus:outline-none"
          />
          <button type="submit" className="btn btn-outline shrink-0 px-2.5 py-1 text-xs">
            <Plus className="size-3.5" />
            Add
          </button>
        </form>
      </section>
    </div>
  );
}

function NewProviderForm({
  onCreated,
}: {
  readonly onCreated: (id: string) => void;
}) {
  const [name, setName] = useState("");
  const [apiFormat, setApiFormat] = useState<ApiFormat>("openai-chat-completions");
  const [baseUrl, setBaseUrl] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [firstModel, setFirstModel] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const create = async () => {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      const config = await api.createProvider({
        name: name.trim(),
        api_format: apiFormat,
        base_url: baseUrl.trim(),
        api_key: apiKey,
        models: firstModel.trim() === "" ? [] : [firstModel.trim()],
      });
      await refreshProviders();
      onCreated(config.id);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setBusy(false);
    }
  };

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto p-4">
      <p className="text-xs text-foreground-subtle">
        Point a custom provider at any Anthropic Messages or OpenAI-compatible
        endpoint. Config is persisted server-side and takes effect immediately.
      </p>
      <section>
        <label className="mb-1 block text-xs text-foreground-subtle">Name</label>
        <input
          value={name}
          onChange={(ev) => setName(ev.target.value)}
          type="text"
          placeholder="my provider"
          className="w-full rounded-lg border border-border bg-card px-3 py-2 text-xs placeholder:text-foreground-subtlest focus:border-border-hover focus:outline-none"
        />
      </section>
      <section>
        <label className="mb-1 block text-xs text-foreground-subtle">API format</label>
        <select
          value={apiFormat}
          onChange={(ev) =>
            setApiFormat(
              ev.target.value === "anthropic-messages"
                ? "anthropic-messages"
                : "openai-chat-completions",
            )
          }
          className="w-full rounded-lg border border-border bg-card px-3 py-2 text-xs focus:border-border-hover focus:outline-none"
        >
          <option value="anthropic-messages">
            Anthropic Messages (/v1/messages)
          </option>
          <option value="openai-chat-completions">
            Chat Completions (/v1/chat/completions)
          </option>
        </select>
      </section>
      <section>
        <label className="mb-1 block text-xs text-foreground-subtle">Base URL</label>
        <input
          value={baseUrl}
          onChange={(ev) => setBaseUrl(ev.target.value)}
          type="text"
          placeholder="https://api.example.com/v1"
          spellCheck={false}
          className="w-full rounded-lg border border-border bg-card px-3 py-2 font-mono text-xs placeholder:text-foreground-subtlest focus:border-border-hover focus:outline-none"
        />
      </section>
      <section>
        <label className="mb-1 block text-xs text-foreground-subtle">API key</label>
        <input
          value={apiKey}
          onChange={(ev) => setApiKey(ev.target.value)}
          type="password"
          placeholder="sk-… (may stay empty)"
          spellCheck={false}
          autoComplete="off"
          className="w-full rounded-lg border border-border bg-card px-3 py-2 font-mono text-xs placeholder:text-foreground-subtlest focus:border-border-hover focus:outline-none"
        />
      </section>
      <section>
        <label className="mb-1 block text-xs text-foreground-subtle">First model</label>
        <input
          value={firstModel}
          onChange={(ev) => setFirstModel(ev.target.value)}
          type="text"
          placeholder="model id"
          spellCheck={false}
          className="w-full rounded-lg border border-border bg-card px-3 py-2 font-mono text-xs placeholder:text-foreground-subtlest focus:border-border-hover focus:outline-none"
        />
      </section>
      {error !== null && (
        <div className="rounded-lg border border-destructive/40 px-3 py-1.5 text-xs text-destructive">
          {error}
        </div>
      )}
      <button
        type="button"
        className="btn btn-outline shrink-0 self-start px-3 py-1.5 text-xs"
        disabled={busy}
        onClick={() => {
          void create();
        }}
      >
        <Plus className="size-3.5" />
        {busy ? "Creating…" : "Create provider"}
      </button>
    </div>
  );
}

export function ModelModal() {
  const open = useApp((s) => s.modalOpen);
  const models = useApp((s) => s.models);
  const providers = useApp((s) => s.providers);
  const [nav, setNav] = useState<string>(BUILTIN_NAV);
  const [selection, setSelection] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const mockEntries = useMemo(
    () => models.entries.filter((e) => e.provider === "mock"),
    [models],
  );

  const validSelection = (key: string | null): boolean => {
    if (key === null) return false;
    const sep = key.indexOf("::");
    if (sep <= 0) return false;
    const pid = key.slice(0, sep);
    const model = key.slice(sep + 2);
    if (mockEntries.some((e) => e.model === model) && pid === "mock") return true;
    return providers.some(
      (p) => p.id === pid && p.models.some((m) => m === model),
    );
  };

  useEffect(() => {
    if (!open) return;
    setError(null);
    setBusy(false);
    const saved = loadSelectedModelKey();
    if (validSelection(saved)) {
      setSelection(saved ?? "");
      const pid = saved === null ? "" : saved.slice(0, saved.indexOf("::"));
      if (pid !== "mock" && providers.some((p) => p.id === pid)) setNav(pid);
      else setNav(BUILTIN_NAV);
      return;
    }
    const def = mockEntries[0];
    setSelection(def === undefined ? "" : modelKey("mock", def.model));
    setNav(BUILTIN_NAV);
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const onKey = (ev: KeyboardEvent) => {
      if (ev.key === "Escape") setModalOpen(false);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open]);

  if (!open) return null;

  let selectionLabel: string | null = null;
  if (selection !== "") {
    const sep = selection.indexOf("::");
    if (sep > 0) {
      const pid = selection.slice(0, sep);
      const model = selection.slice(sep + 2);
      const provider = providers.find((p) => p.id === pid);
      selectionLabel = `${provider === undefined ? pid : provider.name} / ${model}`;
    }
  }

  const create = async () => {
    if (selection === "" || busy) return;
    const sep = selection.indexOf("::");
    if (sep <= 0) return;
    const provider = selection.slice(0, sep);
    const model = selection.slice(sep + 2);
    setBusy(true);
    setError(null);
    const result = await createSession(provider, model);
    if (result.ok) {
      saveSelectedModelKey(selection);
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
        className="flex h-[70vh] w-full max-w-3xl flex-col rounded-xl border border-border bg-card shadow-2xl"
      >
        <div className="flex shrink-0 items-center justify-between border-b border-border px-4 py-3">
          <div>
            <h2 className="text-sm font-semibold">Model settings</h2>
            <p className="mt-0.5 text-xs text-foreground-subtlest">
              Configure custom providers; each session is pinned to the model it
              starts with.
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

        <div className="grid min-h-0 flex-1 grid-cols-[176px_minmax(0,1fr)]">
          <nav className="flex min-h-0 flex-col overflow-y-auto border-r border-border p-2">
            <div className="px-2 pb-1 pt-1 text-[10px] font-medium uppercase tracking-wide text-foreground-subtlest">
              Built-in
            </div>
            <button
              type="button"
              className={`mb-2 flex w-full items-center gap-2 rounded-lg px-2.5 py-1.5 text-left text-sm ${
                nav === BUILTIN_NAV
                  ? "bg-selected text-foreground"
                  : "text-foreground-subtle hover:bg-hover hover:text-foreground"
              }`}
              onClick={() => setNav(BUILTIN_NAV)}
            >
              <span className="size-2 shrink-0 rounded-full bg-success" />
              mock
            </button>
            <div className="px-2 pb-1 text-[10px] font-medium uppercase tracking-wide text-foreground-subtlest">
              Custom providers
            </div>
            {providers.length === 0 && (
              <div className="px-2.5 py-2 text-xs text-foreground-subtlest">
                None yet
              </div>
            )}
            {providers.map((p) => (
              <button
                key={p.id}
                type="button"
                className={`flex w-full items-center gap-2 rounded-lg px-2.5 py-1.5 text-left text-sm ${
                  nav === p.id
                    ? "bg-selected text-foreground"
                    : "text-foreground-subtle hover:bg-hover hover:text-foreground"
                }`}
                onClick={() => setNav(p.id)}
              >
                <span
                  className={`size-2 shrink-0 rounded-full ${
                    p.models.length === 0 ? "bg-warning" : "bg-success"
                  }`}
                />
                <span className="truncate">{p.name}</span>
              </button>
            ))}
            <button
              type="button"
              className={`mt-2 flex w-full items-center gap-2 rounded-lg px-2.5 py-1.5 text-left text-sm ${
                nav === NEW_NAV
                  ? "bg-selected text-foreground"
                  : "text-foreground-subtle hover:bg-hover hover:text-foreground"
              }`}
              onClick={() => setNav(NEW_NAV)}
            >
              <Plus className="size-3.5 shrink-0" />
              Add provider
            </button>
          </nav>

          {nav === BUILTIN_NAV && (
            <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto p-4">
              <div className="flex items-center gap-2">
                <span className="text-sm font-medium">mock</span>
                <span className="pill text-foreground-subtlest">built-in</span>
              </div>
              <p className="text-xs text-foreground-subtle">
                The scripted provider: a fixed tool round trip per turn, no
                network. Used for kernel testing.
              </p>
              <div className="space-y-1">
                {mockEntries.map((entry) => {
                  const key = modelKey("mock", entry.model);
                  const active = key === selection;
                  return (
                    <button
                      key={key}
                      type="button"
                      className={`flex w-full items-center gap-2 rounded-lg px-2.5 py-2 text-left ${
                        active ? "bg-selected" : "hover:bg-hover"
                      }`}
                      onClick={() => setSelection(key)}
                    >
                      {active ? (
                        <CircleCheck className="size-4 shrink-0 text-brand" />
                      ) : (
                        <Circle className="size-4 shrink-0 text-foreground-subtlest" />
                      )}
                      <span className="truncate font-mono text-sm">{entry.model}</span>
                    </button>
                  );
                })}
              </div>
            </div>
          )}
          {nav === NEW_NAV && (
            <NewProviderForm
              onCreated={(id) => {
                setNav(id);
              }}
            />
          )}
          {providers.map((p) =>
            nav === p.id ? (
              <ProviderCard
                key={p.id}
                config={p}
                selected={selection}
                onSelectModel={(providerId, model) =>
                  setSelection(modelKey(providerId, model))
                }
                onDeleted={(providerId) => {
                  if (selection.startsWith(`${providerId}::`)) {
                    const def = mockEntries[0];
                    setSelection(
                      def === undefined ? "" : modelKey("mock", def.model),
                    );
                  }
                  setNav(BUILTIN_NAV);
                }}
              />
            ) : null,
          )}
        </div>

        <div className="flex shrink-0 items-center justify-end gap-2 border-t border-border px-4 py-3">
          {error !== null && (
            <span
              className="mr-auto max-w-[50%] truncate text-xs text-destructive"
              title={error}
            >
              {error}
            </span>
          )}
          {selectionLabel !== null && (
            <span className="mr-auto font-mono text-xs text-foreground-subtlest">
              {selectionLabel}
            </span>
          )}
          <button type="button" className="btn btn-ghost" onClick={() => setModalOpen(false)}>
            Cancel
          </button>
          <button
            type="button"
            className="btn btn-primary"
            disabled={selection === "" || busy}
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
