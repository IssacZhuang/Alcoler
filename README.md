# Alcoler

Alcoler is a personal, experimental coding agent harness built from scratch and kernel-first. It is an ongoing exploration of how far a single-process agent can go when the design optimizes for engineering quality, runtime performance, and a small memory footprint instead of feature breadth.

The project is not a stable product. Interfaces, persistence formats, and behavior may change without notice.

## Design Goals

- **Engineering quality.** A small set of focused projects with explicit contracts, predictable failure handling, and tests at the kernel boundary.
- **High performance.** In-process calls between the workbench and the kernel, streaming model responses, and no IPC or serialization hop on the hot path.
- **Low memory.** Session residency and event retention are bounded. In the kernel memory probe, 10 resident sessions holding 850 scripted turns add about 7 MB of managed memory and 21 MB of working set over the process floor; the footprint is balanced against performance rather than capped at a fixed working-set limit.
- **Kernel-first.** The agent loop, tools, providers, context management, and session persistence live in a UI-independent class library. The Avalonia workbench is a consumer of that library, not the place where behavior is defined.

## Project Layout

| Project | Responsibility |
| --- | --- |
| `src/Alcoler.Kernel` | Agent runtime, turn machine, tools, prompts, compaction, subagents, sessions |
| `src/Alcoler.Contracts` | Shared types, events, commands, projections, and interfaces |
| `src/Alcoler.Adapters` | Model providers, storage, shell and file tools, MCP, web access |
| `src/Alcoler.Browser` | Engine-agnostic browser automation (CDP core, controller, Browser tool) |
| `src/Alcoler.Browser.Chromium` | Cross-platform external Chromium backend (real browser process over loopback CDP) |
| `src/Alcoler` | Avalonia desktop workbench (embeds WebView2 as the Windows browser backend) |
| `tests/` | Kernel, workbench, and browser test suites |

## Build and Run

Alcoler targets .NET 10. The workbench runs on Windows; the browser tool additionally covers Linux and macOS through the headless external Chromium backend, which resolves an installed Chrome/Chromium/Edge or downloads a Chrome-for-Testing build on first use. On Windows, the settings dialog toggles between the embedded WebView2 view and the headless Chromium backend.

```sh
dotnet build Alcoler.slnx
dotnet run --project src/Alcoler/Alcoler.csproj
```

Run the test suites with:

```sh
dotnet test Alcoler.slnx
```

The browser integration suites launch a real browser (`tests/Alcoler.Browser.IntegrationTests` uses the WebView2 runtime on Windows; `tests/Alcoler.Browser.Chromium.IntegrationTests` uses any installed Chromium-family browser). Native search support is provided by the `alco-grep` library and a platform ripgrep binary; both are optional and degrade gracefully when unavailable.

## Credit

The kernel algorithms reference **zcode** as their behavioral and algorithmic source. Alcoler is an independent implementation written from scratch: zcode is used only as a reference for behavior, constants, and failure-recovery semantics, with no code reuse or codebase inheritance.
