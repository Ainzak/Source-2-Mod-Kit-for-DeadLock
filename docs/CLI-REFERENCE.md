# S2ModKit CLI reference

This is the public reference for the current pre-alpha CLI. It is contract-checked against actual
`--help` output by `CliContractTests.CliReferenceDocumentsEveryPublicHelpOption`. When a public help
option changes, the test requires this document to change in the same commit.

Run `s2mod <command> --help` for the host-localized help layout. Command descriptions and option
spellings below are the stable English contract.

## Output and exit codes

- `doctor`, `catalog heroes list`, `catalog resolve`, `compatibility scan`, `inspect`,
  `components list`, and `plan` accept `--format text|json`; default is `text`.
- Commands that publish or manage artifacts return a JSON envelope on stdout by design.
- In JSON mode stdout contains only the envelope; diagnostics belong on stderr.
- `interactive` is a human terminal workflow and intentionally has no JSON mode.
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
| Diagnostics | `doctor` |
| Guided workflow | `interactive` |
| Hero catalogue | `catalog heroes list`, `catalog resolve` |
| Compatibility | `compatibility scan` |
| Projects | `project create`, `project create-vpk` |
| Inspection and authoring | `inspect`, `components list`, `recipe scaffold`, `plan` |
| Candidate lifecycle | `build`, `verify`, `package create`, `package create-minimal`, `package verify` |
| Managed addon lifecycle | `addons inventory`, `addons install`, `addons verify-active`, `addons rollback`, `runtime record` |

### `s2mod doctor`

Checks configured adapter, verifier, and geometry-codec availability.

| Option | Required | Meaning |
|---|---:|---|
| `--format` | No | `text` or `json`; default `text`. |
| `--help` | No | Print help. |

### `s2mod interactive`

Starts or explicitly resumes a version-1 guided session. Tasks 22–26 implement configured source,
catalogue-backed hero/resource selection, available component/action selection, bounded parameters,
canonical recipe scaffolding, concise dry-run, output publication, and the optional managed install
lifecycle. Enter `cancel`, `quit`, or `q` at a prompt to save and stop. Completed project, recipe,
build, package, or export artifacts remain checkpointed; an active receipt-backed installation is
never hidden and its exact rollback command is printed. Resource and component menus print `0` as
the previous-menu choice; `back` and `previous` are accepted aliases. This can return from component
selection through resource selection to a different hero without starting a new guided session.
Navigation reuses the in-memory catalogue selection; opening an unvisited resource still performs
the required model/dependency import into its own immutable internal project.

Base VPKs must match the catalogue's pinned source identity. Mod VPKs offer only exact locators
they contain. A standalone compiled model offers catalogue resources with the same compiled
filename. Missing, moved, duplicate, and ambiguous locators are never selectable.

The guided client derives session-owned immutable project paths and a recipe path beside `--session`
and calls the same Application discovery, scaffolding, and planning use cases as the non-interactive
commands. Each selected source/resource pair has a stable internal project path, allowing an earlier
selection to be reused safely. It offers only capabilities reported `available`. Uniform scale is
bounded to `0.25..4.0` excluding identity; movement axes are bounded to `-256..256`; required visual
and collision displacement caps are greater than zero and at most `256`. The dry-run reports the
selected draw calls, LODs, planned block changes, selected vertices, and coupled collision status,
then offers recipe/plan-only, verified build, minimal package, or atomic verified VPK export.
Package paths continue to explicit no-install, automatic verified slot, or explicit verified slot
choices. Installation shows the exact package and destination and accepts only the exact word
`install`; it then verifies the active receipt and offers immediate rollback or a player-test hold.
The interaction is keyboard-only, uses numbered choices and plain sequential text without ANSI or
cursor control, and does not depend on reading terminal dimensions. Invalid choices are rejected at
the same prompt. Narrow or redirected writers can wrap output without changing workflow state.

