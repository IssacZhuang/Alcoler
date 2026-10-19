# Alcoler Implementation Plan

- Status: Draft for review. Section 2 records owner-confirmed decisions (2026-10-19). Everything else is proposed and open to adjustment.
- Date: 2026-10-20
- Reference: `C:\Projects\ZCode` (behavioral reference only; no code reuse, no codebase inheritance)
- Prior context: `Docs/harness-tech-stack-discussion-2026-10-18.md` (archived in git history, commit `b44e8b1`)

## 1. Product Definition

Alcoler is a from-scratch coding agent harness for daily production use, built kernel-first:

- The kernel (agent loop, tools, providers, context management, session persistence) is an independent class library with no UI dependency.
- Kernel behavior replicates the ZCode mechanisms specified in Section 5, with the same constants and failure-recovery semantics.
- The workbench UI replicates the ZCode desktop interaction in Avalonia, at full workbench scope (Section 6).
- Browser Use is provided through an independent Playwright-driven browser window (Section 7).

Primary target platform is Windows. Avalonia keeps macOS/Linux technically reachable, but they are not validated or gated in any phase (Open Item 7).

### Non-goals (initial)

- Web-remote access and web UI parity (single desktop process only).
- TUI (the Section 3.4 protocol shell would make it possible; not planned).
- i18n (Open Item 4).
- Plugin marketplace.

## 2. Confirmed Decisions

| # | Topic | Decision | Consequence |
| - | ----- | -------- | ----------- |
| D1 | Browser Use form | Independent Playwright window (headed), persistent login profile, in-app pane shows status/screenshots/controls. No embedded webview. | Avoids WebView2/CDP custom layer; Windows-only constraint avoided; takeover = bringing the headed window to front. |
| D2 | Kernel boundary | No communication protocol. Kernel is a class library (agent loop / tools / providers). UI references the kernel project directly and calls it in-process. A protocol shell can wrap the kernel library later if web-ui/tui is ever needed. | Single process, no IPC from day 1. No crash isolation: mitigated by boundary exception normalization, immediate persistence, and resume-on-restart (Section 3.1). |
| D3 | UI scope | Full workbench replication (destination). Phased delivery; every phase ends in a usable product. | Long-running effort on the UI side (Section 6, Section 8). |
| D4 | Tech stack | .NET 10 / C#, Avalonia 11, Microsoft.Data.Sqlite, System.Text.Json, Microsoft.Playwright. | Replaces the abandoned Rust+web experiment (removed in commit `bd5947b`). |

## 3. Architecture

### 3.1 Process model

Single process:

```
Alcoler.Workbench.exe (Avalonia)
 ├─ Workbench UI (projection store, panels, dialogs)
 ├─ Kernel host (in-process): Alcoler.Kernel + Alcoler.Adapters
 │    ├─ Agent runtime (turn machine, command queue, compaction)
 │    ├─ Session store (SQLite + artifact files)
 │    └─ Playwright browser manager (Alcoler.Browser, P4+)
 └─ Workspace services (PTY terminal, git, file dialogs)
```

Accepted tradeoff (D2): a kernel fault can take down the UI process. Mitigations, all mandatory from P0:

1. Every kernel public entry point is async and normalizes exceptions into typed turn/error results at the boundary; no kernel exception escapes into UI event handlers.
2. Every committed message, tool result, and interaction is persisted to SQLite before the next model step, so restart + resume loses at most the in-flight model stream.
3. Session residency and event retention (Section 5.8) bound memory in long multi-session use.

### 3.2 Kernel public API shape (the "one layer of abstraction")

The in-process API adopts the ZCode v4 topic semantics (snapshot + delta projection, CAS commands, reverse interaction requests) as interface contracts instead of wire protocol. This is what keeps the future protocol shell mechanical and lets the UI projection logic be written once.

