using S2ModKit.Adapters.Vpk;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;

namespace S2ModKit.Desktop;

/// <summary>Where a mod comes from: installed by the app, known to Deadlock Mod Manager, or neither. The app enables, disables and deletes all of them.</summary>
internal enum ModSource
{
    App,
    Manager,
    Other,
}

/// <summary>One version of a mod from the library: the list key of the mod that holds it, its label and whether it is the enabled one.</summary>
internal sealed record ModVersion(string Key, string Label, bool Active);

/// <summary>What an operation changed: the list key to select afterwards and the message to show.</summary>
internal sealed record CatalogChange(string SelectKey, string Message);

/// <summary>One recolored part for a build: its colour texture (path without <c>_c</c>), its UV mask in that texture's size and the change.</summary>
internal sealed record PartRecolor(int Part, string Texture, float[] Mask, int Width, int Height, ColorAdjustment Change);

/// <summary>The last doctor check: the game build, the build it was compared with (null for a first check) and its findings.</summary>
internal sealed record DoctorReport(int Build, int? ComparedWith, IReadOnlyList<DoctorFinding> Findings);

/// <summary>An app installation: its receipts folder, latest receipt (status installed or rolled_back) and when its first receipt was written.</summary>
internal sealed record AppInstall(string ReceiptsRoot, AddonFileInstallReceipt Receipt, DateTimeOffset FirstCreatedUtc);

/// <summary>
/// One mod as the user knows it: a Mod Manager split group is one mod (its archives in slot order); an app install that
/// is disabled has no archive in the addons folder and is previewed from its package.
/// </summary>
internal sealed record ModEntry(
    string Key,
    string Name,
    ModSource Source,
    bool Loaded,
    IReadOnlyList<string> Files,
    IReadOnlyList<int?> Slots,
    IReadOnlyDictionary<string, int> Kinds,
    IReadOnlyList<string> Entries,
    IReadOnlyList<string> Models,
    AppInstall? Install,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? HeroKinds = null,
    string Group = ModAttribution.Other,
    DateTimeOffset Added = default,
    string? ModId = null,
    IReadOnlyList<ModVersion>? Versions = null,
    IReadOnlyDictionary<string, int>? PathSlots = null,
    ModConflicts? Conflicts = null,
    IReadOnlyList<string>? StrayHeroes = null)
{
    /// <summary>Lowest pak slot of the mod's enabled archives; a disabled mod sorts after every enabled one.</summary>
    public int FirstSlot => Slots.Where(slot => slot is not null).Select(slot => slot!.Value).DefaultIfEmpty(int.MaxValue).Min();

    public ModConflicts ConflictInfo => Conflicts ?? ModConflicts.None;

    /// <summary>The other versions of this mod (and this one) when the library lists it with at least one more; else empty.</summary>
    public IReadOnlyList<ModVersion> VersionList => Versions ?? [];

    /// <summary>Hero display names the mod changes (from the hero catalogue), by any kind of file; empty for a mod without a hero.</summary>
    public IReadOnlyList<string> HeroNames => [.. (HeroKinds ?? new Dictionary<string, IReadOnlyList<string>>()).Keys];

    /// <summary>Model, sound, UI, particles or other: the mod's main kind for filtering. A known kind beats "other" (a README beside one sound file is still a sound mod).</summary>
    public string MainKind => Models.Count > 0 && Kinds.ContainsKey("model") ? "model"
        : Kinds.Where(pair => pair.Key != "other").DefaultIfEmpty(new("other", 0)).MaxBy(pair => pair.Value).Key;

    public string KindText => Kinds.Count == 0 ? "-" : string.Join(", ", Kinds.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key} {pair.Value}"));

    public string StatusText => (Source, Loaded) switch
    {
        (ModSource.App, true) => "installed by S2ModKit",
        (ModSource.App, false) => "disabled (S2ModKit)",
        (ModSource.Manager, true) => "Mod Manager, enabled",
        (ModSource.Manager, false) => "Mod Manager, disabled",
        (_, true) => "enabled",
        _ => "disabled",
    };
}

/// <summary>
/// The addons folder as a list of mods (read through <see cref="AddonFileApplication"/>'s scan), which hero model each
/// mod shows on, installs over the safe installer, and enable, disable, delete and version switches of any mod by renaming
/// in place (<see cref="AddonModOperations"/>). <paramref name="managerState"/>: Deadlock Mod Manager state file (default:
/// the installed manager's, if any); it is only read. The game probe and the Recycle Bin are replaceable for tests.
/// </summary>
internal sealed class ModCatalog(AppSettings settings, string? managerState = null, IGameProcessProbe? gameProbe = null, IRecycleBin? recycleBin = null)
{
    private readonly string? managerState = managerState ?? AppSettings.ManagerState;
    private readonly AddonFileStore store = new();
    private readonly ValvePakAddonTreeReader tree = new();
    private readonly AddonFileOperations operations = new();
    private readonly ModLibraryStore libraryStore = new();
    private readonly ArchivePackageReader archives = new();
    private readonly SystemClock clock = new();
    private IReadOnlyList<string>? gameModels;

    private AddonFileApplication Files => new(store, tree, new ModManagerStateReader(JournalRecords()), clock, new GameBuildReader());

    private AddonModOperations Management => new(operations, gameProbe ?? new SystemGameProcessProbe(), recycleBin ?? new WindowsRecycleBin(), clock);

    public async Task<IReadOnlyList<ModEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(settings.Data);
        Hero.UseGame(settings.Game, settings.Data);                        // the game's own hero list when it can be read
        var scan = await Files.ScanAsync(settings.AddonsRoot, managerState, settings.TreeCache, cancellationToken).ConfigureAwait(false);
        var installs = await ReadInstallsAsync(cancellationToken).ConfigureAwait(false);
        // A receipt can outlive its file (removed outside the app) and a later install may take the same slot: the newest
        // receipt for a file name is the one that describes it.
        var byTarget = installs.Where(install => install.Receipt.Status == "installed")
            .GroupBy(install => install.Receipt.CurrentFileName ?? install.Receipt.TargetFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.MaxBy(install => install.Receipt.CreatedUtc)!, StringComparer.OrdinalIgnoreCase);

