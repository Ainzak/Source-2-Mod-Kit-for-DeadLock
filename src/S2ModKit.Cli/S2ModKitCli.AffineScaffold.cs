using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private static AffineScaffoldOptions? ParseAffineScaffoldOptions(string intent, AffineScaffoldCliOptions raw)
    {
        var supplied = new[]
        {
            raw.ScaleX, raw.ScaleY, raw.ScaleZ, raw.RotateAxis, raw.RotateDegrees,
            raw.Pivot, raw.PivotPoint, raw.PivotFace, raw.PivotBone, raw.Frame, raw.FrameBone,
        }.Any(value => value is not null);
        if (intent != RecipeScaffoldContract.AffineIntent)
        {
            if (supplied)
            {
                throw Errors.InvalidRecipe("SCAFFOLD_OPTIONS_INVALID", "Affine options require --intent affine.", "Remove the affine options or choose the affine intent.");
            }

            return null;
        }

        var scale = new TransformVector3
        {
            X = ParseOptionalSingle(raw.ScaleX, "--scale-x") ?? 1f,
            Y = ParseOptionalSingle(raw.ScaleY, "--scale-y") ?? 1f,
            Z = ParseOptionalSingle(raw.ScaleZ, "--scale-z") ?? 1f,
        };
        var rotationDegrees = ParseOptionalSingle(raw.RotateDegrees, "--rotate-degrees");
        if ((raw.RotateAxis is null) != (rotationDegrees is null))
        {
            throw Errors.InvalidRecipe("AFFINE_ROTATION_UNSUPPORTED", "Rotation requires both --rotate-axis and --rotate-degrees.", "Provide both options or omit both for identity rotation.");
        }

        var rotation = rotationDegrees is { } degrees
            ? new TransformRotation
            {
                Kind = "axis_angle",
                Axis = ParseVector3(raw.RotateAxis!, "--rotate-axis"),
                Degrees = degrees,
            }
            : new TransformRotation { Kind = "identity" };
        var pivotKind = raw.Pivot ?? "selection-center";
        var pivot = pivotKind switch
        {
            "selection-center" when raw.PivotPoint is null && raw.PivotFace is null && raw.PivotBone is null =>
                new TransformPivot { Kind = "selection_bounds_center" },
            "point" when raw.PivotPoint is not null && raw.PivotFace is null && raw.PivotBone is null =>
                new TransformPivot { Kind = "explicit_point", Point = ParseVector3(raw.PivotPoint, "--pivot-point") },
            "face" when raw.PivotFace is not null && raw.PivotPoint is null && raw.PivotBone is null =>
                new TransformPivot { Kind = "bounds_face", Face = raw.PivotFace },
            "bone" when !string.IsNullOrWhiteSpace(raw.PivotBone) && raw.PivotPoint is null && raw.PivotFace is null =>
                new TransformPivot { Kind = "bone_origin", BoneName = raw.PivotBone },
            _ => throw Errors.InvalidRecipe("PIVOT_INVALID", "The affine anchor kind and its value do not match.", "Use selection-center, point with --pivot-point, face with --pivot-face, or bone with --pivot-bone."),
        };
        var frameKind = raw.Frame ?? "model";
        var frame = frameKind switch
        {
            "model" when raw.FrameBone is null => new TransformFrame { Kind = "model" },
            "bone-bind" when !string.IsNullOrWhiteSpace(raw.FrameBone) =>
                new TransformFrame { Kind = "bone_bind", BoneName = raw.FrameBone },
            _ => throw Errors.InvalidRecipe("AFFINE_FRAME_UNSUPPORTED", "The affine frame kind and bone value do not match.", "Use model or bone-bind with --frame-bone."),
        };
        return new AffineScaffoldOptions(scale, rotation, pivot, frame);
    }

    private static TransformVector3 ParseVector3(string value, string optionName)
    {
        var parts = value.Split(',');
        if (parts.Length != 3)
        {
            throw Errors.InvalidRecipe("CLI_VECTOR_INVALID", $"Option '{optionName}' requires x,y,z.", "Provide three finite invariant-culture numbers separated by commas.");
        }

        return new TransformVector3
        {
            X = ParseOptionalSingle(parts[0].Trim(), optionName)!.Value,
            Y = ParseOptionalSingle(parts[1].Trim(), optionName)!.Value,
            Z = ParseOptionalSingle(parts[2].Trim(), optionName)!.Value,
        };
    }

    private sealed record AffineScaffoldCliOptions(
        string? ScaleX,
        string? ScaleY,
        string? ScaleZ,
        string? RotateAxis,
        string? RotateDegrees,
        string? Pivot,
        string? PivotPoint,
        string? PivotFace,
        string? PivotBone,
        string? Frame,
        string? FrameBone);
}
