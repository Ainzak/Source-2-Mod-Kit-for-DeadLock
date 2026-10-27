using S2ModKit.Application;
using S2ModKit.Domain;
using SteamDatabase.ValvePak;

namespace S2ModKit.Adapters.Vpk;

/// <summary>Reads only the directory tree of an addon VPK (no payload reads, no whole-file hash verification), so a
/// full addons folder is scanned in about a second.</summary>
public sealed class ValvePakAddonTreeReader : IAddonTreeReader
{
    public Task<IReadOnlyList<string>> ReadEntryPathsAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath))
        {
            throw Errors.Input("ADDON_ARCHIVE_NOT_FOUND", $"Addon archive '{fullPath}' does not exist.", "Use an existing VPK file.");
        }

        try
        {
            using var package = new Package();
            package.Read(fullPath);
            if (package.Version is not (1 or 2))
            {
                throw Errors.Unsupported("ADDON_VPK_VERSION_UNSUPPORTED", $"Addon archive '{fullPath}' uses VPK version {package.Version}.", "Use a VPK version 1 or 2 archive.");
            }

            var paths = (package.Entries?.Values.SelectMany(group => group) ?? [])
                .Select(entry => VpkResourceCatalog.NormalizeLogicalPath(entry.GetFullPath()))
                .Order(StringComparer.Ordinal)
                .ToArray();
            return Task.FromResult<IReadOnlyList<string>>(paths);
        }
        catch (S2ModKitException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or OverflowException or ArgumentException or NotSupportedException)
        {
            throw new S2ModKitException(
                new S2Error("ADDON_VPK_INVALID", "verification", $"Addon archive '{fullPath}' could not be read: {exception.Message}", "Do not install while an addon archive is damaged or locked.", ErrorCategory.RewriteOrVerification),
                exception);
        }
    }
}
