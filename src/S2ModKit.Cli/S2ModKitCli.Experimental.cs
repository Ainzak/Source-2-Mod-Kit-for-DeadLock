using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private static ExperimentalScaffoldOptions? ParseExperimentalScaffoldOptions(bool enabled, string intent,
        string? pivotPoint, int? referenceLod, string? axis, string? pinned, string? full)
    {
        if (!enabled)
        {
            if (axis is not null || pinned is not null || full is not null || intent is RecipeScaffoldContract.RegionScaleIntent or RecipeScaffoldContract.EllipsoidScaleIntent or RecipeScaffoldContract.MirroredEllipsoidScaleIntent)
                throw Errors.InvalidRecipe("SCAFFOLD_EXPERIMENTAL_OPT_IN_REQUIRED", "Region options require --experimental.", "Explicitly acknowledge the experimental contract.");
            return null;
        }
        if (intent is not (RecipeScaffoldContract.UniformScaleIntent or RecipeScaffoldContract.RegionScaleIntent))
        {
            if (axis is not null || pinned is not null || full is not null)
                throw Errors.InvalidRecipe("SCAFFOLD_EXPERIMENTAL_OPTIONS_INVALID", "Region parameters require region-scale intent.", "Do not supply ignored experimental parameters to a strict action.");
            return null; // The flag selects schema-3 IDs; this action keeps its strict mutation contract.
        }
        RegionScaleSelection? region = null;
        if (intent == RecipeScaffoldContract.RegionScaleIntent)
        {
            if (axis is not ("x" or "y" or "z") || pinned is null || full is null || pivotPoint is null)
                throw Errors.InvalidRecipe("REGION_SELECTION_INVALID", "Region scaffolding requires axis, both thresholds and an explicit pivot point.", "Provide --region-axis, --pinned-through, --full-from and --pivot-point.");
            region = new("axis_ramp", 1, axis, ParseOptionalSingle(pinned, "--pinned-through")!.Value, ParseOptionalSingle(full, "--full-from")!.Value);
        }
        else if (axis is not null || pinned is not null || full is not null)
            throw Errors.InvalidRecipe("SCAFFOLD_EXPERIMENTAL_OPTIONS_INVALID", "Region options cannot be ignored by uniform-scale.", "Choose region-scale or remove region options.");
        var pivot = pivotPoint is null
            ? new TransformPivot { Kind = "selection_bounds_center", ReferenceLod = referenceLod }
            : new TransformPivot { Kind = "explicit_point", Point = ParseVector3(pivotPoint, "--pivot-point") };
        return new(new("preserve_unverified", 1), pivot, region);
    }
}
