# S2ModKit CLI reference

This is the public reference for the current pre-alpha CLI. It is contract-checked against actual
`--help` output by `CliContractTests.CliReferenceDocumentsEveryPublicHelpOption`. When a public help
option changes, the test requires this document to change in the same commit.

Run `s2mod <command> --help` for the host-localized help layout. Command descriptions and option
spellings below are the stable English contract.

Persisted paired directional contracts support operation 10 / recipe 11 / plan 7 / evidence 12
and closed `paired_directional_field_options@1` intent. `plan` accepts explicit recipe-11 intent
against the qualified source profile, saves schema-7 plans and returns planned schema-12 evidence
with `--format json`.
`build --project <path> --recipe <path>` independently reparses compiled source/output before
publishing a workspace model; `verify --project <path> --build <id>` repeats that audit and saves
observed schema-12 evidence. Missing independent verification still rejects with
`PAIRED_VERIFICATION_UNAVAILABLE`; snapshot success alone cannot authorize publication. Schema-12
JSON uses compact serialization within its 64 MiB budget; other model evidence and Markdown retain
their 16 MiB workspace limits. Procedural simulation, culling, garment/pose and runtime remain
untested. Paired directional discovery/scaffolding/review commands are not implemented. The mirrored ellipsoid workflow documented below retains its existing meaning.

## Output and exit codes

- `doctor`, `bounds diagnose`, `catalog heroes list`, `catalog resolve`, `compatibility scan`, `inspect`,
  `components list`, and `plan` accept `--format text|json`; default is `text`.
- Commands that publish or manage artifacts return a JSON envelope on stdout by design.
- In JSON mode stdout contains only the envelope; diagnostics belong on stderr.
- Every command accepts `-?`, `-h`, or `--help`.

| Exit code | Category |
|---:|---|
| 0 | Success |
| 2 | CLI syntax or schema input |
| 10 | Input or resource resolution |
| 20 | Unsupported capability |
| 30 | Selection or LOD contract |
| 40 | Rewrite or verification |
| 70 | Unexpected internal failure |

## Command index

| Group | Leaf commands |
|---|---|
| Diagnostics | `doctor`, `bounds diagnose`, `influences diagnose` |
| Hero catalogue | `catalog heroes list`, `catalog resolve` |
| Compatibility | `compatibility scan` |
| Projects | `project create`, `project create-vpk` |
| Inspection and authoring | `inspect`, `components list`, `recipe scaffold`, `plan`, `selection-preview` |
| Candidate lifecycle | `build`, `verify`, `package create`, `package create-minimal`, `package verify` |
| Update status of loaded mods | `mods status`, `mods ack` |
| Managed addon lifecycle | `addons scan`, `addons install-file`, `addons rollback-file`, `addons merge`, `addons inventory`, `addons install`, `addons install-current`, `addons verify-active`, `addons rollback`, `runtime record` |

### `s2mod doctor`

Checks configured adapter, verifier, and geometry-codec availability.

| Option | Required | Meaning |
|---|---:|---|
| `--format` | No | `text` or `json`; default `text`. |
| `--help` | No | Print help. |

### `s2mod bounds diagnose`

Reads a compiled model and compares stored per-bone sphere and box bounds with decoded vertices
from every participating buffer. It writes complete JSON and Markdown reports under `--output-root`;
the compiled input is read-only. This diagnostic does not change transform eligibility.

| Option | Required | Meaning |
|---|---:|---|
| `--input` | Yes | Read-only compiled `.vmdl_c` input file. |
| `--resource-path` | Yes | Original logical Source 2 `.vmdl_c` resource path for the report. |
| `--output-root` | Yes | Directory for JSON and Markdown diagnostic reports. |
| `--format` | No | `text` or `json`; default `text`. |
| `--help` | No | Print help. |

### `s2mod influences diagnose`

Writes a content-addressed version-1 influence diagnostic JSON report from an immutable compiled
model. It records per-LOD buffer/index identities, source labels/materials, complete indexed
coverage, every nonzero skinning contributor, render-to-root remaps, procedural flags and observed
existing gate reasons. Unsupported mesh rows retain a reason and no partially assessed buffer facts.

| Option | Required | Meaning |
|---|---:|---|
| `--input` | Yes | Immutable compiled `.vmdl_c` input file. |
| `--resource-path` | Yes | Original logical `.vmdl_c` path. |
| `--output-root` | Yes | Configured directory for an immutable diagnostic JSON report. |
| `--field-options` | No | Existing version-1 common-field options JSON, for a hypothetical position-only probe of every reported buffer. |
| `--format` | No | Compact publication summary in `text` or `json`; default `text`. |
| `--help` | No | Print help. |

