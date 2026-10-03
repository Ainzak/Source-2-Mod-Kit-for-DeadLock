using System.Numerics;

namespace S2ModKit.Geometry.Tests;

public sealed class AxisRampScaleTests
{
    private static readonly TangentFrame Frame = new(new(0, 0, 1), new(1, 0, 0), -1);

    [Fact]
    public void PinnedWordsAndFullScaleRetainEstablishedMeaning()
    {
        var edit = new AxisRampScale(2, 0, 8, 1.5f, new(0, 0, 8));
        var pinned = new Point3(-0f, float.Epsilon, -0f);
        var result = edit.Evaluate(pinned);
        Assert.Equal(BitConverter.SingleToInt32Bits(pinned.X), BitConverter.SingleToInt32Bits(result.Position.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(pinned.Z), BitConverter.SingleToInt32Bits(result.Position.Z));
        Assert.Equal(0, result.Weight);
        Assert.Equal(0, result.Displacement);
        Assert.Same(Frame, edit.TransformFrame(pinned, Frame));
        var full = new Point3(3, 4, 10);
        Assert.Equal(new UniformTransform(edit.Pivot, 1.5f, default).Apply(full), edit.Evaluate(full).Position);
        Assert.Equal(1, edit.Evaluate(full).Weight);
        Assert.Same(Frame, edit.TransformFrame(full, Frame));
    }

    [Fact]
    public void MidpointMaskIsHalfAndTransitionUpdatesFrame()
    {
        var edit = new AxisRampScale(2, 0, 8, 1.5f, new(0, 0, 8));
        var p = new Point3(4, 0, 4);
        Assert.Equal(new Point3(5, 0, 3), edit.Evaluate(p).Position);
        Assert.Equal(0.5, edit.Evaluate(p).Weight);
        var frame = edit.TransformFrame(p, new(new(1, 0, 0), new(0, 0, 1), 1));
        Assert.True(frame.Normal.Z < 0);
        Assert.Equal(1, frame.Handedness);
        Assert.InRange(Math.Abs(Dot(frame.Normal, frame.Tangent)), 0, 1e-6);
    }

    [Fact]
    public void InvalidThresholdsScalesAxesAndGlobalFoldsReject()
    {
        Assert.Throws<ArgumentException>(() => new AxisRampScale(3, 0, 8, 1.5f, default));
        Assert.Throws<ArgumentException>(() => new AxisRampScale(2, 8, 8, 1.5f, default));
        Assert.Throws<ArgumentException>(() => new AxisRampScale(2, float.NaN, 8, 1.5f, default));
        Assert.Throws<ArgumentException>(() => new AxisRampScale(2, 0, float.PositiveInfinity, 1.5f, default));
        Assert.Throws<ArgumentException>(() => new AxisRampScale(2, 0, 8, 1, default));
        Assert.Throws<ArgumentException>(() => new AxisRampScale(2, 0, 8, 2.01f, default));
        Assert.Throws<ArgumentException>(() => new AxisRampScale(2, 0, 1, 2, new(0, 0, 100)));
        Assert.Throws<ArgumentException>(() => new AxisRampScale(2, 0, 8, 0.5f, new(0, 0, -100)));
    }

    [Fact]
    public void InvalidFramesAndOutputOverflowReject()
    {
        var edit = new AxisRampScale(2, 0, 8, 2, default);
        Assert.Throws<ArgumentException>(() => edit.TransformFrame(new(0, 0, 4), Frame with { Handedness = 0 }));
        Assert.Throws<ArgumentException>(() => edit.TransformFrame(new(0, 0, 4), Frame with { Normal = default }));
        Assert.Throws<ArgumentException>(() => edit.TransformFrame(new(0, 0, 4), Frame with { Tangent = Frame.Normal }));
        Assert.Throws<ArgumentException>(() => edit.Evaluate(new(float.MaxValue, 0, 8)));
    }

    [Fact]
    public void AdjacentThresholdsSubnormalsAndLargeCoordinatesStayDeterministic()
    {
        var edit = new AxisRampScale(0, 0, float.Epsilon * 8, 1.5f, default);
        foreach (var p in new[] { new Point3(-0f, 1, -0f), new Point3(float.Epsilon * 4, float.Epsilon, 0), new Point3(float.Epsilon * 8, 2, 0) })
            Assert.Equal(edit.Evaluate(p), edit.Evaluate(p));
        var origin = 1e20f;
        var upper = MathF.BitIncrement(MathF.BitIncrement(origin));
        var large = new AxisRampScale(2, origin, upper, 1.5f, new(0, 0, upper));
        Assert.Equal(0, large.Evaluate(new(1, 2, origin)).Displacement);
        Assert.Equal(0.5, large.Evaluate(new(1, 2, MathF.BitIncrement(origin))).Weight);
    }

    [Fact]
    public void TenThousandCasesAgreeWithIndependentExactRationalPositionOracle()
    {
        var random = new Random(031015);
        for (var i = 0; i < 10_000; i++)
        {
            int axis = i % 3;
            var scale = i % 2 == 0 ? 1.5f : 0.75f;
            var pivot = new Point3(0, 0, 0);
            var edit = new AxisRampScale(axis, 0, 8, scale, pivot);
            float[] xyz = [random.Next(-2048, 2048) / 16f, random.Next(-2048, 2048) / 16f, random.Next(-2048, 2048) / 16f];
            xyz[axis] = random.Next(-16, 145) / 16f;
            var result = edit.Evaluate(new(xyz[0], xyz[1], xyz[2]));
            // Exact integer polynomial with denominator 128^3; no production mask or
            // float/decimal conversion helpers. Independently choose nearest-even float.
            var k = new BigInteger(Math.Clamp((int)(xyz[axis] * 16), 0, 128));
            var denominator = new BigInteger(128 * 128 * 128);
            var maskNumerator = (3 * k * k * 128) - (2 * k * k * k);
            var scaleDenominator = scale == 1.5f ? 2 : 4;
            var numerator = (scaleDenominator * denominator) + (scale == 1.5f ? maskNumerator : -maskNumerator);
            float[] observed = [result.Position.X, result.Position.Y, result.Position.Z];
            for (var component = 0; component < 3; component++)
                Assert.Equal(NearestEven(new BigInteger(xyz[component] * 16) * numerator, 16 * scaleDenominator * denominator), observed[component]);
            Assert.Equal(result, edit.Evaluate(new(xyz[0], xyz[1], xyz[2])));
        }
    }

    [Fact]
    public void IndependentFiniteDifferenceJacobianAgreesWithNormalAndTangent()
    {
        var edit = new AxisRampScale(2, 0, 8, 1.5f, new(0, 0, 8));
        var source = new Point3(4, 2, 4);
        var input = new TangentFrame(new(1, 0, 0), new(0, 0, 1), -1);
        var output = edit.TransformFrame(source, input);
        // Two source surface directions span the plane orthogonal to the source normal.
        // Use an independent polynomial map in double, not Evaluate or its Jacobian.
        static double[] Map(double x, double y, double z)
        {
            var t = z / 8;
            var a = 1 + (0.5 * ((3 * t * t) - (2 * t * t * t)));
            return [a * x, a * y, 8 + (a * (z - 8))];
        }
        const double h = 1e-4;
        var plus = Map(source.X, source.Y, source.Z + h);
        var minus = Map(source.X, source.Y, source.Z - h);
        var derivative = plus.Zip(minus, (a, b) => (a - b) / (2 * h)).ToArray();
        var normalDot = ((derivative[0] * output.Normal.X) + (derivative[1] * output.Normal.Y)) + (derivative[2] * output.Normal.Z);
        Assert.InRange(Math.Abs(normalDot), 0, 1e-6);
        var length = Math.Sqrt(derivative.Sum(v => v * v));
        Assert.InRange(((derivative[0] * output.Tangent.X) + (derivative[1] * output.Tangent.Y) + (derivative[2] * output.Tangent.Z)) / length, 0.999999, 1.000001);
    }

    private static double Dot(Point3 a, Point3 b) => (((double)a.X * b.X) + ((double)a.Y * b.Y)) + ((double)a.Z * b.Z);

    private static float NearestEven(BigInteger numerator, BigInteger denominator)
    {
        var approximate = (float)((double)numerator / (double)denominator);
        var target = numerator << 149;
        BigInteger Distance(float f) => BigInteger.Abs((new BigInteger(Math.ScaleB((double)f, 149)) * denominator) - target);
        return new[] { MathF.BitDecrement(approximate), approximate, MathF.BitIncrement(approximate) }
            .OrderBy(Distance).ThenBy(f => BitConverter.SingleToUInt32Bits(f) & 1u).First();
    }
}
