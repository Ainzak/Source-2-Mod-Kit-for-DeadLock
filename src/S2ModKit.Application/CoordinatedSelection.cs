using System.Globalization;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed record CoordinatedScaffoldOptions(
    [property: System.Text.Json.Serialization.JsonRequired] RuntimeMetadataPolicy Policy,
    [property: System.Text.Json.Serialization.JsonRequired] ZeroBoneBoxPolicy ZeroBoneBoxPolicy,
    [property: System.Text.Json.Serialization.JsonRequired] ZeroRenderSpherePolicy ZeroRenderSpherePolicy,
    [property: System.Text.Json.Serialization.JsonRequired] CoordinatedField Field,
    [property: System.Text.Json.Serialization.JsonRequired] float MaximumDisplacement)
{
    [System.Text.Json.Serialization.JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    [System.Text.Json.Serialization.JsonRequired]
    public string Kind { get; init; } = "coordinated_field_options";
}
public sealed record CoordinatedSelectionProbe(ContentHash DiscoveryFingerprint, IReadOnlyList<string> ComponentIds,
    IReadOnlyList<CoordinatedMember> Members, ComponentCapability Capability);

/// <summary>Maps exact selections by authored mesh lineage and unique whole-buffer material membership.
/// Buffer ordinals and vertex counts are frozen facts, never cross-LOD correspondence guesses.</summary>
public static class CoordinatedSelection
{
    public static CoordinatedScaffoldOptions ReadOptions(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json.ToArray());
            MutationPlanJson.RequireUniqueProperties(document.RootElement);
            MutationPlanJson.RequireProperties(document.RootElement, "schemaVersion", "kind", "policy", "zeroBoneBoxPolicy", "zeroRenderSpherePolicy", "field", "maximumDisplacement");
            var options = JsonDefaults.Deserialize<CoordinatedScaffoldOptions>(json, "Coordinated field options");
            ValidateOptions(options);
            return options;
        }
        catch (System.Text.Json.JsonException error)
        {
            throw Errors.InvalidRecipe("JSON_INVALID", error.Message, "Use a complete typed coordinated field options document.");
        }
    }

    public static void ValidateOptions(CoordinatedScaffoldOptions options)
    {
        if (options.SchemaVersion != 1 || options.Kind != "coordinated_field_options" || options.Policy is not { Kind: "preserve_unverified", Version: 1 }
            || options.ZeroBoneBoxPolicy is not { Version: 1, Kind: "reject" or "preserve_all_zero_single_contributor_unverified" }
            || options.ZeroRenderSpherePolicy is not { Version: 1, Kind: "reject" or "preserve_zero_render_sphere_with_zero_box_unverified" }
            || (options.ZeroRenderSpherePolicy.Kind != "reject" && options.ZeroBoneBoxPolicy.Kind == "reject"))
            throw Errors.InvalidRecipe("COORDINATED_POLICIES_INVALID", "Declare each preservation policy explicitly, including both rejection choices.", "Use the typed acknowledged options; policies are never inferred from the input.");
        _ = new CoordinatedFieldMath(options.Field, options.MaximumDisplacement);
    }
    public static IReadOnlyList<CoordinatedMember> ResolveMembers(ModelSnapshot model, ComponentCandidateUnion union)
        => ResolveMembers(model, union, 2);

    internal static IReadOnlyList<CoordinatedMember> ResolveMembers(ModelSnapshot model, ComponentCandidateUnion union, int minimumMembers)
    {
        var selected = union.SelectedDrawCalls.Select(c => c.DrawCallId).ToHashSet(StringComparer.Ordinal);
        var maps = new List<(string Key, CoordinatedMemberLod Lod)>();
        foreach (var lod in model.Lods.OrderBy(l => l.Level))
        {
            foreach (var mesh in lod.Meshes.Where(m => m.DrawCalls.Any(d => selected.Contains(d.Id))))
            {
                if (mesh.MechanicalLineage is not { } lineage || !lineage.IsCanonical()
                    || lod.Meshes.Count(m => m.MechanicalLineage?.Key == lineage.Key) != 1
                    || model.Lods.Any(l => l.Meshes.Count(m => m.MechanicalLineage?.Key == lineage.Key
                        && m.MechanicalLineage.SourceLabel == lineage.SourceLabel) != 1)
                    || mesh.Geometry is not { Status: "ready" } geometry)
                    throw Invalid("COORDINATED_MEMBER_MAPPING_UNAVAILABLE", "A unique complete authored mesh lineage and decoded geometry are required in every LOD.");
                var signatures = geometry.DrawCalls.GroupBy(d => d.VertexBufferOrdinal).Select(g => new
                {
                    Buffer = g.Key,
                    Draws = g.ToArray(),
                    Materials = mesh.DrawCalls.Where(d => g.Any(x => x.DrawCallId == d.Id)).Select(d => d.MaterialPath).Distinct().Order(StringComparer.Ordinal).ToArray(),
                }).ToArray();
                foreach (var buffer in signatures.Where(g => g.Draws.Any(d => selected.Contains(d.DrawCallId))))
                {
                    if (buffer.Draws.Any(d => !selected.Contains(d.DrawCallId))
                        || signatures.Count(s => s.Materials.SequenceEqual(buffer.Materials)) != 1)
                        throw Invalid("COORDINATED_MEMBER_MAPPING_AMBIGUOUS", "Partial/shared buffers or repeated whole-buffer material membership cannot establish an exact member mapping.");
                    var key = JsonDefaults.Serialize(new { lineage.Key, lineage.SourceLabel, buffer.Materials });
                    var vertices = geometry.VertexBuffers.Single(b => b.Ordinal == buffer.Buffer);
                    maps.Add((key, new(lod.Level, buffer.Draws.Select(d => d.DrawCallId).Order(StringComparer.Ordinal).ToArray(), vertices.VertexCount)));
                }
            }
        }
        var levels = model.Lods.Select(l => l.Level).Order().ToArray();
        var members = maps.GroupBy(m => m.Key, StringComparer.Ordinal).Select(g =>
        {
            var lods = g.Select(m => m.Lod).OrderBy(l => l.Lod).ToArray();
            if (!lods.Select(l => l.Lod).SequenceEqual(levels))
                throw Invalid("COORDINATED_MEMBER_LOD_INCOMPLETE", "Whole-buffer material membership changes or is absent across present LODs.");
            return new CoordinatedMember("member-" + ContentHash.Compute(System.Text.Encoding.UTF8.GetBytes(g.Key)).Value[..24], lods);
        }).OrderBy(m => m.MemberId, StringComparer.Ordinal).ToArray();
        if (members.Length < minimumMembers || members.Length > 16) throw Invalid("COORDINATED_MEMBER_COUNT_INVALID", minimumMembers == 2 ? "Coordination requires 2 through 16 complete all-LOD ordinary members." : "Directionality requires 1 through 16 complete all-LOD ordinary members.");
        return members;
    }

    public static RecipeDocument CreateRecipe(ArtifactContent input, ModelSnapshot model, ComponentCandidateUnion union, CoordinatedScaffoldOptions options)
    {
        ValidateOptions(options);
        var members = ResolveMembers(model, union);
        var identity = ContentHash.Compute(JsonDefaults.SerializeToUtf8(new { input.ContentHash, Members = members, Options = options })).Value[..16];
        var recipe = new RecipeDocument
        {
            SchemaVersion = 9,
            RecipeId = "scaffold-" + identity,
            InputHash = input.ContentHash,
            Operations = [new TransformComponentOperation
            {
                OperationId = "transform-" + identity, Version = 8, Granularity = "coordinated_buffer_vertices", Transform = null!,
                Selector = new() { Kind = "draw_call_ids", DrawCallIds = members.SelectMany(m => m.Lods).SelectMany(l => l.DrawCallIds).Order(StringComparer.Ordinal).ToArray() },
                ExpectedMatchesByLod = members.SelectMany(m => m.Lods).GroupBy(l => l.Lod).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Sum(l => l.DrawCallIds.Count)),
                ExpectedVerticesByLod = members.SelectMany(m => m.Lods).GroupBy(l => l.Lod).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Sum(l => l.ExpectedVertices)),
                RuntimeMetadataPolicy = options.Policy, ZeroBoneBoxPolicy = options.ZeroBoneBoxPolicy, ZeroRenderSpherePolicy = options.ZeroRenderSpherePolicy,
                CoordinatedTransform = new(members, options.Field), Limits = new() { MaximumVertexDisplacement = options.MaximumDisplacement },
            }],
        };
        CoordinatedContractValidator.ValidateRecipe(recipe);
        return recipe;
    }

    internal static S2ModKitException Invalid(string code, string message) => Errors.Selection(code, message,
        "Choose complete uniquely mapped ordinary buffer members in every LOD; an exact union still requires the ordinary planner.");
}