        var firstInstalled = installs.GroupBy(install => install.Receipt.PackagePath, StringComparer.OrdinalIgnoreCase)    // a re-enable writes a new receipt: keep the first date
            .ToDictionary(g => g.Key, g => g.Min(install => install.FirstCreatedUtc), StringComparer.OrdinalIgnoreCase);
        var mods = new List<ModEntry>();
        foreach (var group in scan.Archives.GroupBy(archive => archive.ModId ?? AddonNaming.FileKey(archive.FileName)))
        {
            var archives = group.OrderBy(archive => archive.Slot ?? int.MaxValue).ThenBy(archive => archive.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
            var first = archives[0];
            var install = archives.Select(archive => byTarget.GetValueOrDefault(archive.FileName)).FirstOrDefault(found => found is not null);
            var source = install is not null ? ModSource.App : first.ModId is not null ? ModSource.Manager : ModSource.Other;
            var files = archives.Select(archive => Path.Combine(settings.AddonsRoot, archive.FileName)).ToArray();
            var entries = await ReadEntriesAsync(files, cancellationToken).ConfigureAwait(false);
            var pathSlots = await ReadPathSlotsAsync(archives.Select((archive, i) => (files[i], archive.Slot)), cancellationToken).ConfigureAwait(false);
            var kinds = archives.SelectMany(archive => archive.ResourceKinds).GroupBy(pair => pair.Key).ToDictionary(g => g.Key, g => g.Sum(pair => pair.Value));
            var name = install?.Receipt.Label ?? first.ModName ?? AddonNaming.DisplayName(first.FileName);
            var heroes = HeroesOf(entries, archives.Select(archive => archive.ModHero).FirstOrDefault(hero => hero is not null), kinds);
            var added = archives.Select(archive => archive.ModAddedUtc).FirstOrDefault(time => time is not null)       // Mod Manager's record
                ?? (install is null ? (DateTimeOffset?)null : firstInstalled.GetValueOrDefault(install.Receipt.PackagePath, install.FirstCreatedUtc))   // else the app's install
                ?? files.Select(file => new DateTimeOffset(File.GetCreationTimeUtc(file), TimeSpan.Zero)).Min();         // else the file's creation
            mods.Add(new ModEntry(group.Key, name, source, archives.Any(archive => archive.Loaded), files, archives.Select(archive => archive.Slot).ToArray(),
                kinds, entries, await ResolveModelsAsync(entries, ModelHeroes(entries), cancellationToken).ConfigureAwait(false), install,
                HeroKinds(heroes), GroupOfMod(heroes, entries), added, first.ModId, PathSlots: pathSlots, StrayHeroes: StrayNames(entries)));
        }

        foreach (var disabled in installs.Where(install => install.Receipt.Status == "rolled_back" && File.Exists(install.Receipt.PackagePath))
                     .GroupBy(install => install.Receipt.PackagePath, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()))
        {
            if (installs.Any(other => other.Receipt.Status == "installed" && other.Receipt.PackagePath.Equals(disabled.Receipt.PackagePath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;                                                    // enabled again later
            }

            var entries = await ReadEntriesAsync([disabled.Receipt.PackagePath], cancellationToken).ConfigureAwait(false);
            var kinds = AddonResourceKinds.Count(entries);
            var heroes = HeroesOf(entries, null, kinds);
            mods.Add(new ModEntry("app:" + disabled.Receipt.PackagePath, disabled.Receipt.Label, ModSource.App, false, [disabled.Receipt.PackagePath], [null],
                new Dictionary<string, int>(), entries, await ResolveModelsAsync(entries, ModelHeroes(entries), cancellationToken).ConfigureAwait(false), disabled,
                HeroKinds(heroes), GroupOfMod(heroes, entries), firstInstalled.GetValueOrDefault(disabled.Receipt.PackagePath, disabled.FirstCreatedUtc)));
        }

        var library = await libraryStore.LoadAsync(settings.LibraryPath, cancellationToken).ConfigureAwait(false);
        var conflicts = ModConflictAnalysis.Analyze(mods);
        return [.. mods.Select(mod => mod with { Versions = VersionsOf(library, mod, mods), Conflicts = conflicts.GetValueOrDefault(mod.Key) }).OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)];   // by name: a mod keeps its place when switched
    }

    /// <summary>The versions of <paramref name="mod"/> that are in the list, when the library has at least two of them.</summary>
    private static ModVersion[] VersionsOf(ModLibraryDocument library, ModEntry mod, IReadOnlyList<ModEntry> all)
    {
        var versions = library.FindByKey(mod.Key)?.Versions
            .Select(version => (version, entry: all.FirstOrDefault(other => other.Key == version.Key))).Where(item => item.entry is not null)
            .Select(item => new ModVersion(item.version.Key, item.version.Label, item.entry!.Loaded)).ToArray() ?? [];
        return versions.Length > 1 ? versions : [];
    }


    /// <summary>Installs a finished VPK into a free slot (a new receipts folder per installation); the data chunks beside a
    /// <c>_dir.vpk</c> are copied with it. Files shared with enabled mods are allowed (the list shows the conflicts, as for two
    /// enabled mods); an identical copy already in the folder is refused. Refused while the game runs.</summary>
    public async Task<AddonFileResult> InstallAsync(string package, string label, CancellationToken cancellationToken = default)
    {
        Management.EnsureGameStopped();
        var receipts = Path.Combine(settings.InstallsRoot, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Sanitize(label)}-{Guid.NewGuid().ToString("N")[..6]}");
        var result = await Files.InstallAsync(new AddonFileInstallRequest(settings.AddonsRoot, package, "auto", receipts, label, managerState, settings.TreeCache, AllowSharedFiles: true),
            cancellationToken).ConfigureAwait(false);
        if (result is not { Status: "installed", Receipt: { } receipt })
        {
            return result;
        }

        try
        {
            await Management.CopyChunksAsync(package, settings.AddonsRoot, receipt.TargetFileName, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await Files.RollbackAsync(settings.AddonsRoot, receipts, CancellationToken.None).ConfigureAwait(false);   // no half-installed split archive
            throw;
        }

        return result;
    }

    /// <summary>The <c>.vpk</c> variants (a <c>_dir.vpk</c> with its chunks) in a zip, 7z or rar archive; nothing is extracted.</summary>
    public async Task<IReadOnlyList<ArchiveVariant>> ReadArchiveAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var variants = ArchiveVariants.Group(await archives.ListVpksAsync(archivePath, cancellationToken).ConfigureAwait(false));
        return variants.Count > 0
            ? variants
            : throw Errors.Input("ARCHIVE_NO_VPK", $"'{Path.GetFileName(archivePath)}' contains no .vpk file.", "Only Deadlock addon packages (.vpk) are installed.");
    }

    /// <summary>Extracts the chosen variants into a staging folder under the data folder, installs each through the safe installer
    /// into a free slot and removes the staging folder. A variant the installer refuses (it shares files with an enabled mod,
    /// including one installed a moment earlier from the same archive) is reported and the others still install.</summary>
    public async Task<CatalogChange> InstallArchiveAsync(string archivePath, IReadOnlyList<ArchiveVariant> chosen, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        Management.EnsureGameStopped();
        var baseName = Path.GetFileNameWithoutExtension(archivePath);
        var stagingRoot = Path.GetFullPath(settings.StagingRoot);
        var staging = Path.Combine(stagingRoot, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}");
        var installed = new List<AddonFileInstallReceipt>();
        var refused = new List<string>();
        try
        {
            var layout = ArchiveVariants.Layout(chosen, staging);
            await archives.ExtractAsync(archivePath, [.. layout.SelectMany(item => item.Extractions)], staging, cancellationToken).ConfigureAwait(false);
            foreach (var (variant, dirPath, _) in layout)
            {
                var label = layout.Count == 1 ? baseName : $"{baseName} - {(variant.Folder.Length > 0 ? variant.Folder[(variant.Folder.LastIndexOf('/') + 1)..] : Path.GetFileNameWithoutExtension(variant.FileName))}";
                try
                {
                    if ((await InstallAsync(dirPath, label, cancellationToken).ConfigureAwait(false)).Receipt is { } receipt)
                    {
                        installed.Add(receipt);
                    }
                }
                catch (S2ModKitException exception) when (layout.Count > 1 && exception.Error.Code == "ADDON_FILE_COLLISION")
                {
                    refused.Add($"{variant.FileName}: {exception.Error.Summary}");
                }
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);                  // only the folder created above, inside the staging root
            }
        }

        var message = installed.Count == 0 ? "Nothing was installed." : $"Installed {string.Join(", ", installed.Select(receipt => $"'{receipt.Label}'"))}.";
        return new CatalogChange(installed.Count > 0 ? AddonNaming.FileKey(installed[0].TargetFileName) : string.Empty,
            message + (refused.Count > 0 ? " Refused: " + string.Join(" ", refused) : string.Empty));
    }

