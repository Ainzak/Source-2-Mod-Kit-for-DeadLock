# S2ModKit user guide

S2ModKit is a safety-first command-line workbench for narrowly scoped Deadlock compiled-model
changes. Its supported lifecycle is:

`import -> discover -> scaffold recipe -> plan -> build -> package -> explicit install -> player observation -> rollback`

This guide explains the implemented path. It does not promise that every visual request is
possible.

## Before you begin

Use a verified Release build of the CLI and keep project data under an ignored workspace. Do not
place game assets, generated VPKs, or reports in Git.

```powershell
$s2mod = '.\s2mod.exe'
$workspaceRoot = '.\.workspaces'

# Required for transform discovery, planning, and rewriting.
$env:S2MODKIT_MESHOPTIMIZER_PATH = '<path-to-meshoptimizer.dll>'

# Optional external package verification.
$env:S2MODKIT_SOURCE2_VIEWER_PATH = '<path-to-Source2Viewer-CLI.exe>'
$env:S2MODKIT_EXTERNAL_VERIFY_ROOT = "$workspaceRoot\external-verify"
```

Run `& $s2mod doctor --format json` to check the configured adapter, geometry codec, and external
verifier. A missing codec blocks transform work but not draw-call removal. An external verifier is
supplemental unless a package command includes `--require-external`.

For guided source and hero selection, create a local checkpoint and configure one or more sources:

```powershell
$session = "$workspaceRoot\guided-session.json"
& $s2mod interactive `
  --catalogue '<path-to-hero-catalogue.json>' `
  --session $session `
  --base-vpk '<Deadlock game\citadel\pak01_dir.vpk>' `
  --mod-vpk '<optional-mod_dir.vpk>' `
  --compiled-model '<optional-model.vmdl_c>'
```

For the simpler prompted setup, run only:

```powershell
& $s2mod interactive
```

The CLI asks for the session path, catalogue, source type, and source path. Selecting an existing
session path in this prompted mode resumes it automatically.

The guided workflow saves the exact source, hero, resource, available component/action, bounded
parameters, canonical recipe, and dry-run facts. It then offers recipe/plan-only, verified build,
minimal package, or atomic VPK export. Packaged results may remain uninstalled or use an automatic
or explicit verified addon slot. The CLI shows the exact destination and requires the exact word
`install`, verifies the active receipt, prints a runnable rollback command, and offers immediate
rollback or a player-test hold. Enter `cancel` to pause safely; completed artifacts remain available
on resume. Resume with the command printed by the CLI, or with
`& $s2mod interactive --catalogue '<same-catalogue>' --session $session --resume`. Add `--expert`
to show IDs, paths, and unavailable-locator details. Existing non-interactive commands below remain
the authoritative automation surface.

At resource and component menus, choose the printed `0` entry to go back. `back` and `previous`
remain accepted keyboard aliases. Returning from components lets you select another resource and
then another hero without starting a new guided session. Imported resources use separate immutable
internal workspaces and are reused if selected again. Navigation itself reuses the in-memory
catalogue selection. Opening components for a resource not yet visited imports only that model and
its dependencies; it does not scan every numbered VPK payload.

The default guided view needs only keyboard input and numbered choices; it does not reveal or ask
for component/draw-call IDs. Invalid entries explain the accepted range and repeat the prompt.
Plain sequential output remains usable when redirected or wrapped by a narrow terminal. Agents can
read the documented project, recipe, build, and package fields from the saved session and hand them
directly to the ordinary `plan`, `verify`, and `package verify` commands.

Only guided installation and the `addons install`/`addons rollback` commands can change a game
addon root, and installation requires explicit player authorization. All other normal workflow
commands write only under their project root or an explicitly selected export path.

## Core terms

| Term | Meaning |
|---|---|
| Project | Immutable imported input and dependency provenance below a chosen root. |
| Candidate | A deterministic mechanical view: either every draw call with one exact material (`material_group`) or one exact source-authored whole-mesh lineage across all present LODs (`mesh_lineage`). It is not a semantic object claim. |
| Recipe | Versioned declaration of an existing supported mutation. |
| Plan | Dry-run that independently resolves a recipe; it creates no model bytes. |
| Build | Rewritten model that passed reopen and invariant checks. |
| Package | Deterministic VPK containing a verified build. New vanilla tests normally use one-entry `minimal` packages. |
| Installation receipt | Record that authorizes exact-hash verification and recoverable rollback of one owned addon archive. |
| Runtime observation | Separate immutable player evidence; it never upgrades older offline evidence. |

See [CLI-REFERENCE.md](CLI-REFERENCE.md) for exact arguments and `schemas/` for the machine
contracts.

## 1. Import a model

If you have a reviewed hero catalogue, resolve a player-facing name before import. This reads only
base-VPK directory metadata and does not extract model payloads:

```powershell
& $s2mod catalog heroes list `
  --catalogue '<path-to-hero-catalogue.json>' `
  --source-vpk '<Deadlock game\citadel\pak01_dir.vpk>'

