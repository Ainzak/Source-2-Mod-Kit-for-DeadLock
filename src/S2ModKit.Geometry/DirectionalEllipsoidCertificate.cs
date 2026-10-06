using System.Numerics;

namespace S2ModKit.Geometry;

/// <summary>A reduced positive rational bound; display doubles enclose rather than decide it.</summary>
public readonly record struct DirectionalFieldBound
{
    internal DirectionalFieldBound(EllipsoidRational value)
    {
        if (value.Numerator.Sign <= 0) throw new ArgumentOutOfRangeException(nameof(value));
        Numerator = value.Numerator;
        Denominator = value.Denominator;
        (LowerDouble, UpperDouble) = value.EnclosePositive();
    }

    public BigInteger Numerator { get; }
    public BigInteger Denominator { get; }
    public double LowerDouble { get; }
    public double UpperDouble { get; }
    internal EllipsoidRational Exact => new(Numerator, Denominator);
}

/// <summary>Whole-field directional determinant and Euclidean inverse-norm proof.</summary>
public sealed class DirectionalEllipsoidCertificate
{
    public const string Algorithm = "directional_ellipsoid_rank_one";
    public const int Version = 1;
    public const int SubdivisionDepth = 12;
    public Point3 Scale { get; }
    public Point3 OuterRadii { get; }
    public float CoreFraction { get; }
    public IReadOnlyList<DirectionalFieldBound> AxisLowerBounds { get; }
    public IReadOnlyList<DirectionalFieldBound> MinimumAxisFactors { get; }
    public DirectionalFieldBound AlphaLowerBound { get; }
    public DirectionalFieldBound BetaLowerBound { get; }
    public DirectionalFieldBound RankOneNormUpperBound { get; }
    public DirectionalFieldBound InverseNormUpperBound { get; }
    public DirectionalFieldBound MinimumSingularValueLowerBound { get; }
    public DirectionalFieldBound DeterminantLowerBound { get; }
    public DirectionalFieldBound IdealDisplacementUpperBound { get; }

    private DirectionalEllipsoidCertificate(Point3 scale, Point3 radii, float core,
        EllipsoidRational[] axis, EllipsoidRational[] alpha, EllipsoidRational beta,
        EllipsoidRational norm, EllipsoidRational inverse, EllipsoidRational singular,
        EllipsoidRational determinant, EllipsoidRational displacement)
    {
        Scale = scale; OuterRadii = radii; CoreFraction = core;
        AxisLowerBounds = Array.AsReadOnly(axis.Select(value => new DirectionalFieldBound(value)).ToArray());
        MinimumAxisFactors = Array.AsReadOnly(alpha.Select(value => new DirectionalFieldBound(value)).ToArray());
        AlphaLowerBound = new(alpha.MinBy(value => value, RationalComparer.Instance));
        BetaLowerBound = new(beta); RankOneNormUpperBound = new(norm);
        InverseNormUpperBound = new(inverse); MinimumSingularValueLowerBound = new(singular);
        DeterminantLowerBound = new(determinant); IdealDisplacementUpperBound = new(displacement);
    }

