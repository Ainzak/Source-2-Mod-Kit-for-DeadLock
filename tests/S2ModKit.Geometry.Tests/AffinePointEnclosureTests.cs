using System.Numerics;
using S2ModKit.Geometry;

namespace S2ModKit.Geometry.Tests;

public sealed class AffinePointEnclosureTests
{
    private static readonly float[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];

    [Fact]
    public void IdentityAndExactlyRepresentableTransformsDoNotInflate()
    {
        foreach (var value in new[] { 0f, -0f, float.Epsilon, -float.Epsilon, 1, -1, float.MaxValue, -float.MaxValue })
        {
            var point = new Point3(value, value, value);
            var result = AffinePointEnclosure.Enclose(point, Identity);
            Assert.Equal(point, result.Min);
            Assert.Equal(point, result.Max);
            CheckExact(point, Identity, result);
        }

        float[] matrix = [0, -1, 0, 4, 1, 0, 0, -8, 0, 0, 2, 16];
        var transformed = AffinePointEnclosure.Enclose(new(2, 3, 5), matrix);
        Assert.Equal(new Point3(1, -6, 26), transformed.Min);
        Assert.Equal(transformed.Min, transformed.Max);
    }

    [Fact]
    public void TenThousandDisparateExponentCasesContainAnIndependentExactOracle()
    {
        var random = new Random(300028);
        float Next() => MathF.ScaleB((float)(random.NextDouble() * 4 - 2), random.Next(-145, 61));
        for (var iteration = 0; iteration < 10000; iteration++)
        {
            var point = new Point3(Next(), Next(), Next());
            var matrix = Enumerable.Range(0, 12).Select(_ => Next()).ToArray();
            var result = AffinePointEnclosure.Enclose(point, matrix);
            CheckExact(point, matrix, result);
            var repeated = AffinePointEnclosure.Enclose(point, matrix);
            Assert.Equal(Words(result), Words(repeated));
        }
    }

    [Fact]
    public void LostSmallTermsCancellationAdjacentFloatsAndSubnormalProductsStayContained()
    {
        float[][] matrices =
        [
            [1, 1, -1, 0, 0, 1, 0, float.Epsilon, 0, 0, 1, -float.Epsilon],
            [float.Epsilon, 0, 0, 0, 0, -float.Epsilon, 0, 0, 0, 0, float.Epsilon, 0],
            [MathF.BitIncrement(1), 1, -1, 0, 0, 1, 0, 1, 0, 0, 1, -1],
            [1e15f, 1, -1e15f, 0, 0, 1, 0, 1e15f, 0, 0, 1, -1e15f],
        ];
        Point3[] points = [new(1e30f, float.Epsilon, 1e30f), new(1, 1, 1), new(float.Epsilon, -float.Epsilon, float.Epsilon)];
        // The last large-product cancellation may produce a finite interval too wide for binary32.
        // Keep that explicit rejection test separate rather than weakening the oracle checks here.
        foreach (var matrix in matrices.Take(3))
        {
            foreach (var point in points)
            {
                CheckExact(point, matrix, AffinePointEnclosure.Enclose(point, matrix));
            }
        }

        var adjacent = AffinePointEnclosure.Enclose(new(1, 1, 1), matrices[2]);
        Assert.Equal(MathF.BitIncrement(1), adjacent.Min.X);
        Assert.Equal(adjacent.Min.X, adjacent.Max.X);
        var tiny = AffinePointEnclosure.Enclose(new(float.Epsilon, -float.Epsilon, float.Epsilon), matrices[1]);
        Assert.Equal(0f, tiny.Min.X);
        Assert.Equal(float.Epsilon, tiny.Max.X);
        CheckExact(new(1, 1, 1), matrices[3], AffinePointEnclosure.Enclose(new(1, 1, 1), matrices[3]));
    }

