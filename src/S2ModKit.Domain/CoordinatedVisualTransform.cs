using System.Text.Json.Serialization;

namespace S2ModKit.Domain;

/// <summary>Explicit complete-buffer members; no anatomy is inferred from their identifiers.</summary>
public sealed record CoordinatedMemberLod(
    [property: JsonRequired] int Lod,
    [property: JsonRequired] IReadOnlyList<string> DrawCallIds,
    [property: JsonRequired] int ExpectedVertices);

public sealed record CoordinatedMember(
    [property: JsonRequired] string MemberId,
    [property: JsonRequired] IReadOnlyList<CoordinatedMemberLod> Lods);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CoordinatedEllipsoidField), "ellipsoid")]
[JsonDerivedType(typeof(CoordinatedAxisRampField), "axis_ramp")]
[JsonDerivedType(typeof(CoordinatedTiltedRampField), "tilted_ramp")]
public abstract record CoordinatedField
{
    [JsonRequired]
    public int Version { get; init; }

    [JsonRequired]
    public string CoordinateSpace { get; init; } = string.Empty;
}

public sealed record CoordinatedEllipsoidField : CoordinatedField
{
    [JsonRequired]
    public EllipsoidVisualTransform Intent { get; init; } = null!;
}

public sealed record CoordinatedAxisRampField : CoordinatedField
{
    [JsonRequired]
    public string Axis { get; init; } = string.Empty;
    [JsonRequired]
    public float PinnedThrough { get; init; }
    [JsonRequired]
    public float FullFrom { get; init; }
    [JsonRequired]
    public TransformVector3 Pivot { get; init; } = null!;
    [JsonRequired]
    public float UniformScale { get; init; }
}

public sealed record CoordinatedTiltedRampField : CoordinatedField
{
    [JsonRequired]
    public string FirstAxis { get; init; } = string.Empty;
    [JsonRequired]
    public int FirstSign { get; init; }
    [JsonRequired]
    public string SecondAxis { get; init; } = string.Empty;
    [JsonRequired]
    public int SecondSign { get; init; }
    [JsonRequired]
    public float PinnedThrough { get; init; }
    [JsonRequired]
    public float FullFrom { get; init; }
    [JsonRequired]
    public TransformVector3 Pivot { get; init; } = null!;
    [JsonRequired]
    public float UniformScale { get; init; }
    [JsonRequired]
    public EllipsoidNumericalPolicy NumericalPolicy { get; init; } = null!;
}

public sealed record CoordinatedVisualTransform(
    [property: JsonRequired] IReadOnlyList<CoordinatedMember> Members,
    [property: JsonRequired] CoordinatedField Field);

/// <summary>An explicit experimental choice; never implied by preserve_unverified.</summary>
public sealed record ZeroBoneBoxPolicy([property: JsonRequired] string Kind, [property: JsonRequired] int Version);

/// <summary>Only the render sphere paired with an admitted single-contributor all-zero box.</summary>
public sealed record ZeroRenderSpherePolicy([property: JsonRequired] string Kind, [property: JsonRequired] int Version);

/// <summary>
/// Preserves an unsupported zero-box family. Contributor closure and matrix identity are
/// authority; neither geometric containment nor engine sentinel semantics are qualified.
/// </summary>
public sealed record PlannedZeroBoneBoxPreservationTarget(
    int Lod, int MeshOrdinal, int BoneIndex, int ResourceBlockIndex, string FieldPath,
    string Storage, string CoordinateSpace, ContentHash CoordinateMatrixHash,
    IReadOnlyList<uint> CoordinateMatrixWords, ContentHash ContributorSetHash,
    int ContributorCount, int ContributorMeshVertexIndex,
    ContentHash SourcePayloadHash, IReadOnlyList<uint> OriginalWords,
    ZeroBoneBoxPolicy Policy, string Disposition);

public sealed record PlannedZeroRenderSpherePreservationTarget(
    int ResourceBlockIndex, string FieldPath, string PairedZeroBoxFieldPath,
    ContentHash ContributorSetHash, ContentHash SourcePayloadHash,
    string Storage, ulong OriginalWord, ZeroRenderSpherePolicy Policy, string Disposition);

public sealed record PlannedTiltedRampCertificate(
    [property: JsonRequired] string Algorithm,
    [property: JsonRequired] int Version,
    [property: JsonRequired] string Numerator,
    [property: JsonRequired] string Denominator);

