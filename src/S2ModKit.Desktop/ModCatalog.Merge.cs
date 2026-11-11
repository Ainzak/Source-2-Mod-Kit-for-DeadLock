using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Desktop;

internal sealed partial class ModCatalog
{
    /// <summary>
    /// Merges <paramref name="mods"/> into one new mod (<c>merge_addons@1</c>): every file of every mod, a file several of them have
    /// taken from the one the game loads first (the lowest pak number; disabled mods after enabled ones). The new mod is installed,
    /// the enabled originals are disabled (renamed, never deleted) and put back on if the install fails. A <see cref="MergeRecord"/>
    /// beside the package keeps the mods' files and hashes for <see cref="SplitAsync"/>.
    /// </summary>
    public async Task<CatalogChange> MergeAsync(IReadOnlyList<ModEntry> mods, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mods);
        Management.EnsureGameStopped();
        var ordered = ModMerge.InLoadOrder(mods.DistinctBy(mod => mod.Key));
        if (ordered.Count < 2)
        {
            throw Errors.Input("MERGE_TOO_FEW", "Merging needs at least two mods.", "Tick another mod.");
        }

        if (ordered.FirstOrDefault(mod => mod.Files.Count == 0 || mod.Files.Any(file => !File.Exists(file))) is { } missing)
        {
            throw Errors.Input("MERGE_FILES_MISSING", $"The files of '{missing.Name}' are not there.", "Refresh the list and try again.");
        }

        var entries = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        var keptFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<MergedPart>();
        foreach (var mod in ordered)
        {
            var files = new List<MergedFile>();
            foreach (var file in ModMerge.FilesInOrder(mod))
            {
                using (var package = new SteamDatabase.ValvePak.Package())
                {
                    package.Read(file);
                    foreach (var entry in package.Entries?.Values.SelectMany(group => group) ?? [])
                    {
                        var path = entry.GetFullPath().Replace('\\', '/');
                        if (!keptFrom.ContainsKey(path))
                        {
                            package.ReadEntry(entry, out var bytes);
                            entries[path] = bytes;
                            keptFrom[path] = mod.Name;
                        }
                    }
                }

                files.Add(await ModMerge.DescribeAsync(file, cancellationToken).ConfigureAwait(false));
            }

            parts.Add(new MergedPart(mod.Name, mod.Key, mod.Loaded, mod.Loaded ? mod.FirstSlot : null, files));
        }

        if (entries.Count == 0)
        {
            throw Errors.Input("MERGE_EMPTY", "The chosen mods have no files.", "Choose other mods.");
        }

        var conflicts = ModMerge.Conflicts([.. ordered.Select(mod => (mod.Name, (IReadOnlyList<string>)[.. mod.Entries.Select(entry => entry.Replace('\\', '/'))]))]);
        var folder = Path.Combine(settings.Data, "merges", $"{Sanitize(name)}-{clock.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(folder);
        var output = Path.Combine(folder, $"{Sanitize(name)}_dir.vpk");
        await S2ModKit.Adapters.Vpk.CompactVpkArchiveIO.WriteAsync(output, entries, cancellationToken).ConfigureAwait(false);
        var written = await ModMerge.DescribeAsync(output, cancellationToken).ConfigureAwait(false);
        new MergeRecord(MergeRecord.CurrentSchema, "merge_addons@1", name, clock.UtcNow, parts, conflicts, entries.Count, written.Sha256).Write(folder);

        var disabled = new List<string>();
        try
        {
            foreach (var mod in ordered.Where(mod => mod.Loaded))
            {
                disabled.Add((await DisableAsync(mod, cancellationToken).ConfigureAwait(false)).SelectKey);   // an original must not win over the merge
            }

            var installed = await InstallAsync(output, name, cancellationToken).ConfigureAwait(false);
            if (installed.Receipt is not { } receipt)
            {
                throw Errors.Verification("MERGE_INSTALL_FAILED", $"The merged mod was not installed: {installed.Summary}", "The originals are turned back on; try again.");
            }

            return new CatalogChange(AddonNaming.FileKey(receipt.TargetFileName),
                $"Merged {ordered.Count} mods into '{name}' ({entries.Count} files; {conflicts.Count} shared file{(conflicts.Count == 1 ? string.Empty : "s")} kept from the mod that loads first). "
                + "The originals are disabled; Split turns them back on.");
        }
        catch
        {
            var now = await LoadAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var key in disabled)
            {
                if (now.FirstOrDefault(mod => mod.Key == key) is { Loaded: false } original)
                {
                    await EnableAsync(original, CancellationToken.None).ConfigureAwait(false);
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Undoes a merge: disables the merged mod (renamed, kept) and turns its originals back on, found by their files' sizes and
    /// SHA-256 hashes. Originals that are gone are named in the message.
    /// </summary>
    public async Task<CatalogChange> SplitAsync(ModEntry merged, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(merged);
        var record = MergeRecord.Of(merged) ?? throw Errors.Input("MERGE_RECORD_MISSING", $"'{merged.Name}' was not merged by S2ModKit.", "Only merged mods can be split.");
        Management.EnsureGameStopped();
        var mods = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var found = new List<(MergedPart Part, ModEntry Mod)>();
        var missing = new List<string>();
        foreach (var part in record.Parts)
        {
            ModEntry? match = null;
            foreach (var mod in mods.Where(mod => mod.Key != merged.Key && mod.Files.Count == part.Files.Count))
            {
                var sizes = mod.Files.Select(file => File.Exists(file) ? new FileInfo(file).Length : -1).Order().ToArray();
                if (!sizes.SequenceEqual(part.Files.Select(file => file.Size).Order()))
                {
                    continue;
                }

                var hashes = new List<string>();
                foreach (var file in mod.Files)
                {
                    hashes.Add((await ModMerge.DescribeAsync(file, cancellationToken).ConfigureAwait(false)).Sha256);
                }

                if (hashes.Order(StringComparer.Ordinal).SequenceEqual(part.Files.Select(file => file.Sha256).Order(StringComparer.Ordinal)))
                {
                    match = mod;
                    break;
                }
            }

            if (match is null)
            {
                missing.Add(part.Name);
            }
            else
            {
                found.Add((part, match));
            }
        }

        if (found.Count == 0)
        {
            throw Errors.Input("MERGE_ORIGINALS_MISSING", $"None of the mods merged into '{merged.Name}' are in the addons folder any more.", "Keep the merged mod, or install the originals again.");
        }

        var change = merged.Loaded ? await DisableAsync(merged, cancellationToken).ConfigureAwait(false) : new CatalogChange(merged.Key, string.Empty);
        var selected = change.SelectKey;
        foreach (var (part, mod) in found.Where(item => item.Part.WasEnabled && !item.Mod.Loaded))
        {
            var current = (await LoadAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(entry => entry.Key == mod.Key) ?? mod;
            selected = (await EnableAsync(current, cancellationToken).ConfigureAwait(false)).SelectKey;
        }

        return new CatalogChange(selected, $"Split '{merged.Name}': it is disabled and {found.Count} original{(found.Count == 1 ? " is" : "s are")} back as before."
            + (missing.Count > 0 ? $" Not found any more: {string.Join(", ", missing)}." : string.Empty));
    }
}
