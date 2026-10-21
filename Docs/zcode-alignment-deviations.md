# ZCode Alignment: Status and Remaining Deviations

- Status: The identified output-affecting alignment changes are implemented. The remaining differences below are architectural adaptations, product gaps, or verification limits, not a claim of identical engineering performance.
- Reviewed: 2026-10-21
- Reference: `C:\Projects\ZCode`, the modified ZCode implementation, treated as read-only.
- Scope: Runtime loop, tools, prompts, context management, provider replay and recovery, subagents, and interaction behavior.

The modified reference implementation takes precedence over earlier descriptions of its behavior. Passing scripted tests does not establish byte-for-byte equivalence, equal real-model task success rates, or correctness on untested platforms.

## 1. Implemented Alignment

The following items were previously listed as missing or divergent and must no longer be treated as open gaps.

### 1.1 Runtime loop and streaming recovery

- Normal input uses Queue delivery; explicit Guide input can join the active turn. Pending input does not silently change an executing model step. Guide text remains raw in canonical history, with its provider presentation tracked separately.
- Output continuation prompts are request-local, bounded, and do not consume queued input between truncated steps.
- Eligible safe, read-only tools can start during streaming. Tool-call IDs are deduplicated, and completed early executions are reused rather than run again.
- Recovery preserves accepted tool calls and matching results as an anchor, discards uncommitted partial output, and emits recovery events. Text-only partial failures retry without adding partial history.
- Handler cancellation and deadlines do not wait indefinitely for an uncooperative handler. Result-derived stop behavior prevents later tool groups from executing.
- Background notifications do not release user input held after a failed or cancelled turn. Canonical tool declarations preserve existing early-execution status and output.

Sources: `src/Alcoler.Kernel/Runtime/SessionRuntime.cs`, `src/Alcoler.Kernel/Runtime/StreamingToolCoordinator.cs`, `src/Alcoler.Kernel/Runtime/TurnRequestEntries.cs`, `src/Alcoler.Kernel/Runtime/ProjectionBuilder.cs`, `src/Alcoler.Kernel/Tools/ToolCallExecutor.cs`.

### 1.2 Context management and compaction

- Automatic/reactive prompt-too-long reselection moves recent groups into the verbatim preserved tail. Manual retries use a separate bounded oldest-group truncation policy.
- The summary prompt, resume instruction, token-estimation rules, and microcompaction eligibility follow the modified reference.
- Summary requests retain structured history and media. Eligible stream failures before committed content can fall back to actual non-streaming generation, not another streaming request presented as a fallback.
- Media-size failure permits one request-local retry with image placeholders. Canonical and durable media are unchanged; repeated media failure does not multiply through generic automatic retries.
- Recent-read reminders are newest-first and retain the original Read offset and limit. Compaction persistence precedes in-memory replacement and recent-read clearing.
- Todo reminders are durable model-only messages, matching the reference. Mode/date/anomaly reminders remain in canonical in-memory history; continuation prompts remain request-local. The previous claim that all reference reminders were request-only was incorrect.

Sources: `src/Alcoler.Kernel/Context/CompactionSelector.cs`, `src/Alcoler.Kernel/Context/CompactionPlanner.cs`, `src/Alcoler.Kernel/Context/CompactSummaryModelRunner.cs`, `src/Alcoler.Kernel/Context/SummarizationPrompt.cs`, `src/Alcoler.Kernel/History/MessageHistory.cs`.

### 1.3 Provider replay and repair

- Signed, redacted, and encrypted reasoning metadata are represented and retained. Assistant provider/model origin is recorded for new messages.
- Anthropic signature repair is scoped to signed/redacted reasoning in a request copy, with one extra physical request per logical request scope. It does not delete durable reasoning history.
- OpenAI Responses encrypted reasoning is replayed. Anthropic tool-result images are nested in tool-result content where supported.
- Anthropic, OpenAI-compatible, and OpenAI Responses adapters implement actual non-streaming generation. Failure metadata distinguishes request setup from response-body failures and retains HTTP/context-overflow information needed by recovery.

Sources: `src/Alcoler.Contracts/Conversation/ConversationMessage.cs`, `src/Alcoler.Contracts/Models/IChatModel.cs`, `src/Alcoler.Contracts/Models/ModelStreamEvents.cs`, `src/Alcoler.Adapters/Models/ReasoningHistoryNormalizer.cs`, `src/Alcoler.Adapters/Models/AnthropicChatModel.cs`, `src/Alcoler.Adapters/Models/OpenAiCompatibleChatModel.cs`, `src/Alcoler.Adapters/Models/OpenAiResponsesChatModel.cs`.

