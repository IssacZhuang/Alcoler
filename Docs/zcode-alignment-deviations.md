# ZCode Alignment: Remaining Deviations

- Status: Complete for phases 1-4 (tools, context management, runtime recovery, turn-loop semantics). This file records every remaining behavioral deviation from the ZCode reference that survived acceptance review.
- Date: 2026-10-20
- Reference: `C:\Projects\ZCode` (behavioral reference only)
- Baseline at time of writing: kernel tests 990/990, workbench tests 471/471, solution builds clean.

Everything not listed here was verified verbatim-equivalent during acceptance (tool result texts, guard messages, matcher strategies, exit-code semantics, envelopes, limits, retry/classifier semantics, thinking replay, media pipeline, reminder/continuation prompts, steering and queue semantics, notification channel, plan-mode cadence, and anomaly warnings).

## 1. High impact

### 1.1 Prompt-too-long reselect direction

- Alcoler: `src/Alcoler.Kernel/Context/CompactionSelector.cs` (`ReselectForPromptTooLong`) drops the **oldest** groups from `ToSummarize` and keeps summarizing newer ones.
- ZCode: `apps/zcode-cli/packages/core/src/runtime/helpers/compact-selection.ts` moves **recent** groups out of the summarization set and preserves them verbatim, driven by the declared token gap.
- Effect: after repeated overflow retries, Alcoler permanently loses the oldest context while ZCode loses mid-conversation detail but keeps the most recent exchanges verbatim. Different memory retention under compaction pressure.
- Risk of change: high; interacts with the compaction retry loop and persisted boundaries. Needs a dedicated design pass with new tests.

### 1.2 Thinking-signature repair scope

- Alcoler: `src/Alcoler.Kernel/Runtime/ThinkingSignatureRecovery.cs` + `SessionRuntime.StripReasoningHistoryAsync`. On a narrow signature-rejection 400 match, strips **all** reasoning parts from **all** assistant messages, for any provider, and **rewrites persisted history**.
- ZCode: `packages/adapters/src/model/reasoning-history-normalization.ts`. Strips only signed/redacted reasoning blocks, only for the Anthropic provider kind, only on the request copy, with one extra attempt outside the retry budget.
- Structural passes are aligned: `src/Alcoler.Adapters/Models/ReasoningHistoryNormalizer.cs` removes reasoning-only assistants, trailing reasoning, and whitespace-only assistants (merging adjacent user messages), repairs empty assistants with `"(no content)"`, and downgrades images for models without image support — all on the per-request copy.
- Still missing: signed-only stripping (`ReasoningPart` does not expose signature/redaction info, so the distinction is not expressible), cross-model reasoning removal (no per-part model origin is persisted; recovery stays reactive via signature-rejection), and `redacted_thinking` capture in the Anthropic stream state.
- Effect: Alcoler loses more reasoning context on repair than necessary and can hit avoidable 400s after model switches.

## 2. Medium impact

### 2.1 No image transcoder

- Alcoler: `src/Alcoler.Adapters/Tools/ReadTool.cs` accepts images up to 20 MB input / 5 MB base64 and rejects anything larger.
- ZCode: `read-image.ts` resizes and recompresses oversized images (max dimension 2000, ~3.75 MB base64 target) so they still fit.
- Effect: very large screenshots are rejected instead of downscaled; the user must resize externally. A managed-image resize (System.Drawing on Windows is not cross-platform) would need a new dependency or a hand-rolled codec path.

### 2.2 ReadSessionContext uses local lexical scoring

- Alcoler: `src/Alcoler.Kernel/Tools/ReadSessionContextTool.cs` scores messages locally (query tokens plus CJK bigrams).
- ZCode: `packages/core/src/session-context/read-session-context.ts` may wait on a lite/auxiliary model for extraction and builds chunk-based selection.
- Deliberate: keeps the tool deterministic and cheap. Handoff strategy and not-found semantics are aligned.

### 2.3 SendMessage scope and wrapper adaptations

