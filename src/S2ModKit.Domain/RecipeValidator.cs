using System.Globalization;
using System.Text.RegularExpressions;

namespace S2ModKit.Domain;

public static partial class RecipeValidator
{
    public const float MaximumTransformDisplacement = 256f;

    public static void Validate(RecipeDocument recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        if (recipe.SchemaVersion is not (1 or 2 or 3 or 4 or 5))
        {
            throw Errors.InvalidRecipe("SCHEMA_VERSION_UNSUPPORTED", "Only recipe schemaVersion 1 through 5 are supported.", "Migrate the recipe to a published schema version.");
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

            if (recipe.SchemaVersion < 5 && operation is TransformComponentOperation { Version: 4 })
            {
                throw Errors.InvalidRecipe("SCHEMA_OPERATION_MISMATCH", "transform_component@4 requires recipe schemaVersion 5.", "Use schemaVersion 5 for an affine component transform.");
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
        var validGranularity = operation.Version switch
        {
            1 or 2 => operation.Granularity == "draw_call_owned_vertices",
            3 => operation.Granularity == "connected_component_vertices",
            4 => operation.Granularity is "draw_call_vertices" or "connected_component_vertices",
            _ => false,
        };
        if (!validGranularity)
        {
            throw Errors.Unsupported("OPERATION_UNSUPPORTED", "The transform_component version and granularity combination is unsupported.", "Use a published transform version and its exact vertex-selection granularity.");
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


        var usesConnectedComponents = operation.Version == 3
            || operation is { Version: 4, Granularity: "connected_component_vertices" };
        if (usesConnectedComponents)
        {
            var components = operation.ConnectedComponentIdsByLod;
            if (components is null
                || !components.Keys.Order(StringComparer.Ordinal).SequenceEqual(operation.ExpectedVerticesByLod.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal)
                || components.Any(entry => entry.Value is null
                    || entry.Value.Count == 0
                    || entry.Value.Distinct(StringComparer.Ordinal).Count() != entry.Value.Count
                    || entry.Value.Any(id => id is null || !ConnectedComponentIdRegex().IsMatch(id))))
            {
                throw Errors.InvalidRecipe("CONNECTED_COMPONENT_SELECTION_INVALID", "Connected-component selection requires a non-empty unique component ID list for every declared LOD.", "Copy stable cc_ identifiers from current inspect evidence for every LOD.");
            }
        }
        else if (operation.ConnectedComponentIdsByLod is not null)
        {
            throw Errors.InvalidRecipe("CONNECTED_COMPONENT_SELECTION_INVALID", "Connected-component IDs are only valid for transform_component@3.", "Remove connectedComponentIdsByLod or use version 3.");
        }


        if (operation.Transform is null || operation.Transform.Pivot is null || operation.Transform.Translation is null || operation.Limits is null)
        {
            throw Errors.InvalidRecipe("TRANSFORM_INVALID", "Transform, pivot, translation, and limits are required.", "Provide every field required by the selected transform version.");
        }

        if (operation.Version is 1 or 3 or 4 && (operation.PhysicsPolicy is not null || operation.Limits.MaximumCollisionDisplacement is not null))
        {
            throw Errors.InvalidRecipe("PHYSICS_POLICY_UNSUPPORTED", "transform_component@1 is PHYS-immutable and cannot declare collision-transform fields.", "Remove physicsPolicy and maximumCollisionDisplacement, or use transform_component@2.");
        }

        if (operation.Version == 2
            && (operation.PhysicsPolicy != "transform_coupled_convex"
                || operation.Limits.MaximumCollisionDisplacement is null))
        {
            throw Errors.InvalidRecipe("PHYSICS_POLICY_UNSUPPORTED", "transform_component@2 requires physicsPolicy transform_coupled_convex and a collision displacement limit.", "Set physicsPolicy and maximumCollisionDisplacement exactly as required by recipe schema version 3.");
        }

        if (operation.Version == 4)
        {
            ValidateAffineTransform(operation);
        }
        else
        {
            ValidateLegacyTransform(operation);
        }

        ValidateDisplacementLimits(operation);
    }

    private static void ValidateLegacyTransform(TransformComponentOperation operation)
    {
        var transform = operation.Transform;
        if (transform.Pivot.Kind != "selection_bounds_center"
            || transform.Pivot.ReferenceLod is not { } referenceLodValue
            || referenceLodValue < 0
            || transform.Pivot.Point is not null
            || transform.Pivot.Face is not null
            || transform.Pivot.BoneName is not null)
        {
            throw Errors.InvalidRecipe("PIVOT_INVALID", "transform_component versions 1 through 3 require selection_bounds_center and a non-negative referenceLod.", "Select a present reference LOD and freeze its selection bounds center during planning.");
        }

        if (transform.Scale is not null || transform.Rotation is not null || transform.Frame is not null)
        {
            throw Errors.InvalidRecipe("TRANSFORM_INVALID", "transform_component versions 1 through 3 require only uniformScale and translation.", "Remove affine-only fields or use transform_component@4.");
        }

        ValidateReferenceLod(operation, referenceLodValue);

        var scale = transform.UniformScale;
        RequireFinite(scale, "uniformScale");
        if (scale < 0.25f || scale > 4f)
        {
            throw Errors.InvalidRecipe("TRANSFORM_SCALE_OUT_OF_RANGE", "uniformScale must be within [0.25, 4.0].", "Choose a bounded positive uniform scale.");
        }

        var translation = transform.Translation;
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

    }

    private static void ValidateAffineTransform(TransformComponentOperation operation)
    {
        var transform = operation.Transform;
        if (transform.UniformScale != 0f || transform.Scale is not { } scale
            || transform.Rotation is not { } rotation || transform.Frame is not { } frame)
        {
            throw Errors.InvalidRecipe("TRANSFORM_INVALID", "transform_component@4 requires scale, rotation, frame, pivot, and translation and does not accept uniformScale.", "Use the affine fields from recipe schema version 5.");
        }

        ValidateScaleAxis(scale.X, "scale.x");
        ValidateScaleAxis(scale.Y, "scale.y");
        ValidateScaleAxis(scale.Z, "scale.z");
        ValidatePivot(operation, transform.Pivot);
        ValidateFrame(frame);
        ValidateRotation(rotation);
        RequireFinite(transform.Translation.X, "translation.x");
        RequireFinite(transform.Translation.Y, "translation.y");
        RequireFinite(transform.Translation.Z, "translation.z");

        if (scale.X == 1f && scale.Y == 1f && scale.Z == 1f
            && rotation.Kind == "identity"
            && transform.Translation.X == 0f && transform.Translation.Y == 0f && transform.Translation.Z == 0f)
        {
            throw Errors.InvalidRecipe("TRANSFORM_IDENTITY", "transform_component@4 must change scale, rotation, or translation.", "Use a non-identity bounded affine transform.");
        }
    }

    private static void ValidatePivot(TransformComponentOperation operation, TransformPivot pivot)
    {
        switch (pivot.Kind)
        {
            case "selection_bounds_center" when pivot.ReferenceLod is { } lod
                && pivot.Point is null && pivot.Face is null && pivot.BoneName is null:
                ValidateReferenceLod(operation, lod);
                break;
            case "explicit_point" when pivot.ReferenceLod is null && pivot.Point is { } point
                && pivot.Face is null && pivot.BoneName is null:
                RequireFinite(point.X, "pivot.point.x");
                RequireFinite(point.Y, "pivot.point.y");
                RequireFinite(point.Z, "pivot.point.z");
                break;
            case "bounds_face" when pivot.ReferenceLod is { } lod
                && pivot.Point is null && pivot.Face is "min_x" or "max_x" or "min_y" or "max_y" or "min_z" or "max_z"
                && pivot.BoneName is null:
                ValidateReferenceLod(operation, lod);
                break;
            case "bone_origin" when pivot.ReferenceLod is { } lod
                && pivot.Point is null && pivot.Face is null && !string.IsNullOrWhiteSpace(pivot.BoneName):
                ValidateReferenceLod(operation, lod);
                break;
            default:
                throw Errors.InvalidRecipe("PIVOT_INVALID", "The affine pivot kind or its fields are invalid.", "Use exactly one published typed-pivot representation.");
        }
    }

    private static void ValidateFrame(TransformFrame frame)
    {
        if (frame.Kind == "model" && frame.BoneName is null)
        {
            return;
        }

        if (frame.Kind == "bone_bind" && !string.IsNullOrWhiteSpace(frame.BoneName))
        {
            return;
        }

        throw Errors.InvalidRecipe("AFFINE_FRAME_UNSUPPORTED", "The affine frame kind or its fields are invalid.", "Use model or one exact named bone_bind frame.");
    }

    private static void ValidateRotation(TransformRotation rotation)
    {
        if (rotation.Kind == "identity" && rotation.Axis is null && rotation.Degrees is null)
        {
            return;
        }

        if (rotation.Kind != "axis_angle" || rotation.Axis is not { } axis || rotation.Degrees is not { } degrees)
        {
            throw Errors.InvalidRecipe("AFFINE_ROTATION_UNSUPPORTED", "Rotation must be identity or one complete axis_angle.", "Use a published rotation representation.");
        }

        RequireFinite(axis.X, "rotation.axis.x");
        RequireFinite(axis.Y, "rotation.axis.y");
        RequireFinite(axis.Z, "rotation.axis.z");
        RequireFinite(degrees, "rotation.degrees");
        var length = Math.Sqrt(((double)axis.X * axis.X) + ((double)axis.Y * axis.Y) + ((double)axis.Z * axis.Z));
        if (Math.Abs(length - 1d) > 1e-4d || degrees <= -180f || degrees > 180f || degrees == 0f)
        {
            throw Errors.InvalidRecipe("AFFINE_ROTATION_UNSUPPORTED", "Axis-angle rotation requires a unit axis and non-zero degrees in (-180, 180].", "Normalize the axis and use identity for a zero rotation.");
        }
    }

    private static void ValidateScaleAxis(float value, string field)
    {
        RequireFinite(value, field);
        if (value < 0.25f || value > 4f)
        {
            throw Errors.InvalidRecipe("TRANSFORM_SCALE_OUT_OF_RANGE", $"{field} must be within [0.25, 4.0].", "Choose a bounded positive per-axis scale.");
        }
    }

    private static void ValidateReferenceLod(TransformComponentOperation operation, int referenceLodValue)
    {
        if (referenceLodValue < 0)
        {
            throw Errors.InvalidRecipe("PIVOT_INVALID", "Pivot referenceLod must be non-negative.", "Choose a present reference LOD.");
        }

        var referenceLod = referenceLodValue.ToString(CultureInfo.InvariantCulture);
        if (!operation.ExpectedVerticesByLod.ContainsKey(referenceLod))
        {
            throw Errors.InvalidRecipe("PIVOT_LOD_UNDECLARED", $"Pivot reference LOD {referenceLod} has no vertex expectation.", "Choose a referenceLod declared in expectedVerticesByLod.");
        }
    }

    private static void ValidateDisplacementLimits(TransformComponentOperation operation)
    {
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