| Option | Required | Meaning |
|---|---:|---|
| `--catalogue` | No | Versioned hero catalogue JSON file; prompted when omitted for a new session. |
| `--session` | No | New or existing local checkpoint; prompted when omitted. A prompted existing path is resumed. |
| `--base-vpk` | No | Read-only base `*_dir.vpk`; repeat as needed. Source type/path are prompted when all source options are omitted. |
| `--mod-vpk` | No | Read-only mod `*_dir.vpk`; repeat as needed. |
| `--compiled-model` | No | Compiled `.vmdl_c`; repeat as needed. |
| `--resume` | No | Resume the existing checkpoint; source options are then forbidden. |
| `--expert` | No | Show stable IDs, paths, and unavailable-resource details. |
| `--help` | No | Print help. |

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

Lists discovery-v2 `material_group` and exact source-authored whole-mesh `mesh_lineage` candidates
with capability explanations. The two kinds may overlap. Text and JSON include material membership,
per-LOD draw calls, and, for a lineage, its mechanical source label and exact mesh/block membership.
The command is read-only. Capability output reports the available operation version; the coupled
raw-MBUF/convex-PHYS profile reports `transform_component@2`.

| Option | Required | Meaning |
|---|---:|---|
| `--project` | Yes | Project root. |
| `--format` | No | `text` or `json`; default `text`. |
| `--help` | No | Print help. |

### `s2mod recipe scaffold`

Creates a new canonical existing-operation recipe from current candidate IDs. It does not create a
plan or a model and never overwrites `--output`.

| Option | Required | Meaning |
|---|---:|---|
| `--project` | Yes | Project root. |
| `--component` | Yes | Candidate ID from `components list`; repeat for an explicit union. |
| `--intent` | Yes | `remove`, `uniform-scale`, or `translate`. |
| `--output` | Yes | New recipe JSON path. |
| `--scale` | For `uniform-scale` | Positive uniform scale in the accepted operation range. |
| `--translate-x` | For `translate` | Optional finite X translation. |
| `--translate-y` | For `translate` | Optional finite Y translation. |
| `--translate-z` | For `translate` | Optional finite Z translation. |
| `--reference-lod` | No | Pivot LOD; default is the lowest selected LOD. |
| `--max-displacement` | For transforms | Positive safety cap, at most 256 Source units. |
| `--max-collision-displacement` | For discovered `transform_component@2` | Independent positive collision cap, at most 256 Source units. |
| `--help` | No | Print help. |

Scaffolding emits recipe schema version 1 for removal, version 2 for `transform_component@1`, and
version 3 for the discovered coupled `transform_component@2` profile. Version 2 requires
`uniform-scale`, zero translation, and both displacement caps; it never falls back to a visual-only
rewrite.

Recipe schema version 4 also accepts `transform_component@3` at
`connected_component_vertices` granularity. It requires explicit `connectedComponentIdsByLod` and
expected vertex totals from current inspect evidence. Automatic scaffolding for arbitrary island
groups is not yet exposed; manual recipes still pass the same strict plan/build verification.

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

### `s2mod runtime record`

Publishes immutable `player_observed` evidence after `addons verify-active` succeeds for the same
installation.

| Option | Required | Meaning |
|---|---:|---|
| `--project` | Yes | Project root. |
| `--installation` | Yes | Installation receipt ID. |
| `--observation` | Yes | Runtime-observation JSON input. |
| `--help` | No | Print help. |

## Configuration variables

| Variable | Used by |
|---|---|
| `S2MODKIT_MESHOPTIMIZER_PATH` | Source 2 transform discovery, planning, and rewrite codec. |
| `S2MODKIT_SOURCE2_VIEWER_PATH` | Optional external Source 2 Viewer verifier. |
| `S2MODKIT_EXTERNAL_VERIFY_ROOT` | Workspace-local scratch root for the external verifier. |

The CLI has no implicit game path, installation step, or global working-directory cache. Supply
roots explicitly.