```csharp
public interface IAgentRuntime
{
    ValueTask<SessionHandle> CreateSessionAsync(SessionOptions options, CancellationToken ct);
    ValueTask<CommandAck> SubmitAsync(SessionId session, ConversationCommand command, CancellationToken ct);
    ValueTask<StopAck> StopAsync(SessionId session, StopCommand command, CancellationToken ct);
    IConversationProjection Subscribe(SessionId session);   // snapshot + seq-ordered deltas
    IConversationProjection? TryObserve(SessionId session); // subagent topics, no subscription side effects
}

public interface IConversationProjection
{
    ConversationSnapshot Snapshot();                        // rows + state, at seq N
    IAsyncEnumerable<ConversationDelta> Deltas(CancellationToken ct); // seq-contiguous; gap => resnapshot
}

// Commands carry commandId + baseRevision (compare-and-swap against projection revision).
// UI implements these; kernel calls them for permission requests and AskUserQuestion:
public interface IPermissionBroker { ValueTask<PermissionDecision> DecideAsync(PermissionRequest r, CancellationToken ct); }
public interface IUserInteractionPort { ValueTask<InteractionAnswer> AskAsync(InteractionRequest r, CancellationToken ct); }
```

Commands to support at P2 (minimal set): `CreateSession, SendText (with mode + model selection + queueing), Stop, Compact, ResolveInteraction, SwitchModel, RenameSession, DeleteSession`. Extended in later phases (rewind/fork, queue editing, feedback) — full list in Section 6.2.

### 3.3 Future protocol shell (out of scope)

If web-ui/tui is ever built: a thin host process exposes `IAgentRuntime` over stdio NDJSON (JSON-RPC-shaped, bidirectional for `IPermissionBroker`/`IUserInteractionPort`). Nothing in the kernel or projection store changes.

## 4. Solution Structure

```
Alcoler.sln
 src/
  Alcoler.Contracts/    # Ports + DTOs: turn/session/message/part/event/command types,
                        #   tool contract (JSON Schema based), permission model, projection types.
  Alcoler.Kernel/       # Turn machine, command queue, compaction, context assembly, permission
                        #   service, subagent runtime, event store, residency. References Contracts only.
  Alcoler.Adapters/     # Model providers (anthropic / openai-compatible / scripted mock), SQLite
                        #   storage, artifact store, built-in tool implementations (fs, bash, search,
                        #   web). References Contracts + Kernel.
  Alcoler.Browser/      # Playwright adapter behind BrowserControlPort (P4+).
  Alcoler.Workbench/    # Avalonia app: projection store, workspace shell, panels, settings.
 tests/
  Alcoler.Kernel.Tests/ # Deterministic turn/recovery/compaction tests via scripted provider.
  Alcoler.Workbench.Tests/ # Projection store tests (headless).
```

Conventions: `net10.0`, `<Nullable>enable</Nullable>`, `LangVersion` latest, xUnit, cancellation tokens on every async kernel API, `System.Threading.Channels` for all producer/consumer pipelines. No other frameworks unless a phase needs them.

## 5. Kernel Specification (ported ZCode behavior)

All constants below replicate ZCode values and become named constants in `Alcoler.Kernel` (names in parentheses). ZCode reference paths in Appendix A.

### 5.1 Turn state machine

- States: `Idle, ProcessingInput, AwaitingModelResponse, Streaming, SchedulingTools, ExecutingTools, AggregatingResults, AwaitingPermission, Completing, Error`. Explicit immutable transition table; illegal transition is a bug (throw).
- Loop per model step: drain pending commands (steering, task notifications) → micro-compact check → auto-compact check → build tool list (turn-scoped disallowlist) + inject reminders → assemble provider request (Section 5.6) → model step → schedule/execute tools → aggregate → next round.
- Stop conditions: text-only finish (then drain queued guide input; Stop-hooks may force continuation), forced stops (`plan_exit_denied`, `automation_create_limit`, `subagent_terminal`).
- No hard max-iteration cap in the core loop. Guardrails instead: repeated-identical-tool-call detection (threshold 3), stream-recovery budget (10), output-token continuation limit, rapid-refill compaction breaker.

