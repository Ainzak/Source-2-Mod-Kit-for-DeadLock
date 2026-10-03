using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private const string ExperimentalWarning = "Experimental visual edit: culling spheres, proxies and collision are preserved without proving coherence. Anatomy, hair, clothing fit and runtime stability are not guaranteed.";

    private Task<ComponentDiscoveryResultV2> DiscoverGuidedComponentsAsync(GuidedWorkflowSession session, CancellationToken token) =>
        session.ExperimentalPolicy is not null ? application.DiscoverExperimentalComponentsAsync(session.ProjectRoot!, token)
            : application.DiscoverComponentsAsync(session.ProjectRoot!, token);

    private static async Task<GuidedActionParameters?> ReadGuidedExperimentalParametersAsync(int version,
        TextReader input, TextWriter output, CancellationToken token)
    {
        output.WriteLine(ExperimentalWarning);
        var scale = await ReadGuidedNumberAsync(input, output, "Uniform scale [0.5..2, not 1]: ", v => v is >= 0.5f and <= 2f && v != 1,
            "Enter 0.5 through 2, excluding 1, or 'cancel'.", null, token).ConfigureAwait(false);
        if (scale.Canceled) return null;
        var limit = await ReadGuidedNumberAsync(input, output, "Maximum displacement (0..64]: ", v => v is > 0 and <= 64,
            "Enter a positive limit at most 64, or 'cancel'.", null, token).ConfigureAwait(false);
        if (limit.Canceled) return null;
        var coordinates = new float[3];
        for (var axis = 0; axis < 3; axis++)
        {
            var coordinate = await ReadGuidedNumberAsync(input, output, $"Explicit model-space pivot {"XYZ"[axis]}: ", _ => true,
                "Enter a finite coordinate, or 'cancel'.", null, token).ConfigureAwait(false);
            if (coordinate.Canceled) return null;
            coordinates[axis] = coordinate.Value;
        }
        RegionScaleSelection? region = null;
        if (version == 6)
        {
            output.WriteLine("Choose model-space ramp axis: 1. X  2. Y  3. Z");
            var axis = await ReadGuidedChoiceAsync(input, output, 3, token).ConfigureAwait(false);
            if (axis is null) return null;
            var pinned = await ReadGuidedNumberAsync(input, output, "Pinned through (coordinates at/below stay unchanged): ", _ => true,
                "Enter a finite threshold, or 'cancel'.", null, token).ConfigureAwait(false);
            if (pinned.Canceled) return null;
            var full = await ReadGuidedNumberAsync(input, output, "Full scale from (greater than pinned threshold): ", v => v > pinned.Value,
                "Enter a larger finite threshold, or 'cancel'.", null, token).ConfigureAwait(false);
            if (full.Canceled) return null;
            region = new("axis_ramp", 1, "xyz"[axis.Value].ToString(), pinned.Value, full.Value);
        }
        return new(scale.Value, null, null, null, limit.Value, null, Experimental: new(new("preserve_unverified", 1),
            new() { Kind = "explicit_point", Point = new() { X = coordinates[0], Y = coordinates[1], Z = coordinates[2] } }, region));
    }

    private static ExperimentalScaffoldOptions? ParseExperimentalScaffoldOptions(bool enabled, string intent,
        string? pivotPoint, int? referenceLod, string? axis, string? pinned, string? full)
    {
        if (!enabled)
        {
            if (axis is not null || pinned is not null || full is not null || intent == RecipeScaffoldContract.RegionScaleIntent)
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
