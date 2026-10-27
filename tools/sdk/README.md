# Experimental Blender / SDK workflow

Standalone experimental agent tooling. It uses installed SourceIO and BlenderSource2Tools,
VRF, CSDK12 and CS2 Workshop Tools. It does not replace production transforms or the
unchanged-source DMX adapter. No asset, addon implementation or SDK installation is bundled.

The first profile is a **single-LOD diagnostic** with exact mesh selection and unchanged or
positive axis-scaled positions. Source skeleton identities and parsed hero metadata are checked.
All excluded LOD/variant mesh names must be listed. Arbitrary edited blend import, new topology,
new bones/materials, anatomical selection and cloth/physics authoring are unsupported.

A separate `sdk_edit@2` profile imports one hash-bound authored Blender scene. It may replace
one complete rigid ordinary mesh with closed connected geometry, using one existing material
and bone. Other scene meshes and the source rig remain exact. This is a bounded experimental
return, not general external-character conversion or production replacement.

## Setup

Use Python 3.12+ (standard library only outside Blender), .NET 10 and the configured installed
tools. Build the archive adapter once:

```powershell
dotnet build tools/sdk/archive/ArchiveTool.csproj -c Release --disable-build-servers -maxcpucount:1
```

Copy `tests/fixtures/config.json` to a private configuration and replace every placeholder
with an actual executable or SDK game root. SDK executables must belong to their corresponding
game roots; CSDK and CS2 environments must differ. Both need their own core shader archives.
Install/enable `SourceIO` and `io_scene_valvesource2` in the configured Blender profile first.
No tool auto-discovery, installation or update is performed. Configure work roots on the data
drive; commands require absent output directories with an existing parent.

```powershell
python tools/sdk/workflow.py probe --config sdk-config.json --output work/capabilities
python tools/sdk/workflow.py prepare --config sdk-config.json --archive source/pak01_dir.vpk --model models/example/example.vmdl_c --sha256 SOURCE_SHA256 --skeleton models/example/example.vnmskel --default-graph animgraphs/example.vnmgraph --ui-graph animgraphs/example_ui.vnmgraph --output work/source
```

Archive entries are extracted exactly through the native reader, bypassing installed-addon
auto-discovery. Preparation produces a pinned source manifest, source DATA/ModelDoc/dependencies,
baseline blend and full mesh/rig snapshot. Unknown ModelDoc nodes stop preparation. The supplied
source model hash must come from the intended source, not be inferred from a previous test.

## Edit, build and publish a diagnostic package

The agent writes an intent using `schemas/v1/intent.schema.json` and the manifest hash/names.
`tests/fixtures/intent.json` is synthetic documentation, not a character preset. `selectedMeshes`
and `excludedMeshes` must partition the entire baseline mesh inventory. Every scale names an
exact selected mesh; scales operate in local coordinates. The first implementation requires
identity mesh transforms. A broad upper-body mesh may contain arms/hands: selecting it does
not promise a torso-only change. Acknowledgements are mandatory diagnostic facts.

```powershell
python tools/sdk/workflow.py export --source work/source --intent edit.json --output work/export
python tools/sdk/workflow.py build --export work/export --output work/build
python tools/sdk/workflow.py verify --build work/build --output work/verification
python tools/sdk/workflow.py package --verification work/verification --output work/package
```

Each stage records immutable input/output hashes. Source, tool, schema or addon drift requires
a new preparation; do not edit an old manifest to make it pass. A failed stage keeps diagnostic
files but lacks its completion manifest/evidence and cannot be consumed as a successful stage.

For the authored return use `schemas/v2/intent.schema.json` and `tests/fixtures/intent-v2.json`
as contract documentation. The replacement declares the input blend path/hash, exact mesh,
existing rigid bone, unchanged/closed-replacement mode and finite bounds. The original unit
must already be rigid on that bone. Returned vertices/faces/UVs may change only for that mesh;
every other selected unit, including normals, must match source. The copied blend remains pinned.
Blender opens scenes with script execution disabled. Constraints, non-armature modifiers,
shape keys or changed rig/context are not accepted. Maximum replacement sizes
are 4,096 vertices and 8,192 triangles. No new bone/material or weight transfer is admitted.

