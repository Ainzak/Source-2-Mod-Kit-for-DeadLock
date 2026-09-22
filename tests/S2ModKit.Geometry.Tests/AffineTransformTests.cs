using S2ModKit.Geometry;

namespace S2ModKit.Geometry.Tests;

public sealed class AffineTransformTests
{
    [Fact]
    public void AppliesScaleThenRightHandedRotationThenTranslationAroundPivot()
    {
        var transform = new AffineTransform(
            new Point3(1, 1, 0),
            new AffineScale(2, 1, 1),
            new AxisAngleRotation(new Point3(0, 0, 1), 90),
            RigidFrame.Model,
            new Point3(3, 0, 0));

        var result = transform.Apply(new Point3(2, 1, 0));

        AssertClose(new Point3(4, 3, 0), result);
        Assert.True(transform.LinearMap.Determinant > 0);
    }

    [Fact]
    public void FrameChangesTheAxesInWhichScaleIsApplied()
    {
        var frame = new RigidFrame(new Matrix3(0, -1, 0, 1, 0, 0, 0, 0, 1));
        var transform = new AffineTransform(
            new Point3(0, 0, 0),
            new AffineScale(2, 1, 1),
            AxisAngleRotation.Identity,
            frame,
            new Point3(0, 0, 0));

        AssertClose(new Point3(1, 2, 0), transform.Apply(new Point3(1, 1, 0)));
    }

    [Fact]
    public void TransformsNormalByInverseTransposeAndReorthogonalizesTangent()
    {
        var inverseRootTwo = 1f / MathF.Sqrt(2f);
        var transform = new AffineTransform(
            new Point3(0, 0, 0),
            new AffineScale(2, 1, 1),
            AxisAngleRotation.Identity,
            RigidFrame.Model,
            new Point3(0, 0, 0));

        var result = transform.Apply(new TangentFrame(
            new Point3(inverseRootTwo, inverseRootTwo, 0),
            new Point3(inverseRootTwo, -inverseRootTwo, 0),
            -1));

        AssertClose(1f, Length(result.Normal));
        AssertClose(1f, Length(result.Tangent));
        AssertClose(0f, Dot(result.Normal, result.Tangent));
        Assert.True(result.Normal.Y > result.Normal.X);
        Assert.Equal(-1f, result.Handedness);
    }

    [Fact]
    public void RejectsDegenerateTangentAndInvalidHandedness()
    {
        var transform = IdentityKernel();

        Assert.Throws<ArgumentException>(() => transform.Apply(
            new TangentFrame(new Point3(1, 0, 0), new Point3(1, 0, 0), 1)));
        Assert.Throws<ArgumentException>(() => transform.Apply(
            new TangentFrame(new Point3(1, 0, 0), new Point3(0, 1, 0), 0)));
    }

    [Fact]
    public void RigidFrameRejectsScaleShearAndReflection()
    {
        Assert.Throws<ArgumentException>(() => new RigidFrame(new Matrix3(2, 0, 0, 0, 1, 0, 0, 0, 1)));
        Assert.Throws<ArgumentException>(() => new RigidFrame(new Matrix3(1, 0.1f, 0, 0, 1, 0, 0, 0, 1)));
        Assert.Throws<ArgumentException>(() => new RigidFrame(new Matrix3(-1, 0, 0, 0, 1, 0, 0, 0, 1)));
    }

    [Fact]
    public void RotationAndScaleRejectInvalidRepresentations()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AffineScale(0, 1, 1));
        Assert.Throws<ArgumentException>(() => new AffineScale(float.NaN, 1, 1));
        Assert.Throws<ArgumentException>(() => new AxisAngleRotation(new Point3(2, 0, 0), 45));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxisAngleRotation(new Point3(1, 0, 0), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxisAngleRotation(new Point3(1, 0, 0), -180));
    }

    [Theory]
    [InlineData("min_x", -2, 3, 4)]
    [InlineData("max_x", 2, 3, 4)]
    [InlineData("min_y", 0, 0, 4)]
    [InlineData("max_y", 0, 6, 4)]
    [InlineData("min_z", 0, 3, 1)]
    [InlineData("max_z", 0, 3, 7)]
    public void ResolvesNamedBoundsFaceCenters(string face, float x, float y, float z)
    {
        var bounds = new Bounds3(new Point3(-2, 0, 1), new Point3(2, 6, 7));

        Assert.Equal(new Point3(x, y, z), AffineTransform.ResolveBoundsFace(bounds, face));
    }

    [Fact]
    public void SummaryUsesExactOutputForBoundsAndDisplacement()
    {
        var transform = new AffineTransform(
            new Point3(0, 0, 0),
            new AffineScale(2, 1, 1),
            new AxisAngleRotation(new Point3(0, 0, 1), 90),
            RigidFrame.Model,
            new Point3(1, 0, 0));
        var input = new[] { new Point3(0, 0, 0), new Point3(1, 0, 0) };
        var output = new Point3[2];

        var summary = transform.ApplyAndSummarize(input, output);

        AssertClose(new Point3(1, 0, 0), output[0]);
        AssertClose(new Point3(1, 2, 0), output[1]);
        Assert.Equal(Bounds3.FromPoints(output), summary.AfterBounds);
        Assert.Equal(2, summary.ChangedPointCount);
        AssertClose(2f, summary.MaximumDisplacement);
    }

    [Fact]
    public void DeterministicRandomizedFramesRemainFiniteAndOrientationPreserving()
    {
        var random = new Random(2609);
        for (var iteration = 0; iteration < 128; iteration++)
        {
            var degrees = (float)((random.NextDouble() * 359d) - 179d);
            if (MathF.Abs(degrees) < 0.01f)
            {
                degrees = 30f;
            }

            var transform = new AffineTransform(
                new Point3(1, -2, 3),
                new AffineScale(
                    0.25f + ((float)random.NextDouble() * 3.75f),
                    0.25f + ((float)random.NextDouble() * 3.75f),
                    0.25f + ((float)random.NextDouble() * 3.75f)),
                new AxisAngleRotation(new Point3(0, 0, 1), degrees),
                RigidFrame.Model,
                new Point3(0.5f, -0.25f, 0.125f));

            var result = transform.Apply(new Point3(iteration * 0.01f, 2, -4));
            Assert.True(float.IsFinite(result.X) && float.IsFinite(result.Y) && float.IsFinite(result.Z));
            Assert.True(transform.LinearMap.Determinant > 0f);
        }
    }

    [Fact]
    public void SummaryFailsClosedOnOverflow()
    {
        var transform = new AffineTransform(
            new Point3(0, 0, 0),
            new AffineScale(4, 4, 4),
            AxisAngleRotation.Identity,
            RigidFrame.Model,
            new Point3(0, 0, 0));

        Assert.Throws<ArgumentException>(() => transform.Summarize([new Point3(float.MaxValue, 0, 0)]));
    }

    private static AffineTransform IdentityKernel() => new(
        new Point3(0, 0, 0),
        new AffineScale(1, 1, 1),
        AxisAngleRotation.Identity,
        RigidFrame.Model,
        new Point3(0, 0, 0));

    private static float Length(Point3 value) => MathF.Sqrt(Dot(value, value));
    private static float Dot(Point3 left, Point3 right) =>
        (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);

    private static void AssertClose(Point3 expected, Point3 actual)
    {
        AssertClose(expected.X, actual.X);
        AssertClose(expected.Y, actual.Y);
        AssertClose(expected.Z, actual.Z);
    }

    private static void AssertClose(float expected, float actual) => Assert.InRange(actual, expected - 1e-5f, expected + 1e-5f);
}
