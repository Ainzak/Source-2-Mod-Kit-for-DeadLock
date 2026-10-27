# Existing-mesh position authoring

The agent workflow exports source-bound geometry, creates an editable Blender scene and independently
validates its returned positions, then imports them through the compiled build/verify lifecycle.
Use an installed Blender executable and `tools/blender/blender_bridge.py`.

```text
s2mod authoring export --project PROJECT --selection selection.json --output EXPORT
s2mod authoring scene-create --blender BLENDER --bridge-script BRIDGE --bundle EXPORT/bundle.json --bundle-sha256 HASH --output edit.blend
s2mod authoring scene-extract --blender BLENDER --bridge-script BRIDGE --bundle EXPORT/bundle.json --bundle-sha256 HASH --scene edit.blend --output delta.json
s2mod authoring validate --project PROJECT --selection EXPORT/selection.json --bundle EXPORT/bundle.json --bundle-sha256 HASH --delta delta.json
s2mod authoring import-recipe --project PROJECT --selection EXPORT/selection.json --bundle EXPORT/bundle.json --bundle-sha256 HASH --delta delta.json --output import.recipe.json
s2mod build --project PROJECT --recipe import.recipe.json
s2mod verify --project PROJECT --build BUILD_ID
s2mod package create-minimal --project PROJECT --build BUILD_ID
```

Supply absent outputs. Export publishes `bundle.json`, `selection.json`, `source-facts.json` and
`pins.json` together. Keep the expected bundle hash from the export result outside the editable scene.
The independent validator rereads the immutable project and dependencies and derives geometry/frame
expectations from source records rather than trusting Blender's preflight.

An agent prepares a version-1 selection using `position-authoring-selection.schema.json`: input hash,
1–16 complete ordinary members across every present LOD, displacement cap, and source-hashed protected
IDs for each member/LOD. Explicit policies must select `preserve_unverified` runtime metadata,
`preserve_serialized_inputs_unverified` procedural inputs, `preserve_source_coincidence` source triangles,
and `reject` zero boxes/spheres. Consumer preservation records untested runtime semantics.
Unsupported layouts fail closed.

Edit only the `S2ModKit_Edit` key. Retain record/corner IDs, triangles, basis, object transforms and every
LOD. Coincident seam records stay separate and move together. Modifiers, remeshing, welding, generated
LODs, rig/UV edits and new geometry reject. Blender displays geometry only; game shading is not imported
from its viewport.

The default boundary policy keeps protected positions and packed frames exact. The explicit
`keep_positions_allow_source_derived_boundary_frames@1` alternative names stationary protected,
nonprocedural records in the changed-face neighborhood and permits only their source-derived shading
updates. Every declared record must belong to that neighborhood. Use strict protection for an unchanged
return, which needs no boundary exception.

Validation checks float32 words, protection, displacement, coincidence partitions and triangle orientation,
then transports authored normals/tangents through source-area-weighted incident face differentials.
It preserves authored seams/handedness and untouched packed words. Frames are not recomputed from UV
gradients; degenerate UVs do not trigger a guessed replacement. An exact conservative conditioning
certificate may reject an otherwise usable edit.

Limits: 64 MiB per geometry artifact, 16 MiB per selection, depth 32, 500,000 records per buffer,
1,000,000 selected records, 2,000,000 context records, 256 context buffers and 8 LODs.
Duplicate/unknown/missing/null fields, noncanonical return bytes and incomplete maps reject.
The version-1 result contains position/frame hashes and changed-record lists, with `modelBytesBuilt: false`
and `runtimeStatus: "untested"`.

`import-recipe` performs source planning, freezes the exact return and stores immutable hash-addressed
selection/bundle/delta artifacts in the project. It publishes an absent recipe file using
`import_positions@1` / recipe 12. `plan` produces plan 8 and planned evidence 13; `build` independently
reopens the complete output before atomic publication. An unchanged return retains every source byte.
The verifier reconstructs exact positions and source-relative frames from the original source plus
canonical return, checks complete attributes/index/skin/context and independently audits contributor
bounds and preservation. The native codec identity is part of the frozen layout hash.

`authoring import-request --request request.json` accepts the closed
`position_import_request@1` contract for `validate` or `plan`. Its binding must equal the embedded
recipe's binding. It provides the same checks as direct commands; it does not publish a model.

Admission is limited to characterized exclusive ordinary FLOAT3/native-frame buffers across all LODs.
One import is allowed per recipe; composition with removals, topology changes and other mutations
is not supported by this version. UVs, weights, the rig, excluded geometry and serialized consumers
stay fixed. Bind-space bounds checks and external parsing do not qualify live animation, collision,
culling or simulation. Model evidence 13 is compact and bounded at 64 MiB; older limits remain intact.

For one exact draw removal alongside an import, prepare both recipes against the same original input:

```text
s2mod authoring compose-recipe --project PROJECT --import-recipe import.recipe.json --remove-recipe remove.recipe.json --output combined.recipe.json
s2mod plan --project PROJECT --recipe combined.recipe.json --format json
s2mod build --project PROJECT --recipe combined.recipe.json
```

Recipe 13 / plan 9 / evidence 14 support only this explicit composition. Import and removal draw
selections must be disjoint and cover every LOD. The writer merges compatible metadata changes in
one original parsed resource and serializes each affected block once. Unused coat buffers stay intact.
Verification checks all retained draw entries, scene boundaries and changed ordinals before a separate
source-draw audit projection runs the exact import verifier. Evidence binds the actual combined output
and the projection separately. Removal exposes the retained underlying mesh; it does not construct a
bare body. General stacking remains unsupported. Recipe/request JSON is limited to 16 MiB; combined
plan/evidence JSON uses compact serialization within 64 MiB. Imported geometry is unchanged by removal.