The separate `sdk_edit@3` profile (`schemas/v3/intent.schema.json`) returns one ordinary
skinned mesh. `patch` names source triangles, center, unit axis, radius and radial scale.
It admits only the independently source-derived triangle-centroid splits and smooth compact
deformation defined in `skinned.py`, or an exact unchanged control. This is not arbitrary
retopology or unconstrained sculpt import. At most 1,024 triangles are split; each new vertex
inherits the mean source weights on at most four existing bones. Existing vertex weights and
UV corners stay source-bound; new UV corners use the source mean on the SDK 1/2048 grid.
Source edges remain intact, so subdivision creates no T-junctions or seam welds. Shading updates
are bounded to affected vertex neighborhoods; source corner normals outside remain exact.
The input scene/hash, all other scene meshes, skeleton and compiled geometry checks remain
mandatory. No new bone/material, cloth, weight transfer from another model or production
admission is added. See `tests/fixtures/intent-v3.json` for a synthetic contract example.

The separate `sdk_edit@4` profile accepts **actual authored positions**, without a radial-field
recipe. `schemas/v4/intent.schema.json` and `tests/fixtures/intent-v4.json` describe its closed
contract. One ordinary single-material skinned mesh retains its source skeleton and protected
context. `bindTransport` explicitly chooses `exporter` (unchanged addon output) or `source_rest`
(source-local rest restoration into DMX joint/base states). The latter requires matching bone
names/parents, unchanged non-bind DMX attributes and the existing strict compiled bind check.
The successful unchanged control must use the same transport as the authored return; no automatic
fallback occurs. @4 uses high-precision render-mesh UV markup to retain original UV values through
the compiler. Previous profiles and their tolerances are unchanged.
Select source triangles and editable original vertex IDs before sculpting. Optional
new vertices declare strictly interior source-triangle barycentric coordinates; declared faces
must exactly cover each source triangle with consistent directed edges. Source edges and seam
charts stay intact. This is bounded local refinement/free sculpting, not unrestricted retopology.

Original weights and UV corners remain exact. Added weights derive from source barycentric
coordinates (at most four existing bones, no trimming); new per-face UVs use the SDK 1/2048 grid.
External patch vertices are automatically fixed; the declared tangent-angle limit applies to
actual incident triangle normals, independently of shading. New degeneracy, flips and triangle
intersections reject. Pre-existing unchanged degenerate faces are reported separately. Protected
source normal fans stay exact; source corner colors are retained/interpolated by the Blender
return adapter. Compiled packed frames and color fidelity remain unqualified diagnostic limits.

Run an unchanged export/build/verify control first. Authored @4 intents pin its verification
directory and evidence hash using `unchangedControlPath` / `unchangedControlHash`; the source and
selected/excluded inventory must match. The unchanged @4 control uses empty strings for those
two fields, empty editable/topology arrays and `geometryMode: unchanged`. Characterized source
animation weight lists may be retained only with independently verified exact compiled masks;
procedural skeletons or changed morph defaults still reject.

```powershell
python tools/sdk/workflow.py prepare-surface --source work/source --intent surface-blueprint.json --output work/prepared
# Sculpt work/prepared/surface.blend; save as a new local file, preserving the labeled IDs.
python tools/sdk/workflow.py return-surface --prepared work/prepared --blend work/sculpt/edited.blend --sha256 EDITED_BLEND_SHA256 --output work/returned
python tools/sdk/workflow.py review-surface --source work/source --intent work/returned/intent.json --views views.json --output work/review
python tools/sdk/workflow.py export --source work/source --intent work/returned/intent.json --output work/export
```

Preparation produces a labeled scene, neutral snapshot and source provenance. Its blueprint and
binding hashes are checked on return; changing provenance requires a new preparation. Returned
topology, stable IDs, weights, UVs, rig/rest pose and source context are independently checked.
The return command updates complete affected normal fans and saves an admitted new scene plus
intent/report. It does not move geometry, choose a sculpting algorithm or install a package.

