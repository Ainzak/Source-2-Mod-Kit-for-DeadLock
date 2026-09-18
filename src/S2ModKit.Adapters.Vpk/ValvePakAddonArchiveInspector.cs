using S2ModKit.Application;
using S2ModKit.Domain;
using SteamDatabase.ValvePak;

namespace S2ModKit.Adapters.Vpk;

public sealed class ValvePakAddonArchiveInspector : IAddonArchiveInspector
{
    public Task<AddonArchiveProbe> ProbeAsync(
        string archivePath,
        string logicalPath,
        bool readMatchingEntry,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath))
        {
            throw Errors.Input("ADDON_ARCHIVE_NOT_FOUND", $"Addon archive '{fullPath}' does not exist.", "Refresh inventory and use an existing active VPK.");
        }

        var normalizedTarget = VpkResourceCatalog.NormalizeLogicalPath(logicalPath);
        try
        {
            using var package = new Package();
            package.Read(fullPath);
            if (package.Version is not (1 or 2))
            {
                throw Errors.Unsupported("ADDON_VPK_VERSION_UNSUPPORTED", $"Addon archive '{fullPath}' uses unsupported VPK version {package.Version}.", "Use a Source-compatible VPK version 1 or 2 archive.");
            }

            if (package.Version == 2)
            {
                package.VerifyHashes();
            }

            var entries = package.Entries?.Values.SelectMany(group => group).ToArray()
                ?? throw Errors.Input("ADDON_VPK_TREE_MISSING", $"Addon archive '{fullPath}' has no directory tree.", "Use an intact VPK archive.");
            PackageEntry? match = null;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = VpkResourceCatalog.NormalizeLogicalPath(entry.GetFullPath());
                if (string.Equals(normalized, normalizedTarget, StringComparison.Ordinal))
                {
                    if (match is not null)
                    {
                        throw Errors.Verification("ADDON_VPK_DUPLICATE_TARGET_PATH", $"Addon archive '{fullPath}' contains the candidate path '{normalizedTarget}' more than once.", "Remove or rebuild the ambiguous archive before installation.");
                    }

                    match = entry;
                }
            }

            if (match is null)
            {
                return Task.FromResult(new AddonArchiveProbe(fullPath, entries.Length, false, null, null, null));
            }

            ContentHash? hash = null;
            if (readMatchingEntry)
            {
                package.ReadEntry(match, out var bytes, validateCrc: true);
                hash = ContentHash.Compute(bytes);
            }

            return Task.FromResult(new AddonArchiveProbe(
                fullPath,
                entries.Length,
                true,
                hash,
                match.CRC32,
                match.TotalLength));
        }
        catch (S2ModKitException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or OverflowException or ArgumentException)
        {
            throw new S2ModKitException(
                new S2Error(
                    "ADDON_VPK_INVALID",
                    "verification",
                    $"Addon archive '{fullPath}' could not be inspected: {exception.Message}",
                    "Do not install while an active archive is corrupt, incomplete, or locked.",
                    ErrorCategory.RewriteOrVerification),
                exception);
        }
    }
}
