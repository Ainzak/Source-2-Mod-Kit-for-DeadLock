namespace S2ModKit.Geometry.Tests;

public sealed class DirectionalEllipsoidScaleTests
{
    [Fact]
    public void TenThousandCasesMatchIndependentStoredWordsFullMatrixAndFrameOracles()
    {
        var fields = Fields();
        var reconstructions = fields.Select(f => new DirectionalFieldReconstruction(f.Pivot, f.OuterRadii, f.CoreFraction, f.Scale, f.DisplacementLimit)).ToArray();
        var random = new Random(34005);
        var counts = new int[3];
        for (var i = 0; i < 10_000; i++)
        {
            var field = fields[i % fields.Length];
            var point = Sample(field, random, i % 5);
            var normal = DirectionalEllipsoidOracle.Unit([random.Next(-100, 101), random.Next(-100, 101), 101]);
            var tangent = normal[0] == 0 && normal[1] == 0 ? new decimal[] { 1, 0, 0 }
                : DirectionalEllipsoidOracle.Unit([normal[1], -normal[0], 0]);
            var sourceFrame = new TangentFrame(Point(normal), Point(tangent), i % 2 == 0 ? 1 : -1);
            var expected = DirectionalEllipsoidOracle.Evaluate(field, point, sourceFrame);
            var actual = field.Evaluate(point);
            var reconstructed = reconstructions[i % fields.Length].ReconstructPosition(point);
            Assert.Equal(expected.Membership, reconstructed.Membership);
            Assert.Equal(BitConverter.DoubleToUInt64Bits(expected.Weight), BitConverter.DoubleToUInt64Bits(reconstructed.Weight));
            AssertWords(expected.StoredPosition, reconstructed.Position);
            Assert.Equal(expected.Membership, actual.Membership);
            Assert.Equal(BitConverter.DoubleToUInt64Bits(expected.Weight), BitConverter.DoubleToUInt64Bits(actual.Weight));
            AssertWords(expected.StoredPosition, actual.Position);
            Assert.InRange(actual.MaximumDisplacement, 0, field.DisplacementLimit);
            if (field.Scale.X == 1) AssertWord(point.X, actual.Position.X);
            if (field.Scale.Y == 1) AssertWord(point.Y, actual.Position.Y);
            if (field.Scale.Z == 1) AssertWord(point.Z, actual.Position.Z);
            var frame = field.TransformFrame(point, sourceFrame);
            var roundedFrame = DirectionalEllipsoidOracle.RoundedFrame(field, point, sourceFrame, expected);
            var reconstructedFrame = reconstructions[i % fields.Length].ReconstructFrame(point, sourceFrame);
            AssertWords(roundedFrame.Normal, reconstructedFrame.Normal);
            AssertWords(roundedFrame.Tangent, reconstructedFrame.Tangent);
            Assert.Equal(sourceFrame.Handedness, reconstructedFrame.Handedness);
            AssertWords(roundedFrame.Normal, frame.Normal);
            AssertWords(roundedFrame.Tangent, frame.Tangent);
            Assert.True(Dot(expected.Normal, frame.Normal) >= 0.9999998m);
            Assert.True(Dot(expected.Tangent, frame.Tangent) >= 0.9999998m);
            Assert.Equal(sourceFrame.Handedness, frame.Handedness);
            if (actual.Membership == EllipsoidMembership.Pinned) Assert.Same(sourceFrame, frame);
            var jacobian = field.Jacobian(point);
            for (var row = 0; row < 3; row++) for (var column = 0; column < 3; column++)
                Assert.InRange((decimal)jacobian[row, column] - expected.Jacobian[row, column], -0.00000000002m, 0.00000000002m);
            Assert.True(expected.Determinant >= Exact(field.Certificate.DeterminantLowerBound).Decimal);
            CheckInverseBound(expected.Inverse, Exact(field.Certificate.InverseNormUpperBound).Decimal);
            counts[(int)actual.Membership]++;
        }
        Assert.True(counts[(int)EllipsoidMembership.Core] >= 1900);
        Assert.True(counts[(int)EllipsoidMembership.Pinned] >= 1900);
        Assert.True(counts[(int)EllipsoidMembership.Transition] >= 5900);
    }