    public static DirectionalEllipsoidCertificate Create(float coreFraction, Point3 scale, Point3 radii)
    {
        if (!float.IsFinite(coreFraction) || coreFraction is <= 0 or >= 1)
            throw new ArgumentException("Require finite 0<coreFraction<1.", nameof(coreFraction));
        float[] scales = [scale.X, scale.Y, scale.Z];
        if (scales.Any(value => value is < 0.5f or > 2) || scales.All(value => value == 1))
            throw new ArgumentException("Require a nonidentity scale vector with factors in [0.5,2].", nameof(scale));
        float[] radiusWords = [radii.X, radii.Y, radii.Z];
        if (radiusWords.Any(value => value <= 0)) throw new ArgumentException("Require positive radii.", nameof(radii));
        var radius = radiusWords.Select(value => EllipsoidRational.FromDouble(value)).ToArray();
        var minimumRadius = radius.MinBy(value => value, RationalComparer.Instance);
        var maximumRadius = radius.MaxBy(value => value, RationalComparer.Instance);
        var ratio = maximumRadius / minimumRadius;
        if (ratio.Compare(new(8, 1)) > 0) throw new ArgumentException("Require exact radius aspect ratio <=8.", nameof(radii));
        var s = scales.Select(value => EllipsoidRational.FromDouble(value)).ToArray();
        var one = EllipsoidRational.One;
        var width = one - EllipsoidRational.FromDouble(coreFraction);
        var delta = s.Select(value => value - one).ToArray();
        var axis = s.Select(value => AxisBound(coreFraction, value)).ToArray();
        if (axis.Any(value => value.Compare(new(1, 8)) < 0))
            throw new ArgumentException("A whole-transition axis bound is below 1/8 or inconclusive.");
        var alpha = s.Select(value => Min(one, value)).ToArray();
        var minimumFactor = alpha.MinBy(value => value, RationalComparer.Instance);
        var beta = axis.Select((value, i) => value / Max(one, s[i])).MinBy(value => value, RationalComparer.Instance);
        var maximumDelta = delta.Select(Abs).MaxBy(value => value, RationalComparer.Instance);
        var norm = ((maximumDelta * ratio) * new EllipsoidRational(3, 2)) / width;
        var inverse = (one / minimumFactor) + (norm / ((minimumFactor * minimumFactor) * beta));
        var singular = one / inverse;
        if (singular.Compare(new(1, 64)) < 0)
            throw new ArgumentException("The global Euclidean singular-value bound is below 1/64 or inconclusive.");
        var displacement = delta.Select((value, i) => Abs(value) * radius[i]).MaxBy(value => value, RationalComparer.Instance);
        if (displacement.Compare(new(64, 1)) > 0)
            throw new ArgumentException("The ideal whole-field displacement upper bound exceeds 64.");
        var determinant = ((alpha[0] * alpha[1]) * alpha[2]) * beta;
        return new(scale, radii, coreFraction, axis, alpha, beta, norm, inverse, singular, determinant, displacement);
    }

    private static EllipsoidRational AxisBound(float core, EllipsoidRational scale)
    {
        var one = EllipsoidRational.One;
        if (scale.Compare(one) == 0) return one;
        var h = EllipsoidRational.FromDouble(core);
        var width = one - h;
        var twiceDelta = new EllipsoidRational(2, 1) * (scale - one);
        EllipsoidRational[] controls = [width * scale, (width * scale) - (twiceDelta * h), width - twiceDelta, width];
        // Reviewed exact-dyadic primitive: 3*depth extra bits make every midpoint
        // integral. This does NOT reuse the scalar certificate or symmetry proof.
        var denominator = controls.Max(value => value.Denominator) << (3 * SubdivisionDepth);
        var words = controls.Select(value => value.Numerator * (denominator / value.Denominator)).ToArray();
        return new EllipsoidRational(Subdivide(words, SubdivisionDepth), denominator) / width;
    }

    private static BigInteger Subdivide(BigInteger[] b, int depth)
    {
        if (depth == 0) return b.Min();
        var p = (b[0] + b[1]) / 2; var q = (b[1] + b[2]) / 2; var r = (b[2] + b[3]) / 2;
        var u = (p + q) / 2; var v = (q + r) / 2; var midpoint = (u + v) / 2;
        return BigInteger.Min(Subdivide([b[0], p, u, midpoint], depth - 1), Subdivide([midpoint, v, r, b[3]], depth - 1));
    }

    private static EllipsoidRational Abs(EllipsoidRational value) => new(BigInteger.Abs(value.Numerator), value.Denominator);
    private static EllipsoidRational Min(EllipsoidRational a, EllipsoidRational b) => a.Compare(b) <= 0 ? a : b;
    private static EllipsoidRational Max(EllipsoidRational a, EllipsoidRational b) => a.Compare(b) >= 0 ? a : b;
    private sealed class RationalComparer : IComparer<EllipsoidRational>
    {
        public static RationalComparer Instance { get; } = new();
        public int Compare(EllipsoidRational x, EllipsoidRational y) => x.Compare(y);
    }
}
