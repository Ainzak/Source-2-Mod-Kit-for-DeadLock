namespace S2ModKit.Geometry;

/// <summary>A binary64 analytic differential; never an authorization certificate by itself.</summary>
public readonly record struct DirectionalJacobian(
    double M11, double M12, double M13, double M21, double M22, double M23,
    double M31, double M32, double M33)
{
    public double this[int row, int column] => (row, column) switch
    {
        (0, 0) => M11,
        (0, 1) => M12,
        (0, 2) => M13,
        (1, 0) => M21,
        (1, 1) => M22,
        (1, 2) => M23,
        (2, 0) => M31,
        (2, 1) => M32,
        (2, 2) => M33,
        _ => throw new ArgumentOutOfRangeException(nameof(row), "Require row and column in [0,2].")
    };
}

/// <summary>Centered directional field, independent of resource layouts and persisted contracts.</summary>
public sealed class DirectionalEllipsoidScale
{
    public const string NumericalPolicy = "directional_ellipsoid_numeric";
    public const int NumericalVersion = 1;
    public Point3 Pivot { get; }
    public Point3 OuterRadii { get; }
    public float CoreFraction { get; }
    public Point3 Scale { get; }
    public float DisplacementLimit { get; }
    public DirectionalEllipsoidCertificate Certificate { get; }
    private readonly double width;
    private readonly double[] scales;
    private readonly double[] delta;
    private readonly bool uniformCore;

    public DirectionalEllipsoidScale(Point3 pivot, Point3 outerRadii, float coreFraction,
        Point3 scale, float displacementLimit)
    {
        if (!float.IsFinite(displacementLimit) || displacementLimit is <= 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(displacementLimit));
        Certificate = DirectionalEllipsoidCertificate.Create(coreFraction, scale, outerRadii);
        Pivot = pivot; OuterRadii = outerRadii; CoreFraction = coreFraction;
        Scale = scale; DisplacementLimit = displacementLimit;
        width = 1 - (double)coreFraction;
        scales = [scale.X, scale.Y, scale.Z];
        delta = scales.Select(value => value - 1).ToArray();
        uniformCore = scale.X == scale.Y && scale.Y == scale.Z;
    }

    public EllipsoidPointResult Evaluate(Point3 point)
    {
        var mask = Mask(point);
        if (mask.Membership == EllipsoidMembership.Pinned) return new(point, mask.Membership, 0, 0);
        var a = Factors(mask);
        var output = new Point3(Coordinate(point.X, Pivot.X, a[0], delta[0]),
            Coordinate(point.Y, Pivot.Y, a[1], delta[1]), Coordinate(point.Z, Pivot.Z, a[2], delta[2]));
        var displacement = ConservativePointDistance.RoundUp(point, output);
        if (displacement > DisplacementLimit) throw new ArgumentException("Stored-point displacement exceeds the declared limit.", nameof(point));
        return new(output, mask.Membership, mask.Weight, displacement);
    }

    public DirectionalJacobian Jacobian(Point3 point)
    {
        var mask = Mask(point);
        if (mask.Membership == EllipsoidMembership.Pinned) return new(1, 0, 0, 0, 1, 0, 0, 0, 1);
        var (a, u, g) = Differential(point, mask);
        return new(a[0] + (u[0] * g[0]), u[0] * g[1], u[0] * g[2],
            u[1] * g[0], a[1] + (u[1] * g[1]), u[1] * g[2],
            u[2] * g[0], u[2] * g[1], a[2] + (u[2] * g[2]));
    }

