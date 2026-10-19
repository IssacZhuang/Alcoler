# Alcoler Project Structure

Alcoler is a from-scratch coding agent harness (kernel-first, following ZCode's execution semantics rather than its implementation). This file is the single source of truth for agent instructions and the only document maintained in this repository.

## Crate Layout and Dependency Rules

Only `crates/contracts` exists today; future crates follow this layout:

- `crates/contracts` — kernel contracts: ports (traits) + domain types + error types. **Zero implementations**; dependencies are serde / serde_json / futures-core only. No async runtime and no concrete technology types (tokio, reqwest, sqlite) may appear.
- `crates/core` (planned) — kernel core: turn loop, scheduling, context governance. Depends only on contracts.
- `crates/adapters/*` (planned) — concrete port implementations (SQLite/JSONL storage, HTTP providers, process execution).
- `crates/plugins/browser` (planned) — the built-in Browser Use plugin, attached through the tool-call boundary.
- `crates/app` (planned) — composition root: the only place allowed to see both the kernel and its adapters.

These dependency rules are compiler-enforced; violating them is an architecture breach (this repo's equivalent of a safety boundary):

- The kernel core depends only on contracts and never imports adapters.
- Adapters never import the kernel core.
- Concrete technology types live only in adapters and the composition root — not even a type name may leak into contracts.

## Key Invariants of the Contracts Layer

Before touching `crates/contracts`, confirm the following semantics hold (`tests/ports.rs` locks some of them; a failing regression test is not license to "update the assertion" — first determine whether the contract itself was broken):

- Event and command JSON shapes (`type` / `cmd` tag names) are defined by the regression tests in `crates/contracts/tests/ports.rs`.
- Every tool_use has a paired tool_result — cancellation and timeout must also produce a paired result (`ToolStatus::Cancelled` / `TimedOut`).
- Dropping the model stream cancels the upstream request (a ModelProvider adapter obligation).
- Re-invoking `append_turn` for the same turn returns `StoreErrorKind::Conflict` (idempotent dedup for crash recovery).
- `ProviderError.emitted_output` decides whether the core may retry transparently (the retry rule beyond the visibility boundary).
- Port methods use hand-rolled `BoxFuture` / `BoxStream` (object safety), not async-trait; port methods taking multiple references must spell out explicit `<'a>` lifetimes.
- Naming: module names are domain nouns (`storage`); trait names carry a role suffix (`SessionStore`).

## Code Style

- Comments appear only where code cannot be self-explanatory, and state constraints — never what the next line does; no large explanation blocks.
- contracts keeps `#![forbid(unsafe_code)]`; unsafe added to any crate must be justified at the code site.
- All comments, rustdoc, and documentation in English.
- No emoji.

## Workflow

- Build and lint: `cargo clippy --all-targets` (zero warnings is the baseline)
- Formatting: `cargo fmt` (must be clean before committing)
- Tests: `cargo test` (currently concentrated in `crates/contracts/tests/ports.rs`; for targeted debugging use `cargo test -p alcoler-contracts <test_name>` instead of repeatedly running the full suite)
- Report every test failure; you may suspect a failure is unrelated to your change, but you must verify before concluding so.
- Never commit unless explicitly asked; when asked, never add yourself as co-author.

## Changelog

No CHANGELOG and no standalone documents are maintained; this file is the only long-term maintained document.
