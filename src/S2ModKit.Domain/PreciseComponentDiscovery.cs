using System.Text.Json;
using System.Text.Json.Serialization;

namespace S2ModKit.Domain;

public static class ComponentDiscoveryV2Contract
{
    public const int SchemaVersion = 2;

    public const string MaterialGroupKind = ComponentDiscoveryContract.MaterialGroupKind;

    public const string MeshLineageKind = "mesh_lineage";

    public const string CandidateUnionKind = "candidate_union";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(MaterialGroupComponentCandidateV2), ComponentDiscoveryV2Contract.MaterialGroupKind)]
[JsonDerivedType(typeof(MeshLineageComponentCandidateV2), ComponentDiscoveryV2Contract.MeshLineageKind)]
public abstract record ComponentCandidateV2(
    string CandidateId,
    ComponentModelIdentity Model,
    string DisplayLabel,
    IReadOnlyList<ComponentCapability> Capabilities)
{
    [JsonIgnore]
    public abstract string Kind { get; }

    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } =
        new Dictionary<string, JsonElement>();
}

public sealed record MaterialGroupComponentCandidateV2 : ComponentCandidateV2
{
    public MaterialGroupComponentCandidateV2(
        string candidateId,
        ComponentModelIdentity model,
        string materialPath,
        string displayLabel,
        IReadOnlyList<ComponentCandidateLod> lods,
        IReadOnlyList<ComponentCapability> capabilities)
        : base(candidateId, model, displayLabel, capabilities)
    {
        MaterialPath = materialPath;
        Lods = lods;
    }

    [JsonIgnore]
    public override string Kind => ComponentDiscoveryV2Contract.MaterialGroupKind;

    public string MaterialPath { get; }

    public IReadOnlyList<ComponentCandidateLod> Lods { get; }
}

public sealed record MeshLineageCandidateLod(
    int Lod,
    string ResourcePath,
    int MeshOrdinal,
    int ResourceBlockIndex,
    ContentHash ImmutableSemanticHash,
    string SourceName,
    IReadOnlyList<string> MaterialPaths,
    IReadOnlyList<string> DrawCallIds,
    int DrawCallCount);

public sealed record MeshLineageComponentCandidateV2 : ComponentCandidateV2
{
    public MeshLineageComponentCandidateV2(
        string candidateId,
        ComponentModelIdentity model,
        string lineageKey,
        string sourceLabel,
        string displayLabel,
        IReadOnlyList<string> materialPaths,
        IReadOnlyList<MeshLineageCandidateLod> lods,
        IReadOnlyList<ComponentCapability> capabilities)
        : base(candidateId, model, displayLabel, capabilities)
    {
        LineageKey = lineageKey;
        SourceLabel = sourceLabel;
        MaterialPaths = materialPaths;
        Lods = lods;
    }

    [JsonIgnore]
    public override string Kind => ComponentDiscoveryV2Contract.MeshLineageKind;

    public string LineageKey { get; }

    public string SourceLabel { get; }

    public IReadOnlyList<string> MaterialPaths { get; }

    public IReadOnlyList<MeshLineageCandidateLod> Lods { get; }
}

public sealed record ComponentLineageDiagnostic(
    string? LineageKey,
    string Code,
    string Summary,
    IReadOnlyList<int> ObservedLods);

public sealed record ComponentDiscoveryResultV2(
    int SchemaVersion,
    ComponentModelIdentity Model,
    ContentHash DiscoveryFingerprint,
    ComponentCapabilityAnalyzerIdentity Analyzer,
    IReadOnlyList<ComponentCandidateV2> Candidates,
    IReadOnlyList<ComponentLineageDiagnostic> LineageDiagnostics)
{
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } =
        new Dictionary<string, JsonElement>();
}

public sealed record ComponentCandidateUnion(
    IReadOnlyList<string> CandidateIds,
    IReadOnlyList<string> CandidateKinds,
    IReadOnlyList<string> MaterialPaths,
    IReadOnlyList<SelectedDrawCall> SelectedDrawCalls);