    public TangentFrame TransformFrame(Point3 point, TangentFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateFrame(frame);
        var mask = Mask(point);
        if (mask.Membership == EllipsoidMembership.Pinned || (mask.Membership == EllipsoidMembership.Core && uniformCore))
            return frame;
        var (a, u, g) = Differential(point, mask);
        var e = Divide(u, a);
        var beta = 1 + Dot(g, e);
        if (!double.IsFinite(beta) || EllipsoidRational.FromDouble(beta).Compare(Certificate.BetaLowerBound.Exact) < 0)
            throw new ArgumentException("Rounded differential beta cannot prove the prescribed positive bound.");
        double[] n = [frame.Normal.X, frame.Normal.Y, frame.Normal.Z];
        double[] tangent = [frame.Tangent.X, frame.Tangent.Y, frame.Tangent.Z];
        var r = Divide(n, a);
        var z = Divide(g, a);
        var k = Dot(u, r) / beta;
        for (var i = 0; i < 3; i++) n[i] = r[i] - (z[i] * k);
        Normalize(n);
        var projection = Dot(g, tangent);
        for (var i = 0; i < 3; i++) tangent[i] = (a[i] * tangent[i]) + (u[i] * projection);
        projection = Dot(n, tangent);
        for (var i = 0; i < 3; i++) tangent[i] -= n[i] * projection;
        Normalize(tangent);
        var output = new TangentFrame(new(Narrow(n[0]), Narrow(n[1]), Narrow(n[2])),
            new(Narrow(tangent[0]), Narrow(tangent[1]), Narrow(tangent[2])), frame.Handedness);
        ValidateFrame(output);
        return output;
    }

    private (double[] A, double[] U, double[] G) Differential(Point3 point, MaskResult mask)
    {
        var a = Factors(mask);
        if (mask.Membership == EllipsoidMembership.Core) return (a, [0, 0, 0], [0, 0, 0]);
        double[] d = [(double)point.X - Pivot.X, (double)point.Y - Pivot.Y, (double)point.Z - Pivot.Z];
        double[] radii = [OuterRadii.X, OuterRadii.Y, OuterRadii.Z];
        var derivative = ((-6 * mask.T) * (1 - mask.T)) / width;
        var u = new double[3]; var g = new double[3];
        for (var i = 0; i < 3; i++)
        {
            u[i] = delta[i] * d[i];
            g[i] = (((derivative * d[i]) / radii[i]) / radii[i]) / mask.Rho;
            if (!double.IsFinite(u[i]) || !double.IsFinite(g[i])) throw new ArgumentException("Differential is not finite.");
        }
        return (a, u, g);
    }

    private double[] Factors(MaskResult mask) => mask.Membership == EllipsoidMembership.Core
        ? scales : delta.Select(value => 1 + (value * mask.Weight)).ToArray();

    private MaskResult Mask(Point3 point)
    {
        static EllipsoidRational Square(float p, float c, float r)
        {
            var value = (EllipsoidRational.FromDouble(p) - EllipsoidRational.FromDouble(c)) / EllipsoidRational.FromDouble(r);
            return value * value;
        }
        var q = (Square(point.X, Pivot.X, OuterRadii.X) + Square(point.Y, Pivot.Y, OuterRadii.Y))
            + Square(point.Z, Pivot.Z, OuterRadii.Z);
        var h = EllipsoidRational.FromDouble(CoreFraction);
        if (q.Compare(h * h) <= 0) return new(EllipsoidMembership.Core, 1, 0, 0);
        if (q.Compare(EllipsoidRational.One) >= 0) return new(EllipsoidMembership.Pinned, 0, 1, 1);
        var rho = q.SqrtNearest();
        var t = (rho - CoreFraction) / width;
        var z = 1 - t;
        var weight = (z * z) * (1 + (2 * t));
        if (!double.IsFinite(t) || t is < 0 or > 1 || !double.IsFinite(weight) || weight is < 0 or > 1)
            throw new ArgumentException("Rounded transition is outside its prescribed interval.");
        return new(EllipsoidMembership.Transition, weight, t, rho);
    }

    private readonly record struct MaskResult(EllipsoidMembership Membership, double Weight, double T, double Rho);
    private static float Coordinate(float source, float pivot, double factor, double difference) => difference == 0
        ? source : Narrow(pivot + (factor * ((double)source - pivot)));
    private static double[] Divide(double[] values, double[] divisors) =>
        [values[0] / divisors[0], values[1] / divisors[1], values[2] / divisors[2]];
    private static double Dot(double[] a, double[] b) => ((a[0] * b[0]) + (a[1] * b[1])) + (a[2] * b[2]);
    private static float Narrow(double value) => float.IsFinite((float)value) ? (float)value
        : throw new ArgumentException("Directional result cannot be represented by a finite float.");
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
