# S2ModKit user guide

S2ModKit is a Deadlock mod manager with a desktop app and a command-line toolkit.
Start with [the desktop app](#7-desktop-app-preview-and-mod-list) for the mod list,
model preview and installation. Features vary by version; see your release notes.
The sections below also document the CLI for scripts and advanced model work.

## Before you begin

Geometry shaping and Blender position imports are currently CLI-only. They require explicit source
selections and parameters; the app does not automatically select or reshape body parts or fit clothing.
The [automation guide](AUTOMATION.md#rules-for-scripted-workflows) describes advanced command workflows.
Offline verification does not establish live appearance or animated fit.

Use a verified Release build of the CLI and keep project data under an ignored workspace. Do not
place game assets, generated VPKs, or reports in Git.

```powershell
$s2mod = '.\s2mod.exe'
$workspaceRoot = '.\.workspaces'

# Optional: the release ships meshoptimizer.dll next to s2mod.exe for transforms; set this only to use another build.
# $env:S2MODKIT_MESHOPTIMIZER_PATH = '<path-to-meshoptimizer.dll>'

# Optional external package verification.
$env:S2MODKIT_SOURCE2_VIEWER_PATH = '<path-to-Source2Viewer-CLI.exe>'
$env:S2MODKIT_EXTERNAL_VERIFY_ROOT = "$workspaceRoot\external-verify"
```

Run `& $s2mod doctor --format json` to check the configured adapter, geometry codec, and external
verifier. A missing codec blocks transform work but not draw-call removal. An external verifier is
supplemental unless a package command includes `--require-external`.

The interactive terminal workflow (`s2mod interactive`) was retired in 0.4.0. Every step is available
through the non-interactive commands in this guide and in the [CLI reference](CLI-REFERENCE.md). A
desktop interface for people is planned; it is not part of this release.

Only the `addons install`, `addons install-current` and `addons rollback` commands can change a game
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
yet scaffolded, so treat this as an expert recipe path and never copy IDs between input revisions.

### Experimental whole-part and region editing

These are separately opted-in visual edits, not a fallback when a strict operation rejects.
They preserve unresolved sphere, distance-field and collision metadata without proving that it
still matches the edited shape. Rendering, clothing fit and gameplay need a separate player test.

Use `& $s2mod components list --project $project --experimental --format json` for scripted discovery.
Candidate IDs from strict discovery cannot be reused in experimental discovery.
Current experimental discovery uses schema 4.

Scaffolding with `--experimental --intent uniform-scale` creates a whole-part recipe. With
`--experimental --intent region-scale`, also supply `--region-axis`, `--pinned-through`,
`--full-from`, `--pivot point` and `--pivot-point`. Both require a scale and displacement cap
(at most 64 model units). Region thresholds are model-space coordinates, not anatomical labels.
The lower half-space stays unchanged; the transition blends to full scale above the upper threshold.
Run the ordinary plan/build/verify/package workflow. Actual parameters may reject even when
discovery's small eligibility probe passed. Procedural hair and cloth are not enabled by opt-in.

For an available **Experimental local ellipsoid scale** action, use `--intent ellipsoid-scale`
with explicit `--field-center X,Y,Z`, `--field-radii X,Y,Z`, `--core-fraction`, `--scale` and
`--max-displacement`. Center and radii are model-space coordinates; core fraction sets the inner
full-strength radius relative to the outer ellipsoid. Influence falls smoothly to zero at the outer
boundary. The center also anchors scaling; independent pivot and translation options reject.
The field must pass the Jacobian, displacement and all-LOD geometry gates. Nothing identifies
head/arm/chest anatomy automatically. Generate `selection-preview` before building.

For paired shaping, use `--intent mirrored-ellipsoid-scale` with the same field arguments plus
`--mirror-axis` and `--mirror-coordinate`. An explicit model-axis plane reflects the center;
the outer supports must be disjoint and both sides must change geometry in every LOD. Original
asymmetry is retained.
For several ordinary buffers under one common field, first use
`components list --project $project --experimental --coordinated --format json`. This returns
schema-5 candidate IDs. Select exact material/lineage views; overlapping views deduplicate draw calls.
The scaffold requires unique complete-buffer correspondence in every LOD. It cannot infer that
selecting a head includes hair, eyeballs or accessories. A candidate's individual reject-policy
probe may fail while an explicitly acknowledged complete union passes its separate exact probe.

Save a complete typed options document (schema example, not a model-specific preset):

```json
{
  "schemaVersion": 1,
  "kind": "coordinated_field_options",
  "policy": { "kind": "preserve_unverified", "version": 1 },
  "zeroBoneBoxPolicy": { "kind": "reject", "version": 1 },
  "zeroRenderSpherePolicy": { "kind": "reject", "version": 1 },
  "field": {
    "kind": "axis_ramp", "version": 1, "coordinateSpace": "model",
    "axis": "z", "pinnedThrough": 30, "fullFrom": 50,
    "pivot": { "x": 0, "y": 0, "z": 50 }, "uniformScale": 1.1
  },
  "maximumDisplacement": 16
}
```

Choose field parameters from inspected geometry. Rejection remains the normal zero-field policy;
separately named zero-box/paired-zero-sphere preservation is experimental and does not establish
containment or runtime behavior. See [the options contract](../schemas/coordinated-field-options.schema.json).

```powershell
& $s2mod recipe scaffold --project $project --experimental --intent coordinated-field `
  --component 'cmp_0123456789abcdef01234567' --component 'cmp_111111111111111111111111' `
  --coordinated-options "$workspaceRoot\recipes\common-field-options.json" `
  --output "$workspaceRoot\recipes\coordinated.json"
& $s2mod selection-preview --project $project --recipe "$workspaceRoot\recipes\coordinated.json" `
  --output-root "$workspaceRoot\previews"
```

Replace the example IDs with current schema-5 IDs. The recipe is published only after exact
common-field planning passes; unrelated scale/pivot parameters are forbidden. The sheet shows every
participating buffer, pinned records and excluded siblings, with original/predicted views in every
LOD. Its summary lists exact draw-call/material coverage. This is a bind-space diagnostic, not
proof of animations, garment fit or live culling.

After review, use the ordinary plan/build/verify/package lifecycle.

### Protected directional and paired shaping (scripted workflow)

Advanced region shaping uses the `directional` or `paired` commands. See
[CLI automation](AUTOMATION.md) for source inspection, exact options, scaffolding and matched previews. The paired workflow uses operation 10 / recipe 11 / plan 7 /
evidence 12 and requires explicit disjoint fields, protected records and compatibility policies.
Structural discovery is not anatomical selection or admission: authored options must pass the
exact planner with complete all-LOD members before building. Review front/side silhouettes and
actual regional measurements; a core scale factor is not whole-part growth.

One exact installed Wraith chest candidate has accepted owner appearance feedback after rollback.
Other sources, animations, garment fit, LOD transitions, culling and collision remain unqualified.
Serialized procedural inputs are preserved without proving simulation independence. Use the
ordinary build/verify/minimal-package lifecycle and the guarded `addons install-current` route
with the current game archive and separate installation authorization. Stale source/dependencies
reject; an old successful preflight report cannot authorize a later installation.

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

For a reviewed minimal candidate imported from the game archive, scripts use
[`addons install-current`](CLI-REFERENCE.md#s2mod-addons-install-current) with the exact reviewed
package hash and current base archive. It compares the model and every imported dependency inside
the installation workflow, reverifies the candidate and rereads source at handoff. A mismatch or
read failure stops before addon writes. Its receipt is under `result.installation.receipt`.
It records a source snapshot and does not lock against later updates. The existing installation
route below retains its meaning for earlier workflows and other explicitly authorized sources.

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

<a id="7-desktop-app-preview-and-mod-list"></a>

## 7. Desktop app (mod manager, preview, parts and colours)

This section describes the current development build (0.8.0 in progress). Released downloads may have fewer controls;
the **Shape** tool is new.

`S2ModKit.exe` (next to `s2mod.exe`) manages every mod in Deadlock's `citadel/addons` folder and previews it without
starting the game. It needs a GPU with OpenGL 4.6. Changes to the addons folder are refused while the game runs.

1. **Install and start.** Run `S2ModKit-<version>-setup.exe` and choose where to install it, or unzip the portable
   zip anywhere you can write. `S2ModKit.exe` finds Deadlock in the usual Steam folders; otherwise use
   **Settings**, **Game: Change...** and pick the `game` folder (the one with `citadel`). An installed app keeps its
   settings, journals and caches in `%LocalAppData%\S2ModKit` (**Settings**, **Data: Move...** moves them, for example
   to another drive); the portable copy keeps them in `data` beside the app. On start the app asks GitHub whether a
   newer version exists and offers **Update now** (installed app) or the release page (portable copy); turn this off
   under **Settings**.
2. **The list.** **All mods** shows every mod (one entry per mod, also when Deadlock Mod Manager split it into several
   files), sorted by name, date added or load order. **By hero** groups mods under each hero (heroes are read from the
   game's own data), plus Objects, World, Interface and Other; every group is folded until clicked (the group of the
   shown mod opens), and a hero without mods shows the game's own model. Filter by
   name, hero or kind (model, animation, sound, UI, particles). Names, heroes and dates from Deadlock Mod Manager and
   HELLCAT are read, never written.
3. **Manage.** **Enable** and **Disable** rename the mod's files in place (nothing is moved or copied); **Delete** sends
   them to the Recycle Bin; **Install mod...** (or dropping a file on the window) installs a `.vpk`, `.zip`, `.7z` or
   `.rar`, asking which variant when an archive has several; the new mod is selected, and a note names the enabled mods
   it shares files with. **Show in folder** (and **Settings > Open addons folder**) open Explorer. Rarely used actions
   (Delete, load order) are under **More**; a mod with versions switches with its **Version** list. **Details** next to the
   mod's summary shows its files and model.
4. **Conflicts and load order.** A badge marks mods that replace the same game files; the warning next to the mod's
   summary opens the panel listing the shared files
   and which mod wins (the lower pak number). Two model mods for one hero may both be enabled; the app warns that only
   one shows. Enabling a mod that would replace the same files as an enabled mod asks first. **More > Load earlier / Load
   later** moves a mod in the load order (sort by **Load order** to see it); the app renumbers the files.
5. **After a game update.** A banner appears when Steam's build changes. **Check mods** compares each mod with the game
   (files it replaces, references and bones) and marks mods that work, are worth a look, can be repaired or need their
   author; the list then shows only the mods that are not marked as working (kind filter **Flagged by Check mods**; click one for the reasons).
   **Repair** makes a fixed copy and keeps the original as a disabled old version; nothing is disabled by
   itself.
6. **Preview.** Tabs above the main area show the mod's **Preview**, **Sounds** and **Files** (tabs without content
   are hidden). A model mod opens in the game's hero-select lighting with the ink outline:
   - **Show**: this mod, the game's model, or another mod for the same hero; **Include my enabled mods** also loads
     what the game loads beneath it.
   - Animation: filter and pick a clip, pause, drag the timeline. Camera: left drag orbits, right or middle drag pans,
     the wheel zooms; Front, Side, Back, Below and Close-up.
7. **Part tools.** Beside the preview, the picked part sits above three tabs; hovering a part in the list highlights it,
   clicking the model picks the part under the cursor.
   - **Parts**: untick parts to hide them.
   - **Colour**: recolor the picked part live: hue shift, saturation, brightness, or **Paint a colour** with the picker
     (black and white work; shading is kept) and its strength. For clothes painted on the same texture as skin, tick
     **Only colours like**, **Pick on model** (Esc cancels) and set the tolerance. No Valve tools are needed.
   - **Shape**: change the picked part's width, height and depth (**Keep proportions** locks them together), move it and
     tilt, turn or roll it around its centre, base or top, live in the preview. Any part with its own vertices can be
     reshaped: clothes, hair, bodies and weapons of the game's heroes and of most mods; a refused part says why in plain
     words. Hit boxes stay as they were, so a much bigger part is still hit where the original was.
8. **Build edited mod.** The bottom of the part tools lists the changes; **Cancel all** drops them and **Build edited
   mod** installs one edited version with every hidden, recolored and reshaped part (the original stays as a disabled
   version; for the game's own model, a new small mod). Reshaped parts move in every level of detail, and the size
   data the game uses for drawing follows them; the build checks the result by reading it back.
9. **Sounds.** The **Sounds** tab (a sound-only mod opens on it) lists the sound events it
   changes or adds and the sound files it ships. **▶ Mod** plays the mod's sound and **▶ Game's** the game's
   original, to compare; an event plays its sounds in turn on repeated clicks. Nothing is changed.

10. **Browse GameBanana.** **My mods | Browse GameBanana** at the top switches to Deadlock mods on GameBanana: newest,
    recently updated, most liked or most viewed, by hero or category, or GameBanana's own search. Mods with content
    ratings stay hidden unless **Show mods with content ratings** is ticked. A mod's page shows its screenshots, files
    (size, date, downloads and GameBanana's virus scan), description and credits; **Install** downloads a file, checks
    it against the size and checksum GameBanana lists and installs it like a dropped file (an archive with several
    variants asks which). **More > Open on GameBanana** opens the page of a mod that came from there (installed here or
    by Deadlock Mod Manager).
11. **New files on GameBanana.** Once a day on start (with **Settings > Look for app updates and new mod files on start**) or with **Settings > Look for
    mod updates on GameBanana**, the app asks GameBanana about every installed mod that came from it: a file uploaded
    after the one installed here, or after Mod Manager's download, marks the mod **new file** (kind filter **New files on
    GameBanana**). **Update...** shows the file and its description first (a new file can be an update or an optional
    extra), then installs it as a new version of the mod; the current files stay as a disabled version.
12. **Profiles.** **Profiles > Save enabled mods as a profile...** keeps the enabled mods and their load order under a name
    (for example "Ranked" and "Fun"). Picking a profile in the menu shows what it disables and enables, then renames the
    mods in place in one step, as Enable and Disable do; nothing is deleted. Mods of the profile that were removed since are
    named and skipped. A newly installed mod is in no profile until it is saved again.
13. **Ability buttons.** Over a hero's preview, buttons 1-4 play that hero's ability animations once, every phase in order
    (for example charge, then strike; or hook, flight, then attack), then the preview returns to the animation it showed before. The abilities and their animations come from the game's own data; an ability
    whose animation the model does not have gets no button.
14. **Effects.** The preview's **Effects** tab lists the hero's effects by ability 1-4, Weapon and Other, each with its main
    colours. Tick abilities and pick a colour: the squares show the colours now and, after the arrow, after the change.
    Colours keep their brightness (glows stay glows); **Exactly this colour** makes them flat. **Build edited mod** makes the
    version. Effects shared by every hero are never changed.
15. **Material colours.** A part's recolor also turns its material's colours to the same hue: the see-through glow of the
    cartoon shading, the outline colour and coloured tints (white and grey settings stay). A purple glowing cat painted
    yellow glows yellow, as in the community's best skin mods. Parts that share a material share these colours.
16. **Re-apply edits.** Every **Build edited mod** saves its edits (hidden parts, colours with their colour range, shapes,
    effect colours) and the game version beside the built mod. After a Deadlock update, a banner lists the edited mods made
    for the previous version; **Re-apply edits** builds them again on the current game files (or the current file of the mod
    they were made from) and installs each as a new version; the old one stays as a disabled version. One mod at a time:
    **More > Re-apply edits**. For an edit of someone else's mod the app says that the original may still need its
    author's update. Mods built before this feature have no saved edits.
17. **Move to slot.** **More > Move to slot...** puts a mod at a pak number from 1 to 99 (lower numbers load earlier and win
    shared files). When another mod has that number, the app offers to swap the two. Mods split over several archives move
    one place at a time with **Load earlier** / **Load later**. The 3D preview opens with **Show 3D preview** (so switching
    mods stays quick and no mod file is held open); **Settings > Open the 3D preview automatically** opens it at once.
18. **Log.** Each session writes a log in the data folder (`logs`, the last five are kept): the app's version and folders,
    the messages it showed and error details. **Settings > Open log** shows the file, **Copy log** copies it for a problem
    report. Nothing is sent anywhere.
19. **Shine.** The colour panel's **Shine** slider lowers the selected part's roughness, so it reflects its surroundings like
    latex or polished metal (0 leaves the part as the game has it). The preview shows it at once; **Build edited mod** writes
    it into the part's normal-roughness texture (only where the part's UVs are) and Re-apply edits keeps it.
20. **Separate meshes.** A part drawn as several separate meshes (a belt with a jacket, a weapon's grip and blade) lists its
    larger meshes under it as 3.1, 3.2 and so on. Each takes its own colour; the part's colour no longer reaches a mesh that
    has one. Clicking the model picks the mesh. Each also takes its own shine; hiding and shapes stay on the whole part.
21. **Settings window.** **Settings** opens one window with every rarely used choice: the game and data folders, the addons
    folder, refreshing the list, GameBanana updates, hidden conflicts, the preview (open automatically, 60 or 30 fps), app
    updates (on start or now, with the app's version) and the log. Choices are saved as they change.

Previews are offline evidence: they are close to the game but not a game test.

## 8. Recolor (texture-only mods)

`s2mod recolor` changes the colours of textures and writes a small new mod; the original mod and the game files are
never changed. No Valve tools are needed: the new colours are written into a copy of each texture in its own format.

```powershell
s2mod recolor textures --game <Deadlock game folder> --addon <mod VPK> --model models/heroes_staging/hornet_v3/hornet.vmdl
s2mod recolor apply --game <Deadlock game folder> --recipe red.json --output <new folder>
```

A recipe names each part's texture (from `recolor textures`), an optional mask image in that texture's space, and the
change (`hueShift`, `saturation`, `brightness`, `tint`, `colorize` with strengths); see the
[CLI reference](CLI-REFERENCE.md#s2mod-recolor-apply). The game loads the lower pak number first: install the recolor
in a lower slot than the mod it recolors, or merge both with `s2mod addons merge --prefer recolor_dir.vpk`.

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

## Scripted workflows

For a scripted run, start from this guide, [CLI-REFERENCE.md](CLI-REFERENCE.md) and
[AUTOMATION.md](AUTOMATION.md), with the exact project root, reviewed catalogue and base VPK, and the desired
high-level change. Use `catalog resolve` to map the player-facing name to a verified locator; the
locator remains navigation data rather than a geometry hint. List components,
scaffold rather than hand-author selectors, run `plan`, and accept a structured unsupported result
as valid.
