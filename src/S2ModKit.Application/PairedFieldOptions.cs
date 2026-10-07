using System.Globalization;
using System.Text.Json;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed record PairedFieldOptions(int SchemaVersion, string Kind, int Version, ContentHash InputHash,
    RuntimeMetadataPolicy RuntimeMetadataPolicy, ZeroBoneBoxPolicy ZeroBoneBoxPolicy, ZeroRenderSpherePolicy ZeroRenderSpherePolicy,
    SourceTrianglePolicy SourceTrianglePolicy, ProceduralInputPolicy ProceduralInputPolicy,
    PairedDirectionalVisualTransform PairedTransform, float MaximumVertexDisplacement);

/// <summary>Intent only; no source discovery, planning, publication or mutation is performed.</summary>
public static class PairedFieldOptionsJson
{
    public static PairedFieldOptions Read(ReadOnlySpan<byte> json)
    {
        if (json.Length is 0 or > 64 * 1024 * 1024) throw PairedContractJson.Invalid("Paired options exceed their bounded JSON size.");
        using var document = JsonDocument.Parse(json.ToArray());
        MutationPlanJson.RequireUniqueProperties(document.RootElement);
        PairedContractJson.Shape(document.RootElement, typeof(PairedFieldOptions));
        var options = JsonDefaults.Deserialize<PairedFieldOptions>(json, "paired field options");
        _ = ToRecipe(options);
        return options;
    }
    public static string Write(PairedFieldOptions options)
    {
        _ = ToRecipe(options);
        return JsonDefaults.Serialize(options);
    }
    public static RecipeDocument ToRecipe(PairedFieldOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options is not { SchemaVersion: 1, Kind: "paired_directional_field_options", Version: 1 }
            || options.PairedTransform?.Fields is not { Count: 2 } fields || fields.Any(f => f is null))
            throw PairedContractJson.Invalid("Require complete version-1 paired field options.");
        foreach (var field in fields)
            RecipeValidator.ValidateDirectionalIntent(new(options.PairedTransform.Members, field.Field, options.PairedTransform.Protection), options.MaximumVertexDisplacement);
        var maps = options.PairedTransform.Members.SelectMany(m => m.Lods).ToArray();
        var operation = new TransformComponentOperation
        {
            OperationId = "pair",
            Version = 10,
            Granularity = "paired_directional_buffer_vertices",
            Transform = null!,
            Selector = new() { Kind = "draw_call_ids", DrawCallIds = maps.SelectMany(l => l.DrawCallIds).Order(StringComparer.Ordinal).ToArray() },
            ExpectedMatchesByLod = maps.GroupBy(l => l.Lod).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Sum(l => l.DrawCallIds.Count)),
            ExpectedVerticesByLod = maps.GroupBy(l => l.Lod).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => checked((int)g.Sum(l => (long)l.ExpectedVertices))),
            PairedTransform = options.PairedTransform,
            RuntimeMetadataPolicy = options.RuntimeMetadataPolicy,
            SourceTrianglePolicy = options.SourceTrianglePolicy,
            ProceduralInputPolicy = options.ProceduralInputPolicy,
            ZeroBoneBoxPolicy = options.ZeroBoneBoxPolicy,
            ZeroRenderSpherePolicy = options.ZeroRenderSpherePolicy,
            Limits = new() { MaximumVertexDisplacement = options.MaximumVertexDisplacement }
        };
        var recipe = new RecipeDocument { SchemaVersion = 11, RecipeId = "paired-options", InputHash = options.InputHash, Operations = [operation] };
        PairedContractValidator.ValidateRecipe(recipe);
        return recipe;
    }
}
