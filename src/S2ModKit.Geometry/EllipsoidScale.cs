namespace S2ModKit.Geometry;

public enum EllipsoidMembership { Core, Transition, Pinned }

public readonly record struct EllipsoidPointResult(Point3 Position, EllipsoidMembership Membership,
    double Weight, float MaximumDisplacement);

public interface IEllipsoidScale
{
    EllipsoidJacobianCertificate Certificate { get; }
    float DisplacementLimit { get; }
    EllipsoidPointResult Evaluate(Point3 point);
    TangentFrame TransformFrame(Point3 point, TangentFrame frame);
}

/// <summary>A frozen model-space field with exact endpoint classification and bounded stored-point displacement.</summary>
public sealed class EllipsoidScale : IEllipsoidScale
{
    public Point3 Center { get; }
    public Point3 OuterRadii { get; }
    public float CoreFraction { get; }
    public float Scale { get; }
    public float DisplacementLimit { get; }
    public EllipsoidJacobianCertificate Certificate { get; }
    private readonly double width;
    private readonly double delta;

    public EllipsoidScale(Point3 center, Point3 outerRadii, float coreFraction, float scale, float displacementLimit)
    {
        if (!float.IsFinite(displacementLimit) || displacementLimit is <= 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(displacementLimit));
        Certificate = EllipsoidJacobianCertificate.Create(coreFraction, scale, outerRadii);
        Center = center; OuterRadii = outerRadii; CoreFraction = coreFraction;
        Scale = scale; DisplacementLimit = displacementLimit;
        width = 1 - (double)coreFraction;
        delta = (double)scale - 1;
    }

    public EllipsoidPointResult Evaluate(Point3 point)
    {
        var mask = Mask(point);
        if (mask.Membership == EllipsoidMembership.Pinned) return new(point, mask.Membership, 0, 0);
        var a = mask.Membership == EllipsoidMembership.Core ? Scale : 1 + (delta * mask.Weight);
        // ellipsoid_numeric@1: widened subtraction, multiplication, addition, then ONE
        // nearest-even float narrowing. Never route the core through legacy float arithmetic.
        var output = new Point3(Narrow(Center.X + (a * ((double)point.X - Center.X))),
            Narrow(Center.Y + (a * ((double)point.Y - Center.Y))),
            Narrow(Center.Z + (a * ((double)point.Z - Center.Z))));
        var distance = ConservativePointDistance.RoundUp(point, output);
        if (distance > DisplacementLimit) throw new ArgumentException("Stored-point displacement exceeds the declared limit.");
        return new(output, mask.Membership, mask.Weight, distance);
    }

    public TangentFrame TransformFrame(Point3 point, TangentFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateFrame(frame);
        var mask = Mask(point);
        if (mask.Membership != EllipsoidMembership.Transition) return frame;
        var a = 1 + (delta * mask.Weight);
        var derivative = ((-6 * mask.T) * (1 - mask.T)) / width;
        var b = a + ((delta * mask.Rho) * derivative);
        if (!double.IsFinite(b) || b < 0.125) throw new ArgumentException("Rounded Jacobian is unrepresentable or below 1/8.");
        double[] d = [(double)point.X - Center.X, (double)point.Y - Center.Y, (double)point.Z - Center.Z];
        double[] r = [OuterRadii.X, OuterRadii.Y, OuterRadii.Z];
        var g = Enumerable.Range(0, 3).Select(i => (((derivative * d[i]) / r[i]) / r[i]) / mask.Rho).ToArray();
        double[] n = [frame.Normal.X, frame.Normal.Y, frame.Normal.Z];
        double[] tangent = [frame.Tangent.X, frame.Tangent.Y, frame.Tangent.Z];
        var factor = (delta * Dot(d, n)) / b;
        for (var i = 0; i < 3; i++) n[i] -= g[i] * factor;
        Normalize(n);
        var projection = Dot(g, tangent);
        for (var i = 0; i < 3; i++) tangent[i] = (a * tangent[i]) + ((delta * d[i]) * projection);
        projection = Dot(n, tangent);
        for (var i = 0; i < 3; i++) tangent[i] -= n[i] * projection;
        Normalize(tangent);
        var output = new TangentFrame(new(Narrow(n[0]), Narrow(n[1]), Narrow(n[2])),
            new(Narrow(tangent[0]), Narrow(tangent[1]), Narrow(tangent[2])), frame.Handedness);
        ValidateFrame(output);
        return output;
    }

    private (EllipsoidMembership Membership, double Weight, double T, double Rho) Mask(Point3 point)
    {
        static EllipsoidRational Squared(float p, float c, float r)
        {
            var q = (EllipsoidRational.FromDouble(p) - EllipsoidRational.FromDouble(c)) / EllipsoidRational.FromDouble(r);
            return q * q;
        }
        var q = (Squared(point.X, Center.X, OuterRadii.X) + Squared(point.Y, Center.Y, OuterRadii.Y))
            + Squared(point.Z, Center.Z, OuterRadii.Z);
        var h = EllipsoidRational.FromDouble(CoreFraction);
        if (q.Compare(h * h) <= 0) return (EllipsoidMembership.Core, 1, 0, 0);
        if (q.Compare(EllipsoidRational.One) >= 0) return (EllipsoidMembership.Pinned, 0, 1, 1);
        var rho = q.SqrtNearest();
        var t = (rho - CoreFraction) / width;
        var u = 1 - t;
        var weight = (u * u) * (1 + (2 * t));
        if (!double.IsFinite(t) || t is < 0 or > 1 || !double.IsFinite(weight) || weight is < 0 or > 1)
            throw new ArgumentException("Rounded transition is outside its declared finite interval.");
        return (EllipsoidMembership.Transition, weight, t, rho);
    }

    private static double Dot(double[] a, double[] b) => ((a[0] * b[0]) + (a[1] * b[1])) + (a[2] * b[2]);
    private static float Narrow(double value) => float.IsFinite((float)value) ? (float)value
        : throw new ArgumentException("Ellipsoid output cannot be represented by a finite float.");
    private static void Normalize(double[] vector)
    {
        var length = Math.Sqrt(Dot(vector, vector));
        if (!double.IsFinite(length) || length <= 0) throw new ArgumentException("Differential frame is degenerate or unrepresentable.");
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
