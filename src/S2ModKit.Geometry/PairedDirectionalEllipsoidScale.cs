using System.Numerics;

namespace S2ModKit.Geometry;

/// <summary>Exact projection separation; zero gap means touching pinned boundaries.</summary>
public sealed record DirectionalPairSeparation
{
    internal DirectionalPairSeparation(int axis, EllipsoidRational distance, EllipsoidRational radiusSum)
    {
        Axis = axis;
        CenterDistanceNumerator = distance.Numerator; CenterDistanceDenominator = distance.Denominator;
        RadiusSumNumerator = radiusSum.Numerator; RadiusSumDenominator = radiusSum.Denominator;
        var gap = distance - radiusSum;
        GapNumerator = gap.Numerator; GapDenominator = gap.Denominator;
    }

    public int Axis { get; }
    public BigInteger CenterDistanceNumerator { get; }
    public BigInteger CenterDistanceDenominator { get; }
    public BigInteger RadiusSumNumerator { get; }
    public BigInteger RadiusSumDenominator { get; }
    public BigInteger GapNumerator { get; }
    public BigInteger GapDenominator { get; }

    public static DirectionalPairSeparation Prove(Point3 firstCenter, Point3 firstRadii, Point3 secondCenter, Point3 secondRadii)
    {
        float[] a = [firstCenter.X, firstCenter.Y, firstCenter.Z];
        float[] b = [secondCenter.X, secondCenter.Y, secondCenter.Z];
        float[] ra = [firstRadii.X, firstRadii.Y, firstRadii.Z];
        float[] rb = [secondRadii.X, secondRadii.Y, secondRadii.Z];
        if (ra.Concat(rb).Any(radius => radius <= 0)) throw new ArgumentException("Require positive projection radii.");
        for (var axis = 0; axis < 3; axis++)
        {
            var distance = EllipsoidRational.FromDouble(a[axis]) - EllipsoidRational.FromDouble(b[axis]);
            if (distance.Numerator.Sign < 0) distance = new(-distance.Numerator, distance.Denominator);
            var radiusSum = EllipsoidRational.FromDouble(ra[axis]) + EllipsoidRational.FromDouble(rb[axis]);
            if (distance.Compare(radiusSum) >= 0) return new(axis, distance, radiusSum);
        }
        throw new ArgumentException("Field projection intervals overlap on every axis; exact separation is unproved.", nameof(secondCenter));
    }
}

public sealed record PairedDirectionalPointResult(Point3 Position, int? ActiveFieldIndex,
    EllipsoidMembership Membership, double Weight, float MaximumDisplacement);

/// <summary>Two disjoint fields, dispatched from immutable source positions without composition.</summary>
public sealed class PairedDirectionalEllipsoidScale
{
    public const string SeparationAlgorithm = "axis_projection_exact";
    public const int SeparationVersion = 1;
    public DirectionalEllipsoidScale First { get; }
    public DirectionalEllipsoidScale Second { get; }
    public DirectionalPairSeparation Separation { get; }

    public PairedDirectionalEllipsoidScale(DirectionalEllipsoidScale first, DirectionalEllipsoidScale second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        First = first; Second = second;
        Separation = DirectionalPairSeparation.Prove(first.Pivot, first.OuterRadii, second.Pivot, second.OuterRadii);
    }

    public PairedDirectionalPointResult Evaluate(Point3 source)
    {
        var first = First.Evaluate(source);
        var second = Second.Evaluate(source);
        var firstActive = first.Membership != EllipsoidMembership.Pinned;
        var secondActive = second.Membership != EllipsoidMembership.Pinned;
        if (firstActive && secondActive) throw new ArgumentException("Both field interiors contain the source position.", nameof(source));
        var result = firstActive ? first : second;
        return new(result.Position, firstActive ? 0 : secondActive ? 1 : null,
            result.Membership, result.Weight, result.MaximumDisplacement);
    }

    public TangentFrame TransformFrame(Point3 source, TangentFrame frame)
    {
        // Even an exterior frame must pass the single-field source validation. Membership and
        // transport always use the original point, including when the stored result crosses a support.
        var result = Evaluate(source);
        return result.ActiveFieldIndex == 1 ? Second.TransformFrame(source, frame) : First.TransformFrame(source, frame);
    }

    public DirectionalJacobian Jacobian(Point3 source) => Evaluate(source).ActiveFieldIndex == 1
        ? Second.Jacobian(source) : First.Jacobian(source);

}
