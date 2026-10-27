using S2ModKit.Domain;
using S2ModKit.Geometry;
using System.Text.Json.Serialization;

namespace S2ModKit.Application;

public sealed record PositionRecordMap(string MemberId, int Lod, string SourceDecodedHash, IReadOnlyList<int> VertexIds);
public sealed record PositionImportPolicy(string Kind, int Version);
public sealed record PositionBoundaryPolicy(string Kind, int Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PositionRecordMap>? AllowedBoundary = null);
public sealed record PositionAuthoringSelection(int SchemaVersion, string InputHash, IReadOnlyList<CoordinatedMember> Members,
    float MaximumDisplacement, IReadOnlyList<PositionRecordMap> Protection, PositionBoundaryPolicy BoundaryFramePolicy,
    PositionImportPolicy RuntimeMetadataPolicy, PositionImportPolicy ProceduralInputPolicy, PositionImportPolicy SourceTrianglePolicy,
    PositionImportPolicy ZeroBoneBoxPolicy, PositionImportPolicy ZeroRenderSpherePolicy);
public sealed record PositionSourceBuffer(string? SelectedMemberId, DirectionalContextBuffer Facts,
    IReadOnlyList<Point3> Positions, IReadOnlyList<int> Triangles, IReadOnlyList<uint> FrameWords,
    IReadOnlyList<TangentFrame> Frames, IReadOnlyList<int> ProceduralIds, IReadOnlyList<PositionBoundGate> BoundGates);
public sealed record PositionBoundGate(string Field, IReadOnlyList<int> ContributorIds, bool PositiveBoxAndSpheres);
public sealed record PositionAuthoringSource(ContentHash InputHash, IReadOnlyList<CoordinatedMember> Members,
    IReadOnlyList<PositionSourceBuffer> Buffers, PlannedPairedProceduralInputs ProceduralInputs, IReadOnlyList<PlannedTargetBlock> SourceBlocks);
public interface IPositionAuthoringSourceReader
{
    Task<PositionAuthoringSource> ReadPositionAuthoringSourceAsync(ArtifactContent input, IReadOnlyList<CoordinatedMember> members, CancellationToken token = default);
    uint EncodePositionAuthoringFrame(TangentFrame frame);
}
public sealed record PositionBundleMember(string MemberId, int Lod, int Mesh, int Buffer, string SourceDecodedHash,
    bool Editable, IReadOnlyList<string[]> Positions, IReadOnlyList<int> Triangles, IReadOnlyList<int> ProtectedIds);
public sealed record PositionAuthoringBundle(int SchemaVersion, string Kind, string InputHash, string SnapshotSha256,
    IReadOnlyList<int> RequiredLods, float MaximumDisplacement, IReadOnlyList<string> OmittedAttributes, IReadOnlyList<PositionBundleMember> Members);
public sealed record PositionAuthoringDeltaOperation(string Name, int Version);
public sealed record PositionReturnedMember(string MemberId, IReadOnlyList<string[]> Positions);
public sealed record PositionAuthoringDelta(int SchemaVersion, string Kind, PositionAuthoringDeltaOperation Operation,
    string InputHash, string BundleSha256, string BlenderVersion, string TargetKey, IReadOnlyList<PositionReturnedMember> Members);
public sealed record PositionFrameChange(int VertexId, uint SourceWord, uint ExpectedWord);
public sealed record PositionValidatedBuffer(string MemberId, int Lod, int ChangedPositions, float MaximumDisplacement,
    IReadOnlyList<int> FrameClosure, IReadOnlyList<PositionFrameChange> Frames, IReadOnlyList<int> BoundaryFrameChanges,
    ContentHash ExpectedPositionHash, ContentHash ExpectedFrameHash);
public sealed record PositionValidationResult(string Kind, ContentHash InputHash, ContentHash BundleSha256, ContentHash DeltaSha256,
    ContentHash SelectionSha256, IReadOnlyList<PositionValidatedBuffer> Buffers, bool ModelBytesBuilt, string RuntimeStatus);
public sealed record PositionAuthoringExport(string OutputRoot, ContentHash BundleSha256, ContentHash SelectionSha256, ContentHash SourceFactsSha256);
public interface IPositionAuthoringPublisher
{
    Task<PositionAuthoringExport> PublishAsync(string outputRoot, ReadOnlyMemory<byte> bundle, ReadOnlyMemory<byte> selection,
        ReadOnlyMemory<byte> sourceFacts, CancellationToken token = default);
}
public interface IPositionAuthoringApplication
{
    Task<PositionAuthoringExport> ExportPositionAuthoringAsync(string projectRoot, ReadOnlyMemory<byte> selectionBytes, string outputRoot, CancellationToken token = default);
    Task<PositionValidationResult> ValidatePositionReturnAsync(string projectRoot, ReadOnlyMemory<byte> selectionBytes,
        ReadOnlyMemory<byte> bundleBytes, ContentHash bundleHash, ReadOnlyMemory<byte> deltaBytes, CancellationToken token = default);
}
