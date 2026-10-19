# Alcoler Project Structure

Alcoler is a from-scratch coding agent harness, built kernel-first. This file is the single source of truth for agent instructions and the only document maintained in this repository.

## Repository Layout

The repository is a Cargo workspace with four members:

- `crates/contracts` — kernel contracts: ports (traits) + domain types + error types. **Zero implementations**; dependencies are serde / serde_json / futures-core only.
- `crates/kernel` — the agent loop: command queue, turn loop, tool scheduling, context compaction. Depends on contracts only (plus tokio for spawning and serde_json); every timer and clock read goes through the ports so tests stay deterministic.
- `crates/adapters` — port implementations: runtime (clock/ids/gate/counter), file-backed session and blob storage, Anthropic Messages and OpenAI-compatible Chat Completions providers, a scripted mock provider, and the builtin tools (read_file / write_file / list_dir / run_command).
- `crates/server` — composition root (bin `alcoler-server`): env config, port assembly, axum HTTP + SSE, and the static web UI under `crates/server/static/` (served via `include_str!`, no build step).

Contracts is the innermost layer of the architecture; these rules are the repo's equivalent of a safety boundary:

- contracts declares ports and domain types only — implementations live in separate downstream crates, never here;
- no async runtime and no concrete technology (tokio, reqwest, sqlite, …) may appear in contracts — not even a type name;
- implementations depend on contracts, never the other way around; kernel depends on contracts only, adapters on contracts only, server on all three;
- kernel test semantics (pairing on cancel/timeout, retry vs `emitted_output`, queue serialization, recovery) are locked by `crates/kernel/tests/loop.rs` under the same rule as the contracts regressions.

## Key Invariants of the Contracts Layer

Before touching `crates/contracts`, confirm the following semantics hold (`tests/ports.rs` locks some of them; a failing regression test is not license to "update the assertion" — first determine whether the contract itself was broken):

- Event and command JSON shapes (`type` / `cmd` tag names) are defined by the regression tests in `crates/contracts/tests/ports.rs`.
- Every tool_use has a paired tool_result — cancellation and timeout must also produce a paired result (`ToolStatus::Cancelled` / `TimedOut`).
- Dropping the model stream cancels the upstream request (an obligation on `ModelProvider` implementations).
- Re-invoking `append_turn` for the same turn returns `StoreErrorKind::Conflict` (idempotent dedup for crash recovery).
- `ProviderError.emitted_output` decides whether the core may retry transparently (the retry rule beyond the visibility boundary).
- Port methods use hand-rolled `BoxFuture` / `BoxStream` (object safety), not async-trait; port methods taking multiple references must spell out explicit `<'a>` lifetimes.
- Naming: module names are domain nouns (`storage`); trait names carry a role suffix (`SessionStore`).

## Code Style

- Comments appear only where code cannot be self-explanatory, and state constraints — never what the next line does; no large explanation blocks. This applies to every language in the repo.
- contracts keeps `#![forbid(unsafe_code)]`; unsafe added to any crate must be justified at the code site.
- All comments, API docs, and documentation in English, regardless of language.
- No emoji.

## Workflow

- Runtime smoke test: `cargo run -p alcoler-server` with `ALCOLER_PROVIDER=mock` starts a network-free agent loop plus web UI. All providers (mock/anthropic/openai) stay registered behind a routing provider; each session picks its model at creation (`POST /api/sessions` with `{provider, model}`). Env vars: `ALCOLER_PROVIDER` (default provider for new sessions: mock|anthropic|openai), `ALCOLER_MODEL`, `ALCOLER_BASE_URL` / `ALCOLER_ANTHROPIC_BASE_URL` / `ALCOLER_OPENAI_BASE_URL`, `ANTHROPIC_API_KEY` / `OPENAI_API_KEY` / `ALCOLER_API_KEY`, `ALCOLER_DATA_DIR` (default `~/.alcoler`), `ALCOLER_LISTEN` (default `127.0.0.1:8787`).
- Build and lint: `cargo clippy --all-targets` (zero warnings is the baseline)
- Formatting: `cargo fmt` (must be clean before committing)
- Tests: `cargo test` (currently concentrated in `crates/contracts/tests/ports.rs`; for targeted debugging use `cargo test -p alcoler-contracts <test_name>` instead of repeatedly running the full suite)
- Report every test failure; you may suspect a failure is unrelated to your change, but you must verify before concluding so.
- Never commit unless explicitly asked; when asked, never add yourself as co-author.

## Changelog

No CHANGELOG and no standalone documents are maintained; this file is the only long-term maintained document.