### 5.2 Command queue and admission

- Single FIFO with priority `next` (prompt admission reserves turn start synchronously; no double turns).
- Steering guides and background-task notifications inject as user-role entries between model steps, never mid-step.
- On turn cancel, queued inputs are preserved; on resume, persisted pending inputs are discarded (never auto-replayed).
- Command envelope: `commandId` (UUIDv7) + `baseRevision`; stale revision is rejected with current state, letting the UI rebase.

### 5.3 Model layer

- `IModel` per turn, frozen at turn start (config changes affect future turns only). Provider kinds: `anthropic` (Messages API, `/v1` normalization, `x-api-key` and Bearer), `openai-compatible` (chat completions), `scripted` (test).
- Stream event union: `Start, TextStart/TextDelta/TextEnd, ReasoningStart/ReasoningDelta/ReasoningEnd (carries provider metadata, e.g. thinking signatures), ToolInputStart/ToolInputDelta/ToolInputEnd, ToolCall, Finish (usage), Error`.
- Partial tool-call assembler: normalize names, gate final `ToolCall` on `ToolInputEnd`, dedupe by id, merge `providerExecuted`.
- SSE parsing in-house over `HttpClient` streaming; mid-stream business-error frames parsed into typed errors with retry-after preservation.
- Adapter retry policy: exponential backoff, `MaxAttempts=11`, `BaseDelay=2s`, x2, `CapDelay=60s`, jitter. **Boundary rule: stream events are buffered until a committed boundary; failures before any visible content are silently retried; after visible output has been emitted, never replay — hand off to kernel recovery.** Zero-output completion retried; reasoning-signature repair retried once; per-attempt stream idle timeout +30s per retry.
- Kernel streaming recovery: retry from the latest committed tool-result anchor, max `StreamRecoveryMaxRetries=10`; unsettled tool calls get synthetic results (`unknown_execution_state` / `not_executed`) so every `tool_use` stays paired; context-exceeded triggers one reactive compact per step then retry.
- Cancellation: linked `CancellationTokenSource` per turn, linked again per tool call and per model request; aborted calls produce paired `ToolCancelled` results; abort mid-stream persists the partial output snapshot.

### 5.4 Tool system

- Tool contract fields (per tool declaration): `name, description, inputSchema (JSON Schema), readOnly, destructive, concurrentSafe, requiresUserInteraction, sideEffectScope (none|workspace|git|network|system|session|userInteraction), riskLevel, needsApproval, timeout policy, resultBudget (inline|truncate|artifact + preview limits), allowedInPlanMode, providerVisible`.
- Pipeline: JSON Schema validation → semantic `ValidateInput` → `ResolveInput` (normalize before permission so approval and execution see identical bytes) → `PrepareApproval` (may only narrow an ask) → permission check → execute under linked cancellation + pausable deadline → output validation → result serialization under budget.
- Scheduler: dependency-topological grouping; within-group parallel; `MaxConcurrency=10`. Destructive tools never parallel. A result flagged `stopTurnAfterResult` cancels remaining groups.
- During-stream execution (model streaming still open) only when: `readOnly && concurrentSafe && !destructive && !needsApproval && !requiresUserInteraction && sideEffectScope == none`.
- Oversized outputs: truncate with explicit marker, or offload to artifact store with preview (`Read` cap 25k tokens, partial read target 85%).
- Unknown tool name: `ToolNotFound` result with provider-visible error content (keeps pairing).

### 5.5 Permission service

Precedence chain (first match wins): plan-mode transition policy → `requiresUserInteraction` ask → always-ask gate → `yolo` bypass (disabled in plan mode) → config disallowed → project deny rules → project ask rules → plan mode read-only rules → project allow rules → mode defaults (`build`: read-only allow, risky ask; `edit`: + workspace edit tools; `plan`: read-only only). Rules match by tool name and content subjects (`command, url, file_path, path, pattern, patch_text`); `Write` rules also match `Edit`. Session-granted permissions live in memory; project rules persist.