The optional probe distinguishes field membership, changed/unchanged position words and influenced
procedural contributors. Pinned and unchanged are distinct: a full-strength pivot can remain fixed.
Frames, simulation family/rest/constraint semantics and complete dependency closure remain
unassessed. The probe selects no mutation members and grants no permission to edit a rejected
buffer. Use exact planning for eligibility. The schema is `schemas/influence-diagnostic.schema.json`.
Matching reports are reused; conflicting output is preserved and rejected. Inputs are never rewritten.

### `s2mod catalog heroes list`

Lists heroes and resources after verifying every catalogue locator against read-only base-VPK
directory metadata. Text output favors names and status; JSON retains stable IDs, exact logical
paths, sizes, and source verification.

| Option | Required | Meaning |
|---|---:|---|
| `--catalogue` | Yes | Versioned hero catalogue JSON file. |
| `--source-vpk` | Yes | Read-only Deadlock base `*_dir.vpk` archive. |
| `--format` | No | `text` or `json`; default `text`. |
| `--help` | No | Print help. |

### `s2mod catalog resolve`

Resolves one exact hero ID, display name, or alias and returns its verified resources. It rejects a
stale source, missing/moved/duplicate resource, or ambiguous filename suggestion.

| Option | Required | Meaning |
|---|---:|---|
| `--catalogue` | Yes | Versioned hero catalogue JSON file. |
| `--source-vpk` | Yes | Read-only Deadlock base `*_dir.vpk` archive. |
| `--hero` | Yes | Hero ID, display name, or alias. |
| `--format` | No | `text` or `json`; default `text`. |
| `--help` | No | Print help. |

### `s2mod compatibility scan`

Verifies the catalogue, opens only its listed model resources, reuses current inspection,
component-discovery, and capability analysis, then publishes immutable strict JSON and concise
Markdown reports. Default terminal output is a short summary and gap-cluster count.

| Option | Required | Meaning |
|---|---:|---|
| `--catalogue` | Yes | Versioned hero catalogue JSON file. |
| `--source-vpk` | Yes | Read-only Deadlock base `*_dir.vpk` archive. |
| `--output-root` | Yes | Configured root for new JSON and Markdown reports. |
| `--compare` | No | Prior version-1 compatibility report JSON for added/removed/changed comparison. |
| `--format` | No | `text` or `json`; default `text`. |
| `--help` | No | Print help. |

### `s2mod project create`

Imports a compiled model from a filesystem resource root into an immutable project.

| Option | Required | Meaning |
|---|---:|---|
| `--root` | Yes | New project workspace root. |
| `--input` | Yes | Input `.vmdl_c` file. |
| `--resource-root` | Yes | Filesystem root for owned dependency resolution. |
| `--runtime-resource-root` | No | Read-only root for game-provided dependencies; repeat when required. |
| `--help` | No | Print help. |

### `s2mod project create-vpk`

Imports one compiled model directly from a read-only split base-game VPK.

| Option | Required | Meaning |
|---|---:|---|
| `--root` | Yes | New project workspace root. |
| `--base-vpk` | Yes | Base `*_dir.vpk` file. |
| `--entry` | Yes | Logical compiled-resource path inside the archive. |
| `--expect-directory-sha256` | No | Expected SHA-256 of the small directory VPK. |
| `--help` | No | Print help. |

### `s2mod inspect`

Inventories resource blocks, LODs, meshes, draw calls, and available geometry facts.
JSON geometry facts include stable connected-component IDs for indexed triangle islands when the
buffer profile is decoded. These IDs can drive `transform_component@3` inside a broader draw call.

| Option | Required | Meaning |
|---|---:|---|
| `--project` | Yes | Project root. |
| `--format` | No | `text` or `json`; default `text`. |
| `--help` | No | Print help. |

### `s2mod components list`

`--experimental --directional` selects discovery schema 6 with source-bound component IDs.
It reports structural mapping only: `DIRECTIONAL_OPTIONS_REQUIRED` does not mean an arbitrary
field is admitted. `--coordinated` and `--directional` are mutually exclusive. Default discovery
and historical experimental routes retain their versions.

`--experimental` explicitly requests discovery schema 4, adding planner-probed whole-part,
region and local ellipsoid scale choices. A probe establishes storage eligibility; explicit
field parameters still require exact planning. It does not identify anatomy.
The default schema-2 discovery and strict capability rules are unchanged. Experimental IDs
are distinct: use the same opted-in discovery context when scaffolding. Removal, translation and
affine intents in that context still use their strict mutation contracts, not experimental fallback.