`schemas/v1/surface-views.schema.json` specifies matched orthographic full-character/region views
and explicit synthetic bone rotations in source coordinates. `review-surface` produces source
and authored views, colored region roles, true triangle cross-sections (JSON/SVG) and pose labels.
Optional `--compiled-verification work/verification` also independently compares actual reopened
compiled corners and renders that geometry with the same cameras. Synthetic views are offline
deformation checks, not game/animation qualification. Inspect shape in all views; passing numeric
checks is insufficient. Player-test changes must be obvious at normal viewing distance.

Build compiles raw exported DMX independently in CSDK and CS2 private environments; it does
not feed CSDK compiled mesh bytes into CS2. Source animations retain required bones. Generic
game-data nodes preserve actual compiled source hero/camera/constraint payloads. Final CS2
resource warnings/errors reject. Other SDK search-path diagnostics remain in private logs.

Verification compares actual compiled bone maps, world binds, hero metadata and graph/skeleton
references. Independent SourceIO reopening compares actual triangle-corner positions, UVs,
material names and named skin weights with the declared authored geometry, accepting a measured
position/UV/weight tolerance and explicit identical-corner equivalence classes with exact face
multiplicity. Unsupported ambiguous correspondence rejects. Authoring changes are separately
recomputed from the baseline and intent. Packaging reruns resource and actual geometry checks
before native packaging and external VPK checksum verification.

Compiled geometry guards are 0.0001 source units for position, 0.000001 for UV and 0.005 for
named weights. World-bind guards are separately 0.002 units/degrees. The diagnostic SDK output
has one mesh with mask 255, no switch distances and no bodygroups; it reuses one geometry across
LOD slots rather than retaining original LOD switching.

SDK-regenerated animation/physics/attachment/hitbox/render blocks, native packed frames and
complete original-source export fidelity remain unqualified. A single LOD/variant is deliberate
diagnostic simplification, never all-LOD or production replacement acceptance. Offline evidence
always has `runtimeTested: false`, `productionAdmission: false`. Previous owner feedback belongs
only to the exact prior delivered package; a new artifact cannot inherit its live acceptance.

## Separate installation and rollback

Create a private receipt directory before using these commands. Installation rechecks current
source/dependency hashes, actual verified package entries and addon collisions. It refuses an
occupied slot and never overwrites it. Run only when installation is assigned.
Copies are staged under a non-VPK extension, checked again and published without replacement;
an interrupted copy cannot become an active partial VPK.

```powershell
python tools/sdk/workflow.py install --package work/package --addons GAME_ADDONS_DIRECTORY --name pak98_dir.vpk --receipts work/receipts
python tools/sdk/workflow.py rollback --receipt RECEIPT_PATH_FROM_INSTALL --addons GAME_ADDONS_DIRECTORY
```

Rollback checks ownership/hash and renames only its own artifact to a unique `.disabled` name.
Reported owner rollbacks do not trigger independent confirmation runs. No automatic installation,
game launching, release publication or selection of another free slot occurs.

## Checks and limits

```powershell
python -m unittest discover -s tools/sdk/tests -q
./scripts/Invoke-Verification.ps1 -Mode all
```

Portable .NET tests independently validate the versioned closed JSON schemas. Proprietary integration
inputs stay outside Git and are supplied via explicit paths/hashes. Detailed process logs go to
the requested workspace; progress goes to stderr and JSON results to stdout. Expected errors
exit 20 in this separate experimental tool (not production CLI exit-category compatibility).
Processes have a 300-second timeout and are terminated on cancellation. Initial limits: 256 MiB
per artifact, 1,000,000 vertices, 2,000,000 triangles, 512 bones; no production budget is widened.

Wraith compatibility is a separate gate. Unsupported animation weight lists, procedural/cloth
consumer addressing, LOD/bodygroup or shape failures cannot be waived because Drifter works.
WeightListList retention requires actual compiled ASEQ mask order, named weights and morph
defaults/arrays to match source. SDK cloth-bone renaming or changed mask defaults reject even
when source preparation and compilation succeed.
The integration plan stops at a concrete go/defer decision, without another sculpting batch.
