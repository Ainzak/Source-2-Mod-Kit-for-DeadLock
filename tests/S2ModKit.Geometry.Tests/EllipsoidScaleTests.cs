namespace S2ModKit.Geometry.Tests;

public sealed class EllipsoidScaleTests
{
    private static readonly float[] Scales = [0.5f, 1.25f, 1.5f, 2f];
    [Fact]
    public void TenThousandCasesMatchIndependentDecimalPositionAndMatrixOracle()
    {
        // The oracle uses 28-digit arithmetic, Newton square roots and a full 3x3
        // cofactor inverse. It shares neither classification nor rank-one frame code.
        var random = new Random(32107);
        var center = new Point3(64, -32, 16);
        var radii = new Point3(8, 4, 2);
        var fields = Scales.Select(s => new EllipsoidScale(center, radii, 1f / 16, s, 64)).ToArray();
        var transitionCount = 0;
        for (var i = 0; i < 10_000; i++)
        {
            var p = new Point3(center.X + random.Next(-1536, 1537) / 128f,
                center.Y + random.Next(-1536, 1537) / 256f, center.Z + random.Next(-1536, 1537) / 512f);
            var field = fields[i % fields.Length];
            var n = Unit([random.Next(-100, 101), random.Next(-100, 101), 101])!;
            var t = Unit([n[1], -n[0], 0]);
            // The fixed positive Z above prevents a zero normal; reject the one
            // zero tangent draw explicitly, without changing the deterministic seed.
            if (t is null) t = [1, 0, 0];
            var frame = new TangentFrame(Point(n), Point(t), i % 2 == 0 ? 1 : -1);
            var expected = Oracle(p, frame, center, radii, field.CoreFraction, field.Scale);
            var actual = field.Evaluate(p);
            Assert.Equal(expected.Membership, actual.Membership);
            AssertNear(expected.Position, actual.Position);
            var transformed = field.TransformFrame(p, frame);
            Assert.True(Dot(expected.Normal, transformed.Normal) >= 0.9999998m);
            Assert.True(Dot(expected.Tangent, transformed.Tangent) >= 0.9999998m);
            Assert.Equal(frame.Handedness, transformed.Handedness);
            if (actual.Membership == EllipsoidMembership.Transition) transitionCount++;
            else Assert.Same(frame, transformed);
        }
        Assert.True(transitionCount > 1000);
    }

