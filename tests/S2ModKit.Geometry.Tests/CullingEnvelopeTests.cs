using S2ModKit.Geometry;

namespace S2ModKit.Geometry.Tests;

public sealed class CullingEnvelopeTests
{
    private static Point3 P(float value) => new(value, value, value);
    private static readonly Bounds3 Box = new(P(-2), P(2));
    private static readonly CenterHalfExtentBounds CenterBox = new(default, P(2));
    private static readonly FixedCenterSphere Sphere = new(default, 2);

    [Fact]
    public void EmptyAndInwardEditsRetainAuthoredWordsAndPadding()
    {
        foreach (Point3[] points in new[] { Array.Empty<Point3>(), [P(0), P(0.5f)] })
        {
            AssertUnchanged(CullingEnvelope.Expand(Box, points));
            AssertUnchanged(CullingEnvelope.Expand(CenterBox, points));
            AssertUnchanged(CullingEnvelope.Expand(Sphere, points));
        }

        var signed = new CenterHalfExtentBounds(new(-0f, 1e30f, 0), new(1, float.Epsilon, 1));
        var kept = CullingEnvelope.Expand(signed, []);
        AssertUnchanged(kept);
        Assert.Equal(BitConverter.SingleToInt32Bits(-0f), BitConverter.SingleToInt32Bits(kept.Bounds.Center.X));
        Assert.True(CullingEnvelopeVerifier.Verify(signed, [], kept.Bounds).Passed);
        var changedWord = kept.Bounds with { Center = new(0f, 1e30f, 0) };
        Assert.Equal(EnvelopeVerificationStatus.PolicyOutputMismatch, CullingEnvelopeVerifier.Verify(signed, [], changedWord).Status);
    }

    [Fact]
    public void ExpansionIncludesUnchangedContributorsAcrossBufferAndLodGroups()
    {
        Point3[] editedBuffer = [new(10, 0, 0)];
        Point3[] unchangedBuffer = [new(-4, 0, 0)];
        Point3[] anotherLod = [new(0, 5, 0)];
        Point3[] union = [.. editedBuffer, .. unchangedBuffer, .. anotherLod];
        var minMax = CullingEnvelope.Expand(Box, union);
        Assert.Equal(new Point3(-4, -2, -2), minMax.Bounds.Min);
        Assert.Equal(new Point3(10, 5, 2), minMax.Bounds.Max);
        var centered = CullingEnvelope.Expand(CenterBox, union);
        Assert.Equal(new Point3(3, 1.5f, 0), centered.Bounds.Center);
        Assert.Equal(new Point3(7, 3.5f, 2), centered.Bounds.HalfExtents);
        var sphere = CullingEnvelope.Expand(Sphere, union);
        Assert.Equal(10, sphere.Bounds.Radius);
        Assert.All(new[] { minMax.Changed, centered.Changed, sphere.Changed }, Assert.True);
        Assert.Equal(8, Assert.Single(minMax.Growth, item => item.Measure == "maximum_0").Delta);
        Assert.True(CullingEnvelopeVerifier.Verify(Box, union, minMax.Bounds).Passed);
        Assert.True(CullingEnvelopeVerifier.Verify(CenterBox, union, centered.Bounds).Passed);
        Assert.True(CullingEnvelopeVerifier.Verify(Sphere, union, sphere.Bounds).Passed);
        Assert.Equal(EnvelopeVerificationStatus.EscapedGeometry,
            CullingEnvelopeVerifier.Verify(CenterBox, union, CullingEnvelope.Expand(CenterBox, editedBuffer).Bounds).Status);
    }

    [Fact]
    public void EqualGeometryDoesNotEraseDifferentOriginalEnvelopes()
    {
        Point3[] points = [new(10, 0, 0)];
        Assert.Equal(10, CullingEnvelope.Expand(Sphere, points).Bounds.Radius);
        Assert.Equal(20, CullingEnvelope.Expand(Sphere with { Radius = 20 }, points).Bounds.Radius);
        Assert.Equal(P(-20), CullingEnvelope.Expand(new Bounds3(P(-20), P(20)), points).Bounds.Min);
        Assert.Equal(20, CullingEnvelope.Expand(new CenterHalfExtentBounds(default, P(20)), points).Bounds.HalfExtents.X);
    }

