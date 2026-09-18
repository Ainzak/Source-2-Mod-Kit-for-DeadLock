using System.Collections.Concurrent;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Adapters.Vpk;

public sealed class DeterministicVpkCandidateBuilder : IVpkCandidateBuilder
{
    private readonly ConcurrentDictionary<string, TemporaryCandidateLease> temporaryCandidates = new(StringComparer.OrdinalIgnoreCase);

    public string AdapterName => "s2modkit_compact_vpk_v2";

    public string AdapterVersion => "1";

    public async Task<VpkBuiltCandidate> BuildAsync(
        VpkPackageBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var projectRoot = Path.GetFullPath(request.ProjectRoot);
        var tempRoot = ResolveInside(projectRoot, $"temp/vpk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var candidatePath = Path.Combine(tempRoot, "candidate.vpk");
        try
        {
            var source = await CompactVpkArchiveIO.ReadAndVerifyAsync(request.SourceVpkPath, cancellationToken).ConfigureAwait(false);
            if (source.ContentHash != request.ExpectedSourceArchiveHash)
            {
                throw Errors.Verification(
                    "VPK_SOURCE_HASH_DRIFT",
                    $"Source VPK hash {source.ContentHash} does not match expected {request.ExpectedSourceArchiveHash}.",
                    "Use the exact immutable source archive named by the package request.");
            }

            var sourceEntry = RequireEntry(source, request.EntryLogicalPath);
            if (sourceEntry.ContentHash != request.ExpectedSourceEntryHash)
            {
                throw Errors.Verification(
                    "VPK_SOURCE_ENTRY_HASH_DRIFT",
                    $"Source entry hash {sourceEntry.ContentHash} does not match project input {request.ExpectedSourceEntryHash}.",
                    "Re-extract the project input from the exact source VPK or create a new project.");
            }

            if (!string.Equals(request.Replacement.LogicalPath, sourceEntry.LogicalPath, StringComparison.Ordinal))
            {
                throw Errors.Verification(
                    "VPK_REPLACEMENT_PATH_MISMATCH",
                    $"Replacement logical path '{request.Replacement.LogicalPath}' does not match '{sourceEntry.LogicalPath}'.",
                    "Use a published build for the exact source entry.");
            }

            if (ContentHash.Compute(request.Replacement.Bytes.Span) != request.Replacement.ContentHash)
            {
                throw Errors.Verification(
                    "VPK_REPLACEMENT_HASH_MISMATCH",
                    "Replacement bytes do not match their declared content hash.",
                    "Reload the verified build from the immutable workspace.");
            }

            await CompactVpkArchiveIO.RepackReplacingEntryAsync(
                source,
                sourceEntry.LogicalPath,
                request.Replacement.Bytes,
                candidatePath,
                cancellationToken).ConfigureAwait(false);

            var verification = await VerifyAsync(
                new VpkPackageVerificationRequest(
                    source.Path,
                    candidatePath,
                    source.ContentHash,
                    default,
                    sourceEntry.LogicalPath,
                    sourceEntry.ContentHash,
                    request.Replacement.ContentHash),
                requireCandidateHash: false,
                cancellationToken).ConfigureAwait(false);
            var fullCandidatePath = Path.GetFullPath(candidatePath);
            if (!temporaryCandidates.TryAdd(fullCandidatePath, new TemporaryCandidateLease(projectRoot, tempRoot)))
            {
                throw Errors.Verification(
                    "VPK_TEMPORARY_CANDIDATE_COLLISION",
                    "The generated temporary candidate path is already tracked.",
                    "Retry the package build with a fresh workspace run.");
            }

            return new VpkBuiltCandidate(fullCandidatePath, verification, AdapterName, AdapterVersion);
        }
        catch
        {
            DeleteTemporaryCandidate(projectRoot, tempRoot, candidatePath);
            throw;
        }
    }

    public Task<VpkArchiveComparison> VerifyAsync(
        VpkPackageVerificationRequest request,
        CancellationToken cancellationToken = default) =>
        VerifyAsync(request, requireCandidateHash: true, cancellationToken);

    public async Task<VpkBuiltMinimalCandidate> BuildMinimalAsync(
        VpkMinimalPackageBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var projectRoot = Path.GetFullPath(request.ProjectRoot);
        var tempRoot = ResolveInside(projectRoot, $"temp/vpk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var candidatePath = Path.Combine(tempRoot, "candidate.vpk");
        try
        {
            var logicalPath = CompactVpkArchiveIO.NormalizeVpkPath(request.Entry.LogicalPath);
            if (!string.Equals(logicalPath, request.Entry.LogicalPath, StringComparison.Ordinal)
                || ContentHash.Compute(request.Entry.Bytes.Span) != request.Entry.ContentHash)
            {
                throw Errors.Verification("VPK_MINIMAL_ENTRY_INVALID", "The minimal package entry path or hash is inconsistent.", "Reload the exact published build from the immutable workspace.");
            }

            await CompactVpkArchiveIO.WriteAsync(
                candidatePath,
                new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)
                {
                    [logicalPath] = request.Entry.Bytes,
                },
                cancellationToken).ConfigureAwait(false);
            var verification = await VerifyMinimalAsync(
                new VpkMinimalPackageVerificationRequest(candidatePath, default, logicalPath, request.Entry.ContentHash),
                requireCandidateHash: false,
                cancellationToken).ConfigureAwait(false);
            var fullCandidatePath = Path.GetFullPath(candidatePath);
            TrackTemporaryCandidate(projectRoot, tempRoot, fullCandidatePath);
            return new VpkBuiltMinimalCandidate(fullCandidatePath, verification, AdapterName, AdapterVersion);
        }
        catch
        {
            DeleteTemporaryCandidate(projectRoot, tempRoot, candidatePath);
            throw;
        }
    }

    public Task<VpkMinimalArchiveComparison> VerifyMinimalAsync(
        VpkMinimalPackageVerificationRequest request,
        CancellationToken cancellationToken = default) =>
        VerifyMinimalAsync(request, requireCandidateHash: true, cancellationToken);

    public Task DiscardAsync(VpkBuiltCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        DiscardTemporary(candidate.TemporaryPath, cancellationToken);
        return Task.CompletedTask;
    }

    public Task DiscardAsync(VpkBuiltMinimalCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        DiscardTemporary(candidate.TemporaryPath, cancellationToken);
        return Task.CompletedTask;
    }

    private static async Task<VpkArchiveComparison> VerifyAsync(
        VpkPackageVerificationRequest request,
        bool requireCandidateHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = await CompactVpkArchiveIO.ReadAndVerifyAsync(request.SourceVpkPath, cancellationToken).ConfigureAwait(false);
        var output = await CompactVpkArchiveIO.ReadAndVerifyAsync(request.CandidateVpkPath, cancellationToken).ConfigureAwait(false);
        if (source.ContentHash != request.ExpectedSourceArchiveHash)
        {
            throw Errors.Verification("VPK_SOURCE_HASH_DRIFT", $"Source VPK hash {source.ContentHash} does not match {request.ExpectedSourceArchiveHash}.", "Restore the exact immutable source VPK.");
        }

        if (requireCandidateHash && output.ContentHash != request.ExpectedCandidateArchiveHash)
        {
            throw Errors.Verification("VPK_CANDIDATE_HASH_DRIFT", $"Candidate VPK hash {output.ContentHash} does not match {request.ExpectedCandidateArchiveHash}.", "Do not install the altered package; restore it from the immutable workspace.");
        }

        var sourceEntry = RequireEntry(source, request.EntryLogicalPath);
        var outputEntry = RequireEntry(output, request.EntryLogicalPath);
        if (sourceEntry.ContentHash != request.ExpectedSourceEntryHash)
        {
            throw Errors.Verification("VPK_SOURCE_ENTRY_HASH_DRIFT", $"Source entry hash {sourceEntry.ContentHash} does not match {request.ExpectedSourceEntryHash}.", "Use the source archive from which the project input was imported.");
        }

        if (outputEntry.ContentHash != request.ExpectedReplacementEntryHash)
        {
            throw Errors.Verification("VPK_REPLACEMENT_ENTRY_HASH_DRIFT", $"Output entry hash {outputEntry.ContentHash} does not match build {request.ExpectedReplacementEntryHash}.", "Reject the candidate and repackage the verified model build.");
        }

        if (source.Entries.Count != output.Entries.Count)
        {
            throw Errors.Verification("VPK_ENTRY_COUNT_CHANGED", $"Entry count changed from {source.Entries.Count} to {output.Entries.Count}.", "Reject the candidate; only one existing entry may be replaced.");
        }

        var sourceByPath = source.Entries.ToDictionary(entry => entry.LogicalPath, StringComparer.Ordinal);
        var outputByPath = output.Entries.ToDictionary(entry => entry.LogicalPath, StringComparer.Ordinal);
        if (!sourceByPath.Keys.Order(StringComparer.Ordinal).SequenceEqual(outputByPath.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw Errors.Verification("VPK_ENTRY_PATHS_CHANGED", "Source and candidate VPK logical paths differ.", "Reject the candidate; preserve every source path.");
        }

        var normalizedTarget = CompactVpkArchiveIO.NormalizeVpkPath(request.EntryLogicalPath);
        var unchanged = 0;
        foreach (var sourcePair in sourceByPath)
        {
            if (string.Equals(sourcePair.Key, normalizedTarget, StringComparison.Ordinal))
            {
                continue;
            }

            var outputValue = outputByPath[sourcePair.Key];
            if (sourcePair.Value.ContentHash != outputValue.ContentHash
                || sourcePair.Value.Crc32 != outputValue.Crc32
                || sourcePair.Value.Length != outputValue.Length)
            {
                throw Errors.Verification("VPK_NON_TARGET_ENTRY_CHANGED", $"Non-target entry '{sourcePair.Key}' changed.", "Reject the candidate and inspect the deterministic repack path.");
            }

            unchanged++;
        }

        if (source.ContentHash == output.ContentHash)
        {
            throw Errors.Verification("VPK_ARCHIVE_UNCHANGED", "Source and candidate VPK hashes are identical.", "Use a build whose target entry contains the intended mutation.");
        }

        var entryEvidence = new VpkEntryEvidence(
            normalizedTarget,
            sourceEntry.ContentHash,
            outputEntry.ContentHash,
            sourceEntry.Crc32,
            outputEntry.Crc32,
            sourceEntry.Length,
            outputEntry.Length);
        return new VpkArchiveComparison(
            ToEvidence(source),
            ToEvidence(output),
            entryEvidence,
            unchanged,
            [
                new BoundaryEvidence("vpk_source", "passed", $"Source VPK {source.ContentHash} passed compact-profile CRC/MD5 verification."),
                new BoundaryEvidence("vpk_target_entry", "passed", $"Source entry {normalizedTarget} matches immutable project input {sourceEntry.ContentHash}."),
                new BoundaryEvidence("vpk_replacement_entry", "passed", $"Candidate entry matches published model build {outputEntry.ContentHash}."),
                new BoundaryEvidence("vpk_non_target_entries", "passed", $"All {unchanged} non-target entries retain length, CRC32, and SHA-256 content."),
                new BoundaryEvidence("vpk_internal_checksums", "passed", "Candidate VPK reopened and passed every entry CRC32 and v2 MD5 check."),
            ]);
    }

    private static async Task<VpkMinimalArchiveComparison> VerifyMinimalAsync(
        VpkMinimalPackageVerificationRequest request,
        bool requireCandidateHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var output = await CompactVpkArchiveIO.ReadAndVerifyAsync(request.CandidateVpkPath, cancellationToken).ConfigureAwait(false);
        if (requireCandidateHash && output.ContentHash != request.ExpectedCandidateArchiveHash)
        {
            throw Errors.Verification("VPK_CANDIDATE_HASH_DRIFT", $"Candidate VPK hash {output.ContentHash} does not match {request.ExpectedCandidateArchiveHash}.", "Do not install the altered package; restore it from the immutable workspace.");
        }

        if (output.Entries.Count != 1)
        {
            throw Errors.Verification("VPK_MINIMAL_ENTRY_COUNT_CHANGED", $"A minimal package must contain exactly one entry, found {output.Entries.Count}.", "Reject the package and recreate it from the verified model build.");
        }

        var entry = RequireEntry(output, request.EntryLogicalPath);
        if (entry.ContentHash != request.ExpectedEntryHash)
        {
            throw Errors.Verification("VPK_REPLACEMENT_ENTRY_HASH_DRIFT", $"Minimal package entry hash {entry.ContentHash} does not match build {request.ExpectedEntryHash}.", "Reject the candidate and repackage the verified model build.");
        }

        return new VpkMinimalArchiveComparison(
            ToEvidence(output),
            new VpkPackagedEntryEvidence(entry.LogicalPath, entry.ContentHash, entry.Crc32, entry.Length),
            [
                new BoundaryEvidence("vpk_entry_count", "passed", "The minimal candidate contains exactly one logical entry."),
                new BoundaryEvidence("vpk_packaged_entry", "passed", $"Candidate entry {entry.LogicalPath} matches published model build {entry.ContentHash}."),
                new BoundaryEvidence("vpk_internal_checksums", "passed", "Candidate VPK reopened and passed its entry CRC32 and v2 MD5 checks."),
            ]);
    }

    private static CompactVpkEntry RequireEntry(CompactVpkArchive archive, string logicalPath)
    {
        var normalized = CompactVpkArchiveIO.NormalizeVpkPath(logicalPath);
        var matches = archive.Entries.Where(entry => string.Equals(entry.LogicalPath, normalized, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw Errors.Verification("VPK_TARGET_ENTRY_CARDINALITY", $"Expected one entry '{normalized}' in '{archive.Path}', found {matches.Length}.", "Use a source archive containing exactly one target path.");
    }

    private static VpkArchiveEvidence ToEvidence(CompactVpkArchive archive) =>
        new(archive.Path, archive.ContentHash, archive.Size, archive.Entries.Count);

    private void TrackTemporaryCandidate(string projectRoot, string tempRoot, string candidatePath)
    {
        if (!temporaryCandidates.TryAdd(candidatePath, new TemporaryCandidateLease(projectRoot, tempRoot)))
        {
            throw Errors.Verification(
                "VPK_TEMPORARY_CANDIDATE_COLLISION",
                "The generated temporary candidate path is already tracked.",
                "Retry the package build with a fresh workspace run.");
        }
    }

    private void DiscardTemporary(string temporaryPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidatePath = Path.GetFullPath(temporaryPath);
        if (!temporaryCandidates.TryRemove(candidatePath, out var lease))
        {
            throw Errors.Verification(
                "VPK_TEMPORARY_CANDIDATE_UNTRACKED",
                "Refusing to discard a temporary VPK that was not created by this builder instance.",
                "Discard only the exact candidate returned by the builder.");
        }

        DeleteTemporaryCandidate(lease.ProjectRoot, lease.TemporaryRoot, candidatePath);
    }

    private static string ResolveInside(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw Errors.Input("PATH_ESCAPES_WORKSPACE", $"Path '{relativePath}' escapes '{root}'.", "Use the configured project workspace.");
        }

        return candidate;
    }

    private static void DeleteTemporaryCandidate(string projectRoot, string temporaryRoot, string candidatePath)
    {
        var fullTemporaryRoot = Path.GetFullPath(temporaryRoot);
        var expectedParent = ResolveInside(Path.GetFullPath(projectRoot), "temp");
        var actualParent = Path.GetDirectoryName(fullTemporaryRoot);
        var directoryName = Path.GetFileName(fullTemporaryRoot);
        var fullCandidatePath = Path.GetFullPath(candidatePath);
        var expectedCandidatePath = Path.Combine(fullTemporaryRoot, "candidate.vpk");
        if (!string.Equals(actualParent, expectedParent, StringComparison.OrdinalIgnoreCase)
            || !directoryName.StartsWith("vpk-", StringComparison.Ordinal)
            || !string.Equals(fullCandidatePath, expectedCandidatePath, StringComparison.OrdinalIgnoreCase))
        {
            throw Errors.Verification(
                "VPK_TEMPORARY_PATH_INVALID",
                "Refusing to clean a path outside the expected project temp candidate directory.",
                "Inspect the workspace and discard only an adapter-created VPK candidate.");
        }

        if (File.Exists(fullCandidatePath))
        {
            File.Delete(fullCandidatePath);
        }

        if (Directory.Exists(fullTemporaryRoot))
        {
            if ((File.GetAttributes(fullTemporaryRoot) & FileAttributes.ReparsePoint) != 0)
            {
                throw Errors.Verification(
                    "VPK_TEMPORARY_PATH_REPARSE_POINT",
                    "Refusing to clean a temporary candidate directory that became a reparse point.",
                    "Inspect the workspace for unexpected filesystem changes.");
            }

            Directory.Delete(fullTemporaryRoot, recursive: false);
        }
    }

    private sealed record TemporaryCandidateLease(string ProjectRoot, string TemporaryRoot);
}
