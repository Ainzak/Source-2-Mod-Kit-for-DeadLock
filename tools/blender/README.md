# Experimental geometry authoring handoff

This standalone tool creates a source-bound Blender scene and returns explicit position edits.
It has no compiled-model writer and cannot build or install a mod. It uses Blender's bundled
Python API; no MCP connection or Python package installation is required.

The production CLI can now export immutable source bundles, automate this bridge and independently
validate returned positions/frames. See [position authoring](../../docs/POSITION-AUTHORING.md).
The standalone preflight remains supplemental; neither route has a compiled-position writer yet.

Separate reserved position-import schemas now describe a future compiled import. Current CLI
readers reject those versions; the schemas do not supply a writer. This standalone tool still
returns positions only and cannot build a compiled model or install a mod.

The caller supplies a trusted decoded geometry snapshot and a selection/protection policy.
The main CLI does not export this snapshot format yet. A snapshot has `inputHash` and `rows`;
each row has `lod`, `mesh`, `buffer`, `decodedHash`, float32-convertible `points`, and `calls`
with flattened triangle indices. `inputHash` identifies the original model, not the point JSON.
Keep that model and all omitted source metadata immutable outside the scene.

`handoff.py prepare` freezes snapshot bytes, source positions, exact triangle order, member/LOD
inventory and protected record IDs. The policy and output contracts are in `schemas/v1/`.
Retain the output bundle SHA-256 separately from the editable scene.

```text
python handoff.py prepare --snapshot source.json --policy policy.json --output bundle.json
blender --background --factory-startup --python blender_bridge.py -- create --bundle bundle.json --bundle-sha256 HASH --output authoring.blend
blender --background authoring.blend --python blender_bridge.py -- extract --bundle bundle.json --bundle-sha256 HASH --output delta.json
python handoff.py verify --bundle bundle.json --bundle-sha256 HASH --delta delta.json --output preflight.json
```

Edit only the `S2ModKit_Edit` shape key on selected members. The lowest LOD is visible initially;
other LODs and immutable context are available in the Outliner. Return every selected LOD together.
Protected records are marked by a point attribute and vertex group; they are checked on extraction.
The coat can be shown or hidden for authoring review; hiding it does not remove it from a game model.
Source-coincident records remain separate and must move together. Modifiers, object transforms,
parenting, constraints, basis edits and topology/identity changes are rejected.

Positions use three little-endian float32 words per record, written as eight lowercase hex digits.
This preserves signed zero and avoids JSON-decimal round-trip ambiguity. The extractor returns
raw named-key coordinates, not evaluated viewport positions. Preflight checks finite positions,
protected words, coincidences, exact displacement limits and integer triangle orientation.
Collapsed source faces are preserved; valid faces cannot collapse or leave their source hemisphere.
The delta is canonical JSON; `deltaSha256` in preflight hashes its exact canonical bytes.
Outputs must be absent and are published without replacing existing files.

The scene is geometry-only. Rig, UVs, skin weights, game materials, packed frames, poses and
runtime behavior are unqualified. These tools neither preserve those attributes through Blender
nor admit a new compiled mutation. Triangle checks do not prove a good shape or global injectivity.
Do not disguise returned positions as parameters of an existing analytical-field operation.

Portable synthetic checks: `python -B -m unittest discover -s tests -q` from this directory.
Blender synthetic checks: run `tests/blender_checks.py` with an absent, explicit output directory
after `--`. Fixtures are wholly synthetic; generated scenes and game assets belong outside Git.

## Offline neck authoring

`neck.py` is a separate geometry tool for an already aligned head and body. A per-character
profile selects one geometric head boundary, body cut plane, protected vertex IDs and bounded
blend settings. It uses a boundary-tangent Hermite loft, then joins and locally blends copies.
This replaces the body's geometry above the cut; it does not automatically fit anatomy or rigs.
Objects must have identity world transforms, no shape keys and a triangular head. Unsupported
or non-star-shaped boundaries, incompatible protected regions and failed verification are rejected.

First inspect the source head boundaries without changing the input:

```text
blender --background --factory-startup --disable-autoexec --python-exit-code 1 --python neck_inspect.py -- --scene input.blend --head head_object --body body_object --output inspection.json
```