    [Fact]
    public void InvalidOrMissingInputsAndUnrepresentableOutputsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => CullingEnvelope.Expand(Box, null!));
        Assert.Throws<ArgumentNullException>(() => CullingEnvelope.Expand(CenterBox, null!));
        Assert.Throws<ArgumentNullException>(() => CullingEnvelope.Expand(Sphere, null!));
        Assert.Throws<ArgumentException>(() => CullingEnvelope.Expand(default(Bounds3), []));
        Assert.Throws<ArgumentException>(() => new Bounds3(P(2), P(-2)));
        Assert.Throws<ArgumentException>(() => CullingEnvelope.Expand(new Bounds3(default, new(1, 0, 1)), []));
        foreach (var half in new[] { 0f, -1f })
            Assert.Throws<ArgumentException>(() => CullingEnvelope.Expand(new CenterHalfExtentBounds(default, new(1, half, 1)), []));
        foreach (var radius in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var invalid = Sphere with { Radius = radius };
            Assert.Throws<ArgumentException>(() => CullingEnvelope.Expand(invalid, []));
            Assert.Equal(EnvelopeVerificationStatus.InvalidInput, CullingEnvelopeVerifier.Verify(invalid, [], Sphere).Status);
        }

        foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Assert.Throws<ArgumentException>(() => P(value));
        Assert.Throws<ArgumentException>(() => CullingEnvelope.Expand(new CenterHalfExtentBounds(P(float.MaxValue), P(float.MaxValue)), []));
        Assert.Throws<OverflowException>(() => CullingEnvelope.Expand(Sphere, [P(float.MaxValue)]));
        Assert.Throws<OverflowException>(() => CullingEnvelope.Expand(new FixedCenterSphere(new(-float.MaxValue, 0, 0), 1), [new(float.MaxValue, 0, 0)]));
        var nearLimit = new CenterHalfExtentBounds(new(float.MaxValue, 0, 0), new(1, 1, 1));
        Assert.Throws<OverflowException>(() => CullingEnvelope.Expand(nearLimit, [new(-float.MaxValue, 0, 0)]));
        Assert.Equal(EnvelopeVerificationStatus.InvalidInput, CullingEnvelopeVerifier.Verify(Box, null, Box).Status);
        Assert.Equal(EnvelopeVerificationStatus.InvalidInput, CullingEnvelopeVerifier.Verify(CenterBox, null, CenterBox).Status);
        Assert.Equal(EnvelopeVerificationStatus.InvalidInput, CullingEnvelopeVerifier.Verify(Sphere, null, Sphere).Status);
    }

    [Fact]
    public void VerifierRejectsShrunkStaleAndOversizedResultsForEveryShape()
    {
        Point3[] points = [new(3, 0, 0)];
        Assert.Equal(EnvelopeVerificationStatus.OriginalEnvelopeShrinkage, CullingEnvelopeVerifier.Verify(Box, points, new Bounds3(P(-1), P(1))).Status);
        Assert.Equal(EnvelopeVerificationStatus.OriginalEnvelopeShrinkage, CullingEnvelopeVerifier.Verify(CenterBox, points, CenterBox with { HalfExtents = P(1) }).Status);
        Assert.Equal(EnvelopeVerificationStatus.OriginalEnvelopeShrinkage, CullingEnvelopeVerifier.Verify(Sphere, points, Sphere with { Radius = 1 }).Status);
        Assert.Equal(EnvelopeVerificationStatus.EscapedGeometry, CullingEnvelopeVerifier.Verify(Box, points, Box).Status);
        Assert.Equal(EnvelopeVerificationStatus.EscapedGeometry, CullingEnvelopeVerifier.Verify(CenterBox, points, CenterBox).Status);
        Assert.Equal(EnvelopeVerificationStatus.EscapedGeometry, CullingEnvelopeVerifier.Verify(Sphere, points, Sphere).Status);
        Assert.Equal(EnvelopeVerificationStatus.PolicyOutputMismatch, CullingEnvelopeVerifier.Verify(Box, points, new Bounds3(P(-100), P(100))).Status);
        Assert.Equal(EnvelopeVerificationStatus.PolicyOutputMismatch, CullingEnvelopeVerifier.Verify(CenterBox, points, CenterBox with { HalfExtents = P(100) }).Status);
        Assert.Equal(EnvelopeVerificationStatus.PolicyOutputMismatch, CullingEnvelopeVerifier.Verify(Sphere, points, Sphere with { Radius = 100 }).Status);
        Assert.Equal(EnvelopeVerificationStatus.PolicyOutputMismatch, CullingEnvelopeVerifier.Verify(Sphere, points, new FixedCenterSphere(P(1), 100)).Status);
        // Equal containment and radius do not permit a different center/extent representation.
        var output = CullingEnvelope.Expand(CenterBox, points).Bounds;
        Assert.Equal(EnvelopeVerificationStatus.PolicyOutputMismatch,
            CullingEnvelopeVerifier.Verify(CenterBox, points, output with { Center = default, HalfExtents = P(3) }).Status);
    }

    [Fact]
    public void TranslatedSphereRetainsCenterAndRoundsEuclideanRadiusOutward()
    {
        var original = new FixedCenterSphere(new(10, -20, 30), 1);
        var points = new Point3[] { new(11, -19, 30) };
        var result = CullingEnvelope.Expand(original, points).Bounds;
        Assert.Equal(original.Center, result.Center);
        Assert.True((double)result.Radius * result.Radius >= 2);
        var previous = MathF.BitDecrement(result.Radius);
        Assert.True((double)previous * previous < 2);
        Assert.True(CullingEnvelopeVerifier.Verify(original, points, result).Passed);
        Assert.Equal(EnvelopeVerificationStatus.EscapedGeometry, CullingEnvelopeVerifier.Verify(original, points, result with { Radius = previous }).Status);
        Assert.Equal(EnvelopeVerificationStatus.PolicyOutputMismatch, CullingEnvelopeVerifier.Verify(original, points, result with { Radius = MathF.BitIncrement(result.Radius) }).Status);
    }

    [Fact]
    public void TinyTermsMustNotDisappearFromSphereDistance()
    {
        Point3[] points = [new(1, float.Epsilon, 0)];
        var source = Sphere with { Radius = 1 };
        var output = CullingEnvelope.Expand(source, points).Bounds;
        Assert.Equal(MathF.BitIncrement(1), output.Radius);
        Assert.Equal(EnvelopeVerificationStatus.EscapedGeometry, CullingEnvelopeVerifier.Verify(source, points, source).Status);
        Assert.True(CullingEnvelopeVerifier.Verify(source, points, output).Passed);
    }

    [Fact]
    public void NewZeroExtremaAreCanonicalButAuthoredZerosStayUntouched()
    {
        var source = new Bounds3(P(1), P(2));
        var forward = CullingEnvelope.Expand(source, [P(-0f), P(0f)]).Bounds;
        var reverse = CullingEnvelope.Expand(source, [P(0f), P(-0f)]).Bounds;
        Assert.Equal(0, BitConverter.SingleToInt32Bits(forward.Min.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(forward.Min.X), BitConverter.SingleToInt32Bits(reverse.Min.X));
        var signed = new Bounds3(P(-0f), P(2));
        Assert.Equal(BitConverter.SingleToInt32Bits(-0f), BitConverter.SingleToInt32Bits(CullingEnvelope.Expand(signed, []).Bounds.Min.X));
    }

    private static void AssertUnchanged<T>(EnvelopeCalculation<T> result)
    {
        Assert.False(result.Changed);
        Assert.All(result.Growth, measurement => Assert.Equal(0, measurement.Delta));
    }
}
