using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed record AddonMergeRequest(
    IReadOnlyList<string> Inputs,
    string? OutputPath,
    IReadOnlyList<string> Prefer,
    IReadOnlyDictionary<string, string> Keep,
    bool DryRun);

public sealed record AddonMergeInput(string FileName, string FullPath, ContentHash Hash, int EntryCount, IReadOnlyDictionary<string, int> ResourceKinds);

/// <summary>A logical path more than one input ships; <c>Chosen</c> is the input whose copy goes into the merge.</summary>
public sealed record AddonMergeConflict(string LogicalPath, string Kind, IReadOnlyList<string> Inputs, bool IdenticalPayloads, string? Chosen);

/// <summary>A model in one input that references a resource another input replaces: not a file conflict, but the
/// two mods may not have been made for each other.</summary>
public sealed record AddonMergeDependencyNotice(string ModelPath, string ModelInput, string ReferencedPath, string ReferencedInput);

public sealed record AddonMergeResult(
    string Status,
    IReadOnlyList<AddonMergeInput> Inputs,
    IReadOnlyList<AddonMergeConflict> Conflicts,
    IReadOnlyList<AddonMergeDependencyNotice> DependencyNotices,
    string? OutputPath,
    ContentHash? OutputHash,
    int EntryCount,
    string Summary);

public interface IAddonArchiveContentReader
{
    Task<byte[]> ReadEntryAsync(string archivePath, string logicalPath, CancellationToken cancellationToken = default);
}

public interface IAddonArchiveWriter
{
    /// <summary>Writes a new archive at an absent path; never overwrites.</summary>
    Task WriteNewAsync(string outputPath, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> entries, CancellationToken cancellationToken = default);
}

