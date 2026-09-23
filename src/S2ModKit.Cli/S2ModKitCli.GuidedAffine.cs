using System.Globalization;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private static async Task<GuidedActionParameters?> ReadGuidedAffineParametersAsync(
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        TransformVector3 scale;
        TransformRotation rotation;
        while (true)
        {
            var axes = new float[3];
            for (var index = 0; index < axes.Length; index++)
            {
                var name = "XYZ"[index];
                var result = await ReadGuidedNumberAsync(
                    input, output,
                    $"Scale {name} [0.25..4, blank=1]: ",
                    value => value is >= 0.25f and <= 4f,
                    "Enter a finite scale from 0.25 through 4, blank for 1, or 'cancel'.",
                    1f, cancellationToken).ConfigureAwait(false);
                if (result.Canceled)
                {
                    return null;
                }

                axes[index] = result.Value;
            }

            scale = new TransformVector3 { X = axes[0], Y = axes[1], Z = axes[2] };
            var degrees = await ReadGuidedNumberAsync(
                input, output,
                "Rotation in degrees [(-180, 180], blank=0]: ",
                value => value is > -180f and <= 180f,
                "Enter degrees greater than -180 and at most 180, blank for no rotation, or 'cancel'.",
                0f, cancellationToken).ConfigureAwait(false);
            if (degrees.Canceled)
            {
                return null;
            }

            if (degrees.Value == 0f)
            {
                rotation = new TransformRotation { Kind = "identity" };
            }
            else
            {
                TransformVector3 axis;
                while (true)
                {
                    var text = await ReadGuidedTextAsync(input, output, "Rotation axis [x, y, z, or unit x,y,z]: ", cancellationToken).ConfigureAwait(false);
                    if (text is null)
                    {
                        return null;
                    }

                    if (TryReadGuidedAxis(text, out axis))
                    {
                        break;
                    }

                    output.WriteLine("Enter x, y, z, or three numbers forming a unit axis, such as 0,0,1.");
                }

                rotation = new TransformRotation { Kind = "axis_angle", Axis = axis, Degrees = degrees.Value };
            }

            if (axes.Any(value => value != 1f) || degrees.Value != 0f)
            {
                break;
            }

            output.WriteLine("Change at least one scale axis or choose a non-zero rotation.");
        }

        output.WriteLine("Choose an anchor:");
        output.WriteLine("  1. Center of selected part");
        output.WriteLine("  2. Model-space point");
        output.WriteLine("  3. Face of selected bounds");
        output.WriteLine("  4. Bone origin");
        var pivotChoice = await ReadGuidedChoiceAsync(input, output, 4, cancellationToken).ConfigureAwait(false);
        if (pivotChoice is null)
        {
            return null;
        }

        TransformPivot pivot;
        switch (pivotChoice.Value)
        {
            case 0:
                pivot = new TransformPivot { Kind = "selection_bounds_center" };
                break;
            case 1:
                var point = new float[3];
                for (var index = 0; index < point.Length; index++)
                {
                    var result = await ReadGuidedNumberAsync(
                        input, output,
                        $"Anchor point {"XYZ"[index]} (Source units): ",
                        _ => true,
                        "Enter a finite number, or 'cancel'.",
                        null, cancellationToken).ConfigureAwait(false);
                    if (result.Canceled)
                    {
                        return null;
                    }

                    point[index] = result.Value;
                }

                pivot = new TransformPivot
                {
                    Kind = "explicit_point",
                    Point = new TransformVector3 { X = point[0], Y = point[1], Z = point[2] },
                };
                break;
            case 2:
                var faces = new[] { "min_x", "max_x", "min_y", "max_y", "min_z", "max_z" };
                output.WriteLine("Choose a bounds face:");
                for (var index = 0; index < faces.Length; index++)
                {
                    output.WriteLine($"  {index + 1}. {faces[index].Replace('_', ' ')}");
                }

                var faceChoice = await ReadGuidedChoiceAsync(input, output, faces.Length, cancellationToken).ConfigureAwait(false);
                if (faceChoice is null)
                {
                    return null;
                }

                pivot = new TransformPivot { Kind = "bounds_face", Face = faces[faceChoice.Value] };
                break;
            default:
                var bone = await ReadGuidedTextAsync(input, output, "Exact influencing bone name: ", cancellationToken).ConfigureAwait(false);
                if (bone is null)
                {
                    return null;
                }

                pivot = new TransformPivot { Kind = "bone_origin", BoneName = bone };
                break;
        }

        output.WriteLine("Choose axes for scaling and rotation:");
        output.WriteLine("  1. Model axes");
        output.WriteLine("  2. Bone bind axes");
        var frameChoice = await ReadGuidedChoiceAsync(input, output, 2, cancellationToken).ConfigureAwait(false);
        if (frameChoice is null)
        {
            return null;
        }

        TransformFrame frame;
        if (frameChoice.Value == 0)
        {
            frame = new TransformFrame { Kind = "model" };
        }
        else
        {
            var bone = await ReadGuidedTextAsync(input, output, "Exact influencing frame bone: ", cancellationToken).ConfigureAwait(false);
            if (bone is null)
            {
                return null;
            }

            frame = new TransformFrame { Kind = "bone_bind", BoneName = bone };
        }

        var maximum = await ReadGuidedNumberAsync(
            input, output,
            "Maximum vertex movement in Source units [(0, 256], blank=256]: ",
            value => value is > 0f and <= 256f,
            "Enter a finite value greater than 0 and at most 256, blank for 256, or 'cancel'.",
            256f, cancellationToken).ConfigureAwait(false);
        return maximum.Canceled
            ? null
            : new GuidedActionParameters(
                null, null, null, null, maximum.Value, null,
                new AffineScaffoldOptions(scale, rotation, pivot, frame));
    }

    private static bool TryReadGuidedAxis(string input, out TransformVector3 axis)
    {
        axis = input.Trim().ToLowerInvariant() switch
        {
            "x" => new TransformVector3 { X = 1 },
            "y" => new TransformVector3 { Y = 1 },
            "z" => new TransformVector3 { Z = 1 },
            _ => new TransformVector3(),
        };
        if (input.Trim().ToLowerInvariant() is "x" or "y" or "z")
        {
            return true;
        }

        var parts = input.Split(',');
        if (parts.Length != 3
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)
            || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            return false;
        }

        var length = Math.Sqrt(((double)x * x) + ((double)y * y) + ((double)z * z));
        if (Math.Abs(length - 1d) > 1e-4d)
        {
            return false;
        }

        axis = new TransformVector3 { X = x, Y = y, Z = z };
        return true;
    }
}
