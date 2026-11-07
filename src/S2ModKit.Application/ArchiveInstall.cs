using System.Text.RegularExpressions;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public static class ArchiveLimits
{
    /// <summary>Entries an archive may list; a mod archive holds a handful of files.</summary>
    public const int MaxEntries = 20_000;

    /// <summary>Bytes written while extracting <c>.vpk</c> files from one archive.</summary>
    public const long MaxExtractedBytes = 8L << 30;
}

public sealed record ArchiveVpkEntry(string EntryPath, long Size);

/// <summary>A target file for one archive entry; the caller chooses the path, never the archive.</summary>
public sealed record ArchiveExtraction(string EntryPath, string TargetPath);

/// <summary>One installable package in an archive: a <c>_dir.vpk</c> with its data chunks (or a single <c>.vpk</c>).</summary>
public sealed record ArchiveVariant(string DirEntryPath, string FileName, string Folder, long TotalSize, IReadOnlyList<ArchiveVpkEntry> Files);

public interface IArchivePackageReader
{
    /// <summary>The <c>.vpk</c> entries of a zip, 7z or rar archive; refuses an archive with a traversal entry.</summary>
    Task<IReadOnlyList<ArchiveVpkEntry>> ListVpksAsync(string archivePath, CancellationToken cancellationToken = default);

    /// <summary>Writes the named entries to the given target files, which must lie inside <paramref name="stagingRoot"/>.</summary>
    Task ExtractAsync(string archivePath, IReadOnlyList<ArchiveExtraction> extractions, string stagingRoot, CancellationToken cancellationToken = default);
}

/// <summary>Groups the <c>.vpk</c> entries of an archive into variants and lays out their staging files.</summary>
public static partial class ArchiveVariants
{
    /// <summary>One variant per <c>_dir.vpk</c> (with the numbered chunks of the same folder and name) and one per single-file
    /// <c>.vpk</c> that is no chunk of a <c>_dir.vpk</c>; a chunk without its <c>_dir.vpk</c> is ignored. Sorted by folder, then name.</summary>
    public static IReadOnlyList<ArchiveVariant> Group(IReadOnlyList<ArchiveVpkEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var parsed = entries.Select(entry => (Entry: entry, Folder: FolderOf(entry.EntryPath), Name: NameOf(entry.EntryPath))).ToArray();
        var dirs = parsed.Where(item => item.Name.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase)).ToArray();
        var variants = new List<ArchiveVariant>();
        foreach (var dir in dirs)
        {
            var stem = dir.Name[..^"_dir.vpk".Length];
            var chunks = parsed.Where(item => item.Folder.Equals(dir.Folder, StringComparison.OrdinalIgnoreCase)
                && Chunk().Match(item.Name) is { Success: true } match && match.Groups["stem"].Value.Equals(stem, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Entry).OrderBy(entry => entry.EntryPath, StringComparer.OrdinalIgnoreCase).ToArray();
            variants.Add(Build(dir.Entry, dir.Folder, dir.Name, chunks));
        }

        foreach (var single in parsed.Where(item => !dirs.Contains(item) && !Chunk().IsMatch(item.Name)))
        {
            variants.Add(Build(single.Entry, single.Folder, single.Name, []));
        }

        return [.. variants.OrderBy(variant => variant.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(variant => variant.FileName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Staging files for the chosen variants: <c>staging/vN/&lt;leaf name&gt;</c>, so the chunks keep the names the <c>_dir.vpk</c> expects.
    /// A leaf name that is not a plain file name is refused.</summary>
    public static IReadOnlyList<(ArchiveVariant Variant, string DirPath, IReadOnlyList<ArchiveExtraction> Extractions)> Layout(IReadOnlyList<ArchiveVariant> chosen, string stagingRoot)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        var result = new List<(ArchiveVariant, string, IReadOnlyList<ArchiveExtraction>)>();
        for (var i = 0; i < chosen.Count; i++)
        {
            var folder = Path.Combine(stagingRoot, $"v{i + 1}");
            var extractions = chosen[i].Files.Select(file => new ArchiveExtraction(file.EntryPath, Path.Combine(folder, SafeLeaf(NameOf(file.EntryPath))))).ToArray();
            result.Add((chosen[i], Path.Combine(folder, SafeLeaf(chosen[i].FileName)), extractions));
        }

        return result;
    }

    private static ArchiveVariant Build(ArchiveVpkEntry main, string folder, string name, IReadOnlyList<ArchiveVpkEntry> chunks) =>
        new(main.EntryPath, name, folder, main.Size + chunks.Sum(chunk => chunk.Size), [main, .. chunks]);

    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string FolderOf(string path) => path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : string.Empty;

    private static string SafeLeaf(string name) =>
        name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            ? throw Errors.Input("ARCHIVE_NAME_INVALID", $"'{name}' is not a plain file name.", "Do not install this archive; unpack it by hand.")
            : name;

    [GeneratedRegex(@"^(?<stem>.+)_(?<n>\d{3})\.vpk$", RegexOptions.IgnoreCase)]
    private static partial Regex Chunk();
}
