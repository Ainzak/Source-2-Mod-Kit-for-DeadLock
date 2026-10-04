using System.Globalization;

namespace S2ModKit.Geometry;

public enum TiltedRampMembership { Pinned, Transition, Full }

public readonly record struct TiltedRampPointResult(Point3 Position, TiltedRampMembership Membership,
    double Weight, float MaximumDisplacement);

/// <summary>A bound on the determinant's rank-one factor, not on singular values or garment clearance.</summary>
public sealed record TiltedRampCertificate(string Numerator, string Denominator);

/// <summary>
/// tilted_ramp@1: an exact signed two-axis coordinate with a smooth neck-style transition.
/// This pure field neither infers anatomy nor authorizes resource mutation.
/// </summary>
public sealed class TiltedRampScale
{
    public int FirstAxis { get; }
    public int FirstSign { get; }
    public int SecondAxis { get; }
    public int SecondSign { get; }
    public float PinnedThrough { get; }
    public float FullFrom { get; }
    public float Scale { get; }
    public Point3 Pivot { get; }
    public float DisplacementLimit { get; }
    public TiltedRampCertificate Certificate { get; }
    private readonly EllipsoidRational pin;
    private readonly EllipsoidRational full;
    private readonly EllipsoidRational exactWidth;
    private readonly EllipsoidRational pivotCoordinate;
    private readonly double width;
    private readonly double delta;

    public TiltedRampScale(int firstAxis, int firstSign, int secondAxis, int secondSign,
        float pinnedThrough, float fullFrom, float scale, Point3 pivot, float displacementLimit)
    {
        if (firstAxis is < 0 or > 2 || secondAxis is < 0 or > 2 || firstAxis >= secondAxis
            || firstSign is not (-1 or 1) || secondSign is not (-1 or 1)
            || !float.IsFinite(pinnedThrough) || !float.IsFinite(fullFrom) || pinnedThrough >= fullFrom
            || !float.IsFinite(scale) || scale is < 0.5f or > 2f || scale == 1f
            || !float.IsFinite(displacementLimit) || displacementLimit is <= 0 or > 64)
            throw new ArgumentException("Require sorted distinct axes, explicit signs, ordered finite thresholds, nonidentity scale in [0.5,2] and displacement cap in (0,64].");
        FirstAxis = firstAxis; FirstSign = firstSign; SecondAxis = secondAxis; SecondSign = secondSign;
        PinnedThrough = pinnedThrough; FullFrom = fullFrom; Scale = scale; Pivot = pivot;
        DisplacementLimit = displacementLimit;
        pin = Exact(pinnedThrough); full = Exact(fullFrom); exactWidth = full - pin;
        pivotCoordinate = Coordinate(pivot);
        width = (double)fullFrom - pinnedThrough;
        delta = (double)scale - 1;
        // Exact whole-transition proof: wPrime <= 3/(2*width), a >= min(1,s).
        // Bound only the adverse rank-one term. No rounded coordinate or sample decides admission.
        var adverse = scale > 1 ? pivotCoordinate - pin : full - pivotCoordinate;
        if (adverse.Numerator.Sign < 0) adverse = new(0, 1);
        var magnitude = Exact(scale > 1 ? scale - 1 : 1 - scale);
        var lower = Exact(Math.Min(1, scale)) - ((magnitude * new EllipsoidRational(3, 2) * adverse) / exactWidth);
        if (lower.Compare(new(1, 8)) < 0)
            throw new ArgumentException("The tilted field cannot prove a determinant factor of at least 1/8 over the entire transition.");
        Certificate = new(lower.Numerator.ToString(CultureInfo.InvariantCulture), lower.Denominator.ToString(CultureInfo.InvariantCulture));
    }

    public TiltedRampPointResult Evaluate(Point3 point)
    {
        var mask = Mask(point);
        if (mask.Membership == TiltedRampMembership.Pinned) return new(point, mask.Membership, 0, 0);
        Point3 output;
        if (mask.Membership == TiltedRampMembership.Full)
            output = new UniformTransform(Pivot, Scale, default).Apply(point);
        else
        {
            var a = 1 + (delta * mask.Weight);
            output = new(Narrow(Pivot.X + (a * ((double)point.X - Pivot.X))),
                Narrow(Pivot.Y + (a * ((double)point.Y - Pivot.Y))),
                Narrow(Pivot.Z + (a * ((double)point.Z - Pivot.Z))));
            _ = RoundedDeterminantFactor(point, mask.Weight, mask.Derivative);
        }
        var distance = ConservativePointDistance.RoundUp(point, output);
        if (distance > DisplacementLimit) throw new ArgumentException("Stored-point displacement exceeds the tilted field's declared cap.");
        return new(output, mask.Membership, mask.Weight, distance);
    }