Lists discovery-v2 `material_group` and exact source-authored whole-mesh `mesh_lineage` candidates
with capability explanations. The two kinds may overlap. Text and JSON include material membership,
per-LOD draw calls, and, for a lineage, its mechanical source label and exact mesh/block membership.
The command is read-only. Capability output reports each assessed operation version, including
`transform_component@4` on characterized affine geometry. The coupled raw-MBUF/convex-PHYS profile
reports `transform_component@2` and remains uniform-only.

| Option | Required | Meaning |
|---|---:|---|
| `--project` | Yes | Project root. |
| `--format` | No | `text` or `json`; default `text`. |
| `--experimental` | No | Request schema-4 experimental capability probes. |
| `--coordinated` | No | With `--experimental`, use discovery schema 5 and bounded operation-8 planner probes. Candidate probes use explicit reject policies; exact acknowledged unions are reassessed during scaffolding. |
| `--help` | No | Print help. |

### `s2mod recipe scaffold`

`--intent directional-field --experimental --directional-options <options.json>` uses current
schema-6 `--component` IDs to resolve complete ordinary members and emit canonical recipe 10.
The closed `directional-field-options.schema.json` requires `inputHash`, explicit preserve/reject
policies, the per-axis field, keep-fixed assertions and displacement cap. Scaffolding runs the
exact source planner before writing a new recipe. Do not combine separate affine/local-field
flags with this intent; every field value is in the typed options document.

`--intent mirrored-ellipsoid-scale` uses the same explicit field parameters with required
`--mirror-axis x|y|z` and `--mirror-coordinate <number>`. The reflected center must be exactly
representable; outer supports must be disjoint (touching pinned boundaries are allowed).
Both fields must change stored geometry in every LOD. Asymmetric input is preserved, not repaired.


`--experimental` with `--intent uniform-scale` produces the visual-only version-5 profile;
`--intent region-scale` produces version 6 and additionally requires `--region-axis x|y|z`,
`--pinned-through`, `--full-from`, and `--pivot-point X,Y,Z`. Coordinates at/below the pinned
threshold remain unchanged; full strength starts at the larger full threshold, with a smooth
transition between them. Both require `--scale` and `--max-displacement` (at most 64).
The exact union and parameters must pass planning before the recipe is published.
`--intent ellipsoid-scale --experimental` emits recipe schema 8 and operation version 7.
It requires explicit `--field-center X,Y,Z`, `--field-radii X,Y,Z`, `--core-fraction`,
`--scale` and `--max-displacement`. The center anchors selection and scaling; there is
no independent pivot. Core fraction is the inner ellipsoid's radius fraction; influence
falls smoothly to zero at the outer ellipsoid. Scale must be in 0.5..2, excluding 1;
the Jacobian certificate, displacement cap (at most 64) and exact all-LOD plan must pass.
Spheres, occlusion proxies and collision are preserved without proving coherence; hair,
clothing fit, anatomy and runtime stability are not guaranteed. This is not a strict fallback.

Creates a new canonical recipe from current candidate IDs. Affine scaffolding checks the generated
recipe with the ordinary dry-run planner before writing it. The command does not publish a plan or
model and never overwrites `--output`.

