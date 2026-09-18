using S2ModKit.Domain;

namespace S2ModKit.Application;

public static class GuidedWorkflowContract
{
    public const int SchemaVersion = 1;

    public const string BaseVpkSource = "base_vpk";
    public const string ModVpkSource = "mod_vpk";
    public const string CompiledModelSource = "compiled_model";

    public const string ActiveStatus = "active";
    public const string PausedStatus = "paused";
    public const string CompleteStatus = "complete";

    public const string SourceSelectionStep = "source_selection";
    public const string HeroSelectionStep = "hero_selection";
    public const string ResourceSelectionStep = "resource_selection";
    public const string ComponentSelectionStep = "component_selection";
    public const string ActionSelectionStep = "action_selection";
    public const string ReviewStep = "review";
    public const string OutputSelectionStep = "output_selection";
    public const string InstallSelectionStep = "install_selection";
    public const string RollbackSelectionStep = "rollback_selection";
    public const string CompleteStep = "complete";

    public const string PlanOnlyOutput = "plan_only";
    public const string BuildOnlyOutput = "build_only";
    public const string MinimalPackageOutput = "minimal_package";
    public const string ExportPackageOutput = "export_package";

    public static bool IsSourceKind(string value) =>
        value is BaseVpkSource or ModVpkSource or CompiledModelSource;

    public static bool IsStep(string value) =>
        value is SourceSelectionStep
            or HeroSelectionStep
            or ResourceSelectionStep
            or ComponentSelectionStep
            or ActionSelectionStep
            or ReviewStep
            or OutputSelectionStep
            or InstallSelectionStep
            or RollbackSelectionStep
            or CompleteStep;

    public static bool IsOutputChoice(string value) =>
        value is PlanOnlyOutput or BuildOnlyOutput or MinimalPackageOutput or ExportPackageOutput;
}

public sealed record GuidedSourceOption(
    string SourceId,
    string Kind,
    string DisplayName,
    string Path);

public sealed record GuidedWorkflowSession
{
    public int SchemaVersion { get; init; } = GuidedWorkflowContract.SchemaVersion;

    public string SessionId { get; init; } = string.Empty;

    public string Status { get; init; } = GuidedWorkflowContract.ActiveStatus;

    public string Step { get; init; } = GuidedWorkflowContract.SourceSelectionStep;

    public string CataloguePath { get; init; } = string.Empty;

    public bool Expert { get; init; }

    public IReadOnlyList<GuidedSourceOption> Sources { get; init; } = [];

    public string? SelectedSourceId { get; init; }

    public string? SelectedHeroId { get; init; }

    public string? SelectedResourceId { get; init; }

    public string? SelectedLogicalPath { get; init; }

    public string? ProjectRoot { get; init; }

    public bool ProjectReady { get; init; }

    public string? SelectedComponentId { get; init; }

    public string? SelectedComponentLabel { get; init; }

    public string? SelectedIntent { get; init; }

    public string? SelectedOperationKind { get; init; }

    public int? SelectedOperationVersion { get; init; }

    public float? UniformScale { get; init; }

    public float? TranslationX { get; init; }

    public float? TranslationY { get; init; }

    public float? TranslationZ { get; init; }

    public float? MaximumVertexDisplacement { get; init; }

    public float? MaximumCollisionDisplacement { get; init; }

    public string? RecipePath { get; init; }

    public ContentHash? RecipeContentHash { get; init; }

    public ContentHash? PlanFingerprint { get; init; }

    public int? PlannedDrawCallCount { get; init; }

    public int? PlannedLodCount { get; init; }

    public int? PlannedTargetBlockCount { get; init; }

    public int? PlannedVertexCount { get; init; }

    public bool? PlannedCoupledCollision { get; init; }

    public string? OutputChoice { get; init; }

    public string? BuildId { get; init; }

    public ContentHash? BuildContentHash { get; init; }

    public string? PackageId { get; init; }

    public ContentHash? PackageContentHash { get; init; }

    public string? ExportPath { get; init; }

    public ContentHash? ExportContentHash { get; init; }

    public string? AddonsRoot { get; init; }

    public string? InstallationId { get; init; }

    public string? InstallationTargetFileName { get; init; }

    public int? InstallationSlot { get; init; }

    public string? InstallationStatus { get; init; }
}

public sealed record GuidedResourceChoice(
    string ResourceId,
    string DisplayName,
    string Role,
    string LogicalPath,
    string VerificationStatus,
    IReadOnlyList<string> CandidateLogicalPaths)
{
    public bool Selectable => string.Equals(
        VerificationStatus,
        HeroCatalogueVerificationContract.ResourceVerified,
        StringComparison.Ordinal);
}

public sealed record GuidedHeroChoice(
    string HeroId,
    string DisplayName,
    string RosterStatus,
    IReadOnlyList<GuidedResourceChoice> Resources)
{
    public bool Selectable => Resources.Any(resource => resource.Selectable);
}

public sealed record GuidedCatalogueSelection(
    string CatalogueId,
    string Revision,
    string SourceStatus,
    IReadOnlyList<GuidedHeroChoice> Heroes);

public sealed record GuidedActionChoice(
    string ActionId,
    string DisplayName,
    string Intent,
    string OperationKind,
    int OperationVersion,
    bool RequiresScale,
    bool RequiresTranslation,
    bool RequiresCollisionLimit);

public sealed record GuidedComponentChoice(
    string CandidateId,
    string DisplayLabel,
    string Kind,
    IReadOnlyList<int> Lods,
    IReadOnlyList<GuidedActionChoice> Actions);

public sealed record GuidedDryRunReview(
    ContentHash PlanFingerprint,
    int OperationCount,
    int SelectedDrawCallCount,
    int LodCount,
    int TargetBlockCount,
    int SelectedVertexCount,
    bool IncludesCoupledCollision);