The inspection reports candidate vertex IDs and bounds but never selects an ambiguous interface.
Author a profile using `schemas/v1/neck-profile.schema.json`; `tests/fixtures/neck-profile.json`
illustrates its fields with synthetic IDs. The profile's sceneHash must match the input file.
Blender coordinates and bounds are Z-up and must use the same units. Select all head/body
vertices outside the intended edit region as protected. The tool additionally rejects unordered
bounds, overlapping fades and protection of selected interface vertices.

```text
python neck.py --blender /configured/blender --scene input.blend --profile character-neck.json --output /configured/absent-output
```

Output includes `authored.blend`, eight matched exposed-neck PNGs, saved-mesh `verification.json`
and a hash-bound `bundle.json`. The original scene and retained source objects are checked;
source head face winding, per-corner UV/color provenance, named weights and protected positions
are verified independently. Protected custom normals are restored within Blender's encoding
tolerance (0.00015 vector distance). A local axial-slab intersection check evaluates actual
triangles and rejects overlaps; it is not a global self-intersection or animated-pose guarantee.
Failed jobs may leave diagnostics but cannot publish a completion bundle. All output roots must
be absent and their parent directories must exist. Temporary files follow the output root.

The body still needs authored UVs, materials, complete hero skin weights and a compatible compiled
replacement contract. No SDK admission policy, production writer, game asset or installation is
changed. A successful neck result always reports `gameReady: false` and `compiled: false`.

For asset-free native success/rejection checks, run `tests/neck_blender_checks.py` with factory
startup and scripts disabled, passing an absent output directory after `--`. Portable contract
checks are included in the regular Python test discovery command above.

## Read-only character measurements

`measure.py` compares two to four explicitly selected scenes/geometry states without saving
them. Use `schemas/v1/measure-profile.schema.json` and the wholly synthetic
`tests/fixtures/measure-profile.json` as contract references. Keep character profiles and assets
outside Git. The first input is the reference. The source may be clothing-covered: explicitly
declare unavailable anatomy and estimates rather than measuring fabric as a bare body.

```text
python measure.py --blender /configured/blender --profile character-measurements.json --output /configured/absent-output
```

The profile binds each scene file, selected mesh geometry/world matrix and exact rest rig by hash.
Use `measure_math.geometry_hash` and `rig_hash` to prepare those pins from a trusted inventory.
Mesh hash input is local vertex positions, exact polygon index lists and the world matrix. Rig
hash input is named parent/head/tail/rest-matrix records and its world matrix. Landmarks use exact
per-topology vertex IDs with normalized weights, named rest-bone endpoints, or an explicit
unavailable reason. Three weighted vertex IDs also support a barycentric triangle landmark.
Regions use exact selected face IDs; there is no nearest-matching or whole-scene bounds fallback.

Only `mesh_basis/bone_rest` is supported. Raw basis points are transformed to world coordinates;
modifiers, shape-key mixes and posed/evaluated animation are not measured.
Matched renders use disposable basis-mesh copies with modifiers, keys and animation disabled;
they cannot silently display a different posed/keyed shape from the numerical measurements.
Common axes must be
orthonormal and right-handed. Units are explicitly `source_units` or `synthetic_units`, with no
implicit conversion to centimeters. Ground and camera definitions are common to all inputs.
This does not qualify source rig fidelity for export or animation; imported scene files stay exact.

Supported measurements are landmark distance, signed axial difference, selected-region span,
projection from a landmark datum, section span, orientation angle, sampled regional peak
separation, and minimum sole/region ground clearance. Bone lengths remain separate from surface
dimensions so mesh stretching can be detected even when a rig is unchanged. Anatomical landmarks
must be inspected once when preparing each character's profile.

Sections cut actual selected loop triangles, retain geometric contour components and face IDs,
and do not guess the largest loop or bridge holes. A mismatched expected component count,
branch or coplanar ambiguity makes scalar spans unavailable; all resulting contours remain
visible as evidence. Open chains can have spans but never imply circumference/area. The geometric
endpoint merge tolerance is 0.00001 raw units and must be suitable for the profiled coordinate
scale. Closed profiles alone cannot establish that a selected region is anatomically correct.