### 1.4 Tools and skills

- Read resizes and recompresses images using SkiaSharp. Range-specific read state and freshness/digest guards distinguish usable reads from token-truncated views.
- Bash supports timeout environment overrides, launch-time background execution, eligible timeout-driven background execution, bounded capture, direct output persistence, a 5 GiB persisted-output cap, and a UTF-8-safe bounded tail after persistence failure.
- The POSIX/Git Bash path merges stdout and stderr at the shell boundary. Windows decoding uses UTF-8 first with legacy-code-page fallback. The non-POSIX cmd fallback does not guarantee the same merged-stream ordering.
- The extra generic 60,000-character runtime tool-result truncation was removed. Tools own their output limits and artifact envelopes.
- The reference's normal persisted Bash envelope uses binary byte units and a first-2,000-character preview. Its previous description as a decimal-unit mismatch was incorrect.
- WebFetch supports the preapproved markdown path and the reference's missing-auxiliary-model error behavior.
- ReadSessionContext supports auxiliary-model extraction, chunk selection, synthesis, and local fallback; it is no longer lexical-only.
- Skills reload dynamically, support directory-variable expansion and a 100,000-byte body limit, and retain compatible runtime input forms. Task resolves as an alias for Agent.

Sources: `src/Alcoler.Adapters/Tools/ReadTool.cs`, `src/Alcoler.Adapters/Tools/BashTool.cs`, `src/Alcoler.Adapters/Tools/BackgroundBashRunner.cs`, `src/Alcoler.Adapters/Tools/WebFetchTool.cs`, `src/Alcoler.Kernel/Tools/ReadSessionContextTool.cs`, `src/Alcoler.Kernel/Tools/SkillTool.cs`, `src/Alcoler.Kernel/Tools/ToolRegistry.cs`.

### 1.5 Questions, subagents, and notifications

- AskUserQuestion supports single-select previews, side-by-side presentation, and answer annotations in the Workbench.
- Child-to-parent coordinator messaging is available. Busy child messages use Guide delivery; resumed children receive raw input.
- Background notifications carry tool-use correlation and represent lost state and optional real exit codes without inventing an exit code for cancellation.
- Every subagent keeps a per-agent artifact directory (`<SubagentOutputRoot>/<parentSessionId>/<agentId>/`, `~/.alcoler/exec` in the Workbench, the system temp directory by default) containing `output.txt` (final text / error / fixed stopped sentence), a byte-identical `task.output`, and `metadata.json` with the full lineage (agent, child session, parent session and tool call, agent type, description, prompt, status, timestamps, usage counts, resume markers). Background launches write running metadata before starting; terminal turns write completed/failed/stopped artifacts; SendMessage resume rewrites running metadata with `resumedAt`/`resumedFromToolCallId`.
- The output-file locator is surfaced on the reference's model-visible faces: the background launch result (`output_file:` plus the do-not-read guard, only when the parent's tool set contains Read or Bash, with the fallback end-your-response text otherwise), the background completion notification (`<output-file>`), and the SendMessage resume result (`Output: ...`). Foreground results intentionally carry no locator. Subagent started/changed events carry the output file path.
- TaskOutput and TaskStop address subagent runs (foreground and background) by agent id through a runtime task registry: `local_agent` projections prefer the in-memory result and fall back to an 8 MiB tail read of the output file with a 32,000-character truncation pointer, and stopping a running agent routes through the same cancel pipeline that writes stopped artifacts and the stopped notification. A successful terminal TaskOutput read marks the task notified, suppressing the duplicate background completion notification; SendMessage resume re-enables notification. A failed resume restores the previous terminal registry entry.

Sources: `src/Alcoler.Kernel/Tools/AskUserQuestionTool.cs`, `src/Alcoler.Workbench/ViewModel/PendingInteractionViewModel.cs`, `src/Alcoler.Kernel/Subagents/RespondToCoordinatorTool.cs`, `src/Alcoler.Kernel/Subagents/SubagentWiring.cs`, `src/Alcoler.Kernel/Subagents/SubagentArtifacts.cs`, `src/Alcoler.Kernel/Subagents/SubagentTaskRegistry.cs`, `src/Alcoler.Kernel/Tools/BackgroundJobNotifications.cs`, `src/Alcoler.Kernel/Tools/TaskOutputTool.cs`, `src/Alcoler.Kernel/Tools/TaskStopTool.cs`.

### 1.6 Grep search engine

