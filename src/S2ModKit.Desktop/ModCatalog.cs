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
internal sealed record CatalogChange(string SelectKey, string Message)
{
    /// <summary>The install receipts of what the change installed (archive installs), for records such as the GameBanana source.</summary>
    public IReadOnlyList<S2ModKit.Application.AddonFileInstallReceipt> Receipts { get; init; } = [];
}

/// <summary>One recolored part for a build: its colour texture (path without <c>_c</c>), its UV mask in that texture's size and the change.</summary>
internal sealed record PartRecolor(int Part, string Texture, float[] Mask, int Width, int Height, ColorAdjustment Change, string? Material = null,
    RgbColor? RangeTarget = null, float RangeTolerance = 0);

/// <summary>One shiny part for a build: its normal-roughness texture (path without <c>_c</c>) and the shine (0-1, lower roughness).</summary>
internal sealed record PartShine(int Part, string Texture, float Shine);

/// <summary>A part drawn see-through: its material (<c>.vmat</c> path) gets <paramref name="Opacity"/> (1 solid, 0 invisible).</summary>
internal sealed record PartOpacity(int Part, string Material, float Opacity);

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
internal sealed partial class ModCatalog(AppSettings settings, string? managerState = null, IGameProcessProbe? gameProbe = null, IRecycleBin? recycleBin = null)
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

        // a file renamed outside the app keeps its receipt by content: the one installed package of exactly its size and file count
        var installedPackages = installs.Where(install => install.Receipt.Status == "installed").Select(install => (Install: install, Size: PackageSize(install.Receipt))).ToArray();
        AppInstall? ByContent(AddonArchiveSummary archive)
        {
            var matches = installedPackages.Where(item => item.Size == archive.Size && item.Install.Receipt.EntryCount == archive.EntryCount)
                .Select(item => item.Install.Receipt.PackagePath).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
            return matches.Length == 1
                ? installedPackages.Where(item => item.Install.Receipt.PackagePath.Equals(matches[0], StringComparison.OrdinalIgnoreCase)).MaxBy(item => item.Install.Receipt.CreatedUtc).Install
                : null;
        }

        var firstInstalled = installs.GroupBy(install => install.Receipt.PackagePath, StringComparer.OrdinalIgnoreCase)    // a re-enable writes a new receipt: keep the first date
            .ToDictionary(g => g.Key, g => g.Min(install => install.FirstCreatedUtc), StringComparer.OrdinalIgnoreCase);
        var mods = new List<ModEntry>();
        foreach (var group in scan.Archives.GroupBy(archive => archive.ModId ?? AddonNaming.FileKey(archive.FileName)))
        {
            var archives = group.OrderBy(archive => archive.Slot ?? int.MaxValue).ThenBy(archive => archive.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
            var first = archives[0];
            var install = archives.Select(archive => byTarget.GetValueOrDefault(archive.FileName) is { } found && Describes(found.Receipt, archive) ? found : ByContent(archive))
                .FirstOrDefault(found => found is not null);
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
        var listed = mods.Select(mod => mod with { Versions = VersionsOf(library, mod, mods), Conflicts = conflicts.GetValueOrDefault(mod.Key) }).OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return DismissedConflicts.Apply(listed, Dismissed.Load());                    // conflicts marked as fine are not shown   // by name: a mod keeps its place when switched
    }

    /// <summary>
    /// Whether an install receipt still describes the file of its name: another mod can take the same slot later (a manager or a
    /// manual copy), and the app's old label then named someone else's mod (a mod shown as an old test install was deleted). The
    /// file must have the receipt's file count and, while the installed package still exists, the package's size.
    /// </summary>
    internal static bool Describes(AddonFileInstallReceipt receipt, AddonArchiveSummary archive)
    {
        if (receipt.EntryCount > 0 && archive.EntryCount != receipt.EntryCount)
        {
            return false;
        }

        try
        {
            return !File.Exists(receipt.PackagePath) || new FileInfo(receipt.PackagePath).Length == archive.Size;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;                                                     // unreadable package path: the file count decided
        }
    }

    private static long? PackageSize(AddonFileInstallReceipt receipt)
    {
        try
        {
            return File.Exists(receipt.PackagePath) ? new FileInfo(receipt.PackagePath).Length : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
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
    public async Task<CatalogChange> InstallArchiveAsync(string archivePath, IReadOnlyList<ArchiveVariant> chosen, CancellationToken cancellationToken = default) =>
        await InstallArchiveAsync(archivePath, chosen, null, cancellationToken).ConfigureAwait(false);

    /// <summary>As <see cref="InstallArchiveAsync(string, IReadOnlyList{ArchiveVariant}, CancellationToken)"/>, named <paramref name="label"/>
    /// (a GameBanana mod's name) instead of the archive's file name; the receipts of what was installed are returned too.</summary>
    public async Task<CatalogChange> InstallArchiveAsync(string archivePath, IReadOnlyList<ArchiveVariant> chosen, string? label, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        Management.EnsureGameStopped();
        var baseName = label ?? Path.GetFileNameWithoutExtension(archivePath);
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
                var variantLabel = layout.Count == 1 ? baseName : $"{baseName} - {(variant.Folder.Length > 0 ? variant.Folder[(variant.Folder.LastIndexOf('/') + 1)..] : Path.GetFileNameWithoutExtension(variant.FileName))}";
                try
                {
                    if ((await InstallAsync(dirPath, variantLabel, cancellationToken).ConfigureAwait(false)).Receipt is { } receipt)
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
            message + (refused.Count > 0 ? " Refused: " + string.Join(" ", refused) : string.Empty))
        { Receipts = installed };
    }

    /// <summary>
    /// Installs a downloaded update of <paramref name="mod"/> (a <c>.vpk</c>, or the chosen <paramref name="variant"/> of a zip, 7z or rar)
    /// as a new version of the same mod: the current files stay as a disabled version. Refused while the game runs.
    /// </summary>
    public async Task<CatalogChange> UpdateAsync(ModEntry mod, string download, ArchiveVariant? variant, string label, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        Management.EnsureGameStopped();
        if (Path.GetExtension(download).Equals(".vpk", StringComparison.OrdinalIgnoreCase))
        {
            return await InstallVersionAsync(mod, mod.Name, download, label, cancellationToken).ConfigureAwait(false);
        }

        if (variant is null)
        {
            throw Errors.Input("UPDATE_VARIANT_REQUIRED", "The update is an archive; choose which variant to install.", "Pick one of the archive's variants.");
        }

        var staging = Path.Combine(Path.GetFullPath(settings.StagingRoot), $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}");
        try
        {
            var layout = ArchiveVariants.Layout([variant], staging);
            await archives.ExtractAsync(download, [.. layout.SelectMany(item => item.Extractions)], staging, cancellationToken).ConfigureAwait(false);
            return await InstallVersionAsync(mod, mod.Name, layout[0].DirPath, label, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);                  // only the folder created above, inside the staging root
            }
        }
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

    /// <summary>Moves an enabled mod to the first place in the load order (wins over every other mod) or the last.</summary>
    public async Task<CatalogChange> MoveToEndAsync(ModEntry mod, bool first, IReadOnlyList<ModEntry> all, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(all);
        var result = await Management.ReorderToEndAsync(settings.AddonsRoot, LoadOrderOf(all), mod.Key, first, settings.JournalRoot, cancellationToken).ConfigureAwait(false);
        return await ChangeAsync(mod, result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves an enabled mod to the place of <paramref name="target"/> in the load order (drag and drop).</summary>
    public async Task<CatalogChange> MoveToPlaceOfAsync(ModEntry mod, ModEntry target, IReadOnlyList<ModEntry> all, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(target);
        var result = await Management.ReorderToPlaceOfAsync(settings.AddonsRoot, LoadOrderOf(all), mod.Key, target.Key, settings.JournalRoot, cancellationToken).ConfigureAwait(false);
        return await ChangeAsync(mod, result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Puts an enabled mod at pak number <paramref name="slot"/>; with <paramref name="swap"/>, the mod there takes its old slot.</summary>
    public async Task<CatalogChange> MoveToSlotAsync(ModEntry mod, int slot, bool swap, IReadOnlyList<ModEntry> all, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(all);
        var result = await Management.ReorderToSlotAsync(settings.AddonsRoot, LoadOrderOf(all), mod.Key, slot, swap, settings.JournalRoot, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Conflicts the user marked as fine.</summary>
    public DismissedConflicts Dismissed => new(Path.Combine(settings.Data, "conflicts-ok.json"));

    /// <summary>The saved profiles (named sets of enabled mods).</summary>
    public ModProfiles Profiles => new(Path.Combine(settings.Data, "profiles.json"));

    /// <summary>
    /// Switches to <paramref name="profile"/>: the enabled mods outside it are disabled and its disabled mods enabled in its load order,
    /// in one journaled rename (refused while the game runs). An app install the old way disabled (only its package kept) is installed
    /// again afterwards. Mods of the profile that are no longer in the folder are named in the message.
    /// </summary>
    public async Task<CatalogChange> ApplyProfileAsync(ModProfile profile, IReadOnlyList<ModEntry> mods, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Management.EnsureGameStopped();
        var plan = ModProfiles.Plan(profile, mods);
        var disable = new List<AddonModTarget>();
        foreach (var mod in plan.Disable)
        {
            disable.Add(await TargetOfAsync(mod, cancellationToken).ConfigureAwait(false));
        }

        var enable = new List<AddonModTarget>();
        foreach (var mod in plan.Enable.Where(mod => !mod.Key.StartsWith("app:", StringComparison.Ordinal)))
        {
            enable.Add(await TargetOfAsync(mod, cancellationToken).ConfigureAwait(false));
        }

        var result = await Management.SwitchSetAsync(settings.AddonsRoot, profile.Name, disable, enable, settings.JournalRoot, cancellationToken).ConfigureAwait(false);
        var note = await FollowRecordsAsync(result, cancellationToken).ConfigureAwait(false);
        foreach (var legacy in plan.Enable.Where(mod => mod.Key.StartsWith("app:", StringComparison.Ordinal)))
        {
            await EnableAsync(legacy, cancellationToken).ConfigureAwait(false);
        }

        var message = plan.IsEmpty ? $"The mods already match '{profile.Name}'. No files changed."
            : $"Switched to '{profile.Name}': {plan.Disable.Count} mod{(plan.Disable.Count == 1 ? string.Empty : "s")} disabled, {plan.Enable.Count} enabled.";
        if (plan.Missing.Count > 0)
        {
            message += $" Not in the addons folder any more: {string.Join(", ", plan.Missing.Select(mod => $"'{mod.Name}'"))}.";
        }

        return new CatalogChange(string.Empty, message + note);
    }

    /// <summary>After a rename: the app's receipts, the library and the profiles follow the new names; the list selects the mod under its new key.</summary>
    private async Task<CatalogChange> ChangeAsync(ModEntry mod, AddonOperationResult result, CancellationToken cancellationToken)
    {
        var note = await FollowRecordsAsync(result, cancellationToken).ConfigureAwait(false);
        var first = Path.GetFileName(mod.Files[0]);
        var key = mod.Source == ModSource.Manager || mod.Key.StartsWith("app:", StringComparison.Ordinal) ? mod.Key
            : result.Renames.FirstOrDefault(item => item.From.Equals(first, StringComparison.OrdinalIgnoreCase)) is { } renamed ? AddonNaming.FileKey(renamed.To) : mod.Key;
        return new CatalogChange(key, result.Summary + note);
    }

    /// <summary>The app's own records follow a rename; returns a note for the message when they could not.</summary>
    private async Task<string> FollowRecordsAsync(AddonOperationResult result, CancellationToken cancellationToken)
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

                Profiles.FollowRenames(result.Renames);
            }
#pragma warning disable CA1031 // the files are renamed and verified; say that the app's own records lag
            catch (Exception exception)
#pragma warning restore CA1031
            {
                note = $" The files were renamed, but the app's records could not be updated: {exception.Message}";
            }
        }

        return note;
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
    public async Task<CatalogChange> RepairAsync(ModEntry mod, IReadOnlyList<string> drop, CancellationToken cancellationToken = default) =>
        await RepairAsync(mod, drop, null, null, cancellationToken).ConfigureAwait(false);

    /// <summary>The repair copy: the mod without <paramref name="drop"/> and with <paramref name="replace"/> (files written instead of the
    /// mod's own); <paramref name="evidence"/> goes beside the package as <c>repair.json</c>.</summary>
    private async Task<CatalogChange> RepairAsync(ModEntry mod, IReadOnlyList<string> drop, Dictionary<string, byte[]>? replace, object? evidence, CancellationToken cancellationToken)
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
                if (replace is not null && replace.TryGetValue(path, out var replaced))
                {
                    entries[path] = replaced;
                }
                else if (!drop.Contains(path, StringComparer.OrdinalIgnoreCase))
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
        var word = drop.Count == 0 && replace is { Count: > 0 } ? "updated" : "repaired";   // only older models completed: an update, not a repair
        var repairs = Path.Combine(settings.Data, "repairs");
        Directory.CreateDirectory(repairs);
        var package = Path.Combine(repairs, $"{Sanitize(mod.Name)}-{word}-{clock.UtcNow:yyyyMMdd-HHmmss}_dir.vpk");
        await S2ModKit.Adapters.Vpk.CompactVpkArchiveIO.WriteAsync(package, entries, cancellationToken).ConfigureAwait(false);
        if (evidence is not null)
        {
            await File.WriteAllTextAsync(Path.ChangeExtension(package, ".repair.json"), System.Text.Json.JsonSerializer.Serialize(evidence, IndentedJson), cancellationToken).ConfigureAwait(false);
        }

        var disabled = await DisableAsync(mod, cancellationToken).ConfigureAwait(false);   // the original must not win over its repair
        AddonFileResult installed;
        try
        {
            installed = await InstallAsync(package, $"{mod.Name} ({word} {day})", cancellationToken).ConfigureAwait(false);
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

        versions.Add(new ModLibraryVersion { Key = repairedKey, Label = $"{word} {day}" });
        await libraryStore.SaveAsync(settings.LibraryPath, library.WithVersions(known?.Name ?? mod.Name, versions, clock.UtcNow), cancellationToken).ConfigureAwait(false);
        var what = new[]
        {
            replace is { Count: > 0 } ? $"{replace.Count} model(s) got the game's current settings and attachment points" : null,
            drop.Count > 0 ? $"{drop.Count} outdated game file(s) left out" : null,
        };
        return new CatalogChange(repairedKey, $"{(word == "updated" ? "Updated" : "Repaired")} '{mod.Name}': {string.Join("; ", what.OfType<string>())}. The original is kept disabled as the old version.");
    }

    /// <summary>Builds an edited copy with parts hidden (see <see cref="BuildEditsAsync"/>).</summary>
    public Task<CatalogChange> BuildEditedAsync(ModEntry? mod, string modelPath, IReadOnlySet<int> hidden, CancellationToken cancellationToken = default) =>
        BuildEditsAsync(mod, modelPath, hidden, [], null, null, null, null, null, cancellationToken);

    /// <summary>Builds a recolored copy (see <see cref="BuildEditsAsync"/>).</summary>
    public Task<CatalogChange> BuildRecoloredAsync(ModEntry? mod, string modelPath, IReadOnlyList<PartRecolor> parts, CancellationToken cancellationToken = default) =>
        BuildEditsAsync(mod, modelPath, new HashSet<int>(), parts, null, null, null, null, null, cancellationToken);

    /// <summary>
    /// Builds one edited copy of <paramref name="modelPath"/> with every change made in the app: <paramref name="hidden"/> parts get
    /// no triangles (the rest of the model byte for byte) and each recolored part's colour texture is decoded, changed through the
    /// part's UV mask (<see cref="RecolorMath"/>) and written back in its own format. For a mod: the whole mod with the changed
    /// files, installed as a version beside the original (kept disabled); for the base game (<paramref name="mod"/> null): a new
    /// mod with only the changed files. Evidence beside the package: <c>edit.json</c> (hidden parts; per recolored part: texture,
    /// change, texels, mean colour before and after). Refused while the game runs.
    /// </summary>
    public Task<CatalogChange> BuildEditsAsync(ModEntry? mod, string modelPath, IReadOnlySet<int> hidden, IReadOnlyList<PartRecolor> recolors,
        IReadOnlyList<PartShapeEdit>? shapes = null, IReadOnlyDictionary<string, EffectColorChange>? effects = null, IReadOnlyList<PartShine>? shines = null,
        IReadOnlyList<S2ModKit.Adapters.Source2.AddedEffect>? added = null, IReadOnlyList<PartOpacity>? opacities = null, CancellationToken cancellationToken = default) =>
        BuildEditsCoreAsync(mod, modelPath, hidden, recolors, shapes, effects, shines, added, opacities, null, cancellationToken);

    /// <summary>
    /// Re-apply edits: builds a mod the app made again from its recipe (<see cref="EditRecipe"/>) on the current game files, or the base
    /// mod's current file (also when that base is disabled, as it is under its edit), and installs it as a new version of
    /// <paramref name="edited"/> (the old version stays disabled). Recolor masks are computed from the current model without a window.
    /// </summary>
    public async Task<CatalogChange> ReapplyAsync(ModEntry edited, IReadOnlyList<ModEntry> all, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(all);
        var recipe = EditRecipe.Of(edited) ?? throw Errors.Input("REAPPLY_NO_RECIPE", $"'{edited.Name}' has no saved edits.", "Only mods built after the app started saving edits can be re-applied; build this one again once.");
        var baseMod = recipe.BaseIn(all, edited);
        if (recipe.BaseName is not null && baseMod is null)
        {
            throw Errors.Input("REAPPLY_BASE_MISSING", $"The mod '{recipe.BaseName}' that '{edited.Name}' was made from is not in the addons folder.", "Install that mod again, then re-apply.");
        }

        var archives = new List<string>();
        if (baseMod is not null)
        {
            archives.Add(baseMod.Files[0]);
        }

        archives.Add(Path.Combine(settings.Game, "citadel", "pak01_dir.vpk"));
        archives.Add(Path.Combine(settings.Game, "core", "pak01_dir.vpk"));
        var logical = (recipe.Model.EndsWith("_c", StringComparison.Ordinal) ? recipe.Model : recipe.Model + "_c").Replace('\\', '/');
        var recolors = new List<PartRecolor>();
        if (recipe.Recolors.Length > 0)
        {
            var model = ReadFirst(archives, logical) ?? throw Errors.Input("MODEL_EDIT_MODEL_MISSING", $"'{logical}' is not in the game or the base mod.", "The model moved in a game update; edit it again.");
            var workshop = new S2ModKit.Adapters.Source2.VrfTextureWorkshop();
            var recolored = recipe.Recolors.Select(part => part.Part).ToHashSet();   // a part's mask leaves out its pieces with their own colour
            foreach (var part in recipe.Recolors)
            {
                var texture = await workshop.DecodeAsync(archives, part.Texture, cancellationToken).ConfigureAwait(false);
                var mask = S2ModKit.Adapters.Preview.PartMasks.Mask(model, logical, part.Part, texture.Width, texture.Height, texture.Rgba, part.RangeTarget, part.RangeTolerance, recolored);
                recolors.Add(new PartRecolor(part.Part, part.Texture, mask, texture.Width, texture.Height, part.Change, part.Material, part.RangeTarget, part.RangeTolerance));
            }
        }

        var shapes = recipe.Shapes.Select(shape => new PartShapeEdit(shape.Part, shape.Material,
            new PartShape(Vector(shape.Scale), Vector(shape.RotationDegrees), Vector(shape.Move), shape.Pivot), Vector(shape.BoundsMin), Vector(shape.BoundsMax), shape.Displacement, shape.Level)).ToArray();
        var change = await BuildEditsCoreAsync(baseMod, recipe.Model, recipe.Hidden.ToHashSet(), recolors, shapes, recipe.Effects,
            [.. (recipe.Shines ?? []).Select(shine => new PartShine(shine.Part, shine.Texture, shine.Shine))],
            [.. (recipe.Added ?? []).Select(effect => new S2ModKit.Adapters.Source2.AddedEffect(effect.Effect, effect.Bone, Vector(effect.Offset), effect.Source, effect.Parent))],
            [.. (recipe.Opacities ?? []).Select(part => new PartOpacity(part.Part, part.Material, part.Opacity))], edited, cancellationToken).ConfigureAwait(false);
        return baseMod is null ? change : change with { Message = change.Message + " Edits re-applied. The original mod may still need an update from its author." };

        static System.Numerics.Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
    }

    private async Task<CatalogChange> BuildEditsCoreAsync(ModEntry? mod, string modelPath, IReadOnlySet<int> hidden, IReadOnlyList<PartRecolor> recolors,
        IReadOnlyList<PartShapeEdit>? shapes, IReadOnlyDictionary<string, EffectColorChange>? effects, IReadOnlyList<PartShine>? shines,
        IReadOnlyList<S2ModKit.Adapters.Source2.AddedEffect>? added, IReadOnlyList<PartOpacity>? opacities, ModEntry? installAs, CancellationToken cancellationToken)
    {
        opacities ??= [];
        shines ??= [];
        added ??= [];
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentNullException.ThrowIfNull(recolors);
        shapes ??= [];
        effects ??= new Dictionary<string, EffectColorChange>();
        Management.EnsureGameStopped();
        if (hidden.Count == 0 && recolors.Count == 0 && shapes.Count == 0 && effects.Count == 0 && shines.Count == 0 && added.Count == 0 && opacities.Count == 0)
        {
            throw Errors.Input("MODEL_EDIT_NOTHING", "Nothing is changed.", "Hide, recolor or reshape a part, or recolor or add effects first.");
        }

        if (mod is not null && (mod.Files.Count != 1 || (!mod.Loaded && installAs is null)))   // re-apply reads a base kept disabled under its edit
        {
            throw Errors.Input("MODEL_EDIT_UNSUPPORTED", "Only an enabled mod in one archive can be edited here.", "Enable the mod first; split mods are not edited yet.");
        }

        var logical = (modelPath.EndsWith("_c", StringComparison.Ordinal) ? modelPath : modelPath + "_c").Replace('\\', '/');
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

        // a piece (a separate mesh inside a part, preview index from the part count up) shares its part's material and texture with the
        // part's other pieces, often the same texture area (both boots): its colour, shine and see-through go to its own copies (below)
        var drawCount = int.MaxValue;
        if (recolors.Count > 0 || shines.Count > 0)
        {
            var baseModel = entries.TryGetValue(logical, out var ownModel) ? ownModel.ToArray() : ReadFirst([Path.Combine(settings.Game, "citadel", "pak01_dir.vpk")], logical);
            drawCount = baseModel is null ? int.MaxValue : S2ModKit.Adapters.Preview.PartMasks.PieceOrPart(baseModel, logical, 0).Parts;
        }
        var sharedRecolors = recolors.Where(part => part.Part < drawCount).ToArray();
        var sharedShines = shines.Where(part => part.Part < drawCount).ToArray();
        var ownRecolors = recolors.Where(part => part.Part >= drawCount).ToDictionary(part => part.Part);
        var ownShines = shines.Where(part => part.Part >= drawCount).ToDictionary(part => part.Part);

        (string Input, string Output)? shapeHashes = null;
        if (shapes.Count > 0)
        {
            // the direct reshape: each part's own vertices in every level of detail, bounds recomputed (shape_part@1)
            var source = entries.TryGetValue(logical, out var shipped) ? shipped.ToArray()
                : ReadFirst([Path.Combine(settings.Game, "citadel", "pak01_dir.vpk")], logical)
                ?? throw Errors.Input("MODEL_EDIT_MODEL_MISSING", $"'{logical}' is not in {(mod is null ? "the game" : $"'{mod.Name}'")}.", "Choose the model the mod ships.");
            entries[logical] = await Task.Run(() => ShapeModelBuilder.Build(source, logical, shapes), cancellationToken).ConfigureAwait(false);
            shapeHashes = (ContentHash.Compute(source).ToString(), ContentHash.Compute(entries[logical].ToArray()).ToString());
        }

        if (hidden.Count > 0)
        {
            var model = entries.GetValueOrDefault(logical);
            if (model.IsEmpty)
            {
                using var game = new SteamDatabase.ValvePak.Package();
                game.Read(Path.Combine(settings.Game, "citadel", "pak01_dir.vpk"));
                if (game.FindEntry(logical) is { } found)
                {
                    game.ReadEntry(found, out var bytes);
                    model = bytes;
                }
            }

            if (model.IsEmpty)
            {
                throw Errors.Input("MODEL_EDIT_MODEL_MISSING", $"'{logical}' is not in {(mod is null ? "the game" : $"'{mod.Name}'")}.", "Choose the model the mod ships.");
            }

            entries[logical] = S2ModKit.Adapters.Source2.CompiledModelEdit.HideDrawCalls(model.ToArray(), logical, hidden);
        }

        var withAbilities = new List<S2ModKit.Adapters.Source2.AddedEffect>();   // added to an ability's effect after the recolors, below
        if (added.Count > 0)
        {
            var game = Path.Combine(settings.Game, "citadel", "pak01_dir.vpk");
            // effects from another mod or a loose file: their files are copied under new names, the source is not touched
            var placed = new List<S2ModKit.Adapters.Source2.AddedEffect>();
            using (var gamePackage = new SteamDatabase.ValvePak.Package())
            {
                gamePackage.Read(game);
                foreach (var effect in added)
                {
                    if (effect.Source is null)
                    {
                        if (!entries.ContainsKey(effect.Effect + "_c") && gamePackage.FindEntry(effect.Effect + "_c") is null)
                        {
                            throw Errors.Input("ADDED_EFFECT_MISSING", $"The effect '{effect.Effect}' is not in the game{(mod is null ? string.Empty : " or the mod")}.", "Choose the effect again; a game update may have removed it.");
                        }

                        placed.Add(effect);
                        continue;
                    }

                    if (!File.Exists(effect.Source))
                    {
                        throw Errors.Input("ADDED_EFFECT_SOURCE", $"'{effect.Source}', where the effect '{Path.GetFileName(effect.Effect)}' came from, is not there any more.", "Install that mod again (or put the file back), or remove the effect and build again.");
                    }

                    var copy = EffectClosure.Read(effect.Source, effect.Effect).Copy($"particles/s2modkit/{Path.GetFileNameWithoutExtension(logical[..^2])}/copied/", path => gamePackage.FindEntry(path) is not null);
                    foreach (var (path, bytes) in copy.Files)
                    {
                        entries[path] = bytes;
                    }

                    placed.Add(effect with { Effect = copy.Effect });
                }
            }

            withAbilities.AddRange(placed.Where(effect => effect.Parent is not null));
            var always = placed.Where(effect => effect.Parent is null).ToArray();
            if (always.Length > 0)
            {
                // always-on effects: the model lists them and gets a point per placed effect (add_model_effects@1)
                var model = entries.TryGetValue(logical, out var current) ? current.ToArray()
                    : ReadFirst([game], logical)
                    ?? throw Errors.Input("MODEL_EDIT_MODEL_MISSING", $"'{logical}' is not in {(mod is null ? "the game" : $"'{mod.Name}'")}.", "Choose the model the mod ships.");
                var template = ReadFirst([game], S2ModKit.Adapters.Source2.CompiledAddedEffects.TemplateEffect + "_c")
                    ?? throw Errors.Unsupported("ADDED_EFFECT_TEMPLATE", "The game no longer has the effect that added effects are built from.", "Update S2ModKit.");
                foreach (var (path, bytes) in await Task.Run(() => S2ModKit.Adapters.Source2.CompiledAddedEffects.Build(model, logical, template, always), cancellationToken).ConfigureAwait(false))
                {
                    entries[path] = bytes;
                }
            }
        }

        var evidence = new List<object>();
        if (sharedRecolors.Length > 0)
        {
            var archives = new List<string>();
            if (mod is not null)
            {
                archives.Add(mod.Files[0]);
            }

            archives.Add(Path.Combine(settings.Game, "citadel", "pak01_dir.vpk"));
            archives.Add(Path.Combine(settings.Game, "core", "pak01_dir.vpk"));
            var workshop = new S2ModKit.Adapters.Source2.VrfTextureWorkshop();
            foreach (var group in sharedRecolors.GroupBy(part => part.Texture, StringComparer.OrdinalIgnoreCase))
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

                entries[group.Key + "_c"] = await workshop.CompileAsync(texture, rgba, settings.StagingRoot, cancellationToken).ConfigureAwait(false);   // in place: no compiler
            }

            // the materials' colour settings (tints, glow, outline) and glow colour texture follow (MaterialColors)
            foreach (var group in sharedRecolors.Where(part => part.Material is not null).GroupBy(part => part.Material!, StringComparer.OrdinalIgnoreCase))
            {
                var path = (group.Key.EndsWith("_c", StringComparison.Ordinal) ? group.Key[..^2] : group.Key).Replace('\\', '/');
                var change = group.First().Change;                              // parts sharing a material share its colours
                var material = entries.TryGetValue(path + "_c", out var shipped) ? shipped.ToArray() : ReadFirst(archives, path + "_c");
                if (material is null)
                {
                    continue;
                }

                var (edited, settingsChanged, glow) = S2ModKit.Adapters.Source2.CompiledMaterialEdit.Recolor(material, path + "_c", change);
                if (settingsChanged.Count > 0)
                {
                    entries[path + "_c"] = edited;
                }

                var glowRecolored = false;
                if (glow is not null && !sharedRecolors.Any(part => string.Equals(part.Texture, glow, StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var glowTexture = await workshop.DecodeAsync(archives, glow, cancellationToken).ConfigureAwait(false);
                        var rgba = (byte[])glowTexture.Rgba.Clone();
                        RecolorMath.Apply(rgba, null, change);
                        entries[glow + "_c"] = await workshop.CompileAsync(glowTexture, rgba, settings.StagingRoot, cancellationToken).ConfigureAwait(false);
                        glowRecolored = true;
                    }
                    catch (S2ModKitException exception) when (exception.Error.Code.StartsWith("RECOLOR_", StringComparison.Ordinal))
                    {
                        AppLog.Error($"Glow texture '{glow}' kept as it is", exception);   // the material's glow colour still follows
                    }
                }

                evidence.Add(new { material = path, change, settings = settingsChanged, glowTexture = glowRecolored ? glow : null });
            }
        }

        var shineEvidence = new List<object>();
        if (sharedShines.Length > 0)
        {
            // shine lowers the roughness (alpha of the normal-roughness texture) inside the part's UV mask, as the preview showed it
            var archives = new List<string>();
            if (mod is not null)
            {
                archives.Add(mod.Files[0]);
            }

            archives.Add(Path.Combine(settings.Game, "citadel", "pak01_dir.vpk"));
            archives.Add(Path.Combine(settings.Game, "core", "pak01_dir.vpk"));
            var model = entries.TryGetValue(logical, out var built) ? built.ToArray() : ReadFirst(archives, logical)
                ?? throw Errors.Input("MODEL_EDIT_MODEL_MISSING", $"'{logical}' is not in {(mod is null ? "the game" : $"'{mod.Name}'")}.", "Choose the model the mod ships.");
            foreach (var group in sharedShines.GroupBy(part => part.Texture, StringComparer.OrdinalIgnoreCase))
            {
                var compiled = ReadFirst(archives, group.Key + "_c")
                    ?? throw Errors.Input("SHINE_TEXTURE_MISSING", $"'{group.Key}' is not in the game or the mod.", "Open the preview again.");
                try
                {
                    // the stored pixels (normal in red and green, roughness in blue for HemiOctRB), as the game's shaders read them
                    var (rgba, width, height, codec) = S2ModKit.Adapters.Source2.InPlaceTexture.ReadStored(compiled, group.Key + "_c");
                    var channel = S2ModKit.Adapters.Preview.ShineMath.RoughnessChannel(codec);
                    var whole = width * height <= 16;                              // a one-value texture: the whole material
                    foreach (var part in group)
                    {
                        S2ModKit.Adapters.Preview.ShineMath.Apply(rgba, whole ? null : S2ModKit.Adapters.Preview.PartMasks.Mask(model, logical, part.Part, width, height), part.Shine, channel);
                        shineEvidence.Add(new { part = part.Part, texture = group.Key, shine = part.Shine, codec = codec.ToString() });
                    }

                    entries[group.Key + "_c"] = S2ModKit.Adapters.Source2.InPlaceTexture.Write(compiled, group.Key + "_c", width, height, rgba);
                }
                catch (S2ModKitException exception) when (exception.Error.Code.StartsWith("RECOLOR_", StringComparison.Ordinal))
                {
                    throw Errors.Input("SHINE_TEXTURE_UNSUPPORTED", $"The shine of '{group.Key}' cannot be written: {exception.Error.Summary}", "Leave shine off for this part.");
                }
            }
        }

        var effectEvidence = new List<object>();
        if (effects.Count > 0)
        {
            // a mod's own copy of an effect wins over the game's (the archive is read again for the effects only: entries holds them too)
            var sources = new List<string>();
            if (mod is not null)
            {
                sources.Add(mod.Files[0]);
            }

            sources.Add(Path.Combine(settings.Game, "citadel", "pak01_dir.vpk"));
            var files = EffectFiles.Read(sources, effects.Keys.Where(key => !key.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase)));
            foreach (var (effect, change) in effects.Where(pair => !pair.Key.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase)).OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!files.TryGetValue(effect, out var original))
                {
                    continue;                                               // listed by the game data but not shipped: nothing to recolor
                }

                var (recolored, changedValues) = S2ModKit.Adapters.Source2.CompiledParticleEdit.Recolor(original, effect + "_c", change);
                if (changedValues > 0)
                {
                    entries[effect + "_c"] = recolored;
                    effectEvidence.Add(new { effect, target = change.Target.ToString(), exact = change.Exact, values = changedValues });
                }
            }

            // models an ability turns the hero into (Calico's cat form): their materials and colour textures take the ability's colour
            var abilityModels = effects.Where(pair => pair.Key.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (abilityModels.Length > 0)
            {
                var workshop = new S2ModKit.Adapters.Source2.VrfTextureWorkshop();
                var textureSources = sources.Append(Path.Combine(settings.Game, "core", "pak01_dir.vpk")).ToArray();
                foreach (var (abilityModel, change) in abilityModels)
                {
                    if (ReadFirst(sources, abilityModel + "_c") is not { } modelBytes)
                    {
                        continue;
                    }

                    var adjustment = new ColorAdjustment(Colorize: change.Target);
                    foreach (var material in S2ModKit.Adapters.Source2.CompiledMaterialEdit.ModelMaterials(modelBytes, abilityModel + "_c"))
                    {
                        var materialBytes = entries.TryGetValue(material + "_c", out var known) ? known.ToArray() : ReadFirst(sources, material + "_c");
                        if (materialBytes is null)
                        {
                            continue;
                        }

                        var (edited, settingsChanged, glow) = S2ModKit.Adapters.Source2.CompiledMaterialEdit.Recolor(materialBytes, material + "_c", adjustment);
                        if (settingsChanged.Count > 0)
                        {
                            entries[material + "_c"] = edited;
                        }

                        var textures = S2ModKit.Adapters.Source2.CompiledMaterialEdit.Textures(materialBytes, material + "_c");
                        var recoloredTextures = new List<string>();
                        foreach (var texture in new[] { textures.GetValueOrDefault("g_tColor"), glow }.OfType<string>().Where(path => !path.StartsWith("materials/default/", StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            if (entries.ContainsKey(texture + "_c"))
                            {
                                continue;                                      // already recolored (a part of the hero)
                            }

                            try
                            {
                                var decoded = await workshop.DecodeAsync(textureSources, texture, cancellationToken).ConfigureAwait(false);
                                var rgba = (byte[])decoded.Rgba.Clone();
                                RecolorMath.Apply(rgba, null, adjustment);
                                entries[texture + "_c"] = await workshop.CompileAsync(decoded, rgba, settings.StagingRoot, cancellationToken).ConfigureAwait(false);
                                recoloredTextures.Add(texture);
                            }
                            catch (S2ModKitException exception) when (exception.Error.Code.StartsWith("RECOLOR_", StringComparison.Ordinal))
                            {
                                AppLog.Error($"Texture '{texture}' of '{abilityModel}' kept as it is", exception);
                            }
                        }

                        effectEvidence.Add(new { model = abilityModel, material, target = change.Target.ToString(), settings = settingsChanged, textures = recoloredTextures });
                    }
                }
            }

            if (effectEvidence.Count == 0)
            {
                throw Errors.Input("EFFECT_EDIT_NOTHING", "The chosen effects have no colours that change.", "Pick another colour or other abilities.");
            }
        }

        var opacityEvidence = new List<object>();
        var own = opacities.Select(part => part.Part).Concat(ownRecolors.Keys).Concat(ownShines.Keys).Distinct().Order().ToArray();
        if (own.Length > 0)
        {
            // own copies (set_material_opacity@2, split_draw_material@1): every see-through part and every edited piece gets its own material
            // copy drawing its own copies of the textures it changes (colour: recolored, see-through alpha; normal-roughness: shine), and a
            // piece gets its own draw call. The shared material and textures stay, so the rest keeps its look and outline. Copies of colour
            // textures are written as BC3 (seconds, not a minute); a normal-roughness copy keeps its format.
            var archives = new List<string>();
            if (mod is not null)
            {
                archives.Add(mod.Files[0]);
            }

            string[] gameArchives = [Path.Combine(settings.Game, "citadel", "pak01_dir.vpk"), Path.Combine(settings.Game, "core", "pak01_dir.vpk")];
            archives.AddRange(gameArchives);
            var model = entries.TryGetValue(logical, out var built) ? built.ToArray() : ReadFirst(archives, logical)
                ?? throw Errors.Input("MODEL_EDIT_MODEL_MISSING", $"'{logical}' is not in {(mod is null ? "the game" : $"'{mod.Name}'")}.", "Choose the model the mod ships.");
            var splits = new List<S2ModKit.Adapters.Source2.SeeThroughSplit>();
            var number = 0;
            foreach (var index in own)
            {
                number++;
                var opacity = opacities.FirstOrDefault(part => part.Part == index);
                var recolor = ownRecolors.GetValueOrDefault(index);
                var shine = ownShines.GetValueOrDefault(index);
                var (_, drawPart, triangles) = S2ModKit.Adapters.Preview.PartMasks.PieceOrPart(model, logical, index);
                var material = (opacity?.Material ?? recolor?.Material ?? S2ModKit.Adapters.Source2.CompiledSeeThroughSplit.DrawMaterial(model, logical, drawPart)).Replace('\\', '/');
                material = material.EndsWith("_c", StringComparison.Ordinal) ? material[..^2] : material;
                var bytes = ReadFirst(archives, material + "_c")
                    ?? throw Errors.Input("MATERIAL_OPACITY_MISSING", $"The material '{material}' is not in the game{(mod is null ? string.Empty : " or the mod")}.", "Open the preview again and set the part again.");
                var gameMaterial = ReadFirst(gameArchives, material + "_c");
                var gameOpacity = gameMaterial is null ? null : S2ModKit.Adapters.Source2.CompiledMaterialEdit.Opacity(gameMaterial, material + "_c");
                var textures = S2ModKit.Adapters.Source2.CompiledMaterialEdit.Textures(bytes, material + "_c");
                var stem = $"_s2mk_own{number}";
                var newMaterial = material[..^".vmat".Length] + stem + ".vmat";
                var copy = bytes;
                string? newColour = null;
                string? newNormal = null;
                try
                {
                    if (opacity is not null || recolor is not null)
                    {
                        var colour = textures.GetValueOrDefault("g_tColor")
                            ?? throw Errors.Unsupported("MATERIAL_OPACITY_NO_TEXTURE", $"'{material}' has no colour texture, so this part cannot get its own colours.", "Change the whole part instead.");
                        var compiled = (entries.TryGetValue(colour + "_c", out var sharedColour) ? sharedColour.ToArray() : ReadFirst(archives, colour + "_c"))   // whole parts' recolors included
                            ?? throw Errors.Input("MATERIAL_OPACITY_MISSING", $"The texture '{colour}' is not in the game{(mod is null ? string.Empty : " or the mod")}.", "Open the preview again.");
                        var (rgba, width, height, _) = S2ModKit.Adapters.Source2.InPlaceTexture.ReadStored(compiled, colour + "_c");
                        if (recolor is not null)
                        {
                            if (recolor.Width != width || recolor.Height != height)
                            {
                                throw Errors.Verification("RECOLOR_MASK_SIZE", $"The mask of part {index + 1} is {recolor.Width}x{recolor.Height}; '{colour}' is {width}x{height}.", "Open the preview again.");
                            }

                            RecolorMath.Apply(rgba, recolor.Mask, recolor.Change);
                        }

                        if (opacity is not null)
                        {
                            var keep = Math.Clamp(opacity.Opacity, 0f, 1f);
                            for (var i = 3; i < rgba.Length; i += 4)
                            {
                                rgba[i] = gameOpacity is null ? (byte)Math.Round(keep * 255) : (byte)Math.Round(rgba[i] * keep);   // a translucent original keeps its own pattern
                            }
                        }

                        newColour = (colour.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase) ? colour[..^".vtex".Length] : colour) + stem + ".vtex";
                        entries[newColour + "_c"] = await Task.Run(() => S2ModKit.Adapters.Source2.InPlaceTexture.Write(compiled, colour + "_c", width, height, rgba, fastCopy: true), cancellationToken).ConfigureAwait(false);
                        copy = S2ModKit.Adapters.Source2.CompiledMaterialEdit.WithTexture(copy, material + "_c", newMaterial, "g_tColor", newColour);
                    }

                    if (shine is not null)
                    {
                        var normal = textures.GetValueOrDefault(S2ModKit.Adapters.Preview.PreviewSession.NormalRoughnessTexture)
                            ?? throw Errors.Input("SHINE_TEXTURE_MISSING", $"'{material}' has no roughness texture.", "Leave shine off for this part.");
                        var compiled = (entries.TryGetValue(normal + "_c", out var sharedNormal) ? sharedNormal.ToArray() : ReadFirst(archives, normal + "_c"))
                            ?? throw Errors.Input("SHINE_TEXTURE_MISSING", $"'{normal}' is not in the game or the mod.", "Open the preview again.");
                        var (rgba, width, height, codec) = S2ModKit.Adapters.Source2.InPlaceTexture.ReadStored(compiled, normal + "_c");
                        S2ModKit.Adapters.Preview.ShineMath.Apply(rgba, null, shine.Shine, S2ModKit.Adapters.Preview.ShineMath.RoughnessChannel(codec));
                        newNormal = (normal.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase) ? normal[..^".vtex".Length] : normal) + stem + ".vtex";
                        entries[newNormal + "_c"] = await Task.Run(() => S2ModKit.Adapters.Source2.InPlaceTexture.Write(compiled, normal + "_c", width, height, rgba), cancellationToken).ConfigureAwait(false);
                        copy = S2ModKit.Adapters.Source2.CompiledMaterialEdit.WithTexture(copy, material + "_c", newMaterial, S2ModKit.Adapters.Preview.PreviewSession.NormalRoughnessTexture, newNormal);
                    }
                }
                catch (S2ModKitException exception) when (exception.Error.Code.StartsWith("RECOLOR_", StringComparison.Ordinal))
                {
                    throw Errors.Input("MATERIAL_OPACITY_TEXTURE", $"A texture of '{material}' cannot be copied for part {index + 1}: {exception.Error.Summary}", "Change the whole part instead.");
                }

                if (recolor is not null)
                {
                    copy = S2ModKit.Adapters.Source2.CompiledMaterialEdit.Recolor(copy, material + "_c", recolor.Change).Bytes;   // the copy's tints and outline follow
                }

                entries[newMaterial + "_c"] = opacity is not null ? S2ModKit.Adapters.Source2.CompiledMaterialEdit.SetOpacity(copy, newMaterial + "_c", gameOpacity ?? 1f) : copy;
                splits.Add(new S2ModKit.Adapters.Source2.SeeThroughSplit(drawPart, triangles, newMaterial));
                opacityEvidence.Add(new
                {
                    part = index,
                    drawCall = drawPart,
                    piece = triangles is not null,
                    triangles = triangles?.Length,
                    material,
                    newMaterial,
                    newColour,
                    newNormal,
                    opacity = opacity?.Opacity,
                    recolor = recolor?.Change,
                    shine = shine?.Shine,
                });
            }

            entries[logical] = await Task.Run(() => S2ModKit.Adapters.Source2.CompiledSeeThroughSplit.Apply(model, logical, splits, ShapeCapabilities.CodecPath()), cancellationToken).ConfigureAwait(false);
        }

        foreach (var effect in withAbilities)
        {
            // during an ability: the effect becomes a child of one of the ability's effects (its recolored copy when there is one)
            var parent = effect.Parent! + "_c";
            var bytes = entries.TryGetValue(parent, out var shipped) ? shipped.ToArray()
                : ReadFirst([Path.Combine(settings.Game, "citadel", "pak01_dir.vpk")], parent)
                ?? throw Errors.Input("ADDED_EFFECT_MISSING", $"The ability effect '{effect.Parent}' is not in the game{(mod is null ? string.Empty : " or the mod")}.", "Choose the ability effect again; a game update may have removed it.");
            entries[parent] = S2ModKit.Adapters.Source2.CompiledAddedEffects.AddChild(bytes, parent, effect.Effect);
        }

        var changes = new List<string>();
        if (hidden.Count > 0)
        {
            changes.Add($"{hidden.Count} part{(hidden.Count == 1 ? string.Empty : "s")} hidden");
        }

        if (recolors.Count > 0)
        {
            changes.Add($"{recolors.Count} part{(recolors.Count == 1 ? string.Empty : "s")} recolored");
        }

        if (shapes.Count > 0)
        {
            changes.Add($"{shapes.Count} part{(shapes.Count == 1 ? string.Empty : "s")} reshaped");
        }

        if (shines.Count > 0)
        {
            changes.Add($"{shines.Count} part{(shines.Count == 1 ? string.Empty : "s")} shiny");
        }

        if (opacities.Count > 0)
        {
            changes.Add($"{opacities.Count} part{(opacities.Count == 1 ? string.Empty : "s")} see-through");
        }

        if (effectEvidence.Count > 0)
        {
            changes.Add($"{effectEvidence.Count} effect{(effectEvidence.Count == 1 ? string.Empty : "s")} recolored");
        }

        if (added.Count > 0)
        {
            changes.Add($"{added.Count} effect{(added.Count == 1 ? string.Empty : "s")} added");
        }

        var label = (installAs is null ? "edited: " : "edits re-applied: ") + string.Join(", ", changes);
        var name = mod?.Name ?? Hero.Catalogue.FirstOrDefault(hero => hero.Model.Equals(modelPath.Replace("_c", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))?.Name ?? Path.GetFileNameWithoutExtension(modelPath);
        var folder = Path.Combine(settings.Data, "edits", $"{Sanitize(name)}-{clock.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");   // two builds in one second (re-apply) never share a folder
        Directory.CreateDirectory(folder);
        var package = Path.Combine(folder, $"{Sanitize(name)}-edited_dir.vpk");
        await S2ModKit.Adapters.Vpk.CompactVpkArchiveIO.WriteAsync(package, entries, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(folder, "edit.json"), System.Text.Json.JsonSerializer.Serialize(
            new
            {
                operation = "app-edit@1",
                model = modelPath,
                mod = mod?.Name,
                hidden = hidden.Order().ToArray(),
                recolors = evidence,
                shapes = shapes.Select(edit => new { part = edit.Part, material = edit.Material, edit.Level, scale = new[] { edit.Shape.Scale.X, edit.Shape.Scale.Y, edit.Shape.Scale.Z }, rotationDegrees = new[] { edit.Shape.RotationDegrees.X, edit.Shape.RotationDegrees.Y, edit.Shape.RotationDegrees.Z }, move = new[] { edit.Shape.Move.X, edit.Shape.Move.Y, edit.Shape.Move.Z }, pivot = edit.Shape.Pivot.ToString() }),
                shapeOperation = shapes.Count > 0 ? "shape_part@1" : null,    // the direct reshape (vertices and bounds checked by reopening)
                shapeInputHash = shapeHashes?.Input,
                shapeOutputHash = shapeHashes?.Output,
                effects = effectEvidence,
                shines = shineEvidence,
                opacityOperation = opacities.Count > 0 ? "set_material_opacity@2" : null,
                opacities = opacityEvidence,
                addedOperation = added.Count > 0 ? "add_model_effects@1" : null,
                added = added.Select(effect => new { effect = effect.Effect, source = effect.Source, withAbilityEffect = effect.Parent, bone = effect.Bone, offset = new[] { effect.Offset.X, effect.Offset.Y, effect.Offset.Z } }),
            }, IndentedJson), cancellationToken).ConfigureAwait(false);
        new EditRecipe(EditRecipe.CurrentSchema, await GameBuildAsync(cancellationToken).ConfigureAwait(false), modelPath, mod?.Name, mod?.ModId, mod?.Key,
            [.. hidden.Order()],
            [.. recolors.Select(part => new RecipeRecolor(part.Part, part.Texture, part.Material, part.Change, part.RangeTarget, part.RangeTolerance))],
            [.. shapes.Select(edit => new RecipeShape(edit.Part, edit.Material, [edit.Shape.Scale.X, edit.Shape.Scale.Y, edit.Shape.Scale.Z],
                [edit.Shape.RotationDegrees.X, edit.Shape.RotationDegrees.Y, edit.Shape.RotationDegrees.Z], [edit.Shape.Move.X, edit.Shape.Move.Y, edit.Shape.Move.Z],
                edit.Shape.Pivot, [edit.BoundsMin.X, edit.BoundsMin.Y, edit.BoundsMin.Z], [edit.BoundsMax.X, edit.BoundsMax.Y, edit.BoundsMax.Z], edit.Displacement, edit.Level))],
            new Dictionary<string, EffectColorChange>(effects, StringComparer.Ordinal), clock.UtcNow,
            [.. shines.Select(part => new RecipeShine(part.Part, part.Texture, part.Shine))],
            [.. added.Select(effect => new RecipeAddedEffect(effect.Effect, effect.Bone, [effect.Offset.X, effect.Offset.Y, effect.Offset.Z], effect.Source, effect.Parent))],
            [.. opacities.Select(part => new RecipeOpacity(part.Part, part.Material, part.Opacity))]).Write(folder);   // for Re-apply edits after a game update
        return installAs is null
            ? await InstallVersionAsync(mod, name, package, label, cancellationToken).ConfigureAwait(false)
            : await InstallVersionAsync(installAs, installAs.Name, package, label, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Installs <paramref name="package"/>: for the base game (<paramref name="mod"/> null) as a new mod; for a mod as a version of
    /// it (the original disabled first so it cannot win, put back if the install fails, both listed in the library).
    /// </summary>
    /// <summary>A file from the first archive that has it (a mod before the game), or null.</summary>
    private static byte[]? ReadFirst(IEnumerable<string> archives, string logicalPath)
    {
        foreach (var archive in archives.Where(File.Exists))
        {
            using var package = new SteamDatabase.ValvePak.Package();
            package.Read(archive);
            if (package.FindEntry(logicalPath) is { } entry)
            {
                package.ReadEntry(entry, out var bytes);
                return bytes;
            }
        }

        return null;
    }

    private async Task<CatalogChange> InstallVersionAsync(ModEntry? mod, string name, string package, string label, CancellationToken cancellationToken)
    {
        if (mod is null)
        {
            var result = await InstallAsync(package, $"{name} ({label})", cancellationToken).ConfigureAwait(false);
            return new CatalogChange(result.Receipt is { } installedReceipt ? AddonNaming.FileKey(installedReceipt.TargetFileName) : string.Empty,
                result.Receipt is null ? result.Summary : $"Installed '{name} ({label})'. It loads like any other mod; disable or delete it to go back.");
        }

        var disabled = mod.Loaded
            ? await DisableAsync(mod, cancellationToken).ConfigureAwait(false)   // the original must not win over its edit
            : new CatalogChange(mod.Key, string.Empty);
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
        return new CatalogChange(versionKey, $"Installed '{mod.Name} ({label})'. The previous files are kept disabled as a version of the same mod.") { Receipts = [receipt] };
    }

    private static string Sanitize(string label) => new([.. label.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').Take(40)]);
}