    public TangentFrame TransformFrame(Point3 point, TangentFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateFrame(frame);
        var mask = Mask(point);
        if (mask.Membership != TiltedRampMembership.Transition) return frame;
        var a = 1 + (delta * mask.Weight);
        var b = RoundedDeterminantFactor(point, mask.Weight, mask.Derivative);
        var factor = delta * mask.Derivative;
        double[] u = [factor * ((double)point.X - Pivot.X), factor * ((double)point.Y - Pivot.Y), factor * ((double)point.Z - Pivot.Z)];
        double[] v = [0, 0, 0]; v[FirstAxis] = FirstSign; v[SecondAxis] = SecondSign;
        double[] n = [frame.Normal.X, frame.Normal.Y, frame.Normal.Z];
        double[] tangent = [frame.Tangent.X, frame.Tangent.Y, frame.Tangent.Z];
        var correction = Dot(u, n) / b;
        for (var i = 0; i < 3; i++) n[i] -= v[i] * correction;
        Normalize(n);
        var projection = Dot(v, tangent);
        for (var i = 0; i < 3; i++) tangent[i] = (a * tangent[i]) + (u[i] * projection);
        projection = Dot(n, tangent);
        for (var i = 0; i < 3; i++) tangent[i] -= n[i] * projection;
        Normalize(tangent);
        var output = new TangentFrame(new(Narrow(n[0]), Narrow(n[1]), Narrow(n[2])),
            new(Narrow(tangent[0]), Narrow(tangent[1]), Narrow(tangent[2])), frame.Handedness);
        ValidateFrame(output);
        return output;
    }

    private (TiltedRampMembership Membership, double Weight, double Derivative) Mask(Point3 point)
    {
        var q = Coordinate(point);
        if (q.Compare(pin) <= 0) return (TiltedRampMembership.Pinned, 0, 0);
        if (q.Compare(full) >= 0) return (TiltedRampMembership.Full, 1, 0);
        var t = Nearest((q - pin) / exactWidth);
        var weight = (t * t) * (3 - (2 * t));
        var derivative = ((6 * t) * (1 - t)) / width;
        if (!double.IsFinite(t) || t is < 0 or > 1 || !double.IsFinite(weight) || weight is < 0 or > 1
            || !double.IsFinite(derivative) || derivative < 0)
            throw new ArgumentException("Tilted transition arithmetic left its declared finite interval.");
        return (TiltedRampMembership.Transition, weight, derivative);
    }

    private double RoundedDeterminantFactor(Point3 point, double weight, double derivative)
    {
        var qDistance = Nearest(Coordinate(point) - pivotCoordinate);
        var b = (1 + (delta * weight)) + ((delta * derivative) * qDistance);
        if (!double.IsFinite(b) || b < 0.125)
            throw new ArgumentException("Rounded tilted Jacobian is unrepresentable or below 1/8.");
        return b;
    }

    private EllipsoidRational Coordinate(Point3 point) =>
        (new EllipsoidRational(FirstSign, 1) * Exact(Component(point, FirstAxis)))
        + (new EllipsoidRational(SecondSign, 1) * Exact(Component(point, SecondAxis)));
    private static float Component(Point3 p, int axis) => axis switch { 0 => p.X, 1 => p.Y, _ => p.Z };
    private static EllipsoidRational Exact(float value) => EllipsoidRational.FromDouble(value);

    // Shared exact IEEE rational primitives; endpoint classification and this field's
    // operation order remain separate from the published ellipsoid/axis-ramp contracts.
    private static double Nearest(EllipsoidRational value)
    {
        if (value.Numerator.IsZero) return 0;
        var negative = value.Numerator.Sign < 0;
        var magnitude = negative ? new EllipsoidRational(-value.Numerator, value.Denominator) : value;
        var (lower, upper) = magnitude.EnclosePositive();
        if (!double.IsFinite(upper)) throw new ArgumentException("Tilted rational cannot be represented by finite binary64 arithmetic.");
        var midpoint = (EllipsoidRational.FromDouble(lower) + EllipsoidRational.FromDouble(upper)) / new EllipsoidRational(2, 1);
        var comparison = magnitude.Compare(midpoint);
        var rounded = comparison < 0 || (comparison == 0 && (BitConverter.DoubleToUInt64Bits(lower) & 1) == 0) ? lower : upper;
        return negative ? -rounded : rounded;
    }

    private static double Dot(double[] a, double[] b) => ((a[0] * b[0]) + (a[1] * b[1])) + (a[2] * b[2]);
    private static float Narrow(double value) => float.IsFinite((float)value) ? (float)value
        : throw new ArgumentException("Tilted output cannot be represented by a finite float.");
    private static void Normalize(double[] vector)
    {
        var length = Math.Sqrt(Dot(vector, vector));
        if (!double.IsFinite(length) || length <= 0) throw new ArgumentException("Tilted differential frame is degenerate or unrepresentable.");
        for (var i = 0; i < 3; i++) vector[i] /= length;
    }

    private static void ValidateFrame(TangentFrame frame)
    {
        static double FrameDot(Point3 a, Point3 b) => (((double)a.X * b.X) + ((double)a.Y * b.Y)) + ((double)a.Z * b.Z);
        if (frame.Handedness is not (-1f or 1f) || Math.Abs(FrameDot(frame.Normal, frame.Normal) - 1) > 1e-4
            || Math.Abs(FrameDot(frame.Tangent, frame.Tangent) - 1) > 1e-4 || Math.Abs(FrameDot(frame.Normal, frame.Tangent)) > 1e-4)
            throw new ArgumentException("Require a unit orthogonal frame with handedness +/-1.");
    }
}
