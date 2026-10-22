# CefGlue.Next Feasibility Experiment for Alcoler

Date: 2026-10-22  
Status: Experimental evidence, not production acceptance  
Scope: Isolated prototype; no production browser migration

## 1. Executive conclusion

CefGlue.Next can host Alcoler's core browser automation. Real runs demonstrated this on Windows with both ordinary .NET and NativeAOT, and on Linux under WSLg/X11. Existing browser-controller and CDP automation code was reused rather than replaced.

The experiment does **not** establish that CefGlue.Next meets all production requirements. Two acceptance concerns remain decisive:

1. **Sandbox:** The tested high-level startup path did not provide a working Chromium sandbox. Windows remained unsandboxed even with `NoSandbox=false` and without an explicit `--no-sandbox` argument. Linux was deliberately tested without a sandbox against the local fixture only.
2. **Memory:** After the scenario sequence returned to one tab, even the minimal Windows AOT host exceeded the experimental 200 MiB working-set threshold. Full Workbench compliance with the repository's under-200-MB target is unproven.

A reproducible OOPIF failure was traced to existing Alcoler session routing, not a missing CEF protocol capability. Direct child-session commands and incoming events succeeded.

**Recommendation:** Keep CefGlue.Next as a viable unified embedded-Chromium candidate, but do not treat it as a drop-in production replacement. Require a supported sandbox/isolation design, full-application memory acceptance, and macOS validation before adoption.

## 2. Setup and methodology

### Versions and environments

| Component | Tested value |
|---|---|
| CefGlue.Next.Avalonia | 154.8037.98 |
| Avalonia Desktop / Fluent | 11.3.17 |
| .NET SDK / runtime | 10.0.401 / 10.0.12 |
| Windows | Windows NT 10.0.22621, x64 |
| Linux | Ubuntu 22.04 under WSLg, kernel 6.18.54.1, X11 |
| Observed Chromium product, both systems | Chrome/154.0.8037.94 |
| Observed CDP version | 1.3 |
| Reviewed CefGlue source commit | c88806fae822e552dea10f1a3760d2b09d98a907 |

The observed Chromium version ends in `.94`, although the managed package version and reviewed version configuration end in `.98`. Runtime evidence takes precedence over package naming; the discrepancy was not resolved in this experiment.

### Prototype

All experiment-owned files are under this ignored artifact directory. The prototype consists of:

- [CefNextProbe.csproj](CefNextProbe.csproj): pinned experimental dependencies and a reference to the existing browser project.
- [Program.cs](Program.cs): Avalonia/CEF startup, baseline mode, and test lifetime.
- [CefHost.cs](CefHost.cs): `IBrowserHost` / `IBrowserPage` implementation, session-specific request contexts, and raw in-process CDP transport.
- [ProbeSuite.cs](ProbeSuite.cs): real-browser scenarios and assertions.
- The existing [LoopbackFixture.cs](../../tests/Alcoler.Browser.IntegrationTests/LoopbackFixture.cs), source-linked into the prototype.

The controller creates the existing `CdpBrowserAutomation` for each CEF page. No browser automation was mocked. CDP messages use `CefBrowserHost.SendDevToolsMessage` and `CefDevToolsMessageObserver`; no remote-debugging TCP port was required.

The fixture is local. A `localhost` child frame inside a `127.0.0.1` parent, together with `--site-per-process`, forces the OOPIF test. Windows executables use an 8 MiB stack and the repository's existing supported-OS application manifest.

No production project dependency was changed. Linux NSS/NSPR dependencies were downloaded and extracted locally, without system-wide installation or configuration changes. No commits were made.

## 3. Final scenario results

