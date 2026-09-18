# Hero catalogue contract and maintenance

The hero catalogue is portable navigation data. It maps player-facing hero names to
compiled-model logical paths without granting mutation support.

## Contract

The strict Draft 7 schema is `schemas/hero-catalogue.schema.json` (version 1). A document contains:

- a portable catalogue ID and revision;
- Deadlock base-VPK provenance, with optional game-build and expected directory-hash evidence;
- canonical hero IDs, display names, normalized aliases, and roster status; and
- globally unique model-resource IDs, roles, normalized `.vmdl_c` logical paths, optionality, and
  qualification status.

Each hero has exactly one non-optional `primary_model`. `accessory_model` and `variant_model`
entries cover separately packaged resources. Qualification status is evidence scope only:
`unqualified`, `read_only_qualified`, `offline_static`, or `runtime_qualified`. It never means that
a particular operation is available.

The reviewed current-roster data is `catalogues/deadlock-current.json`. It contains only portable
logical resource paths and source metadata; the configured local VPK path remains a runtime input.

Catalogue documents contain no machine paths, selectors, draw-call IDs, mesh ordinals, bone
indices, coordinates, or copied game data. Unknown properties are rejected except under explicit
`extensions` objects.

## Runtime trust

Every locator is an untrusted hint until the configured VPK catalogue verifies it. Missing,
duplicate, ambiguous, or drifted entries fail closed. Structural profile analysis uses the opened
immutable artifact and inspected facts; it never receives a hero name or catalogue entry as a
dispatch key.

## Maintenance procedure

1. Inventory the configured base VPK through the read-only catalogue workflow.
2. Update names, aliases, roles, or logical paths as a reviewed data change.
3. Increment `revision`; set `gameBuild` and `expectedDirectoryHash` only from verified source
   evidence.
4. Validate the document against the schema and `HeroCatalogueValidator`.
5. Require lookup keys, resource IDs, and logical paths to remain unambiguous.
6. Run the whole-roster compatibility scan before raising qualification status.
7. Commit no Valve asset, extracted model, local VPK path, or detailed machine report.

A game update may move a resource or change its structure. Updating the locator does not preserve
an old compatibility result automatically; the new immutable input must be scanned again.
