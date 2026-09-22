using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed record ArtifactContent(
    string LogicalPath,
    ContentHash ContentHash,
    ReadOnlyMemory<byte> Bytes);

public sealed record ProjectCreationRequest(
    string ProjectRoot,
    string InputPath,
    string ResourceRoot,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<string>? RuntimeResourceRoots = null);

public sealed record VpkProjectCreationRequest(
    string ProjectRoot,
    string BaseVpkPath,
    string InputLogicalPath,
    DateTimeOffset CreatedUtc,
    ContentHash? ExpectedDirectoryHash = null);

public sealed record ResourceCatalogDescriptor(
    string SourceKind,
    string SourceIdentity,
    string ProvenanceKind,
    int Priority,
    string VerificationMode);

public sealed record ResourceCatalogArtifact(
    ArtifactContent Content,
    string SourceIdentity);

public sealed record ResourceCatalogEntry(
    string LogicalPath,
    string SourceIdentity,
    long Size);

public sealed record ProjectSourceArtifact(
    ArtifactContent Content,
    string SourceIdentity,
    string ProvenanceKind,
    string SourceKind,
    string CatalogIdentity,
    string VerificationMode);

public sealed record ProjectResourceSource(
    string InputLogicalPath,
    IResourceCatalog InputCatalog,
    IReadOnlyList<IResourceCatalog> DependencyCatalogs,
    IReadOnlyList<string> ResourceRoots,
    IReadOnlyList<string> RuntimeResourceRoots) : IDisposable
{
    public void Dispose()
    {
        foreach (var catalog in DependencyCatalogs
            .Append(InputCatalog)
            .Distinct(ReferenceEqualityComparer.Instance)
            .OfType<IDisposable>())
        {
            catalog.Dispose();
        }
    }
}

public sealed record ProjectPublicationRequest(
    string ProjectRoot,
    DateTimeOffset CreatedUtc,
    ProjectSourceArtifact Input,
    IReadOnlyList<ProjectSourceArtifact> Dependencies,
    IReadOnlyList<ProjectDependencyEdge> DependencyEdges,
    IReadOnlyList<string> ResourceRoots,
    IReadOnlyList<string> RuntimeResourceRoots);

public sealed record ResourceDependency(
    string LogicalPath,
    string ReferenceId);

public sealed record BuildPublication(
    string BuildId,
    MutationPlan Plan,
    RewriteCandidate Candidate,
    string EvidenceJson,
    string EvidenceMarkdown);

public sealed record PublishedBuild(
    string BuildId,
    string LogicalPath,
    ContentHash ContentHash,
    long Size,
    ContentHash PlanFingerprint,
    ContentHash EvidenceJsonHash,
    ContentHash EvidenceMarkdownHash);

public sealed record BuildPublicationResult(
    PublishedBuild Build,
    string EvidenceJson,
    string EvidenceMarkdown);

public interface IProjectWorkspace
{
    Task<ProjectManifest> PublishProjectAsync(
        ProjectPublicationRequest request,
        CancellationToken cancellationToken = default);

    Task<ProjectManifest> LoadProjectAsync(string projectRoot, CancellationToken cancellationToken = default);

    Task<ArtifactContent> LoadInputAsync(string projectRoot, ProjectManifest project, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArtifactContent>> LoadDependenciesAsync(
        string projectRoot,
        ProjectManifest project,
        CancellationToken cancellationToken = default);

    Task SavePlanAsync(string projectRoot, MutationPlan plan, CancellationToken cancellationToken = default);

    Task<MutationPlan> LoadPlanAsync(string projectRoot, ContentHash fingerprint, CancellationToken cancellationToken = default);

    Task<BuildPublicationResult> PublishBuildAsync(string projectRoot, BuildPublication publication, CancellationToken cancellationToken = default);

    Task<(PublishedBuild Build, ArtifactContent Content)> LoadBuildAsync(string projectRoot, string buildId, CancellationToken cancellationToken = default);

    Task SaveEvidenceAsync(string projectRoot, string reportId, string json, string markdown, CancellationToken cancellationToken = default);
}

public interface IResourceCatalog
{
    ResourceCatalogDescriptor Descriptor { get; }