| Scenario | Windows .NET | Windows AOT | Linux .NET |
|---|---|---|---|
| Real page, CDP, engine version | Pass | Pass | Pass |
| Accessibility reference and DOM snapshot | Pass | Pass | Pass |
| Locators, keyboard, Shadow DOM | Pass | Pass | Pass |
| Viewport, clipped, full-page PNG | Pass | Pass | Pass |
| OOPIF through existing controller routing | Fail | Fail | Fail |
| Separate raw OOPIF child-session diagnostic | Pass | Pass | Pass |
| Same-session sharing and cross-session isolation | Pass | Pass | Pass |
| Bounded evaluation cancellation and recovery | Pass | Pass | Pass |
| WebM recording and takeover authorization | Pass | Pass | Pass |
| Media download bytes | Pass | Pass | Pass |
| JavaScript prompt via CDP | Pass | Pass | Pass |
| Hidden-window changing-pixel rendering | Pass | Pass | Pass |
| Detach/reattach retains browser and JS state | Pass | Pass | Pass |
| Read sandbox status | Pass: unsandboxed | Pass: unsandboxed | Pass: unsandboxed |
| One-tab host working-set budget | Fail | Fail | Fail |
| **Total** | **13 pass / 2 fail** | **13 pass / 2 fail** | **13 pass / 2 fail** |

The sandbox row asserts that status can be inspected, **not** that security acceptance passed. These totals are diagnostic-suite totals, not a declaration that 13 production requirements are fully satisfied.

### What the passing assertions establish

- Accessibility snapshots yielded a real button reference, which was clicked successfully. DOM snapshots contained real page data.
- Locator, keyboard, and Shadow DOM actions produced expected DOM changes. Chinese text injection was exercised; this is not a real Chinese IME test.
- PNG images were decoded and checked: viewport 960 x 640, clip 240 x 130, full-page height at least 1900 pixels.
- Session-specific contexts shared cookies/localStorage between same-session tabs and isolated a different session.
- A promise resolving after 1500 ms was timed out at 250 ms; the next evaluation returned 42. Infinite JavaScript termination was not tested.
- Recording returned WebM bytes with the EBML signature. The final Windows AOT recording was 24,305 bytes; Linux returned 23,610 bytes. Full playback/content quality was not verified.
- Takeover rejected model input and model-requested resume; a user-initiated resume succeeded. Actual manual GUI takeover was not exercised.
- `download_media` returned the fixture's exact bytes. This is not a native clicked-download/save-path test.
- The prompt returned the accepted text through CDP.
- While the entire window was hidden, decoded screenshot pixel (100,100) changed from `FFFF0000` to `FF0000FF`.
- Detach/reattach preserved both the CEF browser identifier and a JavaScript sentinel. Full Workbench presenter integration was not exercised.

### Restart persistence and regression checks

After explicitly awaiting the session cookie manager's `FlushStore` callback, separate reader processes reused the writer's profile and found the persistent cookie:

| Additional check | Result |
|---|---|
| Windows .NET restart persistence | 2 pass / 0 fail |
| Windows NativeAOT restart persistence | 2 pass / 0 fail |
| Linux .NET restart persistence | 2 pass / 0 fail |
| Existing Alcoler.Browser.Tests | 246 pass / 0 fail / 0 skipped |

Cookie scope ignores the fixture's ephemeral port. The restart test therefore checks cookies, not localStorage, whose origin includes the port. These are a small number of runs, not a reliability or soak-test study.

## 4. OOPIF failure: existing engine routing defect

The final Windows AOT and Linux traces show this ordering:

```text
Target.attachedToTarget(frame F, session S)
Page.frameDetached(frame F, reason="swap")
Page.createIsolatedWorld(frame F, without sessionId)
Error -32602: No frame for given id found
```

In the source reviewed during the experiment:

- `CdpBrowserAutomation.cs`, around lines 474-485, removes `_frameSessions[frameId]` on `Page.frameDetached`, including `reason="swap"`, after the attachment event has installed the live child route.
- Around lines 283-285, the missing mapping causes `Page.createIsolatedWorld` to be sent to the root session.

See [CdpBrowserAutomation.cs](../../src/Alcoler.Browser/Automation/CdpBrowserAutomation.cs). Line numbers describe the inspected revision and may move.

The separate diagnostic retained the session from the attachment event, enabled child Page/Runtime domains, verified the child frame tree, and changed/read the child DOM. In final AOT and Linux runs, the exact failing method, `Page.createIsolatedWorld`, succeeded when supplied with session S. Each logged five child commands and two received session-bearing events. The raw event payloads were not printed individually; the transport's top-level `sessionId` counter and assertions provide the recorded event evidence.

This isolates the observed failure to Alcoler's mapping logic. The original failed action remains reported; it was not silently retried or converted into a pass. No production fix was applied. A future fix must handle swap ordering and retire routes on target detachment without deleting a newer session mapping.