UI interaction flows through `IPermissionBroker` (default implementation: deny-all, so headless tests never hang).

### 5.6 Context management

**Token accounting**: char heuristic (CJK chars count double, total / 3) as estimate; replaced by provider-usage anchor when available: `contextUsageTokens ?? inputTokens` of the latest committed assistant + locally estimated tail since then.

**Assembly per model step** (ContextBuilder):
- Exactly three system messages (CLI prefix / stable / dynamic), each carrying an ephemeral cache breakpoint; one more breakpoint on the last non-system message. All other per-message cache flags cleared before this.
- AGENTS.md and skills listing are meta-user attachments in an immutable prefix; prefix is rebuilt only on config change (model/style/language), never per step.
- Runtime metadata stripped in projection; system reminders become user-role `<system-reminder>` wrappers.

**Compaction policy** (`CompactPolicy` constants):

| Constant | Value |
| --- | --- |
| `DefaultContextWindowTokens` | 200,000 |
| `AutoCompactOutputReserveTokens` | 21,000 (requested reserve 32k, capped) |
| `AutoCompactBufferTokens` | 13,000 |
| Effective auto-compact threshold | window − reserve − buffer = 166,000 |
| `MaxConsecutiveAutoCompactFailures` | 3 (circuit breaker) |
| `RapidRefillToolTurnThreshold` / `MaxConsecutiveRapidRefills` | 3 / 3 (anti compaction-loop breaker) |
| `MicroCompactIdleMinutes` | 60 |
| `MicroCompactTriggerTokens` | min(0.9 × threshold, threshold − 2,000) |
| `MicroCompactKeepRecentGroups` | 5 |
| `MicroCompactMinSavingsTokens` | 256 |
| `SummaryMaxOutputTokens` | 20,000 |
| `CompactToolKeepMaxCount` | 100 (summary request sent with zero tools above this) |
| `AutoCompactMaxAttempts` / prompt-too-long retries | 3 / 3 |

- Micro-compact (runs before auto-compact each step): clear content of old tool results for read/search-type tools only, keep last 5 groups, never clear media or errors, only if savings ≥ 256 tokens.
- Auto-compact: structured 9-section summary (primary intent, concepts, files+snippets, errors+fixes, all user messages verbatim, pending tasks, current work, next step); the last assistant-led round is preserved verbatim; summary request bypasses tools when >100 registered.
- Reactive compact: on provider context-overflow error, parse the token gap, compact once, retry.
- Prompt-too-long during summarization: parse gap, reselect with more preserved rounds / drop oldest behind a truncation marker, max 3 retries.
- Compaction boundary persisted as a `compact_summary` user message + boundary payload (pre/post token counts, `lastSummarizedMessageId`, preserved segment anchors). Resume slices from the last boundary.
- Post-compact re-injection: up to 5 recently read files return as attachments (≤5,000 tokens each, ≤50,000 total); oversized become "use Read tool" pointers.
- Media in tool results always offloaded to the artifact store (`alcoler-artifact://`, session retention), DB stores references; hydration on resume.

### 5.7 Session persistence

- Single SQLite database (`~/.alcoler/db/db.sqlite`, WAL mode). Tables: `session, message, part, todo, session_entry, permission, input_history, model_usage, turn_usage, tool_usage, session_input`. Messages/parts stored as JSON with a monotonic `sequence` column.
- In-memory event store is the sequence authority for live + replay; only selected event types are durably side-written (checkpoints, rewind ledgers, input ledger).
- Sessions are created lazily on first input; title from first input.
- Resume hydration: dedupe parts by id, honor branch (rewind/fork) message-id sets, replace interrupted tools with placeholders, rehydrate media from artifacts, restore mode only from authoritative events, drop persisted pending inputs.
- Session index: SQL listing by workspace/directory, ordered by `time_updated desc`.

