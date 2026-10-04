using System.Reflection;
using System.Text.Json;
using S2ModKit.Domain;

namespace S2ModKit.Application;

/// <summary>Strict dispatch before deserialization can substitute defaults or discard null variants.</summary>
internal static class EllipsoidContractJson
{
    internal static bool RecipeShape(JsonElement root)
    {
        if (!HasVersion(root, 8)) { RejectOperationProperty(root, "localTransform"); return false; }
        MutationPlanJson.RequireUniqueProperties(root);
        MutationPlanJson.RequireProperties(root, "schemaVersion", "recipeId", "inputHash", "operations", "extensions");
        RequireTypedShape(root.GetProperty("inputHash"), typeof(ContentHash));
        var operation = SingleOperation(root);
        MutationPlanJson.RequireProperties(operation, "operationId", "kind", "version", "granularity", "selector", "lodPolicy",
            "expectedMatchesByLod", "expectedVerticesByLod", "ownershipPolicy", "runtimeMetadataPolicy", "localTransform", "limits", "extensions");
        foreach (var name in new[] { "transform", "region", "physicsPolicy", "connectedComponentIdsByLod" }) RejectProperty(operation, name);
        RequireTypedShape(operation.GetProperty("localTransform"), typeof(EllipsoidVisualTransform));
        RequireTypedShape(operation.GetProperty("runtimeMetadataPolicy"), typeof(RuntimeMetadataPolicy));
        MutationPlanJson.RequireProperties(operation.GetProperty("limits"), "maximumVertexDisplacement");
        RejectProperty(operation.GetProperty("limits"), "maximumCollisionDisplacement");
        return true;
    }

    internal static MutationPlan ReadPlan(JsonElement root, ReadOnlySpan<byte> json)
    {
        MutationPlanJson.RequireUniqueProperties(root);
        MutationPlanJson.RequireProperties(root, "schemaVersion", "recipeId", "inputHash", "fingerprint", "operations", "inputs");
        var operation = SingleOperation(root);
        MutationPlanJson.RequireProperties(operation, "operationId", "kind", "version", "selectedDrawCalls", "targetBlocks", "geometryTargets", "distanceFieldTargets", "ellipsoidTransformTarget");
        RequireTypedShape(root.GetProperty("inputHash"), typeof(ContentHash));
        RequireTypedShape(root.GetProperty("fingerprint"), typeof(ContentHash));
        RejectMixedTargets(operation);
        RequireTypedShape(operation.GetProperty("ellipsoidTransformTarget"), typeof(PlannedEllipsoidTransformTarget));
        RequireTypedShape(operation.GetProperty("selectedDrawCalls"), typeof(IReadOnlyList<SelectedDrawCall>));
        RequireTypedShape(operation.GetProperty("targetBlocks"), typeof(IReadOnlyList<PlannedTargetBlock>));
        RequireTypedShape(root.GetProperty("inputs"), typeof(IReadOnlyList<PlannedInput>));
        var plan = JsonSerializer.Deserialize<MutationPlan>(json, JsonDefaults.Options) ?? throw Invalid("Null plan.");
        EllipsoidContractValidator.ValidatePlan(plan);
        if (plan.Fingerprint != MutationPlanJson.ComputeExperimentalFingerprint(plan)) throw Invalid("Plan fingerprint drift.");
        return plan;
    }

    internal static bool EvidenceShape(JsonElement root)
    {
        if (!HasVersion(root, 9)) { RejectOperationProperty(root, "ellipsoidTransform"); return false; }
        MutationPlanJson.RequireUniqueProperties(root);
        MutationPlanJson.RequireProperties(root, "schemaVersion", "reportId", "createdUtc", "proofLevel", "command", "status", "input", "dependencies",
            "planFingerprint", "operations", "boundaries", "blocks", "toolVersions", "warnings", "extensions");
        var operation = SingleOperation(root);
        MutationPlanJson.RequireProperties(operation, "operationId", "kind", "version", "selectedDrawCallIds", "changedResources", "geometryChanges", "ellipsoidTransform");
        RequireTypedShape(root.GetProperty("planFingerprint"), typeof(ContentHash));
        RequireTypedShape(root.GetProperty("input"), typeof(ArtifactEvidence));
        if (root.TryGetProperty("output", out var output)) RequireTypedShape(output, typeof(ArtifactEvidence));
        foreach (var name in new[] { "experimentalTransform", "affineTransform", "coupledTransform" }) RejectProperty(operation, name);
        RequireTypedShape(operation.GetProperty("ellipsoidTransform"), typeof(EllipsoidTransformEvidence));
        return true;
    }

    internal static bool HasVersion(JsonElement root, int version) => root.ValueKind == JsonValueKind.Object
        && root.EnumerateObject().Any(p => p.Name == "schemaVersion" && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var v) && v == version);

    internal static void RejectProperty(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out _))
            throw Invalid($"Property '{name}' is forbidden in this contract, including null.");
    }

    internal static void RejectOperationProperty(JsonElement root, string name)
    {
        // Arbitrary historical extension data is not an operation property and
        // must retain its old wire meaning, even when a key resembles a new name.
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("operations", out var operations) && operations.ValueKind == JsonValueKind.Array)
            foreach (var operation in operations.EnumerateArray()) RejectProperty(operation, name);
    }

    private static void RejectMixedTargets(JsonElement operation)
    {
        foreach (var name in new[] { "experimentalTransformTarget", "affineTransformTarget", "coupledTransformTarget" }) RejectProperty(operation, name);
        foreach (var name in new[] { "geometryTargets", "distanceFieldTargets" })
            if (operation.TryGetProperty(name, out var value) && (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 0))
                throw Invalid("Ellipsoid targets cannot mix legacy target arrays.");
    }

    private static JsonElement SingleOperation(JsonElement root)
    {
        if (!root.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() != 1)
            throw Invalid("Exactly one ellipsoid operation is required.");
        return operations[0];
    }

    private static void RequireTypedShape(JsonElement value, Type type, bool allowNull = false)
    {
        if (value.ValueKind == JsonValueKind.Null) { if (allowNull) return; throw Invalid("Required ellipsoid facts cannot be null."); }
        if (Nullable.GetUnderlyingType(type) is { } underlying) type = underlying;
        if (type == typeof(ContentHash))
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: 64 } hash || hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw Invalid("Content hashes require canonical lowercase SHA-256.");
            return;
        }
        if (type == typeof(string) || type.IsValueType) return;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            if (value.ValueKind != JsonValueKind.Array) throw Invalid("Required facts must be an array.");
            foreach (var child in value.EnumerateArray()) RequireTypedShape(child, type.GetGenericArguments()[0]);
            return;
        }
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("Required facts must be an object.");
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            if (name is "mirrorPlane" or "mirror" or "mirroredMasks" && !value.TryGetProperty(name, out _)) continue;
            // Selector variants retain their historical mutually exclusive optional properties.
            if (type == typeof(ComponentSelector) && name != "kind") continue;
            if (!value.TryGetProperty(name, out var child)) throw Invalid($"Required ellipsoid fact '{name}' is missing.");
            var observed = name is "observedBuffers" or "observedWords" or "observedPayloadHash";
            RequireTypedShape(child, property.PropertyType, observed);
        }
    }

    internal static S2ModKitException Invalid(string message) => Errors.InvalidRecipe("ELLIPSOID_CONTRACT_INVALID", message,
        "Regenerate complete versioned ellipsoid facts from immutable inputs.");
}
