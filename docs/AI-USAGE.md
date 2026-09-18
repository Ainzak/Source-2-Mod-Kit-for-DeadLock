# AI usage guide

S2ModKit is designed so an AI agent can inspect and plan a change without guessing Source 2
identities. The non-interactive commands are the automation contract; `interactive` is a convenient
human-guided front end.

## Agent rules

1. Run `doctor` first and record its reported adapter and codec status.
2. Prefer `--format json` for commands that another tool must parse. Use text only for a human.
3. Import an immutable VPK or compiled model, then use inspection and component discovery before
   writing a recipe. Never invent draw-call IDs, mesh ordinals, paths, or LODs.
4. Treat unsupported or ambiguous results as a hard stop. Do not hand-edit a recipe to bypass them.
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
