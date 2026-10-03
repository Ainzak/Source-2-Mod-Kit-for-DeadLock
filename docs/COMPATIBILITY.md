# S2ModKit compatibility matrix

Updated: 2026-10-03.
Status: current pre-alpha production boundary.

Compatibility is determined from inspected structure, not a hero allowlist. A named hero below is
qualification evidence for a reusable profile; it does not enable a character-specific branch.

The packaged catalogue targets Steam build 25689475. Earlier player observations below apply to
their tested model revisions, not automatically to this game update. Current offline checks cover
Apollo's sword, Haze's left gun (scale and removal), Pocket's suitcase, Holliday's separate and worn
hats, and Grey Talon's eyes. The owner also tested experimental head/region deformation and Mina's
umbrella, then rolled back. These observations do not qualify other models or all runtime behavior.

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
| `transform_component@3`, connected-component vertices | Current input offline qualified; earlier player test | Holliday visible worn hat inside the primary body draw call | Updated geometry needs fresh exact island IDs. A reviewed `2.0x` selection across all four LODs passes build, reopen and minimal-package verification; topology, skinning, PHYS, and unselected vertices remain unchanged. |
| `transform_component@4`, characterized root MVTX/MIDX | Offline qualified | Abrams gun, four LODs | Typed selection/point/face/bone pivots, per-axis scale, and rotation; affected packed frames and reproducible bounds are rewritten and reopened. Live-game behavior is untested. |
| `transform_component@4`, one selected buffer in a multi-buffer mesh | Offline qualified | Abrams teeth, four LODs | Selected positions/frames and derived scene/bone bounds change; other buffers and indices remain unchanged. Other body selections still fail their bounds or distance-field checks. Live-game behavior is untested. |
| `transform_component@5`, complete root buffer visual scale | Experimental, explicit opt-in | Grey Talon head and Mina umbrella player observations; Yamato shortsword and Werewolf gun offline builds | Preserves unresolved spheres/proxies/collision without claiming coherence. Procedural/cloth, morphs, flat affected boxes and incomplete ownership reject. |
| `transform_component@6`, bounded axis-ramp region scale | Experimental, explicit opt-in | Grey Talon region player observation | Pins a half-space and updates transition shading. Crude head shape and buried hair remain observed limitations; not automatic anatomy or sculpting. |
| Current-roster coverage | Measured offline | 39 active and five experimental heroes, 45 resource locators | 44 resources pass strict discovery; Priest remains inspection-only. Experimental probes add 26 selections across 15 resources; 43 resources have some transform before and after opt-in. A probe is not a completed build or a player test. |
| Non-empty external mesh resource handles | Unsupported | Structural synthetic tests | Requires a future explicitly reviewed resource-resolution and rewrite profile. |
| Arbitrary topology, weight, and morph edits | Unsupported | — | No typed mutation or allowed-change contract. |

The October 1 patch preserved 43 of 45 model hashes; changed Billy and Rat King models were
rescanned rather than inheriting old results. A complete Grey Talon eye-material selection passed
offline three-LOD build, reopen verification, and minimal-package verification. Yamato's complete
body candidate and Werewolf's broader body selections
still fail affected bone-bound reproduction; no broad body-transform support is claimed. Offline
capability is not player-tested behavior.

Models containing a mesh assigned to no LOD group inspect read-only: the reported LODs omit the
nonparticipating opaque mesh, whose raw block remains inventoried, and the whole resource stays
inspection-only.

## Persisted contract compatibility

| Contract | Current writer | Retained compatibility |
|---|---:|---|
| Project manifest | 2 | Version 1 schema/reader retained. |
| Recipe | 7 | Versions 1–6 remain readable with unchanged meaning under versioned schemas. Experimental whole-part/region recipes require an explicit preservation policy. |
| Build evidence | 8 | Versions 1–7 remain published under versioned schemas. Experimental consumer obligations remain visibly untested. |
| Package evidence | 2 | Version 1 replace-source packages remain readable. |
| Component discovery | 2 default; 3 opt-in | Versions 1–2 remain published; experimental output preserves candidate kinds but has separately bound identities. |
| Guided session | 1 default; 2 opt-in | Existing strict sessions do not silently upgrade to experimental mode. |
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

Mina's umbrella is already eligible for strict uniform scaling: its experimental test is a
cross-model check, not proof of a newly unlocked selection. Affine eligibility is a separate check.
New package IDs bind build provenance as well as archive bytes, so equal outputs from different
recipes do not collide. Previously published packages and their evidence remain readable unchanged.

The CLI and user guide describe the public safety boundary and the commands used to inspect a
resource before attempting a mutation.
