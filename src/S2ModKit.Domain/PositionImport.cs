using System.Text.Json.Serialization;

namespace S2ModKit.Domain;

public sealed record PositionImportArtifact(string Path, ContentHash Sha256);
public sealed record PositionImportBinding(ContentHash BundleSha256, ContentHash DeltaSha256, ContentHash PolicySha256, ContentHash SelectionSha256, PositionImportArtifact DeltaArtifact);
public sealed record PositionImportRecordMap(string MemberId, int Lod, ContentHash SourceDecodedHash, IReadOnlyList<int> VertexIds);
public sealed record PositionImportFramePolicy(string Kind, int Version);
public sealed record PositionImportBoundaryPolicy(string Kind, int Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PositionImportRecordMap>? AllowedBoundary = null);
public sealed record PositionImportIntent(PositionImportBinding Binding, IReadOnlyList<CoordinatedMember> Members,
    PositionImportFramePolicy FramePolicy, PositionImportBoundaryPolicy BoundaryFramePolicy, IReadOnlyList<PositionImportRecordMap> Protection);
public sealed record ImportPositionsOperation : RecipeOperation
{
    [JsonIgnore] public override string OperationKind => "import_positions";
    public override string Granularity { get; init; } = "returned_buffer_positions";
    [JsonRequired] public IReadOnlyDictionary<string, int> ExpectedVerticesByLod { get; init; } = null!;
    [JsonRequired] public string OwnershipPolicy { get; init; } = null!;
    [JsonRequired] public RuntimeMetadataPolicy RuntimeMetadataPolicy { get; init; } = null!;
    [JsonRequired] public SourceTrianglePolicy SourceTrianglePolicy { get; init; } = null!;
    [JsonRequired] public ProceduralInputPolicy ProceduralInputPolicy { get; init; } = null!;
    [JsonRequired] public ZeroBoneBoxPolicy ZeroBoneBoxPolicy { get; init; } = null!;
    [JsonRequired] public ZeroRenderSpherePolicy ZeroRenderSpherePolicy { get; init; } = null!;
    [JsonRequired] public TransformLimits Limits { get; init; } = null!;
    [JsonRequired] public PositionImportIntent PositionImport { get; init; } = null!;
}
public sealed record PositionImportFrameWord(int VertexId, string SourceWord, string ExpectedWord);
public sealed record PlannedPositionImportBuffer(string MemberId, int Lod, int MeshOrdinal, int BufferOrdinal, int VertexCount,
    ContentHash SourceDecodedHash, ContentHash SourceEncodedHash, ContentHash SourceIndexHash, ContentHash LayoutHash,
    ContentHash ExpectedPositionHash, ContentHash ExpectedFrameHash, ContentHash ExpectedDecodedHash, int PositionChangeCount,
    IReadOnlyList<int> FrameClosureIds, IReadOnlyList<PositionImportFrameWord> FrameWords, ContentHash ProtectedPositionHash,
    ContentHash ProtectedFrameHash, IReadOnlyList<int> BoundaryChangedFrameIds);
public sealed record PlannedPositionImportTarget(ContentHash InputHash, ContentHash TargetFingerprint, string StructuralProfileId, int StructuralProfileVersion,
    string BoundsPolicyId, int BoundsPolicyVersion, PositionImportIntent PositionImport, IReadOnlyList<PlannedPositionImportBuffer> Buffers,
    ContentHash ContextInventoryHash, ContentHash SourceBlocksHash, ContentHash SourceCoincidenceHash, IReadOnlyList<PairedSourceTriangleFacts> SourceTriangles,
    PlannedPairedProceduralInputs ProceduralInputs, IReadOnlyList<PlannedExperimentalBoxTarget> BoxTargets, IReadOnlyList<DirectionalBoxClosure> BoxClosures,
    IReadOnlyList<PlannedExperimentalPreservationTarget> PreservationTargets, IReadOnlyList<DirectionalContextBuffer> ContextBuffers,
    IReadOnlyList<DirectionalCoincidenceLod> Coincidences, IReadOnlyList<PlannedTargetBlock> SourceBlocks);
public sealed record PositionImportObservedBuffer(string MemberId, int Lod, ContentHash ObservedDecodedHash, ContentHash ObservedPositionHash,
    ContentHash ObservedFrameHash, ContentHash UnchangedAttributesHash, string Status);
public sealed record PositionImportObservation(ContentHash InputHash, ContentHash OutputHash, ContentHash PlanFingerprint, ContentHash TargetFingerprint,
    PositionImportBinding Binding, IReadOnlyList<PositionImportObservedBuffer> Buffers, string StructuralStatus, string BoxStatus, string PreservationStatus,
    string ConsumerStatus, string PoseStatus, string RuntimeStatus);
public sealed record PositionImportEvidence(PlannedPositionImportTarget Target, DirectionalPlanBinding PlanBinding,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PositionImportObservation? Observed,
    IReadOnlyList<ExperimentalBoxEvidence> Boxes, IReadOnlyList<ExperimentalPreservationEvidence> PreservedMetadata);