/// <summary>Combines several addon archives into one new archive. Inputs are never changed. Files of
/// different resources merge freely; the same logical path in two inputs is a conflict that the caller resolves
/// with <c>Prefer</c> (input order of precedence) or <c>Keep</c> (per path). Identical payloads are not conflicts.
/// Which archive the game itself would prefer is not assumed.</summary>
public sealed class AddonMergeApplication(
    IAddonTreeReader treeReader,
    IAddonArchiveContentReader contentReader,
    IAddonArchiveWriter writer,
    IAddonFileStore store,
    IResourceDependencyReader? dependencyReader)
{
    public async Task<AddonMergeResult> MergeAsync(AddonMergeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Inputs.Count < 2)
        {
            throw Errors.InvalidRecipe("ADDON_MERGE_INPUTS", "A merge needs at least two input archives.", "Pass --input once per archive.");
        }

        var paths = request.Inputs.Select(Path.GetFullPath).ToArray();
        var names = paths.Select(Path.GetFileName).ToArray();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
        {
            throw Errors.InvalidRecipe("ADDON_MERGE_DUPLICATE_INPUT", "Two inputs have the same file name.", "Each input must be a different archive file name.");
        }

        if (!request.DryRun)
        {
            if (string.IsNullOrWhiteSpace(request.OutputPath))
            {
                throw Errors.InvalidRecipe("ADDON_MERGE_OUTPUT_REQUIRED", "No output path was given.", "Pass --output <new .vpk> or use --dry-run.");
            }

            var output = Path.GetFullPath(request.OutputPath);
            if (File.Exists(output) || Directory.Exists(output) || paths.Contains(output, StringComparer.OrdinalIgnoreCase))
            {
                throw Errors.Verification("ADDON_MERGE_OUTPUT_EXISTS", $"'{output}' already exists or is an input.", "Choose a new output path; inputs and existing files are never overwritten.");
            }
        }

        foreach (var preferred in request.Prefer.Concat(request.Keep.Values))
        {
            if (!names.Contains(preferred, StringComparer.OrdinalIgnoreCase))
            {
                throw Errors.InvalidRecipe("ADDON_MERGE_UNKNOWN_INPUT", $"'{preferred}' is not one of the input file names.", "Name inputs by file name, e.g. pak03_dir.vpk.");
            }
        }

        var trees = new List<(string Name, string Path, IReadOnlyList<string> Entries)>();
        var inputs = new List<AddonMergeInput>();
        for (var index = 0; index < paths.Length; index++)
        {
            var entries = await treeReader.ReadEntryPathsAsync(paths[index], cancellationToken).ConfigureAwait(false);
            if (entries.Distinct(StringComparer.Ordinal).Count() != entries.Count)
            {
                throw Errors.Verification("ADDON_MERGE_DUPLICATE_ENTRY", $"'{names[index]}' lists a path more than once.", "Rebuild that archive before merging.");
            }

            trees.Add((names[index]!, paths[index], entries));
            inputs.Add(new AddonMergeInput(names[index]!, paths[index], await store.HashFileAsync(paths[index], cancellationToken).ConfigureAwait(false), entries.Count, AddonResourceKinds.Count(entries)));
        }

        var owners = trees.SelectMany(tree => tree.Entries.Select(entry => (entry, tree.Name)))
            .GroupBy(item => item.entry, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Name).ToArray(), StringComparer.Ordinal);
        var chosen = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicts = new List<AddonMergeConflict>();
        foreach (var (logicalPath, holders) in owners.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (holders.Length == 1)
            {
                chosen[logicalPath] = holders[0];
                continue;
            }

            var payloads = new List<byte[]>();
            foreach (var holder in holders)
            {
                payloads.Add(await contentReader.ReadEntryAsync(trees.Single(tree => tree.Name == holder).Path, logicalPath, cancellationToken).ConfigureAwait(false));
            }

            var identical = payloads.Skip(1).All(payload => payload.AsSpan().SequenceEqual(payloads[0]));
            var pick = identical ? holders[0] : Resolve(logicalPath, holders, request);
            if (pick is not null)
            {
                chosen[logicalPath] = pick;
            }

            conflicts.Add(new AddonMergeConflict(logicalPath, AddonResourceKinds.Classify(logicalPath), holders, identical, pick));
        }

        var notices = await DependencyNoticesAsync(trees, owners, cancellationToken).ConfigureAwait(false);
        var unresolved = conflicts.Where(item => item.Chosen is null).ToArray();
        if (unresolved.Length > 0)
        {
            var listed = string.Join("; ", unresolved.Take(10).Select(item => $"{item.LogicalPath} ({string.Join(" / ", item.Inputs)})"));
            var more = unresolved.Length > 10 ? $"; and {unresolved.Length - 10} more" : string.Empty;
            if (request.DryRun)
            {
                return new AddonMergeResult("conflicts", inputs, conflicts, notices, null, null, 0, $"{unresolved.Length} file(s) differ between inputs: {listed}{more}. Choose with --prefer or --keep.");
            }

            throw Errors.Verification("ADDON_MERGE_CONFLICT", $"{unresolved.Length} file(s) differ between inputs: {listed}{more}.", "Choose with --prefer <input> (whole input wins) or --keep <path>=<input> (one file), or run --dry-run to list all.");
        }

        if (request.DryRun)
        {
            return new AddonMergeResult("ready", inputs, conflicts, notices, null, null, chosen.Count, $"Ready: {chosen.Count} files from {inputs.Count} archives, {conflicts.Count(item => !item.IdenticalPayloads)} conflict(s) resolved, {notices.Count} dependency notice(s).");
        }

        var payloadsByPath = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        foreach (var (logicalPath, input) in chosen)
        {
            cancellationToken.ThrowIfCancellationRequested();
            payloadsByPath[logicalPath] = await contentReader.ReadEntryAsync(trees.Single(tree => tree.Name == input).Path, logicalPath, cancellationToken).ConfigureAwait(false);
        }

        var outputPath = Path.GetFullPath(request.OutputPath!);
        await writer.WriteNewAsync(outputPath, payloadsByPath, cancellationToken).ConfigureAwait(false);
        var written = await treeReader.ReadEntryPathsAsync(outputPath, cancellationToken).ConfigureAwait(false);
        if (!written.Order(StringComparer.Ordinal).SequenceEqual(chosen.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw Errors.Verification("ADDON_MERGE_OUTPUT_MISMATCH", "The written archive does not list exactly the merged files.", "Delete the output and retry; inputs were not changed.");
        }

        var hash = await store.HashFileAsync(outputPath, cancellationToken).ConfigureAwait(false);
        return new AddonMergeResult("merged", inputs, conflicts, notices, outputPath, hash, chosen.Count,
            $"Merged {inputs.Count} archives into {Path.GetFileName(outputPath)} ({chosen.Count} files, {conflicts.Count(item => !item.IdenticalPayloads)} conflict(s) resolved, {notices.Count} dependency notice(s)). Inputs were not changed.");
    }

    private static string? Resolve(string logicalPath, string[] holders, AddonMergeRequest request)
    {
        if (request.Keep.TryGetValue(logicalPath, out var kept))
        {
            return holders.Contains(kept, StringComparer.OrdinalIgnoreCase)
                ? holders.First(holder => string.Equals(holder, kept, StringComparison.OrdinalIgnoreCase))
                : throw Errors.InvalidRecipe("ADDON_MERGE_KEEP_NOT_HOLDER", $"--keep names '{kept}' for {logicalPath}, which that input does not contain.", "Pick one of the inputs that ship this file.");
        }

        foreach (var preferred in request.Prefer)
        {
            var match = holders.FirstOrDefault(holder => string.Equals(holder, preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<AddonMergeDependencyNotice>> DependencyNoticesAsync(
        List<(string Name, string Path, IReadOnlyList<string> Entries)> trees,
        Dictionary<string, string[]> owners,
        CancellationToken cancellationToken)
    {
        if (dependencyReader is null)
        {
            return [];
        }

        var notices = new List<AddonMergeDependencyNotice>();
        foreach (var tree in trees)
        {
            foreach (var model in tree.Entries.Where(entry => entry.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase)))
            {
                var bytes = await contentReader.ReadEntryAsync(tree.Path, model, cancellationToken).ConfigureAwait(false);
                var artifact = new ArtifactContent(model, ContentHash.Compute(bytes), bytes);
                if (!dependencyReader.CanReadDependencies(artifact))
                {
                    continue;
                }

                IReadOnlyList<ResourceDependency> dependencies;
                try
                {
                    dependencies = await dependencyReader.ReadDependenciesAsync(artifact, cancellationToken).ConfigureAwait(false);
                }
                catch (S2ModKitException)
                {
                    continue;                                             // unreadable models only lose the notice
                }

                foreach (var dependency in dependencies)
                {
                    var reference = StableIdentity.NormalizePath(dependency.LogicalPath);
                    var compiled = reference.EndsWith("_c", StringComparison.Ordinal) ? reference : reference + "_c";
                    if (owners.TryGetValue(compiled, out var holders))
                    {
                        notices.AddRange(holders.Where(holder => holder != tree.Name && !tree.Entries.Contains(compiled))
                            .Select(holder => new AddonMergeDependencyNotice(model, tree.Name, compiled, holder)));
                    }
                }
            }
        }

        return notices;
    }
}