    Task<ResourceCatalogEntry?> FindEntryAsync(
        string logicalPath,
        CancellationToken cancellationToken = default);

    Task<ResourceCatalogArtifact?> TryOpenAsync(
        string logicalPath,
        CancellationToken cancellationToken = default);
}

public interface IResourceCatalogInventory : IResourceCatalog
{
    ContentHash SourceContentHash { get; }

    Task<IReadOnlyList<ResourceCatalogEntry>> ListEntriesAsync(
        CancellationToken cancellationToken = default);
}

public interface IResourceCatalogInventoryFactory
{
    IResourceCatalogInventory OpenReadOnly(string baseVpkPath);
}

public interface IProjectResourceSourceFactory
{
    ProjectResourceSource Create(ProjectCreationRequest request);
}

public interface IVpkProjectResourceSourceFactory
{
    ProjectResourceSource Create(VpkProjectCreationRequest request);
}

public sealed record PlanRunResult(MutationPlan Plan, EvidenceReport Evidence);

public sealed record InspectionRunResult(ProjectManifest Project, ModelSnapshot Model);

public sealed record BuildRunResult(PublishedBuild Build, EvidenceReport Evidence);

public sealed record VerifyRunResult(PublishedBuild Build, EvidenceReport Evidence);

public sealed record TransformPlanningRequest(
    ArtifactContent Input,
    ModelSnapshot Model,
    TransformComponentOperation Operation,
    IReadOnlyList<SelectedDrawCall> SelectedDrawCalls);

public sealed record TransformPlanningResult(
    IReadOnlyList<PlannedGeometryTarget> GeometryTargets,
    IReadOnlyList<PlannedDistanceFieldTarget> DistanceFieldTargets,
    IReadOnlyList<PlannedTargetBlock> TargetBlocks)
{
    public PlannedCoupledTransformTarget? CoupledTransformTarget { get; init; }

    public PlannedAffineTransformTarget? AffineTransformTarget { get; init; }
}

public sealed record AffineSelectionBoundsEvidence(
    int Lod,
    GeometryBounds Bounds,
    ContentHash VertexSetHash);

public sealed record AffineBindMatrix(
    float M11, float M12, float M13, float M14,
    float M21, float M22, float M23, float M24,
    float M31, float M32, float M33, float M34);

public sealed record AffineBoneBindEvidence(
    int Lod,
    string SkeletonIdentity,
    string BoneName,
    bool InfluencesSelection,
    ContentHash InverseBindPoseHash,
    AffineBindMatrix InverseBindPose);

public sealed record TypedPivotResolutionRequest(
    TransformPivot Pivot,
    IReadOnlyList<int> ExpectedLods,
    IReadOnlyList<AffineSelectionBoundsEvidence> SelectionBounds,
    IReadOnlyList<AffineBoneBindEvidence> BoneBindings);

public sealed record TypedFrameResolutionRequest(
    TransformFrame Frame,
    IReadOnlyList<int> ExpectedLods,
    IReadOnlyList<AffineBoneBindEvidence> BoneBindings);

public interface IAffineEvidenceResolver
{
    ResolvedTransformPivot ResolvePivot(TypedPivotResolutionRequest request);

    ResolvedTransformFrame ResolveFrame(TypedFrameResolutionRequest request);
}

public sealed record ComponentCapabilitySelection(
    string SelectionId,
    string CandidateKind,
    IReadOnlyList<string> MaterialPaths,
    IReadOnlyList<SelectedDrawCall> SelectedDrawCalls);

public sealed record ComponentCapabilityAnalysisRequest(
    ArtifactContent Input,
    ModelSnapshot Model,
    IReadOnlyList<ComponentCapabilitySelection> Selections);

public sealed record ComponentCapabilityAnalysis(
    string SelectionId,
    string OperationKind,
    int OperationVersion,
    string Availability,
    IReadOnlyList<ComponentCapabilityReason> Reasons,
    IReadOnlyList<ComponentGeometryLodFacts> GeometryByLod);

public interface IS2ModKitApplication
{
    Task<ProjectManifest> CreateProjectAsync(string projectRoot, string inputPath, string resourceRoot, CancellationToken cancellationToken = default);

