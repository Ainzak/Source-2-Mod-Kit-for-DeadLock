using S2ModKit.Adapters.Vpk;
using S2ModKit.Application;
using S2ModKit.Infrastructure;

namespace S2ModKit.Desktop;

/// <summary>Who may change a mod from the app: only what the app installed; Mod Manager mods stay read-only.</summary>
internal enum ModSource
{
    App,
    Manager,
    Other,
}

/// <summary>An app installation: its receipts folder and latest receipt (status installed or rolled_back).</summary>
internal sealed record AppInstall(string ReceiptsRoot, AddonFileInstallReceipt Receipt);

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
    IReadOnlyList<string>? Heroes = null)
{
    /// <summary>Hero display names the mod changes (from the hero catalogue); empty for sound, UI and other mods.</summary>
    public IReadOnlyList<string> HeroNames => Heroes ?? [];

    /// <summary>Model, sound, UI, particles or other: the mod's main kind for filtering.</summary>
    public string MainKind => Models.Count > 0 ? "model" : Kinds.Count == 0 ? "other" : Kinds.MaxBy(pair => pair.Value).Key;

    public string KindText => Kinds.Count == 0 ? "-" : string.Join(", ", Kinds.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key} {pair.Value}"));

    public string StatusText => (Source, Loaded) switch
    {
        (ModSource.App, true) => "installed by S2ModKit",
        (ModSource.App, false) => "disabled (S2ModKit)",
        (ModSource.Manager, true) => "Mod Manager, enabled",
        (ModSource.Manager, false) => "Mod Manager, disabled",
        (_, true) => "loaded",
        _ => "not loaded",
    };
}

/// <summary>
/// The addons folder as a list of mods (read through <see cref="AddonFileApplication"/>'s scan), which hero model each
/// mod shows on, and installs, disables and re-enables of the app's own installations over the safe installer.
/// <paramref name="managerState"/>: Deadlock Mod Manager state file (default: the installed manager's, if any).
/// </summary>
internal sealed class ModCatalog(AppSettings settings, string? managerState = null)
{
    private readonly string? managerState = managerState ?? AppSettings.ManagerState;
    private readonly AddonFileStore store = new();
    private readonly ValvePakAddonTreeReader tree = new();
    private IReadOnlyList<string>? gameModels;

    private AddonFileApplication Files => new AddonFileApplication(store, tree, new ModManagerStateReader(), new SystemClock(), new GameBuildReader());

    public async Task<IReadOnlyList<ModEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(settings.Data);
        var scan = await Files.ScanAsync(settings.AddonsRoot, managerState, settings.TreeCache, cancellationToken).ConfigureAwait(false);
        var installs = await ReadInstallsAsync(cancellationToken).ConfigureAwait(false);
        var byTarget = installs.Where(install => install.Receipt.Status == "installed")
            .ToDictionary(install => install.Receipt.TargetFileName, StringComparer.OrdinalIgnoreCase);

        var mods = new List<ModEntry>();
        foreach (var group in scan.Archives.GroupBy(archive => archive.ModId ?? "file:" + archive.FileName))
        {
            var archives = group.OrderBy(archive => archive.Slot ?? int.MaxValue).ThenBy(archive => archive.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
            var first = archives[0];
            var install = archives.Select(archive => byTarget.GetValueOrDefault(archive.FileName)).FirstOrDefault(found => found is not null);
            var source = install is not null ? ModSource.App : first.ModId is not null ? ModSource.Manager : ModSource.Other;
            var files = archives.Select(archive => Path.Combine(settings.AddonsRoot, archive.FileName)).ToArray();
            var entries = await ReadEntriesAsync(files, cancellationToken).ConfigureAwait(false);
            var kinds = archives.SelectMany(archive => archive.ResourceKinds).GroupBy(pair => pair.Key).ToDictionary(g => g.Key, g => g.Sum(pair => pair.Value));
            var name = install?.Receipt.Label ?? first.ModName ?? Path.GetFileNameWithoutExtension(first.FileName);
            var heroes = HeroesOf(entries);
            mods.Add(new ModEntry(group.Key, name, source, archives.Any(archive => archive.Loaded), files, archives.Select(archive => archive.Slot).ToArray(),
                kinds, entries, await ResolveModelsAsync(entries, heroes, cancellationToken).ConfigureAwait(false), install, [.. heroes.Select(hero => hero.Name)]));
        }

        foreach (var disabled in installs.Where(install => install.Receipt.Status == "rolled_back" && File.Exists(install.Receipt.PackagePath))
                     .GroupBy(install => install.Receipt.PackagePath, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()))
        {
            if (installs.Any(other => other.Receipt.Status == "installed" && other.Receipt.PackagePath.Equals(disabled.Receipt.PackagePath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;                                                    // enabled again later
            }

            var entries = await ReadEntriesAsync([disabled.Receipt.PackagePath], cancellationToken).ConfigureAwait(false);
            var heroes = HeroesOf(entries);
            mods.Add(new ModEntry("app:" + disabled.Receipt.PackagePath, disabled.Receipt.Label, ModSource.App, false, [disabled.Receipt.PackagePath], [null],
                new Dictionary<string, int>(), entries, await ResolveModelsAsync(entries, heroes, cancellationToken).ConfigureAwait(false), disabled,
                [.. heroes.Select(hero => hero.Name)]));
        }

        return [.. mods.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)];   // by name: a mod keeps its place when switched
    }

    /// <summary>Installs a finished VPK into a free slot; a new receipts folder per installation.</summary>
    public async Task<AddonFileResult> InstallAsync(string package, string label, CancellationToken cancellationToken = default)
    {
        var receipts = Path.Combine(settings.InstallsRoot, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Sanitize(label)}");
        return await Files.InstallAsync(new AddonFileInstallRequest(settings.AddonsRoot, package, "auto", receipts, label, managerState, settings.TreeCache),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes the file the app installed (rollback); the package stays where it is for re-enabling.</summary>
    public Task<AddonFileResult> DisableAsync(AppInstall install, CancellationToken cancellationToken = default) =>
        Files.RollbackAsync(settings.AddonsRoot, install.ReceiptsRoot, cancellationToken);

    public Task<AddonFileResult> EnableAsync(AppInstall install, CancellationToken cancellationToken = default) =>
        InstallAsync(install.Receipt.PackagePath, install.Receipt.Label, cancellationToken);

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
                installs.Add(new AppInstall(folder, latest));
            }
        }

        return installs;
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

    /// <summary>Heroes whose folder (catalogue id, alias or main-model folder, a _v2-style suffix ignored) the mod's model paths use.</summary>
    private static List<Hero> HeroesOf(IReadOnlyList<string> entries)
    {
        var folders = entries.Where(path => path.StartsWith("models/heroes", StringComparison.OrdinalIgnoreCase))
            .Select(path => path.Split('/')).Where(parts => parts.Length > 3).Select(parts => Hero.StripVersion(parts[2]))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. Hero.Catalogue.Where(hero => hero.Folders.Any(folders.Contains))];
    }

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

    private static string Sanitize(string label) => new([.. label.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').Take(40)]);
}
