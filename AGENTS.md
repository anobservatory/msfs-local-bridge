# Repository Instructions

## Language

Use English for all authored repository content, including UI copy,
documentation, code comments, mockups, and preview controls.

## Verification

- Run `git diff --check` and the checks appropriate to the change. Follow
  `docs/post-format-rebuild-checklist.md` for build and package commands.
- For desktop service changes, run `tests/WorkspaceChecks`. Lifecycle fixtures
  must stay isolated from the user's Bridge, simulator, and network settings.
- For process launch or stream changes, also verify an extracted portable ZIP
  with `tests/smoke-windows.ps1`, including its synthetic worker path.
- Report synthetic telemetry separately from actual MSFS flight verification.
- Documentation-only edits do not require application test suites.
