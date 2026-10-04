using S2ModKit.Domain;

namespace S2ModKit.Application;

/// <summary>Independent resource audit is mandatory before publishing a coordinated candidate.</summary>
public interface ICoordinatedTransformVerifier
{
    Task<CoordinatedTransformVerification> VerifyCoordinatedTransformAsync(
        ArtifactContent input, ArtifactContent output, MutationPlan plan, CancellationToken cancellationToken = default);
}

public sealed record CoordinatedTransformVerification(
    IReadOnlyList<BoundaryEvidence> Boundaries,
    IReadOnlyList<CoordinatedBufferObservation> Buffers,
    IReadOnlyList<ExperimentalBoxEvidence> Boxes,
    IReadOnlyList<ZeroBoneBoxPreservationEvidence> ZeroBoxes,
    IReadOnlyList<ZeroRenderSpherePreservationEvidence> ZeroRenderSpheres,
    IReadOnlyList<ExperimentalPreservationEvidence> PreservedMetadata);
