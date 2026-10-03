using System.Numerics;
using S2ModKit.Geometry;

namespace S2ModKit.Geometry.Tests;

public sealed class CullingEnvelopeOracleTests
{
    // Independent exact oracle: scaling a binary32 through binary64 by a power of two is exact.
    // This intentionally does not reproduce production's IEEE bit decoder or call its helpers.
    private static BigInteger Q(float value) => new(Math.ScaleB((double)value, 149));
    private static float Axis(Point3 point, int axis) => axis == 0 ? point.X : axis == 1 ? point.Y : point.Z;

    [Fact]
    public void TenThousandSeededCasesPassExactOraclesAndIndependentVerifier()
    {
        var random = new Random(401);
        for (var iteration = 0; iteration < 10000; iteration++)
        {
            var scale = MathF.ScaleB(1, random.Next(-140, 121));
            float Next() => (float)((random.NextDouble() * 4 - 2) * scale);
            Point3 NextPoint() => new(Next(), Next(), Next());
            var points = Enumerable.Range(0, iteration % 9).Select(_ => NextPoint()).ToArray();
            var original = new CenterHalfExtentBounds(NextPoint(), new(scale, scale, scale));
            var output = CullingEnvelope.Expand(original, points).Bounds;
            CheckBox(original, points, output);
            Assert.True(CullingEnvelopeVerifier.Verify(original, points, output).Passed);
            Assert.Equal(output, CullingEnvelope.Expand(original, points.Reverse().ToArray()).Bounds);

            var minMax = new Bounds3(new(-scale, -scale, -scale), new(scale, scale, scale));
            var minMaxOutput = CullingEnvelope.Expand(minMax, points).Bounds;
            for (var axis = 0; axis < 3; axis++)
            {
                Assert.Equal(points.Select(p => Q(Axis(p, axis))).Append(Q(-scale)).Min(), Q(Axis(minMaxOutput.Min, axis)));
                Assert.Equal(points.Select(p => Q(Axis(p, axis))).Append(Q(scale)).Max(), Q(Axis(minMaxOutput.Max, axis)));
            }

            Assert.True(CullingEnvelopeVerifier.Verify(minMax, points, minMaxOutput).Passed);
            var sphere = new FixedCenterSphere(NextPoint(), scale);
            var sphereOutput = CullingEnvelope.Expand(sphere, points).Bounds;
            CheckSphere(sphere, points, sphereOutput);
            Assert.True(CullingEnvelopeVerifier.Verify(sphere, points, sphereOutput).Passed);
        }
    }

    [Fact]
    public void DisparateExponentsDoNotLoseSmallContributorsOrAuthoredExtents()
    {
        var random = new Random(280029);
        float Positive() => MathF.ScaleB((float)(1 + random.NextDouble()), random.Next(-145, 121));
        float Signed() => random.Next(2) == 0 ? Positive() : -Positive();
        Point3 Point() => new(Signed(), Signed(), Signed());
        for (var i = 0; i < 2000; i++)
        {
            var original = new CenterHalfExtentBounds(Point(), new(Positive(), Positive(), Positive()));
            Point3[] points = [Point(), Point(), Point()];
            var output = CullingEnvelope.Expand(original, points).Bounds;
            CheckBox(original, points, output);
            Assert.True(CullingEnvelopeVerifier.Verify(original, points, output).Passed);
            var sphere = new FixedCenterSphere(Point(), Positive());
            var sphereOutput = CullingEnvelope.Expand(sphere, points).Bounds;
            CheckSphere(sphere, points, sphereOutput);
            Assert.True(CullingEnvelopeVerifier.Verify(sphere, points, sphereOutput).Passed);
        }
    }

