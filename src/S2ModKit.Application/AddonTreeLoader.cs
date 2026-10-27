using S2ModKit.Domain;

namespace S2ModKit.Application;

/// <summary>Reads the directory trees of the addon archives in one folder (tree only, no payload reads), optionally
/// through a cache keyed by file size and modification time. Shared by the addon scan, the install collision check and
/// the mod status.</summary>
public sealed class AddonTreeLoader(IAddonFileStore store, IAddonTreeReader treeReader)
{
    public async Task<(IReadOnlyList<(AddonFileInfo File, IReadOnlyList<string> Paths)> Trees, int Cached)> ReadAsync(
        string addonsRoot, string? cachePath, bool loadedOnly, CancellationToken cancellationToken)
    {
        var files = await store.ListArchivesAsync(addonsRoot, cancellationToken).ConfigureAwait(false);
        var cache = await store.LoadTreeCacheAsync(cachePath, cancellationToken).ConfigureAwait(false);
        var next = new Dictionary<string, AddonTreeCacheEntry>(StringComparer.OrdinalIgnoreCase);
        var trees = new List<(AddonFileInfo, IReadOnlyList<string>)>();
        var cached = 0;
        foreach (var file in files.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (loadedOnly && !AddonFileApplication.IsLoadedName(file.FileName))
            {
                continue;
            }

            if (file.IsReparsePoint)
            {
                throw Errors.Verification("ADDON_ARCHIVE_REPARSE_POINT", $"Addon archive '{file.FullPath}' is a reparse point.", "Replace it with a regular file.");
            }

            IReadOnlyList<string> paths;
            if (cache.TryGetValue(file.FileName, out var hit) && hit.Size == file.Size && hit.LastWriteUtcTicks == file.LastWriteUtcTicks)
            {
                paths = hit.Paths;
                cached++;
            }
            else
            {
                paths = await treeReader.ReadEntryPathsAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
            }

            next[file.FileName] = new AddonTreeCacheEntry(file.Size, file.LastWriteUtcTicks, paths);
            trees.Add((file, paths));
        }

        if (!loadedOnly)
        {
            await store.SaveTreeCacheAsync(cachePath, next, cancellationToken).ConfigureAwait(false);
        }
        else if (cachePath is not null)
        {
            foreach (var (name, entry) in cache)
            {
                next.TryAdd(name, entry);
            }

            await store.SaveTreeCacheAsync(cachePath, next, cancellationToken).ConfigureAwait(false);
        }

        return (trees, cached);
    }
}
