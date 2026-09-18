using System.Text.Json;

namespace S2ModKit.Domain;

public static class ComponentDiscoveryContract
{
    public const int SchemaVersion = 1;

    public const string MaterialGroupKind = "material_group";

    public const string Available = CapabilityAvailability.Available;

    public const string Blocked = CapabilityAvailability.Blocked;

    public const string Unsupported = CapabilityAvailability.Unsupported;

    public const string Ambiguous = CapabilityAvailability.Ambiguous;
}

public sealed record ComponentModelIdentity(
    string LogicalPath,
    ContentHash ContentHash,
    long Size);

public sealed record ComponentCapabilityAnalyzerIdentity(
    string Name,
    string Version,
    IReadOnlyDictionary<string, string> ComponentVersions);

public sealed record ComponentCapabilityReason(
    string Code,
    string Summary);

public sealed record ComponentGeometryLodFacts(
    int Lod,
    int SelectedVertexCount,
    ContentHash VertexSetHash,
    bool ExclusivelyOwned);

public sealed record ComponentCapability(
    string OperationKind,
    int OperationVersion,
    string Availability,
    IReadOnlyList<ComponentCapabilityReason> Reasons,
    IReadOnlyList<ComponentGeometryLodFacts> GeometryByLod);

public sealed record ComponentCandidateLod(
    int Lod,
    IReadOnlyList<string> DrawCallIds,
    int DrawCallCount);

public sealed record ComponentCandidate(
    string CandidateId,
    string Kind,
    ComponentModelIdentity Model,
    string MaterialPath,
    string DisplayLabel,
    IReadOnlyList<ComponentCandidateLod> Lods,
    IReadOnlyList<ComponentCapability> Capabilities)
{
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } =
        new Dictionary<string, JsonElement>();
}

public sealed record ComponentDiscoveryResult(
    int SchemaVersion,
    ComponentModelIdentity Model,
    ContentHash DiscoveryFingerprint,
    ComponentCapabilityAnalyzerIdentity Analyzer,
    IReadOnlyList<ComponentCandidate> Candidates)
{
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } =
        new Dictionary<string, JsonElement>();
}