    [Fact]
    public void CertificateEnclosuresAndAnalyticPolynomialMinimaAreIndependentAndExact()
    {
        foreach (var field in Fields())
        {
            var certificate = field.Certificate;
            var factors = DirectionalEllipsoidOracle.Values(field.Scale);
            var h = DirectionalOracleFraction.From(field.CoreFraction).Decimal;
            var k = h / (1 - h);
            var b = 12 * k - 18;
            var discriminant = DirectionalEllipsoidOracle.Sqrt(b * b + 576 * k);
            decimal[] locations = [0, 1, (-b + discriminant) / 48, (-b - discriminant) / 48];
            for (var i = 0; i < 3; i++)
            {
                var minimum = locations.Where(t => t is >= 0 and <= 1).Min(t => factors[i]
                    + (factors[i] - 1) * (8 * t * t * t + (6 * k - 9) * t * t - 6 * k * t));
                var lower = Exact(certificate.AxisLowerBounds[i]).Decimal;
                Assert.True(lower <= minimum && minimum - lower < 0.000001m);
            }
            foreach (var bound in certificate.AxisLowerBounds.Concat(certificate.MinimumAxisFactors).Concat(
                [certificate.AlphaLowerBound, certificate.BetaLowerBound, certificate.RankOneNormUpperBound,
                    certificate.InverseNormUpperBound, certificate.MinimumSingularValueLowerBound,
                    certificate.DeterminantLowerBound, certificate.IdealDisplacementUpperBound]))
            {
                var exact = Exact(bound);
                Assert.True(DirectionalOracleFraction.From(bound.LowerDouble).Compare(exact) <= 0);
                Assert.True(DirectionalOracleFraction.From(bound.UpperDouble).Compare(exact) >= 0);
                Assert.True(bound.LowerDouble == bound.UpperDouble || Math.BitIncrement(bound.LowerDouble) == bound.UpperDouble);
                Assert.Equal(System.Numerics.BigInteger.One, System.Numerics.BigInteger.GreatestCommonDivisor(bound.Numerator, bound.Denominator));
            }
        }
        var body = Fields()[0].Certificate;
        Assert.Equal("41633267932752439", body.AxisLowerBounds[0].Numerator.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("172938223973040128", body.AxisLowerBounds[0].Denominator.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.InRange(body.MinimumSingularValueLowerBound.LowerDouble, 0.04884921, 0.04884923);
        Assert.Equal(5, body.IdealDisplacementUpperBound.LowerDouble);
    }

    [Fact]
    public void VectorAdmissionDoesNotSubstituteTheScalarCertificate()
    {
        _ = EllipsoidJacobianCertificate.Create(1f / 16, 2, new(8, 4, 1));
        Assert.Throws<ArgumentException>(() => DirectionalEllipsoidCertificate.Create(1f / 16, new(2, 2, 2), new(8, 4, 1)));
        // Actual fold, independently evaluated in power basis at t=3/4.
        const decimal t = 0.75m;
        var b = 2 + 8 * t * t * t + (2 - 9) * t * t - 2 * t;
        Assert.Equal(-1m / 16, b);
        Assert.Throws<ArgumentException>(() => DirectionalEllipsoidCertificate.Create(0.25f, new(2, 1, 1), new(2, 2, 2)));
        Assert.Throws<ArgumentException>(() => DirectionalEllipsoidCertificate.Create(0.99f, new(0.5f, 1, 1), new(8, 1, 1)));
    }

    [Fact]
    public void IndependentSoftwareRoundingHandlesTiesSubnormalsAndSignedZero()
    {
        var two = new System.Numerics.BigInteger(2);
        Assert.Equal(1d, new DirectionalOracleFraction(System.Numerics.BigInteger.Pow(two, 53) + 1,
            System.Numerics.BigInteger.Pow(two, 53)).Round());
        Assert.Equal(Math.BitIncrement(Math.BitIncrement(1d)), new DirectionalOracleFraction(System.Numerics.BigInteger.Pow(two, 53) + 3,
            System.Numerics.BigInteger.Pow(two, 53)).Round());
        AssertWord(1, new DirectionalOracleFraction(System.Numerics.BigInteger.Pow(two, 24) + 1,
            System.Numerics.BigInteger.Pow(two, 24)).RoundFloat());
        AssertWord(float.BitIncrement(float.BitIncrement(1)), new DirectionalOracleFraction(System.Numerics.BigInteger.Pow(two, 24) + 3,
            System.Numerics.BigInteger.Pow(two, 24)).RoundFloat());
        AssertWord(0, new DirectionalOracleFraction(1, System.Numerics.BigInteger.Pow(two, 150)).RoundFloat());
        AssertWord(float.Epsilon * 2, new DirectionalOracleFraction(3, System.Numerics.BigInteger.Pow(two, 150)).RoundFloat());
        Assert.Equal(0d, new DirectionalOracleFraction(1, System.Numerics.BigInteger.Pow(two, 1075)).Round());
        Assert.Equal(double.Epsilon * 2, new DirectionalOracleFraction(3, System.Numerics.BigInteger.Pow(two, 1075)).Round());
        Assert.Equal(.4d, new DirectionalOracleFraction(4, 25).Sqrt());
        var negativeZero = DirectionalOracleFraction.From(BitConverter.UInt64BitsToDouble(0x8000000000000000));
        Assert.Equal(0x8000000000000000UL, BitConverter.DoubleToUInt64Bits(negativeZero.Round()));
        Assert.Equal(0x8000000000000000UL, BitConverter.DoubleToUInt64Bits((negativeZero + negativeZero).Round()));
        Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits((negativeZero - negativeZero).Round()));
        Assert.Equal(0x8000000000000000UL, BitConverter.DoubleToUInt64Bits((negativeZero * DirectionalOracleFraction.One).Round()));
    }

    [Fact]
    public void ExactAspectAndIdealDisplacementCeilingsRejectAdjacentOutOfRangeWords()
    {
        _ = new DirectionalEllipsoidScale(default, new(8, 1, 1), .25f, new(1.125f, 1, 1), 64);
        Assert.Throws<ArgumentException>(() => new DirectionalEllipsoidScale(default, new(float.BitIncrement(8), 1, 1),
            .25f, new(1.125f, 1, 1), 64));
        var limit = new DirectionalEllipsoidScale(default, new(256, 256, 256), .5f, new(1.25f, 1, 1), 64);
        Assert.Equal(64, limit.Certificate.IdealDisplacementUpperBound.LowerDouble);
        Assert.Throws<ArgumentException>(() => new DirectionalEllipsoidScale(default, new(float.BitIncrement(256), 256, 256),
            .5f, new(1.25f, 1, 1), 64));
    }

    [Theory]
    [InlineData(0, 1.5f, 1, 1, 1, 1, 1)]
    [InlineData(1, 1.5f, 1, 1, 1, 1, 1)]
    [InlineData(float.NaN, 1.5f, 1, 1, 1, 1, 1)]
    [InlineData(0.25f, 1, 1, 1, 1, 1, 1)]
    [InlineData(0.25f, 0, 1, 1, 1, 1, 1)]
    [InlineData(0.25f, -1, 1, 1, 1, 1, 1)]
    [InlineData(0.25f, 0.49f, 1, 1, 1, 1, 1)]
    [InlineData(0.25f, 2.01f, 1, 1, 1, 1, 1)]
    [InlineData(0.25f, 1.5f, 1, 1, 0, 1, 1)]
    [InlineData(0.25f, 1.5f, 1, 1, -1, 1, 1)]
    [InlineData(0.25f, 1.25f, 1, 1, 8.01f, 1, 1)]
    [InlineData(0.25f, 1.25f, 1, 1, 257, 257, 257)]
    public void InvalidOrOutOfDomainFieldsReject(float core, float sx, float sy, float sz, float rx, float ry, float rz) =>
        Assert.ThrowsAny<ArgumentException>(() => new DirectionalEllipsoidScale(default, new(rx, ry, rz), core, new(sx, sy, sz), 64));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidStoredDisplacementLimitsReject(float cap) => Assert.ThrowsAny<ArgumentException>(() =>
        new DirectionalEllipsoidScale(default, new(2, 2, 2), .25f, new(1.25f, 1, 1), cap));

    [Fact]
    public void ExactBoundariesAdjacentFloatsAndRoundedEndpointTransitionsStayDistinct()
    {
        var field = new DirectionalEllipsoidScale(default, new(8, 4, 2), 1f / 16, new(2, 1, 1), 64);
        Assert.Equal(EllipsoidMembership.Core, field.Evaluate(new(.5f, 0, 0)).Membership);
        Assert.Equal(EllipsoidMembership.Core, field.Evaluate(new(float.BitDecrement(.5f), 0, 0)).Membership);
        Assert.Equal(EllipsoidMembership.Transition, field.Evaluate(new(float.BitIncrement(.5f), 0, 0)).Membership);
        var roundedEndpoint = field.Evaluate(new(.5f, float.Epsilon, 0));
        Assert.Equal(EllipsoidMembership.Transition, roundedEndpoint.Membership);
        Assert.Equal(1, roundedEndpoint.Weight);
        Assert.Equal(EllipsoidMembership.Transition, field.Evaluate(new(float.BitDecrement(8), 0, 0)).Membership);
        Assert.Equal(EllipsoidMembership.Pinned, field.Evaluate(new(8, 0, 0)).Membership);
        Assert.Equal(EllipsoidMembership.Pinned, new DirectionalEllipsoidScale(default, new(5, 5, 5), .25f,
            new(1.25f, 1, 1), 64).Evaluate(new(3, 4, 0)).Membership);
    }

    [Fact]
    public void PinnedAndUnscaledAxisWordsRetainNegativeZero()
    {
        var negativeZero = BitConverter.UInt32BitsToSingle(0x80000000);
        var field = new DirectionalEllipsoidScale(default, new(8, 4, 2), .25f, new(1.25f, 1, 1), 64);
        foreach (var point in new Point3[] { new(.25f, negativeZero, negativeZero), new(4, negativeZero, negativeZero), new(8, negativeZero, negativeZero) })
        {
            var output = field.Evaluate(point).Position;
            AssertWord(point.Y, output.Y); AssertWord(point.Z, output.Z);
            if (point.X == 8) AssertWords(point, output);
        }
    }

    [Fact]
    public void AnisotropicCoreTransformsAFrameEvenAtTheFixedPivot()
    {
        var field = Fields()[0];
        var component = (float)(1 / Math.Sqrt(2));
        var frame = new TangentFrame(new(component, 0, component), new(0, 1, 0), -1);
        AssertWords(field.Pivot, field.Evaluate(field.Pivot).Position);
        var output = field.TransformFrame(field.Pivot, frame);
        Assert.True(Dot(DirectionalEllipsoidOracle.Values(frame.Normal), output.Normal) < .9999m);
        Assert.Equal(-1, output.Handedness);
        var equal = Fields()[3];
        Assert.Same(frame, equal.TransformFrame(equal.Pivot, frame));
        Assert.Same(frame, field.TransformFrame(new(100, 100, 100), frame));
    }

    [Fact]
    public void AllSixAxisPermutationsPreserveTheDirectionalCalculation()
    {
        int[][] permutations = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        var field = Fields()[1];
        var point = new Point3(5, -2, 8);
        var frame = new TangentFrame(new(0, 0, 1), new(1, 0, 0), -1);
        var expected = field.Evaluate(point); var expectedFrame = field.TransformFrame(point, frame);
        foreach (var permutation in permutations)
        {
            var permuted = new DirectionalEllipsoidScale(Permute(field.Pivot, permutation), Permute(field.OuterRadii, permutation),
                field.CoreFraction, Permute(field.Scale, permutation), 64);
            var actual = permuted.Evaluate(Permute(point, permutation));
            Assert.Equal(expected.Membership, actual.Membership);
            AssertWords(Permute(expected.Position, permutation), actual.Position);
            var transformed = permuted.TransformFrame(Permute(point, permutation),
                new(Permute(frame.Normal, permutation), Permute(frame.Tangent, permutation), frame.Handedness));
            Assert.True(Dot(DirectionalEllipsoidOracle.Values(Permute(expectedFrame.Normal, permutation)), transformed.Normal) > .9999998m);
            Assert.True(Dot(DirectionalEllipsoidOracle.Values(Permute(expectedFrame.Tangent, permutation)), transformed.Tangent) > .9999998m);
        }
    }

    [Fact]
    public void SubnormalCoordinatesAndExtremePivotsUseStoredPointLimits()
    {
        var tiny = new DirectionalEllipsoidScale(default, new(float.Epsilon * 16, float.Epsilon * 16, float.Epsilon * 16),
            .4f, new(.5f, 1, 1), 64);
        Assert.Equal(EllipsoidMembership.Pinned, tiny.Evaluate(new(float.Epsilon * 16, 0, 0)).Membership);
        Assert.Equal(EllipsoidMembership.Core, tiny.Evaluate(new(float.Epsilon * 4, 0, 0)).Membership);
        AssertWord(float.Epsilon * 2, tiny.Evaluate(new(float.Epsilon * 4, 0, 0)).Position.X);
        var distant = new DirectionalEllipsoidScale(new(float.MaxValue, -float.MaxValue, float.MaxValue), new(8, 8, 8),
            .25f, new(1.25f, 1, 1), 64);
        AssertWords(distant.Pivot, distant.Evaluate(distant.Pivot).Position);
        Assert.Equal(EllipsoidMembership.Pinned, distant.Evaluate(default).Membership);
        const float binade = 1 << 30;
        var rounded = new DirectionalEllipsoidScale(new(float.BitDecrement(binade), 0, 0), new(256, 256, 256),
            .5f, new(1.25f, 1, 1), 32);
        Assert.Throws<ArgumentException>(() => rounded.Evaluate(new(binade - 192, 0, 0)));
    }

    [Fact]
    public void AValidIdealCertificateDoesNotAuthorizeCollapsedStoredTriangles()
    {
        var field = new DirectionalEllipsoidScale(default, new(float.Epsilon * 16, float.Epsilon * 16, float.Epsilon * 16),
            .4f, new(.5f, 1, 1), 64);
        Point3[] points = [new(float.Epsilon * 3, 0, 0), new(float.Epsilon * 4, 0, 0), new(float.Epsilon * 3, float.Epsilon, 0)];
        var output = points.Select(point => field.Evaluate(point).Position).ToArray();
        AssertWords(output[0], output[1]);
        RegionTriangleGuard.Validate(points, points, [0, 1, 2]);
        Assert.Throws<ArgumentException>(() => RegionTriangleGuard.Validate(points, output, [0, 1, 2]));
    }

    [Fact]
    public void InvalidFramesRejectInEveryRegion()
    {
        var field = new DirectionalEllipsoidScale(default, new(8, 8, 8), .25f, new(1.25f, 1, 1), 64);
        foreach (var point in new Point3[] { default, new(4, 0, 0), new(8, 0, 0) })
            foreach (var frame in new TangentFrame[] { new(default, new(1, 0, 0), 1), new(new(1, 0, 0), new(1, 0, 0), 1), new(new(0, 0, 1), new(1, 0, 0), 0) })
                Assert.Throws<ArgumentException>(() => field.TransformFrame(point, frame));
    }

    private static DirectionalEllipsoidScale[] Fields() => [
        new(new(6, -25.5f, 64), new(10, 8, 4), .4f, new(1.5f, 1.5f, 1), 64),
        new(new(3, -5, 7), new(7, 6, 4), .25f, new(1.25f, 1.125f, .875f), 64),
        new(new(-16, 8, 0), new(4, 3, 2), .4f, new(.75f, .875f, 1), 64),
        new(default, new(10, 8, 4), .4f, new(1.5f, 1.5f, 1.5f), 64),
        new(default, new(8, 4, 2), 1f / 16, new(2, 1, 1), 64),
        new(default, new(2, 2, 2), .25f, new(.5f, 1, 1), 64)];
    private static Point3 Sample(DirectionalEllipsoidScale field, Random random, int region)
    {
        var pivot = new[] { field.Pivot.X, field.Pivot.Y, field.Pivot.Z };
        var radii = new[] { field.OuterRadii.X, field.OuterRadii.Y, field.OuterRadii.Z };
        double[] direction = [random.Next(-1024, 1025), random.Next(-1024, 1025), 1025];
        var length = Math.Sqrt(direction.Sum(value => value * value));
        var radius = region switch { 0 => field.CoreFraction * .5, 1 => 1.5, _ => field.CoreFraction + (1 - field.CoreFraction) * random.Next(1, 1024) / 1024d };
        return new((float)(pivot[0] + radius * radii[0] * direction[0] / length),
            (float)(pivot[1] + radius * radii[1] * direction[1] / length), (float)(pivot[2] + radius * radii[2] * direction[2] / length));
    }
    private static void CheckInverseBound(decimal[,] inverse, decimal limit)
    {
        var difference = new decimal[3, 3];
        for (var row = 0; row < 3; row++) for (var column = 0; column < 3; column++)
            difference[row, column] = (row == column ? limit * limit : 0)
                - Enumerable.Range(0, 3).Sum(i => inverse[i, row] * inverse[i, column]);
        Assert.True(difference[0, 0] > 0);
        Assert.True(difference[0, 0] * difference[1, 1] - difference[0, 1] * difference[1, 0] > 0);
        var cof = DirectionalEllipsoidOracle.Cofactors(difference);
        Assert.True(Enumerable.Range(0, 3).Sum(i => difference[0, i] * cof[0, i]) > 0);
    }
    private static DirectionalOracleFraction Exact(DirectionalFieldBound bound) => new(bound.Numerator, bound.Denominator);
    private static decimal Dot(decimal[] expected, Point3 point) { var actual = DirectionalEllipsoidOracle.Values(point); return Enumerable.Range(0, 3).Sum(i => expected[i] * actual[i]); }
    private static Point3 Point(decimal[] values) => new((float)values[0], (float)values[1], (float)values[2]);
    private static Point3 Permute(Point3 point, int[] axes) { var values = new[] { point.X, point.Y, point.Z }; return new(values[axes[0]], values[axes[1]], values[axes[2]]); }
    private static void AssertWord(float expected, float actual) => Assert.Equal(BitConverter.SingleToUInt32Bits(expected), BitConverter.SingleToUInt32Bits(actual));
    private static void AssertWords(Point3 expected, Point3 actual) { AssertWord(expected.X, actual.X); AssertWord(expected.Y, actual.Y); AssertWord(expected.Z, actual.Z); }
}
