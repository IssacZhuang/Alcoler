# Alcoler

Alcoler is a from-scratch coding agent harness, built kernel-first. This file is the single source of truth for agent instructions and the only document maintained in this repository.

## Code Style

- Comments appear only where code cannot be self-explanatory, and state constraints — never what the next line does; no large explanation blocks. This applies to every language in the repo.
- All comments, API docs, and documentation in English, regardless of language.
- No emoji.

## Workflow

- Report every test failure; you may suspect a failure is unrelated to your change, but you must verify before concluding so.
- Never commit unless explicitly asked; when asked, never add yourself as co-author.

## Browser Backends

The Browser tool is engine-agnostic: `src/Alcoler.Browser` (BrowserTool, BrowserController, CdpBrowserAutomation) drives any `IBrowserHost`/`IBrowserPage` implementation. Two backends ship:

- `src/Alcoler/Browser` — embedded WebView2, selected by the Windows "embedded browser view" setting. In-app pane embedding; native download/dialog interception.
- `src/Alcoler.Browser.Chromium` — headless Chromium-family browser process over loopback CDP WebSocket, the default everywhere except Windows-embedded. Never launches with `--no-sandbox`; one process per browser session with a persisted per-session profile; there is no visible browser window (window control actions report unsupported; takeover still gates control).

Selection: `browser.embeddedView` in `~/.alcoler/config.json` (Windows-only meaning; default true there, false elsewhere) picks WebView2 vs Chromium. `browser.headless: false` is a diagnostics escape hatch to run Chromium headed. Binary resolution for chromium: `browser.executablePath` → `ALCOLER_BROWSER_EXECUTABLE` → installed Chrome/Chromium/Edge → Chrome-for-Testing download into `~/.alcoler`. Verification: `tests/Alcoler.Browser.Tests` + `tests/Alcoler.Browser.Chromium.Tests` (unit, no browser), `tests/Alcoler.Browser.IntegrationTests` (WebView2, Windows) and `tests/Alcoler.Browser.Chromium.IntegrationTests` (real browser, any OS).

## Memory Budget

Baseline from the kernel memory probe (`tests/Alcoler.Kernel.Tests/EndToEnd/MemorySpikeProbe.cs`): 10 resident sessions holding 850 scripted turns cost 7 MB managed / +21 MB working set over the process floor; the desktop process floor is Avalonia-dominated (~135 MB). No fixed working-set cap is enforced: memory and performance must be weighed together, with session residency and event retention kept bounded.

## Changelog

No CHANGELOG and no standalone documents are maintained; this file is the only long-term maintained document.
