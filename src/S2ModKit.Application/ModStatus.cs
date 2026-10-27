using System.Globalization;
using S2ModKit.Domain;

namespace S2ModKit.Application;

/// <summary>Game build read from <c>steam.inf</c>: <c>ClientVersion</c> and <c>VersionDate</c> (kept as the text the
/// game wrote, plus the parsed calendar day).</summary>
public sealed record GameBuildInfo(int Build, string VersionDate, DateOnly VersionDay);

public static class SteamInf
{
    /// <summary>Parses the text of a <c>steam.inf</c>; a missing or malformed <c>ClientVersion</c> or <c>VersionDate</c>
    /// is an error, never a guess.</summary>
    public static GameBuildInfo Parse(string text, string sourceDescription)
    {
        ArgumentNullException.ThrowIfNull(text);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (key is "ClientVersion" or "VersionDate" && !values.TryAdd(key, line[(separator + 1)..].Trim()))
            {
                throw Invalid(sourceDescription, $"'{key}' appears more than once");
            }
        }

        if (!values.TryGetValue("ClientVersion", out var versionText)
            || !int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var build)
            || build <= 0)
        {
            throw Invalid(sourceDescription, "'ClientVersion' is missing or is not a positive whole number");
        }

        if (!values.TryGetValue("VersionDate", out var dateText)
            || !DateTime.TryParseExact(dateText, "MMM d yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
        {
            throw Invalid(sourceDescription, "'VersionDate' is missing or is not like 'Oct 26 2026'");
        }

        return new GameBuildInfo(build, dateText, DateOnly.FromDateTime(date));
    }

    private static S2ModKitException Invalid(string source, string reason) =>
        Errors.Input("GAME_STEAM_INF_INVALID", $"{source} is not usable: {reason}.", "Verify the game files in Steam, then retry. S2ModKit does not guess the game build.");
}

public interface IGameBuildReader
{
    /// <summary>Reads <c>citadel/steam.inf</c> below the Deadlock <c>game</c> folder; throws when it is missing or malformed.</summary>
    Task<GameBuildInfo> ReadAsync(string gameDirectory, CancellationToken cancellationToken = default);

    /// <summary>Reads the <c>steam.inf</c> next to an addons folder (<c>citadel/addons</c>); null when there is none, which
    /// means the addons folder is not inside a game folder. A file that exists but is malformed throws.</summary>
    Task<GameBuildInfo?> TryReadForAddonsRootAsync(string addonsRoot, CancellationToken cancellationToken = default);
}

public sealed record BaseFileInfo(uint Crc32, long Size);

/// <summary>The directory tree of the base game archive (<c>pak01_dir.vpk</c>); nothing is hashed or read until a payload is asked for.</summary>
public interface IBaseGameArchive : IDisposable
{
    IReadOnlyDictionary<string, BaseFileInfo> Entries { get; }

    /// <summary>Payload of one entry, checked against its stored CRC32.</summary>
    byte[] ReadPayload(string logicalPath);
}

public interface IBaseGameArchiveFactory
{
    IBaseGameArchive Open(string gameDirectory);
}

public sealed record GameBuildRecord(int Build, string VersionDate, DateTimeOffset FirstSeenUtc);

public sealed record BaseFileRecord(string Path, string Crc32, long Size);

public sealed record GameFilesDocument(int Version, int Build, IReadOnlyList<BaseFileRecord> Files)
{
    public const int CurrentVersion = 1;
}

public sealed record BuildFileCopy(string LogicalPath, byte[] Content);

public sealed record ModAcknowledgement(string FileName, long Size, long LastWriteUtcTicks, DateTimeOffset AcknowledgedUtc);

/// <summary>The folder where S2ModKit keeps what it learned about game builds and what the user dismissed:
/// <c>builds/&lt;build&gt;/build.json</c>, <c>game-files.json</c>, <c>files/&lt;logical path&gt;</c> and <c>acks/&lt;build&gt;.json</c>.</summary>
public interface IModStatusStore
{
    Task<bool> BuildExistsAsync(string stateRoot, int build, CancellationToken cancellationToken = default);