    [Fact]
    public void EnclosureCornersFeedRetainExpandWithoutLosingAuthoredPaddingOrContributors()
    {
        var original = new CenterHalfExtentBounds(default, new(2, 2, 2));
        float[] matrix = [1, 0, 0, 1, 0, 1, 0, 0, 0, 0, 1, 0];
        Point3[] contributors = [new(-1, -1, -1), new(3, 1, 1), new(0, 0, 0)];
        var enclosures = contributors.Select(point => AffinePointEnclosure.Enclose(point, matrix)).ToArray();
        var corners = enclosures.SelectMany(bounds => new[] { bounds.Min, bounds.Max }).ToArray();
        var output = CullingEnvelope.Expand(original, corners);
        Assert.True(output.Changed);
        Assert.True(CullingEnvelopeVerifier.Verify(original, corners, output.Bounds).Passed);
        for (var index = 0; index < contributors.Length; index++) CheckExact(contributors[index], matrix, enclosures[index]);
        Assert.Equal(1f, output.Bounds.Center.X);
        Assert.Equal(3f, output.Bounds.HalfExtents.X);
        Assert.Equal(2f, output.Bounds.HalfExtents.Y);
        Assert.Equal(2f, output.Bounds.HalfExtents.Z);
    }

    [Fact]
    public void UnavailableMalformedAndNonfiniteMatricesReject()
    {
        Assert.Throws<ArgumentNullException>(() => AffinePointEnclosure.Enclose(default, null!));
        Assert.Throws<ArgumentException>(() => AffinePointEnclosure.Enclose(default, new float[11]));
        Assert.Throws<ArgumentException>(() => AffinePointEnclosure.Enclose(default, new float[13]));
        foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            for (var index = 0; index < 12; index++)
            {
                var matrix = Identity.ToArray();
                matrix[index] = value;
                Assert.Throws<ArgumentException>(() => AffinePointEnclosure.Enclose(default, matrix));
            }
        }
    }

    [Fact]
    public void OverflowAndUnrepresentableCancellationIntervalsReject()
    {
        float[] overflow = [2, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];
        Assert.Throws<ArgumentException>(() => AffinePointEnclosure.Enclose(new(float.MaxValue, 0, 0), overflow));
        Assert.Throws<ArgumentException>(() => AffinePointEnclosure.Enclose(new(-float.MaxValue, 0, 0), overflow));
        float[] cancellation = [1e30f, 1, -1e30f, 0, 0, 1, 0, 0, 0, 0, 1, 0];
        Assert.Throws<ArgumentException>(() => AffinePointEnclosure.Enclose(new(1e30f, 1, 1e30f), cancellation));
    }

    // Independent dyadic oracle: binary32 values are integer multiples of 2^-149.
    // Products and the offset are compared in units of 2^-298, with no production interval helpers.
    private static BigInteger Q(float value) => new(Math.ScaleB((double)value, 149));

    private static void CheckExact(Point3 point, float[] matrix, Bounds3 result)
    {
        for (var axis = 0; axis < 3; axis++)
        {
            var row = axis * 4;
            var exact = Q(matrix[row]) * Q(point.X) + Q(matrix[row + 1]) * Q(point.Y)
                + Q(matrix[row + 2]) * Q(point.Z) + (Q(matrix[row + 3]) << 149);
            var minimum = Q(Axis(result.Min, axis)) << 149;
            var maximum = Q(Axis(result.Max, axis)) << 149;
            Assert.True(minimum <= exact && exact <= maximum, $"Axis {axis}: exact affine coordinate escaped.");
        }
    }

    private static float Axis(Point3 point, int axis) => axis == 0 ? point.X : axis == 1 ? point.Y : point.Z;

    private static int[] Words(Bounds3 result) => new[] { result.Min.X, result.Min.Y, result.Min.Z, result.Max.X, result.Max.Y, result.Max.Z }
        .Select(BitConverter.SingleToInt32Bits).ToArray();
}
