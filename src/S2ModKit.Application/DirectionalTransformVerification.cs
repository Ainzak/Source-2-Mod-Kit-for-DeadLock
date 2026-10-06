using S2ModKit.Domain;

namespace S2ModKit.Application;

public interface IDirectionalTransformVerifier
{
    Task<DirectionalTransformVerification> VerifyDirectionalTransformAsync(
        ArtifactContent input, ArtifactContent output, MutationPlan plan, CancellationToken cancellationToken = default);
}

public sealed record DirectionalTransformVerification(
    IReadOnlyList<BoundaryEvidence> Boundaries, DirectionalObservation Observed,
    IReadOnlyList<ExperimentalBoxEvidence> Boxes, IReadOnlyList<ExperimentalPreservationEvidence> PreservedMetadata);
