"use strict";

const els = {
  list: document.getElementById("session-list"),
  newSession: document.getElementById("new-session"),
  title: document.getElementById("session-title"),
  status: document.getElementById("session-status"),
  sessionModel: document.getElementById("session-model"),
  transcript: document.getElementById("transcript"),
  composer: document.getElementById("composer"),
  input: document.getElementById("input"),
  send: document.getElementById("send"),
  stop: document.getElementById("stop"),
  modelSelect: document.getElementById("model-select"),
  manageModels: document.getElementById("manage-models"),
  modelsPanel: document.getElementById("models-panel"),
  modelsList: document.getElementById("models-list"),
  modelForm: document.getElementById("model-form"),
  modelProvider: document.getElementById("model-provider"),
  modelName: document.getElementById("model-name"),
};

const CUSTOM_MODELS_KEY = "alcoler.custom-models";
const SELECTED_MODEL_KEY = "alcoler.selected-model";

const state = {
  sessions: [],
  current: null,
  base: [], // rows of completed turns (server history is authoritative)
  live: [], // rows of the open turn (snapshot + applied events)
  running: false,
  source: null,
  models: { default: null, entries: [] },
  customs: loadCustoms(),
};

// ---------- models ----------

function loadCustoms() {
  try {
    const raw = JSON.parse(localStorage.getItem(CUSTOM_MODELS_KEY) || "[]");
    return Array.isArray(raw) ? raw : [];
  } catch {
    return [];
  }
}

function saveCustoms() {
  localStorage.setItem(CUSTOM_MODELS_KEY, JSON.stringify(state.customs));
}

function allModels() {
  return state.models.entries.concat(
    state.customs.map((m) => ({ ...m, available: true, custom: true }))
  );
}

async function loadModels() {
  const res = await fetch("/api/models");
  if (!res.ok) return;
  state.models = await res.json();
  renderModelSelect();
  renderModelsPanel();
}

function modelKey(entry) {
  return `${entry.provider}::${entry.model}`;
}

function renderModelSelect() {
  const previous = localStorage.getItem(SELECTED_MODEL_KEY);
  els.modelSelect.replaceChildren();
  let havePrevious = false;
  for (const entry of allModels()) {
    const option = document.createElement("option");
    option.value = modelKey(entry);
    option.textContent = `${entry.provider} / ${entry.model}`;
    if (!entry.available) {
      option.disabled = true;
      option.textContent += " (unavailable)";
    }
    if (previous !== null && option.value === previous) {
      option.selected = true;
      havePrevious = true;
    }
    els.modelSelect.appendChild(option);
  }
  const fallback = state.models.default;
  if (!havePrevious && fallback) {
    els.modelSelect.value = modelKey(fallback);
  }
}

els.modelSelect.addEventListener("change", () => {
  localStorage.setItem(SELECTED_MODEL_KEY, els.modelSelect.value);
});

els.manageModels.addEventListener("click", () => {
  els.modelsPanel.hidden = !els.modelsPanel.hidden;
  renderModelsPanel();
});

function renderModelsPanel() {
  if (els.modelsPanel.hidden) return;
  els.modelsList.replaceChildren();
  for (const entry of allModels()) {
    const row = document.createElement("div");
    row.className = "model-item";
    const label = document.createElement("span");
    label.textContent = `${entry.provider} / ${entry.model}`;
    if (!entry.available) label.classList.add("unavailable");
    row.appendChild(label);
    if (entry.custom) {
      const remove = document.createElement("button");
      remove.type = "button";
      remove.className = "remove";
      remove.textContent = "Remove";
      remove.addEventListener("click", () => {
        const key = modelKey(entry);
        state.customs = state.customs.filter((m) => modelKey(m) !== key);
        saveCustoms();
        renderModelSelect();
        renderModelsPanel();
      });
      row.appendChild(remove);
    }
    els.modelsList.appendChild(row);
  }
  const providers = [...new Set(state.models.entries.map((m) => m.provider))];
  els.modelProvider.replaceChildren();
  for (const provider of providers) {
    const option = document.createElement("option");
    option.value = provider;
    option.textContent = provider;
    els.modelProvider.appendChild(option);
  }
}

els.modelForm.addEventListener("submit", (ev) => {
  ev.preventDefault();
  const provider = els.modelProvider.value;
  const model = els.modelName.value.trim();
  if (!provider || !model) return;
  if (allModels().some((m) => m.provider === provider && m.model === model)) return;
  state.customs.push({ provider, model });
  saveCustoms();
  els.modelName.value = "";
  renderModelSelect();
  els.modelSelect.value = modelKey({ provider, model });
  localStorage.setItem(SELECTED_MODEL_KEY, els.modelSelect.value);
  renderModelsPanel();
});

// ---------- sessions ----------

async function loadSessions() {
  const res = await fetch("/api/sessions");
  if (!res.ok) return;
  state.sessions = await res.json();
  state.sessions.sort((a, b) => b.updated_at_unix_ms - a.updated_at_unix_ms);
  renderSessions();
}

function renderSessions() {
  els.list.replaceChildren();
  for (const s of state.sessions) {
    const li = document.createElement("li");
    li.textContent = `${s.id} (${s.turns_completed} turns)`;
    if (s.id === state.current) li.classList.add("selected");
    li.addEventListener("click", () => selectSession(s.id));
    els.list.appendChild(li);
  }
}

