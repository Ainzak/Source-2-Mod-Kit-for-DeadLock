using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed record VpkArchiveEvidence(
    string Path,
    ContentHash ContentHash,
    long Size,
    int EntryCount);

public sealed record VpkEntryEvidence(
    string LogicalPath,
    ContentHash SourceContentHash,
    ContentHash OutputContentHash,
    uint SourceCrc32,
    uint OutputCrc32,
    long SourceSize,
    long OutputSize);

public sealed record VpkPackagedEntryEvidence(
    string LogicalPath,
    ContentHash ContentHash,
    uint Crc32,
    long Size);

public sealed record VpkPackageEvidence
{
    public int SchemaVersion { get; init; } = 2;

    public string Mode { get; init; } = "replace_source";

    public string ReportId { get; init; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; init; }

    public string Command { get; init; } = string.Empty;

    public string Status { get; init; } = "passed";

    public string ProofLevel { get; init; } = "offline_static";

    public string PackageId { get; init; } = string.Empty;

    public string BuildId { get; init; } = string.Empty;

    public VpkArchiveEvidence? SourceArchive { get; init; }

    public required VpkArchiveEvidence OutputArchive { get; init; }

    public VpkEntryEvidence? ReplacedEntry { get; init; }

    public VpkPackagedEntryEvidence? PackagedEntry { get; init; }

    public int UnchangedEntryCount { get; init; }

    public IReadOnlyList<BoundaryEvidence> Boundaries { get; init; } = [];

    public IReadOnlyDictionary<string, string> ToolVersions { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record VpkPackageBuildRequest(
    string ProjectRoot,
    string SourceVpkPath,
    ContentHash ExpectedSourceArchiveHash,
    string EntryLogicalPath,
    ContentHash ExpectedSourceEntryHash,
    ArtifactContent Replacement);

public sealed record VpkPackageVerificationRequest(
    string SourceVpkPath,
    string CandidateVpkPath,
    ContentHash ExpectedSourceArchiveHash,
    ContentHash ExpectedCandidateArchiveHash,
    string EntryLogicalPath,
    ContentHash ExpectedSourceEntryHash,
    ContentHash ExpectedReplacementEntryHash);

public sealed record VpkMinimalPackageBuildRequest(
    string ProjectRoot,
    ArtifactContent Entry);

public sealed record VpkMinimalPackageVerificationRequest(
    string CandidateVpkPath,
    ContentHash ExpectedCandidateArchiveHash,
    string EntryLogicalPath,
    ContentHash ExpectedEntryHash);

public sealed record VpkArchiveComparison(
    VpkArchiveEvidence SourceArchive,
    VpkArchiveEvidence OutputArchive,
    VpkEntryEvidence ReplacedEntry,
    int UnchangedEntryCount,
    IReadOnlyList<BoundaryEvidence> Boundaries);

public sealed record VpkBuiltCandidate(
    string TemporaryPath,
    VpkArchiveComparison Comparison,
    string AdapterName,
    string AdapterVersion);

public sealed record VpkMinimalArchiveComparison(
    VpkArchiveEvidence OutputArchive,
    VpkPackagedEntryEvidence PackagedEntry,
    IReadOnlyList<BoundaryEvidence> Boundaries);

public sealed record VpkBuiltMinimalCandidate(
    string TemporaryPath,
    VpkMinimalArchiveComparison Comparison,
    string AdapterName,
    string AdapterVersion);

public interface IVpkCandidateBuilder
{
    string AdapterName { get; }

    string AdapterVersion { get; }

    Task<VpkBuiltCandidate> BuildAsync(VpkPackageBuildRequest request, CancellationToken cancellationToken = default);

    Task<VpkArchiveComparison> VerifyAsync(VpkPackageVerificationRequest request, CancellationToken cancellationToken = default);

    Task<VpkBuiltMinimalCandidate> BuildMinimalAsync(VpkMinimalPackageBuildRequest request, CancellationToken cancellationToken = default);

    Task<VpkMinimalArchiveComparison> VerifyMinimalAsync(VpkMinimalPackageVerificationRequest request, CancellationToken cancellationToken = default);

    Task DiscardAsync(VpkBuiltCandidate candidate, CancellationToken cancellationToken = default);

    Task DiscardAsync(VpkBuiltMinimalCandidate candidate, CancellationToken cancellationToken = default);
}

public interface IVpkExternalVerifier
{
    string VerifierName { get; }

    string VerifierVersion { get; }

    bool IsAvailable { get; }

    Task<BoundaryEvidence> VerifyAsync(
        string candidateVpkPath,
        string entryLogicalPath,
        CancellationToken cancellationToken = default);
}

public sealed record VpkPackagePublication(
    string PackageId,
    string BuildId,
    string Mode,
    string? SourceVpkPath,
    ContentHash? SourceVpkHash,
    string EntryLogicalPath,
    ContentHash? SourceEntryHash,
    ContentHash ReplacementEntryHash,
    string CandidateTemporaryPath,
    ContentHash CandidateHash,
    long CandidateSize,
    string EvidenceJson,
    string EvidenceMarkdown);

public sealed record PublishedVpkPackage(
    string PackageId,
    string BuildId,
    string? SourceVpkPath,
    ContentHash? SourceVpkHash,
    string EntryLogicalPath,
    ContentHash? SourceEntryHash,
    ContentHash ReplacementEntryHash,
    string PackageRelativePath,
    ContentHash ContentHash,
    long Size,
    ContentHash EvidenceJsonHash,
    ContentHash EvidenceMarkdownHash)
{
    public int SchemaVersion { get; init; } = 1;

    public string Mode { get; init; } = "replace_source";
}

public sealed record VpkPackagePublicationResult(
    PublishedVpkPackage Package,
    string PackagePath,
    string EvidenceJson,
    string EvidenceMarkdown);

public interface IVpkPackageWorkspace
{
    Task<VpkPackagePublicationResult> PublishPackageAsync(
        string projectRoot,
        VpkPackagePublication publication,
        CancellationToken cancellationToken = default);

    Task<VpkPackagePublicationResult> LoadPackageAsync(
        string projectRoot,
        string packageId,
        CancellationToken cancellationToken = default);
}

public interface IVpkPackageReportRenderer
{
    string RenderJson(VpkPackageEvidence report);

    string RenderMarkdown(VpkPackageEvidence report);
}

public sealed record VpkPackageRunResult(PublishedVpkPackage Package, VpkPackageEvidence Evidence);

public sealed record VpkPackageExportResult(
    string PackageId,
    string OutputPath,
    ContentHash ContentHash,
    long Size);

public interface IVpkPackageExporter
{
    Task<VpkPackageExportResult> ExportAsync(
        string packageId,
        string sourcePath,
        ContentHash expectedContentHash,
        string outputPath,
        CancellationToken cancellationToken = default);
}

public interface IVpkPackagingApplication
{
    Task<VpkPackageRunResult> CreateAsync(
        string projectRoot,
        string buildId,
        string sourceVpkPath,
        ContentHash expectedSourceVpkHash,
        bool requireExternalVerifier,
        CancellationToken cancellationToken = default);

    Task<VpkPackageRunResult> CreateMinimalAsync(
        string projectRoot,
        string buildId,
        bool requireExternalVerifier,
        CancellationToken cancellationToken = default);

    Task<VpkPackageRunResult> VerifyAsync(
        string projectRoot,
        string packageId,
        bool requireExternalVerifier,
        CancellationToken cancellationToken = default);

    Task<VpkPackageExportResult> ExportAsync(
        string projectRoot,
        string packageId,
        string outputPath,
        CancellationToken cancellationToken = default);
}
