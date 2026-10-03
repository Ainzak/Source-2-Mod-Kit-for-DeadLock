namespace S2ModKit.Domain;

/// <summary>Frozen visual intent; unresolved runtime metadata is deliberately preserved, not qualified.</summary>
public sealed record PlannedExperimentalTransformTarget(
    string StructuralProfileId,
    int StructuralProfileVersion,
    string BoundsPolicyId,
    int BoundsPolicyVersion,
    RuntimeMetadataPolicy RuntimeMetadataPolicy,
    ComponentSelector Selector,
    TransformPivot PivotIntent,
    ResolvedTransformPivot Pivot,
    float UniformScale,
    float MaximumDisplacement,
    float DisplacementLimit,
    IReadOnlyList<PlannedGeometryTarget> GeometryTargets,
    IReadOnlyList<PlannedExperimentalBoxTarget> BoxTargets,
    IReadOnlyList<PlannedExperimentalPreservationTarget> PreservationTargets,
    IReadOnlyList<PlannedTargetBlock> SourceBlocks)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PlannedRegionScale? Region { get; init; }
}

public sealed record PlannedRegionScale(RegionScaleSelection Selection, IReadOnlyList<PlannedRegionBuffer> Buffers);

public sealed record PlannedRegionBuffer(
    int Lod,
    int MeshOrdinal,
    int VertexBufferOrdinal,
    ContentHash MaskHash,
    int PinnedVertexCount,
    int TransitionVertexCount,
    int FullVertexCount,
    int ChangedVertexCount,
    PackedFrameLayout PackedFrameLayout,
    ContentHash InputPackedFrameHash,
    ContentHash ExpectedPackedFrameHash);

/// <summary>
/// Six IEEE-754 words store min/max or center/half-extent pairs without losing authored signed zeros.
/// A twelve-word row-major matrix identifies the characterized contributor-to-field conversion.
/// </summary>
public sealed record PlannedExperimentalBoxTarget(
    int ResourceBlockIndex,
    string FieldPath,
    string Storage,
    string CoordinateSpace,
    ContentHash CoordinateMatrixHash,
    IReadOnlyList<uint> CoordinateMatrixWords,
    ContentHash ContributorSetHash,
    int ContributorCount,
    IReadOnlyList<uint> OriginalWords,
    IReadOnlyList<uint> ExpectedWords,
    IReadOnlyList<ExperimentalBoundsGrowth> Growth);

public sealed record ExperimentalBoundsGrowth(string Measure, double OriginalValue, double PlannedValue, double Delta);

/// <summary>Byte preservation only. It proves neither containment nor collision/proxy coherence.</summary>
public sealed record PlannedExperimentalPreservationTarget(
    int ResourceBlockIndex,
    string FieldPath,
    string Category,
    string Scope,
    string Disposition,
    ContentHash SourcePayloadHash,
    IReadOnlyList<uint> OriginalWords);
