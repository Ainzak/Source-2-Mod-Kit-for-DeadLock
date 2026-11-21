using System.CommandLine;
using System.Security.Cryptography;
using S2ModKit.Adapters.Source2;
using S2ModKit.Adapters.Vpk;
using S2ModKit.Domain;

namespace S2ModKit.Cli;

/// <summary>Result of <c>s2mod models update</c>: the models updated and what each received.</summary>
public sealed record ModelUpdateReport(string Operation, string InputSha256, string? OutputSha256, IReadOnlyList<ModelUpdateEntry> Models, int Entries);

public sealed record ModelUpdateEntry(string Model, IReadOnlyList<string> Settings, IReadOnlyList<string> Attachments, IReadOnlyList<string> NotRestored);

public sealed partial class S2ModKitCli
{
    private static Command CreateModelsCommand(TextWriter output, TextWriter error)
    {
        var command = new Command("models", "Model helpers for finished mod archives.");
        var update = new Command("update", "Copy what later game updates added to the game's hero models (cameras, sounds, hero-select scene, attachments) into an older mod archive; writes a new archive (restore_model_settings@1).");
        var package = RequiredStringOption("--package", "Mod archive (_dir.vpk) to update; never changed.");
        var game = RequiredStringOption("--game", "Deadlock 'game' folder (contains 'citadel').");
        var outputPath = RequiredStringOption("--output", "New archive to write (must not exist). Nothing is written when no model lacks anything.");
        var format = CreateFormatOption();
        foreach (var option in new Option[] { package, game, outputPath, format })
        {
            update.Options.Add(option);
        }

        update.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "models.update", IsJson(parseResult.GetRequiredValue(format)), output, error,
            () => UpdateModelsAsync(parseResult.GetRequiredValue(package), parseResult.GetRequiredValue(game), parseResult.GetRequiredValue(outputPath), cancellationToken),
            (writer, report) => writer.WriteLine(report.OutputSha256 is null
                ? "Up to date: no model lacks anything the game added."
                : $"Updated {report.Models.Count} model(s): {string.Join("; ", report.Models.Select(model => $"{Path.GetFileName(model.Model)} +{model.Settings.Count + model.Attachments.Count}"))}."),
            cancellationToken));
        command.Subcommands.Add(update);
        return command;
    }

    private static async Task<ModelUpdateReport> UpdateModelsAsync(string package, string game, string target, CancellationToken cancellationToken)
    {
        if (File.Exists(target))
        {
            throw Errors.Input("MODEL_UPDATE_OUTPUT_EXISTS", $"'{target}' exists.", "Pass a new file; inputs are never overwritten.");
        }

        var gameArchive = Path.Combine(game, "citadel", "pak01_dir.vpk");
        var content = new ValvePakAddonArchiveContent();
        var entries = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        using (var source = new SteamDatabase.ValvePak.Package())
        {
            source.Read(package);
            foreach (var entry in source.Entries!.SelectMany(group => group.Value))
            {
                source.ReadEntry(entry, out var bytes);
                entries[entry.GetFullPath()] = bytes;
            }
        }

        var updated = new List<ModelUpdateEntry>();
        foreach (var model in entries.Keys.Where(path => path.StartsWith("models/heroes", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            byte[] gameBytes;
            try
            {
                gameBytes = await content.ReadEntryAsync(gameArchive, model, cancellationToken).ConfigureAwait(false);
            }
            catch (S2ModKitException)
            {
                continue;                                                    // a model the game does not have: nothing to compare with
            }

            var modBytes = entries[model].ToArray();
            var gaps = CompiledModelRestore.Gaps(modBytes, gameBytes, model);
            if (!gaps.Any)
            {
                continue;
            }

            var restored = CompiledModelRestore.Restore(modBytes, gameBytes, model);
            if (CompiledModelRestore.Gaps(restored, gameBytes, model).Any)
            {
                throw Errors.Verification("MODEL_UPDATE_VERIFY", $"The updated '{Path.GetFileName(model)}' still lacks what the game added.", "Report the model.");
            }

            entries[model] = restored;
            updated.Add(new ModelUpdateEntry(model, gaps.Settings, gaps.Attachments, gaps.Skipped));
        }

        var input = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(package, cancellationToken).ConfigureAwait(false)));
        if (updated.Count == 0)
        {
            return new ModelUpdateReport("restore_model_settings@1", input, null, updated, entries.Count);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        await CompactVpkArchiveIO.WriteAsync(target, entries, cancellationToken).ConfigureAwait(false);
        var outputHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false)));
        return new ModelUpdateReport("restore_model_settings@1", input, outputHash, updated, entries.Count);
    }
}
