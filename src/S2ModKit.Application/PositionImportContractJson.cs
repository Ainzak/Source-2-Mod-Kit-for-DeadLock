using System.Text.Json;
using S2ModKit.Domain;

namespace S2ModKit.Application;

internal static class PositionImportContractJson
{
    internal static bool RecipeShape(JsonElement root, int size)
    {
        if (!EllipsoidContractJson.HasVersion(root, 12)) { EllipsoidContractJson.RejectOperationProperty(root, "positionImport"); return false; }
        if (size > PositionImportCompositionContract.MaximumRecipeBytes) throw PositionAuthoringJson.Invalid("IMPORT_JSON_BUDGET", "Recipe exceeds 16 MiB.");
        Keys(root, "schemaVersion", "recipeId", "inputHash", "operations", "extensions");
        var op = Single(root); Keys(op, "operationId", "kind", "version", "granularity", "selector", "lodPolicy", "expectedMatchesByLod", "expectedVerticesByLod", "ownershipPolicy", "runtimeMetadataPolicy", "limits", "extensions", "zeroBoneBoxPolicy", "zeroRenderSpherePolicy", "sourceTrianglePolicy", "proceduralInputPolicy", "positionImport");
        return true;
    }
    internal static MutationPlan ReadPlan(ReadOnlySpan<byte> bytes)
    {
        var p = PositionAuthoringJson.Read<MutationPlan>(bytes.ToArray());
        using var doc = JsonDocument.Parse(bytes.ToArray()); Keys(doc.RootElement, "schemaVersion", "recipeId", "inputHash", "fingerprint", "operations", "inputs");
        Keys(Single(doc.RootElement), "operationId", "kind", "version", "selectedDrawCalls", "targetBlocks", "geometryTargets", "distanceFieldTargets", "positionImportTarget");
        PositionImportContract.ValidatePlan(p); return p;
    }
    internal static bool EvidenceShape(JsonElement root, int size)
    {
        if (!EllipsoidContractJson.HasVersion(root, 13)) { EllipsoidContractJson.RejectOperationProperty(root, "positionImport"); return false; }
        Budget(size);
        MutationPlanJson.RequireProperties(root, "schemaVersion", "reportId", "createdUtc", "proofLevel", "command", "status", "input", "dependencies", "planFingerprint", "operations", "boundaries", "blocks", "toolVersions", "warnings", "extensions");
        var op = Single(root); Keys(op, "operationId", "kind", "version", "selectedDrawCallIds", "changedResources", "geometryChanges", "positionImport");
        return true;
    }
    internal static void Keys(JsonElement e, params string[] keys)
    {
        MutationPlanJson.RequireUniqueProperties(e); MutationPlanJson.RequireProperties(e, keys);
        if (e.EnumerateObject().Any(p => !keys.Contains(p.Name, StringComparer.Ordinal))) throw PositionAuthoringJson.Invalid("IMPORT_JSON_INVALID", "Unknown or mixed import property.");
    }
    private static JsonElement Single(JsonElement root)
    {
        if (!root.TryGetProperty("operations", out var ops) || ops.ValueKind != JsonValueKind.Array || ops.GetArrayLength() != 1) throw PositionAuthoringJson.Invalid("IMPORT_JSON_INVALID", "Exactly one import operation is required.");
        return ops[0];
    }
    private static void Budget(int size) { if (size > PositionAuthoringJson.MaximumArtifactBytes) throw PositionAuthoringJson.Invalid("IMPORT_JSON_BUDGET", "Import contract exceeds its 64-MiB budget."); }
}