| Option | Required | Meaning |
|---|---:|---|
| `--project` | Yes | Project root. |
| `--component` | Yes | Candidate ID from `components list`; repeat for an explicit union. |
| `--intent` | Yes | `remove`, `uniform-scale`, `translate`, `affine`, or opted-in `region-scale` / `ellipsoid-scale`. |
| `--experimental` | No | Acknowledge the experimental visual policy and use current experimental candidate IDs. |
| `--field-center` | For `ellipsoid-scale` | Explicit model-space center as `X,Y,Z`. |
| `--field-radii` | For `ellipsoid-scale` | Explicit positive outer radii as `X,Y,Z`. |
| `--core-fraction` | For `ellipsoid-scale` | Explicit inner radius fraction in `(0,1)`. |
| `--output` | Yes | New recipe JSON path. |
| `--scale` | For `uniform-scale` | Positive uniform scale in the accepted operation range. |
| `--translate-x` | For `translate` | Optional finite X translation. |
| `--translate-y` | For `translate` | Optional finite Y translation. |
| `--translate-z` | For `translate` | Optional finite Z translation. |
| `--reference-lod` | No | Pivot LOD; default is the lowest selected LOD. |
| `--max-displacement` | For transforms | Positive safety cap, at most 256 Source units. |
| `--max-collision-displacement` | For discovered `transform_component@2` | Independent positive collision cap, at most 256 Source units. |
| `--scale-x` | No | Affine X scale in `0.25..4`; default `1`. |
| `--scale-y` | No | Affine Y scale in `0.25..4`; default `1`. |
| `--scale-z` | No | Affine Z scale in `0.25..4`; default `1`. |
| `--rotate-axis` | With `--rotate-degrees` | Unit axis as `x,y,z` in the selected frame. |
| `--rotate-degrees` | With `--rotate-axis` | Nonzero signed angle in `(-180, 180]`. |
| `--pivot` | No | `selection-center` (default), `point`, `face`, or `bone`. |
| `--pivot-point` | For point pivot | Finite model-space point as `x,y,z`. |
| `--pivot-face` | For face pivot | `min_x`, `max_x`, `min_y`, `max_y`, `min_z`, or `max_z`. |
| `--pivot-bone` | For bone pivot | Exact influencing bone name. |
| `--frame` | No | `model` (default) or `bone-bind`. |
| `--frame-bone` | For bone-bind frame | Exact influencing bone name. |
| `--coordinated-options` | For coordinated-field | Version-1 typed common-field JSON with every policy, field and displacement limit. Use `--experimental --intent coordinated-field` and current schema-5 component IDs; other transform parameters are forbidden. |
| `--help` | No | Print help. |

Scaffolding emits recipe schema version 1 for removal, version 2 for `transform_component@1`, and
version 3 for the discovered coupled `transform_component@2` profile. The coupled profile requires
`uniform-scale`, zero translation, and both displacement caps; it never falls back to a visual-only
rewrite.

When a component lacks the older uniform profile but has an available version-4 affine profile,
`--intent uniform-scale --scale ...` emits a schema-5, version-4 recipe with equal axis scales and
identity rotation. The same dry-run safety checks apply.

The `affine` intent emits recipe schema version 5 and `transform_component@4` for a characterized
root vertex selection, including a characterized single selected buffer within a multi-buffer mesh.
It accepts per-axis scale, rotation, typed pivot and frame, optional final
translation, and a required `--max-displacement`. The component must show an available version-4
capability; the specific parameters must also pass the normal planner. An affected distance field,
morph, or collision-coupled layout remains unsupported.

Recipe schema version 4 also accepts `transform_component@3` at
`connected_component_vertices` granularity. It requires explicit `connectedComponentIdsByLod` and
expected vertex totals from current inspect evidence. Automatic scaffolding for arbitrary island
groups is not yet exposed; manual recipes still pass the same strict plan/build verification.

Coordinated authoring uses one explicit options document, validated by
`coordinated-field-options.schema.json`, plus selected candidate IDs from
`components list --experimental --coordinated`. Its `schemaVersion: 1` and
`kind: coordinated_field_options` bind `policy`, `zeroBoneBoxPolicy`,
`zeroRenderSpherePolicy`, `field`, and `maximumDisplacement`. Declare both zero policies
as `reject@1` unless explicitly acknowledging their separately named unverified preservation
profiles. One ellipsoid, axis ramp, or tilted ramp applies to all selected ordinary members.
Whole-buffer correspondence requires a unique authored mesh lineage and unique complete material
membership in every LOD; absent or ambiguous mapping rejects. Material and lineage views can overlap
and are deduplicated. No label infers anatomy, hair, eyeballs, clothing or attachments.
The canonical schema-9 recipe is published only after exact common-field planning passes.

### `s2mod selection-preview`
The concise agent route is `directional review`; the existing selection-preview response is unchanged.



```powershell
s2mod selection-preview --project H:\S2ModKit\projects\example --recipe H:\S2ModKit\recipes\field.json --output-root H:\S2ModKit\diagnostics
```

| Option | Required | Meaning |
|---|---:|---|
| `--project` | Yes | Project containing immutable imported input and its complete dependency graph. |
| `--recipe` | Yes | Explicit acknowledged local-field schema 8 (`transform_component@7`), coordinated schema 9 (`transform_component@8`) or directional schema 10 (`transform_component@9`). |
| `--output-root` | Yes | Configurable ignored directory for diagnostic artifacts. |
| `--help` | No | Print help. |

