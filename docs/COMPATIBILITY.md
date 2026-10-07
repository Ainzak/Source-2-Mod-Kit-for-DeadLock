# S2ModKit compatibility matrix

Updated: 2026-10-07.
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
| `transform_component@7`, single ellipsoid or disjoint mirrored pair | Experimental, offline qualified | Abrams face single/mirrored fields; Mina umbrella handle/canopy fields | Smooth local scaling in one complete ordinary buffer per LOD; exact pinned words, differential transition frames and independent reopen. Mina's mirrored canopy player test showed no clear change, possibly a smaller-looking umbrella, and was rolled back; it does not qualify the intended appearance. Mirroring retains asymmetry; broader field parameters can fail triangle or certificate checks. |
| `transform_component@8`, common field across ordinary buffers | Experimental, offline qualified; bounded owner appearance report | Abrams face/head-detail/teeth, three members across four LODs | Atomic common-field geometry and complete shared box closure. Owner accepts the enlarged head and neck; the report arrived after rollback and is not an active-bound formal runtime record. Animation, LOD transitions, garment fit and preserved zero-field consumers remain unqualified. |
| `transform_component@9`, protected directional ellipsoid | Source planning, atomic writing, independent reopen, verified workspace builds and read-only surface comparisons | Synthetic fixtures and one hash-pinned body source | Complete ordinary ownership, prescribed position/frame words, protection, source coincidences, combined contributors/boxes and unchanged data are independently checked. Source-backed discovery/scaffolding and matched all-LOD comparisons are available; one Bebop forearm package has bounded owner appearance feedback after rollback. Optional animation/LOD/culling/collision checks remain unqualified. |
| `transform_component@10`, disjoint protected directional pair with explicit compatibility preservation | Experimental, offline qualified; bounded owner appearance report | Wraith chest and a separate arm structural case across three LODs | Exact source-backed members, independent compiled reconstruction and matched regional measurements. Owner accepts the installed chest increase after rollback; the arm case is offline only. Preserves source-coincidence partitions and serialized procedural inputs without proving simulation independence. Sphere/proxy/PHYS coherence, pose/garment fit and LOD transitions remain unqualified. |
| Guarded current-source installation | Supported for verified minimal packages | Wraith current model/dependency preflight, exact active hash and verified receipt rollback; synthetic stale/linkage failures | Reopens the actual model and every imported dependency from the current game archive twice and independently verifies candidate/package/linkage. Saved preflight success cannot authorize a later installation. Explicit installation authorization remains required. |
| Current-roster coverage | Measured offline | 39 active and five experimental heroes, 45 resource locators | 44 resources pass strict discovery; Priest remains inspection-only. Experimental probes add 26 selections across 15 resources; 43 resources have some transform before and after opt-in. A probe is not a completed build or a player test. |
| Non-empty external mesh resource handles | Unsupported | Structural synthetic tests | Requires a future explicitly reviewed resource-resolution and rewrite profile. |
| Arbitrary topology, weight, and morph edits | Unsupported | — | No typed mutation or allowed-change contract. |

On the same October 3 Abrams and Mina inputs, all 27 candidate views and earlier transform
capabilities are unchanged. Local-field probes add six candidate views representing four unique
exact selectors (three Abrams, one Mina). Mirrored shaping is a new action on those enclosing
selectors, with each actual pair requiring an exact plan. One explicit acknowledged Abrams union
additionally qualifies coordination; individual discovery probes with reject zero policies do not
advertise it. These are new editing actions on two already supported resources, not new hero support
or a new roster-wide scan. Broad body, procedural hair and clothing-fit support remain unavailable.

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
| Recipe | 11 paired; 10 directional; earlier workflows retained | Versions 1–10 retain their meanings. Source-backed paired scaffolding emits recipe 11 only after exact planning. |
| Mutation plan | 7 | Legacy and versions 2–6 retain their meanings. Schema 7 freezes paired proof, exact member/context/protection and combined final-state facts; independent reconstruction is mandatory before build publication. |
| Build evidence | 12 | Versions 1–11 retain their meanings. Schema 12 distinguishes planned paired facts from independently observed build/reverify facts. Procedural consumer obligations remain unqualified. |
| Package evidence | 2 | Version 1 replace-source packages remain readable. |
| Component discovery | 2 default; 4 local; 5 coordinated; 6 directional; 7 paired opt-in | Earlier versions remain readable; pair mappings remain unsupported until explicit options pass exact source planning. |
| Guided session | 1 default; 3 local opt-in; 4 coordinated opt-in | Earlier versions retain their meanings. Schema 4 freezes exact selected IDs, common-field options and preview identity. Existing sessions never silently upgrade. |
| Installation receipt | 1 | Current. |
| Runtime observation | 1 | Active-bound recording only; direct after-rollback owner feedback is separately labelled. |
| Current-source preflight report | 1 | Closed diagnostic only; install repeats current archive checks. |
| Paired field options / comparison | 1 / 1 | Closed exact source bindings and matched regional observations. |
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
