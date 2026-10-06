using System.Text.Json;
using System.Text.Json.Serialization;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json, string description)
    {
        try
        {
            if (typeof(T) == typeof(MutationPlan))
            {
                return (T)(object)MutationPlanJson.Read(utf8Json);
            }

            if (typeof(T) == typeof(RecipeDocument))
            {
                ValidateExperimentalRecipeShape(utf8Json);
            }
            if (typeof(T) == typeof(EvidenceReport)) ExperimentalEvidenceValidator.ValidateShape(utf8Json);
            if (typeof(T) == typeof(GuidedWorkflowSession)) GuidedWorkflow.ValidateSessionShape(utf8Json);
            if (typeof(T) == typeof(ComponentDiscoveryResultV2)) EllipsoidDiscoveryJson.ValidateShape(utf8Json);

            var result = JsonSerializer.Deserialize<T>(utf8Json, Options)
                ?? throw Errors.InvalidRecipe("JSON_NULL_DOCUMENT", $"{description} cannot be null.", "Provide a JSON object matching the published schema.");
            if (result is RecipeDocument { SchemaVersion: 8 or 9 or 10 } recipe)
            {
                // Shape dispatch already forbids a serialized legacy transform, including
                // null. Remove only the old CLR initializer revived by its absence.
                recipe = recipe with
                {
                    Operations = recipe.Operations.Select(operation => operation is TransformComponentOperation transform
                    ? transform with { Transform = null! } : operation).ToArray()
                };
                if (recipe.SchemaVersion == 10) DirectionalContractValidator.ValidateRecipe(recipe);
                else if (recipe.SchemaVersion == 9) CoordinatedContractValidator.ValidateRecipe(recipe);
                else EllipsoidContractValidator.ValidateRecipe(recipe);
                result = (T)(object)recipe;
            }
            if (result is EvidenceReport evidence)
            {
                if (evidence.SchemaVersion == 11) DirectionalContractValidator.ValidateEvidence(evidence);
                else if (evidence.SchemaVersion == 10) CoordinatedContractValidator.ValidateEvidence(evidence);
                else if (evidence.SchemaVersion == 9) EllipsoidContractValidator.ValidateEvidence(evidence);
                else ExperimentalEvidenceValidator.Validate(evidence);
            }
            if (result is ComponentDiscoveryResultV2 discovery) EllipsoidDiscoveryJson.Validate(discovery);
            if (result is GuidedWorkflowSession { SchemaVersion: 3 or 4 } session) GuidedWorkflow.ValidateSession(session);
            return result;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new S2ModKitException(
                new S2Error("JSON_INVALID", "schema", $"{description} is not valid: {exception.Message}", "Validate the document against the corresponding schema.", ErrorCategory.CliOrSchema),
                exception);
        }
    }

    public static byte[] SerializeToUtf8<T>(T value)
    {
        ValidateDirectionalWrite(value);
        return JsonSerializer.SerializeToUtf8Bytes(value, Options);
    }

    public static string Serialize<T>(T value)
    {
        ValidateDirectionalWrite(value);
        return JsonSerializer.Serialize(value, Options);
    }

    private static void ValidateDirectionalWrite<T>(T value)
    {
        if (value is RecipeDocument oldRecipe && oldRecipe.SchemaVersion != 10
            && oldRecipe.Operations?.OfType<TransformComponentOperation>().Any(op => op.Version == 9 || op.DirectionalTransform is not null) == true)
            throw DirectionalContractJson.Invalid("Directional intent cannot be written in an old recipe.");
        if (value is MutationPlan oldPlan && oldPlan.SchemaVersion != 6 && oldPlan.Operations?.Any(op => op is { Version: 9 } || op?.DirectionalTransformTarget is not null) == true)
            throw DirectionalContractJson.Invalid("Directional targets cannot be written in an old plan.");
        if (value is EvidenceReport oldReport && oldReport.SchemaVersion != 11 && oldReport.Operations?.Any(op => op is { Version: 9 } || op?.DirectionalTransform is not null) == true)
            throw DirectionalContractJson.Invalid("Directional evidence cannot be written in an old report.");
        if (value is RecipeDocument { SchemaVersion: 10 } recipe) DirectionalContractValidator.ValidateRecipe(recipe);
        if (value is MutationPlan { SchemaVersion: 6 } plan) DirectionalContractValidator.ValidatePlan(plan);
        if (value is EvidenceReport { SchemaVersion: 11 } report) DirectionalContractValidator.ValidateEvidence(report);
    }

    private static void ValidateExperimentalRecipeShape(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        if (DirectionalContractJson.RecipeShape(root)) return;
        if (CoordinatedContractJson.RecipeShape(root)) return;
        if (EllipsoidContractJson.RecipeShape(root)) return;
        var versions = root.EnumerateObject().Where(property => property.Name == "schemaVersion").ToArray();
        var regionRecipe = versions.Any(property => property.Value.ValueKind == JsonValueKind.Number
            && property.Value.TryGetInt32(out var v) && v == 7);
        if (!regionRecipe && root.TryGetProperty("operations", out var oldRegionOperations)
            && oldRegionOperations.ValueKind == JsonValueKind.Array
            && oldRegionOperations.EnumerateArray().Any(operation => operation.ValueKind == JsonValueKind.Object && operation.TryGetProperty("region", out _)))
            throw Errors.InvalidRecipe("REGION_SELECTION_UNSUPPORTED", "Earlier recipes cannot contain region fields, including null.", "Use the separately versioned region contract.");
        // Do not reinterpret old readers. A duplicated discriminator involving version 6 cannot
        // smuggle the new fields through last-property-wins legacy deserialization.
        if (!versions.Any(property => property.Value.ValueKind == JsonValueKind.Number
            && property.Value.TryGetInt32(out var version) && version is 6 or 7))
        {
            if (root.TryGetProperty("operations", out var legacyOperations) && legacyOperations.ValueKind == JsonValueKind.Array
                && legacyOperations.EnumerateArray().Any(operation => operation.ValueKind == JsonValueKind.Object
                    && operation.TryGetProperty("runtimeMetadataPolicy", out _)))
            {
                throw Errors.InvalidRecipe("RUNTIME_METADATA_POLICY_UNSUPPORTED", "Legacy recipes cannot contain runtimeMetadataPolicy, including null.", "Use the unchanged strict recipe contract or explicitly choose schemaVersion 6.");
            }

            return;
        }
        MutationPlanJson.RequireUniqueProperties(root);
        MutationPlanJson.RequireProperties(root, "schemaVersion", "recipeId", "inputHash", "operations", "extensions");
        var operations = root.GetProperty("operations");
        if (operations.ValueKind != JsonValueKind.Array) throw Errors.InvalidRecipe("EXPERIMENTAL_RECIPE_SCOPE_INVALID", "Experimental operations must be an array.", "Use one explicit version-5 operation.");
        foreach (var operation in operations.EnumerateArray())
        {
            MutationPlanJson.RequireProperties(operation, "operationId", "kind", "version", "granularity", "selector", "lodPolicy",
                "expectedMatchesByLod", "expectedVerticesByLod", "ownershipPolicy", "runtimeMetadataPolicy", "transform", "limits", "extensions");
            MutationPlanJson.RequireProperties(operation.GetProperty("transform"), "pivot", "uniformScale", "translation");
            MutationPlanJson.RequireProperties(operation.GetProperty("transform").GetProperty("translation"), "x", "y", "z");
            MutationPlanJson.RequireProperties(operation.GetProperty("limits"), "maximumVertexDisplacement");
            if (regionRecipe)
            {
                MutationPlanJson.RequireProperties(operation, "region");
                MutationPlanJson.RequireProperties(operation.GetProperty("region"), "kind", "version", "axis", "pinnedThrough", "fullFrom");
            }
        }
    }

    private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        AllowOutOfOrderMetadataProperties = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
