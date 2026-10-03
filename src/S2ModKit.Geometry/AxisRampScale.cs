namespace S2ModKit.Geometry;

/// <summary>
/// A model-space half-space exclusion followed by one cubic smoothstep transition.
/// This is geometry math, not anatomical recognition or permission to mutate a resource.
/// </summary>
public sealed class AxisRampScale
{
    public const double MinimumAxisDerivative = 0.125;
    public int Axis { get; }
    public float PinnedThrough { get; }
    public float FullFrom { get; }
    public float Scale { get; }
    public Point3 Pivot { get; }
    private readonly double width;
    private readonly double delta;

    public AxisRampScale(int axis, float pinnedThrough, float fullFrom, float scale, Point3 pivot)
    {
        if (axis is < 0 or > 2 || !float.IsFinite(pinnedThrough) || !float.IsFinite(fullFrom)
            || pinnedThrough >= fullFrom || !float.IsFinite(scale) || scale is < 0.5f or > 2f || scale == 1f)
            throw new ArgumentException("Require an axis 0–2, ordered finite thresholds and nonidentity scale within [0.5, 2].");
        Axis = axis;
        PinnedThrough = pinnedThrough;
        FullFrom = fullFrom;
        Scale = scale;
        Pivot = pivot;
        width = (double)fullFrom - pinnedThrough;
        delta = (double)scale - 1;
        // w' <= 1.5 / width, a >= min(1,s). Bound the only possibly negative
        // rank-one contribution over the ENTIRE transition, not just sampled vertices.
        var adverseDistance = delta > 0
            ? Math.Max(0, Coordinate(pivot) - pinnedThrough)
            : Math.Max(0, fullFrom - Coordinate(pivot));
        // Directed binary64 rounding makes this a conservative admission bound even
        // when distant float origins make a subtraction inexact. It is not an epsilon.
        var derivativeBound = Math.BitIncrement((Math.Abs(delta) * 1.5) / Math.BitDecrement(width));
        var adverseBound = Math.BitIncrement(derivativeBound * Math.BitIncrement(adverseDistance));
        var lowerDerivative = Math.BitDecrement(Math.Min(1, scale) - adverseBound);
        if (!double.IsFinite(lowerDerivative) || lowerDerivative < MinimumAxisDerivative)
            throw new ArgumentException("The transition cannot prove a positive, bounded axis derivative throughout its interval.");
    }

    /// <summary>
    /// Binary64 arithmetic order is frozen: t=(q-lo)/width; w=(t*t)*(3-2*t);
    /// derivative=((6*t)*(1-t))/width; a=1+delta*w. No fused multiply-add.
    /// Pinned points return original words; full-strength points use UniformTransform's
    /// established float arithmetic. Transition coordinates narrow once, round-to-nearest.
    /// </summary>
    public RegionPointResult Evaluate(Point3 point)
    {
        var (weight, derivative) = Mask(point);
        if (weight == 0) return new(point, 0, 0);
        Point3 output;
        if (weight == 1) output = new UniformTransform(Pivot, Scale, default).Apply(point);
        else
        {
            var a = 1 + (delta * weight);
            output = new Point3(
                Narrow(Pivot.X + (a * ((double)point.X - Pivot.X))),
                Narrow(Pivot.Y + (a * ((double)point.Y - Pivot.Y))),
                Narrow(Pivot.Z + (a * ((double)point.Z - Pivot.Z))));
        }
        var dx = (double)output.X - point.X;
        var dy = (double)output.Y - point.Y;
        var dz = (double)output.Z - point.Z;
        var displacement = Math.Sqrt(((dx * dx) + (dy * dy)) + (dz * dz));
        if (!double.IsFinite(displacement)) throw new ArgumentException("Region displacement overflowed.");
        _ = AxisDerivative(point, weight, derivative);
        return new(output, weight, displacement);
    }

