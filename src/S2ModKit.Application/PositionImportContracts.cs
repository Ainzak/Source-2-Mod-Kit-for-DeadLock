using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed record PositionImportAssets(ReadOnlyMemory<byte> Selection, ReadOnlyMemory<byte> Bundle, ReadOnlyMemory<byte> Delta);
public interface IPositionImportArtifactStore
{
    Task StorePositionImportAsync(string projectRoot, PositionImportBinding binding, PositionImportAssets assets, CancellationToken token = default);
    Task<PositionImportAssets> LoadPositionImportAsync(string projectRoot, PositionImportBinding binding, CancellationToken token = default);
}
public sealed record PositionImportVerification(IReadOnlyList<BoundaryEvidence> Boundaries, PositionImportObservation Observed,
    IReadOnlyList<ExperimentalBoxEvidence> Boxes, IReadOnlyList<ExperimentalPreservationEvidence> PreservedMetadata);
public interface IPositionImportResourceAdapter
{
    Task<PlannedOperation> PlanPositionImportAsync(ArtifactContent input, ModelSnapshot snapshot, ImportPositionsOperation op, PositionImportAssets assets, CancellationToken token = default);
    Task<RewriteCandidate> RewritePositionImportAsync(ArtifactContent input, ModelSnapshot snapshot, MutationPlan plan, PositionImportAssets assets, CancellationToken token = default);
    Task<PositionImportVerification> VerifyPositionImportAsync(ArtifactContent input, ArtifactContent output, MutationPlan plan, PositionImportAssets assets, CancellationToken token = default);
}
public interface IPositionImportApplication
{
    Task<RecipeScaffoldResult> ScaffoldPositionImportAsync(string projectRoot, PositionImportAssets assets, ContentHash bundleHash, string outputPath, CancellationToken token = default);
    Task<object> RunPositionImportRequestAsync(ReadOnlyMemory<byte> request, CancellationToken token = default);
}
public sealed record PositionImportRequest(int SchemaVersion, string Kind, string Project, PositionImportBinding Binding, RecipeDocument Recipe, string Action);

public interface IPositionImportCompositionAdapter
{
    Task<IReadOnlyList<PositionImportDrawProjection>> PlanImportDrawProjectionAsync(ArtifactContent input, PlannedOperation removal, CancellationToken token = default);
    Task<RewriteCandidate> RewritePositionImportCompositionAsync(ArtifactContent input, ModelSnapshot snapshot, MutationPlan plan, PositionImportAssets assets, CancellationToken token = default);
    Task<PositionImportCompositionVerification> VerifyPositionImportCompositionAsync(ArtifactContent input, ArtifactContent output, MutationPlan plan, PositionImportAssets assets, CancellationToken token = default);
}
public sealed record PositionImportCompositionVerification(RewriteCandidate Projection, PositionImportVerification ImportVerification, IReadOnlyList<BoundaryEvidence> Boundaries);
public interface IPositionImportCompositionApplication
{
    Task<RecipeScaffoldResult> ComposePositionImportRecipeAsync(string projectRoot, RecipeDocument import, RecipeDocument removal, string outputPath, CancellationToken token = default);
}
