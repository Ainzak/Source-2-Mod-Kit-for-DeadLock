using S2ModKit.Domain;

namespace S2ModKit.Desktop;

internal sealed partial class ModCatalog
{
    /// <summary>
    /// Repair for one finding of the mod check, as one repaired copy of the mod (the original kept disabled as its old version):
    /// hero models older than the game's get the game's current settings and attachment points (<c>restore_model_settings@1</c>,
    /// see <see cref="S2ModKit.Adapters.Source2.CompiledModelRestore"/>; each restored model is checked by reading it again), and
    /// stale game data files are left out. A <c>.repair.json</c> beside the package keeps the inputs' and outputs' hashes and what
    /// was restored. Refused while the game runs.
    /// </summary>
    public async Task<CatalogChange> RepairFindingAsync(ModEntry mod, DoctorFinding finding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(finding);
        Management.EnsureGameStopped();
        if (mod.Files.Count != 1 || !mod.Loaded)
        {
            throw Errors.Input("DOCTOR_REPAIR_UNSUPPORTED", "Only an enabled mod in one archive can be repaired here.", "Enable the mod first; split mods are not repaired yet.");
        }

        var game = Path.Combine(settings.Game, "citadel", "pak01_dir.vpk");
        var replace = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var restored = new List<object>();
        foreach (var model in (finding.RestoreModels ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var modBytes = ReadFirst(mod.Files, model)
                ?? throw Errors.Input("DOCTOR_REPAIR_MISSING", $"'{mod.Name}' no longer has '{model}'.", "Check mods again.");
            var gameBytes = ReadFirst([game], model)
                ?? throw Errors.Input("DOCTOR_REPAIR_MISSING", $"The game no longer has '{model}'.", "Check mods again.");
            var gaps = S2ModKit.Adapters.Source2.CompiledModelRestore.Gaps(modBytes, gameBytes, model);
            if (!gaps.Any)
            {
                continue;
            }

            var output = await Task.Run(() => S2ModKit.Adapters.Source2.CompiledModelRestore.Restore(modBytes, gameBytes, model), cancellationToken).ConfigureAwait(false);
            if (S2ModKit.Adapters.Source2.CompiledModelRestore.Gaps(output, gameBytes, model).Any)
            {
                throw Errors.Verification("DOCTOR_REPAIR_VERIFY", $"The repaired '{Path.GetFileName(model)}' still lacks what was added.", "Report the mod.");
            }

            replace[model] = output;
            restored.Add(new
            {
                model,
                inputSha256 = Sha256(modBytes),
                gameSha256 = Sha256(gameBytes),
                outputSha256 = Sha256(output),
                settings = gaps.Settings,
                attachments = gaps.Attachments,
                notRestored = gaps.Skipped,
            });
        }

        if (replace.Count == 0 && finding.StaleOverrides.Count == 0)
        {
            throw Errors.Input("DOCTOR_NOTHING_TO_REPAIR", $"'{mod.Name}' has nothing left to repair.", "Check mods again.");
        }

        var evidence = new
        {
            operation = replace.Count > 0 ? "restore_model_settings@1" : "drop_stale_game_files@1",
            mod = mod.Name,
            inputSha256 = Sha256(await File.ReadAllBytesAsync(mod.Files[0], cancellationToken).ConfigureAwait(false)),
            gameBuild = await GameBuildAsync(cancellationToken).ConfigureAwait(false),
            createdUtc = clock.UtcNow,
            models = restored,
            dropped = finding.StaleOverrides,
        };
        return await RepairAsync(mod, finding.StaleOverrides, replace, evidence, cancellationToken).ConfigureAwait(false);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
}
