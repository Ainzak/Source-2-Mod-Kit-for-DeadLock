# Contributing to S2ModKit

S2ModKit is an agent-first, CLI-first pre-alpha project. Before making a substantial change,
open an issue describing the user problem, the affected contract, and how the change can be
verified. Small documentation and test fixes can use a pull request directly.

## Boundaries

- Keep Domain and Application independent of ValveResourceFormat, machine paths, and hero names.
- Prefer reusable structural profiles and versioned operations over hero-specific branches.
- Preserve fail-closed behavior, immutable inputs, atomic publication, hashes, and evidence.
- Never commit Deadlock/Valve models, textures, VPKs, compiled resources, proprietary logs, or
  personal paths.
- Use synthetic fixtures for portable tests. Real game inputs require explicit owner permission
  and stay outside Git.
- Changes to operation, recipe, evidence, or public CLI contracts need an ADR or an update to the
  relevant accepted decision before implementation.

## Local verification

From the repository root:

```powershell
.\scripts\Invoke-Verification.ps1 -Mode all
```

The wrapper runs the Release build and the portable suite with the current 524-test baseline.
Use `git diff --check` and `dotnet format S2ModKit.slnx --no-restore --verify-no-changes` for
formatting checks. Do not commit generated `bin/`, `obj/`, coverage output, or temporary release
directories.

## Pull requests

Explain the behavior and safety boundary, include focused tests for successful and failing paths,
update public docs when the CLI changes, and call out any compatibility or schema impact. Keep
commits focused and do not mix internal engineering notes into the public export.
