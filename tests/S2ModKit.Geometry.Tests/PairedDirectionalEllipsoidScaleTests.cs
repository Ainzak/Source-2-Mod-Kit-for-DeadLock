namespace S2ModKit.Geometry.Tests;

public sealed class PairedDirectionalEllipsoidScaleTests
{
    [Fact]
    public void TenThousandNewDispatchCasesMatchIndependentRationalAndFullMatrixOracles()
    {
        var pairs = Pairs();
        var reversed = pairs.Select(p => new PairedDirectionalEllipsoidScale(p.Second, p.First)).ToArray();
        var readers = pairs.Select(p => new PairedDirectionalFieldReconstruction(Parameters(p.First), Parameters(p.Second))).ToArray();
        var random = new Random(35005);
        var counts = new int[3]; var partners = new int[2];
        for (var i = 0; i < 10_000; i++)
        {
            var pair = pairs[i % pairs.Length];
            var chosen = i % 2 == 0 ? pair.First : pair.Second;
            var point = Sample(chosen, random, i % 5);
            var normal = DirectionalEllipsoidOracle.Unit([random.Next(-100, 101), random.Next(-100, 101), 101]);
            var tangent = normal[0] == 0 && normal[1] == 0 ? new decimal[] { 1, 0, 0 }
                : DirectionalEllipsoidOracle.Unit([normal[1], -normal[0], 0]);
            var frame = new TangentFrame(Point(normal), Point(tangent), i % 2 == 0 ? 1 : -1);
            var a = DirectionalEllipsoidOracle.Evaluate(pair.First, point, frame);
            var b = DirectionalEllipsoidOracle.Evaluate(pair.Second, point, frame);
            Assert.False(a.Membership != EllipsoidMembership.Pinned && b.Membership != EllipsoidMembership.Pinned);
            var expected = a.Membership != EllipsoidMembership.Pinned ? a : b;
            int? active = a.Membership != EllipsoidMembership.Pinned ? 0 : b.Membership != EllipsoidMembership.Pinned ? 1 : null;
            var actual = pair.Evaluate(point);
            var reordered = reversed[i % pairs.Length].Evaluate(point);
            var reconstructed = readers[i % pairs.Length].ReconstructPosition(point);
            Assert.Equal(active, actual.ActiveFieldIndex);
            Assert.Equal(active, reconstructed.ActiveFieldIndex);
            Assert.Equal(active is null ? null : 1 - active, reordered.ActiveFieldIndex);
            Assert.Equal(expected.Membership, actual.Membership);
            Assert.Equal(expected.Membership, reconstructed.Membership);
            Assert.Equal(BitConverter.DoubleToUInt64Bits(expected.Weight), BitConverter.DoubleToUInt64Bits(actual.Weight));
            Assert.Equal(BitConverter.DoubleToUInt64Bits(expected.Weight), BitConverter.DoubleToUInt64Bits(reconstructed.Weight));
            Words(expected.StoredPosition, actual.Position);
            Words(expected.StoredPosition, reordered.Position);
            Words(expected.StoredPosition, reconstructed.Position);
            var selected = active == 1 ? pair.Second : pair.First;
            var expectedFrame = DirectionalEllipsoidOracle.RoundedFrame(selected, point, frame, expected);
            var outputFrame = pair.TransformFrame(point, frame);
            var readFrame = readers[i % pairs.Length].ReconstructFrame(point, frame);
            var reverseFrame = reversed[i % pairs.Length].TransformFrame(point, frame);
            foreach (var observed in new[] { outputFrame, readFrame, reverseFrame })
            {
                Words(expectedFrame.Normal, observed.Normal); Words(expectedFrame.Tangent, observed.Tangent);
                Assert.Equal(frame.Handedness, observed.Handedness);
            }
            var jacobian = pair.Jacobian(point);
            for (var row = 0; row < 3; row++) for (var column = 0; column < 3; column++)
                Assert.InRange((decimal)jacobian[row, column] - expected.Jacobian[row, column], -0.00000000002m, 0.00000000002m);
            counts[(int)actual.Membership]++;
            if (active is not null) partners[active.Value]++;
            else Assert.Same(frame, outputFrame);
        }
        Assert.True(counts[(int)EllipsoidMembership.Core] >= 2000);
        Assert.True(counts[(int)EllipsoidMembership.Transition] >= 2000);
        Assert.True(counts[(int)EllipsoidMembership.Pinned] >= 2000);
        Assert.All(partners, count => Assert.True(count >= 2000));
    }