### 5.8 Resource governance

- Event retention ("turn window"): transient events (streaming deltas, progress, network status) evicted one turn after `TurnComplete/TurnError`, 120s grace for sealed turns without successor; sequence counter stays monotonic.
- Session residency pool: target 8 active runtimes, high-water 16, idle timeout 10 min, LRU deactivation; eligibility gates (no pending interactions, queued commands, subscribers, active background work).
- Streaming write backpressure: bounded channel, capacity 128, await-on-full.
- High-frequency events aggregated per turn/type, flushed every 100.

### 5.9 Subagents

- Same-process logical child runtimes: own session id, own turn loop, borrowed MCP connections (never owned), filtered skills, tool allowlist = parent visible tools minus dispatch tools plus `RespondToCoordinator`; cannot spawn their own children; `maxTurns` default 4.
- Explore-type defaults to `yolo` + read-only toolset; otherwise inherit parent mode.
- Child events: summarized tool activity mirrored into the parent timeline (raw child transcript never enters parent history); child blocking interactions route to the parent session's `IPermissionBroker`.
- Foreground/background: auto-background timer; background completion injects a task notification into the parent command queue, drained between model steps.

## 6. Workbench UI Specification

### 6.1 Layout and shell

Replicates the ZCode workspace shell: left workspace sidebar (session/task list, grouped by workspace), center conversation frame (workspace header + timeline + composer), bottom terminal frame (own tab bar), right Side Pane (own tab bar), 4px resizable gaps between frames. Light/dark themes; the ZCode design token system (semantic colors, `text-ui-*` scale, radius hierarchy, density) is ported to Avalonia theme resources as the styling baseline.

### 6.2 Component inventory and phase assignment

| Area | Components | Phase |
| --- | --- | --- |
| Conversation | Timeline (virtualized), turn rows/groups, tool-call blocks, queue panel, status panel, turn navigator | P2 core, P4 polish |
| Tool blocks | Read, Write/Edit (inline diff), Bash, Glob/Grep, Todo, AskUserQuestion first; remaining matrix (web, MCP, subagent, browser, cron) as subsystems land | P2 / P3 / P4 |
| Composer | Text input, mode switch (build/edit/plan/yolo), model selector, thought level, context usage indicator, attachments, queueing | P2 (modes), P4 (full) |
| Interactions | Permission dialog, AskUserQuestion dialog, snooze/auto-resolution | P2 |
| Sessions | Sidebar list, tabs, unread counts, rename/delete, resume on restart | P2 |
| Plan | Plan pane, ExitPlanMode confirmation treatment | P2 (basic), P3 (full) |
| Terminal | PTY terminal pane (Pty.Net + custom VT rendering), background bash output pane | P4 |
| Git | Git pane, branch switcher, git graph | P4 |
| Settings | Providers/models, MCP, skills, subagents, keybindings, usage stats, appearance | P4 |
| Browser | Browser pane: session status, screenshots, control handoff, takeover button | P4 |
| Debug | Model I/O trajectory pane | P4 (optional) |
| Deferred / pending scope decision | Whiteboard, treemapping, dynamic-workflow timeline, resource manager, quickpick/command palette | Open Item 1 |

### 6.3 Projection store (UI-side data layer)

- Snapshot wholesale-replace on subscribe; deltas applied only when seq-contiguous; gap triggers resubscribe and server-side snapshot/resume arbitration.
- Optimistic overlay for pending commands (send → optimistic row/state → CAS ack or rejection rebase).
- Ref-counted per-session stores, keep-warm ~30s after last unsubscribe.
- Streaming deltas coalesced on a ~30ms timer before rendering.

### 6.4 Rendering stack

