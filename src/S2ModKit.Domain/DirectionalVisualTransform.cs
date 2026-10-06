using System.Text.Json.Serialization;

namespace S2ModKit.Domain;

public sealed record DirectionalEllipsoidField(
    [property: JsonRequired] string Kind, [property: JsonRequired] int Version,
    [property: JsonRequired] string CoordinateSpace, [property: JsonRequired] TransformPivot Pivot,
    [property: JsonRequired] TransformVector3 OuterRadii, [property: JsonRequired] float CoreFraction,
    [property: JsonRequired] TransformVector3 Scale, [property: JsonRequired] EllipsoidNumericalPolicy NumericalPolicy);

public sealed record DirectionalVisualTransform(
    [property: JsonRequired] IReadOnlyList<CoordinatedMember> Members,
    [property: JsonRequired] DirectionalEllipsoidField Field,
    [property: JsonRequired] DirectionalProtection Protection);

public sealed record DirectionalProtection(
    [property: JsonRequired] string Kind, [property: JsonRequired] int Version,
    [property: JsonRequired] IReadOnlyList<DirectionalProtectionAssertion> Assertions);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(DirectionalVertexAssertion), "vertex_set")]
[JsonDerivedType(typeof(DirectionalBoneAssertion), "root_bone_contributors")]
public abstract record DirectionalProtectionAssertion
{
    [JsonRequired] public int Version { get; init; }
    [JsonRequired] public string AssertionId { get; init; } = string.Empty;
}

public sealed record DirectionalVertexAssertion : DirectionalProtectionAssertion
{
    [JsonRequired] public IReadOnlyList<DirectionalVertexSet> Sets { get; init; } = [];
}

public sealed record DirectionalBoneAssertion : DirectionalProtectionAssertion
{
    [JsonRequired] public string BoneName { get; init; } = string.Empty;
    [JsonRequired] public int BoneIndex { get; init; }
    [JsonRequired] public ContentHash RootSkeletonHash { get; init; }
    [JsonRequired] public IReadOnlyList<DirectionalBoneLod> Lods { get; init; } = [];
}

public sealed record DirectionalVertexSet(
    string MemberId, int Lod, ContentHash SourceDecodedBufferHash,
    IReadOnlyList<int> VertexIndices, ContentHash VertexSetHash, int VertexCount);

public sealed record DirectionalBoneLod(int Lod, ContentHash ContributorSetHash, int ContributorCount);

public sealed record DirectionalRationalBound(string Numerator, string Denominator);

public sealed record PlannedDirectionalCertificate(
    string Algorithm, int Version, int SubdivisionDepth,
    IReadOnlyList<DirectionalRationalBound> AxisLowerBounds,
    IReadOnlyList<DirectionalRationalBound> MinimumAxisFactors,
    DirectionalRationalBound AlphaLowerBound, DirectionalRationalBound BetaLowerBound,
    DirectionalRationalBound RankOneNormUpperBound, DirectionalRationalBound InverseNormUpperBound,
    DirectionalRationalBound MinimumSingularValueLowerBound, DirectionalRationalBound DeterminantLowerBound,
    DirectionalRationalBound IdealDisplacementUpperBound);

/// <summary>Source inventory includes unchanged/excluded buffers and authored view masks.</summary>
public sealed record DirectionalContextBuffer(
    int Lod, int MeshOrdinal, int VertexBufferOrdinal, int ResourceBlockIndex,
    int VertexResourceBlockIndex, int IndexResourceBlockIndex, int VertexCount,
    string ResourcePath, bool Selected, ulong MeshMask, ulong BodygroupMask,
    ContentHash DecodedBufferHash, ContentHash PositionHash, ContentHash PackedFrameHash,
    ContentHash IndexHash, ContentHash SkinningHash, ContentHash RootRenderRemapHash);

public sealed record DirectionalCoincidenceLod(
    int Lod, ContentHash CompleteSourcePositionHash, ContentHash PairSetHash,
    int RecordCount, long PairCount, int MovingSelectedRecordCount, int ExcludedMovingMates);

/// <summary>Exact resolved contributor records; source and expected words must both remain fixed.</summary>
public sealed record DirectionalProtectedSet(
    int Lod, int MeshOrdinal, int VertexBufferOrdinal, ContentHash SourceDecodedBufferHash,
    IReadOnlyList<int> VertexIndices, ContentHash VertexSetHash, int VertexCount,
    ContentHash SourcePositionHash, ContentHash ExpectedPositionHash,
    ContentHash SourcePackedFrameHash, ContentHash ExpectedPackedFrameHash);