- Alcoler: `src/Alcoler.Kernel/Subagents/SendMessageTool.cs`, scoped to parent-to-background-child only. Busy wrapper says "The parent session sent a message..." where ZCode's coordinator steer says "The coordinator sent a message..." (Alcoler's child prompts never define a "coordinator"). A resumed child receives the raw message as its next prompt, matching ZCode, but the resume result text drops ZCode's stopped-status and per-agent output-file pointer (Alcoler has no per-agent output file; completion is delivered via the background-job notification).
- Child-to-parent messaging is not supported (the Task tool returns the child's final output directly). Documented design decision.

## 3. Low impact / cosmetic

### 3.1 AskUserQuestion: no Preview feature

- Alcoler: `src/Alcoler.Kernel/Tools/AskUserQuestionTool.cs` omits the "Preview feature:" description block and the `preview` option schema field.
- Reason: the Avalonia workbench does not render previews; advertising them would produce dead output. All other schema strings and answer texts are verbatim.

### 3.2 Skill tool guidance and variable expansion

- Alcoler: `src/Alcoler.Kernel/Tools/SkillTool.cs` drops the plugin-namespaced-skill form (`plugin:skill`) and the built-in-CLI-command guidance (neither concept exists in Alcoler), and rephrases the "already loaded" bullet.
- No `${CLAUDE_SKILL_DIR}`/`${ZCODE_SKILL_DIR}`-style base-directory variable expansion in skill bodies. Skill discovery scans `.alcoler/skills` with workspace-level shadowing of user-level skills.
- The skills listing section is rendered once per session creation; ZCode re-renders it per prompt build, so mid-session skill-file changes are not picked up until the next session.

### 3.3 Bash tool details

- No `BASH_DEFAULT_TIMEOUT_MS` / `BASH_MAX_TIMEOUT_MS` env overrides; effective timeout clamps at 1ms lower bound where ZCode has none.
- No user-manual-background text ("Command was manually backgrounded by user with ID: ..."); no tail-direction 30KB inline preview; no `killProcessOnPersistedLimit` machinery.
- Persisted-output byte formatting is 1024-based with one decimal ("43.9KB") vs ZCode's 1000-based ("45 KB").
- Alcoler adds a second 60,000-char tool-result budget with a `[Tool output truncated by resultBudget]` marker that ZCode does not have.

### 3.4 WebFetch fallback path

- No preapproved-URL markdown direct-return path. When no aux model is configured, Alcoler returns a 5,000-char raw preview with an explanatory note where ZCode throws "Model is not configured for WebFetch prompt processing". The aux prompt itself is now the ZCode single-user-message format, verbatim.

### 3.5 Token estimator CJK weighting

- `src/Alcoler.Kernel/Context/TokenEstimator.cs` double-weights CJK Extension-A and fullwidth forms; ZCode weights U+4E00-9FFF only. Estimates for CJK-heavy content run slightly high, occasionally triggering read partial-view or persisted-output paths earlier than ZCode would.

### 3.6 Compact summarization prompt structure

- `src/Alcoler.Kernel/Context/SummarizationPrompt.cs` covers the same nine topics but uses its own markdown-sections format with mandatory verbatim quoting of user messages; ZCode uses the classic `<analysis>`/`<summary>` tag discipline. The continuation header, truncation marker, and "Recent messages are preserved verbatim." suffix are aligned.
- ZCode's `suppressFollowup` resume instruction (reactive compaction: "Continue the conversation from where it left off without asking the user any further questions...") is not injected; Alcoler's turn loop just re-invokes the model after compaction.

### 3.7 Background-job notification details

- `src/Alcoler.Kernel/Tools/BackgroundJobNotifications.cs` has no `<tool-use-id>` field, appends ": {error}" for failures without an exit code where ZCode stays bare, and has no "lost" state. Prefix, XML shape, and summary wording ("completed (exit code N)", "failed with exit code N", "was stopped") are aligned.

### 3.8 Tool-name adaptations (intentional)

- "Wasted call — file unchanged since your last **ReadFile**" (ZCode: Read); background-start texts reference ReadFile. Kept "no need to Read it back" verbatim in edit/write freshness suffixes. Behaviorally neutral renaming.
- The "Task" alias for the Agent tool is not registered; only "Agent" resolves.

### 3.9 Media replay placement

- Alcoler: hydrated tool-result images are projected into a followup user message for every provider (`ContextBuilder.Project`), so image content always arrives as its own user turn.
- ZCode: Anthropic requests carry images inside the `tool_result` content array; other providers get the followup-message shape.
- Effect: Anthropic sees the same bytes in a slightly different structural position. No behavioral difference in practice.

### 3.10 OpenAI Responses reasoning replay

- Alcoler drops reasoning history for OpenAI Responses requests (`ReasoningHistoryNormalizer` with `keepReasoning: false`); ZCode replays encrypted reasoning content (`encrypted_content`) for Responses-capable models. ZCO1-style reasoning continuity across steps is lost; text and tool-call continuity are unaffected.

### 3.11 Runtime reminders are persisted

- Todo and plan-mode reminders are committed as provider-visible synthetic user messages, where ZCode injects them into the request copy only (its counters still derive from persisted history).
- Consequence: reminder text stays in Alcoler's persisted context (and UI transcript) rather than vanishing after the request. This is forced by Alcoler's history-mirrors-store model and is strictly more durable, but consumes slightly more context tokens than ZCode.

### 3.12 Stream-recovery observability

- Stream recovery (discard partial, fresh retry, max 10/turn, growing idle timeout) matches ZCode, but no observability events are emitted for recoveries and discarded partials are not persisted; ZCode surfaces these through its telemetry pipeline.

### 3.13 Grep regex dialect note

- Alcoler's Grep uses .NET regex; ZCode uses ripgrep. Match semantics agree for common patterns, and the tool description states the .NET dialect explicitly rather than quoting ZCode's ripgrep wording.
