using System.Text.Json;
using System.Text.Json.Serialization;

namespace S2ModKit.Domain;

public static class CullingEnvelopeContract
{
    public const int InventorySchemaVersion = 1;
    public const int PlanSchemaVersion = 1;
    public const int EvidenceSchemaVersion = 1;
    public const string PolicyId = "conservative_culling_envelope";
    public const int PolicyVersion = 1;
    public const string ContributorIdentityAlgorithm = "sha256/source2-culling-contributors-v1";
}

public sealed record CullingEnvelopePolicyIdentity(string Id, int Version);

public sealed record CullingResourceIdentity(
    string LogicalPath,
    ContentHash ContentHash,
    long Size);

public sealed record CullingFieldIdentity(
    string ResourcePath,
    ContentHash ResourceHash,
    int ResourceBlockIndex,
    string ResourceBlockType,
    string FieldPath,
    int? MeshOrdinal,
    int? FieldOrdinal,
    IReadOnlyList<int> CoveredLods)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? MeshOrdinal { get; init; } = MeshOrdinal;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? FieldOrdinal { get; init; } = FieldOrdinal;
}

public sealed record CullingRawFieldValue(
    string Kind,
    TransformVector3? Minimum,
    TransformVector3? Maximum,
    TransformVector3? Center,
    TransformVector3? HalfExtents,
    float? Radius)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TransformVector3? Minimum { get; init; } = Minimum;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TransformVector3? Maximum { get; init; } = Maximum;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TransformVector3? Center { get; init; } = Center;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TransformVector3? HalfExtents { get; init; } = HalfExtents;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public float? Radius { get; init; } = Radius;
}

public sealed record CullingCoordinateSpaceIdentity(
    string Status,
    string? SpaceId,
    ContentHash? MatrixIdentity,
    IReadOnlyList<float>? Matrix3x4,
    string? ReasonCode)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? SpaceId { get; init; } = SpaceId;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ContentHash? MatrixIdentity { get; init; } = MatrixIdentity;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public IReadOnlyList<float>? Matrix3x4 { get; init; } = Matrix3x4;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ReasonCode { get; init; } = ReasonCode;
}

public sealed record CullingContributorSourceIdentity(
    string ResourcePath,
    ContentHash ResourceHash,
    IReadOnlyList<int> CoveredLods,
    int MeshOrdinal,
    int MeshResourceBlockIndex,
    int VertexBufferOrdinal,
    int VertexResourceBlockIndex,
    int IndexBufferOrdinal,
    int IndexResourceBlockIndex,
    int VertexCount,
    int ContributorCount,
    ContentHash VertexBlockHash,
    ContentHash DecodedVertexBufferHash,
    ContentHash IndexBlockHash,
    ContentHash DecodedIndexBufferHash,
    uint? BlendIndexFormat,
    uint? BlendWeightFormat,
    string BoneRemapStatus,
    ContentHash? BoneRemapIdentity,
    int? ResolvedModelBoneIndex)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public uint? BlendIndexFormat { get; init; } = BlendIndexFormat;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public uint? BlendWeightFormat { get; init; } = BlendWeightFormat;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ContentHash? BoneRemapIdentity { get; init; } = BoneRemapIdentity;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? ResolvedModelBoneIndex { get; init; } = ResolvedModelBoneIndex;
}

public sealed record CullingContributorSetIdentity(
    string Status,
    string? IdentityAlgorithm,
    ContentHash? IdentityHash,
    int? ContributorCount,
    IReadOnlyList<CullingContributorSourceIdentity>? Sources,
    string? ReasonCode)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? IdentityAlgorithm { get; init; } = IdentityAlgorithm;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ContentHash? IdentityHash { get; init; } = IdentityHash;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? ContributorCount { get; init; } = ContributorCount;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public IReadOnlyList<CullingContributorSourceIdentity>? Sources { get; init; } = Sources;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ReasonCode { get; init; } = ReasonCode;
}