    [Fact]
    public void EndpointsAdjacentFloatsAndSignedZeroHaveExactMembership()
    {
        var field = new EllipsoidScale(default, new(8, 4, 2), 1f / 16, 2, 64);
        Assert.Equal(EllipsoidMembership.Core, field.Evaluate(new(0.5f, 0, 0)).Membership);
        Assert.Equal(EllipsoidMembership.Core, field.Evaluate(new(float.BitDecrement(0.5f), 0, 0)).Membership);
        Assert.Equal(EllipsoidMembership.Transition, field.Evaluate(new(float.BitIncrement(0.5f), 0, 0)).Membership);
        // Exact Q is above h*h although correctly rounded rho equals h and w=1.
        var roundedCore = field.Evaluate(new(0.5f, float.Epsilon, 0));
        Assert.Equal(EllipsoidMembership.Transition, roundedCore.Membership);
        Assert.Equal(1, roundedCore.Weight);
        Assert.Equal(EllipsoidMembership.Transition, field.Evaluate(new(float.BitDecrement(8), 0, 0)).Membership);
        var negativeZero = BitConverter.UInt32BitsToSingle(0x80000000);
        foreach (var x in new[] { 8f, float.BitIncrement(8) })
        {
            var result = field.Evaluate(new(x, negativeZero, 0));
            Assert.Equal(EllipsoidMembership.Pinned, result.Membership);
            Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(result.Position.Y));
        }
        // Exactly on the non-axis ellipsoid, using the integer 3-4-5 identity.
        Assert.Equal(EllipsoidMembership.Pinned, new EllipsoidScale(default, new(5, 5, 5), 1f / 16, 2, 64).Evaluate(new(3, 4, 0)).Membership);
    }

    [Fact]
    public void SubnormalRadiiLargeOriginsAndStoredDisplacementAreHandled()
    {
        var tiny = new EllipsoidScale(default, new(float.Epsilon * 8, float.Epsilon * 8, float.Epsilon * 8), 1f / 16, 2, 64);
        Assert.Equal(EllipsoidMembership.Pinned, tiny.Evaluate(new(float.Epsilon * 8, 0, 0)).Membership);
        Assert.Equal(EllipsoidMembership.Transition, tiny.Evaluate(new(float.Epsilon, 0, 0)).Membership);
        var origin = new Point3(1 << 24, -(1 << 24), 1 << 24);
        var large = new EllipsoidScale(origin, new(32, 32, 32), 1f / 16, 2, 64);
        Assert.Equal(new Point3(origin.X + 4, origin.Y, origin.Z), large.Evaluate(new(origin.X + 2, origin.Y, origin.Z)).Position);
        Assert.Throws<ArgumentException>(() => new EllipsoidScale(default, new(100, 100, 100), 1f / 16, 2, 1).Evaluate(new(6, 0, 0)));
        var overflow = new EllipsoidScale(new(float.BitDecrement(float.MaxValue), 0, 0), new(float.MaxValue, float.MaxValue, float.MaxValue), 1f / 16, 2, 64);
        Assert.Throws<ArgumentException>(() => overflow.Evaluate(new(float.MaxValue, 0, 0)));
    }

    [Fact]
    public void CertificateBoundsIndependentContinuousMinimumAndRejectsFolding()
    {
        var certificate = EllipsoidJacobianCertificate.Create(1f / 16, 2, new(8, 4, 1));
        var lower = (decimal)certificate.Numerator / (decimal)certificate.Denominator;
        Assert.InRange(lower, 0.23642628m, 0.23642630m);
        // Minimum of b(t)=s+delta*(8t^3+(6h/v-9)t^2-6h/v*t).
        var h = 1m / 16; var k = h / (1 - h);
        var root = (18 - 12 * k + Sqrt((12 * k - 18) * (12 * k - 18) + 576 * k)) / 48;
        var exactMinimum = 2 + (8 * root * root * root) + ((6 * k - 9) * root * root) - (6 * k * root);
        Assert.True(lower <= exactMinimum && exactMinimum - lower < 0.0000001m);
        Assert.True(certificate.MinimumSingularValueLowerBound >= 1d / 64);
        Assert.Throws<ArgumentException>(() => EllipsoidJacobianCertificate.Create(0.25f, 2, new(1, 1, 1)));
        Assert.Throws<ArgumentException>(() => EllipsoidJacobianCertificate.Create(float.BitDecrement(1), 2, new(1, 1, 1)));
        Assert.Throws<ArgumentException>(() => EllipsoidJacobianCertificate.Create(0.0625f, 2, new(9, 1, 1)));
        Assert.Throws<ArgumentException>(() => EllipsoidJacobianCertificate.Create(0, 2, new(1, 1, 1)));
        Assert.Throws<ArgumentException>(() => EllipsoidJacobianCertificate.Create(0.0625f, 1, new(1, 1, 1)));
        Assert.Throws<ArgumentException>(() => EllipsoidJacobianCertificate.Create(0.0625f, 2, default));
    }

    [Fact]
    public void InvalidFramesRejectEvenInCoreAndPinnedRegions()
    {
        var field = new EllipsoidScale(default, new(8, 8, 8), 1f / 16, 2, 64);
        foreach (var point in new Point3[] { default, new(4, 1, 0), new(8, 0, 0) })
            foreach (var frame in new TangentFrame[] { new(default, new(1, 0, 0), 1), new(new(1, 0, 0), new(1, 0, 0), 1), new(new(0, 0, 1), new(1, 0, 0), 0) })
                Assert.Throws<ArgumentException>(() => field.TransformFrame(point, frame));
    }

    [Fact]
    public void NarrowAdmittedContractionStillMatchesTheIndependentFrameOracle()
    {
        var field = new EllipsoidScale(default, new(1, 1, 1), float.BitDecrement(1), 0.5f, 64);
        var point = new Point3(float.BitDecrement(1), 1f / 8192, 0);
        var frame = new TangentFrame(new(0, 0, 1), new(1, 0, 0), -1);
        var expected = Oracle(point, frame, default, field.OuterRadii, field.CoreFraction, field.Scale);
        var actual = field.Evaluate(point);
        Assert.Equal(EllipsoidMembership.Transition, actual.Membership);
        AssertNear(expected.Position, actual.Position);
        var output = field.TransformFrame(point, frame);
        Assert.True(Dot(expected.Normal, output.Normal) >= 0.9999998m);
        Assert.True(Dot(expected.Tangent, output.Tangent) >= 0.9999998m);
    }

    [Fact]
    public void QuantizedTriangleGuardRemainsIndependentOfContinuousCertificate()
    {
        var field = new EllipsoidScale(default, new(8, 8, 8), 1f / 16, 2, 64);
        Point3[] source = [default, new(0.25f, 0, 0), new(0, 0.25f, 0)];
        var output = source.Select(p => field.Evaluate(p).Position).ToArray();
        RegionTriangleGuard.Validate(source, output, [0, 1, 2]);
        Assert.Throws<ArgumentException>(() => RegionTriangleGuard.Validate(source, [output[0], output[1], output[1]], [0, 1, 2]));
        Assert.Throws<ArgumentException>(() => RegionTriangleGuard.Validate(source, [output[0], output[2], output[1]], [0, 1, 2]));
    }

    private static (EllipsoidMembership Membership, decimal[] Position, decimal[] Normal, decimal[] Tangent) Oracle(
        Point3 point, TangentFrame frame, Point3 center, Point3 radii, float core, float scale)
    {
        var p = Values(point); var c = Values(center); var r = Values(radii);
        var d = Enumerable.Range(0, 3).Select(i => p[i] - c[i]).ToArray();
        var q = Enumerable.Range(0, 3).Sum(i => (d[i] / r[i]) * (d[i] / r[i]));
        var h = Exact(core); var s = Exact(scale);
        if (q <= h * h) return (EllipsoidMembership.Core, Enumerable.Range(0, 3).Select(i => c[i] + s * d[i]).ToArray(), Values(frame.Normal), Values(frame.Tangent));
        if (q >= 1) return (EllipsoidMembership.Pinned, p, Values(frame.Normal), Values(frame.Tangent));
        var rho = Sqrt(q); var t = (rho - h) / (1 - h);
        var w = 1 - 3 * t * t + 2 * t * t * t;
        var a = 1 + (s - 1) * w;
        var g = Enumerable.Range(0, 3).Select(i => -6 * t * (1 - t) / (1 - h) * d[i] / (r[i] * r[i]) / rho).ToArray();
        var j = new decimal[3, 3];
        for (var i = 0; i < 3; i++) for (var k = 0; k < 3; k++) j[i, k] = (i == k ? a : 0) + (s - 1) * d[i] * g[k];
        var cof = new decimal[3, 3];
        for (var i = 0; i < 3; i++) for (var k = 0; k < 3; k++)
            cof[i, k] = j[(i + 1) % 3, (k + 1) % 3] * j[(i + 2) % 3, (k + 2) % 3] - j[(i + 1) % 3, (k + 2) % 3] * j[(i + 2) % 3, (k + 1) % 3];
        var determinant = Enumerable.Range(0, 3).Sum(k => j[0, k] * cof[0, k]);
        var inputN = Values(frame.Normal); var inputT = Values(frame.Tangent);
        var n = Unit(Enumerable.Range(0, 3).Select(i => Enumerable.Range(0, 3).Sum(k => cof[i, k] * inputN[k]) / determinant).ToArray())!;
        var z = Enumerable.Range(0, 3).Select(i => Enumerable.Range(0, 3).Sum(k => j[i, k] * inputT[k])).ToArray();
        var projection = Enumerable.Range(0, 3).Sum(i => z[i] * n[i]);
        var tangent = Unit(Enumerable.Range(0, 3).Select(i => z[i] - n[i] * projection).ToArray())!;
        return (EllipsoidMembership.Transition, Enumerable.Range(0, 3).Select(i => c[i] + a * d[i]).ToArray(), n, tangent);
    }

    private static decimal Sqrt(decimal value)
    {
        if (value == 0) return 0;
        var x = value > 1 ? value : 1;
        for (var i = 0; i < 100; i++) { var next = (x + value / x) / 2; if (Math.Abs(next - x) <= 0.000000000000000000000000001m) return next; x = next; }
        return x;
    }
    private static decimal Exact(float value)
    {
        var bits = BitConverter.SingleToUInt32Bits(value);
        var exponent = (int)((bits >> 23) & 255) - 150;
        decimal mantissa = (bits & 0x7fffff) | 0x800000;
        if ((bits & 0x7fffffff) == 0) return 0;
        while (exponent < 0) { mantissa /= 2; exponent++; }
        while (exponent > 0) { mantissa *= 2; exponent--; }
        return bits >> 31 == 0 ? mantissa : -mantissa;
    }
    private static decimal[] Values(Point3 p) => [Exact(p.X), Exact(p.Y), Exact(p.Z)];
    private static Point3 Point(decimal[] p) => new((float)p[0], (float)p[1], (float)p[2]);
    private static decimal[]? Unit(decimal[] p) { var length = Sqrt(p.Sum(v => v * v)); return length == 0 ? null : p.Select(v => v / length).ToArray(); }
    private static decimal Dot(decimal[] p, Point3 q) { var v = Values(q); return Enumerable.Range(0, 3).Sum(i => p[i] * v[i]); }
    private static void AssertNear(decimal[] expected, Point3 actual)
    {
        var values = new[] { actual.X, actual.Y, actual.Z };
        for (var i = 0; i < 3; i++)
        {
            var rounded = (float)expected[i];
            // Ideal high-precision arithmetic and the frozen binary64 sequence may
            // differ at a float midpoint; permit only its two adjacent float words.
            Assert.InRange(values[i], float.BitDecrement(rounded), float.BitIncrement(rounded));
        }
    }
}
