using System.Reflection;
using System.Text.Json;
using S2ModKit.Domain;

namespace S2ModKit.Application;

internal static class CoordinatedContractJson
{
    internal static bool RecipeShape(JsonElement root)
    {
        if (!EllipsoidContractJson.HasVersion(root, 9))
        {
            foreach (var name in new[] { "coordinatedTransform", "zeroBoneBoxPolicy", "zeroRenderSpherePolicy" })
                EllipsoidContractJson.RejectOperationProperty(root, name);
            return false;
        }
        MutationPlanJson.RequireUniqueProperties(root);
        MutationPlanJson.RequireProperties(root, "schemaVersion", "recipeId", "inputHash", "operations", "extensions");
        Shape(root.GetProperty("inputHash"), typeof(ContentHash));
        var operation = Single(root);
        MutationPlanJson.RequireProperties(operation, "kind", "operationId", "version", "granularity", "selector", "lodPolicy",
            "expectedMatchesByLod", "expectedVerticesByLod", "ownershipPolicy", "runtimeMetadataPolicy", "coordinatedTransform",
            "zeroBoneBoxPolicy", "zeroRenderSpherePolicy", "limits", "extensions");
        foreach (var name in new[] { "transform", "localTransform", "region", "connectedComponentIdsByLod", "physicsPolicy" })
            EllipsoidContractJson.RejectProperty(operation, name);
        Shape(operation.GetProperty("coordinatedTransform"), typeof(CoordinatedVisualTransform));
        Shape(operation.GetProperty("zeroBoneBoxPolicy"), typeof(ZeroBoneBoxPolicy));
        Shape(operation.GetProperty("zeroRenderSpherePolicy"), typeof(ZeroRenderSpherePolicy));
        Shape(operation.GetProperty("runtimeMetadataPolicy"), typeof(RuntimeMetadataPolicy));
        MutationPlanJson.RequireProperties(operation.GetProperty("limits"), "maximumVertexDisplacement");
        EllipsoidContractJson.RejectProperty(operation.GetProperty("limits"), "maximumCollisionDisplacement");
        return true;
    }

    internal static MutationPlan ReadPlan(JsonElement root, ReadOnlySpan<byte> json)
    {
        MutationPlanJson.RequireUniqueProperties(root);
        MutationPlanJson.RequireProperties(root, "schemaVersion", "recipeId", "inputHash", "fingerprint", "operations", "inputs");
        Shape(root.GetProperty("inputHash"), typeof(ContentHash));
        Shape(root.GetProperty("fingerprint"), typeof(ContentHash));
        Shape(root.GetProperty("inputs"), typeof(IReadOnlyList<PlannedInput>));
        var operation = Single(root);
        MutationPlanJson.RequireProperties(operation, "operationId", "kind", "version", "selectedDrawCalls", "targetBlocks", "geometryTargets",
            "distanceFieldTargets", "coordinatedTransformTarget");
        foreach (var name in new[] { "experimentalTransformTarget", "ellipsoidTransformTarget", "affineTransformTarget", "coupledTransformTarget" })
            EllipsoidContractJson.RejectProperty(operation, name);
        Shape(operation.GetProperty("selectedDrawCalls"), typeof(IReadOnlyList<SelectedDrawCall>));
        Shape(operation.GetProperty("targetBlocks"), typeof(IReadOnlyList<PlannedTargetBlock>));
        Shape(operation.GetProperty("coordinatedTransformTarget"), typeof(PlannedCoordinatedTransformTarget));
        var plan = JsonSerializer.Deserialize<MutationPlan>(json, JsonDefaults.Options) ?? throw Invalid("Null coordinated plan.");
        CoordinatedContractValidator.ValidatePlan(plan);
        if (plan.Fingerprint != MutationPlanJson.ComputeExperimentalFingerprint(plan)) throw Invalid("Plan fingerprint drift.");
        return plan;
    }

    internal static bool EvidenceShape(JsonElement root)
    {
        if (!EllipsoidContractJson.HasVersion(root, 10))
        {
            EllipsoidContractJson.RejectOperationProperty(root, "coordinatedTransform");
            return false;
        }
        MutationPlanJson.RequireUniqueProperties(root);
        MutationPlanJson.RequireProperties(root, "schemaVersion", "reportId", "createdUtc", "proofLevel", "command", "status", "input", "dependencies",
            "planFingerprint", "operations", "boundaries", "blocks", "toolVersions", "warnings", "extensions");
        Shape(root.GetProperty("planFingerprint"), typeof(ContentHash));
        Shape(root.GetProperty("input"), typeof(ArtifactEvidence));
        if (root.TryGetProperty("output", out var output)) Shape(output, typeof(ArtifactEvidence));
        var operation = Single(root);
        MutationPlanJson.RequireProperties(operation, "operationId", "kind", "version", "selectedDrawCallIds", "changedResources", "geometryChanges", "coordinatedTransform");
        foreach (var name in new[] { "experimentalTransform", "ellipsoidTransform", "affineTransform", "coupledTransform" })
            EllipsoidContractJson.RejectProperty(operation, name);
        Shape(operation.GetProperty("coordinatedTransform"), typeof(CoordinatedTransformEvidence));
        return true;
    }

    private static JsonElement Single(JsonElement root)
    {
        if (root.GetProperty("operations") is not { ValueKind: JsonValueKind.Array } operations || operations.GetArrayLength() != 1)
            throw Invalid("Exactly one coordinated operation is required.");
        return operations[0];
    }

    private static void Shape(JsonElement value, Type type, bool allowNull = false)
    {
        if (value.ValueKind == JsonValueKind.Null) { if (allowNull) return; throw Invalid("Required coordinated facts cannot be null."); }
        if (Nullable.GetUnderlyingType(type) is { } underlying) type = underlying;
        if (type == typeof(ContentHash))
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: 64 } hash
                || hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw Invalid("Canonical SHA-256 identity is required.");
            return;
        }
        if (type == typeof(CoordinatedField))
        {
            MutationPlanJson.RequireProperties(value, "kind");
            type = value.GetProperty("kind").GetString() switch
            {
                "ellipsoid" => typeof(CoordinatedEllipsoidField),
                "axis_ramp" => typeof(CoordinatedAxisRampField),
                "tilted_ramp" => typeof(CoordinatedTiltedRampField),
                _ => throw Invalid("Unknown common field variant."),
            };
        }
        if (type == typeof(string) || type.IsValueType) return;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            if (value.ValueKind != JsonValueKind.Array) throw Invalid("Required coordinated facts must be an array.");
            foreach (var child in value.EnumerateArray()) Shape(child, type.GetGenericArguments()[0]);
            return;
        }
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("Required coordinated facts must be an object.");
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            if (type == typeof(EllipsoidField) && name == "mirrorPlane" && !value.TryGetProperty(name, out _)) continue;
            if (type == typeof(ComponentSelector) && name != "kind") continue;
            if (!value.TryGetProperty(name, out var child)) throw Invalid($"Required coordinated fact '{name}' is missing.");
            Shape(child, property.PropertyType, name is "ellipsoidCertificate" or "tiltedCertificate" or "observedBuffers" or "observedWords"
                or "observedPayloadHash" or "observedStorage" or "observedWord");
        }
    }

    internal static S2ModKitException Invalid(string message) => Errors.InvalidRecipe("COORDINATED_CONTRACT_INVALID", message,
        "Regenerate strict coordinated facts from immutable input with complete member/LOD and metadata closure.");
}