& $s2mod catalog resolve `
  --catalogue '<path-to-hero-catalogue.json>' `
  --source-vpk '<Deadlock game\citadel\pak01_dir.vpk>' `
  --hero haze
```

Use the resolved primary or accessory locator as `project create-vpk --entry`. A stale catalogue or
ambiguous/moved/missing resource fails with exit category 10 instead of guessing.

To scan every resource listed in that catalogue without creating projects or model candidates:

```powershell
& $s2mod compatibility scan `
  --catalogue '<path-to-hero-catalogue.json>' `
  --source-vpk '<Deadlock game\citadel\pak01_dir.vpk>' `
  --output-root "$workspaceRoot\compatibility-reports"
```

The default output is concise. The command writes a strict JSON report with exact identities and a
short Markdown summary below `--output-root`. Add `--compare <prior-report.json>` to list stable
resource IDs that were added, removed, changed, or unchanged.

### Vanilla model from a base VPK

The base VPK is read-only. The selected model is copied into the project object store.

```powershell
$project = "$workspaceRoot\my-hero"
$baseVpk = '<Deadlock game\citadel\pak01_dir.vpk>'
$entry = 'models/heroes_wip/fencer/fencer.vmdl_c'

& $s2mod project create-vpk `
  --root $project `
  --base-vpk $baseVpk `
  --entry $entry
```

For a reproducible source check, add `--expect-directory-sha256 <sha256>` for the small
`*_dir.vpk` file. Never write into the base VPK or its chunks.

### Existing compiled model

Use filesystem import only when the model and owned dependencies are available under a resource
root:

```powershell
& $s2mod project create `
  --root $project `
  --input '<path-to-model.vmdl_c>' `
  --resource-root '<root-containing-owned-resources>' `
  --runtime-resource-root '<optional-read-only-base-resource-root>'
```

`--runtime-resource-root` describes dependencies supplied by the game rather than copied as owned
project inputs.

## 2. Inspect and discover candidates

```powershell
& $s2mod inspect --project $project --format text
& $s2mod components list --project $project --format text
& $s2mod components list --project $project --format json
```

For each candidate, use the stable `cmp_...` ID, inspect its kind, exact materials, LOD membership,
and capability/reason output. A `mesh_lineage` also reports its mechanical source label and exact
resource path, mesh ordinal, block, source name, and draw calls per LOD. Source and material labels
are authored hints, not universal semantic claims; for example, `_l` is not interpreted as “left.”

Candidate kinds are overlapping views, not a partition. If one visual object has several materials,
or if combining material and lineage views is intentional, explicitly pass every desired candidate
to one scaffold command. The tool remaps current membership, deduplicates identical draw calls, and
recomputes ownership and transform capability over the exact union. Never assume that individually
available or unavailable candidates imply the union result.

## 3. Scaffold a recipe

Scaffolding rediscovers the project, rejects stale candidate IDs, derives draw-call IDs and LOD
expectations, validates the current recipe contract, and writes a new canonical JSON file. It does
not create a plan or a model, and it never overwrites `--output`.

### Remove a component

```powershell
$recipe = "$workspaceRoot\recipes\remove-accessory.json"

& $s2mod recipe scaffold `
  --project $project `
  --component 'cmp_0123456789abcdef01234567' `
  --intent remove `
  --output $recipe
```

This emits `remove_component@1`. It removes complete draw-call records but deliberately retains
vertex and index payloads, even if some data becomes unused.

### Uniformly scale one component or an explicit union

```powershell
$recipe = "$workspaceRoot\recipes\scale-weapon.json"

& $s2mod recipe scaffold `
  --project $project `
  --component 'cmp_blade-id-from-components-list' `
  --component 'cmp_gem-id-from-components-list' `
  --intent uniform-scale `
  --output $recipe `
  --scale 1.5 `
  --reference-lod 0 `
  --max-displacement 96
```

`--scale` must be in `0.25..4.0`; `--max-displacement` must be greater than zero and at most `256`.
Numbers always use a decimal point (`1.5`, not `1,5`), regardless of Windows locale. This legacy
uniform operation uses the selection center at the reference LOD.