- The Grep tool executes on the ripgrep 14.1.1 engine crates (`ignore` walker, `grep-searcher`, `grep-regex`) compiled into an in-process Rust library (`alco_grep`) and invoked via P/Invoke; no helper process is spawned. A managed .NET engine remains as a fallback when the native library is unavailable, mirroring the reference's JS fallback architecture. The tool contract, parameter schema, and output formats are unchanged.
- Native libraries are delivered for win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, and osx-arm64 (`src/Alcoler.Adapters/runtimes/<rid>/native/`), produced by the `native-binaries` GitHub workflow; a fresh checkout of any of these RIDs needs no Rust installation.
- File selection honors `.gitignore` / `.ignore` / `.rgignore` rules, searches hidden files, excludes version-control directories, skips binary files the way ripgrep's quit mode does, and sniffs BOMs (UTF-16) during search.
- Cancellation interrupts a running native search through a cancel flag checked during the walk; invalid patterns surface the same error text on both engines; lines longer than 500 characters are truncated identically by the shared formatter.
- The regex dialect is ripgrep's Rust regex (no look-around or backreferences), stated in the tool description.
- Bash sessions define an `rg` shell function over a bundled official ripgrep 14.1.1 binary when no system `rg` is on PATH. The official release publishes no aarch64-pc-windows-msvc build, so win-arm64 bundles no rg and its Bash sessions degrade to a system rg or none; packaging copies only the target platform's binary (`tools/ripgrep/<rid>/`).

Sources: `src/Alcoler.Adapters.Native/alco-grep/`, `src/Alcoler.Adapters/Grep/`, `src/Alcoler.Adapters/Tools/GrepTool.cs`, `src/Alcoler.Adapters/Tools/ManagedGrepEngine.cs`, `src/Alcoler.Adapters/Tools/AlcoGrepEngine.cs`, `src/Alcoler.Adapters/Tools/RipgrepBinary.cs`, `src/Alcoler.Adapters/Tools/BackgroundBashRunner.cs`.

### 1.7 Structured session sources for ReadSessionContext

- Every execution path (success/not_found/failed, source local/lite/fallback/none) produces a structured sources payload with the reference contract's field names: status, sessionId, title, directory, strategy, query, source, messageCount, selectedMessageCount, truncated, error, and per-message references (`messageId`, `index`, `role`; `partId`/`reason` reserved). References and counts describe the local selection, matching the reference, not what the auxiliary model consumed.
- The payload persists as a `ToolResultPart` JSON side-channel (`structuredOutput`) through `ToolExecutionResult` and tool-batch commit; it is serialized with the durable message and never enters provider payloads. The model-visible text is unchanged.
- The material pipeline carries chunk records (start/end message index, count, score, references) mirroring `buildSessionContextMaterial`; the chunk labels fed to the auxiliary model come from the same structure.
- The per-request `#sess_*` reminder is ported: real user turn input containing `#sess_*` (dot suffixes allowed) injects the referenced-session reminder before the date-change reminder as an in-memory canonical reminder; notification turns and mid-turn Guide input do not inject it, and it is not persisted or compaction-preserved.
- Local transcript formatting now follows the reference budget walk (header-length subtraction, per-snippet fit, first overflowing snippet truncated in place when over 200 chars remain) and the reference `truncateText` semantics. Session ids are validated against `^sess_[A-Za-z0-9._-]+$` at the schema and input-validation layers. The final result text is capped at 80,000 UTF-8 bytes with the reference's resultBudget truncation suffix.

Sources: `src/Alcoler.Kernel/Tools/ReadSessionContextTool.cs`, `src/Alcoler.Contracts/Tools/ReadSessionContextTypes.cs`, `src/Alcoler.Contracts/Conversation/ConversationMessage.cs`, `src/Alcoler.Contracts/Tools/ToolExecution.cs`, `src/Alcoler.Kernel/Runtime/SessionRuntime.cs`, `src/Alcoler.Kernel/Prompts/SessionReferenceReminder.cs`.

## 2. Remaining Differences and Long-Task Impact

### 2.1 Grep engine fallback and pagination cost

**Priority: Medium. Directly affects model evidence only where the native library is unavailable.**

- The six delivered RIDs load the native ripgrep engine for every search. On any other RID without a Rust toolchain at build time, the managed .NET fallback runs instead: .NET Regex semantics, no ignore-file rules, a heuristic binary sniff instead of ripgrep quit-mode detection, and no per-match regex timeout. Output formats are identical; file-selection semantics are not.
- `head_limit` paginates results after the search completes; the engine does not stop early at the limit, preserving deterministic mtime/ordinal ordering. A small `head_limit` limits returned results, not the work performed.
- The native libraries and the bundled `rg` binaries are all version-locked to ripgrep 14.1.1 but are separate artifacts produced by different sources (cargo build vs. official release download); updating one does not update the other.