    /// <summary>Creates the record for a build in one step; returns false (and writes nothing) when it already exists.</summary>
    Task<bool> TryCreateBuildAsync(string stateRoot, GameBuildRecord record, GameFilesDocument files, IReadOnlyList<BuildFileCopy> copies, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModAcknowledgement>> LoadAcknowledgementsAsync(string stateRoot, int build, CancellationToken cancellationToken = default);

    Task SaveAcknowledgementsAsync(string stateRoot, int build, IReadOnlyList<ModAcknowledgement> acknowledgements, CancellationToken cancellationToken = default);
}

public sealed record ModStatusRequest(string GameDirectory, string AddonsRoot, string StateRoot, IReadOnlyList<string> ReceiptsRoots, string? ManagerStatePath);

public sealed record ModAckRequest(string GameDirectory, string AddonsRoot, string StateRoot, string? FileName, bool All);

public static class ModStatusStates
{
    public const string Current = "current";
    public const string OutdatedBuild = "built_for_older_game";
    public const string UnknownBuild = "unknown_build";
    public const string MayBeOutdated = "may_be_outdated";
    public const string InstalledAfterUpdate = "installed_after_update";
}

public sealed record ModStatusEntry(
    string FileName,
    string? ModName,
    IReadOnlyDictionary<string, int> ResourceKinds,
    string Source,
    string State,
    string Message,
    bool Notice,
    bool Acknowledged,
    int? InstalledOnBuild);

public sealed record ModStatusReport(
    int Build,
    string VersionDate,
    bool NewBuildRecorded,
    string StateRoot,
    int NoticeCount,
    int AcknowledgedCount,
    IReadOnlyList<ModStatusEntry> Mods);

public sealed record ModAckResult(int Build, IReadOnlyList<string> Acknowledged);

public interface IModStatusApplication
{
    Task<ModStatusReport> StatusAsync(ModStatusRequest request, CancellationToken cancellationToken = default);

