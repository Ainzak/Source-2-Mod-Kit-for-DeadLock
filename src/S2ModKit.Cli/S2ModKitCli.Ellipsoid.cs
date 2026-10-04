using System.Globalization;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;
using S2ModKit.Reporting;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private static EllipsoidScaffoldOptions? ParseEllipsoidScaffoldOptions(bool experimental, string intent, string? center, string? radii, string? core, string? scale, string? limit, string? mirrorAxis, string? mirrorCoordinate)
    {
        if (intent is not (RecipeScaffoldContract.EllipsoidScaleIntent or RecipeScaffoldContract.MirroredEllipsoidScaleIntent))
        {
            if (center is not null || radii is not null || core is not null || mirrorAxis is not null || mirrorCoordinate is not null)
                throw Errors.InvalidRecipe("SCAFFOLD_ELLIPSOID_OPTIONS_INVALID", "Field parameters require ellipsoid-scale intent.", "Remove ignored field options or select the local-field action.");
            return null;
        }
        if (!experimental || center is null || radii is null || core is null || scale is null || limit is null)
            throw Errors.InvalidRecipe("SCAFFOLD_ELLIPSOID_OPTIONS_INVALID", "Local-field scaffolding requires --experimental, --field-center, --field-radii, --core-fraction, --scale and --max-displacement.", "Declare every field parameter explicitly; no anatomical defaults are inferred.");
        var mirrored = intent == RecipeScaffoldContract.MirroredEllipsoidScaleIntent;
        if (mirrored ? mirrorAxis is not ("x" or "y" or "z") || mirrorCoordinate is null : mirrorAxis is not null || mirrorCoordinate is not null)
            throw Errors.InvalidRecipe("SCAFFOLD_ELLIPSOID_OPTIONS_INVALID", "Mirrored intent requires both explicit plane options; single fields forbid them.", "Match the action and plane parameters.");
        var transform = new EllipsoidVisualTransform(new(mirrored ? "mirrored_ellipsoids" : "ellipsoid", 1, "model", ParseVector3(center, "--field-center"), ParseVector3(radii, "--field-radii"),
            ParseOptionalSingle(core, "--core-fraction")!.Value)
        { MirrorPlane = mirrored ? new(mirrorAxis!, ParseOptionalSingle(mirrorCoordinate, "--mirror-coordinate")!.Value) : null }, ParseOptionalSingle(scale, "--scale")!.Value, new("ellipsoid_numeric", 1));
        EllipsoidContractValidator.ValidateLocalTransform(transform, ParseOptionalSingle(limit, "--max-displacement")!.Value);
        return new(new("preserve_unverified", 1), transform);
    }

    private static async Task<GuidedActionParameters?> ReadGuidedEllipsoidParametersAsync(bool mirrored, TextReader input, TextWriter output, CancellationToken token)
    {
        output.WriteLine(ExperimentalWarning);
        output.WriteLine("Local field in model coordinates; center anchors both selection and scaling. Core fraction sets the full-strength core; the remainder falls smoothly to pinned points. No automatic anatomy selection. Enter 'back' to choose another action.");
        while (true)
        {
            float[] values = new float[9];
            string[] prompts = ["Uniform scale [0.5..2, not 1]", "Maximum displacement (0..64]", "Field center X", "Field center Y", "Field center Z", "Outer radius X", "Outer radius Y", "Outer radius Z", "Core fraction (0..1)"];
            for (var i = 0; i < prompts.Length; i++)
            {
                while (true)
                {
                    output.Write(prompts[i] + ": ");
                    var text = (await input.ReadLineAsync(token).ConfigureAwait(false))?.Trim();
                    if (text is "back" or "previous") return new(null, null, null, null, null, null, Back: true);
                    if (text is null or "cancel" or "quit" or "q") return null;
                    if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && float.IsFinite(number)
                        && (i switch { 0 => number is >= 0.5f and <= 2f && number != 1, 1 => number is > 0 and <= 64, >= 5 and <= 7 => number > 0, 8 => number is > 0 and < 1, _ => true }))
                    { values[i] = number; break; }
                    output.WriteLine("Enter a finite value in the stated range, 'back', or 'cancel'.");
                }
            }
            EllipsoidMirrorPlane? plane = null;
            if (mirrored)
            {
                output.WriteLine("Explicit model-axis plane: 1. X  2. Y  3. Z (no anatomical inference)");
                var axis = await ReadGuidedChoiceAsync(input, output, 3, token).ConfigureAwait(false);
                if (axis is null) return null;
                var coordinate = await ReadGuidedNumberAsync(input, output, "Plane coordinate: ", _ => true, "Enter a finite coordinate or cancel.", null, token).ConfigureAwait(false);
                if (coordinate.Canceled) return null;
                plane = new("xyz"[axis.Value].ToString(), coordinate.Value);
            }
            var local = new EllipsoidVisualTransform(new(mirrored ? "mirrored_ellipsoids" : "ellipsoid", 1, "model", new() { X = values[2], Y = values[3], Z = values[4] },
                new() { X = values[5], Y = values[6], Z = values[7] }, values[8])
            { MirrorPlane = plane }, values[0], new("ellipsoid_numeric", 1));
            try { EllipsoidContractValidator.ValidateLocalTransform(local, values[1]); }
            catch (S2ModKitException ex) when (ex.Error.Category is ErrorCategory.CliOrSchema or ErrorCategory.UnsupportedCapability)
            { output.WriteLine($"Field rejected [{ex.Error.Code}]: {ex.Error.Summary} Re-enter parameters, 'back', or 'cancel'."); continue; }
            return new(values[0], null, null, null, values[1], null, Ellipsoid: new(new("preserve_unverified", 1), local));
        }
    }

    private async Task<GuidedEllipsoidPreview> CreateGuidedEllipsoidPreviewAsync(GuidedWorkflowSession session, RecipeDocument recipe, string sessionPath, ContentHash? expectedPlan, TextWriter output, CancellationToken token)
    {
        var preview = await application.PreviewEllipsoidSelectionAsync(session.ProjectRoot!, recipe, token).ConfigureAwait(false);
        if (preview.PlanFingerprint != expectedPlan)
            throw Errors.Verification("GUIDED_PREVIEW_PLAN_STALE", "The current selection preview disagrees with the reviewed plan.", "Restore the exact immutable input/recipe or start a new session.");
        var artifacts = EllipsoidSelectionPreviewRenderer.Render(preview);
        var root = Path.Combine(Path.GetDirectoryName(sessionPath)!, Path.GetFileNameWithoutExtension(sessionPath) + ".previews");
        var published = await FileSystemEllipsoidPreviewPublisher.PublishAsync(root, artifacts, token).ConfigureAwait(false);
        var identity = new GuidedEllipsoidPreview(preview.PreviewFingerprint, preview.PlanFingerprint, artifacts.SummaryHash, artifacts.ContactSheetHash, published.SummaryPath, published.ContactSheetPath);
        if (session.EllipsoidPreview is not null && session.EllipsoidPreview != identity)
            throw Errors.Verification("GUIDED_PREVIEW_STALE", "Saved preview identity/paths differ from the current read-only artifacts.", "Restore the intact session and preview; do not build from stale pictures.");
        output.WriteLine($"Selection sheet: {published.ContactSheetPath}");
        output.WriteLine($"Selection summary: {published.SummaryPath}");
        output.WriteLine("Bind-space prediction only; gray geometry is non-editable context. Inspect the sheet before choosing build/package output.");
        return identity;
    }
}