Returns a JSON envelope with the content-addressed directory, summary/contact-sheet paths and
hashes. Produces `selection-summary.json` (strict ellipsoid or coordinated summary schema 1) and a static SVG contact sheet
showing original/predicted XY, XZ and YZ geometry in every LOD. Core, transition and pinned points
retain source-derived colors; enclosing buffer topology and model axes are labelled. Gray geometry
is non-editable context. Each focused view retains every enclosing point and triangle; only context
is cropped. Coordinated sheets show each participating buffer and use one common field; their
summary lists exact members and excluded siblings with draw-call IDs, material paths and advisory
source labels. There is no Blender or interactive viewport dependency.

Directional schema-10 recipes produce a separate `directional_surface_comparison@1` artifact and
closed schema-1 summary (`directional-selection-preview.schema.json`). Every LOD gets matched
original/predicted shaded selection surfaces over complete excluded silhouettes, with a full outline
and a labelled focus crop. Cameras and scale are shared across both states and all LODs. Protected
records are green; alternative authored views appear together rather than simulating activation or
physical occlusion. The summary reports XYZ factors, changed/pinned/protected counts, maximum
stored displacement, complete-source AABB diagonal and their ratio, plus before/after spans for
the same changed records and the complete selection. These measures do not define a perceptibility
threshold or prove animation/garment fit. The route requires an existing explicit recipe;
directional discovery/scaffolding use the separate agent route above.

The command plans from immutable source without saving a plan, building or editing a resource.
Matching existing artifacts are reused; conflicting or incomplete artifacts reject without
overwriting. Incomplete geometry or a preview exceeding eight LODs, one million points (including
context), three million triangle indices or the 64 MiB SVG character budget rejects without
publishing a partial preview. These are bind-space predictions, not texture, animation, hair,
clothing-fit, collision or runtime qualification. Use an ignored root: sheets contain source geometry.


### `s2mod directional inspect`

Use `--help` to print the command options.

`s2mod directional inspect --project <project> --component <schema-6-id> --experimental --output-root <ignored-root>`
returns one compact summary and a hash-bound source-report path. Repeat `--component` for a union.
The report contains complete selected positions, canonical members, selected/excluded context and
explicit source-root bone assertion candidates. Choosing a bone still requires inspecting its
meaning and passing exact field/protection planning. Unavailable complete contributor closures
are labelled; partial closures are never substituted. No field, recipe or model is generated.

### `s2mod directional review`

Use `--help` to print the command options.

`s2mod directional review --project <project> --recipe <schema-10.json> --experimental --output-root <ignored-root>`
plans the exact recipe and publishes the same immutable matched surface comparison as
selection-preview. One summary reports factors, member/protection counts and all-LOD effects,
plus `previewPath` and `proofPath`. This is a bind-space prediction; runtime behavior is untested.

### `s2mod paired discover`

Agent-first JSON structural discovery (`component-discovery@7`). Labels select exact buffers,
not anatomy. Mapping alone is unsupported until authored fields pass the exact planner.

| Option | Required | Meaning |
|---|---:|---|
| `--project` | Yes | Immutable imported project root. |
| `--experimental` | Yes | Acknowledge unqualified runtime consumers. |
| `--help` | No | Print help. |

### `s2mod paired inspect`

Publishes complete current members, source positions/context and root-bone assertions for
explicit author choices. Copy the reported members into closed pair options; never edit draw IDs.

| Option | Required | Meaning |
|---|---:|---|
| `--project` | Yes | Immutable project root. |
| `--component` | Yes | Current schema-7 candidate ID; repeat for a deduplicated union. |
| `--output-root` | Yes | Configured ignored source-report root. |
| `--experimental` | Yes | Explicit acknowledgement. |
| `--help` | No | Print help. |

### `s2mod paired scaffold`

Binds current discovery/members to `paired_directional_field_options@1`, runs the exact
source planner, then writes one canonical recipe atomically. Existing output is refused.

| Option | Required | Meaning |
|---|---:|---|
| `--project` | Yes | Immutable project root. |
| `--component` | Yes | Current schema-7 candidate ID; repeat for an exact union. |
| `--options` | Yes | Complete source-bound options JSON, at most 1 MiB. |
| `--output` | Yes | New recipe path. |
| `--experimental` | Yes | Explicit acknowledgement. |
| `--help` | No | Print help. |

### `s2mod paired review`

Runs the same planner and publishes one matched all-LOD surface comparison and closed summary.
Front/side/top show source and prediction at equal camera/scale, the common region and each
partner separately. Local spans and positive/negative axis reaches from authored pivots measure
identical changed records; zero spans give explicit
null percentages. Protection and procedural context stay fixed. These are bind-space positions;
packed-frame counts are planned, and animation, garment fit and runtime remain untested.
Use the normal build/verify/minimal-package commands after review.

