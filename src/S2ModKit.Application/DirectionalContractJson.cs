using System.Reflection;
using System.Text.Json;
using S2ModKit.Domain;

namespace S2ModKit.Application;

internal static class DirectionalContractJson
{
    internal static void OptionsShape(JsonElement root) => Shape(root, typeof(DirectionalScaffoldOptions));

    internal static bool RecipeShape(JsonElement root)
    {
        if (!EllipsoidContractJson.HasVersion(root, 10))
        {
            EllipsoidContractJson.RejectOperationProperty(root, "directionalTransform");
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("operations", out var ops) && ops.ValueKind == JsonValueKind.Array
                && ops.EnumerateArray().Any(op => op.ValueKind == JsonValueKind.Object && op.TryGetProperty("version", out var v)
                    && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var version) && version == 9)) throw Invalid("Directional operations require recipe schema 10.");
            return false;
        }
        MutationPlanJson.RequireUniqueProperties(root);
        MutationPlanJson.RequireProperties(root, "schemaVersion", "recipeId", "inputHash", "operations", "extensions");
        Shape(root.GetProperty("inputHash"), typeof(ContentHash));
        var op = Single(root);
        MutationPlanJson.RequireProperties(op, "kind", "operationId", "version", "granularity", "selector", "lodPolicy", "expectedMatchesByLod",
            "expectedVerticesByLod", "ownershipPolicy", "runtimeMetadataPolicy", "directionalTransform", "zeroBoneBoxPolicy", "zeroRenderSpherePolicy", "limits", "extensions");
        Reject(op, "transform", "region", "localTransform", "coordinatedTransform", "physicsPolicy", "connectedComponentIdsByLod");
        Shape(op.GetProperty("directionalTransform"), typeof(DirectionalVisualTransform));
        Shape(op.GetProperty("runtimeMetadataPolicy"), typeof(RuntimeMetadataPolicy));
        Shape(op.GetProperty("zeroBoneBoxPolicy"), typeof(ZeroBoneBoxPolicy));
        Shape(op.GetProperty("zeroRenderSpherePolicy"), typeof(ZeroRenderSpherePolicy));
        Shape(op.GetProperty("selector"), typeof(ComponentSelector));
        MutationPlanJson.RequireProperties(op.GetProperty("limits"), "maximumVertexDisplacement");
        Reject(op.GetProperty("limits"), "maximumCollisionDisplacement");
        return true;
    }

    internal static MutationPlan ReadPlan(JsonElement root, ReadOnlySpan<byte> json)
    {
        MutationPlanJson.RequireUniqueProperties(root);
        MutationPlanJson.RequireProperties(root, "schemaVersion", "recipeId", "inputHash", "fingerprint", "operations", "inputs");
        Shape(root.GetProperty("inputHash"), typeof(ContentHash));
        Shape(root.GetProperty("fingerprint"), typeof(ContentHash));
        Shape(root.GetProperty("inputs"), typeof(IReadOnlyList<PlannedInput>));
        var op = Single(root);
        MutationPlanJson.RequireProperties(op, "operationId", "kind", "version", "selectedDrawCalls", "targetBlocks", "geometryTargets", "distanceFieldTargets", "directionalTransformTarget");
        Reject(op, "experimentalTransformTarget", "ellipsoidTransformTarget", "coordinatedTransformTarget", "affineTransformTarget", "coupledTransformTarget");
        Shape(op.GetProperty("selectedDrawCalls"), typeof(IReadOnlyList<SelectedDrawCall>));
        Shape(op.GetProperty("targetBlocks"), typeof(IReadOnlyList<PlannedTargetBlock>));
        Shape(op.GetProperty("directionalTransformTarget"), typeof(PlannedDirectionalTransformTarget));
        var plan = JsonSerializer.Deserialize<MutationPlan>(json, JsonDefaults.Options) ?? throw Invalid("Null directional plan.");
        DirectionalContractValidator.ValidatePlan(plan);
        return plan;
    }

    internal static bool EvidenceShape(JsonElement root)
    {
        if (!EllipsoidContractJson.HasVersion(root, 11)) { EllipsoidContractJson.RejectOperationProperty(root, "directionalTransform"); return false; }
        MutationPlanJson.RequireUniqueProperties(root);
        MutationPlanJson.RequireProperties(root, "schemaVersion", "reportId", "createdUtc", "proofLevel", "command", "status", "input", "dependencies",
            "planFingerprint", "operations", "boundaries", "blocks", "toolVersions", "warnings", "extensions");
        Shape(root.GetProperty("planFingerprint"), typeof(ContentHash));
        Shape(root.GetProperty("input"), typeof(ArtifactEvidence));
        if (root.TryGetProperty("output", out var output)) Shape(output, typeof(ArtifactEvidence));
        var op = Single(root);
        MutationPlanJson.RequireProperties(op, "operationId", "kind", "version", "selectedDrawCallIds", "changedResources", "geometryChanges", "directionalTransform");
        Reject(op, "experimentalTransform", "ellipsoidTransform", "coordinatedTransform", "affineTransform", "coupledTransform");
        Shape(op.GetProperty("directionalTransform"), typeof(DirectionalTransformEvidence));
        return true;
    }

    private static JsonElement Single(JsonElement root)
    {
        if (root.GetProperty("operations") is not { ValueKind: JsonValueKind.Array } ops || ops.GetArrayLength() != 1)
            throw Invalid("Exactly one directional operation is required.");
        return ops[0];
    }

    private static void Reject(JsonElement value, params string[] names)
    {
        foreach (var name in names) EllipsoidContractJson.RejectProperty(value, name);
    }

    private static void Shape(JsonElement value, Type type, bool allowNull = false)
    {
        if (value.ValueKind == JsonValueKind.Null) { if (allowNull) return; throw Invalid("Required directional facts cannot be null."); }
        if (Nullable.GetUnderlyingType(type) is { } underlying) type = underlying;
        if (type == typeof(ContentHash))
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: 64 } h
                || h.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw Invalid("Canonical lowercase SHA-256 is required.");
            return;
        }
        if (type == typeof(string) || type.IsValueType) return;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 1_000_000) throw Invalid("Invalid or oversized directional collection.");
            foreach (var child in value.EnumerateArray()) Shape(child, type.GetGenericArguments()[0]);
            return;
        }
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("Required directional facts must be an object.");
        if (type == typeof(DirectionalProtectionAssertion))
        {
            MutationPlanJson.RequireProperties(value, "kind");
            if (value.GetProperty("kind").ValueKind != JsonValueKind.String) throw Invalid("Invalid assertion discriminator.");
            type = value.GetProperty("kind").GetString() switch
            {
                "vertex_set" => typeof(DirectionalVertexAssertion),
                "root_bone_contributors" => typeof(DirectionalBoneAssertion),
                _ => throw Invalid("Unknown assertion variant."),
            };
        }
        if (type == typeof(TransformPivot))
        {
            MutationPlanJson.RequireProperties(value, "kind");
            if (value.GetProperty("kind").ValueKind != JsonValueKind.String) throw Invalid("Invalid pivot discriminator.");
            string[] names = value.GetProperty("kind").GetString() switch
            {
                "explicit_point" => ["kind", "point"],
                "selection_bounds_center" => ["kind", "referenceLod"],
                "bounds_face" => ["kind", "referenceLod", "face"],
                "bone_origin" => ["kind", "referenceLod", "boneName"],
                _ => throw Invalid("Unknown typed pivot."),
            };
            MutationPlanJson.RequireProperties(value, names);
            if (value.EnumerateObject().Any(p => !names.Contains(p.Name))) throw Invalid("Typed pivot contains a forbidden field, including null.");
            if (value.TryGetProperty("point", out var point)) Shape(point, typeof(TransformVector3));
            return;
        }
        if (type == typeof(ComponentSelector))
        {
            MutationPlanJson.RequireProperties(value, "kind", "drawCallIds");
            Reject(value, "materialPath");
            Shape(value.GetProperty("drawCallIds"), typeof(IReadOnlyList<string>));
            return;
        }
        foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(p.Name);
            if (!value.TryGetProperty(name, out var child)) throw Invalid($"Required directional fact '{name}' is missing.");
            Shape(child, p.PropertyType, name is "observed" or "observedWords" or "observedPayloadHash" || (type == typeof(DirectionalPivotEvidence) && name == "referenceLod"));
        }
    }

    internal static S2ModKitException Invalid(string message) => Errors.InvalidRecipe("DIRECTIONAL_INTENT_INVALID", message,
        "Regenerate explicit versioned directional contracts with complete source/protection/context facts.");
}
