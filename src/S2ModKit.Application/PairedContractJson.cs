using System.Text.Json;
using S2ModKit.Domain;

namespace S2ModKit.Application;

internal static class PairedContractJson
{
    internal static bool RecipeShape(JsonElement root, int jsonLength)
    {
        if (!EllipsoidContractJson.HasVersion(root, 11))
        {
            foreach (var property in new[] { "pairedTransform", "sourceTrianglePolicy", "proceduralInputPolicy" })
                EllipsoidContractJson.RejectOperationProperty(root, property);
            return false;
        }
        RequireBudget(jsonLength);
        MutationPlanJson.RequireUniqueProperties(root);
        MutationPlanJson.RequireProperties(root, "schemaVersion", "recipeId", "inputHash", "operations", "extensions");
        Shape(root.GetProperty("inputHash"), typeof(ContentHash));
        var op = Single(root);
        MutationPlanJson.RequireProperties(op, "kind", "operationId", "version", "granularity", "selector", "lodPolicy", "expectedMatchesByLod",
            "expectedVerticesByLod", "ownershipPolicy", "runtimeMetadataPolicy", "pairedTransform", "sourceTrianglePolicy", "proceduralInputPolicy",
            "zeroBoneBoxPolicy", "zeroRenderSpherePolicy", "limits", "extensions");
        Reject(op, "transform", "directionalTransform", "region", "localTransform", "coordinatedTransform", "physicsPolicy", "connectedComponentIdsByLod");
        Shape(op.GetProperty("pairedTransform"), typeof(PairedDirectionalVisualTransform));
        foreach (var pair in new[] { ("runtimeMetadataPolicy", typeof(RuntimeMetadataPolicy)), ("sourceTrianglePolicy", typeof(SourceTrianglePolicy)),
            ("proceduralInputPolicy", typeof(ProceduralInputPolicy)), ("zeroBoneBoxPolicy", typeof(ZeroBoneBoxPolicy)), ("zeroRenderSpherePolicy", typeof(ZeroRenderSpherePolicy)),
            ("selector", typeof(ComponentSelector)) }) Shape(op.GetProperty(pair.Item1), pair.Item2);
        MutationPlanJson.RequireProperties(op.GetProperty("limits"), "maximumVertexDisplacement");
        Reject(op.GetProperty("limits"), "maximumCollisionDisplacement");
        return true;
    }

    internal static MutationPlan ReadPlan(JsonElement root, ReadOnlySpan<byte> json)
    {
        RequireBudget(json.Length);
        MutationPlanJson.RequireUniqueProperties(root);
        MutationPlanJson.RequireProperties(root, "schemaVersion", "recipeId", "inputHash", "fingerprint", "operations", "inputs");
        Shape(root.GetProperty("inputHash"), typeof(ContentHash)); Shape(root.GetProperty("fingerprint"), typeof(ContentHash));
        Shape(root.GetProperty("inputs"), typeof(IReadOnlyList<PlannedInput>));
        var op = Single(root);
        MutationPlanJson.RequireProperties(op, "operationId", "kind", "version", "selectedDrawCalls", "targetBlocks", "geometryTargets", "distanceFieldTargets", "pairedTransformTarget");
        Reject(op, "directionalTransformTarget", "experimentalTransformTarget", "ellipsoidTransformTarget", "coordinatedTransformTarget", "affineTransformTarget", "coupledTransformTarget");
        Shape(op.GetProperty("selectedDrawCalls"), typeof(IReadOnlyList<SelectedDrawCall>));
        Shape(op.GetProperty("targetBlocks"), typeof(IReadOnlyList<PlannedTargetBlock>));
        Shape(op.GetProperty("pairedTransformTarget"), typeof(PlannedPairedTransformTarget));
        var plan = JsonSerializer.Deserialize<MutationPlan>(json, JsonDefaults.Options) ?? throw Invalid("Null paired plan.");
        PairedContractValidator.ValidatePlan(plan);
        return plan;
    }

    internal static bool EvidenceShape(JsonElement root, int jsonLength)
    {
        if (!EllipsoidContractJson.HasVersion(root, 12)) { EllipsoidContractJson.RejectOperationProperty(root, "pairedTransform"); return false; }
        RequireBudget(jsonLength);
        MutationPlanJson.RequireUniqueProperties(root);
        MutationPlanJson.RequireProperties(root, "schemaVersion", "reportId", "createdUtc", "proofLevel", "command", "status", "input", "dependencies",
            "planFingerprint", "operations", "boundaries", "blocks", "toolVersions", "warnings", "extensions");
        Shape(root.GetProperty("planFingerprint"), typeof(ContentHash)); Shape(root.GetProperty("input"), typeof(ArtifactEvidence));
        if (root.TryGetProperty("output", out var output)) Shape(output, typeof(ArtifactEvidence));
        var op = Single(root);
        MutationPlanJson.RequireProperties(op, "operationId", "kind", "version", "selectedDrawCallIds", "changedResources", "geometryChanges", "pairedTransform");
        Reject(op, "directionalTransform", "experimentalTransform", "ellipsoidTransform", "coordinatedTransform", "affineTransform", "coupledTransform");
        Shape(op.GetProperty("pairedTransform"), typeof(PairedDirectionalTransformEvidence));
        return true;
    }

    internal static void Shape(JsonElement value, Type type) => DirectionalContractJson.Shape(value, type);
    internal static void RequireBudget(int bytes)
    {
        if (bytes is 0 or > 64 * 1024 * 1024) throw Invalid("Paired JSON exceeds its 64-MiB artifact budget.");
    }
    internal static bool IsPairedArtifact<T>(T value) => value is RecipeDocument { SchemaVersion: 11 } or MutationPlan { SchemaVersion: 7 }
        or EvidenceReport { SchemaVersion: 12 } or PairedFieldOptions;
    private static JsonElement Single(JsonElement root)
    {
        if (root.GetProperty("operations") is not { ValueKind: JsonValueKind.Array } ops || ops.GetArrayLength() != 1)
            throw Invalid("Exactly one paired operation is required.");
        return ops[0];
    }
    private static void Reject(JsonElement value, params string[] names)
    {
        foreach (var name in names) EllipsoidContractJson.RejectProperty(value, name);
    }
    internal static S2ModKitException Invalid(string message) => Errors.InvalidRecipe("PAIRED_CONTRACT_INVALID", message,
        "Regenerate complete explicit operation-10 contracts from immutable source; contract parsing alone grants no resource capability.");
}
