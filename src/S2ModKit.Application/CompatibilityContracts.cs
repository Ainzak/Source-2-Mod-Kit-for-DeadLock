using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed record StructuralProfileAnalysisRequest(
    ArtifactContent Input,
    ModelSnapshot Model);

public sealed record CompatibilityScanEntry(
    string ResourceId,
    string LogicalPath,
    ContentHash ContentHash,
    long Size,
    StructuralCompatibilityAssessment Compatibility);

public interface IStructuralProfileAnalyzer
{
    string AnalyzerName { get; }

    string AnalyzerVersion { get; }

    IReadOnlyDictionary<string, string> ComponentVersions { get; }

    bool CanAnalyze(ArtifactContent input, ModelSnapshot model);

    Task<StructuralCompatibilityAssessment> AnalyzeAsync(
        StructuralProfileAnalysisRequest request,
        CancellationToken cancellationToken = default);
}