- Markdown: Markdown.Avalonia base + custom extensions; code highlighting via TextMateSharp. Parity target: headings, lists, tables, fenced code with highlight, inline code, links. KaTeX/Mermaid deferred.
- Code/diff: custom diff control (inline side-by-side), TextMateSharp highlighting, independent font-size settings.
- Virtualization: `ItemsRepeater`-based timeline; tool output blocks lazily materialized.
- Terminal: Pty.Net (ConPTY on Windows) + custom VT renderer (largest UI-side risk; fallback plan in Section 10).

## 7. Browser Use

- `BrowserControlPort` in Contracts (execute, cancel, session lifecycle); implementation in `Alcoler.Browser` on Microsoft.Playwright.
- Independent headed Chromium/Edge window (`channel` configurable); persistent user-data directory for login state; per-session browser contexts.
- In-app pane shows page status, screenshots (CDP/Playwright screenshots), and a takeover action (bring window to front; automation pauses control on user input where feasible).
- Lifecycle rules: one shared browser process per runtime; contexts per agent session; page count cap; idle context/page reclaim; browser process exits when no sessions need it.
- No second agent state machine in the browser layer: navigation/act results return as tool results; decisions/approvals stay in the kernel.

## 8. Roadmap

| Phase | Scope | Exit criteria |
| --- | --- | --- |
| P0 Kernel skeleton | Contracts; turn machine; command queue; SQLite stores; event store; scripted/anthropic/openai-compatible providers; tools (read/write/edit/bash/glob/grep); permission service + deny-all broker; memory spike | Deterministic xUnit: a full turn with tool call, permission ask, result pairing, persistence, and resume — via scripted provider. Memory spike report (Open Item 6 baseline). |
| P1 Stability | Micro/auto/manual/reactive compaction; boundary persistence; resume hydration; streaming recovery anchors; adapter retry boundary rules; governance (retention, residency, backpressure) | Replay tests: session crossing 166k threshold compacts with boundary and resumes; injected stream failures before/after visible output follow the boundary rule; context-exceeded triggers reactive compact + retry; long-session memory stays bounded. |
| P2 Workbench skeleton | Shell layout; timeline + projection store; composer (modes, model selector); core tool blocks; permission/ask dialogs; session sidebar/tabs; basic plan; restart resume | Daily-driver capable end to end with a real provider on a real workspace. |
| P3 Kernel completeness | Subagents; MCP client; skills listing; todo; full plan mode | Subagent flows (foreground/background) with mirrored timeline; MCP tools usable; plan round-trip. |
| P4 Workbench completeness | Terminal pane; git pane; settings center; browser pane (Playwright); remaining tool blocks; markdown/diff polish; usage stats | Terminal and git panes daily-usable; browser pane drives Playwright end to end (navigate, screenshot, takeover). |
| P5 Optional shell | stdio NDJSON host exposing `IAgentRuntime` | Out of scope until web-ui/tui is requested. |

## 9. Testing Strategy

- Scripted provider: deterministic `ModelStreamEvent` scripts including failure injection (mid-stream errors, context-overflow, idle timeouts). All P0/P1 exit criteria are automated tests on this provider.
- Golden replay: recorded real sessions (SQLite) replayed through the kernel asserting context assembly and compaction decisions.
- Permission: precedence-chain table tests; deny-all broker in headless runs.
- Projection store: seq/gap/resubscribe/CAS tests headless.
- UI: manual daily-driver from P2; automated coverage limited to data layer (no brittle UI automation).

## 10. Risks and Mitigations

| Risk | Assessment | Mitigation |
| --- | --- | --- |
| VT terminal rendering | Highest UI risk; no xterm.js equivalent in Avalonia | Timebox in P4; fallback: output-only console pane first (background bash), full VT as incremental milestone; embed/adopt a renderer only if proven |
| Markdown parity vs streamdown+shiki | Medium; achievable for code-centric content | Markdown.Avalonia + TextMateSharp baseline; custom renderer only where the baseline blocks |
| No crash isolation (single process) | Accepted (D2) | Boundary exception normalization; persist-before-step; resume on restart; optionally isolate subagents later |
| Full-workbench effort | Longest-running risk (UI side is a product-scale build) | Phase gates; each phase ends usable; scope discipline via Open Item 1 |
| .NET memory assumptions | Unmeasured | P0 memory spike; set explicit budget (Open Item 6); workstation GC, bounded channels, residency pool |
| Playwright browser distribution | Browser download at first use / bundled | Decide in P4 (download-on-demand first) |
| SQLite single-writer | Kernel is the only writer; UI reads via projection | Single writer discipline; WAL; no UI direct DB access |

