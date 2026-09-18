namespace S2ModKit.Domain;

public sealed record SelectedDrawCall(
    int Lod,
    string ResourcePath,
    int MeshOrdinal,
    int ResourceBlockIndex,
    string DrawCallId,
    string MaterialPath,
    int DrawCallOrdinal,
    long IndexStart,
    long IndexCount);

public sealed record PlannedTargetBlock(
    int Index,
    string Type,
    ContentHash InputHash);

public sealed record PlannedInput(
    string LogicalPath,
    ContentHash ContentHash,
    long Size);

public sealed record PlannedOperation(
    string OperationId,
    string Kind,
    int Version,
    IReadOnlyList<SelectedDrawCall> SelectedDrawCalls,
    IReadOnlyList<PlannedTargetBlock> TargetBlocks)
{
    public IReadOnlyList<PlannedGeometryTarget> GeometryTargets { get; init; } = [];

    public IReadOnlyList<PlannedDistanceFieldTarget> DistanceFieldTargets { get; init; } = [];

    public PlannedCoupledTransformTarget? CoupledTransformTarget { get; init; }
}

public sealed record GeometryBounds(
    TransformVector3 Min,
    TransformVector3 Max);

public sealed record PositionLayout(
    string Format,
    int Offset,
    int Stride);

public sealed record GeometryCodecIdentity(
    string Name,
    string ApiProfile,
    string Platform,
    ContentHash BinaryHash,
    string Version);

public sealed record PlannedGeometryTarget(
    int Lod,
    string ResourcePath,
    int MeshOrdinal,
    int ResourceBlockIndex,
    int VertexBufferOrdinal,
    int IndexBufferOrdinal,
    int VertexResourceBlockIndex,
    int IndexResourceBlockIndex,
    ContentHash VertexBlockInputHash,
    ContentHash IndexBlockInputHash,
    ContentHash DecodedVertexBufferHash,
    ContentHash ExpectedDecodedVertexBufferHash,
    ContentHash DecodedIndexBufferHash,
    ContentHash VertexSetHash,
    int SelectedVertexCount,
    PositionLayout PositionLayout,
    GeometryBounds BeforeBounds,
    GeometryBounds ExpectedAfterBounds,
    TransformVector3 FrozenPivot,
    float UniformScale,
    TransformVector3 Translation,
    float MaximumDisplacement,
    IReadOnlyList<string> AllowedChangedAttributes,
    GeometryCodecIdentity Codec)
{
    public IReadOnlyList<PlannedBoneBoundsTarget> BoneBoundsTargets { get; init; } = [];

    public IReadOnlyList<string> ConnectedComponentIds { get; init; } = [];
}

public sealed record PlannedBoneBoundsTarget(
    int BoneIndex,
    string BoneName,
    ContentHash InverseBindPoseHash,
    ContentHash InfluencedVertexSetHash,
    int InfluencedVertexCount,
    TransformVector3 BeforeCenter,
    TransformVector3 BeforeSize,
    GeometryBounds BeforeBounds,
    TransformVector3 ExpectedCenter,
    TransformVector3 ExpectedSize,
    GeometryBounds ExpectedAfterBounds,
    TransformVector3 LocalPivot,
    TransformVector3 LocalTranslation,
    float SphereRadius,
    float ExpectedSphereRadius);

public sealed record PlannedDistanceFieldTarget(
    int ResourceBlockIndex,
    int FieldIndex,
    uint ParentBoneNameHash,
    int BodyGroupIndex,
    int BodyGroupChoice,
    GeometryBounds BeforeBounds,
    GeometryBounds ExpectedAfterBounds,
    int ResolutionX,
    int ResolutionY,
    int ResolutionZ,
    float GridCellSize,
    float ExpectedGridCellSize,
    float MaximumQuantizedDistance,
    float ExpectedMaximumQuantizedDistance,
    float SurfaceBias,
    bool IsTwoSided,
    bool IsFarFieldOnly,
    bool UseForOcclusion,
    bool UseForCollision,
    ContentHash QuantizedDataHash,
    int QuantizedDataLength);

public sealed record PlannedCoupledTransformTarget(
    PlannedRawMbufTransformTarget Visual,
    PlannedConvexPhysTransformTarget Collision,
    IReadOnlyList<PlannedTargetBlock> TargetBlocks);

public sealed record PlannedRawMbufTransformTarget(
    int MeshResourceBlockIndex,
    int MbufResourceBlockIndex,
    ContentHash MeshBlockInputHash,
    ContentHash MbufBlockInputHash,
    ContentHash ExpectedMbufBlockHash,
    ContentHash DecodedVertexBufferHash,
    ContentHash ExpectedDecodedVertexBufferHash,
    ContentHash DecodedIndexBufferHash,
    ContentHash VertexSetHash,
    string DrawCallId,
    string MaterialPath,
    string SkinningRootBone,
    uint SkinningRootBoneHash,
    int VertexCount,
    PositionLayout PositionLayout,
    GeometryBounds BeforeBounds,
    GeometryBounds ExpectedAfterBounds,
    TransformVector3 FrozenPivot,
    float UniformScale,
    float MaximumDisplacement,
    float DisplacementLimit,
    IReadOnlyList<string> AllowedByteClasses,
    IReadOnlyList<PlannedBoneBoundsTarget> BoneBoundsTargets);

public sealed record PlannedPlaneTransformTarget(
    TransformVector3 Normal,
    float BeforeOffset,
    float ExpectedOffset);

public sealed record PlannedConvexDerivedValues(
    GeometryBounds Bounds,
    TransformVector3 VertexCentroid,
    float MaximumAngularRadius,
    float Volume,
    float SurfaceArea,
    TransformVector3 CenterOfMass,
    IReadOnlyList<float> MassProperties);

public sealed record PlannedConvexPhysTransformTarget(
    int ResourceBlockIndex,
    ContentHash PayloadInputHash,
    ContentHash PositionInputHash,
    ContentHash ExpectedPositionHash,
    int VertexCount,
    TransformVector3 FrozenPivot,
    float UniformScale,
    float MaximumDisplacement,
    float DisplacementLimit,
    PlannedConvexDerivedValues Before,
    PlannedConvexDerivedValues ExpectedAfter,
    IReadOnlyList<PlannedPlaneTransformTarget> HullPlanes,
    IReadOnlyList<PlannedPlaneTransformTarget> RegionPlanes,
    ContentHash HullVertexEdgesHash,
    ContentHash HalfEdgesHash,
    ContentHash FacesHash,
    ContentHash RegionNodesHash,
    ContentHash HullPlanesInputHash,
    ContentHash RegionPlanesInputHash,
    int HalfEdgeCount,
    int FaceCount,
    int RegionNodeCount,
    TransformVector3 UnchangedOrthographicAreaFractions,
    string CollisionGroupString,
    IReadOnlyList<string> AllowedByteClasses);

public sealed record MutationPlan(
    string RecipeId,
    ContentHash InputHash,
    ContentHash Fingerprint,
    IReadOnlyList<PlannedOperation> Operations)
{
    public IReadOnlyList<PlannedInput> Inputs { get; init; } = [];
}

public sealed record RewriteCandidate(
    string LogicalPath,
    ReadOnlyMemory<byte> Content,
    ModelSnapshot Snapshot);

public sealed record VerificationResult(
    bool IsValid,
    IReadOnlyList<BoundaryEvidence> Boundaries,
    IReadOnlyList<string> Warnings);