/// <summary>Exactly the proof variant prescribed by the common field; null alternatives stay explicit.</summary>
public sealed record PlannedCoordinatedFieldProof(
    string Kind, int Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PlannedEllipsoidCertificate? EllipsoidCertificate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PlannedTiltedRampCertificate? TiltedCertificate);

public sealed record PlannedCoordinatedBuffer(
    string MemberId, int Lod, string ResourcePath, int MeshOrdinal, int ResourceBlockIndex,
    int VertexBufferOrdinal, int IndexBufferOrdinal, int VertexResourceBlockIndex, int IndexResourceBlockIndex,
    ContentHash VertexBlockInputHash, ContentHash IndexBlockInputHash,
    ContentHash InputDecodedVertexBufferHash, ContentHash ExpectedDecodedVertexBufferHash, ContentHash DecodedIndexBufferHash,
    ContentHash VertexSetHash, int VertexCount, string OwnershipPolicy,
    PositionLayout PositionLayout, PackedFrameLayout PackedFrameLayout, GeometryCodecIdentity Codec,
    ContentHash MaskHash, ContentHash WeightHash,
    int FullVertexCount, int TransitionVertexCount, int PinnedVertexCount,
    int ChangedPositionCount, int ChangedFrameCount,
    ContentHash InputPositionHash, ContentHash ExpectedPositionHash,
    ContentHash InputPackedFrameHash, ContentHash ExpectedPackedFrameHash,
    GeometryBounds BeforeBounds, GeometryBounds ExpectedAfterBounds, float MaximumDisplacement);

public sealed record PlannedCoordinatedTransformTarget(
    ContentHash InputHash, ContentHash TargetFingerprint,
    string StructuralProfileId, int StructuralProfileVersion, string BoundsPolicyId, int BoundsPolicyVersion,
    RuntimeMetadataPolicy RuntimeMetadataPolicy, ZeroBoneBoxPolicy ZeroBoneBoxPolicy, ZeroRenderSpherePolicy ZeroRenderSpherePolicy,
    ComponentSelector Selector, CoordinatedVisualTransform CoordinatedTransform, PlannedCoordinatedFieldProof FieldProof,
    float MaximumDisplacement, float DisplacementLimit,
    IReadOnlyList<PlannedCoordinatedBuffer> Buffers,
    IReadOnlyList<PlannedExperimentalBoxTarget> BoxTargets,
    IReadOnlyList<PlannedZeroBoneBoxPreservationTarget> ZeroBoxTargets,
    IReadOnlyList<PlannedZeroRenderSpherePreservationTarget> ZeroRenderSphereTargets,
    IReadOnlyList<PlannedExperimentalPreservationTarget> PreservationTargets,
    IReadOnlyList<PlannedTargetBlock> SourceBlocks,
    PlannedCoordinatedSeamInventory SeamInventory);

/// <summary>Exact coordinate coincidence evidence only; no-match does not qualify proximity or clothing fit.</summary>
public sealed record PlannedCoordinatedSeamInventory(ContentHash CompleteSourcePositionIdentity,
    int SelectedRecordCount, int MovingCohortCount, int CoincidentSelectedRecordCount, int ExcludedMovingMates);

public sealed record CoordinatedBufferObservation(
    string MemberId, int Lod, int MeshOrdinal, int VertexBufferOrdinal,
    ContentHash PositionHash, ContentHash PackedFrameHash, ContentHash DecodedVertexBufferHash,
    ContentHash MaskHash, ContentHash WeightHash, float MaximumDisplacement);

public sealed record ZeroBoneBoxPreservationEvidence(
    PlannedZeroBoneBoxPreservationTarget Target,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<uint>? ObservedWords,
    string PreservationStatus, string ContainmentStatus, string ConsumerStatus);

public sealed record ZeroRenderSpherePreservationEvidence(
    PlannedZeroRenderSpherePreservationTarget Target,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ObservedStorage,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ulong? ObservedWord,
    string PreservationStatus, string ContainmentStatus, string ConsumerStatus);

public sealed record CoordinatedTransformEvidence(
    PlannedCoordinatedTransformTarget Target,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<CoordinatedBufferObservation>? ObservedBuffers,
    IReadOnlyList<ExperimentalBoxEvidence> Boxes,
    IReadOnlyList<ZeroBoneBoxPreservationEvidence> ZeroBoxes,
    IReadOnlyList<ZeroRenderSpherePreservationEvidence> ZeroRenderSpheres,
    IReadOnlyList<ExperimentalPreservationEvidence> PreservedMetadata);
