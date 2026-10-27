using S2ModKit.Application;
using S2ModKit.Domain;

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
}
