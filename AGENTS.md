# Alcoler

Alcoler is a from-scratch coding agent harness, built kernel-first. This file is the single source of truth for agent instructions and the only document maintained in this repository.

## Code Style

- Comments appear only where code cannot be self-explanatory, and state constraints — never what the next line does; no large explanation blocks. This applies to every language in the repo.
- All comments, API docs, and documentation in English, regardless of language.
- No emoji.

## Workflow

- Report every test failure; you may suspect a failure is unrelated to your change, but you must verify before concluding so.
- Never commit unless explicitly asked; when asked, never add yourself as co-author.

## Memory Budget

Baseline from the kernel memory probe (`tests/Alcoler.Kernel.Tests/EndToEnd/MemorySpikeProbe.cs`): 10 resident sessions holding 850 scripted turns cost 7 MB managed / +21 MB working set over the process floor. Steady state must stay under 200 MB working set with one active session; the desktop process floor is Avalonia-dominated (~135 MB).

## Changelog

No CHANGELOG and no standalone documents are maintained; this file is the only long-term maintained document.
