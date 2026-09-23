# S2ModKit compatibility matrix

Updated: 2026-09-23.
Status: current pre-alpha production boundary.

Compatibility is determined from inspected structure, not a hero allowlist. A named hero below is
qualification evidence for a reusable profile; it does not enable a character-specific branch.

## Discovery and mutation profiles

| Capability or storage profile | Status | Qualification evidence | Important limit |
|---|---|---|---|
| `material_group` discovery | Supported | Graves, Apollo, Pocket, Wraith, Haze | One material may cover several visual objects. |
| `mesh_lineage` discovery | Supported when exact and complete | Haze left/right guns across four LODs | Requires one canonical source-authored mesh name per present LOD; no ordinal or suffix guessing beyond the exact authoritative `_lodN` normalization. |
| `remove_component@1`, root MDAT with MVTX/MIDX | Runtime qualified | Graves backpack, Apollo sword/gemstone, Haze left gun | Removes complete draw-call records only; geometry buffers are not compacted. |
| `transform_component@1`, packed weighted skinning | Runtime qualified | Apollo sword/gemstone | Positive uniform scale plus translation over exclusively owned vertices; PHYS remains unchanged. |
| `transform_component@1`, rigid single-influence skinning | Runtime qualified | Pocket suitcase | Requires valid slot-zero bone indices and reproducible per-bone culling facts. |
| `transform_component@1`, precise Haze gun lineage | Runtime qualified | Haze left gun | Player confirmed the `2.0x` result in gameplay, animations, every LOD, and the hero menu; the managed receipt is rolled back. |
| `transform_component@2`, embedded raw MBUF with coupled convex PHYS | Offline qualified | Holliday standalone hat | Positive uniform scale only; visual and collision rewrites are atomic. This resource is not the visible worn hat in the current hero model. |
| `transform_component@3`, connected-component vertices | Runtime qualified | Holliday visible worn hat inside the primary body draw call | Scales explicitly selected disconnected vertex islands across every LOD; topology, skinning, PHYS, and unselected vertices remain unchanged. |
| `transform_component@4`, characterized root MVTX/MIDX | Offline qualified | Abrams gun, four LODs | Typed selection/point/face/bone pivots, per-axis scale, and rotation; affected packed frames and reproducible bounds are rewritten and reopened. Live-game behavior is untested. |
| `transform_component@4`, one selected buffer in a multi-buffer mesh | Offline qualified | Abrams teeth, four LODs | Selected positions/frames and derived scene/bone bounds change; other buffers and indices remain unchanged. Other body selections still fail their bounds or distance-field checks. Live-game behavior is untested. |
| Current-roster read-only coverage | Measured | 38 active heroes plus Holliday's standalone hat | 34 of 39 resources inspect successfully; five still share the unsupported `LOD_MASK_UNSUPPORTED` layout. The standalone hat supplies the only real version-2 resource in this catalogue. |
| Non-empty external mesh resource handles | Unsupported | Structural synthetic tests | Requires a future explicitly reviewed resource-resolution and rewrite profile. |
| Arbitrary topology, weight, and morph edits | Unsupported | — | No typed mutation or allowed-change contract. |

The current catalogue has 44 available version-4 candidate views. Eleven of those lack an available
version-1 transform. These are offline capability assessments, not 44 player-tested mods.

## Persisted contract compatibility

| Contract | Current writer | Retained compatibility |
|---|---:|---|
| Project manifest | 2 | Version 1 schema/reader retained. |
| Recipe | 5 | Versions 1–4 remain readable with unchanged meaning under versioned schemas. |
| Build evidence | 6 | Versions 1–5 remain published under versioned schemas. |
| Package evidence | 2 | Version 1 replace-source packages remain readable. |
| Component discovery | 2 | Version 1 material-group schema remains published under `schemas/v1`; new CLI output is version 2. |
| Installation receipt | 1 | Current. |
| Runtime observation | 1 | Current. |
| Hero catalogue | 1 | New additive navigation contract; no earlier persisted versions. |

## Reading the status

- **Supported** means the public inspection/discovery contract is implemented and tested.
- **Runtime qualified** means an exact installed artifact was observed by the player and rolled back
  through its receipt.
- **Offline and active-install verified** proves the candidate and active bytes, not rendering,
  animation, LOD behavior, or collision in Deadlock.
- **Read-only geometry inspection** means geometry and ownership facts are available while mutation
  remains unavailable.
- **Unsupported** is an intentional fail-closed result, not permission to hand-edit a recipe.

The CLI and user guide describe the public safety boundary and the commands used to inspect a
resource before attempting a mutation.
