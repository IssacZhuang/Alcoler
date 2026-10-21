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

Sources: `src/Alcoler.Kernel/Tools/AskUserQuestionTool.cs`, `src/Alcoler.Workbench/ViewModel/PendingInteractionViewModel.cs`, `src/Alcoler.Kernel/Subagents/RespondToCoordinatorTool.cs`, `src/Alcoler.Kernel/Subagents/SubagentWiring.cs`, `src/Alcoler.Kernel/Tools/BackgroundJobNotifications.cs`.

### 1.6 Grep search engine

- The Grep tool executes on the ripgrep 14.1.1 engine crates (`ignore` walker, `grep-searcher`, `grep-regex`) compiled into an in-process Rust library (`alco_grep.dll`) and invoked via P/Invoke; no helper process is spawned. A managed .NET engine remains as a fallback when the native library is unavailable, mirroring the reference's JS fallback architecture. The tool contract, parameter schema, and output formats are unchanged.
- File selection honors `.gitignore` / `.ignore` / `.rgignore` rules, searches hidden files, excludes version-control directories, skips binary files the way ripgrep's quit mode does, and sniffs BOMs (UTF-16) during search.
- Cancellation interrupts a running native search through a cancel flag checked during the walk; invalid patterns surface the same error text on both engines; lines longer than 500 characters are truncated identically by the shared formatter.
- The regex dialect is ripgrep's Rust regex (no look-around or backreferences), stated in the tool description.
- Bash sessions define an `rg` shell function over a bundled official ripgrep 14.1.1 binary when no system `rg` is on PATH.

Sources: `src/Alcoler.Adapters.Native/alco-grep/`, `src/Alcoler.Adapters/Grep/`, `src/Alcoler.Adapters/Tools/GrepTool.cs`, `src/Alcoler.Adapters/Tools/ManagedGrepEngine.cs`, `src/Alcoler.Adapters/Tools/AlcoGrepEngine.cs`, `src/Alcoler.Adapters/Tools/RipgrepBinary.cs`, `src/Alcoler.Adapters/Tools/BackgroundBashRunner.cs`.

## 2. Remaining Differences and Long-Task Impact

### 2.1 Grep engine fallback and pagination cost

**Priority: Medium. Directly affects model evidence only where the native library is unavailable.**

- The delivered win-x64 build loads the native ripgrep engine for every search. On other RIDs without a Rust toolchain at build time, the managed .NET fallback runs instead: .NET Regex semantics, no ignore-file rules, a heuristic binary sniff instead of ripgrep quit-mode detection, and no per-match regex timeout. Output formats are identical; file-selection semantics are not.
- `head_limit` paginates results after the search completes; the engine does not stop early at the limit, preserving deterministic mtime/ordinal ordering. A small `head_limit` limits returned results, not the work performed.
- The native library and the bundled `rg.exe` shim binary are both version-locked to ripgrep 14.1.1 but are separate artifacts; updating one does not update the other.

Long-task effect: on the delivered build, repeated searches no longer include ignored dependencies, generated files, or build output, and cancellation bounds a runaway search. Fallback builds retain the original evidence-noise and unbounded-match exposure.

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

### 2.4 Per-subagent transcript locator

**Priority: Medium for complex multi-agent verification; low for simple delegation.**

Parents normally receive the child's final assistant text, not its full tool transcript. Background completion places that result in a notification whose body is truncated at 120,000 characters before the truncation marker and system prefix are added; this is not a strict total-envelope limit. There is no per-agent transcript file pointer comparable to the reference.

Child sessions still share durable storage, and live observation exists; this is not loss of child history. Bash background jobs already have output-file pointers and should not be confused with this gap.

Long-task effect: investigating a questionable child conclusion requires less direct navigation through child-session facilities. That can increase duplicate investigation or reliance on an insufficiently checked conclusion. The benefit of a locator is on-demand evidence retrieval, not injecting every child's complete transcript into the parent's context.