public sealed record CullingMeshBoneRemapInventory(
    string ResourcePath,
    ContentHash ResourceHash,
    int MeshOrdinal,
    int MeshResourceBlockIndex,
    IReadOnlyList<int> CoveredLods,
    string BoneRemapStatus,
    ContentHash? BoneRemapIdentity,
    IReadOnlyList<int>? BoneRemapValues,
    string RenderBoneNamesStatus,
    IReadOnlyList<string>? RenderBoneNames,
    string ModelBoneNamesStatus,
    IReadOnlyList<string>? ModelBoneNames,
    string? ReasonCode)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ContentHash? BoneRemapIdentity { get; init; } = BoneRemapIdentity;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public IReadOnlyList<int>? BoneRemapValues { get; init; } = BoneRemapValues;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public IReadOnlyList<string>? RenderBoneNames { get; init; } = RenderBoneNames;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public IReadOnlyList<string>? ModelBoneNames { get; init; } = ModelBoneNames;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ReasonCode { get; init; } = ReasonCode;
}

public sealed record CullingInventoryField(
    CullingFieldIdentity Identity,
    string Status,
    CullingRawFieldValue? RawOriginalValue,
    CullingCoordinateSpaceIdentity? CoordinateSpace,
    CullingContributorSetIdentity? Contributors)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public CullingRawFieldValue? RawOriginalValue { get; init; } = RawOriginalValue;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public CullingCoordinateSpaceIdentity? CoordinateSpace { get; init; } = CoordinateSpace;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public CullingContributorSetIdentity? Contributors { get; init; } = Contributors;
}

public sealed record CullingInventoryDocument(
    int SchemaVersion,
    CullingResourceIdentity Resource,
    string Status,
    IReadOnlyList<CullingInventoryField> Fields,
    IReadOnlyList<CullingMeshBoneRemapInventory> BoneRemaps,
    IReadOnlyDictionary<string, JsonElement> Extensions);

public sealed record ConservativeCullingPlanTarget(
    CullingFieldIdentity Identity,
    CullingRawFieldValue OriginalValue,
    CullingCoordinateSpaceIdentity CoordinateSpace,
    CullingContributorSetIdentity Contributors,
    CullingRawFieldValue PlannedOutputValue,
    IReadOnlyList<CullingGrowthMeasurement> Growth);

public sealed record CullingGrowthMeasurement(
    string Measure,
    float OriginalValue,
    float PlannedValue,
    float Delta);

public sealed record ConservativeCullingPlanDocument(
    int SchemaVersion,
    string PlanId,
    CullingEnvelopePolicyIdentity Policy,
    CullingResourceIdentity Input,
    IReadOnlyList<ConservativeCullingPlanTarget> Targets,
    IReadOnlyDictionary<string, JsonElement> Extensions);

public sealed record ConservativeCullingTargetEvidence(
    CullingFieldIdentity Identity,
    CullingRawFieldValue OriginalValue,
    CullingCoordinateSpaceIdentity CoordinateSpace,
    CullingContributorSetIdentity Contributors,
    CullingRawFieldValue PlannedOutputValue,
    CullingRawFieldValue? ObservedOutputValue,
    CullingVerificationResult Verification)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public CullingRawFieldValue? ObservedOutputValue { get; init; } = ObservedOutputValue;
}

public sealed record CullingVerificationResult(
    string Status,
    bool? OriginalEnvelopeContained,
    bool? GeometryContained,
    bool? PolicyOutputMatched,
    string? ResultCode)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? OriginalEnvelopeContained { get; init; } = OriginalEnvelopeContained;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? GeometryContained { get; init; } = GeometryContained;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? PolicyOutputMatched { get; init; } = PolicyOutputMatched;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ResultCode { get; init; } = ResultCode;
}

public sealed record ConservativeCullingEvidenceDocument(
    int SchemaVersion,
    CullingEnvelopePolicyIdentity Policy,
    ContentHash PlanFingerprint,
    CullingResourceIdentity Input,
    CullingResourceIdentity? Output,
    IReadOnlyList<ConservativeCullingTargetEvidence> Targets,
    IReadOnlyDictionary<string, JsonElement> Extensions)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public CullingResourceIdentity? Output { get; init; } = Output;
}
