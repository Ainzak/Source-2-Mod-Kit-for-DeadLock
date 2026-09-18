using System.Globalization;
using System.Text.RegularExpressions;

namespace S2ModKit.Domain;

public static partial class RecipeValidator
{
    public const float MaximumTransformDisplacement = 256f;

    public static void Validate(RecipeDocument recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        if (recipe.SchemaVersion is not (1 or 2 or 3 or 4))
        {
            throw Errors.InvalidRecipe("SCHEMA_VERSION_UNSUPPORTED", "Only recipe schemaVersion 1, 2, 3, and 4 are supported.", "Migrate the recipe to a published schema version.");
        }

        ValidateIdentifier(recipe.RecipeId, "recipeId");
        if (recipe.Extensions is null)
        {
            throw Errors.InvalidRecipe("EXTENSIONS_INVALID", "Recipe extensions must be an object.", "Use an empty object when no extensions are required.");
        }

        if (recipe.Operations is null || recipe.Operations.Count == 0)
        {
            throw Errors.InvalidRecipe("RECIPE_EMPTY", "A recipe must contain at least one operation.", "Add a supported typed operation.");
        }

        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in recipe.Operations)
        {
            if (operation is null)
            {
                throw Errors.InvalidRecipe("OPERATION_INVALID", "Recipe operations cannot contain null.", "Remove the null entry and add a supported typed operation.");
            }

            if (recipe.SchemaVersion == 1 && operation is not RemoveComponentOperation)
            {
                throw Errors.InvalidRecipe("SCHEMA_OPERATION_MISMATCH", "Recipe schemaVersion 1 supports only remove_component@1.", "Use schemaVersion 2 for transform_component@1.");
            }


            if (recipe.SchemaVersion < 3 && operation is TransformComponentOperation { Version: 2 })
            {
                throw Errors.InvalidRecipe("SCHEMA_OPERATION_MISMATCH", "transform_component@2 requires recipe schemaVersion 3.", "Use schemaVersion 3 for the coupled visual/collision transform.");
            }

            if (recipe.SchemaVersion < 4 && operation is TransformComponentOperation { Version: 3 })
            {
                throw Errors.InvalidRecipe("SCHEMA_OPERATION_MISMATCH", "transform_component@3 requires recipe schemaVersion 4.", "Use schemaVersion 4 for a connected-component vertex transform.");
            }

            ValidateOperation(operation);
            if (!operationIds.Add(operation.OperationId))
            {
                throw Errors.InvalidRecipe("OPERATION_ID_DUPLICATE", $"Operation id '{operation.OperationId}' is duplicated.", "Use a unique operationId for every operation.");
            }
        }
    }

    private static void ValidateOperation(RecipeOperation operation)
    {
        ValidateIdentifier(operation.OperationId, "operationId");
        if (operation.Extensions is null)
        {
            throw Errors.InvalidRecipe("EXTENSIONS_INVALID", $"Operation '{operation.OperationId}' extensions must be an object.", "Use an empty object when no extensions are required.");
        }

        ValidateCommonOperation(operation);
        switch (operation)
        {
            case RemoveComponentOperation remove:
                ValidateRemoveComponent(remove);
                break;
            case TransformComponentOperation transform:
                ValidateTransformComponent(transform);
                break;
            default:
                throw Errors.Unsupported("OPERATION_UNSUPPORTED", $"Operation kind '{operation.OperationKind}' is not supported.", "Use a published operation kind and version.");
        }
    }

    private static void ValidateCommonOperation(RecipeOperation operation)
    {
        if (operation.LodPolicy != "all_present")
        {
            throw Errors.InvalidRecipe("LOD_POLICY_UNSUPPORTED", "Phase 1 requires lodPolicy all_present.", "Set lodPolicy to all_present and declare every LOD count.");
        }

        ValidateLodExpectations(operation.ExpectedMatchesByLod, "expectedMatchesByLod");
        ValidateSelector(operation.Selector);
    }

    private static void ValidateRemoveComponent(RemoveComponentOperation operation)
    {
        if (operation.Version != 1 || operation.Granularity != "draw_call")
        {
            throw Errors.Unsupported("OPERATION_UNSUPPORTED", "Only remove_component version 1 at draw_call granularity is supported.", "Use kind remove_component, version 1, and granularity draw_call.");
        }
    }

    private static void ValidateTransformComponent(TransformComponentOperation operation)
    {
        var expectedGranularity = operation.Version == 3 ? "connected_component_vertices" : "draw_call_owned_vertices";
        if (operation.Version is not (1 or 2 or 3) || operation.Granularity != expectedGranularity)
        {
            throw Errors.Unsupported("OPERATION_UNSUPPORTED", "The transform_component version and granularity combination is unsupported.", "Use versions 1 or 2 at draw_call_owned_vertices, or version 3 at connected_component_vertices.");
        }

        ValidateLodExpectations(operation.ExpectedMatchesByLod, "expectedMatchesByLod", requireCanonicalKeys: true);
        ValidateLodExpectations(operation.ExpectedVerticesByLod, "expectedVerticesByLod", requireCanonicalKeys: true);
        if (!operation.ExpectedMatchesByLod.Keys.Order(StringComparer.Ordinal)
            .SequenceEqual(operation.ExpectedVerticesByLod.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw Errors.InvalidRecipe("LOD_EXPECTATION_MISMATCH", "Draw-call and vertex expectations must name the same LODs.", "Declare expectedMatchesByLod and expectedVerticesByLod for the same complete LOD set.");
        }

        if (operation.OwnershipPolicy != "exclusive")
        {
            throw Errors.InvalidRecipe("OWNERSHIP_POLICY_UNSUPPORTED", "transform_component@1 requires exclusive vertex ownership.", "Set ownershipPolicy to exclusive.");
        }


        if (operation.Version == 3)
        {
            var components = operation.ConnectedComponentIdsByLod;
            if (components is null
                || !components.Keys.Order(StringComparer.Ordinal).SequenceEqual(operation.ExpectedVerticesByLod.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal)
                || components.Any(entry => entry.Value is null
                    || entry.Value.Count == 0
                    || entry.Value.Distinct(StringComparer.Ordinal).Count() != entry.Value.Count
                    || entry.Value.Any(id => id is null || !ConnectedComponentIdRegex().IsMatch(id))))
            {
                throw Errors.InvalidRecipe("CONNECTED_COMPONENT_SELECTION_INVALID", "transform_component@3 requires a non-empty unique connected-component ID list for every declared LOD.", "Copy stable cc_ identifiers from current inspect evidence for every LOD.");
            }
        }
        else if (operation.ConnectedComponentIdsByLod is not null)
        {
            throw Errors.InvalidRecipe("CONNECTED_COMPONENT_SELECTION_INVALID", "Connected-component IDs are only valid for transform_component@3.", "Remove connectedComponentIdsByLod or use version 3.");
        }


        if (operation.Version is 1 or 3 && (operation.PhysicsPolicy is not null || operation.Limits.MaximumCollisionDisplacement is not null))
        {
            throw Errors.InvalidRecipe("PHYSICS_POLICY_UNSUPPORTED", "transform_component@1 is PHYS-immutable and cannot declare collision-transform fields.", "Remove physicsPolicy and maximumCollisionDisplacement, or use transform_component@2.");
        }

        if (operation.Version == 2
            && (operation.PhysicsPolicy != "transform_coupled_convex"
                || operation.Limits.MaximumCollisionDisplacement is null))
        {
            throw Errors.InvalidRecipe("PHYSICS_POLICY_UNSUPPORTED", "transform_component@2 requires physicsPolicy transform_coupled_convex and a collision displacement limit.", "Set physicsPolicy and maximumCollisionDisplacement exactly as required by recipe schema version 3.");
        }

        if (operation.Transform is null || operation.Transform.Pivot is null || operation.Transform.Translation is null || operation.Limits is null)
        {
            throw Errors.InvalidRecipe("TRANSFORM_INVALID", "Transform, pivot, translation, and limits are required.", "Provide every transform_component@1 field from the published recipe schema.");
        }

        if (operation.Transform.Pivot.Kind != "selection_bounds_center" || operation.Transform.Pivot.ReferenceLod < 0)
        {
            throw Errors.InvalidRecipe("PIVOT_INVALID", "transform_component@1 requires selection_bounds_center and a non-negative referenceLod.", "Select a present reference LOD and freeze its selection bounds center during planning.");
        }

        var referenceLod = operation.Transform.Pivot.ReferenceLod.ToString(CultureInfo.InvariantCulture);
        if (!operation.ExpectedVerticesByLod.ContainsKey(referenceLod))
        {
            throw Errors.InvalidRecipe("PIVOT_LOD_UNDECLARED", $"Pivot reference LOD {referenceLod} has no vertex expectation.", "Choose a referenceLod declared in expectedVerticesByLod.");
        }

        var scale = operation.Transform.UniformScale;
        RequireFinite(scale, "uniformScale");
        if (scale < 0.25f || scale > 4f)
        {
            throw Errors.InvalidRecipe("TRANSFORM_SCALE_OUT_OF_RANGE", "uniformScale must be within [0.25, 4.0].", "Choose a bounded positive uniform scale.");
        }

        var translation = operation.Transform.Translation;
        RequireFinite(translation.X, "translation.x");
        RequireFinite(translation.Y, "translation.y");
        RequireFinite(translation.Z, "translation.z");
        if (scale == 1f && translation.X == 0f && translation.Y == 0f && translation.Z == 0f)
        {
            throw Errors.InvalidRecipe("TRANSFORM_IDENTITY", "transform_component@1 must change scale or translation.", "Use a non-identity bounded transform.");
        }


        if (operation.Version == 2 && (scale == 1f || translation.X != 0f || translation.Y != 0f || translation.Z != 0f))
        {
            throw Errors.InvalidRecipe("COUPLED_TRANSFORM_TYPE_UNSUPPORTED", "transform_component@2 admits only a non-identity positive uniform scale with zero translation.", "Choose a scale other than 1 and set every translation axis to zero.");
        }

        var maximumDisplacement = operation.Limits.MaximumVertexDisplacement;
        RequireFinite(maximumDisplacement, "maximumVertexDisplacement");
        if (maximumDisplacement <= 0f || maximumDisplacement > MaximumTransformDisplacement)
        {
            throw Errors.InvalidRecipe("TRANSFORM_LIMIT_OUT_OF_RANGE", $"maximumVertexDisplacement must be within (0, {MaximumTransformDisplacement}].", "Choose a positive cap no greater than the hard safety ceiling.");
        }


        if (operation.Limits.MaximumCollisionDisplacement is { } collisionDisplacement)
        {
            RequireFinite(collisionDisplacement, "maximumCollisionDisplacement");
            if (collisionDisplacement <= 0f || collisionDisplacement > MaximumTransformDisplacement)
            {
                throw Errors.InvalidRecipe("TRANSFORM_LIMIT_OUT_OF_RANGE", $"maximumCollisionDisplacement must be within (0, {MaximumTransformDisplacement}].", "Choose a positive collision cap no greater than the hard safety ceiling.");
            }
        }
    }

    private static void ValidateLodExpectations(
        IReadOnlyDictionary<string, int>? expectations,
        string field,
        bool requireCanonicalKeys = false)
    {
        if (expectations is null || expectations.Count == 0 || expectations.Any(entry =>
            !int.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var lod)
            || lod < 0
            || (requireCanonicalKeys && entry.Key != lod.ToString(CultureInfo.InvariantCulture))
            || entry.Value < 1))
        {
            throw Errors.InvalidRecipe("LOD_EXPECTATION_INVALID", $"{field} must contain a positive count for each numeric LOD.", $"Provide {field} such as {{ \"0\": 1, \"1\": 1 }}.");
        }
    }

    private static void ValidateSelector(ComponentSelector? selector)
    {
        if (selector is null)
        {
            throw Errors.InvalidRecipe("SELECTOR_INVALID", "A component selector is required.", "Use exactly one published selector representation.");
        }

        switch (selector.Kind)
        {
            case "material_exact" when !string.IsNullOrWhiteSpace(selector.MaterialPath) && selector.DrawCallIds is null:
                break;
            case "draw_call_ids" when selector.DrawCallIds is { Count: > 0 } ids &&
                                      ids.Distinct(StringComparer.Ordinal).Count() == ids.Count &&
                                      ids.All(id => id is not null && DrawCallIdRegex().IsMatch(id)) &&
                                      selector.MaterialPath is null:
                break;
            default:
                throw Errors.InvalidRecipe("SELECTOR_INVALID", "Selector must be material_exact or a non-empty unique list of stable draw_call_ids.", "Use exactly one selector representation and copy IDs from inspect output.");
        }
    }

    private static void RequireFinite(float value, string field)
    {
        if (!float.IsFinite(value))
        {
            throw Errors.InvalidRecipe("TRANSFORM_NON_FINITE", $"{field} must be finite.", "Use finite JSON numbers for every transform value.");
        }
    }

    private static void ValidateIdentifier(string identifier, string field)
    {
        if (identifier is null || !IdentifierRegex().IsMatch(identifier))
        {
            throw Errors.InvalidRecipe("IDENTIFIER_INVALID", $"{field} '{identifier}' is invalid.", "Use 1-64 lowercase letters, digits, dots, underscores, or hyphens, beginning with a letter or digit.");
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex("^cc_[0-9a-f]{24}$", RegexOptions.CultureInvariant)]
    private static partial Regex ConnectedComponentIdRegex();

    [GeneratedRegex("^dc_[0-9a-f]{24}$", RegexOptions.CultureInvariant)]
    private static partial Regex DrawCallIdRegex();
}