    [Fact]
    public void FloatEdgesCancellationAndAsymmetricRawBoxesPassExactOracle()
    {
        foreach (var center in new[] { 0f, -0f, float.Epsilon, -float.Epsilon, 1f, -1f, 1e15f, -1e15f, 1e30f, -1e30f })
        {
            var source = new CenterHalfExtentBounds(new(center, center, center), new(float.Epsilon, float.Epsilon, float.Epsilon));
            Point3[] points = [new(MathF.BitDecrement(center), center, center), new(MathF.BitIncrement(center), center, center)];
            var output = CullingEnvelope.Expand(source, points).Bounds;
            CheckBox(source, points, output);
            Assert.True(CullingEnvelopeVerifier.Verify(source, points, output).Passed);
        }

        var cancel = new CenterHalfExtentBounds(new(1, 0, 0), new(1, 1, 1));
        Point3[] cancellationPoints = [new(-float.Epsilon, 0, 0)];
        var expanded = CullingEnvelope.Expand(cancel, cancellationPoints).Bounds;
        CheckBox(cancel, cancellationPoints, expanded);
        Assert.Equal(MathF.BitIncrement(1), expanded.HalfExtents.X);
        Assert.True(CullingEnvelopeVerifier.Verify(cancel, cancellationPoints, expanded).Passed);
        var extreme = new CenterHalfExtentBounds(default, new(float.MaxValue, float.MaxValue, float.MaxValue));
        Assert.False(CullingEnvelope.Expand(extreme, [new(float.MaxValue, 0, 0)]).Changed);
    }

    private static void CheckBox(CenterHalfExtentBounds before, Point3[] points, CenterHalfExtentBounds after)
    {
        for (var axis = 0; axis < 3; axis++)
        {
            var c0 = Axis(before.Center, axis);
            var h0 = Axis(before.HalfExtents, axis);
            var c = Axis(after.Center, axis);
            var h = Axis(after.HalfExtents, axis);
            Assert.True(float.IsFinite(c - h) && float.IsFinite(c + h));
            Assert.True(Q(c) - Q(h) <= Q(c0) - Q(h0));
            Assert.True(Q(c) + Q(h) >= Q(c0) + Q(h0));
            Assert.True(c - h <= c0 - h0 && c + h >= c0 + h0);
            var values = points.Select(p => Q(Axis(p, axis))).ToArray();
            Assert.All(values, value => Assert.True(Fits(c, h, value)));
            if (values.All(value => Fits(c0, h0, value)))
            {
                Assert.Equal(BitConverter.SingleToInt32Bits(c0), BitConverter.SingleToInt32Bits(c));
                Assert.Equal(BitConverter.SingleToInt32Bits(h0), BitConverter.SingleToInt32Bits(h));
                continue;
            }

            var endpoints = values.Concat([Q(c0) - Q(h0), Q(c0) + Q(h0), Q(c0 - h0), Q(c0 + h0)]).ToArray();
            var low = endpoints.Min();
            var high = endpoints.Max();
            var twiceMidpoint = low + high;
            var error = BigInteger.Abs(2 * Q(c) - twiceMidpoint);
            foreach (var neighbor in new[] { MathF.BitDecrement(c), MathF.BitIncrement(c) })
            {
                if (!float.IsFinite(neighbor)) continue;
                var otherError = BigInteger.Abs(2 * Q(neighbor) - twiceMidpoint);
                Assert.True(error <= otherError);
                if (error == otherError) Assert.Equal(0, BitConverter.SingleToInt32Bits(c) & 1);
            }

            Assert.True(Fits(c, h, low) && Fits(c, h, high));
            var previous = MathF.BitDecrement(h);
            Assert.False(Fits(c, previous, low) && Fits(c, previous, high));
        }
    }

    private static bool Fits(float center, float half, BigInteger value) =>
        Q(center) - Q(half) <= value && Q(center) + Q(half) >= value
        && Q(center - half) <= value && Q(center + half) >= value;

    private static void CheckSphere(FixedCenterSphere before, Point3[] points, FixedCenterSphere after)
    {
        var required = BigInteger.Pow(Q(before.Radius), 2);
        foreach (var point in points)
        {
            var distance = Enumerable.Range(0, 3).Select(axis => BigInteger.Pow(Q(Axis(point, axis)) - Q(Axis(before.Center, axis)), 2)).Aggregate(BigInteger.Zero, (sum, term) => sum + term);
            required = BigInteger.Max(required, distance);
        }

        Assert.Equal(before.Center, after.Center);
        Assert.True(BigInteger.Pow(Q(after.Radius), 2) >= required);
        Assert.True(BigInteger.Pow(Q(MathF.BitDecrement(after.Radius)), 2) < required);
    }
}
