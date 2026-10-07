using S2ModKit.Domain;

namespace S2ModKit.Application;

public interface IPairedTransformVerifier
{
    Task<PairedTransformVerification> VerifyPairedTransformAsync(
        ArtifactContent input, ArtifactContent output, MutationPlan plan, CancellationToken cancellationToken = default);
}

public sealed record PairedTransformVerification(
    IReadOnlyList<BoundaryEvidence> Boundaries, PairedDirectionalObservation Observed,
    IReadOnlyList<ExperimentalBoxEvidence> Boxes, IReadOnlyList<ExperimentalPreservationEvidence> PreservedMetadata);