public sealed record PlannedDirectionalAssertion(
    string AssertionId, IReadOnlyList<DirectionalProtectedSet> Sets, ContentHash ContributorSetHash);

public sealed record PlannedDirectionalProtection(
    IReadOnlyList<PlannedDirectionalAssertion> Assertions,
    IReadOnlyList<DirectionalProtectedSet> Union, ContentHash UnionHash);

/// <summary>Every represented buffer has a row, including explicit zero-contributor rows.</summary>
public sealed record DirectionalBoxContributor(
    int Lod, int MeshOrdinal, int VertexBufferOrdinal, int ContributorCount,
    ContentHash ContributorSetHash, ContentHash SourcePositionHash, ContentHash ExpectedPositionHash);

public sealed record DirectionalBoxClosure(
    int ResourceBlockIndex, string FieldPath, int Lod, int MeshOrdinal,
    IReadOnlyList<DirectionalBoxContributor> Contributors, ContentHash ClosureHash);

public sealed record DirectionalWordAudit(
    string MemberId, int Lod, IReadOnlyList<int> ChangedPositionIndices, ContentHash ChangedPositionSetHash,
    IReadOnlyList<int> ChangedFrameIndices, ContentHash ChangedFrameSetHash,
    IReadOnlyList<int> PinnedIndices, ContentHash PinnedSetHash,
    ContentHash SourcePinnedPositionHash, ContentHash ExpectedPinnedPositionHash,
    ContentHash SourcePinnedFrameHash, ContentHash ExpectedPinnedFrameHash,
    ContentHash SourceUnchangedAttributesHash, ContentHash ExpectedUnchangedAttributesHash);

public sealed record PlannedDirectionalTransformTarget(
    ContentHash InputHash, ContentHash TargetFingerprint,
    string StructuralProfileId, int StructuralProfileVersion, string BoundsPolicyId, int BoundsPolicyVersion,
    RuntimeMetadataPolicy RuntimeMetadataPolicy, ZeroBoneBoxPolicy ZeroBoneBoxPolicy, ZeroRenderSpherePolicy ZeroRenderSpherePolicy,
    ComponentSelector Selector, DirectionalVisualTransform DirectionalTransform,
    DirectionalPivotEvidence Pivot, PlannedDirectionalCertificate Certificate,
    float MaximumDisplacement, float DisplacementLimit,
    IReadOnlyList<PlannedCoordinatedBuffer> Buffers,
    IReadOnlyList<DirectionalWordAudit> WordAudits,
    PlannedDirectionalProtection Protection,
    IReadOnlyList<DirectionalContextBuffer> ContextBuffers,
    IReadOnlyList<DirectionalCoincidenceLod> Coincidences,
    IReadOnlyList<PlannedExperimentalBoxTarget> BoxTargets,
    IReadOnlyList<DirectionalBoxClosure> BoxClosures,
    IReadOnlyList<PlannedExperimentalPreservationTarget> PreservationTargets,
    IReadOnlyList<PlannedTargetBlock> SourceBlocks);

public sealed record DirectionalPivotEvidence(string Kind, TransformVector3 Point, string CoordinateSpace,
    string SourceIdentity, ContentHash SourceHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? ReferenceLod);

public sealed record DirectionalObservation(
    IReadOnlyList<CoordinatedBufferObservation> Buffers,
    IReadOnlyList<DirectionalWordAudit> WordAudits,
    PlannedDirectionalProtection Protection,
    IReadOnlyList<DirectionalContextBuffer> ContextBuffers,
    IReadOnlyList<DirectionalCoincidenceLod> Coincidences,
    IReadOnlyList<DirectionalBoxClosure> BoxClosures,
    PlannedDirectionalCertificate Certificate);

public sealed record DirectionalTransformEvidence(
    PlannedDirectionalTransformTarget Target,
    DirectionalPlanBinding PlanBinding,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DirectionalObservation? Observed,
    IReadOnlyList<ExperimentalBoxEvidence> Boxes,
    IReadOnlyList<ExperimentalPreservationEvidence> PreservedMetadata);

public sealed record DirectionalPlanBinding(string RecipeId, IReadOnlyList<PlannedInput> Inputs,
    IReadOnlyList<SelectedDrawCall> SelectedDrawCalls, IReadOnlyList<PlannedTargetBlock> TargetBlocks);
