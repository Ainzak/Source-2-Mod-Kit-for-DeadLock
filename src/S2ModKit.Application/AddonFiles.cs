using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed record AddonFileInfo(string FileName, string FullPath, long Size, long LastWriteUtcTicks, bool IsReparsePoint);

public sealed record ManagedModInfo(string ModId, string? Name, bool Enabled);

/// <summary>Read-only view of Deadlock Mod Manager records: which addon file belongs to which mod, and every
/// <c>pakNN_dir.vpk</c> name a record uses (loaded or not), which S2ModKit must never take as a slot.</summary>
public sealed record ModManagerView(
    IReadOnlyDictionary<string, ManagedModInfo> ModsByFileName,
    IReadOnlySet<string> ReservedSlotNames,
    bool ManagerStateFound);

public sealed record AddonArchiveSummary(
    string FileName,
    long Size,
    bool Loaded,
    int? Slot,
    string? ModId,
    string? ModName,
    int EntryCount,
    IReadOnlyDictionary<string, int> ResourceKinds);

public sealed record AddonScan(
    string AddonsRoot,
    bool ManagerStateFound,
    int CachedArchives,
    IReadOnlyList<AddonArchiveSummary> Archives);

public sealed record AddonFileCollision(string FileName, string ModName, IReadOnlyList<string> Paths, bool IdenticalToPackage);

public sealed record AddonFileInstallReceipt
{
    public string Operation { get; init; } = "addon_file_install@1";

