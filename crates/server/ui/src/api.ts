import {
  parseHistoryResponse,
  parseModelCatalog,
  parseProviderConfig,
  parseProviderConfigs,
  parseSessionSummaries,
  toJson,
  type HistoryResponse,
  type ModelCatalog,
  type ProviderConfig,
  type ProviderInput,
  type SessionSummary,
} from "./types";

export class ApiError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = "ApiError";
    this.status = status;
  }
}

async function request(path: string, init?: RequestInit): Promise<unknown> {
  const res = await fetch(path, init);
  const text = await res.text();
  if (!res.ok) {
    throw new ApiError(res.status, text.length > 0 ? text : res.statusText);
  }
  return text.length > 0 ? toJson(text) : null;
}

export async function getModels(): Promise<ModelCatalog> {
  const v = await request("/api/models");
  const catalog = parseModelCatalog(v);
  if (catalog === null) throw new Error("invalid /api/models payload");
  return catalog;
}

export async function listProviders(): Promise<ProviderConfig[]> {
  const v = await request("/api/providers");
  const configs = parseProviderConfigs(v);
  if (configs === null) throw new Error("invalid /api/providers payload");
  return configs;
}

async function mutateProvider(
  path: string,
  method: "POST" | "PUT",
  input: ProviderInput,
): Promise<ProviderConfig> {
  const v = await request(path, {
    method,
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(input),
  });
  const config = parseProviderConfig(v);
  if (config === null) throw new Error("invalid provider payload");
  return config;
}

export function createProvider(input: ProviderInput): Promise<ProviderConfig> {
  return mutateProvider("/api/providers", "POST", input);
}

export function updateProvider(
  id: string,
  input: ProviderInput,
): Promise<ProviderConfig> {
  return mutateProvider(`/api/providers/${encodeURIComponent(id)}`, "PUT", input);
}

export async function deleteProvider(id: string): Promise<void> {
  await request(`/api/providers/${encodeURIComponent(id)}`, { method: "DELETE" });
}

export async function listSessions(): Promise<SessionSummary[]> {
  const v = await request("/api/sessions");
  const sessions = parseSessionSummaries(v);
  if (sessions === null) throw new Error("invalid /api/sessions payload");
  return sessions;
}

export async function createSession(provider: string, model: string): Promise<string> {
  const v = await request("/api/sessions", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ provider, model }),
  });
  if (typeof v !== "object" || v === null) throw new Error("invalid create response");
  const id = (v as Record<string, unknown>).id;
  if (typeof id !== "string") throw new Error("invalid create response");
  return id;
}

export async function getHistory(sessionId: string): Promise<HistoryResponse | null> {
  const v = await request(
    `/api/sessions/${encodeURIComponent(sessionId)}/history`,
  );
  return parseHistoryResponse(v);
}

export async function postMessage(sessionId: string, text: string): Promise<void> {
  await request(`/api/sessions/${encodeURIComponent(sessionId)}/messages`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ text }),
  });
}

export async function cancelSession(sessionId: string): Promise<void> {
  await request(`/api/sessions/${encodeURIComponent(sessionId)}/cancel`, {
    method: "POST",
  });
}
