# Source 2 Mod Kit for Deadlock

S2ModKit is a Windows mod manager for Deadlock with a desktop interface and a command-line toolkit.
Use **S2ModKit.exe** to browse installed mods, preview models and animations, and install or manage mods.
Use **s2mod.exe** for command-line workflows, scripts and advanced model operations.

The desktop app is the main interface. Geometry shaping and Blender position-import workflows are
currently available through the CLI only. Routine mod management does not require an AI assistant.

This is pre-alpha community software, independent of Valve. Game assets are not included.

## Desktop app

- Every mod in the Deadlock addons folder in one list or grouped by hero; filter by name, hero or kind.
- Enable, disable and delete any mod (renamed in place, nothing copied); install from VPK, zip, 7z or rar.
- See which mods replace the same files and which one wins; change the load order.
- After a game update, check every mod and repair the ones that only need stale files removed.
- Preview models with animations and compare a mod with the game's model.
- Hide parts of a model, recolor parts with a live preview, and build the edited copy; no Valve tools needed.
- Play a sound mod's sounds next to the game's originals.
- Installer with a free choice of folder, and new versions offered in the app.

## Getting started

Download the [latest Windows release](https://github.com/Ainzak/Source-2-Mod-Kit-for-DeadLock/releases/latest):
run the setup program and choose where to install it (the app then offers new versions itself), or extract the
portable zip anywhere you can write. The app looks for Deadlock through Steam; use **Settings > Game folder...** if it
cannot find the installation. The 3D preview needs OpenGL 4.6.

See [the desktop guide](docs/USER-GUIDE.md#7-desktop-app-mod-manager-preview-parts-and-colours) for the app,
[the CLI reference](docs/CLI-REFERENCE.md) for commands, and
[the compatibility matrix](docs/COMPATIBILITY.md) for model-editing limitations.
The [automation guide](docs/AI-USAGE.md) is optional reference for scripts and coding assistants.

## Command-line toolkit

The CLI provides archive and model inspection, recipes, build verification, packaging and addon
workflows. Advanced geometry operations require explicit selections and parameters; they do not
automatically infer body parts or fit clothing. They remain available for technical users, with
no geometry-shaping controls in the desktop app at present.

<details>
<summary>Advanced geometry operations and compatibility limits</summary>

### Geometry and authoring commands (CLI only)

- Remove complete draw-call components across every present LOD.
- Uniformly scale and/or translate exclusively owned vertex components.
- Apply bounded per-axis scale or rotation around a typed pivot on characterized vertex buffers.
- Transform the supported embedded visual/convex-physics accessory profile atomically.
- Transform explicitly selected disconnected triangle islands inside a broader draw call.
- Opt into experimental complete-buffer scaling or axis-ramp region scaling on eligible parts.
- Shape an eligible buffer with a local ellipsoid or disjoint mirrored pair, or apply one common
  field atomically to several eligible ordinary buffers.
- Author experimental protected directional fields or disjoint pairs with source-backed CLI
  commands and matched region/silhouette previews across every LOD.
- Preview selected and excluded geometry across every LOD before building.
- Inspect component candidates and scaffold recipes without publishing a mutation.
- Export Blender geometry, validate all-LOD position returns and build independently verified
  existing-mesh sculpts through a bounded import contract. See [position authoring](docs/POSITION-AUTHORING.md).
- Build, verify, package, explicitly install, observe, and receipt-rollback owned addons.

Support is structural, not hero-name based. Unknown layouts, incomplete LOD coverage, ambiguous
selectors, shared ownership, input drift, and unexplained resource changes fail closed. Affine
transforms require a reported version-4 capability and independently verified bounds; they do not
enable topology edits, morph edits, or arbitrary vertex selection.

Experimental editing requires `--experimental` discovery and an explicitly acknowledged recipe.
It verifies prescribed geometry, conservative box expansion and unchanged data, but does not qualify
sphere containment, occlusion proxies or collision correspondence. It is not anatomical sculpting:
hair, clothing and animation may not follow an enlarged part. See the compatibility matrix before
using an experimental edit.

</details>

### CLI quick start

```powershell
$s2mod = '.\s2mod.exe'
& $s2mod doctor --format json
& $s2mod catalog heroes list `
  --catalogue '.\catalogues\deadlock-current.json' `
  --source-vpk '<Deadlock game>\citadel\pak01_dir.vpk'
```

JSON output supports scripts and other tools. The CLI is non-interactive;
the former `s2mod interactive` terminal mode was retired in 0.4.0.

## Building from source

Requires the .NET SDK version pinned by `global.json`.

```powershell
dotnet restore S2ModKit.slnx
dotnet build S2ModKit.slnx --configuration Release
dotnet test --solution S2ModKit.slnx --configuration Release --no-build
```

For contributor checks, use `scripts/Invoke-Verification.ps1`. The optional native meshoptimizer
codec ships with the release (`meshoptimizer.dll` next to `s2mod.exe`; `S2MODKIT_MESHOPTIMIZER_PATH` overrides it); removal and inspection do not require it.

## Safety and scope

Imported inputs are never overwritten. Game-directory changes require explicit user authorization and
use the addon management commands and their verification checks. Offline verification is not proof that Deadlock
will render or behave correctly; live player testing remains a separate step.

## License

S2ModKit is released under the MIT License. Dependency notices are in
[THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES), with the shipped runtime inventory in
[THIRD-PARTY-SBOM.json](THIRD-PARTY-SBOM.json).

Security reports and contribution guidance are in [SECURITY.md](SECURITY.md) and
[CONTRIBUTING.md](CONTRIBUTING.md).
