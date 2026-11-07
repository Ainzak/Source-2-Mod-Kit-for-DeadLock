using S2ModKit.Application;
using S2ModKit.Domain;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace S2ModKit.Infrastructure;

/// <summary>
/// Lists and extracts the <c>.vpk</c> entries of zip, 7z and rar archives (SharpCompress). Entry names are never used as
/// paths: the caller names each target file, and every target must lie inside the staging folder. An archive with a
/// traversal entry (<c>..</c>, a rooted path, a drive) is refused as a whole, and extraction stops at a size budget
/// counted from the bytes actually written, not from the sizes the archive declares.
/// </summary>
public sealed class ArchivePackageReader : IArchivePackageReader
{
    public Task<IReadOnlyList<ArchiveVpkEntry>> ListVpksAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        using var archive = Open(archivePath);
        var found = new List<ArchiveVpkEntry>();
        var count = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > ArchiveLimits.MaxEntries)
            {
                throw Errors.Input("ARCHIVE_TOO_MANY_ENTRIES", $"The archive has more than {ArchiveLimits.MaxEntries} entries.", "Install a smaller archive, or unpack it and install the .vpk file.");
            }

            var key = entry.Key ?? string.Empty;
            RequireSafeEntryName(key);
            if (!entry.IsDirectory && key.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(new ArchiveVpkEntry(key.Replace('\\', '/'), entry.Size));
            }
        }

        return Task.FromResult<IReadOnlyList<ArchiveVpkEntry>>(found);
    }

    public async Task ExtractAsync(string archivePath, IReadOnlyList<ArchiveExtraction> extractions, string stagingRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extractions);
        var root = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var extraction in extractions)
        {
            var target = Path.GetFullPath(extraction.TargetPath);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw Errors.Input("ARCHIVE_TARGET_ESCAPE", $"'{extraction.TargetPath}' is outside the staging folder.", "Extract only into the staging folder.");
            }

            wanted[extraction.EntryPath] = target;
        }

        using var archive = Open(archivePath);
        var written = 0L;
        var done = 0;
        if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)         // these can only be read in order
        {
            using var reader = archive.ExtractAllEntries();
            while (done < wanted.Count && await reader.MoveToNextEntryAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = (reader.Entry.Key ?? string.Empty).Replace('\\', '/');
                RequireSafeEntryName(key);
                if (!reader.Entry.IsDirectory && wanted.TryGetValue(key, out var target))
                {
                    await using var input = await reader.OpenEntryStreamAsync(cancellationToken).ConfigureAwait(false);
                    written = await WriteAsync(input, target, written, cancellationToken).ConfigureAwait(false);
                    done++;
                }
            }
        }
        else
        {
            foreach (var entry in archive.Entries)
            {
                var key = (entry.Key ?? string.Empty).Replace('\\', '/');
                RequireSafeEntryName(key);
                if (!entry.IsDirectory && wanted.TryGetValue(key, out var target))
                {
                    await using var input = await entry.OpenEntryStreamAsync(cancellationToken).ConfigureAwait(false);
                    written = await WriteAsync(input, target, written, cancellationToken).ConfigureAwait(false);
                    done++;
                }
            }
        }

        if (done < wanted.Count)
        {
            throw Errors.Input("ARCHIVE_ENTRY_MISSING", $"{wanted.Count - done} selected file(s) were not found in the archive.", "The archive changed after it was read; choose it again.");
        }
    }

    /// <summary>Copies an entry to a new file and returns the running total of bytes written; stops at the size budget.</summary>
    private static async Task<long> WriteAsync(Stream input, string target, long written, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            written += read;
            if (written > ArchiveLimits.MaxExtractedBytes)
            {
                throw Errors.Input("ARCHIVE_TOO_LARGE", $"The archive expands to more than {ArchiveLimits.MaxExtractedBytes >> 30} GiB.", "Install a smaller archive, or unpack it and install the .vpk file.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    private static IArchive Open(string archivePath)
    {
        var full = Path.GetFullPath(archivePath);
        if (!File.Exists(full))
        {
            throw Errors.Input("ARCHIVE_NOT_FOUND", $"'{full}' does not exist.", "Choose an existing .zip, .7z or .rar file.");
        }

        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);   // owned by the archive, which closes it
        try
        {
            return ArchiveFactory.Open(stream, new ReaderOptions { LeaveStreamOpen = false });
        }
        catch (Exception exception) when (exception is not S2ModKitException and not OperationCanceledException)
        {
            stream.Dispose();                                                // a failed open must not keep the file locked
            throw Errors.Input("ARCHIVE_UNREADABLE", $"'{Path.GetFileName(full)}' is not a readable zip, 7z or rar archive: {exception.Message}", "Unpack the archive by hand and install the .vpk file.");
        }
    }

    /// <summary>Refuses an entry that could leave the extraction folder (zip-slip): a parent segment, a rooted path or a drive.</summary>
    internal static void RequireSafeEntryName(string key)
    {
        var path = key.Replace('\\', '/');
        if (path.StartsWith('/') || path.Contains(':', StringComparison.Ordinal) || path.Split('/').Any(segment => segment == ".."))
        {
            throw Errors.Input("ARCHIVE_PATH_ESCAPE", $"The archive has an entry that leaves its folder: '{key}'.", "Do not install this archive; it may be malicious.");
        }
    }
}
