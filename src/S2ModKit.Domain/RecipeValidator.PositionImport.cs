namespace S2ModKit.Domain;

public static partial class RecipeValidator
{
    private static void ValidatePositionImport(RecipeDocument recipe)
    {
        if (recipe.Operations is not [ImportPositionsOperation { Version: 1 } op] || recipe.Extensions.Count != 0)
            throw Errors.InvalidRecipe("IMPORT_RECIPE_INVALID", "Require exactly one import_positions@1 operation in recipe 12.", "Composition has a separate contract.");
        ValidateIdentifier(recipe.RecipeId, "recipeId"); ValidateIdentifier(op.OperationId, "operationId"); ValidateCommonOperation(op);
        if (op.Granularity != "returned_buffer_positions" || op.OwnershipPolicy != "exclusive" || op.Extensions.Count != 0
            || op.RuntimeMetadataPolicy != new RuntimeMetadataPolicy("preserve_unverified", 1)
            || op.SourceTrianglePolicy != new SourceTrianglePolicy("preserve_source_coincidence", 1)
            || op.ProceduralInputPolicy != new ProceduralInputPolicy("preserve_serialized_inputs_unverified", 1)
            || op.ZeroBoneBoxPolicy != new ZeroBoneBoxPolicy("reject", 1) || op.ZeroRenderSpherePolicy != new ZeroRenderSpherePolicy("reject", 1)
            || !float.IsFinite(op.Limits.MaximumVertexDisplacement) || op.Limits.MaximumVertexDisplacement is <= 0 or > 64
            || op.Limits.MaximumCollisionDisplacement is not null || op.PositionImport.FramePolicy != new PositionImportFramePolicy("incident_source_differential", 1))
            throw Errors.InvalidRecipe("IMPORT_RECIPE_INVALID", "Require explicit import scope, limits and characterized policies.", "Regenerate complete source-bound intent.");
    }
}
