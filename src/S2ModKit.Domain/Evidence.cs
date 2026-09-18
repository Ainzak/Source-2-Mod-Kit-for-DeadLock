using System.Text.Json;
using System.Text.Json.Serialization;

namespace S2ModKit.Domain;

public sealed record ArtifactEvidence(
    string LogicalPath,
    ContentHash ContentHash,
    long Size,
    string SourceKind = "generated",
    string CatalogIdentity = "generated",
    string VerificationMode = "sha256");

public sealed record OperationEvidence(
    string OperationId,
    string Kind,
    int Version,
    IReadOnlyList<string> SelectedDrawCallIds,
    IReadOnlyList<string> ChangedResources)
{
    public IReadOnlyList<GeometryChangeEvidence> GeometryChanges { get; init; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CoupledTransformEvidence? CoupledTransform { get; init; }
}

public sealed record CoupledTransformEvidence(
    string PhysicsPolicy,
    TransformVector3 Pivot,
    float UniformScale,
    CoupledTransformHalfEvidence Visual,
    CoupledTransformHalfEvidence Collision,
    IReadOnlyList<string> ChangedByteClasses);

public sealed record CoupledTransformHalfEvidence(
    int ResourceBlockIndex,
    int VertexCount,
    ContentHash InputPositionHash,
    ContentHash ExpectedPositionHash,
    GeometryBounds BeforeBounds,
    GeometryBounds AfterBounds,
    float MaximumDisplacement,
    float DisplacementLimit);

public sealed record GeometryChangeEvidence(
    int Lod,
    string ResourcePath,
    int MeshOrdinal,
    int ResourceBlockIndex,
    ContentHash VertexSetHash,
    int VertexCount,
    GeometryBounds BeforeBounds,
    GeometryBounds AfterBounds,
    TransformVector3 Pivot,
    float UniformScale,
    TransformVector3 Translation,
    float MaximumDisplacement,
    IReadOnlyList<string> ChangedAttributes,
    ContentHash InputVertexBlockHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ContentHash? OutputVertexBlockHash,
    GeometryCodecIdentity Codec)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContentHash? InputDecodedVertexBufferHash { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContentHash? ExpectedDecodedVertexBufferHash { get; init; }
}

public sealed record BoundaryEvidence(
    string Name,
    string Status,
    string Summary);

public sealed record ResourceBlockEvidence(
    int Index,
    string Type,
    ContentHash InputHash,
    ContentHash? OutputHash,
    string Disposition);

public sealed record EvidenceReport
{
    public int SchemaVersion { get; init; } = 5;

    public string ReportId { get; init; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; init; }

    public string ProofLevel { get; init; } = "offline_static";

    public string Command { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public required ArtifactEvidence Input { get; init; }

    public IReadOnlyList<ArtifactEvidence> Dependencies { get; init; } = [];

    public ArtifactEvidence? Output { get; init; }

    public ContentHash PlanFingerprint { get; init; }

    public IReadOnlyList<OperationEvidence> Operations { get; init; } = [];

    public IReadOnlyList<BoundaryEvidence> Boundaries { get; init; } = [];

    public IReadOnlyList<ResourceBlockEvidence> Blocks { get; init; } = [];

    public IReadOnlyDictionary<string, string> ToolVersions { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}