    Task<ProjectManifest> CreateProjectAsync(
        string projectRoot,
        string inputPath,
        string resourceRoot,
        IReadOnlyList<string> runtimeResourceRoots,
        CancellationToken cancellationToken = default);

    Task<ProjectManifest> CreateVpkProjectAsync(
        string projectRoot,
        string baseVpkPath,
        string inputLogicalPath,
        ContentHash? expectedDirectoryHash = null,
        CancellationToken cancellationToken = default);

    Task<InspectionRunResult> InspectAsync(string projectRoot, CancellationToken cancellationToken = default);

    Task<ComponentDiscoveryResultV2> DiscoverComponentsAsync(
        string projectRoot,
        CancellationToken cancellationToken = default);

    Task<RecipeScaffoldResult> ScaffoldRecipeAsync(
        string projectRoot,
        RecipeScaffoldRequest request,
        CancellationToken cancellationToken = default);

    Task<PlanRunResult> PlanAsync(string projectRoot, RecipeDocument recipe, CancellationToken cancellationToken = default);

    Task<BuildRunResult> BuildAsync(string projectRoot, RecipeDocument recipe, CancellationToken cancellationToken = default);

    Task<VerifyRunResult> VerifyAsync(string projectRoot, string buildId, CancellationToken cancellationToken = default);
}

public interface IModelInspector
{
    string AdapterName { get; }

    string AdapterVersion { get; }

    IReadOnlyDictionary<string, string> ComponentVersions { get; }

    bool CanInspect(ArtifactContent artifact);

    Task<ModelSnapshot> InspectAsync(ArtifactContent artifact, CancellationToken cancellationToken = default);
}

public interface IResourceDependencyReader
{
    bool CanReadDependencies(ArtifactContent artifact);

    Task<IReadOnlyList<ResourceDependency>> ReadDependenciesAsync(
        ArtifactContent artifact,
        CancellationToken cancellationToken = default);
}

public interface IModelRewriter
{
    bool CanRewrite(ModelSnapshot model, MutationPlan plan);

    Task<RewriteCandidate> RewriteAsync(
        ArtifactContent input,
        ModelSnapshot model,
        MutationPlan plan,
        CancellationToken cancellationToken = default);
}

public interface ITransformOperationPlanner
{
    TransformPlanningResult PlanTransform(TransformPlanningRequest request);
}

public interface IComponentCapabilityAnalyzer
{
    string AnalyzerName { get; }

    string AnalyzerVersion { get; }

    IReadOnlyDictionary<string, string> ComponentVersions { get; }

    bool CanAnalyze(ArtifactContent input, ModelSnapshot model);

    Task<IReadOnlyList<ComponentCapabilityAnalysis>> AnalyzeAsync(
        ComponentCapabilityAnalysisRequest request,
        CancellationToken cancellationToken = default);
}

public interface IRecipeDocumentWriter
{
    Task<string> WriteNewAsync(
        string outputPath,
        ReadOnlyMemory<byte> canonicalJson,
        CancellationToken cancellationToken = default);
}

public interface IExternalVerifier
{
    string VerifierName { get; }

    string VerifierVersion { get; }

    bool IsAvailable { get; }

    Task<BoundaryEvidence> VerifyAsync(RewriteCandidate candidate, CancellationToken cancellationToken = default);
}

public interface IReportRenderer
{
    string RenderJson(EvidenceReport report);

    string RenderMarkdown(EvidenceReport report);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class SkippedExternalVerifier : IExternalVerifier
{
    public string VerifierName => "source2_viewer";

    public string VerifierVersion => "not_configured";

    public bool IsAvailable => false;

    public Task<BoundaryEvidence> VerifyAsync(RewriteCandidate candidate, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BoundaryEvidence("external_verifier", "skipped", "No external verifier is configured; internal verification remains authoritative."));
}

public sealed class MisconfiguredExternalVerifier(string summary) : IExternalVerifier
{
    public string VerifierName => "source2_viewer";

    public string VerifierVersion => "misconfigured";

    public bool IsAvailable => false;

    public Task<BoundaryEvidence> VerifyAsync(RewriteCandidate candidate, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BoundaryEvidence("external_verifier", "failed", summary));
}
