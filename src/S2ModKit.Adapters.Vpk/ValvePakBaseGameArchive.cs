using S2ModKit.Application;
using S2ModKit.Domain;
using SteamDatabase.ValvePak;

namespace S2ModKit.Adapters.Vpk;

/// <summary>Opens the base game archive <c>citadel/pak01_dir.vpk</c> and reads only its directory tree (CRC32 and size per
/// entry; no whole-archive hash verification). Payloads are read one entry at a time on request.</summary>
public sealed class ValvePakBaseGameArchiveFactory : IBaseGameArchiveFactory
{
    public IBaseGameArchive Open(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        var path = Path.Combine(Path.GetFullPath(gameDirectory), "citadel", "pak01_dir.vpk");
        if (!File.Exists(path))
        {
            throw Errors.Input("GAME_BASE_VPK_NOT_FOUND", $"Base archive '{path}' does not exist.", "Pass the Deadlock 'game' folder (the one that contains 'citadel').");
        }

        Package? package = null;
        try
        {
            package = new Package();
            package.Read(path);
            if (package.Version is not (1 or 2))
            {
                throw Errors.Unsupported("GAME_BASE_VPK_VERSION_UNSUPPORTED", $"Base archive '{path}' uses VPK version {package.Version}.", "Use a VPK version 1 or 2 archive.");
            }

            var entries = new Dictionary<string, PackageEntry>(StringComparer.Ordinal);
            foreach (var entry in package.Entries?.Values.SelectMany(group => group) ?? [])
            {
                var logicalPath = VpkResourceCatalog.NormalizeLogicalPath(entry.GetFullPath());
                if (!entries.TryAdd(logicalPath, entry))
                {
                    throw Errors.Input("VPK_DUPLICATE_LOGICAL_PATH", $"Base archive '{path}' contains duplicate logical path '{logicalPath}'.", "Use an archive with one entry per normalized logical path.");
                }
            }

            return new Archive(package, entries);
        }
        catch (S2ModKitException)
        {
            package?.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or OverflowException or ArgumentException or NotSupportedException)
        {
            package?.Dispose();
            throw new S2ModKitException(
                new S2Error("GAME_BASE_VPK_INVALID", "verification", $"Base archive '{path}' could not be read: {exception.Message}", "Verify the game files in Steam, then retry.", ErrorCategory.RewriteOrVerification),
                exception);
        }
    }

    private sealed class Archive(Package package, Dictionary<string, PackageEntry> entries) : IBaseGameArchive
    {
        private readonly IReadOnlyDictionary<string, BaseFileInfo> infos = entries.ToDictionary(
            pair => pair.Key,
            pair => new BaseFileInfo(pair.Value.CRC32, pair.Value.TotalLength),
            StringComparer.Ordinal);

        public IReadOnlyDictionary<string, BaseFileInfo> Entries => infos;

        public byte[] ReadPayload(string logicalPath)
        {
            if (!entries.TryGetValue(logicalPath, out var entry))
            {
                throw Errors.Input("GAME_BASE_ENTRY_NOT_FOUND", $"Base archive has no entry '{logicalPath}'.", "Read only paths listed in Entries.");
            }

            try
            {
                package.ReadEntry(entry, out var bytes, validateCrc: true);
                return bytes;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException)
            {
                throw new S2ModKitException(
                    new S2Error("GAME_BASE_ENTRY_UNREADABLE", "verification", $"Base entry '{logicalPath}' could not be read or failed its CRC32 check: {exception.Message}", "Verify the game files in Steam, then retry.", ErrorCategory.RewriteOrVerification),
                    exception);
            }
        }

        public void Dispose() => package.Dispose();
    }
}
