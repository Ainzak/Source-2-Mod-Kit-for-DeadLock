using System.Text.Json.Serialization;

namespace S2ModKit.Domain;

public sealed record SourceTrianglePolicy([property: JsonRequired] string Kind, [property: JsonRequired] int Version);
public sealed record ProceduralInputPolicy([property: JsonRequired] string Kind, [property: JsonRequired] int Version);
public sealed record PairedDirectionalField(string FieldId, DirectionalEllipsoidField Field);
public sealed record PairedDirectionalVisualTransform(IReadOnlyList<CoordinatedMember> Members,
    IReadOnlyList<PairedDirectionalField> Fields, DirectionalProtection Protection);

public sealed record PlannedPairedField(string FieldId, DirectionalPivotEvidence Pivot, PlannedDirectionalCertificate Certificate);
public sealed record PlannedPairSeparation(string Algorithm, int Version, string Axis,
    DirectionalRationalBound CenterDistance, DirectionalRationalBound RadiusSum, DirectionalRationalBound Gap);

/// <summary>Membership is always classified at original source positions, never a prior output.</summary>
public sealed record PairedFieldDispatch(string FieldId, IReadOnlyList<int> CoreIndices, IReadOnlyList<int> TransitionIndices,
    IReadOnlyList<int> ChangedPositionIndices, IReadOnlyList<int> ChangedFrameIndices,
    ContentHash MembershipHash, ContentHash WeightHash);
public sealed record PairedBufferDispatch(string MemberId, int Lod, IReadOnlyList<PairedFieldDispatch> Fields,
    IReadOnlyList<int> PinnedIndices, ContentHash MembershipHash, ContentHash WeightHash);

public sealed record PairedSourceTriangleFacts(string MemberId, int Lod, ContentHash IndexHash,
    IReadOnlyList<int> TriangleIndices, ContentHash TriangleSetHash, IReadOnlyList<byte> SourcePartitions,
    ContentHash SourcePartitionHash, ContentHash ExpectedPartitionHash,
    int ValidTriangleCount, int ChangedValidTriangleCount, int CollapsedTriangleCount, int TouchedCollapsedTriangleCount);

public sealed record PairedRootBone(int Index, string Name, int ParentIndex, uint Flags, bool Procedural, ContentHash BindHash);
public sealed record PairedProceduralBoneContributors(int RootBoneIndex, string RootBoneName,
    IReadOnlyList<DirectionalProtectedSet> Sets, ContentHash ContributorSetHash);
public sealed record PairedRootContributorSet(int RootBoneIndex, IReadOnlyList<int> VertexIndices, ContentHash VertexSetHash);
public sealed record PairedSkinningBufferFacts(int Lod, int MeshOrdinal, int VertexBufferOrdinal, int Stride, int PositionOffset, int PackedFrameOffset,
    uint IndexFormat, int IndexOffset, int IndexWidth, uint WeightFormat, int WeightOffset, int WeightWidth,
    int DeclaredInfluenceCount, ContentHash SourceSkinningHash, ContentHash ExpectedSkinningHash,
    ContentHash RootRenderRemapHash, IReadOnlyList<PairedRootContributorSet> RootContributors, ContentHash ContributorHash);

/// <summary>Known inventoried references can remain explicitly runtime-unverified; unknown families cannot.</summary>
public sealed record PairedConsumerReference(string Kind, string ScopeId, int SourceIndex, string SourceName,
    int RootBoneIndex, string RootBoneName, ContentHash RootIdentityHash, ContentHash IdentityHash, string Disposition);
public sealed record PairedConsumerRecord(string ConsumerId, int ResourceBlockIndex, string FieldPath,
    ContentHash SourcePayloadHash, ContentHash ExpectedPayloadHash, string Disposition,
    IReadOnlyList<PairedConsumerReference> References, ContentHash ReferenceHash);
public sealed record PairedConsumerFamily(string Category, bool Present, IReadOnlyList<PairedConsumerRecord> Records);
public sealed record PlannedPairedProceduralInputs(int RootDataBlockIndex, ContentHash RootPayloadHash, ContentHash RootSkeletonHash,
    IReadOnlyList<PairedRootBone> RootBones, ContentHash RootFactsHash,
    IReadOnlyList<PairedSkinningBufferFacts> SkinningBuffers,
    IReadOnlyList<PairedProceduralBoneContributors> BoneContributors,
    IReadOnlyList<DirectionalProtectedSet> Union, ContentHash UnionHash,
    IReadOnlyList<PairedConsumerFamily> ConsumerFamilies, ContentHash ConsumerInventoryHash);

public sealed record PlannedPairedTransformTarget(
    ContentHash InputHash, ContentHash TargetFingerprint,
    string StructuralProfileId, int StructuralProfileVersion, string BoundsPolicyId, int BoundsPolicyVersion,
    RuntimeMetadataPolicy RuntimeMetadataPolicy, ZeroBoneBoxPolicy ZeroBoneBoxPolicy, ZeroRenderSpherePolicy ZeroRenderSpherePolicy,
    SourceTrianglePolicy SourceTrianglePolicy, ProceduralInputPolicy ProceduralInputPolicy,
    ComponentSelector Selector, PairedDirectionalVisualTransform PairedTransform,
    IReadOnlyList<PlannedPairedField> Fields, PlannedPairSeparation Separation,
    float MaximumDisplacement, float DisplacementLimit,
    IReadOnlyList<PlannedCoordinatedBuffer> Buffers, IReadOnlyList<PairedBufferDispatch> Dispatch,
    IReadOnlyList<DirectionalWordAudit> WordAudits, PlannedDirectionalProtection Protection,
    IReadOnlyList<DirectionalContextBuffer> ContextBuffers, IReadOnlyList<DirectionalCoincidenceLod> Coincidences,
    IReadOnlyList<PairedSourceTriangleFacts> SourceTriangles, PlannedPairedProceduralInputs ProceduralInputs,
    IReadOnlyList<PlannedExperimentalBoxTarget> BoxTargets, IReadOnlyList<DirectionalBoxClosure> BoxClosures,
    IReadOnlyList<PlannedExperimentalPreservationTarget> PreservationTargets, IReadOnlyList<PlannedTargetBlock> SourceBlocks);

public sealed record PairedDirectionalObservation(IReadOnlyList<CoordinatedBufferObservation> Buffers,
    IReadOnlyList<PairedBufferDispatch> Dispatch, IReadOnlyList<DirectionalWordAudit> WordAudits,
    PlannedDirectionalProtection Protection, IReadOnlyList<DirectionalContextBuffer> ContextBuffers,
    IReadOnlyList<DirectionalCoincidenceLod> Coincidences, IReadOnlyList<DirectionalBoxClosure> BoxClosures,
    IReadOnlyList<PlannedPairedField> Fields, PlannedPairSeparation Separation,
    IReadOnlyList<PairedSourceTriangleFacts> SourceTriangles, PlannedPairedProceduralInputs ProceduralInputs);
public sealed record PairedDirectionalTransformEvidence(PlannedPairedTransformTarget Target, DirectionalPlanBinding PlanBinding,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PairedDirectionalObservation? Observed,
    IReadOnlyList<ExperimentalBoxEvidence> Boxes, IReadOnlyList<ExperimentalPreservationEvidence> PreservedMetadata);