## 5. Sandbox and lifecycle findings

### Runtime sandbox evidence

Windows final tests did not pass `--no-sandbox`; the prototype settings therefore used `NoSandbox=false`. Nevertheless:

- `chrome://sandbox` reported Renderer, GPU, and Utility processes as `Not Sandboxed`.
- The separately captured child-process command lines included `--no-sandbox`.

Linux ran with `--no-sandbox` against the local fixture. Its status page reported no layer-1 sandbox, no PID/network namespaces, no Seccomp-BPF sandbox, and `You are NOT adequately sandboxed.` This does not prove that CEF itself cannot be sandboxed on Linux; it proves no successful sandbox setup in this tested integration.

Source review found that the high-level helpers pass a null Windows sandbox-information pointer. Matching CEF initialization disables the Windows sandbox in that case. Merely changing the managed `NoSandbox` property is insufficient. The reviewed loader also forces `NoSandbox=true` on Linux/macOS; macOS was not executed.

References:

- [Pinned CefRuntimeLoader source](https://github.com/Deon-Berlin/CefGlue/blob/c88806fae822e552dea10f1a3760d2b09d98a907/CefGlue.Common/CefRuntimeLoader.cs)
- [Pinned subprocess helper](https://github.com/Deon-Berlin/CefGlue/blob/c88806fae822e552dea10f1a3760d2b09d98a907/CefGlue.BrowserProcess.Core/CefSubProcess.cs)
- [Matching CEF main runner](https://github.com/chromiumembedded/cef/blob/14c5a08/libcef/browser/main_runner.cc)
- [CEF sandbox setup](https://chromiumembedded.github.io/cef/sandbox_setup.html)

Profile isolation is not an operating-system security boundary. Arbitrary untrusted web access needs a supported sandbox or an explicitly designed external isolation boundary.

### Shutdown and persistence

The first restart-persistence check failed. After awaiting explicit cookie flush completion, restart checks passed on both systems and in Windows AOT.

The reviewed loader registers an anonymous ProcessExit callback calling `CefRuntime.Shutdown()`, with no public opt-out. The reviewed shutdown implementation does not reset its initialized flag. Adding another explicit shutdown risks duplicate native shutdown, and ProcessExit does not guarantee CEF's required shutdown-thread semantics.

Control disposal requests asynchronous closure; this probe does not wait for per-browser `OnBeforeClose` acknowledgements. Final observed runs exited without remaining experiment-owned processes, but this is not proof of rigorously correct shutdown ordering. Production integration needs an owned, exactly-once initialization/close/shutdown lifecycle.

## 6. Memory and deployment

All values below are MiB, measured from the host process after managed collection. They are observations, not confidence intervals or optimized lower bounds.

| Host-process measurement | Windows .NET | Windows AOT | Linux .NET / WSLg |
|---|---:|---:|---:|
| Minimal Avalonia-only baseline | 105.04 | 69.05 | Not measured |
| One tab, idle after about 30 seconds | 208.18 | 175.27 | Not measured |
| Final full-suite initial page | 195.48 | 151.91 | 390.41 |
| Final suite returned to one tab | 234.90 | 204.87 | 420.91 |
| Private memory at final one-tab sample | 153.31 | 150.81 | 541.93 |
| Managed memory at final one-tab sample | 1.39 | 5.15 | 2.94 |

The final one-tab sample follows navigation, additional contexts/tabs, recording, screenshots, reparenting, and sandbox inspection, with a three-second wait after returning to the fixture. It is a post-scenario sample, not a long-term steady-state study. Browser caches and asynchronous close completion can affect it.

The repository target is under 200 MB working set for one active session. The probe used a permissive 200 MiB threshold and still failed after the scenario sequence in all three final environments. The minimal harness is not the full Workbench; its baseline cannot be substituted for the approximately 135 MB desktop floor documented in AGENTS.md. AOT helps substantially, but full-application compliance is not established. WSLg results must not be generalized to every Linux desktop or compared as identical accounting to Windows.

A separate Windows AOT idle snapshot contained six owned processes. Working sets summed to 438.62 MiB, with private-memory counters summing to 292.97 MiB. Summing working sets double-counts shared pages and is **not** a measurement of unique physical memory. Evidence: [processes.json](results/win-aot-idle/processes.json).

### AOT and packaging

- Windows NativeAOT publishing and real native execution succeeded. Runtime output explicitly recorded `aot=True`.
- Publishing emitted CefGlue trimming/AOT analysis warnings (`IL2104`, `IL3053`), plus existing Contracts serialization warnings (`IL2026`, `IL3050`). These were not fatal, but success for the exercised path does not prove all package APIs are AOT-safe.
- Final Windows AOT output measured approximately 453.76 MiB excluding PDBs, including all copied locale resources; the executable was about 38.81 MiB. The unoptimized output is not a final installer-size estimate.
- Linux publish omitted CEF native/resource assets even though they were copied to the build directory. The package target copied after Build into `TargetDir` but did not register those files for publish.
- After staging missing CEF assets, Linux aborted on ICU initialization because resources were nested under `Resources/` while executable-relative ICU data was absent. Staging the resources beside the executable resolved this observed startup failure.
- Local NSS/NSPR extraction resolved required Linux dynamic-library dependencies. An optional LTTng tracing library dependency remained unavailable; it did not prevent the tested run. No incompatible ABI symlink was created.

## 7. Failure history and limitations

The final results do not erase earlier failures:

| Earlier observation | Investigation / disposition |
|---|---|
| Compile error from disposing the observer | Observer lacks public disposal; unregistering its retained registration corrected the adapter. |
| Native startup exits with code 3 | Moving request-context creation into the browser's post-initialization factory allowed startup to progress. An 8 MiB stack alone had not fixed it; exact native root cause was not independently established. |
| Mixed-separator profile path and missing supported-OS manifest | Absolute path normalization and the existing integration-test manifest resolved the observed startup errors. The first AOT executable was stale and failed with these same defects; rebuilt AOT ran. |
| Prompt test timed out and caused a modal cascade | Harness used raw-CDP field names instead of controller `operation` / `text`; corrected runs passed. |
| Showing the window restarted the suite | Repeated Opened events were guarded. The affected run's later memory samples were excluded. |
| One early recording did not return valid WebM | Root cause was not independently isolated. Later independent Windows .NET, AOT, and Linux runs passed; intermittent reliability remains unproven. |
| First restart cookie check failed | Explicit flush completion was added; subsequent restart checks passed. |
| OOPIF and memory checks failed | Retained as final failures, with separate OOPIF transport diagnosis. |
| First Linux launch command resolved the wrong executable path | Shell-command quoting was replaced with a script; this was not a CEF runtime failure. |
| Linux abort with invalid ICU data descriptor | Corrected app-local resource layout; subsequent suite completed. |

Linux emitted missing user D-Bus/UPower errors under WSLg while assertions continued to pass. One successful AOT persistence run also logged GPU command-buffer errors during exit. These diagnostics remain in the logs; they were not established as harmless for production.

Not validated:

- macOS execution or its sandbox, lifecycle, IME, and NativeAOT behavior.
- Linux NativeAOT or native Wayland; the Linux run used X11 under WSLg.
- Full Workbench composition/presenter integration and real human input/takeover.
- Native popup ownership, clicked-download destination security, permission policy, and external-URL handling.
- Infinite JavaScript interruption and recovery.
- Long-running navigation, tab churn, memory soak, and crash recovery.
- Full audio/video codec coverage or recording playback quality.

## 8. Evidence and reproduction

### Primary evidence

| Evidence | Location |
|---|---|
| Windows .NET final results / log | [JSON](results/win-jit-v5/results.json), [log](results/win-jit-v5/run.log) |
| Windows AOT final results / log | [JSON](results/win-aot-v3/results.json), [log](results/win-aot-v3/run.log) |
| Linux final results / log | [JSON](linux-deps/run-output-v3/results.json), [log](linux-deps/run-linux-v3.log) |
| Windows AOT idle memory / baseline | [Idle](results/win-aot-idle/run.log), [baseline](results/win-aot-baseline/run.log) |
| Windows .NET idle memory | [Log](results/win-jit-idle/run.log) |
| Windows .NET restart after flush | [JSON](results/win-jit-persistence-flushed/results.json), [log](results/win-jit-persistence-flushed/run.log) |
| Windows AOT restart after flush | [JSON](results/win-aot-persistence/results.json), [log](results/win-aot-persistence/run.log) |
| Linux restart after flush | [JSON](linux-deps/run-persistence/results.json), [log](linux-deps/run-persistence.log) |
| Final publish warnings | [Log](results/publish-final.log) |
| Existing browser tests | [Log](results/browser-tests.log) |
| Windows AOT hidden rendering | [Red PNG](results/win-aot-v3/hidden-red.png), [blue PNG](results/win-aot-v3/hidden-blue.png) |
| Windows AOT recording | [WebM](results/win-aot-v3/recording.webm) |
| Windows AOT / Linux sandbox status | [Windows JSON](results/win-aot-v3/sandbox.json), [Linux JSON](linux-deps/run-output-v3/sandbox.json) |

### Reproduction on the prepared machine

Use a fresh output directory for each run to preserve existing evidence. The original full-suite binaries intentionally return a nonzero exit code for the reported OOPIF and memory failures.

Windows .NET, from Git Bash in the repository root:

```bash
dotnet build artifacts/cef-next-probe-20261022/CefNextProbe.csproj -c Release -r win-x64
'/c/Program Files/Microsoft Visual Studio/18/Insiders/VC/Tools/MSVC/14.51.36231/bin/Hostx64/x64/editbin.exe' \
  /STACK:8388608 artifacts/cef-next-probe-20261022/bin/Release/net10.0/win-x64/CefNextProbe.exe
CEF_PROBE_OUTPUT=C:/Projects/Alcoler/artifacts/cef-next-probe-20261022/results/recheck-jit \
  artifacts/cef-next-probe-20261022/bin/Release/net10.0/win-x64/CefNextProbe.exe
```

Windows AOT uses the machine-specific Visual Studio path in [publish-aot.cmd](publish-aot.cmd):

```bash
MSYS_NO_PATHCONV=1 cmd.exe /c 'C:\Projects\Alcoler\artifacts\cef-next-probe-20261022\publish-aot.cmd'
CEF_PROBE_OUTPUT=C:/Projects/Alcoler/artifacts/cef-next-probe-20261022/results/recheck-aot \
  artifacts/cef-next-probe-20261022/publish-aot/CefNextProbe.exe
```

Linux on the already prepared WSL Ubuntu instance, with the staged resources and locally extracted libraries:

```bash
MSYS_NO_PATHCONV=1 wsl.exe -d Ubuntu -- env \
  CEF_PROBE_OUTPUT=/mnt/c/Projects/Alcoler/artifacts/cef-next-probe-20261022/linux-deps/recheck \
  CEF_PROBE_PROFILE=/mnt/c/Projects/Alcoler/artifacts/cef-next-probe-20261022/linux-deps/recheck/profiles \
  CEF_PROBE_LOG=/mnt/c/Projects/Alcoler/artifacts/cef-next-probe-20261022/linux-deps/recheck.log \
  bash /mnt/c/Projects/Alcoler/artifacts/cef-next-probe-20261022/run-linux.sh
```

The Linux launch script deliberately passes `--no-sandbox`; it is for this local-fixture experiment only. Re-publishing Linux requires checking/staging CEF assets again because of the observed package publish gap.

For restart verification, wait for the writer process to exit, keep its `CEF_PROBE_PROFILE`, choose a new output directory, and run the same executable with `--verify-persistence`. `--memory-only` samples an idle single tab; `--baseline` starts Avalonia without initializing CEF. Existing browser regression command:

```bash
dotnet test tests/Alcoler.Browser.Tests/Alcoler.Browser.Tests.csproj --no-restore --verbosity minimal
```

## 9. Adoption gates

Production acceptance requires evidence for all of the following, rather than another successful page load:

1. Supported, independently verified sandboxing or an explicit external isolation boundary for untrusted content.
2. Full Workbench memory compliance after representative activity and sustained tab/session churn.
3. Correct OOPIF session lifecycle with regression coverage for attach-before-swap ordering.
4. Deterministic profile flush, browser closure, and exactly-once CEF shutdown ownership.
5. Reproducible native-resource deployment and platform dependency checks.
6. Real macOS execution and remaining platform-specific UI/input/security scenarios.

The experiment supports the architectural feasibility of a unified Chromium backend. It does not authorize or justify relaxing those gates.