    public string Id { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string AddonsRoot { get; init; } = string.Empty;

    public string TargetFileName { get; init; } = string.Empty;

    public string StagingFileName { get; init; } = string.Empty;

    public string PackagePath { get; init; } = string.Empty;

    public ContentHash? PackageHash { get; init; }

    public int EntryCount { get; init; }

    /// <summary>Game build (<c>ClientVersion</c> in <c>steam.inf</c>) the package was installed on; null when the addons
    /// folder is not inside a game folder.</summary>
    public int? GameBuild { get; init; }

    public string Status { get; init; } = "prepared";

    public string? Note { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }
}

public sealed record AddonFileResult(string Status, string Summary, AddonFileInstallReceipt? Receipt);

public interface IAddonTreeReader
{
    /// <summary>Logical entry paths from the archive directory tree only; payloads are not read or hashed.</summary>
    Task<IReadOnlyList<string>> ReadEntryPathsAsync(string archivePath, CancellationToken cancellationToken = default);
}

public interface IModManagerStateReader
{
    Task<ModManagerView> ReadAsync(string addonsRoot, string? managerStatePath, CancellationToken cancellationToken = default);
}

public interface IAddonFileStore
{
    Task<IReadOnlyList<AddonFileInfo>> ListArchivesAsync(string addonsRoot, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, AddonTreeCacheEntry>> LoadTreeCacheAsync(string? cachePath, CancellationToken cancellationToken = default);

    Task SaveTreeCacheAsync(string? cachePath, IReadOnlyDictionary<string, AddonTreeCacheEntry> entries, CancellationToken cancellationToken = default);

    Task<ContentHash> HashFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Copies the package to a new staging file while hashing it, then renames it to the target; returns the hash.</summary>
    Task<ContentHash> CopyHashedAndActivateAsync(string sourcePath, string addonsRoot, string stagingFileName, string targetFileName, CancellationToken cancellationToken = default);

    Task DeleteOwnedAsync(string addonsRoot, string fileName, ContentHash expectedHash, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AddonFileInstallReceipt>> ListReceiptsAsync(string receiptsRoot, CancellationToken cancellationToken = default);

    Task WriteReceiptAsync(string receiptsRoot, AddonFileInstallReceipt receipt, bool createNew, CancellationToken cancellationToken = default);
}

public sealed record AddonTreeCacheEntry(long Size, long LastWriteUtcTicks, IReadOnlyList<string> Paths);

public interface IAddonFileApplication
{
    Task<AddonScan> ScanAsync(string addonsRoot, string? managerStatePath, string? cachePath, CancellationToken cancellationToken = default);

    Task<AddonFileResult> InstallAsync(AddonFileInstallRequest request, CancellationToken cancellationToken = default);

    Task<AddonFileResult> RollbackAsync(string addonsRoot, string receiptsRoot, CancellationToken cancellationToken = default);
}

public sealed record AddonFileInstallRequest(
    string AddonsRoot,
    string PackagePath,
    string Slot,
    string ReceiptsRoot,
    string Label,
    string? ManagerStatePath,
    string? CachePath);

/// <summary>Installs one finished VPK (from any build route) into a free addon slot and removes it again. It never
/// renames, moves or deletes a file it did not install and never edits mod-manager state.</summary>
public sealed class AddonFileApplication(
    IAddonFileStore store,
    IAddonTreeReader treeReader,
    IModManagerStateReader managerReader,
    IClock clock,
    IGameBuildReader? gameBuildReader = null)
    : IAddonFileApplication
{
    private readonly AddonTreeLoader treeLoader = new(store, treeReader);

    public async Task<AddonScan> ScanAsync(string addonsRoot, string? managerStatePath, string? cachePath, CancellationToken cancellationToken = default)
    {
        var manager = await managerReader.ReadAsync(addonsRoot, managerStatePath, cancellationToken).ConfigureAwait(false);
        var (trees, cached) = await treeLoader.ReadAsync(addonsRoot, cachePath, loadedOnly: false, cancellationToken).ConfigureAwait(false);
        var archives = trees.Select(tree =>
        {
            manager.ModsByFileName.TryGetValue(tree.File.FileName, out var mod);
            return new AddonArchiveSummary(
                tree.File.FileName,
                tree.File.Size,
                IsLoadedName(tree.File.FileName),
                TryParseSlot(tree.File.FileName),
                mod?.ModId,
                mod?.Name,
                tree.Paths.Count,
                AddonResourceKinds.Count(tree.Paths));
        }).ToArray();
        return new AddonScan(Path.GetFullPath(addonsRoot), manager.ManagerStateFound, cached, archives);
    }

    public async Task<AddonFileResult> InstallAsync(AddonFileInstallRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var label = string.IsNullOrWhiteSpace(request.Label) ? "test" : request.Label.Trim();
        var packagePath = Path.GetFullPath(request.PackagePath);
        var packagePaths = await treeReader.ReadEntryPathsAsync(packagePath, cancellationToken).ConfigureAwait(false);
        if (packagePaths.Count == 0)
        {
            throw Errors.Input("ADDON_PACKAGE_EMPTY", $"Package '{packagePath}' has no entries.", "Rebuild the package before installing.");
        }

        var receipts = await store.ListReceiptsAsync(request.ReceiptsRoot, cancellationToken).ConfigureAwait(false);
        var active = receipts.Where(item => item.Status is "installed" or "prepared").ToArray();
        if (active.Length > 1 || active.Any(item => item.Status == "prepared"))
        {
            throw Errors.Verification("ADDON_FILE_RECEIPT_UNRESOLVED", "An earlier install did not finish cleanly.", "Run rollback for this receipts folder first; it removes only the file this tool installed.");
        }

        var packageHash = await store.HashFileAsync(packagePath, cancellationToken).ConfigureAwait(false);
        if (active.Length == 1)
        {
            if (active[0].PackageHash == packageHash)
            {
                return new AddonFileResult("unchanged", $"This {label} package is already installed as {active[0].TargetFileName}. No files changed.", active[0]);
            }

            throw Errors.Verification("ADDON_FILE_OTHER_PACKAGE_INSTALLED", $"Another {label} package is installed as {active[0].TargetFileName}.", "Run rollback first, then install again.");
        }

        var manager = await managerReader.ReadAsync(request.AddonsRoot, request.ManagerStatePath, cancellationToken).ConfigureAwait(false);
        var (trees, _) = await treeLoader.ReadAsync(request.AddonsRoot, request.CachePath, loadedOnly: true, cancellationToken).ConfigureAwait(false);
        var wanted = new HashSet<string>(packagePaths, StringComparer.Ordinal);
        var collisions = new List<AddonFileCollision>();
        foreach (var tree in trees)
        {
            var hits = tree.Paths.Where(wanted.Contains).Order(StringComparer.Ordinal).ToArray();
            if (hits.Length == 0)
            {
                continue;
            }

            var identical = await store.HashFileAsync(tree.File.FullPath, cancellationToken).ConfigureAwait(false) == packageHash;
            var name = manager.ModsByFileName.TryGetValue(tree.File.FileName, out var mod) && !string.IsNullOrWhiteSpace(mod.Name)
                ? mod.Name!
                : "not managed by Deadlock Mod Manager";
            collisions.Add(new AddonFileCollision(tree.File.FileName, name, hits, identical));
        }

        if (collisions.Count > 0)
        {
            var lines = collisions.Select(item => item.IdenticalToPackage
                ? $"{item.FileName}: an identical copy of this package, installed outside S2ModKit; remove it"
                : $"{item.ModName} ({item.FileName}, {item.Paths.Count} shared file(s))");
            throw Errors.Verification(
                "ADDON_FILE_COLLISION",
                $"These enabled mods change the same files as the {label} package: {string.Join("; ", lines)}.",
                "Turn them off in Deadlock Mod Manager, then install again. No game files changed.");
        }

        var gameBuild = gameBuildReader is null ? null : await gameBuildReader.TryReadForAddonsRootAsync(request.AddonsRoot, cancellationToken).ConfigureAwait(false);
        var existing = await store.ListArchivesAsync(request.AddonsRoot, cancellationToken).ConfigureAwait(false);
        var occupied = existing.Select(item => item.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var slot = SelectSlot(request.Slot, occupied, manager.ReservedSlotNames);
        var target = $"pak{slot:00}_dir.vpk";
        var now = clock.UtcNow.ToUniversalTime();
        var id = Guid.NewGuid().ToString("N");
        var receipt = new AddonFileInstallReceipt
        {
            Id = id,
            Label = label,
            AddonsRoot = Path.GetFullPath(request.AddonsRoot),
            TargetFileName = target,
            StagingFileName = $".{target}.{id}.s2modkit-staging",
            PackagePath = packagePath,
            PackageHash = packageHash,
            EntryCount = packagePaths.Count,
            GameBuild = gameBuild?.Build,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        await store.WriteReceiptAsync(request.ReceiptsRoot, receipt, createNew: true, cancellationToken).ConfigureAwait(false);
        ContentHash copied;
        try
        {
            copied = await store.CopyHashedAndActivateAsync(packagePath, request.AddonsRoot, receipt.StagingFileName, target, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await store.WriteReceiptAsync(request.ReceiptsRoot, receipt with { Status = "failed_nothing_installed", UpdatedUtc = clock.UtcNow.ToUniversalTime() }, createNew: false, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (copied != packageHash)
        {
            await store.DeleteOwnedAsync(request.AddonsRoot, target, copied, CancellationToken.None).ConfigureAwait(false);
            await store.WriteReceiptAsync(request.ReceiptsRoot, receipt with { Status = "failed_nothing_installed", UpdatedUtc = clock.UtcNow.ToUniversalTime(), Note = "package changed while it was copied" }, createNew: false, CancellationToken.None).ConfigureAwait(false);
            throw Errors.Verification("ADDON_FILE_PACKAGE_CHANGED", "The package changed while it was being copied.", "Rebuild or stop writing the package, then install again.");
        }

        var installed = receipt with { Status = "installed", UpdatedUtc = clock.UtcNow.ToUniversalTime() };
        await store.WriteReceiptAsync(request.ReceiptsRoot, installed, createNew: false, cancellationToken).ConfigureAwait(false);
        return new AddonFileResult("installed", $"Installed the {label} package as {target}. Other mods were not touched. Restart Deadlock.", installed);
    }

    public async Task<AddonFileResult> RollbackAsync(string addonsRoot, string receiptsRoot, CancellationToken cancellationToken = default)
    {
        var receipts = await store.ListReceiptsAsync(receiptsRoot, cancellationToken).ConfigureAwait(false);
        var active = receipts.Where(item => item.Status is "installed" or "prepared").ToArray();
        if (active.Length == 0)
        {
            return new AddonFileResult("unchanged", "Nothing from this receipts folder is installed. No files changed.", null);
        }

        if (active.Length > 1)
        {
            throw Errors.Verification("ADDON_FILE_RECEIPTS_AMBIGUOUS", "More than one receipt in this folder claims an installation.", "Inspect the receipts; S2ModKit will not guess which file is its own.");
        }

        var receipt = active[0];
        if (!string.Equals(Path.GetFullPath(addonsRoot), receipt.AddonsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw Errors.Verification("ADDON_ROOT_RECEIPT_MISMATCH", "The supplied addons folder differs from the receipt.", "Use the addons folder recorded by install.");
        }

        var files = await store.ListArchivesAsync(addonsRoot, cancellationToken).ConfigureAwait(false);
        var target = files.FirstOrDefault(item => string.Equals(item.FileName, receipt.TargetFileName, StringComparison.OrdinalIgnoreCase));
        string summary;
        if (target is null)
        {
            receipt = receipt with { Note = "installed file already absent at rollback" };
            summary = $"The {receipt.Label} file {receipt.TargetFileName} was already gone. Record closed; no files changed.";
        }
        else
        {
            if (receipt.PackageHash is not { } expected)
            {
                throw Errors.Verification("ADDON_FILE_RECEIPT_INVALID", "The receipt has no package hash.", "Inspect the receipt; S2ModKit will not remove an unverified file.");
            }

            await store.DeleteOwnedAsync(addonsRoot, receipt.TargetFileName, expected, cancellationToken).ConfigureAwait(false);
            summary = $"Removed the {receipt.Label} package ({receipt.TargetFileName}). Other mods were not touched. Restart Deadlock.";
        }

        var closed = receipt with { Status = "rolled_back", UpdatedUtc = clock.UtcNow.ToUniversalTime() };
        await store.WriteReceiptAsync(receiptsRoot, closed, createNew: false, cancellationToken).ConfigureAwait(false);
        return new AddonFileResult("rolled_back", summary, closed);
    }

    private static int SelectSlot(string requested, HashSet<string> occupied, IReadOnlySet<string> reserved)
    {
        bool Free(int slot)
        {
            var name = $"pak{slot:00}_dir.vpk";
            return !occupied.Contains(name) && !reserved.Contains(name);
        }

        if (string.Equals(requested, "auto", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var slot in Enumerable.Range(90, 10).OrderDescending())
            {
                if (Free(slot))
                {
                    return slot;
                }
            }

            throw Errors.Verification("ADDON_NO_FREE_SLOT", "No slot from 90 to 99 is free of files and mod-manager records.", "Choose an explicit free slot with --slot NN.");
        }

        if (requested.Length != 2 || !int.TryParse(requested, out var explicitSlot))
        {
            throw Errors.InvalidRecipe("ADDON_SLOT_INVALID", $"Slot '{requested}' is not 'auto' or a two-digit number.", "Use --slot auto or --slot NN.");
        }

        var name = $"pak{explicitSlot:00}_dir.vpk";
        if (occupied.Contains(name))
        {
            throw Errors.Verification("ADDON_SLOT_OCCUPIED", $"Slot {name} is occupied.", "Choose an empty slot; S2ModKit never overwrites an existing file.");
        }

        if (reserved.Contains(name))
        {
            throw Errors.Verification("ADDON_SLOT_RESERVED", $"Slot {name} is used by a Deadlock Mod Manager record.", "Choose another slot so the mod manager keeps track of its own mods.");
        }

        return explicitSlot;
    }

    internal static bool IsLoadedName(string fileName) => TryParseSlot(fileName) is not null;

    internal static int? TryParseSlot(string fileName) =>
        fileName.Length == 13
        && fileName.StartsWith("pak", StringComparison.OrdinalIgnoreCase)
        && fileName.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase)
        && char.IsAsciiDigit(fileName[3])
        && char.IsAsciiDigit(fileName[4])
            ? int.Parse(fileName.AsSpan(3, 2), System.Globalization.CultureInfo.InvariantCulture)
            : null;
}

/// <summary>Resource kinds used to decide whether two mods really conflict: a sound mod and a model mod for the same
/// hero touch different kinds and coexist.</summary>
public static class AddonResourceKinds
{
    public static string Classify(string logicalPath)
    {
        var path = logicalPath.ToLowerInvariant();
        var extension = Path.GetExtension(path);
        return extension switch
        {
            ".vmdl_c" or ".vmesh_c" or ".vnmskel_c" or ".vphys_c" or ".vmorf_c" => "model",
            ".vanim_c" or ".vanmgrph_c" or ".vnmgraph_c" or ".vagrp_c" or ".vseq_c" or ".vnmclip_c" => "animation",
            ".vmat_c" or ".vtex_c" or ".vcompmat_c" => path.StartsWith("panorama/", StringComparison.Ordinal) ? "ui" : "material",
            ".vpcf_c" or ".vsnap_c" => "particles",
            ".vsnd_c" or ".vsndevts_c" or ".vsndstck_c" or ".vmix_c" => "sound",
            ".vxml_c" or ".vcss_c" or ".vjs_c" or ".vts_c" or ".vsvg_c" or ".vpdi_c" => "ui",
            ".txt" when path.StartsWith("resource/localization/", StringComparison.Ordinal) => "localization",
            ".vdata_c" or ".vpulse_c" or ".vrr_c" => "data",
            _ when path.StartsWith("panorama/", StringComparison.Ordinal) => "ui",
            _ when path.StartsWith("sounds/", StringComparison.Ordinal) => "sound",
            _ => "other",
        };
    }

    public static IReadOnlyDictionary<string, int> Count(IEnumerable<string> paths) =>
        paths.GroupBy(Classify).OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
}
