using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Infrastructure;

public sealed partial class FileSystemProjectWorkspace
{
    public Task SavePlanAsync(string projectRoot, MutationPlan plan, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(projectRoot);
        var path = ResolveInside(root, $"plans/{plan.Fingerprint}.json");
        return WriteIdempotentAsync(path, JsonDefaults.SerializeToUtf8(plan), cancellationToken);
    }

    public async Task<MutationPlan> LoadPlanAsync(string projectRoot, ContentHash fingerprint, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(projectRoot);
        var path = ResolveInside(root, $"plans/{fingerprint}.json");
        if (!File.Exists(path))
        {
            throw Errors.Input("PLAN_NOT_FOUND", $"Mutation plan '{fingerprint}' was not found.", "Run s2mod plan or build using the original recipe.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var plan = JsonDefaults.Deserialize<MutationPlan>(bytes, "Mutation plan");
        if (plan.Fingerprint != fingerprint)
        {
            throw Errors.Verification("PLAN_FINGERPRINT_DRIFT", "Stored plan content does not identify the requested fingerprint.", "Discard this workspace and recreate the plan from immutable input.");
        }

        return plan;
    }

    public async Task<BuildPublicationResult> PublishBuildAsync(string projectRoot, BuildPublication publication, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(projectRoot);
        EnsureSafeSegment(publication.BuildId, "build id");
        var finalBuildRoot = ResolveInside(root, $"builds/{publication.BuildId}");
        if (Directory.Exists(finalBuildRoot))
        {
            var existing = await LoadBuildAsync(root, publication.BuildId, cancellationToken).ConfigureAwait(false);
            var expectedLogicalPath = StableIdentity.NormalizePath(publication.Candidate.LogicalPath);
            if (existing.Build.ContentHash == publication.Candidate.Snapshot.Artifact.ContentHash
                && existing.Build.PlanFingerprint == publication.Plan.Fingerprint
                && existing.Build.Size == publication.Candidate.Content.Length
                && string.Equals(existing.Build.LogicalPath, expectedLogicalPath, StringComparison.Ordinal))
            {
                var existingEvidence = await LoadPublishedEvidenceAsync(root, finalBuildRoot, existing.Build, cancellationToken).ConfigureAwait(false);
                return new BuildPublicationResult(existing.Build, existingEvidence.Json, existingEvidence.Markdown);
            }

            throw Errors.Verification("BUILD_ID_COLLISION", $"Build id '{publication.BuildId}' already identifies different content.", "Do not overwrite it; investigate deterministic hashing.");
        }

        var temporaryRoot = ResolveInside(root, $"temp/publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        var finalReportJson = ResolveInside(root, $"reports/{publication.BuildId}.json");
        var finalReportMarkdown = ResolveInside(root, $"reports/{publication.BuildId}.md");
        var movedReportJson = false;
        var movedReportMarkdown = false;
        var evidenceJsonBytes = new UTF8Encoding(false).GetBytes(publication.EvidenceJson);
        var evidenceMarkdownBytes = new UTF8Encoding(false).GetBytes(publication.EvidenceMarkdown);
        if (evidenceJsonBytes.LongLength > MaximumEvidenceBytes || evidenceMarkdownBytes.LongLength > MaximumEvidenceBytes)
        {
            throw Errors.Verification("BUILD_EVIDENCE_SIZE_UNSUPPORTED", "Build evidence exceeds the 16 MiB per-file publication limit.", "Reduce diagnostic output before publishing the build.");
        }

        try
        {
            if (File.Exists(finalReportJson)
                || Directory.Exists(finalReportJson)
                || File.Exists(finalReportMarkdown)
                || Directory.Exists(finalReportMarkdown))
            {
                throw Errors.Verification("BUILD_REPORT_COLLISION", $"Report paths for build '{publication.BuildId}' already exist without a published build.", "Inspect and remove only the orphaned report paths before retrying the immutable build.");
            }

            var temporaryBuildRoot = ResolveInside(temporaryRoot, "build");
            var contentRoot = ResolveInside(temporaryBuildRoot, "content");
            Directory.CreateDirectory(contentRoot);
            var logicalPath = StableIdentity.NormalizePath(publication.Candidate.LogicalPath);
            var contentPath = ResolveInside(contentRoot, logicalPath);
            Directory.CreateDirectory(Path.GetDirectoryName(contentPath)!);
            await File.WriteAllBytesAsync(contentPath, publication.Candidate.Content.ToArray(), cancellationToken).ConfigureAwait(false);

            var hash = await ComputeFileHashAsync(contentPath, cancellationToken).ConfigureAwait(false);
            if (hash != publication.Candidate.Snapshot.Artifact.ContentHash)
            {
                throw Errors.Verification("PUBLISHED_CONTENT_HASH_MISMATCH", "Temporary build bytes changed before publication.", "Reject the build and inspect storage integrity.");
            }

            var build = new PublishedBuild(
                publication.BuildId,
                logicalPath,
                hash,
                publication.Candidate.Content.Length,
                publication.Plan.Fingerprint,
                ContentHash.Compute(evidenceJsonBytes),
                ContentHash.Compute(evidenceMarkdownBytes));
            await File.WriteAllBytesAsync(ResolveInside(temporaryBuildRoot, "build.s2mod.json"), JsonDefaults.SerializeToUtf8(build), cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(ResolveInside(temporaryBuildRoot, "evidence.json"), evidenceJsonBytes, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(ResolveInside(temporaryBuildRoot, "evidence.md"), evidenceMarkdownBytes, cancellationToken).ConfigureAwait(false);
            var temporaryReportJson = ResolveInside(temporaryRoot, "report.json");
            var temporaryReportMarkdown = ResolveInside(temporaryRoot, "report.md");
            await File.WriteAllBytesAsync(temporaryReportJson, evidenceJsonBytes, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(temporaryReportMarkdown, evidenceMarkdownBytes, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(Path.GetDirectoryName(finalBuildRoot)!);
            Directory.CreateDirectory(Path.GetDirectoryName(finalReportJson)!);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryReportJson, finalReportJson, overwrite: false);
            movedReportJson = true;
            File.Move(temporaryReportMarkdown, finalReportMarkdown, overwrite: false);
            movedReportMarkdown = true;
            Directory.Move(temporaryBuildRoot, finalBuildRoot);
            return new BuildPublicationResult(build, publication.EvidenceJson, publication.EvidenceMarkdown);
        }
        catch
        {
            if (movedReportMarkdown && File.Exists(finalReportMarkdown))
            {
                File.Delete(finalReportMarkdown);
            }

            if (movedReportJson && File.Exists(finalReportJson))
            {
                File.Delete(finalReportJson);
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    public async Task<(PublishedBuild Build, ArtifactContent Content)> LoadBuildAsync(string projectRoot, string buildId, CancellationToken cancellationToken = default)
    {
        EnsureSafeSegment(buildId, "build id");
        var root = Path.GetFullPath(projectRoot);
        var buildRoot = ResolveInside(root, $"builds/{buildId}");
        var manifestPath = ResolveInside(buildRoot, "build.s2mod.json");
        if (!File.Exists(manifestPath))
        {
            throw Errors.Input("BUILD_NOT_FOUND", $"Build '{buildId}' does not exist.", "List the workspace builds and provide an existing id.");
        }

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var build = JsonDefaults.Deserialize<PublishedBuild>(manifestBytes, "Build manifest");
        if (!string.Equals(build.BuildId, buildId, StringComparison.Ordinal))
        {
            throw Errors.Verification("BUILD_MANIFEST_ID_MISMATCH", "Build directory and manifest IDs differ.", "Do not use this build; recreate it from immutable input.");
        }

        var contentPath = ResolveInside(ResolveInside(buildRoot, "content"), build.LogicalPath);
        var content = await LoadVerifiedArtifactAsync(contentPath, build.LogicalPath, build.ContentHash, build.Size, cancellationToken).ConfigureAwait(false);
        _ = await LoadPublishedEvidenceAsync(root, buildRoot, build, cancellationToken).ConfigureAwait(false);
        return (build, content);
    }
}