### Scale individual axes or rotate

When `components list` reports an available `transform_component@4` capability, scaffold an affine
recipe. The command checks the exact parameters with the ordinary dry-run planner before saving it:

```powershell
$recipe = "$workspaceRoot\recipes\affine-weapon.json"

& $s2mod recipe scaffold `
  --project $project `
  --component 'cmp_id-from-components-list' `
  --intent affine `
  --output $recipe `
  --scale-x 1.6 `
  --scale-y 1 `
  --scale-z 0.7 `
  --rotate-axis '0,0,1' `
  --rotate-degrees 30 `
  --pivot selection-center `
  --max-displacement 256
```

The X/Y/Z scales are in `0.25..4.0`; unspecified axes stay at `1`. Rotation uses a unit axis in
the chosen frame and signed degrees in `(-180, 180]`. `--pivot point --pivot-point 'x,y,z'` uses an
explicit model-space point; `--pivot face --pivot-face max_z` uses a selected bounds face; and
`--pivot bone --pivot-bone name` uses an exact influencing bone origin. The options
`--frame bone-bind --frame-bone name` express the axes in one verified bind frame. The default frame
is model space.
Every LOD must satisfy the selected geometry, bounds, skinning, codec, and displacement rules.
An unavailable capability reason is a stop, not a suggestion to hand-edit around the check.
For a component that offers version 4 but not the older uniform profile, the ordinary
`--intent uniform-scale --scale 1.5` command automatically uses version 4 with equal axis scales.

If discovery reports `transform_component@2`, use the same `uniform-scale` intent and add an
independent collision ceiling:

```powershell
& $s2mod recipe scaffold `
  --project $project `
  --component 'cmp_id-from-components-list' `
  --intent uniform-scale `
  --output $recipe `
  --scale 2 `
  --max-displacement 96 `
  --max-collision-displacement 96
```

This emits recipe schema version 3 with `physicsPolicy: transform_coupled_convex`. It scales the
accepted visual and convex-collision profile atomically. Translation, visual-only fallback, and
unrecognized PHYS layouts remain unsupported.

For an accessory embedded inside a broader draw call, inspect JSON may expose stable disconnected
triangle-island IDs. Recipe schema version 4 can apply `transform_component@3` with
`granularity: connected_component_vertices`, explicit `connectedComponentIdsByLod`, and exact
per-LOD vertex totals. The implementation resolves and hashes those islands again during planning
and rewriting; it does not use hero names or coordinates. Arbitrary island-group scaffolding is not
yet interactive, so treat this as an expert recipe path and never copy IDs between input revisions.

### Translate a component

```powershell
$recipe = "$workspaceRoot\recipes\move-accessory.json"

& $s2mod recipe scaffold `
  --project $project `
  --component 'cmp_0123456789abcdef01234567' `
  --intent translate `
  --output $recipe `
  --translate-x 6 `
  --translate-y 0 `
  --translate-z -2 `
  --max-displacement 16
```

At least one translation axis is required. This legacy `translate` intent remains a position-only
uniform transform; affine scale and rotation use `--intent affine`.

## 4. Dry-run before mutation

Always run the normal planner. It independently validates the recipe against the immutable project
and freezes a plan fingerprint.

```powershell
& $s2mod plan --project $project --recipe $recipe --format text
```

Stop on a non-zero exit code. Do not hand-edit generated draw-call IDs or LOD counts to force a
plan through. Rerun `components list`, then scaffold a new recipe when identity has changed.

## 5. Build, verify, and package

```powershell
$buildResult = & $s2mod build --project $project --recipe $recipe | ConvertFrom-Json
$buildId = $buildResult.result.build.buildId

& $s2mod verify --project $project --build $buildId

$packageResult = & $s2mod package create-minimal `
  --project $project `
  --build $buildId `
  --require-external | ConvertFrom-Json
$packageId = $packageResult.result.package.packageId

& $s2mod package verify `
  --project $project `
  --package $packageId `
  --require-external
```

`create-minimal` produces one logical model entry and does not need an artificial source VPK. The
legacy `package create` mode replaces one entry in an immutable source VPK and requires its hash.

Build/package evidence is `offline_static`: it proves structural and reopen checks, not live
animation or runtime load order.

## 6. Explicit installation, observation, and rollback

First inspect addon collisions:

```powershell
$addonsRoot = '<Deadlock game\citadel\addons>'

& $s2mod addons inventory `
  --addons-root $addonsRoot `
  --project $project `
  --package $packageId