els.newSession.addEventListener("click", async () => {
  const selected = allModels().find((m) => modelKey(m) === els.modelSelect.value)
    || state.models.default;
  const res = await fetch("/api/sessions", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ provider: selected.provider, model: selected.model }),
  });
  if (!res.ok) return;
  const body = await res.json();
  await loadSessions();
  selectSession(body.id);
});

// ---------- transcript ----------

async function selectSession(id) {
  state.current = id;
  state.base = [];
  state.live = [];
  state.running = false;
  if (state.source) state.source.close();
  renderSessions();
  renderStatus();
  await refreshHistory();
  openEvents(id);
  els.input.disabled = false;
  els.send.disabled = false;
  els.input.focus();
}

async function refreshHistory() {
  const res = await fetch(`/api/sessions/${encodeURIComponent(state.current)}/history`);
  const body = res.ok ? await res.json() : null;
  state.base = body ? body.rows : [];
  els.sessionModel.textContent = body && body.model
    ? `${body.model.provider} / ${body.model.model}`
    : "";
  render();
}

function openEvents(id) {
  const es = new EventSource(`/api/sessions/${encodeURIComponent(id)}/events`);
  state.source = es;
  es.addEventListener("snapshot", (ev) => {
    state.live = JSON.parse(ev.data);
    state.running = state.live.some((r) => r.kind === "assistant_text" && r.open);
    render();
    renderStatus();
  });
  es.onmessage = (ev) => applyEvent(JSON.parse(ev.data));
  es.onerror = () => {}; // EventSource reconnects on its own
}

function applyEvent(event) {
  switch (event.type) {
    case "turn_started":
      if (!state.live.some((r) => r.kind === "assistant_text" && r.turn === event.turn)) {
        state.live.push({ kind: "assistant_text", turn: event.turn, text: "", open: true });
      }
      state.running = true;
      break;
    case "model_delta": {
      const row = state.live.find(
        (r) => r.kind === "assistant_text" && r.open && r.turn === event.turn
      );
      if (row) row.text += event.text;
      break;
    }
    case "tool_call": {
      if (!state.live.some((r) => r.kind === "tool_use" && r.call === event.call)) {
        state.live.push({
          kind: "tool_use",
          turn: event.turn,
          call: event.call,
          tool: event.tool,
          input: JSON.stringify(event.input),
          status: "running",
          bytes: 0,
        });
      }
      state.running = true;
      break;
    }
    case "tool_result": {
      const row = state.live.find((r) => r.kind === "tool_use" && r.call === event.call);
      if (row) {
        row.status = statusLabel(event.status);
        row.bytes = event.bytes;
      }
      break;
    }
    case "error":
      state.live.push({ kind: "error", code: event.code, fatal: event.fatal });
      break;
    case "turn_done":
      // The turn is persisted: history becomes authoritative, live rows drop.
      state.running = false;
      state.live = [];
      refreshHistory().then(renderSessions);
      break;
  }
  render();
  renderStatus();
}

function statusLabel(status) {
  if (typeof status === "string") return status;
  if (status && typeof status === "object") return "failed";
  return String(status);
}

function renderStatus() {
  els.status.textContent = state.running ? "Running" : "Idle";
  els.stop.disabled = !state.running;
  els.title.textContent = state.current ?? "No session selected";
}

function render() {
  els.transcript.replaceChildren();
  for (const row of state.base.concat(state.live)) {
    els.transcript.appendChild(renderRow(row));
  }
  els.transcript.scrollTop = els.transcript.scrollHeight;
}

function renderRow(row) {
  const div = document.createElement("div");
  if (row.kind === "user_text") {
    div.className = "row user";
    div.innerHTML = '<div class="label">You</div>';
    const bubble = document.createElement("div");
    bubble.className = "bubble";
    bubble.textContent = row.text;
    div.appendChild(bubble);
  } else if (row.kind === "assistant_text") {
    div.className = "row assistant";
    div.innerHTML = '<div class="label">Assistant</div>';
    const text = document.createElement("div");
    text.className = row.open ? "text cursor" : "text";
    text.textContent = row.text;
    div.appendChild(text);
  } else if (row.kind === "tool_use") {
    div.className = "row tool";
    const head = document.createElement("div");
    head.className = "head";
    const name = document.createElement("span");
    name.className = "name";
    name.textContent = row.tool;
    const status = document.createElement("span");
    status.className = `status ${row.status}`;
    status.textContent = row.bytes ? `${row.status} (${row.bytes} B)` : row.status;
    head.append(name, status);
    const input = document.createElement("div");
    input.className = "input";
    input.textContent =
      typeof row.input === "string" ? row.input : JSON.stringify(row.input);
    div.append(head, input);
  } else if (row.kind === "error") {
    div.className = "row error";
    div.textContent = `error: ${row.code}${row.fatal ? " (fatal)" : ""}`;
  }
  return div;
}

// ---------- composer ----------

els.input.addEventListener("keydown", (ev) => {
  if (ev.key === "Enter" && !ev.shiftKey) {
    ev.preventDefault();
    els.composer.requestSubmit();
  }
});

els.composer.addEventListener("submit", async (ev) => {
  ev.preventDefault();
  const text = els.input.value.trim();
  if (!text || !state.current) return;
  els.input.value = "";
  state.live.push({ kind: "user_text", text });
  render();
  await fetch(`/api/sessions/${encodeURIComponent(state.current)}/messages`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ text }),
  });
});

els.stop.addEventListener("click", () => {
  if (!state.current) return;
  fetch(`/api/sessions/${encodeURIComponent(state.current)}/cancel`, { method: "POST" });
});

loadModels();
loadSessions();
