# AI usage guide

S2ModKit is designed so an AI agent can inspect and plan a change without guessing Source 2
identities. The non-interactive commands are the automation contract; `interactive` is a convenient
human-guided front end.

Experimental editing is explicit: use `components list --experimental`, then scaffold with the
same discovery context and acknowledged `--experimental` policy. Never silently switch from a
strict rejection. Whole-part and axis-ramp region scaling preserve unresolved runtime metadata;
offline passes do not qualify culling, collision, clothing fit or hair. Installation is still a
separate authorized action. See [the experimental workflow](USER-GUIDE.md#experimental-whole-part-and-region-editing).

## Agent rules

Directional editing is agent-first. Use `components list --experimental --directional`, choose
source-derived schema-6 IDs, and run `directional inspect` with an ignored `--output-root`.
Its source report contains complete member maps, selected points, excluded context and optional
root-bone assertion candidates. `DIRECTIONAL_OPTIONS_REQUIRED` means structural mapping only;
it is not field admission. Complete bone closures can include excluded and unindexed records.

Author `directional_field_options@1` with the report's input hash, explicit preserve/reject
policies, model-space field/pivot/radii, XYZ factors, displacement cap and keep-fixed assertions.
Choose assertions explicitly. For a coordinate band, resolve source points to exact sorted vertex
indices and set hashes for every member/LOD, including empty rows; the writer never runs a
coordinate predicate. Do not invent anatomy, filter tiny nonzero weights or omit excluded bone
contributors. Unavailable assertion candidates are diagnostics, not permission to weaken protection.

Run `recipe scaffold --intent directional-field --experimental --directional-options ...` with
the same component IDs. It maps and plans the exact source before publishing a canonical recipe.
Then use `directional review` for one compact summary and comparison/proof paths, followed by
the existing `plan`, `build` and `verify` commands. Judge shape and measured effects before
offering a player checkpoint. Surface predictions alone do not prove animated fit or live behavior.
There is no directional interactive menu. See [CLI options](CLI-REFERENCE.md#s2mod-directional-inspect).

For a skinning/dependency rejection, `influences diagnose` supplies bounded per-buffer root/render
bone mappings and contributor counts, including tiny nonzero weights and unindexed vertices.
An optional existing common-field options file compares buffer-wide dependencies with hypothetical
changed/pinned position sets. Read the referenced JSON; the CLI envelope is a compact publication
summary. The report is advisory, probes all reported buffers and does not establish frame/simulation
closure or mutation permission. Exact input-bound planning remains required. See
[the diagnostic options](CLI-REFERENCE.md#s2mod-influences-diagnose).

1. Run `doctor` first and record its reported adapter and codec status.
2. Prefer `--format json` for commands that another tool must parse. Use text only for a human.
3. Import an immutable VPK or compiled model, then use inspection and component discovery before
   writing a recipe. Never invent draw-call IDs, mesh ordinals, paths, or LODs.
4. Treat unsupported or ambiguous results as a hard stop. Do not hand-edit a recipe to bypass them.
   For per-axis scale, rotation, or a typed pivot, require an available `transform_component@4`
   assessment and let `recipe scaffold --intent affine` dry-run the exact parameters.
5. Keep project, recipe, plan, build, package, and receipt paths in the configured workspace. Never
   overwrite an imported input or an active addon.
6. A successful offline verify is not live-game evidence. Installation requires explicit user
   authorization, and the agent must preserve and report the printed rollback command.
7. Keep Valve and user-supplied game assets outside the repository and never commit them.

## Quiet wrapper

Successful CLI calls can be reduced to one `OK` line with the included PowerShell wrapper:

```powershell
& .\scripts\Invoke-S2ModQuiet.ps1 -Cli .\s2mod.exe doctor --format text
```

Use the real CLI directly when the returned JSON, IDs, hashes, or diagnostics are needed.

## Guided workflow

`& .\s2mod.exe interactive` prompts for the session, catalogue, and source paths. With explicit
options, use `--catalogue`, `--session`, and one or more `--base-vpk`, `--mod-vpk`, or
`--compiled-model` options. Menus print `0` for a previous selection where navigation is available;
`back` and `previous` are accepted aliases. A prompted existing session resumes it.

## Evidence language

Use precise language in reports: “offline verified”, “package verified”, and “player-confirmed” are
different claims. Do not describe a catalogue qualification or static test as proof of rendering,
animation, collision, or gameplay in Deadlock.