    Task<ModAckResult> AcknowledgeAsync(ModAckRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Shows which loaded addons a game update may have broken. It records each newly seen game build once (which
/// base files the addons override, with small data copies) and never changes, renames or repairs an addon.</summary>
public sealed class ModStatusApplication(
    IAddonFileStore addonStore,
    IAddonTreeReader treeReader,
    IModManagerStateReader managerReader,
    IGameBuildReader buildReader,
    IBaseGameArchiveFactory baseArchives,
    IModStatusStore stateStore,
    IClock clock) : IModStatusApplication
{
    /// <summary>Largest base file copied into the build record.</summary>
    public const long MaximumCopiedFileBytes = 1024 * 1024;

    private readonly AddonTreeLoader treeLoader = new(addonStore, treeReader);

    public async Task<ModStatusReport> StatusAsync(ModStatusRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StateRoot);
        var build = await buildReader.ReadAsync(request.GameDirectory, cancellationToken).ConfigureAwait(false);
        var manager = await managerReader.ReadAsync(request.AddonsRoot, request.ManagerStatePath, cancellationToken).ConfigureAwait(false);
        var (trees, _) = await treeLoader.ReadAsync(request.AddonsRoot, cachePath: null, loadedOnly: true, cancellationToken).ConfigureAwait(false);
        var recorded = await RecordBuildIfNewAsync(request, build, trees, cancellationToken).ConfigureAwait(false);

        var installedByName = new Dictionary<string, AddonFileInstallReceipt>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in request.ReceiptsRoots)
        {
            foreach (var receipt in await addonStore.ListReceiptsAsync(root, cancellationToken).ConfigureAwait(false))
            {
                if (receipt.Status == "installed" && receipt.PackageHash is not null)
                {
                    installedByName[receipt.TargetFileName] = receipt;
                }
            }
        }

        var acknowledgements = await stateStore.LoadAcknowledgementsAsync(request.StateRoot, build.Build, cancellationToken).ConfigureAwait(false);
        var updateInstant = new DateTime(build.VersionDay.Year, build.VersionDay.Month, build.VersionDay.Day, 0, 0, 0, DateTimeKind.Utc);
        var entries = new List<ModStatusEntry>();
        foreach (var (file, paths) in trees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            manager.ModsByFileName.TryGetValue(file.FileName, out var managed);
            var ours = installedByName.TryGetValue(file.FileName, out var receiptForFile)
                && await addonStore.HashFileAsync(file.FullPath, cancellationToken).ConfigureAwait(false) == receiptForFile.PackageHash
                    ? receiptForFile
                    : null;
            string source, state, message;
            bool notice;
            int? installedOn = null;
            if (ours is not null)
            {
                source = "s2modkit";
                installedOn = ours.GameBuild;
                (state, message, notice) = ours.GameBuild switch
                {
                    null => (ModStatusStates.UnknownBuild, "installed by S2ModKit; the game build at install time was not recorded", true),
                    var made when made == build.Build => (ModStatusStates.Current, $"installed by S2ModKit on build {made} (current)", false),
                    var made => (ModStatusStates.OutdatedBuild, $"installed on build {made}; game is now build {build.Build}", true),
                };
            }
            else
            {
                source = "third_party";
                if (new DateTime(file.LastWriteUtcTicks, DateTimeKind.Utc) < updateInstant)
                {
                    (state, message, notice) = (ModStatusStates.MayBeOutdated, $"game updated on {build.VersionDate} after this mod was installed; it may be outdated", true);
                }
                else
                {
                    (state, message, notice) = (ModStatusStates.InstalledAfterUpdate, "installed after the current game update", false);
                }
            }

            var acknowledged = notice && acknowledgements.Any(item =>
                string.Equals(item.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)
                && item.Size == file.Size
                && item.LastWriteUtcTicks == file.LastWriteUtcTicks);
            entries.Add(new ModStatusEntry(
                file.FileName,
                managed?.Name ?? ours?.Label,
                AddonResourceKinds.Count(paths),
                source,
                state,
                message,
                notice,
                acknowledged,
                installedOn));
        }

        return new ModStatusReport(
            build.Build,
            build.VersionDate,
            recorded,
            Path.GetFullPath(request.StateRoot),
            entries.Count(item => item.Notice && !item.Acknowledged),
            entries.Count(item => item.Acknowledged),
            entries);
    }

    public async Task<ModAckResult> AcknowledgeAsync(ModAckRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StateRoot);
        if (request.All == !string.IsNullOrWhiteSpace(request.FileName))
        {
            throw Errors.InvalidRecipe("MODS_ACK_SELECTOR_INVALID", "Give exactly one of --file and --all.", "Use --file pakNN_dir.vpk for one mod or --all for every loaded mod.");
        }

        var build = await buildReader.ReadAsync(request.GameDirectory, cancellationToken).ConfigureAwait(false);
        var loaded = (await addonStore.ListArchivesAsync(request.AddonsRoot, cancellationToken).ConfigureAwait(false))
            .Where(item => AddonFileApplication.IsLoadedName(item.FileName))
            .ToArray();
        var selected = request.All
            ? loaded
            : loaded.Where(item => string.Equals(item.FileName, request.FileName!.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Length == 0 && !request.All)
        {
            throw Errors.Input("MODS_ACK_FILE_UNKNOWN", $"'{request.FileName}' is not a loaded addon (pakNN_dir.vpk) in the addons folder.", "Use a file name shown by `s2mod mods status`.");
        }

        var now = clock.UtcNow.ToUniversalTime();
        var names = selected.Select(item => item.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var merged = (await stateStore.LoadAcknowledgementsAsync(request.StateRoot, build.Build, cancellationToken).ConfigureAwait(false))
            .Where(item => !names.Contains(item.FileName))
            .Concat(selected.Select(item => new ModAcknowledgement(item.FileName, item.Size, item.LastWriteUtcTicks, now)))
            .OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await stateStore.SaveAcknowledgementsAsync(request.StateRoot, build.Build, merged, cancellationToken).ConfigureAwait(false);
        return new ModAckResult(build.Build, selected.Select(item => item.FileName).ToArray());
    }

    private async Task<bool> RecordBuildIfNewAsync(
        ModStatusRequest request,
        GameBuildInfo build,
        IReadOnlyList<(AddonFileInfo File, IReadOnlyList<string> Paths)> trees,
        CancellationToken cancellationToken)
    {
        if (await stateStore.BuildExistsAsync(request.StateRoot, build.Build, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var overridden = trees.SelectMany(tree => tree.Paths).ToHashSet(StringComparer.Ordinal);
        using var archive = baseArchives.Open(request.GameDirectory);
        var files = overridden
            .Where(archive.Entries.ContainsKey)
            .Order(StringComparer.Ordinal)
            .Select(path => new BaseFileRecord(path, archive.Entries[path].Crc32.ToString("x8", CultureInfo.InvariantCulture), archive.Entries[path].Size))
            .ToArray();
        var copies = new List<BuildFileCopy>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Size <= MaximumCopiedFileBytes && AddonResourceKinds.Classify(file.Path) is "data" or "localization")
            {
                copies.Add(new BuildFileCopy(file.Path, archive.ReadPayload(file.Path)));
            }
        }

        return await stateStore.TryCreateBuildAsync(
            request.StateRoot,
            new GameBuildRecord(build.Build, build.VersionDate, clock.UtcNow.ToUniversalTime()),
            new GameFilesDocument(GameFilesDocument.CurrentVersion, build.Build, files),
            copies,
            cancellationToken).ConfigureAwait(false);
    }
}
