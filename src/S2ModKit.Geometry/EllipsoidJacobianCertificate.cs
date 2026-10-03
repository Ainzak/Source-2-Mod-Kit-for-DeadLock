using System.Numerics;

namespace S2ModKit.Geometry;

/// <summary>Exact lower bound for the ideal continuous field; not a quantized-triangle guarantee.</summary>
public sealed record EllipsoidJacobianCertificate(
    BigInteger Numerator, BigInteger Denominator, double LowerBound, double UpperBound,
    double MinimumSingularValueLowerBound)
{
    public const int SubdivisionDepth = 12;
    public const string Algorithm = "ellipsoid_bernstein_midpoint";
    public const int Version = 1;

    public static EllipsoidJacobianCertificate Create(float coreFraction, float scale, Point3 radii)
    {
        if (!float.IsFinite(coreFraction) || coreFraction is <= 0 or >= 1
            || !float.IsFinite(scale) || scale is < 0.5f or > 2 || scale == 1)
            throw new ArgumentException("Require finite 0<coreFraction<1 and nonidentity scale in [0.5,2].");
        var minimumRadius = Math.Min(radii.X, Math.Min(radii.Y, radii.Z));
        var maximumRadius = Math.Max(radii.X, Math.Max(radii.Y, radii.Z));
        if (minimumRadius <= 0 || (double)maximumRadius > (double)minimumRadius * 8)
            throw new ArgumentException("Require positive radii with aspect ratio at most 8.");
        var h = EllipsoidRational.FromDouble(coreFraction);
        var s = EllipsoidRational.FromDouble(scale);
        var v = EllipsoidRational.One - h;
        var delta = s - EllipsoidRational.One;
        var two = new EllipsoidRational(2, 1);
        EllipsoidRational[] controls = [v * s, (v * s) - (two * delta * h), v - (two * delta), v];
        // All starting controls are dyadics. An extra 3*depth bits makes every midpoint
        // operation integral, including negative controls. No division truncates a remainder.
        var denominator = controls.Max(c => c.Denominator) << (3 * SubdivisionDepth);
        var integers = controls.Select(c => c.Numerator * (denominator / c.Denominator)).ToArray();
        var minimum = new EllipsoidRational(Subdivide(integers, SubdivisionDepth), denominator) / v;
        if (minimum.Compare(new(1, 8)) < 0)
            throw new ArgumentException("The exact global radial Jacobian bound is below 1/8 or inconclusive.");
        var enclosure = minimum.EnclosePositive();
        var singular = (minimum.Compare(EllipsoidRational.FromDouble(Math.Min(1, scale))) <= 0
            ? minimum : EllipsoidRational.FromDouble(Math.Min(1, scale)))
            * EllipsoidRational.FromDouble(minimumRadius) / EllipsoidRational.FromDouble(maximumRadius);
        return new(minimum.Numerator, minimum.Denominator, enclosure.Lower, enclosure.Upper, singular.EnclosePositive().Lower);
    }

    private static BigInteger Subdivide(BigInteger[] b, int depth)
    {
        if (depth == 0) return b.Min();
        var p = (b[0] + b[1]) / 2; var q = (b[1] + b[2]) / 2; var r = (b[2] + b[3]) / 2;
        var u = (p + q) / 2; var v = (q + r) / 2; var w = (u + v) / 2;
        return BigInteger.Min(Subdivide([b[0], p, u, w], depth - 1), Subdivide([w, v, r, b[3]], depth - 1));
    }
}