| Option | Required | Meaning |
|---|---:|---|
| `--project` | Yes | Immutable project root. |
| `--recipe` | Yes | Canonical schema-11 paired recipe. |
| `--output-root` | Yes | Configured ignored comparison root. |
| `--experimental` | Yes | Explicit acknowledgement. |
| `--help` | No | Print help. |

### `s2mod plan`

Resolves a recipe against immutable current input without producing model bytes.

| Option | Required | Meaning |
|---|---:|---|
| `--project` | Yes | Project root. |
| `--recipe` | Yes | Recipe JSON document. |
| `--format` | No | `text` or `json`; default `text`. |
| `--help` | No | Print help. |

### `s2mod build`

Rewrites, reopens, verifies, and atomically publishes a model build.

| Option | Required | Meaning |
|---|---:|---|
| `--project` | Yes | Project root. |
| `--recipe` | Yes | Recipe JSON document. |
| `--help` | No | Print help. |

### `s2mod verify`

Reopens and independently verifies an existing build.

| Option | Required | Meaning |
|---|---:|---|
| `--project` | Yes | Project root. |
| `--build` | Yes | Published build ID. |
| `--help` | No | Print help. |

### `s2mod package create`

Creates a replace-source package by replacing exactly one model entry in an immutable source VPK.

| Option | Required | Meaning |
|---|---:|---|
| `--project` | Yes | Project root. |
| `--build` | Yes | Verified model build ID. |
| `--source-vpk` | Yes | Immutable source mod VPK. |
| `--source-vpk-sha256` | Yes | Expected source VPK SHA-256. |
| `--require-external` | No | Require Source 2 Viewer verification. |
| `--help` | No | Print help. |

### `s2mod package create-minimal`

Creates a deterministic one-entry VPK from a verified model build. This is preferred for new
vanilla candidates.

| Option | Required | Meaning |
|---|---:|---|
| `--project` | Yes | Project root. |
| `--build` | Yes | Verified model build ID. |
| `--require-external` | No | Require Source 2 Viewer verification. |
| `--help` | No | Print help. |

### `s2mod package verify`

Reopens and verifies a published minimal or replace-source package.

| Option | Required | Meaning |
|---|---:|---|
| `--project` | Yes | Project root. |
| `--package` | Yes | Published package ID. |
| `--require-external` | No | Require Source 2 Viewer verification. |
| `--help` | No | Print help. |

### `s2mod addons inventory`

Lists active addon archives and collision facts for a package. It does not change the addon root.

| Option | Required | Meaning |
|---|---:|---|
| `--addons-root` | Yes | Deadlock addon directory or isolated test root. |
| `--project` | Yes | Project root. |
| `--package` | Yes | Published package ID. |
| `--help` | No | Print help. |

### `s2mod addons install`

Explicitly installs a package into a safe empty addon slot and creates a receipt.

| Option | Required | Meaning |
|---|---:|---|
| `--addons-root` | Yes | Deadlock addon directory or isolated test root. |
| `--project` | Yes | Project root. |
| `--package` | Yes | Published package ID. |
| `--slot` | No | `auto` (default) or an explicit two-digit empty slot. |
| `--help` | No | Print help. |

### `s2mod addons install-current`

The agent/player checkpoint route for a reviewed minimal package. It hashes the current model and
every imported dependency, reverifies the model and package, then rereads source at the same
workflow's install handoff. Missing/drifted source, failed reads, stale linkage or verification failure
stop before managed addon writes. Equal relevant bytes may proceed under changed archive provenance.
The version-1 `source-preflight.schema.json` report records both observations and verified linkage;
a saved report cannot authorize a later install. This is a bounded snapshot, not an update lock.

| Option | Required | Meaning |
|---|---:|---|
| `--addons-root` | Yes | Explicit addon destination. |
| `--project` | Yes | Immutable project root. |
| `--package` | Yes | Reviewed minimal package ID. |
| `--expected-package-hash` | Yes | SHA-256 of that reviewed package. |
| `--base-vpk` | Yes | Current archive containing the model and every imported dependency. |
| `--slot` | No | `auto` (default) or an explicit empty slot. |

Model verification and required external package verification must succeed. Explicit owner install
authorization remains required. The JSON result contains `installation` and `preflight`; obtain the
receipt from `result.installation.receipt`. Receipt, hash, load-order, DMM and rollback rules are
unchanged. The earlier `addons install` route retains its existing behavior and document meaning.

