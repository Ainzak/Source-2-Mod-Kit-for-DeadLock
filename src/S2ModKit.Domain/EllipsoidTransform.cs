using System.Text.Json.Serialization;

namespace S2ModKit.Domain;

public sealed record EllipsoidField(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] int Version,
    [property: JsonRequired] string CoordinateSpace,
    [property: JsonRequired] TransformVector3 Center,
    [property: JsonRequired] TransformVector3 OuterRadii,
    [property: JsonRequired] float CoreFraction);

public sealed record EllipsoidNumericalPolicy([property: JsonRequired] string Kind, [property: JsonRequired] int Version);

/// <summary>One center anchors both membership and scaling; there is no independent pivot.</summary>
public sealed record EllipsoidVisualTransform(
    [property: JsonRequired] EllipsoidField Field,
    [property: JsonRequired] float UniformScale,
    [property: JsonRequired] EllipsoidNumericalPolicy NumericalPolicy);

public sealed record PlannedEllipsoidCertificate(
    [property: JsonRequired] string Algorithm,
    [property: JsonRequired] int Version,
    [property: JsonRequired] int SubdivisionDepth,
    [property: JsonRequired] string Numerator,
    [property: JsonRequired] string Denominator,
    [property: JsonRequired] double LowerBound,
    [property: JsonRequired] double UpperBound,
    [property: JsonRequired] double MinimumSingularValueLowerBound);

/// <summary>Complete ordinary buffer identity, membership and changed words are distinct facts.</summary>
public sealed record PlannedEllipsoidBuffer(
    int Lod, string ResourcePath, int MeshOrdinal, int ResourceBlockIndex,
    int VertexBufferOrdinal, int IndexBufferOrdinal, int VertexResourceBlockIndex, int IndexResourceBlockIndex,
    ContentHash VertexBlockInputHash, ContentHash IndexBlockInputHash,
    ContentHash InputDecodedVertexBufferHash, ContentHash ExpectedDecodedVertexBufferHash, ContentHash DecodedIndexBufferHash,
    ContentHash VertexSetHash, int VertexCount, string OwnershipPolicy,
    PositionLayout PositionLayout, PackedFrameLayout PackedFrameLayout, GeometryCodecIdentity Codec,
    ContentHash MaskHash, ContentHash WeightHash,
    int CoreVertexCount, int TransitionVertexCount, int PinnedVertexCount,
    int ChangedPositionCount, int ChangedFrameCount,
    ContentHash InputPositionHash, ContentHash ExpectedPositionHash,
    ContentHash InputPackedFrameHash, ContentHash ExpectedPackedFrameHash,
    GeometryBounds BeforeBounds, GeometryBounds ExpectedAfterBounds, float MaximumDisplacement);

public sealed record PlannedEllipsoidTransformTarget(
    ContentHash InputHash,
    ContentHash TargetFingerprint,
    string StructuralProfileId, int StructuralProfileVersion,
    string BoundsPolicyId, int BoundsPolicyVersion,
    RuntimeMetadataPolicy RuntimeMetadataPolicy, ComponentSelector Selector,
    EllipsoidVisualTransform LocalTransform, PlannedEllipsoidCertificate Certificate,
    float MaximumDisplacement, float DisplacementLimit,
    IReadOnlyList<PlannedEllipsoidBuffer> Buffers,
    IReadOnlyList<PlannedExperimentalBoxTarget> BoxTargets,
    IReadOnlyList<PlannedExperimentalPreservationTarget> PreservationTargets,
    IReadOnlyList<PlannedTargetBlock> SourceBlocks);

public sealed record EllipsoidBufferObservation(
    int Lod, int MeshOrdinal, int VertexBufferOrdinal,
    ContentHash PositionHash, ContentHash PackedFrameHash, ContentHash DecodedVertexBufferHash,
    ContentHash MaskHash, ContentHash WeightHash, float MaximumDisplacement);

/// <summary>Planned facts remain separate from reopened output observations and untested consumers.</summary>
public sealed record EllipsoidTransformEvidence(
    PlannedEllipsoidTransformTarget Target,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<EllipsoidBufferObservation>? ObservedBuffers,
    IReadOnlyList<ExperimentalBoxEvidence> Boxes,
    IReadOnlyList<ExperimentalPreservationEvidence> PreservedMetadata);
