namespace S2ModKit.Geometry;

/// <summary>Two independent disjoint supports; touching boundaries are pinned in both fields.</summary>
public sealed class MirroredEllipsoidScale : IEllipsoidScale
{
    public EllipsoidScale Base { get; }
    public EllipsoidScale Reflected { get; }
    public int Axis { get; }
    public float PlaneCoordinate { get; }
    public EllipsoidJacobianCertificate Certificate => Base.Certificate;
    public float DisplacementLimit => Base.DisplacementLimit;

    public MirroredEllipsoidScale(Point3 center, Point3 radii, float coreFraction, float scale,
        float displacementLimit, int axis, float planeCoordinate)
    {
        if (axis is < 0 or > 2 || !float.IsFinite(planeCoordinate)) throw new ArgumentException("Require a finite explicit model-axis plane.");
        Axis = axis; PlaneCoordinate = planeCoordinate;
        var c = EllipsoidRational.FromDouble(Component(center, axis));
        var plane = EllipsoidRational.FromDouble(planeCoordinate);
        var distance = c - plane;
        if (distance.Numerator.Sign < 0) distance = new(-distance.Numerator, distance.Denominator);
        if (distance.Compare(EllipsoidRational.FromDouble(Component(radii, axis))) < 0)
            throw new EllipsoidMirrorException("ELLIPSOID_MIRROR_OVERLAP", "The exact outer supports overlap.");
        var exact = (new EllipsoidRational(2, 1) * plane) - c;
        // Binary64 is only a candidate generator. Exact rational equality authorizes the float word.
        var candidate = (float)((2d * planeCoordinate) - Component(center, axis));
        if (!float.IsFinite(candidate) || EllipsoidRational.FromDouble(candidate).Compare(exact) != 0)
            throw new EllipsoidMirrorException("ELLIPSOID_REFLECTION_UNREPRESENTABLE", "The reflected center is not exactly representable in binary32.");
        if (exact.Numerator.IsZero) candidate = 0f;
        var reflected = axis switch { 0 => new Point3(candidate, center.Y, center.Z), 1 => new(center.X, candidate, center.Z), _ => new(center.X, center.Y, candidate) };
        Base = new(center, radii, coreFraction, scale, displacementLimit);
        Reflected = new(reflected, radii, coreFraction, scale, displacementLimit);
    }

    public (EllipsoidPointResult Base, EllipsoidPointResult Reflected) EvaluatePair(Point3 point)
    {
        var first = Base.Evaluate(point); var second = Reflected.Evaluate(point);
        if (first.Membership != EllipsoidMembership.Pinned && second.Membership != EllipsoidMembership.Pinned)
            throw new ArgumentException("A point cannot belong to both active supports.");
        return (first, second);
    }

    public EllipsoidPointResult Evaluate(Point3 point)
    {
        var pair = EvaluatePair(point);
        return pair.Base.Membership != EllipsoidMembership.Pinned ? pair.Base : pair.Reflected;
    }

    public TangentFrame TransformFrame(Point3 point, TangentFrame frame)
    {
        var pair = EvaluatePair(point);
        return pair.Base.Membership != EllipsoidMembership.Pinned ? Base.TransformFrame(point, frame) : Reflected.TransformFrame(point, frame);
    }

    private static float Component(Point3 p, int axis) => axis switch { 0 => p.X, 1 => p.Y, _ => p.Z };
}

public sealed class EllipsoidMirrorException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}
