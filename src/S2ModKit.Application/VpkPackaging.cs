using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed class VpkPackagingApplication : IVpkPackagingApplication
{
    private readonly IProjectWorkspace projectWorkspace;
    private readonly IVpkPackageWorkspace packageWorkspace;
    private readonly IVpkCandidateBuilder builder;
    private readonly IVpkExternalVerifier externalVerifier;
    private readonly IVpkPackageExporter exporter;
    private readonly IVpkPackageReportRenderer reports;
    private readonly IClock clock;

    public VpkPackagingApplication(
        IProjectWorkspace projectWorkspace,
        IVpkPackageWorkspace packageWorkspace,
        IVpkCandidateBuilder builder,
        IVpkExternalVerifier externalVerifier,
        IVpkPackageExporter exporter,
        IVpkPackageReportRenderer reports,
        IClock clock)
    {
        this.projectWorkspace = projectWorkspace;
        this.packageWorkspace = packageWorkspace;
        this.builder = builder;
        this.externalVerifier = externalVerifier;
        this.exporter = exporter;
        this.reports = reports;
        this.clock = clock;
    }

    public async Task<VpkPackageExportResult> ExportAsync(
        string projectRoot,
        string packageId,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var published = await packageWorkspace
            .LoadPackageAsync(projectRoot, packageId, cancellationToken)
            .ConfigureAwait(false);
        return await exporter
            .ExportAsync(
                published.Package.PackageId,
                published.PackagePath,
                published.Package.ContentHash,
                outputPath,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<VpkPackageRunResult> CreateAsync(
        string projectRoot,
        string buildId,
        string sourceVpkPath,
        ContentHash expectedSourceVpkHash,
        bool requireExternalVerifier,
        CancellationToken cancellationToken = default)
    {
        var project = await projectWorkspace.LoadProjectAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        var input = await projectWorkspace.LoadInputAsync(projectRoot, project, cancellationToken).ConfigureAwait(false);
        var (build, replacement) = await projectWorkspace.LoadBuildAsync(projectRoot, buildId, cancellationToken).ConfigureAwait(false);
        RequireMatchingBuild(input, build, replacement);

        VpkBuiltCandidate? candidate = null;
        try
        {
            candidate = await builder.BuildAsync(
                new VpkPackageBuildRequest(
                    projectRoot,
                    sourceVpkPath,
                    expectedSourceVpkHash,
                    input.LogicalPath,
                    input.ContentHash,
                    replacement),
                cancellationToken).ConfigureAwait(false);
            var external = await externalVerifier.VerifyAsync(candidate.TemporaryPath, input.LogicalPath, cancellationToken).ConfigureAwait(false);
            RequireExternalBoundary(external, requireExternalVerifier);

            var packageId = $"vpk-{candidate.Comparison.OutputArchive.ContentHash.Value[..20]}";
            var boundaries = candidate.Comparison.Boundaries
                .Concat([external, new BoundaryEvidence("runtime", "untested", "The package has not been installed or observed in live Deadlock.")])
                .ToArray();
            var evidence = CreateEvidence("package.create", packageId, packageId, buildId, candidate.Comparison, boundaries, candidate.AdapterName, candidate.AdapterVersion);
            var publication = new VpkPackagePublication(
                packageId,
                buildId,
                "replace_source",
                candidate.Comparison.SourceArchive.Path,
                candidate.Comparison.SourceArchive.ContentHash,
                candidate.Comparison.ReplacedEntry.LogicalPath,
                candidate.Comparison.ReplacedEntry.SourceContentHash,
                candidate.Comparison.ReplacedEntry.OutputContentHash,
                candidate.TemporaryPath,
                candidate.Comparison.OutputArchive.ContentHash,
                candidate.Comparison.OutputArchive.Size,
                reports.RenderJson(evidence),
                reports.RenderMarkdown(evidence));
            var published = await packageWorkspace.PublishPackageAsync(projectRoot, publication, cancellationToken).ConfigureAwait(false);
            var canonicalEvidence = JsonDefaults.Deserialize<VpkPackageEvidence>(Encoding.UTF8.GetBytes(published.EvidenceJson), "Published VPK evidence");
            return new VpkPackageRunResult(published.Package, canonicalEvidence);
        }
        finally
        {
            if (candidate is not null)
            {
                await builder.DiscardAsync(candidate, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public async Task<VpkPackageRunResult> CreateMinimalAsync(
        string projectRoot,
        string buildId,
        bool requireExternalVerifier,
        CancellationToken cancellationToken = default)
    {
        var project = await projectWorkspace.LoadProjectAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        var input = await projectWorkspace.LoadInputAsync(projectRoot, project, cancellationToken).ConfigureAwait(false);
        var (build, replacement) = await projectWorkspace.LoadBuildAsync(projectRoot, buildId, cancellationToken).ConfigureAwait(false);
        RequireMatchingBuild(input, build, replacement);

        VpkBuiltMinimalCandidate? candidate = null;
        try
        {
            candidate = await builder.BuildMinimalAsync(
                new VpkMinimalPackageBuildRequest(projectRoot, replacement),
                cancellationToken).ConfigureAwait(false);
            var external = await externalVerifier.VerifyAsync(candidate.TemporaryPath, input.LogicalPath, cancellationToken).ConfigureAwait(false);
            RequireExternalBoundary(external, requireExternalVerifier);

            var packageId = $"vpk-{candidate.Comparison.OutputArchive.ContentHash.Value[..20]}";
            var boundaries = candidate.Comparison.Boundaries
                .Concat([external, new BoundaryEvidence("runtime", "untested", "The package has not been installed or observed in live Deadlock.")])
                .ToArray();
            var evidence = CreateMinimalEvidence(
                "package.create-minimal",
                packageId,
                packageId,
                buildId,
                candidate.Comparison,
                boundaries,
                candidate.AdapterName,
                candidate.AdapterVersion);
            var publication = new VpkPackagePublication(
                packageId,
                buildId,
                "minimal",
                null,
                null,
                candidate.Comparison.PackagedEntry.LogicalPath,
                null,
                candidate.Comparison.PackagedEntry.ContentHash,
                candidate.TemporaryPath,
                candidate.Comparison.OutputArchive.ContentHash,
                candidate.Comparison.OutputArchive.Size,
                reports.RenderJson(evidence),
                reports.RenderMarkdown(evidence));
            var published = await packageWorkspace.PublishPackageAsync(projectRoot, publication, cancellationToken).ConfigureAwait(false);
            var canonicalEvidence = JsonDefaults.Deserialize<VpkPackageEvidence>(Encoding.UTF8.GetBytes(published.EvidenceJson), "Published minimal VPK evidence");
            return new VpkPackageRunResult(published.Package, canonicalEvidence);
        }
        finally
        {
            if (candidate is not null)
            {
                await builder.DiscardAsync(candidate, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public async Task<VpkPackageRunResult> VerifyAsync(
        string projectRoot,
        string packageId,
        bool requireExternalVerifier,
        CancellationToken cancellationToken = default)
    {
        var loaded = await packageWorkspace.LoadPackageAsync(projectRoot, packageId, cancellationToken).ConfigureAwait(false);
        var package = loaded.Package;
        var external = await externalVerifier.VerifyAsync(loaded.PackagePath, package.EntryLogicalPath, cancellationToken).ConfigureAwait(false);
        RequireExternalBoundary(external, requireExternalVerifier);
        var reportId = $"{packageId}-verify";
        VpkPackageEvidence evidence;
        if (string.Equals(package.Mode, "minimal", StringComparison.Ordinal))
        {
            var comparison = await builder.VerifyMinimalAsync(
                new VpkMinimalPackageVerificationRequest(
                    loaded.PackagePath,
                    package.ContentHash,
                    package.EntryLogicalPath,
                    package.ReplacementEntryHash),
                cancellationToken).ConfigureAwait(false);
            var boundaries = comparison.Boundaries
                .Concat([external, new BoundaryEvidence("runtime", "untested", "The package has not been observed in live Deadlock.")])
                .ToArray();
            evidence = CreateMinimalEvidence("package.verify", reportId, packageId, package.BuildId, comparison, boundaries, builder.AdapterName, builder.AdapterVersion);
        }
        else
        {
            if (package.SourceVpkPath is null || package.SourceVpkHash is null || package.SourceEntryHash is null)
            {
                throw Errors.Verification("VPK_PACKAGE_SOURCE_EVIDENCE_MISSING", "A replace-source package has incomplete source evidence.", "Recreate the package from its immutable source archive.");
            }

            var comparison = await builder.VerifyAsync(
                new VpkPackageVerificationRequest(
                    package.SourceVpkPath,
                    loaded.PackagePath,
                    package.SourceVpkHash.Value,
                    package.ContentHash,
                    package.EntryLogicalPath,
                    package.SourceEntryHash.Value,
                    package.ReplacementEntryHash),
                cancellationToken).ConfigureAwait(false);
            var boundaries = comparison.Boundaries
                .Concat([external, new BoundaryEvidence("runtime", "untested", "The package has not been observed in live Deadlock.")])
                .ToArray();
            evidence = CreateEvidence("package.verify", reportId, packageId, package.BuildId, comparison, boundaries, builder.AdapterName, builder.AdapterVersion);
        }

        await projectWorkspace.SaveEvidenceAsync(projectRoot, reportId, reports.RenderJson(evidence), reports.RenderMarkdown(evidence), cancellationToken).ConfigureAwait(false);
        return new VpkPackageRunResult(package, evidence);
    }

    private static void RequireMatchingBuild(ArtifactContent input, PublishedBuild build, ArtifactContent replacement)
    {
        if (!string.Equals(input.LogicalPath, replacement.LogicalPath, StringComparison.Ordinal)
            || build.ContentHash != replacement.ContentHash)
        {
            throw Errors.Verification(
                "VPK_BUILD_INPUT_MISMATCH",
                "The published build does not replace the project's immutable input logical path.",
                "Use a verified build produced by this project.");
        }
    }

    private VpkPackageEvidence CreateEvidence(
        string command,
        string reportId,
        string packageId,
        string buildId,
        VpkArchiveComparison comparison,
        IReadOnlyList<BoundaryEvidence> boundaries,
        string adapterName,
        string adapterVersion) =>
        new()
        {
            Mode = "replace_source",
            ReportId = reportId,
            CreatedUtc = clock.UtcNow.ToUniversalTime(),
            Command = command,
            PackageId = packageId,
            BuildId = buildId,
            SourceArchive = comparison.SourceArchive,
            OutputArchive = comparison.OutputArchive,
            ReplacedEntry = comparison.ReplacedEntry,
            PackagedEntry = new VpkPackagedEntryEvidence(
                comparison.ReplacedEntry.LogicalPath,
                comparison.ReplacedEntry.OutputContentHash,
                comparison.ReplacedEntry.OutputCrc32,
                comparison.ReplacedEntry.OutputSize),
            UnchangedEntryCount = comparison.UnchangedEntryCount,
            Boundaries = boundaries,
            ToolVersions = CreateToolVersions(adapterName, adapterVersion),
            Warnings =
            [
                "Offline archive and compiled-resource verification is not proof of live Deadlock behavior or load order.",
                "The package changes exactly one entry in the explicitly supplied source VPK; the archive's vanilla or third-party provenance is not inferred.",
            ],
        };

    private VpkPackageEvidence CreateMinimalEvidence(
        string command,
        string reportId,
        string packageId,
        string buildId,
        VpkMinimalArchiveComparison comparison,
        IReadOnlyList<BoundaryEvidence> boundaries,
        string adapterName,
        string adapterVersion) =>
        new()
        {
            Mode = "minimal",
            ReportId = reportId,
            CreatedUtc = clock.UtcNow.ToUniversalTime(),
            Command = command,
            PackageId = packageId,
            BuildId = buildId,
            OutputArchive = comparison.OutputArchive,
            PackagedEntry = comparison.PackagedEntry,
            Boundaries = boundaries,
            ToolVersions = CreateToolVersions(adapterName, adapterVersion),
            Warnings =
            [
                "Offline archive and compiled-resource verification is not proof of live Deadlock behavior or load order.",
                "The minimal package contains only the generated model entry and relies on the base game for every other runtime resource.",
            ],
        };

    private SortedDictionary<string, string> CreateToolVersions(string adapterName, string adapterVersion) =>
        new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["dotnet.runtime"] = RuntimeInformation.FrameworkDescription,
            ["operating_system"] = RuntimeInformation.OSDescription,
            ["process.architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["s2modkit"] = typeof(VpkPackagingApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            [$"vpk_adapter.{adapterName}"] = adapterVersion,
            [$"vpk_verifier.{externalVerifier.VerifierName}"] = externalVerifier.VerifierVersion,
        };

    private static void RequireExternalBoundary(BoundaryEvidence boundary, bool required)
    {
        if (boundary.Status == "failed" || (required && boundary.Status != "passed"))
        {
            throw Errors.Verification(
                "VPK_EXTERNAL_VERIFICATION_FAILED",
                required
                    ? $"Required external VPK verification did not pass: {boundary.Summary}"
                    : $"External VPK verification failed: {boundary.Summary}",
                "Do not publish or install the package; configure Source 2 Viewer and rerun verification.");
        }
    }
}

public sealed class SkippedVpkExternalVerifier : IVpkExternalVerifier
{
    public string VerifierName => "source2_viewer";

    public string VerifierVersion => "not_configured";

    public bool IsAvailable => false;

    public Task<BoundaryEvidence> VerifyAsync(string candidateVpkPath, string entryLogicalPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BoundaryEvidence("vpk_external_verifier", "skipped", "Source 2 Viewer is not configured."));
}

public sealed class MisconfiguredVpkExternalVerifier(string summary) : IVpkExternalVerifier
{
    public string VerifierName => "source2_viewer";

    public string VerifierVersion => "misconfigured";

    public bool IsAvailable => false;

    public Task<BoundaryEvidence> VerifyAsync(string candidateVpkPath, string entryLogicalPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BoundaryEvidence("vpk_external_verifier", "failed", summary));
}
