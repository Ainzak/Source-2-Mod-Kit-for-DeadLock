# Source 2 Mod Kit for Deadlock

S2ModKit is a Windows CLI for deterministic, inspectable edits to Deadlock/Source 2 compiled
character models. It discovers structural components, creates a typed recipe, rewrites only a
verified selection, reopens and verifies the result, and can package it as a minimal VPK.

This is pre-alpha software. It is independent community software and is not affiliated with Valve.
It does not distribute Deadlock assets, models, textures, VPKs, or compiled game resources.

## Supported operations

- Remove complete draw-call components across every present LOD.
- Uniformly scale and/or translate exclusively owned vertex components.
- Transform the supported embedded visual/convex-physics accessory profile atomically.
- Transform explicitly selected disconnected triangle islands inside a broader draw call.
- Inspect component candidates and scaffold recipes without publishing a mutation.
- Build, verify, package, explicitly install, observe, and receipt-rollback owned addons.

Support is structural, not hero-name based. Unknown layouts, incomplete LOD coverage, ambiguous
selectors, shared ownership, input drift, and unexplained resource changes fail closed. Rotation,
anisotropic scaling, topology edits, morph edits, and arbitrary partial regions are not supported.

## Quick start

The easiest route is the guided workflow. It prompts for paths when options are omitted:

```powershell
$s2mod = '.\s2mod.exe'
& $s2mod interactive
```

For an explicit run:

```powershell
& $s2mod interactive `
  --catalogue '.\catalogues\deadlock-current.json' `
  --session '.\guided-session.json' `
  --base-vpk '<Deadlock game>\citadel\pak01_dir.vpk'
```

The workflow is read-only until an explicit install confirmation. It prints a receipt-backed
rollback command after installation. The catalogue contains portable logical paths only; the local
VPK path is always supplied by the user.

See [the user guide](docs/USER-GUIDE.md), [the CLI reference](docs/CLI-REFERENCE.md),
[the AI usage guide](docs/AI-USAGE.md), and [the compatibility matrix](docs/COMPATIBILITY.md).

## Building from source

Requires the .NET SDK version pinned by `global.json`.

```powershell
dotnet restore S2ModKit.slnx
dotnet build S2ModKit.slnx --configuration Release
dotnet test --solution S2ModKit.slnx --configuration Release --no-build
```

For contributor checks, use `scripts/Invoke-Verification.ps1`. The optional native meshoptimizer
codec is configured through `S2MODKIT_MESHOPTIMIZER_PATH`; removal and inspection do not require it.

## Safety and scope

Imported inputs are never overwritten. Game-directory changes require explicit user authorization and
are limited to the receipt-backed addon lifecycle. Offline verification is not proof that Deadlock
will render or behave correctly; live player testing remains a separate step.

## License

S2ModKit is released under the MIT License. Dependency notices are in
[THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES), with the shipped runtime inventory in
[THIRD-PARTY-SBOM.json](THIRD-PARTY-SBOM.json).