```

After explicit authorization, install and verify the receipt:

```powershell
$installResult = & $s2mod addons install `
  --addons-root $addonsRoot `
  --project $project `
  --package $packageId `
  --slot auto | ConvertFrom-Json
$installationId = $installResult.result.receipt.installationId

& $s2mod addons verify-active `
  --addons-root $addonsRoot `
  --project $project `
  --installation $installationId
```

`auto` considers slots `90..99` and refuses unsafe priority/collision conditions. Installation
stages under a non-VPK name, hashes the bytes, and activates only an empty slot. It never rewrites
`.dmm.json`, another tool's VPK, or an occupied archive.

While the receipt is still active, record what the player actually tested. Copy `projectId`,
`packageId`, `installationId`, and `installedHash` from install/verify output; do not invent them.
`passed` requires all required checks to pass; use `partial` when any check is `not_checked`.

```json
{
  "schemaVersion": 1,
  "observationId": "runtime-20260913-001",
  "createdUtc": "2026-09-13T12:00:00Z",
  "proofLevel": "player_observed",
  "status": "partial",
  "projectId": "replace-with-project-id",
  "packageId": "vpk-replace-with-package-id",
  "installationId": "install-replace-with-receipt-id",
  "installedHash": "replace-with-exact-lowercase-sha256",
  "checks": {
    "targetComponent": "passed",
    "preservedMaterialsAndParts": "passed",
    "animations": "passed",
    "lodTransitions": "not_checked",
    "menuPreview": "not_checked",
    "deathAndRespawn": "not_checked"
  },
  "notes": ["Player observed the exact active package."],
  "boundaries": [{
    "name": "player_review",
    "status": "passed",
    "summary": "Observation was made while verify-active succeeded."
  }],
  "extensions": {}
}
```

```powershell
& $s2mod runtime record `
  --project $project `
  --installation $installationId `
  --observation '<path-to-observation.json>'

& $s2mod addons rollback `
  --addons-root $addonsRoot `
  --project $project `
  --installation $installationId
```

Rollback renames only the exact-hash owned VPK to a receipt-specific inactive filename. It never
deletes the candidate and refuses to touch a changed or foreign file.

## Reading failures

Expected failures contain a stable `error.code`, `error.boundary`, summary, and remediation. In
JSON mode, consume the code rather than localized prose.

| Code | Safe response |
|---|---|
| `COMPONENT_INCOMPLETE_LOD_COVERAGE` | Select a complete material union or stop. |
| `SCAFFOLD_COMPONENT_STALE` | List components again and use current IDs. |
| `SCAFFOLD_TRANSFORM_UNSUPPORTED` / `SCAFFOLD_TRANSFORM_BLOCKED` | Respect the reason; do not hand-edit a recipe. |
| `SCAFFOLD_TRANSFORM_AMBIGUOUS` | Membership or ownership cannot be proven; choose another target or stop. |
| `COMPONENT_LINEAGE_INCOMPLETE_LOD_COVERAGE` / `COMPONENT_LINEAGE_DUPLICATE_LOD` | Use another exact candidate; the tool will not guess lineage membership. |
| `MBUF_*_UNSUPPORTED` / `MBUF_INDEX_RANGE_INVALID` | The embedded geometry is outside the bounded read-only profile; do not force it through the MVTX path or attempt mutation. |
| `PHYS_*_UNSUPPORTED` | The coupled collision payload is outside the bounded one-part/one-convex-hull read-only profile; a visual-only edit of such a component is never valid. |
| `EXTERNAL_MESH_RESOURCE_UNSUPPORTED` | The model uses a separate mesh resource outside the implemented adapter profile. |
| `RECIPE_OUTPUT_EXISTS` | Choose a new recipe path; S2ModKit never overwrites it. |
| `CLI_NUMBER_INVALID` | Use finite invariant numbers such as `1.25` or `-3.5`. |
| `POST_WRITE_VERIFICATION_FAILED` | No candidate was published; inspect evidence and do not install. |

## Giving the workflow to another agent

Give a new coding agent this guide, [CLI-REFERENCE.md](CLI-REFERENCE.md), and
[AI-USAGE.md](AI-USAGE.md), plus the exact project root, reviewed catalogue and base VPK, and desired
high-level change. Use `catalog resolve` to map the player-facing name to a verified locator; the
locator remains navigation data rather than a geometry hint. The agent should list components,
scaffold rather than hand-author selectors, run `plan`, and accept a structured unsupported result
as valid.