## 11. Open Items (owner to adjust)

1. **Full-workbench boundary**: are whiteboard, treemapping, dynamic-workflow timeline, resource manager, and command palette inside "full workbench replication" or deferred? (Proposal: defer all five; revisit after P4.)
2. **Terminal scope**: full VT emulation or interactive-shell subset for P4? (Proposal: start output-only + input forwarding, full VT as stretch.)
3. **Provider order in P0**: anthropic-compatible and openai-compatible in parallel, or one first? (Proposal: openai-compatible first — broadest coverage — then anthropic.)
4. **UI language**: English-only initially, i18n when? (Proposal: English-only through P4.)
5. **Distribution**: self-contained single-file vs framework-dependent vs installer? (Proposal: self-contained, decide packaging at P2.)
6. **Memory budget number**: acceptance threshold for the P0 spike (e.g., steady state < 200 MB with one active session)? Proposal needed from owner.
7. **Cross-platform posture**: Windows-only validated through P4, macOS/Linux best-effort later? (Proposal: yes.)

## Appendix A: ZCode Reference Map

| Mechanism | ZCode location (apps/zcode-cli/packages unless noted) |
| --- | --- |
| Turn loop | `core/src/runtime/methods/turn-loop.ts:43`, `turn-model-step.ts:88`, `turn-stop.ts:157` |
| Turn state machine | `core/src/agent/turn-state.ts:25`, `turn-machine.ts:68` |
| Compaction policy/constants | `core/src/compact/policy.ts`, `compact/prompt.ts`, `runtime/methods/compact.ts:183`, `compact-active.ts:75` |
| Micro-compact | `core/src/compact/microcompact.ts`, `runtime/methods/microcompact.ts:24` |
| Token estimation | `core/src/context/utils.ts:12`, `compact/manual.ts:102`, `runtime/methods/compact.ts:312` |
| Context builder / cache breakpoints | `core/src/context/builder.ts:38`, `runtime/helpers/provider-request-messages.ts:48` |
| Tool contract / scheduler / budgets | `core/src/tool/types.ts:67`, `tool/scheduler.ts:50`, `contracts/src/tools/contract.ts:117` |
| Permission service | `core/src/permission/service.ts:93`, `tool/executor/permission-flow.ts:40` |
| Subagent runtime | `core/src/runtime/methods/subagent.ts:194`, `core/src/subagent/runner.ts:131` |
| Streaming retry boundary | `adapters/src/model/runner-stream.ts:1455`, `stream-retry-boundary.ts:6` |
| Streaming recovery | `core/src/runtime/methods/streaming-recovery.ts:14`, `streaming-tool-coordinator.ts:148` |
| Session store (SQLite) | `adapters/src/storage/session-store/` (migrations.ts, repositories/) |
| Resume hydration | `core/src/agent/session-history-hydrator.ts:54`, `runtime/methods/resume.ts:59` |
| Event retention / residency | `contracts/src/events/session-event-retention.ts:55`, `bootstrap/src/zcode-protocol/session-resident-pool.ts` |
| Model interface / adapters | `contracts/src/model/model.ts:44`, `adapters/src/model/model-execution.ts:156` |
| V4 topic protocol (API semantics source) | root `packages/shared/src/zcode-protocol-v4/` (delta.ts, command.ts, transport.ts) |
| Workspace shell layout | root `packages/ui/src/app-shell/WorkspaceShellLayout.tsx`, root `DESIGN.md` |