### `s2mod addons verify-active`

Checks an active receipt-backed installation, exact package hash, internal entry, collisions, and
unchanged `.dmm.json` state.

| Option | Required | Meaning |
|---|---:|---|
| `--addons-root` | Yes | Deadlock addon directory or isolated test root. |
| `--project` | Yes | Project root. |
| `--installation` | Yes | Installation receipt ID. |
| `--help` | No | Print help. |

### `s2mod addons rollback`

Disables only the exact archive named by an installation receipt.

| Option | Required | Meaning |
|---|---:|---|
| `--addons-root` | Yes | Deadlock addon directory or isolated test root. |
| `--project` | Yes | Project root. |
| `--installation` | Yes | Installation receipt ID. |
| `--help` | No | Print help. |

### `s2mod addons scan`

Read-only list of every addon archive: whether the game loads it (`pakNN_dir.vpk`), the Deadlock Mod Manager mod
it belongs to (from `.dmm.json` and the manager's `state.json`, both read only), and its file counts by resource kind
(model, animation, material, particles, sound, ui, localization, data, other). Only directory trees are read; an
optional cache keeps them by file size and modification time.

| Option | Required | Meaning |
|---|---:|---|
| `--addons-root` | Yes | Deadlock addon directory or isolated test root. |
| `--manager-state` | No | Deadlock Mod Manager `state.json` (default: its standard location under `%APPDATA%`). |
| `--cache` | No | Cache file for addon file lists. |
| `--format` | No | `text` (default) or `json`. |

### `s2mod addons install-file`

Installs one finished VPK (from any build route) into a free slot. It refuses, naming the mods, when an addon the
game loads changes any of the same files; it never takes a slot a mod-manager record uses; it copies and hashes the
package in one pass and records a receipt. Running it again with the same package changes nothing; a different package
needs `rollback-file` first. Text output is one line.

| Option | Required | Meaning |
|---|---:|---|
| `--addons-root` | Yes | Deadlock addon directory or isolated test root. |
| `--package` | Yes | VPK file to install (copied, never moved). |
| `--slot` | No | Two-digit slot or `auto` (highest free slot 90-99; default). |
| `--receipts` | Yes | Receipts folder for this installation (one installation at a time). |
| `--label` | No | Short name used in messages. |
| `--manager-state` | No | As for `addons scan`. |
| `--cache` | No | As for `addons scan`. |
| `--format` | No | `text` (default) or `json`. |

### `s2mod addons rollback-file`

Removes the file installed from a receipts folder, only when it still matches the installed package; other files are
never touched. Without an installation it changes nothing.

| Option | Required | Meaning |
|---|---:|---|
| `--addons-root` | Yes | Deadlock addon directory or isolated test root. |
| `--receipts` | Yes | Receipts folder used by `install-file`. |
| `--format` | No | `text` (default) or `json`. |

### `s2mod mods status`

Read-only. Lists every addon the game loads (`pakNN_dir.vpk`) with its Deadlock Mod Manager name, resource kinds and an
update status, so a game update that may have broken a mod is visible. Nothing is repaired, renamed or moved.

- A mod installed by S2ModKit (its file name and hash match an `installed` receipt in a `--receipts` folder) reports
  the game build recorded at install time: `installed on build X; game is now build Y` when they differ, `(current)`
  when equal, and an unknown-build notice when the receipt has no build.
- Any other mod is compared with the date of the current game build (`VersionDate` in `steam.inf`, start of that day,
  UTC): a file last written before it gets `game updated on <date> after this mod was installed; it may be outdated`;
  otherwise `installed after the current game update`.
- The first line reads `Game build 6821 (Oct 26 2026)` and adds `; new build recorded` when the build was not seen
  before. A new build is recorded once in the state folder: `builds/<build>/build.json`, `game-files.json` (CRC32 and
  size of every base game file that a loaded addon overrides, read from the directory tree of `citadel/pak01_dir.vpk`
  without reading payloads) and `files/<logical path>` (copies of overridden base files of kind data or localization
  that are at most 1 MiB). An existing build record is never rewritten. Acknowledgements are kept in
  `acks/<build>.json`.
- Notices that were acknowledged with `mods ack` are hidden in text mode (a count is shown); in JSON they stay in the
  list with `acknowledged: true`. A new game build starts without acknowledgements.
- A missing or malformed `citadel/steam.inf` stops the command with an error; the build is never guessed.

| Option | Required | Meaning |
|---|---:|---|
| `--game` | Yes | Deadlock `game` folder (contains `citadel`). |
| `--addons-root` | Yes | Deadlock addon directory or isolated test root. |
| `--state` | Yes | S2ModKit state folder (never defaulted; choose a folder that is not on the system drive). |
| `--receipts` | No | Receipts folder written by `addons install-file`; repeat the option for several folders. A missing folder is ignored. |
| `--manager-state` | No | As for `addons scan`. |
| `--format` | No | `text` (default) or `json`. |
| `--help` | No | Print help. |

### `s2mod mods ack`

Dismisses the update notice of one loaded addon (`--file`) or of all loaded addons (`--all`) for the current game
build. An acknowledgement is stored with the file size and modification time, so a replaced or changed file is not
acknowledged. It does not touch any addon file.

| Option | Required | Meaning |
|---|---:|---|
| `--game` | Yes | Deadlock `game` folder (contains `citadel`). |
| `--state` | Yes | State folder used by `mods status`. |
| `--file` | One of `--file` and `--all` | Loaded addon file name, for example `pak03_dir.vpk`. |
| `--all` | One of `--file` and `--all` | Acknowledge every loaded addon. |
| `--addons-root` | No | Addon directory (default: `citadel/addons` below `--game`). |
| `--format` | No | `text` (default) or `json`. |
| `--help` | No | Print help. |

### `s2mod addons merge`

Combines several addon archives into one new archive; inputs are never changed and an existing output is never
overwritten. Files of different resources merge freely. The same logical path with different content in two inputs is
a conflict: the command stops and lists it until it is resolved with `--prefer` (an input whose files win every
conflict; repeat to set an order) or `--keep <logical path>=<input file name>` (one file). Identical copies are not
conflicts. Which archive the game itself prefers is not assumed. A model in one input that uses a material or other
resource another input replaces is reported as a dependency note. Text output: one summary line plus one line per
conflict and note.

| Option | Required | Meaning |
|---|---:|---|
| `--input` | Yes | Input VPK file; repeat for each archive (at least two, distinct file names). |
| `--output` | Unless `--dry-run` | New merged VPK file. |
| `--prefer` | No | Input file name whose files win conflicts; repeatable. |
| `--keep` | No | `<logical path>=<input file name>` for one conflict; repeatable. |
| `--dry-run` | No | List inputs, conflicts and notes without writing. |
| `--format` | No | `text` (default) or `json`. |

### `s2mod runtime record`

Publishes immutable `player_observed` evidence after `addons verify-active` succeeds for the same
installation.

| Option | Required | Meaning |
|---|---:|---|
| `--project` | Yes | Project root. |
| `--installation` | Yes | Installation receipt ID. |
| `--observation` | Yes | Runtime-observation JSON input. |
| `--help` | No | Print help. |

## Position authoring

The `authoring` commands return JSON. `import-recipe` prepares a source-checked recipe for the normal
compiled build/verify/package lifecycle. See [position authoring](POSITION-AUTHORING.md).

| Command | Required options |
|---|---|
| `authoring export` | `--project`, `--selection`, `--output` (absent directory) |
| `authoring scene-create` | `--blender`, `--bridge-script`, `--bundle`, `--bundle-sha256`, `--output` (absent scene) |
| `authoring scene-extract` | `--blender`, `--bridge-script`, `--bundle`, `--bundle-sha256`, `--scene`, `--output` (absent return) |
| `authoring validate` | `--project`, `--selection`, `--bundle`, `--bundle-sha256`, `--delta` |
| `authoring import-recipe` | `--project`, `--selection`, `--bundle`, `--bundle-sha256`, `--delta`, `--output` (absent recipe) |
| `authoring import-request` | `--request` (closed version-1 validate/plan request) |
| `authoring compose-recipe` | `--project`, `--import-recipe`, `--remove-recipe`, `--output` (absent combined recipe) |

## Configuration variables

| Variable | Used by |
|---|---|
| `S2MODKIT_MESHOPTIMIZER_PATH` | Source 2 transform discovery, planning, and rewrite codec (optional: the release ships `meshoptimizer.dll` next to `s2mod.exe`). |
| `S2MODKIT_SOURCE2_VIEWER_PATH` | Optional external Source 2 Viewer verifier. |
| `S2MODKIT_EXTERNAL_VERIFY_ROOT` | Workspace-local scratch root for the external verifier. |

The CLI has no implicit game path, installation step, or global working-directory cache. Supply
roots explicitly.