Sources: `src/Alcoler.Kernel/Subagents/SubagentWiring.cs`, `src/Alcoler.Kernel/Tools/BackgroundJobNotifications.cs`, `src/Alcoler.Contracts/Projections/IAgentRuntime.cs`.

### 2.5 Branch-generation filtering for stale notifications

**Priority: Conditional. Required before adding history rewind or branching.**

The reference associates background notifications with branch generations and discards stale-branch results. Alcoler has session disposal and queue/cancellation protections, but no equivalent branch-generation contract. Its current public commands do not expose the corresponding full rewind/branch workflow.

Long-task effect: no current cross-branch failure has been established. If history branching or another explicit result-invalidation workflow is introduced, a task from an abandoned branch must not later enter the active branch as valid evidence. Design the filtering with that feature, rather than assuming cancellation alone supplies it.

Sources: `src/Alcoler.Kernel/Runtime/SessionRuntime.cs`, `src/Alcoler.Contracts/Commands/ConversationCommands.cs`.

### 2.6 Structured sources for ReadSessionContext

**Priority: Medium for cross-session continuation and auditing; low for ordinary continuous execution.**

Text extraction and synthesis are implemented. Local source text includes message identifiers and timestamps, and extraction input contains chunk/message ranges. The final auxiliary-model text need not preserve them, and the tool lacks a stable structured references/selected-ranges/counts contract.

Long-task effect: after resumption, a claim in a historical summary is harder to map reliably back to its original messages for checking. Source navigation, coverage checks, and automated evaluation are less direct. This does not prove poorer summary content: the reference's model-facing formatter also primarily presents text rather than its complete structured references.

Source: `src/Alcoler.Kernel/Tools/ReadSessionContextTool.cs`.

### 2.7 Other compatibility adaptations

- Normalized model events do not retain all raw response-start and indexed provider content-block provenance. Compaction commit/fallback decisions use normalized block-end/tool-ready events rather than an identical raw provider event contract. Edge-case equivalence is therefore bounded by adapter normalization.
- Concrete supported reasoning fields are modeled explicitly; there is no arbitrary provider-options bag. New provider-specific features need explicit contract/adapter support rather than automatic passthrough.
- Legacy messages without known provider/model origin remain unknown. New origin metadata cannot retroactively establish where old reasoning came from.
- Local skill roots and workspace shadowing are intentional. Guidance for nonexistent CLI commands is not advertised.

These are compatibility boundaries, not evidence of a universal long-task regression. They matter when using the corresponding provider feature, legacy history, or skill layout.

## 3. Validation Evidence and Limits

### 3.1 Last completed alignment checkpoint

The completed alignment run reported:

| Check | Result |
| --- | --- |
| Solution build | 0 warnings, 0 errors |
| Kernel tests | 1,278 passed, 0 failed |
| Workbench tests | 503 passed, 0 failed |
| Browser tests | 18 passed, 1 skipped, 0 failed |
| CRLF-aware whitespace check | Passed |

The skipped browser test requires installed Playwright browsers. It is not a successful real-browser smoke test.

A separate memory probe on the final alignment binaries reported 110.2 MB working set after one 400-turn session with 800 timeline rows, and 117.7 MB after ten resident sessions totaling 850 turns, with 15.2 MB managed memory at that point. These are kernel test-process measurements, not complete Avalonia desktop measurements or cross-machine guarantees.

This is a historical checkpoint, not a claim that later workspace changes or this documentation update reran the suites.

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
4. Add on-demand child-transcript navigation and structured historical sources where complex delegation or cross-session verification warrants them.
5. Add manual foreground-to-background control as a product feature; implement branch-generation filtering before exposing history branching.

The remaining differences should not be described either as proof of a defective core loop or as uniformly cosmetic. Grep directly affects evidence acquisition; the other gaps are mostly conditional reliability, control, traceability, and verification concerns.
