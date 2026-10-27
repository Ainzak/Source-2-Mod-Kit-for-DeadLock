using System.Globalization;
using System.Text.Json;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Infrastructure;

/// <summary>State folder of <c>mods status</c>: one immutable directory per game build and one acknowledgement file per build.</summary>
public sealed class ModStatusStore : IModStatusStore
{
    private const string BuildsFolder = "builds";
    private const string AcksFolder = "acks";

    public Task<bool> BuildExistsAsync(string stateRoot, int build, CancellationToken cancellationToken = default) =>
        Task.FromResult(Directory.Exists(BuildDirectory(stateRoot, build)));

    public async Task<bool> TryCreateBuildAsync(
        string stateRoot,
        GameBuildRecord record,
        GameFilesDocument files,
        IReadOnlyList<BuildFileCopy> copies,
        CancellationToken cancellationToken = default)
    {
        var final = BuildDirectory(stateRoot, record.Build);
        if (Directory.Exists(final))
        {
            return false;
        }

        var buildsRoot = Path.GetDirectoryName(final)!;
        Directory.CreateDirectory(buildsRoot);
        var temporary = Path.Combine(buildsRoot, ".tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(temporary, "build.json"), JsonDefaults.SerializeToUtf8(record), cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(temporary, "game-files.json"), JsonDefaults.SerializeToUtf8(files), cancellationToken).ConfigureAwait(false);
            var copyRoot = Path.GetFullPath(Path.Combine(temporary, "files")) + Path.DirectorySeparatorChar;
            foreach (var copy in copies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.GetFullPath(Path.Combine(copyRoot, copy.LogicalPath));
                if (!target.StartsWith(copyRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw Errors.Input("MODS_STATE_PATH_ESCAPE", $"'{copy.LogicalPath}' escapes the build record.", "Use a normalized logical path.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, copy.Content, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                Directory.Move(temporary, final);
            }
            catch (IOException) when (Directory.Exists(final))
            {
                return false;                                             // another run recorded this build first; never rewrite it
            }

            return true;
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }

    public async Task<IReadOnlyList<ModAcknowledgement>> LoadAcknowledgementsAsync(string stateRoot, int build, CancellationToken cancellationToken = default)
    {
        var path = AckFile(stateRoot, build);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var document = JsonSerializer.Deserialize<AckDocument>(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), JsonDefaults.Options);
            return document is { Version: AckDocument.CurrentVersion } && document.Build == build && document.Acknowledgements is not null
                ? document.Acknowledgements
                : [];
        }
        catch (JsonException)
        {
            return [];                                                    // a damaged file only shows the notices again
        }
    }

    public async Task SaveAcknowledgementsAsync(string stateRoot, int build, IReadOnlyList<ModAcknowledgement> acknowledgements, CancellationToken cancellationToken = default)
    {
        var path = AckFile(stateRoot, build);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var document = new AckDocument(AckDocument.CurrentVersion, build, acknowledgements);
            await File.WriteAllBytesAsync(temporary, JsonDefaults.SerializeToUtf8(document), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string BuildDirectory(string stateRoot, int build) =>
        Path.Combine(Root(stateRoot), BuildsFolder, build.ToString(CultureInfo.InvariantCulture));

    private static string AckFile(string stateRoot, int build) =>
        Path.Combine(Root(stateRoot), AcksFolder, build.ToString(CultureInfo.InvariantCulture) + ".json");

    private static string Root(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        return Path.GetFullPath(stateRoot);
    }

    private sealed record AckDocument(int Version, int Build, IReadOnlyList<ModAcknowledgement>? Acknowledgements)
    {
        public const int CurrentVersion = 1;
    }
}
