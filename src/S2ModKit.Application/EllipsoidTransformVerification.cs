using S2ModKit.Domain;

namespace S2ModKit.Application;

/// <summary>Independent immutable-source and output-byte audit; snapshot checks cannot authorize publication.</summary>
public interface IEllipsoidTransformVerifier
{
    Task<EllipsoidTransformVerification> VerifyEllipsoidTransformAsync(
        ArtifactContent input, ArtifactContent output, MutationPlan plan, CancellationToken cancellationToken = default);
}

public sealed record EllipsoidTransformVerification(
    IReadOnlyList<BoundaryEvidence> Boundaries,
    IReadOnlyList<EllipsoidBufferObservation> Buffers,
    IReadOnlyList<ExperimentalBoxEvidence> Boxes,
    IReadOnlyList<ExperimentalPreservationEvidence> PreservedMetadata);