Output includes `measurements.json`, `report.md`, a static `report.html`, matched plain PNGs,
annotated SVGs and section SVG plots, plus `bundle.json` binding profile/input/tool/output hashes.
Open the HTML in a local browser yourself when automated local-file browser preview is unavailable;
all numerical data and selectors are also available in JSON/Markdown. Annotations number the
listed landmarks and draw actual contours. Clothing estimates use amber markers; direct/authored
landmarks use cyan. Plain images remain available separately for appearance review.

Comparisons report absolute and relative changes when valid. A zero reference leaves percentage
unavailable. Estimates never become precise preservation warnings; unavailable values stay
unavailable even if another input supplies a number. `preserve`, `intentional` and `reference`
expectations and absolute review tolerances live in the profile. Warnings do not mutate the body
or change any import limit. Every bundle reports offline evidence and `gameReady: false`.

Portable tests use the discovery command above. Run `tests/measure_blender_checks.py` through
configured Blender with factory startup/scripts disabled and an absent output directory after
`--` for native extraction, nonuniform transforms, unchanged-rig surface discrepancies, input
preservation, wide-camera render coverage and rejection cases. No external Python packages are
needed by the measurement tool or its native synthetic checks.

## Idle-pose rest bake

`pose_bake_math.py` (pure numpy) and `pose_bake.py` (Blender glue) solve exact inverse linear-blend
skinning per vertex. A chosen pose, such as a hero's idle clip, then shows a target shape without
any weight change. Tests: `"<Blender>/python/bin/python.exe" -m unittest tests/test_pose_bake.py`.
The recipe and its limits are in [POSE-BAKE](../../docs/reference/POSE-BAKE.md).

## Posed-defect check (pose_check@1)

`posecheck.py` poses one to four hash-bound scenes with game animation clips (DMX through the Valve
Source Tools importer, or actions already in the scene) and reports, per input x pose x frame:

- sharp posed creases (edges whose fold angle grows by more than `creaseDegrees` from rest), counted
  per region and per side (`lateralAxis` points to the character's left);
- squashed/stretched triangles (area ratio outside `[squashLow, squashHigh]`);
- the largest posed gap between coincident rest vertices of two meshes (`seams`, e.g. head/body);
- 95th-percentile drift of finger/limb vertices from their own bone chain (`chains`).

It also renders matched sheets (rows = inputs, columns = poses) for each view in two modes: true
vertex colours with posed normals (`shaded`) and an emulated ink outline (`outline`): a black
back-face shell pushed along each game vertex's stored normal, as Deadlock's inverted-hull
outline does. Posed creases and folds that flat-colour or rest previews hide show up here.

```text
python posecheck.py --blender /configured/blender --profile check.json --output /absent/output
```

The worker opens scenes without saving them; the launcher re-hashes inputs, profile and tool files.
Outputs: `summary.txt` (printed), `result.json`, `report.html`, sheets, `bundle.json`. Offline
evidence only; it does not prove live game behaviour. Portable checks: `tests/test_posecheck.py`.

## Iso-contour cut (meshcut.py)

`meshcut.iso_cut(bm, value)` cuts a bmesh along the zero contour of any per-vertex function and
deletes the negative side. Split points interpolate vertex groups, UVs and colours; crossings within
5% of an existing vertex reuse that vertex, so no sliver triangles are created. The boundary lies on
the original surface. Used to remove a source head's collar-hidden, sliver-triangulated lower edge
before joining a body. Blender check: `tests/meshcut_blender_checks.py`.

## Head/neck join (neckjoin.py)

Reusable steps for joining a retained source head to a new body (extracted from the accepted Wraith route,
character-free parameters): `band_reweight`, `edge_height_fn`, `cut_fringe`, `relax_on_surface`,
`unify_split_uvs`, `skin_twin_normals`, `open_loops`, `loft_neck`, `graded_join`. `skinweights.py` holds the
weight helpers. The Wraith builder calls these steps; rebuilding through them matches the pre-extraction
builder within its own run-to-run variation. Blender check: `tests/neckjoin_blender_checks.py`.
Review every result with `posecheck.py` against the previous delivered build.
`posesweep.py --spec sweep.json --blender <exe>` builds parameter variants of a builder (JSON override in an
environment variable) in parallel and prints one posecheck table for all of them.
