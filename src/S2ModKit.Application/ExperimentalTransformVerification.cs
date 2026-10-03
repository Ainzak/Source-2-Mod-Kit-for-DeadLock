using S2ModKit.Domain;

namespace S2ModKit.Application;

/// <summary>Reopens immutable source and output bytes; snapshot/hash checks alone cannot prove box policy.</summary>
public interface IExperimentalTransformVerifier
{
    Task<ExperimentalTransformVerification> VerifyExperimentalTransformAsync(
        ArtifactContent input, ArtifactContent output, MutationPlan plan, CancellationToken cancellationToken = default);
}

public sealed record ExperimentalTransformVerification(
    IReadOnlyList<BoundaryEvidence> Boundaries,
    IReadOnlyList<ExperimentalBoxEvidence> Boxes,
    IReadOnlyList<ExperimentalPreservationEvidence> PreservedMetadata);
