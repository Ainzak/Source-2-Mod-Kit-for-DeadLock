using System.Text.Json;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed record PairedPreviewBufferSummary(DirectionalContextBuffer Source,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? MemberId,
    IReadOnlyList<string> DrawCallIds, int TriangleCount, int ProtectedCount, ContentHash ProtectedIndexHash,
    int ProceduralCount, ContentHash ProceduralIndexHash);
public sealed record PairedPreviewSummary(int SchemaVersion, string Kind, string ProjectionProfile, string ProofLevel,
    ContentHash InputHash, ContentHash PlanFingerprint, ContentHash TargetFingerprint, ContentHash PreviewFingerprint,
    IReadOnlyList<PairedDirectionalField> Fields, IReadOnlyList<PairedRegionMeasurement> Regions, ContentHash ContactSheetHash,
    IReadOnlyList<PairedPreviewBufferSummary> Buffers, IReadOnlyList<string> Limitations);

public static class PairedPreviewSummaryJson
{
    public static PairedPreviewSummary Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > 64 * 1024 * 1024) throw Invalid("Comparison summary exceeds its bounded budget.");
        try
        {
            using var doc = JsonDocument.Parse(bytes.ToArray());
            MutationPlanJson.RequireUniqueProperties(doc.RootElement);
            ShapeSummary(doc.RootElement, typeof(PairedPreviewSummary));
            var value = JsonDefaults.Deserialize<PairedPreviewSummary>(bytes, "Paired comparison summary");
            Validate(value); return value;
        }
        catch (JsonException e) { throw Invalid(e.Message); }
    }
    public static byte[] Write(PairedPreviewSummary value)
    {
        Validate(value); var bytes = JsonDefaults.SerializeToUtf8(value);
        if (bytes.Length > 64 * 1024 * 1024) throw Invalid("Comparison summary exceeds its bounded budget.");
        return bytes;
    }
    private static void Validate(PairedPreviewSummary value)
    {
        if (value is not
            {
                SchemaVersion: 1, Kind: "paired_surface_comparison", ProjectionProfile: PairedSelectionPreview.ProjectionProfile,
                ProofLevel: "bind_space_prediction", Fields.Count: 2, Regions.Count: > 0, Buffers.Count: > 0, Limitations.Count: > 0
            }
            || new[] { value.InputHash, value.PlanFingerprint, value.TargetFingerprint, value.PreviewFingerprint, value.ContactSheetHash }.Any(h => h.Value is not { Length: 64 } || !h.Value.All(Uri.IsHexDigit))
            || value.Fields.Select(f => f.FieldId).Distinct(StringComparer.Ordinal).Count() != 2
            || value.Regions.Any(r => r.ChangedPositions <= 0 || r.ChangedFrames < 0 || r.ProtectedRecords <= 0 || r.ProceduralRecords < 0
                || r.Lod < 0 || r.Lod >= 8 || !value.Fields.Any(f => f.FieldId == r.FieldId)
                || !float.IsFinite(r.MaximumDisplacement) || r.MaximumDisplacement <= 0 || r.MaximumDisplacement > 64
                || new[] { r.XChangePercent, r.YChangePercent, r.ZChangePercent }.Any(n => n is { } v && !double.IsFinite(v))
                || !ValidSpan(r.SpanBefore) || !ValidSpan(r.SpanAfter)
                || new[] { r.PositiveReachBefore, r.PositiveReachAfter, r.NegativeReachBefore, r.NegativeReachAfter }.Any(v => !ValidSpan(v))
                || !ValidChange(r.SpanBefore.X, r.SpanAfter.X, r.XChangePercent)
                || !ValidChange(r.SpanBefore.Y, r.SpanAfter.Y, r.YChangePercent)
                || !ValidChange(r.SpanBefore.Z, r.SpanAfter.Z, r.ZChangePercent))
            || value.Regions.GroupBy(r => r.Lod).Any(g => g.Count() != 2 || g.Select(r => r.FieldId).Distinct(StringComparer.Ordinal).Count() != 2)
            || value.Buffers.Any(b => b.TriangleCount <= 0 || b.ProtectedCount < 0 || b.ProceduralCount < 0
                || b.ProtectedCount > b.Source.VertexCount || b.ProceduralCount > b.Source.VertexCount
                || b.Source.Selected != (b.MemberId is not null) || b.DrawCallIds.Count == 0
                || b.DrawCallIds.Count != b.DrawCallIds.Distinct(StringComparer.Ordinal).Count()
                || new[] { b.ProtectedIndexHash, b.ProceduralIndexHash }.Any(h => h.Value is not { Length: 64 } || !h.Value.All(Uri.IsHexDigit)))
            || value.Buffers.Select(b => (b.Source.Lod, b.Source.MeshOrdinal, b.Source.VertexBufferOrdinal)).Distinct().Count() != value.Buffers.Count
            || !value.Buffers.Select(b => b.Source.Lod).Distinct().Order().SequenceEqual(value.Regions.Select(r => r.Lod).Distinct().Order())
            || value.Regions.Any(r => r.ProtectedRecords != value.Buffers.Where(b => b.Source.Lod == r.Lod).Sum(b => b.ProtectedCount)
                || r.ProceduralRecords != value.Buffers.Where(b => b.Source.Lod == r.Lod).Sum(b => b.ProceduralCount)))
            throw Invalid("Complete source-bound paired prediction with explicit limitations is required.");
        foreach (var field in value.Fields) _ = DirectionalContractValidator.Certificate(field.Field);
        _ = PairedContractValidator.Separation(value.Fields);
    }
    private static void ShapeSummary(JsonElement value, Type type)
    {
        if (type != typeof(PairedPreviewSummary) && type != typeof(PairedRegionMeasurement) && type != typeof(PairedPreviewBufferSummary))
        { PairedContractJson.Shape(value, type); return; }
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("Expected a complete typed summary record.");
        var properties = type.GetProperties().Where(p => p.GetMethod is not null).ToArray();
        string Name(System.Reflection.PropertyInfo p) => JsonNamingPolicy.CamelCase.ConvertName(p.Name);
        var names = properties.Select(Name).ToArray();
        if (names.Any(name => !value.TryGetProperty(name, out _))) throw Invalid("Required summary property is missing.");
        if (value.EnumerateObject().Any(p => !names.Contains(p.Name, StringComparer.Ordinal))) throw Invalid("Unknown summary property.");
        foreach (var p in properties)
        {
            var child = value.GetProperty(Name(p));
            if (child.ValueKind == JsonValueKind.Null && (p.Name == "MemberId" || Nullable.GetUnderlyingType(p.PropertyType) == typeof(double))) continue;
            if (p.PropertyType == typeof(IReadOnlyList<PairedRegionMeasurement>) || p.PropertyType == typeof(IReadOnlyList<PairedPreviewBufferSummary>))
            {
                if (child.ValueKind != JsonValueKind.Array) throw Invalid("Expected complete summary records.");
                foreach (var row in child.EnumerateArray()) ShapeSummary(row, p.PropertyType.GetGenericArguments()[0]);
            }
            else PairedContractJson.Shape(child, p.PropertyType);
        }
    }
    private static bool ValidSpan(TransformVector3 p) => new[] { p.X, p.Y, p.Z }.All(v => float.IsFinite(v) && v >= 0);
    private static bool ValidChange(float before, float after, double? change) => before == 0 ? change is null : change == 100 * ((double)after / before - 1);
    private static S2ModKitException Invalid(string message) => Errors.InvalidRecipe("PAIRED_PREVIEW_SUMMARY_INVALID", message, "Regenerate the closed version-1 bind-space summary.");
}