    /// <summary>
    /// Differential policy: J=aI+u e_axis^T, u=delta*w'*(p-pivot).
    /// Normals use J^-T; tangents use J then Gram-Schmidt. Split vertex records stay
    /// split, so UV/hard-edge discontinuities are never welded or averaged together.
    /// Positive axis derivative preserves tangent handedness. Pinned/full regions
    /// preserve the original frame; only transition frames need re-encoding.
    /// </summary>
    public TangentFrame TransformFrame(Point3 point, TangentFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateFrame(frame);
        var (weight, derivative) = Mask(point);
        if (derivative == 0) return frame;
        var a = 1 + (delta * weight);
        var b = AxisDerivative(point, weight, derivative);
        var factor = delta * derivative;
        double[] u = [factor * ((double)point.X - Pivot.X), factor * ((double)point.Y - Pivot.Y), factor * ((double)point.Z - Pivot.Z)];
        double[] n = [frame.Normal.X, frame.Normal.Y, frame.Normal.Z];
        double[] t = [frame.Tangent.X, frame.Tangent.Y, frame.Tangent.Z];
        var un = ((u[0] * n[0]) + (u[1] * n[1])) + (u[2] * n[2]);
        n[Axis] -= un / b; // Common positive 1/a cancels when normalizing.
        Normalize(n);
        var axisTangent = t[Axis];
        for (var i = 0; i < 3; i++) t[i] = (a * t[i]) + (u[i] * axisTangent);
        var projection = ((n[0] * t[0]) + (n[1] * t[1])) + (n[2] * t[2]);
        for (var i = 0; i < 3; i++) t[i] -= n[i] * projection;
        Normalize(t);
        var result = new TangentFrame(new(Narrow(n[0]), Narrow(n[1]), Narrow(n[2])), new(Narrow(t[0]), Narrow(t[1]), Narrow(t[2])), frame.Handedness);
        ValidateFrame(result);
        return result;
    }

    private (double Weight, double Derivative) Mask(Point3 point)
    {
        var q = Coordinate(point);
        if (q <= PinnedThrough) return (0, 0);
        if (q >= FullFrom) return (1, 0);
        var t = (q - PinnedThrough) / width;
        return ((t * t) * (3 - (2 * t)), ((6 * t) * (1 - t)) / width);
    }

    private double AxisDerivative(Point3 p, double weight, double derivative)
    {
        var result = (1 + (delta * weight)) + ((delta * derivative) * (Coordinate(p) - Coordinate(Pivot)));
        if (!double.IsFinite(result) || result < MinimumAxisDerivative)
            throw new ArgumentException("Region Jacobian is unrepresentable or insufficiently positive.");
        return result;
    }

    private double Coordinate(Point3 p) => Axis switch { 0 => p.X, 1 => p.Y, _ => p.Z };
    private static float Narrow(double value)
    {
        var result = (float)value;
        if (!float.IsFinite(result)) throw new ArgumentException("Region output cannot be represented by a finite float.");
        return result;
    }

    private static void Normalize(double[] vector)
    {
        var length = Math.Sqrt(((vector[0] * vector[0]) + (vector[1] * vector[1])) + (vector[2] * vector[2]));
        if (!double.IsFinite(length) || length <= 0) throw new ArgumentException("Region frame is degenerate or unrepresentable.");
        for (var i = 0; i < 3; i++) vector[i] /= length;
    }

    private static void ValidateFrame(TangentFrame frame)
    {
        static double Dot(Point3 a, Point3 b) => (((double)a.X * b.X) + ((double)a.Y * b.Y)) + ((double)a.Z * b.Z);
        // This declared unit/orthogonality threshold matches the existing packed-frame
        // boundary; it is not a geometric containment epsilon or asset-fitted tolerance.
        if (frame.Handedness is not (-1f or 1f) || Math.Abs(Dot(frame.Normal, frame.Normal) - 1) > 1e-4
            || Math.Abs(Dot(frame.Tangent, frame.Tangent) - 1) > 1e-4 || Math.Abs(Dot(frame.Normal, frame.Tangent)) > 1e-4)
            throw new ArgumentException("Region shading requires a unit orthogonal tangent frame and sign +/-1.");
    }
}

public readonly record struct RegionPointResult(Point3 Position, double Weight, double Displacement);
