using S2ModKit.Application;
using S2ModKit.Domain;
using SteamDatabase.ValvePak;

namespace S2ModKit.Adapters.Vpk;

/// <summary>Reads single entries (CRC-checked) from addon archives and writes merged archives to new paths only.</summary>
public sealed class ValvePakAddonArchiveContent : IAddonArchiveContentReader, IAddonArchiveWriter
{
    public Task<byte[]> ReadEntryAsync(string archivePath, string logicalPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(archivePath);
        var target = VpkResourceCatalog.NormalizeLogicalPath(logicalPath);
        try
        {
            using var package = new Package();
            package.Read(fullPath);
            var entry = (package.Entries?.Values.SelectMany(group => group) ?? [])
                .FirstOrDefault(item => string.Equals(VpkResourceCatalog.NormalizeLogicalPath(item.GetFullPath()), target, StringComparison.Ordinal))
                ?? throw Errors.Input("ADDON_ENTRY_NOT_FOUND", $"'{logicalPath}' is not in '{fullPath}'.", "Refresh the archive listing.");
            package.ReadEntry(entry, out var bytes, validateCrc: true);
            return Task.FromResult(bytes);
        }
        catch (S2ModKitException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or ArgumentException or NotSupportedException)
        {
            throw new S2ModKitException(
                new S2Error("ADDON_VPK_INVALID", "verification", $"'{logicalPath}' could not be read from '{fullPath}': {exception.Message}", "Do not merge a damaged or locked archive.", ErrorCategory.RewriteOrVerification),
                exception);
        }
    }

    public async Task WriteNewAsync(string outputPath, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> entries, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(outputPath);
        if (File.Exists(full) || Directory.Exists(full))
        {
            throw Errors.Verification("ADDON_MERGE_OUTPUT_EXISTS", $"'{full}' already exists.", "Choose a new output path.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await CompactVpkArchiveIO.WriteAsync(full, entries, cancellationToken).ConfigureAwait(false);   // partial file + no-overwrite move
    }
}