Long-task effect: on the delivered RIDs, repeated searches no longer include ignored dependencies, generated files, or build output, and cancellation bounds a runaway search. Fallback RIDs retain the original evidence-noise and unbounded-match exposure.

Narrowing path/glob still reduces exposure on fallback builds. Do not assume the fallback glob matcher supports all ripgrep exclusion syntax.

Sources: `src/Alcoler.Adapters/Tools/GrepTool.cs`, `src/Alcoler.Adapters/Tools/ManagedGrepEngine.cs`, `src/Alcoler.Adapters/Grep/AlcoGrepNative.cs`, `src/Alcoler.Adapters/Alcoler.Adapters.csproj`.

### 2.2 Image transformation and extreme decode size

**Priority: Medium for resource safety; usually low for ordinary code-only tasks.**

SkiaSharp replaces the reference's Jimp pipeline. Input is limited to 20 MiB and the output's longest dimension to 2,000 pixels. The nominal byte gates allow 3.75 MiB of encoded image data and 5 MiB of base64, but the additional 25,000-token gate at 0.125 tokens per base64 byte is stricter: the effective output ceiling is 200,000 base64 bytes, or approximately 150,000 encoded-image bytes. Describing this as a 3.75 MB base64 target would be incorrect.

Compression details and transformed pixels are not byte-identical to Jimp; dense screenshot text may differ in clarity. This alone does not establish worse model vision performance. Visual comparison should exercise the effective token-derived compression threshold, not only nominal byte limits.

Scaled decoding is requested where supported, but codec behavior determines whether a source-sized native bitmap is still needed. There is no independent decoded-pixel cap, and cancellation does not immediately interrupt native decode. A small compressed file can therefore require much more native memory than its file size suggests. No out-of-memory incident has been established; the reference's decode-first path is not inherently immune either.

Long-task effect: usually negligible for source-code work, but repeated or extremely large image inputs can create latency and memory peaks. The useful follow-up is extreme-input resource hardening and visual tests, not switching libraries solely for implementation identity.

Source: `src/Alcoler.Adapters/Tools/ReadTool.cs`.

### 2.3 Manual promotion of a running foreground task

**Priority: Low for model reasoning; relevant to long-command user control.**

Launch-time background execution and eligible timeout-driven conversion already exist. The missing capability is a user command/UI action that promotes the same already-running foreground operation to the background immediately.

Long-task effect: reduced ability to intervene while a slow build or test is running. This is not absence of background execution, and it does not imply timeout always kills the process.

Sources: `src/Alcoler.Adapters/Tools/BashTool.cs`, `src/Alcoler.Contracts/Commands/ConversationCommands.cs`.

### 2.4 Branch-generation filtering for stale notifications

**Priority: Conditional. Required before adding history rewind or branching.**

The reference associates background notifications with branch generations and discards stale-branch results. Alcoler has session disposal and queue/cancellation protections, but no equivalent branch-generation contract. Its current public commands do not expose the corresponding full rewind/branch workflow.

Long-task effect: no current cross-branch failure has been established. If history branching or another explicit result-invalidation workflow is introduced, a task from an abandoned branch must not later enter the active branch as valid evidence. Design the filtering with that feature, rather than assuming cancellation alone supplies it.

Sources: `src/Alcoler.Kernel/Runtime/SessionRuntime.cs`, `src/Alcoler.Contracts/Commands/ConversationCommands.cs`.

### 2.5 Structured sources for ReadSessionContext: residual adaptations

**Priority: Low. The contract is implemented; the remaining differences are storage-shape adaptations, not missing structure.**

- The persisted structured payload is metadata-only: `content` is not duplicated into it, unlike the reference's raw tool output, which embeds content. The model-visible text block remains the single content carrier, so audit tooling must join the payload with the sibling text. Selected ranges are derivable from reference indexes; no separate ranges field exists, matching the reference output contract.
- `path` is always null: the SQLite store has no per-session file path equivalent to the reference's session file path.
- The Workbench does not yet render references/counts from the payload; the durable contract exists for auditing and future UI.

Long-task effect: none on model-visible behavior; the reference's formatter also omits references from model content. Cross-session auditing can now map a returned context back to its source messages via the persisted references.

Sources: `src/Alcoler.Kernel/Tools/ReadSessionContextTool.cs`, `src/Alcoler.Contracts/Tools/ReadSessionContextTypes.cs`.

### 2.6 Other compatibility adaptations

