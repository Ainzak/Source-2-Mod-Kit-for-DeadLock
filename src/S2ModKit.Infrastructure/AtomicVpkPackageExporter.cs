using System.Security.Cryptography;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Infrastructure;

public sealed class AtomicVpkPackageExporter : IVpkPackageExporter
{
    private const int BufferSize = 1024 * 1024;

    public async Task<VpkPackageExportResult> ExportAsync(
        string packageId,
        string sourcePath,
        ContentHash expectedContentHash,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        var source = Path.GetFullPath(sourcePath);
        var output = Path.GetFullPath(outputPath);
        if (!File.Exists(source) || Directory.Exists(output) || File.Exists(output))
        {
            throw Errors.Input(
                "VPK_EXPORT_PATH_INVALID",
                "The package export source must exist and the destination must be a new file.",
                "Choose a new .vpk output path in a writable directory.");
        }

        if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
        {
            throw Errors.Input(
                "VPK_EXPORT_SOURCE_DESTINATION_MATCH",
                "The package export destination is the published package itself.",
                "Choose a different new .vpk output path.");
        }

        var parent = Path.GetDirectoryName(output);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            throw Errors.Input(
                "VPK_EXPORT_DIRECTORY_NOT_FOUND",
                "The package export directory does not exist.",
                "Create the destination directory and retry.");
        }

        var temporary = Path.Combine(parent, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.s2modkit-export");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var candidate = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(candidate, BufferSize, cancellationToken).ConfigureAwait(false);
                await candidate.FlushAsync(cancellationToken).ConfigureAwait(false);
                candidate.Flush(flushToDisk: true);
            }

            ContentHash hash;
            await using (var verification = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                hash = new ContentHash(Convert.ToHexStringLower(await SHA256.HashDataAsync(verification, cancellationToken).ConfigureAwait(false)));
            }

            if (hash != expectedContentHash)
            {
                throw Errors.Verification(
                    "VPK_EXPORT_HASH_MISMATCH",
                    "The exported package does not match the verified published package.",
                    "Do not use the export; preserve diagnostics and retry from the verified package.");
            }

            var size = new FileInfo(temporary).Length;
            File.Move(temporary, output, overwrite: false);
            return new VpkPackageExportResult(packageId, output, hash, size);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
