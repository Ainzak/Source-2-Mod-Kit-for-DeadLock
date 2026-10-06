using System.Globalization;
using System.Text.Json.Serialization;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Application;

public sealed record DirectionalScaffoldOptions(
    [property: JsonRequired] ContentHash InputHash,
    [property: JsonRequired] RuntimeMetadataPolicy Policy,
    [property: JsonRequired] ZeroBoneBoxPolicy ZeroBoneBoxPolicy,
    [property: JsonRequired] ZeroRenderSpherePolicy ZeroRenderSpherePolicy,
    [property: JsonRequired] DirectionalEllipsoidField Field,
    [property: JsonRequired] DirectionalProtection Protection,
    [property: JsonRequired] float MaximumDisplacement)
{
    [JsonRequired] public int SchemaVersion { get; init; } = 1;
    [JsonRequired] public string Kind { get; init; } = "directional_field_options";
}

public sealed record DirectionalAuthoringBuffer(string MemberId, DirectionalContextBuffer Source, IReadOnlyList<Point3> Points);
public sealed record DirectionalAuthoringBone(string Name, int Index,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DirectionalProtectionAssertion? Assertion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? UnavailableReason);
public sealed record DirectionalAuthoringSource(ContentHash InputHash, IReadOnlyList<CoordinatedMember> Members,
    IReadOnlyList<DirectionalAuthoringBuffer> Buffers, IReadOnlyList<DirectionalContextBuffer> Context,
    IReadOnlyList<DirectionalAuthoringBone> Bones);

/// <summary>Read-only source facts for explicit agent choices; this port does not admit a field or generate anatomy.</summary>
public interface IDirectionalAuthoringSourceReader
{
    Task<DirectionalAuthoringSource> ReadDirectionalAuthoringSourceAsync(ArtifactContent input,
        IReadOnlyList<CoordinatedMember> members, CancellationToken token = default);
}

public static class DirectionalAuthoring
{
    public static DirectionalScaffoldOptions ReadOptions(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json.ToArray());
            MutationPlanJson.RequireUniqueProperties(document.RootElement);
            MutationPlanJson.RequireProperties(document.RootElement, "schemaVersion", "kind", "inputHash", "policy", "zeroBoneBoxPolicy", "zeroRenderSpherePolicy", "field", "protection", "maximumDisplacement");
            DirectionalContractJson.OptionsShape(document.RootElement);
            var options = JsonDefaults.Deserialize<DirectionalScaffoldOptions>(json, "Directional field options");
            if (options.SchemaVersion != 1 || options.Kind != "directional_field_options")
                throw Errors.InvalidRecipe("DIRECTIONAL_OPTIONS_INVALID", "Unknown directional options contract.", "Use directional_field_options@1.");
            return options;
        }
        catch (System.Text.Json.JsonException e) { throw Errors.InvalidRecipe("JSON_INVALID", e.Message, "Use complete typed directional field options."); }
    }

    public static RecipeDocument CreateRecipe(ArtifactContent input, ModelSnapshot model, ComponentCandidateUnion union, DirectionalScaffoldOptions options)
    {
        if (options.SchemaVersion != 1 || options.Kind != "directional_field_options" || options.InputHash != input.ContentHash)
            throw Errors.InvalidRecipe("DIRECTIONAL_OPTIONS_INVALID", "Options version or immutable input hash differs.", "Author options from the current source inspection.");
        var members = CoordinatedSelection.ResolveMembers(model, union, 1);
        var identity = ContentHash.Compute(JsonDefaults.SerializeToUtf8(new { input.ContentHash, Members = members, Options = options })).Value[..16];
        var recipe = new RecipeDocument
        {
            SchemaVersion = 10,
            RecipeId = "scaffold-" + identity,
            InputHash = input.ContentHash,
            Operations = [new TransformComponentOperation
            {
                OperationId = "transform-" + identity, Version = 9, Granularity = "directional_buffer_vertices", Transform = null!,
                Selector = new() { Kind = "draw_call_ids", DrawCallIds = members.SelectMany(m => m.Lods).SelectMany(l => l.DrawCallIds).Order(StringComparer.Ordinal).ToArray() },
                ExpectedMatchesByLod = members.SelectMany(m => m.Lods).GroupBy(l => l.Lod).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Sum(l => l.DrawCallIds.Count)),
                ExpectedVerticesByLod = members.SelectMany(m => m.Lods).GroupBy(l => l.Lod).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Sum(l => l.ExpectedVertices)),
                RuntimeMetadataPolicy = options.Policy, ZeroBoneBoxPolicy = options.ZeroBoneBoxPolicy, ZeroRenderSpherePolicy = options.ZeroRenderSpherePolicy,
                DirectionalTransform = new(members, options.Field, options.Protection), Limits = new() { MaximumVertexDisplacement = options.MaximumDisplacement },
            }],
        };
        DirectionalContractValidator.ValidateRecipe(recipe);
        return recipe;
    }

    public static async Task<ComponentDiscoveryResultV2> DiscoverAsync(ArtifactContent input, ModelSnapshot model, CancellationToken token = default)
    {
        // Do not invent a field/protection pair to turn a structural mapping into claimed admission.
        var strict = await new PreciseComponentDiscoveryService(null).DiscoverAsync(input, model, token).ConfigureAwait(false);
        var candidates = strict.Candidates.Select(candidate =>
        {
            ComponentCapability capability;
            try
            {
                var union = ComponentCandidateUnionBuilder.Create(model, [candidate], 2);
                _ = CoordinatedSelection.ResolveMembers(model, union, 1);
                capability = new("transform_component", 9, "unsupported", [new("DIRECTIONAL_OPTIONS_REQUIRED", "Complete structural mapping found; authored field/protection and exact planner admission are required.")], []);
            }
            catch (S2ModKitException e) { capability = new("transform_component", 9, "unsupported", [new(e.Error.Code, e.Error.Summary)], []); }
            var id = candidate switch
            {
                MaterialGroupComponentCandidateV2 m => ComponentCandidateIdentity.ComputeMaterialCandidateId(m.Model, m.MaterialPath, m.Lods.ToArray(), 6),
                MeshLineageComponentCandidateV2 m => ComponentCandidateIdentity.ComputeLineageCandidateId(m.Model, m.LineageKey, m.SourceLabel, m.Lods.ToArray(), 6),
                _ => throw new InvalidOperationException("Unknown candidate kind."),
            };
            return candidate with { CandidateId = id, Capabilities = [capability] };
        }).ToArray();
        return strict with
        {
            SchemaVersion = 6,
            Candidates = candidates,
            ExperimentalPolicy = new("preserve_unverified", 1),
            DiscoveryFingerprint = ComponentCandidateIdentity.ComputeDiscoveryFingerprint(strict.Model, strict.Analyzer, candidates, strict.LineageDiagnostics, 6)
        };
    }
}