- Normalized model events do not retain all raw response-start and indexed provider content-block provenance. Compaction commit/fallback decisions use normalized block-end/tool-ready events rather than an identical raw provider event contract. Edge-case equivalence is therefore bounded by adapter normalization.
- Concrete supported reasoning fields are modeled explicitly; there is no arbitrary provider-options bag. New provider-specific features need explicit contract/adapter support rather than automatic passthrough.
- Legacy messages without known provider/model origin remain unknown. New origin metadata cannot retroactively establish where old reasoning came from.
- Local skill roots and workspace shadowing are intentional. Guidance for nonexistent CLI commands is not advertised.

These are compatibility boundaries, not evidence of a universal long-task regression. They matter when using the corresponding provider feature, legacy history, or skill layout.

## 3. Validation Evidence and Limits

### 3.1 Last completed alignment checkpoint

The per-subagent artifact/locator alignment run (2026-10-21) reported:

| Check | Result |
| --- | --- |
| Solution build | 0 warnings, 0 errors |
| Kernel tests | 1,345 passed, 4 failed (environmental) |
| Kernel tests excluding the affected class | 1,304 passed, 0 failed |
| Workbench tests | 516 passed, 0 failed |
| Browser tests | 18 passed, 1 skipped, 0 failed |
| CRLF-aware whitespace check | Passed |

The four failing kernel tests are cmd-dialect BashToolTests shell-fixture tests. They were reproduced failing identically on a clean HEAD full-suite run of the same host, pass when run in isolation, and the host carries leaked shell/testhost processes from earlier sessions; they are environmental flakiness, not a regression of this checkpoint. The skipped browser test requires installed Playwright browsers. It is not a successful real-browser smoke test.

A separate memory probe on the final alignment binaries reported 110.2 MB working set after one 400-turn session with 800 timeline rows, and 117.7 MB after ten resident sessions totaling 850 turns, with 15.2 MB managed memory at that point. These are kernel test-process measurements, not complete Avalonia desktop measurements or cross-machine guarantees.

This is a historical checkpoint, not a claim that later workspace changes or this documentation update reran the suites.

### 3.1.1 Structured session sources alignment run (2026-10-21)

The ReadSessionContext structured-sources and `#sess_*` reminder run (section 1.7) reported: solution build 0 errors; kernel tests 1,380 passed, 0 failed; Workbench tests 617 passed with intermittent single-test failures in `DraftConversationLifecycleTests.UnsentSessionModes_SurviveRestartIndependently_WithoutChangingRuntimePolicy` and `DraftConversationSmokeTests.ModePicker_ClickSavesImmediately_AndNewWindowRestoresSelection` (permission mode reverting to Build). Those failures were reproduced at a similar rate (3 of 10 full-suite runs) on a clean HEAD worktree without these changes, pass consistently in isolation, and exercise only Workbench draft-mode persistence with a fake runtime; they are pre-existing flakiness, not a regression of this run.

### 3.2 Evidence still missing

- No configured real-provider engineering comparison was run. The real-provider smoke test returns early when configuration is absent; green suite counts must not be interpreted as real-model execution.
- No same-model, same-task, same-repository comparison establishes equivalent long-task success rates, recovery quality, or context retention.
- Native desktop interaction and steady-state memory were not measured end to end. Headless Workbench tests do not replace that evidence.
- Native AOT publication and non-Windows execution were not validated in that checkpoint.

These are validation limits, not known architectural failures. Scripted regression coverage is necessary, but cannot prove real-model engineering parity.

## 4. Long-Task Priorities

1. Close Grep's residual gaps: native-engine delivery beyond win-x64 (source builds or additional prebuilts) and `head_limit` early exit without breaking deterministic ordering. Evidence supplied by the model on the delivered build now comes from real ripgrep semantics.
2. Compare real long tasks with the same model and repository, including repeated search, compaction, stream interruption, delegation, and session resumption. Use failures to distinguish harness effects from model variation.
3. Bound extreme image decode resources and validate the actual target-platform desktop/AOT artifacts.
4. Surface the persisted ReadSessionContext structured references in the Workbench if auditing workflows need them; the durable contract, `#sess_*` reminder, and per-message references already exist, and on-demand child-transcript navigation is covered by the per-agent artifacts and `local_agent` task queries.
5. Add manual foreground-to-background control as a product feature; implement branch-generation filtering before exposing history branching.

The remaining differences should not be described either as proof of a defective core loop or as uniformly cosmetic. Grep directly affects evidence acquisition; the other gaps are mostly conditional reliability, control, traceability, and verification concerns.