    [Fact]
    public void TenThousandNewProjectionCasesAgreeWithIndependentExactFractions()
    {
        var random = new Random(35006); var accepted = 0; var rejected = 0;
        for (var i = 0; i < 10_000; i++)
        {
            var unit = (float)Math.ScaleB(1, random.Next(-149, 120));
            var r0 = unit * random.Next(1, 9); var r1 = unit * random.Next(1, 9);
            var c0 = unit * random.Next(-8, 9);
            var c1 = c0 + r0 + r1;
            c1 = i % 3 == 0 ? float.BitDecrement(c1) : i % 3 == 1 ? float.BitIncrement(c1) : c1;
            var centersA = new float[3]; var centersB = new float[3]; var axis = i % 3;
            centersA[axis] = c0; centersB[axis] = c1;
            var before = Vector(centersA); var after = Vector(centersB);
            var ra = new Point3(r0, r0, r0); var rb = new Point3(r1, r1, r1);
            var difference = DirectionalOracleFraction.From(c1) - DirectionalOracleFraction.From(c0);
            var width = DirectionalOracleFraction.From(r0) + DirectionalOracleFraction.From(r1);
            if (difference.Compare(width) < 0)
            {
                Assert.Throws<ArgumentException>(() => DirectionalPairSeparation.Prove(before, ra, after, rb));
                rejected++;
            }
            else
            {
                var proof = DirectionalPairSeparation.Prove(before, ra, after, rb);
                Assert.Equal(axis, proof.Axis);
                Assert.Equal(difference.N, proof.CenterDistanceNumerator); Assert.Equal(difference.D, proof.CenterDistanceDenominator);
                Assert.Equal(width.N, proof.RadiusSumNumerator); Assert.Equal(width.D, proof.RadiusSumDenominator);
                var gap = difference - width;
                Assert.Equal(gap.N, proof.GapNumerator); Assert.Equal(gap.D, proof.GapDenominator);
                Assert.Equal(proof, DirectionalPairSeparation.Prove(after, rb, before, ra));
                accepted++;
            }
        }
        Assert.True(accepted > 4000 && rejected > 2000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TouchingBoundaryAndAdjacentStoredPointsDispatchFromSource(int axis)
    {
        var left = new float[3]; var right = new float[3]; left[axis] = -2; right[axis] = 2;
        var pair = new PairedDirectionalEllipsoidScale(Field(Vector(left), new(2, 2, 2), new(1.25f, 1.125f, .875f)),
            Field(Vector(right), new(2, 2, 2), new(.875f, 1.125f, 1.25f)));
        Assert.Equal(System.Numerics.BigInteger.Zero, pair.Separation.GapNumerator);
        var negativeZero = BitConverter.UInt32BitsToSingle(0x80000000);
        var point = new Point3(negativeZero, negativeZero, negativeZero);
        var result = pair.Evaluate(point);
        Assert.Null(result.ActiveFieldIndex); Words(point, result.Position);
        var below = new float[3]; below[axis] = -float.Epsilon;
        var above = new float[3]; above[axis] = float.Epsilon;
        Assert.Equal(0, pair.Evaluate(Vector(below)).ActiveFieldIndex);
        Assert.Equal(1, pair.Evaluate(Vector(above)).ActiveFieldIndex);
    }

    [Fact]
    public void ExactProjectionDoesNotLoseCancellationSmallRadiiOrExtremeOrigins()
    {
        var tiny = new Point3(float.Epsilon, float.Epsilon, float.Epsilon);
        var proof = DirectionalPairSeparation.Prove(new(float.MaxValue, 0, 0), tiny, new(-float.MaxValue, 0, 0), tiny);
        Assert.Equal(0, proof.Axis); Assert.True(proof.GapNumerator.Sign > 0);
        Assert.Throws<ArgumentException>(() => DirectionalPairSeparation.Prove(new(16777216, 0, 0), new(1, 1, 1),
            new(16777216, 0, 0), tiny));
        Assert.Throws<ArgumentException>(() => DirectionalPairSeparation.Prove(default, new(1, 1, 1), new(2, 0, 0),
            new(float.BitIncrement(1), 1, 1)));
    }

    [Fact]
    public void OverlapAndUnprovedDiagonalSeparationRejectRegardlessOfSampledVertices()
    {
        var first = Field(default, new(1, 1, 1), new(1.25f, 1, 1));
        foreach (var center in new Point3[] { default, new(float.BitDecrement(2), 0, 0), new(1.5f, 1.5f, 0) })
        {
            var second = Field(center, new(1, 1, 1), new(1.125f, 1, 1));
            Assert.Throws<ArgumentException>(() => new PairedDirectionalEllipsoidScale(first, second));
            Assert.Throws<ArgumentException>(() => new PairedDirectionalFieldReconstruction(Parameters(first), Parameters(second)));
        }
    }

    [Fact]
    public void IdentityFoldingPartnerInvalidFrameAndStoredDisplacementCannotBeHidden()
    {
        Assert.Throws<ArgumentException>(() => Field(default, new(1, 1, 1), new(1, 1, 1)));
        Assert.Throws<ArgumentException>(() => new DirectionalEllipsoidScale(default, new(1, 1, 1), .25f, new(2, 1, 1), 64));
        var pair = new PairedDirectionalEllipsoidScale(new(default, new(2, 2, 2), .25f, new(1.25f, 1, 1), .00001f),
            Field(new(4, 0, 0), new(2, 2, 2), new(1.125f, 1, 1)));
        Assert.Throws<ArgumentException>(() => pair.Evaluate(new(.25f, 0, 0)));
        Assert.Throws<ArgumentException>(() => pair.TransformFrame(new(10, 10, 10), new(default, new(1, 0, 0), 1)));
    }

    [Fact]
    public void CoreFixedPositionStillRequiresAnisotropicFrameTransportForBothPartners()
    {
        var pair = Pairs()[0]; var diagonal = (float)(1 / Math.Sqrt(2));
        var frame = new TangentFrame(new(diagonal, 0, diagonal), new(0, 1, 0), -1);
        foreach (var field in new[] { pair.First, pair.Second })
        {
            Words(field.Pivot, pair.Evaluate(field.Pivot).Position);
            var output = pair.TransformFrame(field.Pivot, frame);
            Assert.NotEqual(BitConverter.SingleToUInt32Bits(frame.Normal.X), BitConverter.SingleToUInt32Bits(output.Normal.X));
            Assert.Equal(-1, output.Handedness);
        }
    }

    private static PairedDirectionalEllipsoidScale[] Pairs() => Enumerable.Range(0, 6).Select(i =>
    {
        int[][] orders = [[0, 1, 2], [1, 2, 0], [2, 0, 1], [0, 2, 1], [1, 0, 2], [2, 1, 0]];
        var order = orders[i];
        Point3 Permute(Point3 p) { float[] values = [p.X, p.Y, p.Z]; return new(values[order[0]], values[order[1]], values[order[2]]); }
        return new PairedDirectionalEllipsoidScale(Field(Permute(new(-5, 2, -3)), Permute(new(4, 3, 2)), Permute(new(1.25f, 1.125f, .875f))),
            Field(Permute(new(6, 2, -3)), Permute(new(3, 2, 2)), Permute(new(.75f, .875f, 1))));
    }).ToArray();
    private static DirectionalEllipsoidScale Field(Point3 pivot, Point3 radii, Point3 scale) => new(pivot, radii, .25f, scale, 64);
    private static DirectionalFieldParameters Parameters(DirectionalEllipsoidScale field) =>
        new(field.Pivot, field.OuterRadii, field.CoreFraction, field.Scale, field.DisplacementLimit);
    private static Point3 Sample(DirectionalEllipsoidScale field, Random random, int category)
    {
        float[] center = [field.Pivot.X, field.Pivot.Y, field.Pivot.Z];
        float[] radii = [field.OuterRadii.X, field.OuterRadii.Y, field.OuterRadii.Z];
        var axis = random.Next(3);
        if (category == 0) return field.Pivot;
        if (category is 1 or 2) { center[axis] += radii[axis] * (category == 1 ? .5f : 1); return Vector(center); }
        var scale = category == 3 ? .75f : 2f;
        for (var i = 0; i < 3; i++) center[i] += radii[i] * scale * (float)random.NextDouble();
        return Vector(center);
    }
    private static Point3 Point(decimal[] values) => new((float)values[0], (float)values[1], (float)values[2]);
    private static Point3 Vector(float[] values) => new(values[0], values[1], values[2]);
    private static void Words(Point3 expected, Point3 actual)
    {
        Assert.Equal(BitConverter.SingleToUInt32Bits(expected.X), BitConverter.SingleToUInt32Bits(actual.X));
        Assert.Equal(BitConverter.SingleToUInt32Bits(expected.Y), BitConverter.SingleToUInt32Bits(actual.Y));
        Assert.Equal(BitConverter.SingleToUInt32Bits(expected.Z), BitConverter.SingleToUInt32Bits(actual.Z));
    }
}