    /// <summary>Disables the mod by renaming its files in place (see <see cref="AddonNaming"/>).</summary>
    public async Task<CatalogChange> DisableAsync(ModEntry mod, CancellationToken cancellationToken = default)
    {
        var result = await Management.DisableAsync(await TargetOfAsync(mod, cancellationToken).ConfigureAwait(false), settings.JournalRoot, cancellationToken).ConfigureAwait(false);
        return await ChangeAsync(mod, result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Enables the mod by renaming its files back into a free slot; an app install the old way disabled (its file removed,
    /// only the package kept) is installed again from the package.</summary>
    public async Task<CatalogChange> EnableAsync(ModEntry mod, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        if (mod.Install is { } install && mod.Key.StartsWith("app:", StringComparison.Ordinal))
        {
            var again = await InstallAsync(install.Receipt.PackagePath, install.Receipt.Label, cancellationToken).ConfigureAwait(false);
            return new CatalogChange(again.Receipt is { } receipt ? AddonNaming.FileKey(receipt.TargetFileName) : mod.Key, $"Enabled '{mod.Name}'.");
        }

        var result = await Management.EnableAsync(await TargetOfAsync(mod, cancellationToken).ConfigureAwait(false), settings.JournalRoot, cancellationToken).ConfigureAwait(false);
        return await ChangeAsync(mod, result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends the mod's files to the Recycle Bin. An app install's receipt is closed (<c>deleted</c>); its package copy in
    /// the data folder stays. An old-style disabled app install has no file in the addons folder: only its record is closed.</summary>
    public async Task<CatalogChange> DeleteAsync(ModEntry mod, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        Management.EnsureGameStopped();
        var message = $"Removed '{mod.Name}' from the list (it had no file in the addons folder).";
        if (!mod.Key.StartsWith("app:", StringComparison.Ordinal))
        {
            message = (await Management.DeleteAsync(await TargetOfAsync(mod, cancellationToken).ConfigureAwait(false), settings.JournalRoot, cancellationToken).ConfigureAwait(false)).Summary;
        }

        if (mod.Install is { } install)
        {
            await store.WriteReceiptAsync(install.ReceiptsRoot, install.Receipt with { Status = "deleted", Note = "deleted from the app", UpdatedUtc = clock.UtcNow.ToUniversalTime() }, createNew: false, cancellationToken).ConfigureAwait(false);
        }

        var library = await libraryStore.LoadAsync(settings.LibraryPath, cancellationToken).ConfigureAwait(false);
        if (library.FindByKey(mod.Key) is not null)
        {
            await libraryStore.SaveAsync(settings.LibraryPath, library.Without(mod.Key), cancellationToken).ConfigureAwait(false);
        }

        return new CatalogChange(string.Empty, message);
    }

    /// <summary>Makes <paramref name="chosen"/> the enabled version of its mod: the enabled siblings (the mod's other versions) are
    /// disabled and the chosen one enabled, in one journaled operation.</summary>
    public async Task<CatalogChange> ActivateVersionAsync(ModEntry chosen, IReadOnlyList<ModEntry> all, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(all);
        var siblings = chosen.VersionList.Where(version => version.Key != chosen.Key).Select(version => all.FirstOrDefault(mod => mod.Key == version.Key))
            .Where(mod => mod is not null && mod.Loaded).ToArray();
        var targets = new List<AddonModTarget>();
        foreach (var sibling in siblings)
        {
            targets.Add(await TargetOfAsync(sibling!, cancellationToken).ConfigureAwait(false));
        }

        var result = await Management.SwitchVersionAsync(await TargetOfAsync(chosen, cancellationToken).ConfigureAwait(false), targets, settings.JournalRoot, cancellationToken).ConfigureAwait(false);
        return await ChangeAsync(chosen, result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves an enabled mod one place earlier (<paramref name="direction"/> -1: lower pak number, wins more) or later (1) in the
    /// load order: one journaled <c>addon-reorder@1</c> operation (see <see cref="AddonLoadOrder"/>).</summary>
    public async Task<CatalogChange> MoveAsync(ModEntry mod, int direction, IReadOnlyList<ModEntry> all, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(all);
        var result = await Management.ReorderAsync(settings.AddonsRoot, LoadOrderOf(all), mod.Key, direction, settings.JournalRoot, cancellationToken).ConfigureAwait(false);
        return await ChangeAsync(mod, result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The enabled mods with their enabled archives (the input of <see cref="AddonLoadOrder.Plan"/>).</summary>
    internal static IReadOnlyList<LoadOrderMod> LoadOrderOf(IEnumerable<ModEntry> all) =>
        [.. all.Where(mod => mod.Loaded && !mod.Key.StartsWith("app:", StringComparison.Ordinal))
            .Select(mod => new LoadOrderMod(mod.Key, mod.Name, [.. mod.Files.Where((_, i) => mod.Slots[i] is not null).Select(file => Path.GetFileName(file))]))
            .Where(mod => mod.Archives.Count > 0)];

    /// <summary>Lists mods as versions of one library mod (<c>library.json</c>, <c>mod-library@1</c>).</summary>
    public async Task GroupVersionsAsync(string name, IReadOnlyList<(string Key, string Label)> versions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versions);
        var library = await libraryStore.LoadAsync(settings.LibraryPath, cancellationToken).ConfigureAwait(false);
        await libraryStore.SaveAsync(settings.LibraryPath, library.WithVersions(name, [.. versions.Select(version => new ModLibraryVersion { Key = version.Key, Label = version.Label })], clock.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The renames Disable would do now (Enable when the mod is already disabled); nothing changes.</summary>
    public async Task<IReadOnlyList<AddonRename>> PlanToggleAsync(ModEntry mod, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        if (mod.Key.StartsWith("app:", StringComparison.Ordinal))
        {
            return [];
        }

        var target = await TargetOfAsync(mod, cancellationToken).ConfigureAwait(false);
        var disable = await Management.PlanDisableAsync(target, cancellationToken).ConfigureAwait(false);
        return disable.Count > 0 ? disable : await Management.PlanEnableAsync(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What the app's completed rename journals say each file is, replayed in order: a file keeps the mod id and name it had
    /// before its rename (or, for its first rename, the journal's mod id and label); deleted files drop out. Only mods with an
    /// id are returned: a nameless <c>off_</c> mod is named after its file anyway.
    /// </summary>
    private Dictionary<string, ManagedModInfo> JournalRecords()
    {
        var records = new Dictionary<string, ManagedModInfo>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(settings.JournalRoot))
        {
            return records;
        }

        var journals = Directory.EnumerateFiles(settings.JournalRoot, "*.json").Select(ReadJournal).OfType<AddonOperationJournal>()
            .Where(journal => journal.Result == "completed").OrderBy(journal => journal.StartedUtc);
        foreach (var journal in journals)
        {
            foreach (var entry in journal.Entries)
            {
                records.Remove(entry.From, out var before);
                var id = journal.ModId ?? IdPrefix(entry.From) ?? IdPrefix(entry.To ?? string.Empty);
                var record = before ?? (journal.Operation == AddonOperationJournal.RenameOperation && id is not null
                    ? new ManagedModInfo(id, journal.Label, false) : null);
                if (entry.To is not null && record is not null)
                {
                    records[entry.To] = record with { Enabled = System.Text.RegularExpressions.Regex.IsMatch(entry.To, @"^pak[0-9]{2}_dir\.vpk$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) };
                }
            }
        }

        return records;

        static AddonOperationJournal? ReadJournal(string path)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<AddonOperationJournal>(File.ReadAllText(path), JournalJson);
            }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
            {
                return null;                                                 // an unreadable journal names nothing
            }
        }

        // <mod id>_pakNN_dir.vpk: the part before the first '_', unless it is the app's own off_/offN_ prefix.
        static string? IdPrefix(string file) =>
            file.IndexOf('_', StringComparison.Ordinal) is > 0 and var cut && !System.Text.RegularExpressions.Regex.IsMatch(file[..cut], "^off[0-9]*$")
                && !file.StartsWith("pak", StringComparison.OrdinalIgnoreCase) ? file[..cut] : null;
    }

    private static readonly System.Text.Json.JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private static readonly System.Text.Json.JsonSerializerOptions JournalJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    private async Task<AddonModTarget> TargetOfAsync(ModEntry mod, CancellationToken cancellationToken)
    {
        var view = await new ModManagerStateReader(JournalRecords()).ReadAsync(settings.AddonsRoot, managerState, cancellationToken).ConfigureAwait(false);
        return new AddonModTarget(settings.AddonsRoot, mod.Name, mod.Source == ModSource.Manager ? mod.ModId : null,
            [.. mod.Files.Select(file => Path.GetFileName(file))], view.ReservedSlotNames);
    }

    /// <summary>After a rename: the app's receipts and the library follow the new names; the list selects the mod under its new key.</summary>
    private async Task<CatalogChange> ChangeAsync(ModEntry mod, AddonOperationResult result, CancellationToken cancellationToken)
    {
        var note = string.Empty;
        if (result.Renames.Count > 0)
        {
            try
            {
                foreach (var install in await ReadInstallsAsync(cancellationToken).ConfigureAwait(false))
                {
                    var current = install.Receipt.CurrentFileName ?? install.Receipt.TargetFileName;
                    if (install.Receipt.Status == "installed" && result.Renames.FirstOrDefault(item => item.From.Equals(current, StringComparison.OrdinalIgnoreCase)) is { } moved)
                    {
                        var name = moved.To.Equals(install.Receipt.TargetFileName, StringComparison.OrdinalIgnoreCase) ? null : moved.To;
                        await store.WriteReceiptAsync(install.ReceiptsRoot, install.Receipt with { CurrentFileName = name, UpdatedUtc = clock.UtcNow.ToUniversalTime() }, createNew: false, cancellationToken).ConfigureAwait(false);
                    }
                }

                var library = await libraryStore.LoadAsync(settings.LibraryPath, cancellationToken).ConfigureAwait(false);
                var followed = library.WithRenames(result.Renames);
                if (!ReferenceEquals(followed, library))
                {
                    await libraryStore.SaveAsync(settings.LibraryPath, followed, cancellationToken).ConfigureAwait(false);
                }
            }
#pragma warning disable CA1031 // the files are renamed and verified; say that the app's own records lag
            catch (Exception exception)
#pragma warning restore CA1031
            {
                note = $" The files were renamed, but the app's records could not be updated: {exception.Message}";
            }
        }

        var first = Path.GetFileName(mod.Files[0]);
        var key = mod.Source == ModSource.Manager || mod.Key.StartsWith("app:", StringComparison.Ordinal) ? mod.Key
            : result.Renames.FirstOrDefault(item => item.From.Equals(first, StringComparison.OrdinalIgnoreCase)) is { } renamed ? AddonNaming.FileKey(renamed.To) : mod.Key;
        return new CatalogChange(key, result.Summary + note);
    }

    private async Task<List<AppInstall>> ReadInstallsAsync(CancellationToken cancellationToken)
    {
        var installs = new List<AppInstall>();
        if (!Directory.Exists(settings.InstallsRoot))
        {
            return installs;
        }

        foreach (var folder in Directory.EnumerateDirectories(settings.InstallsRoot).Order(StringComparer.Ordinal))
        {
            var receipts = await store.ListReceiptsAsync(folder, cancellationToken).ConfigureAwait(false);
            if (receipts.OrderBy(receipt => receipt.UpdatedUtc).LastOrDefault() is { } latest)
            {
                installs.Add(new AppInstall(folder, latest, receipts.Min(receipt => receipt.CreatedUtc)));
            }
        }

        return installs;
    }

    /// <summary>Normalized entry path to the lowest slot among the enabled archives holding it (null when no archive is enabled).</summary>
    private async Task<IReadOnlyDictionary<string, int>?> ReadPathSlotsAsync(IEnumerable<(string File, int? Slot)> archives, CancellationToken cancellationToken)
    {
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (file, slot) in archives.Where(item => item.Slot is not null))
        {
            foreach (var path in await tree.ReadEntryPathsAsync(file, cancellationToken).ConfigureAwait(false))
            {
                var key = ModConflictAnalysis.Normalize(path);
                slots[key] = slots.TryGetValue(key, out var known) ? Math.Min(known, slot!.Value) : slot!.Value;
            }
        }

        return slots.Count == 0 ? null : slots;
    }

    private async Task<IReadOnlyList<string>> ReadEntriesAsync(IEnumerable<string> files, CancellationToken cancellationToken)
    {
        var entries = new List<string>();
        foreach (var file in files)
        {
            entries.AddRange(await tree.ReadEntryPathsAsync(file, cancellationToken).ConfigureAwait(false));
        }

        return entries;
    }

    /// <summary>
    /// Models a mod shows on: the catalogue's main model of each hero it changes first (a mod that also ships an ability or
    /// prop model still opens on the hero: Gojo Doorman showed an ability sphere), then its own compiled models (a hero's
    /// main model first), else the game model of each hero folder whose materials or textures it replaces.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveModelsAsync(IReadOnlyList<string> entries, IReadOnlyList<Hero> heroes, CancellationToken cancellationToken)
    {
        var own = entries.Where(path => path.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase) && path.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            .Select(path => path[..^2]).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(MainModelRank).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var primary = heroes.Select(hero => hero.Model);
        if (own.Length > 0)
        {
            return [.. primary.Concat(own).Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        var folders = entries.Where(path => path.StartsWith("models/heroes", StringComparison.OrdinalIgnoreCase))
            .Select(path => path.Split('/')).Where(parts => parts.Length > 3).Select(parts => $"{parts[0]}/{parts[1]}/{parts[2]}/")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (folders.Length == 0)
        {
            return [];
        }

        gameModels ??= (await tree.ReadEntryPathsAsync(Path.Combine(settings.Game, "citadel", "pak01_dir.vpk"), cancellationToken).ConfigureAwait(false))
            .Where(path => path.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase)).Select(path => path[..^2]).ToArray();
        return [.. primary.Concat(folders.SelectMany(folder => gameModels.Where(path => path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && path.IndexOf('/', folder.Length) < 0)
            .OrderBy(MainModelRank).Take(1))).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Heroes the mod changes and the kinds of change for each (see <see cref="ModAttribution"/>). Only when its paths name no
    /// hero does Mod Manager's own hero field apply, tagged with the mod's overall kinds.
    /// </summary>
    private static Dictionary<Hero, IReadOnlyList<string>> HeroesOf(IReadOnlyList<string> entries, string? managerHero, IReadOnlyDictionary<string, int> kinds)
    {
        var stray = ModAttribution.StrayHeroes(entries);
        var found = ModAttribution.HeroKinds(entries).Where(pair => !stray.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        if (found.Count == 0 && Hero.FindByName(managerHero) is { } hero)
        {
            var tags = kinds.Keys.Select(kind => kind switch { "model" or "material" => ModAttribution.Model, "animation" => ModAttribution.Animation, "sound" => ModAttribution.Sound, "ui" => ModAttribution.Ui, "particles" => ModAttribution.Particles, _ => string.Empty })
                .ToHashSet();
            found[hero] = [.. ModAttribution.Tags.Where(tags.Contains)];
        }

        return found;
    }

    /// <summary>Heroes whose model paths the mod ships: only those make it open on the hero's model (not Mod Manager's hero field).</summary>
    private static Hero[] ModelHeroes(IReadOnlyList<string> entries) =>
        [.. ModAttribution.HeroKinds(entries).Where(pair => pair.Value.Contains(ModAttribution.Model) && !ModAttribution.StrayHeroes(entries).Contains(pair.Key)).Select(pair => pair.Key).OrderBy(hero => hero.Name, StringComparer.OrdinalIgnoreCase)];

    private static string[] StrayNames(IReadOnlyList<string> entries) => [.. ModAttribution.StrayHeroes(entries).Select(hero => hero.Name).Order(StringComparer.OrdinalIgnoreCase)];

    private static Dictionary<string, IReadOnlyList<string>> HeroKinds(Dictionary<Hero, IReadOnlyList<string>> heroes) =>
        heroes.OrderBy(pair => pair.Key.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(pair => pair.Key.Name, pair => pair.Value);

    private static string GroupOfMod(Dictionary<Hero, IReadOnlyList<string>> heroes, IReadOnlyList<string> entries) =>
        heroes.Count > 0 ? string.Empty : ModAttribution.GroupOf(entries);

    /// <summary>0 for a hero's main model (named like one of its folders: models/heroes_*/x[_v2]/x.vmdl), 1 for other hero
    /// models, 2 for the rest.</summary>
    private static int MainModelRank(string path)
    {
        var parts = path.Split('/');
        if (!path.StartsWith("models/heroes", StringComparison.OrdinalIgnoreCase) || parts.Length < 4)
        {
            return 2;
        }

        var name = Path.GetFileNameWithoutExtension(parts[^1]);
        return parts[2..^1].Any(folder => Hero.StripVersion(folder).Equals(name, StringComparison.OrdinalIgnoreCase)) ? 0 : 1;
    }

    /// <summary>The game build when it can be read (citadel/steam.inf), else null.</summary>
    public async Task<int?> GameBuildAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return (await new GameBuildReader().ReadAsync(settings.Game, cancellationToken).ConfigureAwait(false)).Build;
        }
        catch (S2ModKitException)
        {
            return null;
        }
    }

    /// <summary>True when the game build is newer than the last one the doctor checked (a banner suggests a check).</summary>
    public async Task<bool> GameUpdatedSinceCheckAsync(CancellationToken cancellationToken = default) =>
        await GameBuildAsync(cancellationToken).ConfigureAwait(false) is { } build && DoctorStore.LastBuild(settings.DoctorRoot) is { } last && build > last;

    /// <summary>
    /// The mod doctor (read-only): records every mod's facts for the current game build and compares them with the snapshot of the
    /// previous build the doctor saw. The first check only records the baseline. The findings are kept until the next check.
    /// </summary>
    public async Task<DoctorReport> CheckModsAsync(IReadOnlyList<ModEntry> mods, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mods);
        var build = await GameBuildAsync(cancellationToken).ConfigureAwait(false)
            ?? throw Errors.Input("GAME_BUILD_UNKNOWN", "The game build cannot be read (citadel/steam.inf).", "Choose the Deadlock game folder.");
        return await Task.Run(() =>
        {
            using var scanner = new DoctorScanner(settings.Game);
            var checkedMods = mods.Where(mod => !mod.Key.StartsWith("app:", StringComparison.Ordinal)).ToArray();   // a disabled app install lives outside addons
            var facts = new List<ModFacts>();
            foreach (var (mod, i) in checkedMods.Select((mod, i) => (mod, i)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Checking {i + 1}/{checkedMods.Length}: {mod.Name}");
                facts.Add(scanner.Read(mod.Key, mod.Name, mod.Files));
            }

            var current = new DoctorSnapshot(build, clock.UtcNow, facts);
            var baseline = DoctorStore.Before(settings.DoctorRoot, build);
            DoctorStore.Save(settings.DoctorRoot, current);
            var report = new DoctorReport(build, baseline?.Build, DoctorRules.Compare(baseline, current));
            File.WriteAllText(Path.Combine(settings.DoctorRoot, "findings.json"), System.Text.Json.JsonSerializer.Serialize(report));
            return report;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The findings of the last check, if any.</summary>
    public DoctorReport? LastReport()
    {
        var path = Path.Combine(settings.DoctorRoot, "findings.json");
        try
        {
            return File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<DoctorReport>(File.ReadAllText(path)) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Repairs a mod whose older copies of game data files hide the game's new versions: writes a copy of the mod without
    /// <paramref name="drop"/> (its other files unchanged, byte for byte), installs it and keeps the original disabled as the
    /// "old version" of the same mod (versions in the library). Nothing inside a file is rewritten. Refused while the game runs.
    /// </summary>
    public async Task<CatalogChange> RepairAsync(ModEntry mod, IReadOnlyList<string> drop, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(drop);
        Management.EnsureGameStopped();
        if (mod.Files.Count != 1 || !mod.Loaded)
        {
            throw Errors.Input("DOCTOR_REPAIR_UNSUPPORTED", "Only an enabled mod in one archive can be repaired here.", "Enable the mod first; split mods are not repaired yet.");
        }

        var entries = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        using (var source = new SteamDatabase.ValvePak.Package())
        {
            source.Read(mod.Files[0]);
            foreach (var entry in source.Entries!.SelectMany(group => group.Value))
            {
                var path = entry.GetFullPath();
                if (!drop.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    source.ReadEntry(entry, out var bytes);
                    entries[path] = bytes;
                }
            }
        }

        if (entries.Count == 0)
        {
            throw Errors.Input("DOCTOR_REPAIR_EMPTY", "Without those files the mod would be empty.", "Disable or delete the mod instead.");
        }

        var day = clock.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var repairs = Path.Combine(settings.Data, "repairs");
        Directory.CreateDirectory(repairs);
        var package = Path.Combine(repairs, $"{Sanitize(mod.Name)}-repaired-{clock.UtcNow:yyyyMMdd-HHmmss}_dir.vpk");
        await S2ModKit.Adapters.Vpk.CompactVpkArchiveIO.WriteAsync(package, entries, cancellationToken).ConfigureAwait(false);

        var disabled = await DisableAsync(mod, cancellationToken).ConfigureAwait(false);   // the original must not win over its repair
        AddonFileResult installed;
        try
        {
            installed = await InstallAsync(package, $"{mod.Name} (repaired {day})", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if ((await LoadAsync(CancellationToken.None).ConfigureAwait(false)).FirstOrDefault(entry => entry.Key == disabled.SelectKey) is { } original)
            {
                await EnableAsync(original, CancellationToken.None).ConfigureAwait(false);   // put the original back
            }

            throw;
        }

        if (installed.Receipt is not { } receipt)
        {
            return new CatalogChange(disabled.SelectKey, installed.Summary);
        }

        var repairedKey = AddonNaming.FileKey(receipt.TargetFileName);
        var library = await libraryStore.LoadAsync(settings.LibraryPath, cancellationToken).ConfigureAwait(false);
        var known = library.FindByKey(disabled.SelectKey);
        var versions = new List<ModLibraryVersion>();
        if (known is null)
        {
            versions.Add(new ModLibraryVersion { Key = disabled.SelectKey, Label = "old version" });
        }

        versions.Add(new ModLibraryVersion { Key = repairedKey, Label = $"repaired {day}" });
        await libraryStore.SaveAsync(settings.LibraryPath, library.WithVersions(known?.Name ?? mod.Name, versions, clock.UtcNow), cancellationToken).ConfigureAwait(false);
        return new CatalogChange(repairedKey, $"Repaired '{mod.Name}': {drop.Count} outdated game file(s) left out. The original is kept disabled as the old version.");
    }

    /// <summary>
    /// Builds an edited copy with parts of <paramref name="modelPath"/> hidden (no triangles; the rest of the model byte for byte).
    /// For a mod: the whole mod is copied with the edited model, the copy is installed and the original kept disabled as a version
    /// of the same mod. For the base game (<paramref name="mod"/> null): a new mod with only the edited model. Refused while the
    /// game runs.
    /// </summary>
    public async Task<CatalogChange> BuildEditedAsync(ModEntry? mod, string modelPath, IReadOnlySet<int> hidden, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        Management.EnsureGameStopped();
        if (hidden.Count == 0)
        {
            throw Errors.Input("MODEL_EDIT_NOTHING", "No part is hidden.", "Untick the parts to hide first.");
        }

        if (mod is not null && (mod.Files.Count != 1 || !mod.Loaded))
        {
            throw Errors.Input("MODEL_EDIT_UNSUPPORTED", "Only an enabled mod in one archive can be edited here.", "Enable the mod first; split mods are not edited yet.");
        }

        var logical = (modelPath.EndsWith("_c", StringComparison.Ordinal) ? modelPath : modelPath + "_c").Replace('\\', '/');
        var entries = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        var sources = mod is not null ? [mod.Files[0]] : new[] { Path.Combine(settings.Game, "citadel", "pak01_dir.vpk") };
        byte[]? model = null;
        foreach (var archive in sources)
        {
            using var source = new SteamDatabase.ValvePak.Package();
            source.Read(archive);
            foreach (var entry in source.Entries!.SelectMany(group => group.Value))
            {
                var path = entry.GetFullPath();
                if (mod is null && !path.Equals(logical, StringComparison.OrdinalIgnoreCase))
                {
                    continue;                                               // the base game: only the model
                }

                source.ReadEntry(entry, out var bytes);
                if (path.Equals(logical, StringComparison.OrdinalIgnoreCase))
                {
                    model = bytes;
                }
                else
                {
                    entries[path] = bytes;
                }
            }
        }

        if (model is null)
        {
            throw Errors.Input("MODEL_EDIT_MODEL_MISSING", $"'{logical}' is not in {(mod is null ? "the game" : $"'{mod.Name}'")}.", "Choose the model the mod ships.");
        }

        entries[logical] = S2ModKit.Adapters.Source2.CompiledModelEdit.HideDrawCalls(model, logical, hidden);
        var label = $"edited: {hidden.Count} part{(hidden.Count == 1 ? string.Empty : "s")} hidden";
        var name = mod?.Name ?? Hero.Catalogue.FirstOrDefault(hero => hero.Model.Equals(modelPath.Replace("_c", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))?.Name ?? Path.GetFileNameWithoutExtension(modelPath);
        var edits = Path.Combine(settings.Data, "edits");
        Directory.CreateDirectory(edits);
        var package = Path.Combine(edits, $"{Sanitize(name)}-edited-{clock.UtcNow:yyyyMMdd-HHmmss}_dir.vpk");
        await S2ModKit.Adapters.Vpk.CompactVpkArchiveIO.WriteAsync(package, entries, cancellationToken).ConfigureAwait(false);

        return await InstallVersionAsync(mod, name, package, label, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Installs <paramref name="package"/>: for the base game (<paramref name="mod"/> null) as a new mod; for a mod as a version of
    /// it (the original disabled first so it cannot win, put back if the install fails, both listed in the library).
    /// </summary>
    private async Task<CatalogChange> InstallVersionAsync(ModEntry? mod, string name, string package, string label, CancellationToken cancellationToken)
    {
        if (mod is null)
        {
            var result = await InstallAsync(package, $"{name} ({label})", cancellationToken).ConfigureAwait(false);
            return new CatalogChange(result.Receipt is { } installedReceipt ? AddonNaming.FileKey(installedReceipt.TargetFileName) : string.Empty,
                result.Receipt is null ? result.Summary : $"Installed '{name} ({label})'. It loads like any other mod; disable or delete it to go back.");
        }

        var disabled = await DisableAsync(mod, cancellationToken).ConfigureAwait(false);   // the original must not win over its edit
        AddonFileResult installed;
        try
        {
            installed = await InstallAsync(package, $"{mod.Name} ({label})", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if ((await LoadAsync(CancellationToken.None).ConfigureAwait(false)).FirstOrDefault(entry => entry.Key == disabled.SelectKey) is { } original)
            {
                await EnableAsync(original, CancellationToken.None).ConfigureAwait(false);   // put the original back
            }

            throw;
        }

        if (installed.Receipt is not { } receipt)
        {
            return new CatalogChange(disabled.SelectKey, installed.Summary);
        }

        var versionKey = AddonNaming.FileKey(receipt.TargetFileName);
        var library = await libraryStore.LoadAsync(settings.LibraryPath, cancellationToken).ConfigureAwait(false);
        var known = library.FindByKey(disabled.SelectKey);
        var versions = new List<ModLibraryVersion>();
        if (known is null)
        {
            versions.Add(new ModLibraryVersion { Key = disabled.SelectKey, Label = "original" });
        }

        versions.Add(new ModLibraryVersion { Key = versionKey, Label = label });
        await libraryStore.SaveAsync(settings.LibraryPath, library.WithVersions(known?.Name ?? mod.Name, versions, clock.UtcNow), cancellationToken).ConfigureAwait(false);
        return new CatalogChange(versionKey, $"Built '{mod.Name} ({label})'. The original is kept disabled as a version of the same mod.");
    }

    /// <summary>
    /// Builds a recolored copy: each changed part's colour texture is decoded, every part's change applied through that part's UV
    /// mask (<see cref="RecolorMath"/>) and the pixels written back in the texture's own format. For a mod: the whole mod
    /// with the new textures, installed as a version beside the original; for the base game: a new mod with only the textures.
    /// Evidence beside the package: <c>recolor.json</c> (per part: texture, change, texels, mean colour before and after).
    /// </summary>
    public async Task<CatalogChange> BuildRecoloredAsync(ModEntry? mod, string modelPath, IReadOnlyList<PartRecolor> parts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parts);
        Management.EnsureGameStopped();
        if (parts.Count == 0)
        {
            throw Errors.Input("RECOLOR_NOTHING", "No part is recolored.", "Change a part's colour first.");
        }

        if (mod is not null && (mod.Files.Count != 1 || !mod.Loaded))
        {
            throw Errors.Input("RECOLOR_UNSUPPORTED", "Only an enabled mod in one archive can be recolored here.", "Enable the mod first; split mods are not recolored yet.");
        }

        var archives = new List<string>();
        if (mod is not null)
        {
            archives.Add(mod.Files[0]);
        }

        archives.Add(Path.Combine(settings.Game, "citadel", "pak01_dir.vpk"));
        archives.Add(Path.Combine(settings.Game, "core", "pak01_dir.vpk"));
        var workshop = new S2ModKit.Adapters.Source2.VrfTextureWorkshop();
        var textures = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        var evidence = new List<object>();
        foreach (var group in parts.GroupBy(part => part.Texture, StringComparer.OrdinalIgnoreCase))
        {
            var texture = await workshop.DecodeAsync(archives, group.Key, cancellationToken).ConfigureAwait(false);
            var rgba = (byte[])texture.Rgba.Clone();
            foreach (var part in group)
            {
                if (part.Width != texture.Width || part.Height != texture.Height)
                {
                    throw Errors.Verification("RECOLOR_MASK_SIZE", $"The mask of part {part.Part + 1} is {part.Width}x{part.Height}; '{group.Key}' is {texture.Width}x{texture.Height}.", "Open the preview again.");
                }

                var before = RecolorMath.Mean(rgba, part.Mask);
                var texels = RecolorMath.Apply(rgba, part.Mask, part.Change);
                evidence.Add(new { part = part.Part, texture = group.Key, change = part.Change, texels, meanBefore = before.ToString(), meanAfter = RecolorMath.Mean(rgba, part.Mask).ToString() });
            }

            textures[group.Key + "_c"] = await workshop.CompileAsync(texture, rgba, settings.StagingRoot, cancellationToken).ConfigureAwait(false);   // in place: no compiler
        }

        var entries = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        if (mod is not null)
        {
            using var source = new SteamDatabase.ValvePak.Package();
            source.Read(mod.Files[0]);
            foreach (var entry in source.Entries!.SelectMany(group => group.Value))
            {
                source.ReadEntry(entry, out var bytes);
                entries[entry.GetFullPath()] = bytes;
            }
        }

        foreach (var (path, bytes) in textures)
        {
            entries[path] = bytes;
        }

        var label = $"recolored: {parts.Count} part{(parts.Count == 1 ? string.Empty : "s")}";
        var name = mod?.Name ?? Hero.Catalogue.FirstOrDefault(hero => hero.Model.Equals(modelPath.Replace("_c", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))?.Name ?? Path.GetFileNameWithoutExtension(modelPath);
        var folder = Path.Combine(settings.Data, "recolors", $"{Sanitize(name)}-{clock.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(folder);
        var package = Path.Combine(folder, $"{Sanitize(name)}-recolored_dir.vpk");
        await S2ModKit.Adapters.Vpk.CompactVpkArchiveIO.WriteAsync(package, entries, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(folder, "recolor.json"), System.Text.Json.JsonSerializer.Serialize(new { operation = "recolor@1", model = modelPath, mod = mod?.Name, parts = evidence },
            IndentedJson), cancellationToken).ConfigureAwait(false);
        return await InstallVersionAsync(mod, name, package, label, cancellationToken).ConfigureAwait(false);
    }

    private static string Sanitize(string label) => new([.. label.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').Take(40)]);
}
