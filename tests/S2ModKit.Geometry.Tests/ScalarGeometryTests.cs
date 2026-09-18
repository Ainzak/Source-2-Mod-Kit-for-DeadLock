using S2ModKit.Geometry;

namespace S2ModKit.Geometry.Tests;

public sealed class Point3Tests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ConstructorRejectsNonFiniteComponents(int component)
    {
        var values = new[] { 0f, 0f, 0f };

        foreach (var nonFinite in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            values[component] = nonFinite;

            Assert.Throws<ArgumentException>(() => new Point3(values[0], values[1], values[2]));
        }
    }
}

public sealed class Bounds3Tests
{
    [Fact]
    public void FromPointsComputesComponentwiseExtrema()
    {
        var points = new[] { new Point3(0f, 4f, -2f), new Point3(-3f, 1f, 7f), new Point3(2f, 0f, 3f) };

        var bounds = Bounds3.FromPoints(points);

        Assert.Equal(new Point3(-3f, 0f, -2f), bounds.Min);
        Assert.Equal(new Point3(2f, 4f, 7f), bounds.Max);
    }

    [Fact]
    public void FromPointsRejectsNullAndEmptySequences()
    {
        Assert.Throws<ArgumentNullException>(() => Bounds3.FromPoints(null!));
        Assert.Throws<ArgumentException>(() => Bounds3.FromPoints([]));
    }

    [Fact]
    public void ContainsReportsInsideAndOutsidePoints()
    {
        var bounds = new Bounds3(new Point3(-1f, -1f, -1f), new Point3(1f, 1f, 1f));

        Assert.True(bounds.Contains(new Point3(0f, 0.5f, -1f)));
        Assert.True(bounds.Contains(bounds.Min));
        Assert.True(bounds.Contains(bounds.Max));
        Assert.False(bounds.Contains(new Point3(1.0001f, 0f, 0f)));
        Assert.False(bounds.Contains(new Point3(0f, -2f, 0f)));
    }

    [Fact]
    public void ConstructorRejectsMinimumAboveMaximum()
    {
        Assert.Throws<ArgumentException>(() => new Bounds3(new Point3(1f, 0f, 0f), new Point3(0f, 0f, 0f)));
        Assert.Throws<ArgumentException>(() => new Bounds3(new Point3(0f, 1f, 0f), new Point3(0f, 0f, 0f)));
        Assert.Throws<ArgumentException>(() => new Bounds3(new Point3(0f, 0f, 1f), new Point3(0f, 0f, 0f)));
    }

    [Fact]
    public void CenterRemainsFiniteForExtremeFiniteBounds()
    {
        var bounds = new Bounds3(
            new Point3(-float.MaxValue, -float.MaxValue, -float.MaxValue),
            new Point3(float.MaxValue, float.MaxValue, float.MaxValue));

        Assert.Equal(new Point3(0f, 0f, 0f), bounds.Center);
    }
}

public sealed class UniformTransformTests
{
    [Fact]
    public void ConstructorAcceptsScaleBoundariesInclusive()
    {
        var pivot = new Point3(1f, 2f, 3f);
        var translation = new Point3(0f, 0f, 0f);

        Assert.Equal(UniformTransform.MinimumScale, new UniformTransform(pivot, 0.25f, translation).Scale);
        Assert.Equal(UniformTransform.MaximumScale, new UniformTransform(pivot, 4f, translation).Scale);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(0.2f)]
    [InlineData(4.5f)]
    public void ConstructorRejectsOutOfRangeScale(float scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new UniformTransform(new Point3(0f, 0f, 0f), scale, new Point3(0f, 0f, 0f)));
    }

    [Fact]
    public void ConstructorRejectsNonFiniteScale()
    {
        foreach (var scale in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Assert.Throws<ArgumentException>(
                () => new UniformTransform(new Point3(0f, 0f, 0f), scale, new Point3(0f, 0f, 0f)));
        }
    }

    [Fact]
    public void ConstructorRejectsNonFinitePivotAndTranslation()
    {
        Assert.Throws<ArgumentException>(
            () => new UniformTransform(new Point3(float.NaN, 0f, 0f), 1f, new Point3(0f, 0f, 0f)));
        Assert.Throws<ArgumentException>(
            () => new UniformTransform(new Point3(0f, 0f, 0f), 1f, new Point3(0f, float.PositiveInfinity, 0f)));
        Assert.Throws<ArgumentException>(
            () => new UniformTransform(new Point3(0f, 0f, 0f), 1f, new Point3(0f, 0f, float.NegativeInfinity)));
    }

    [Fact]
    public void ApplyScalesAroundNonZeroPivot()
    {
        var transform = new UniformTransform(new Point3(2f, 3f, 4f), 2f, new Point3(0f, 0f, 0f));

        var transformed = transform.Apply(new Point3(4f, 3f, 6f));

        Assert.Equal(new Point3(6f, 3f, 8f), transformed);
    }

    [Fact]
    public void ApplyUsesScaleThenTranslationOrder()
    {
        var transform = new UniformTransform(new Point3(1f, 1f, 1f), 3f, new Point3(2f, 0f, 0f));

        var transformed = transform.Apply(new Point3(2f, 1f, 1f));

        Assert.Equal(new Point3(6f, 1f, 1f), transformed);
        Assert.NotEqual(new Point3(10f, 1f, 1f), transformed);
    }

    [Fact]
    public void ApplyToSequenceReturnsNewArrayWithoutMutatingInput()
    {
        var transform = new UniformTransform(new Point3(0f, 0f, 0f), 2f, new Point3(1f, 1f, 1f));
        var input = new[] { new Point3(0f, 0f, 0f), new Point3(2f, 1f, 0f), new Point3(-1f, 0f, 4f) };
        var snapshot = (Point3[])input.Clone();

        var output = transform.Apply(input);

        Assert.NotSame(input, output);
        AssertPointSequencesEqual(snapshot, input);
        AssertPointSequencesEqual(
            [new Point3(1f, 1f, 1f), new Point3(5f, 3f, 1f), new Point3(-1f, 1f, 9f)],
            output);
    }

    [Fact]
    public void ApplyAndSummarizeRejectNullAndEmptyCollections()
    {
        var transform = new UniformTransform(new Point3(0f, 0f, 0f), 2f, new Point3(0f, 0f, 0f));

        Assert.Throws<ArgumentNullException>(() => transform.Apply(null!));
        Assert.Throws<ArgumentException>(() => transform.Apply([]));
        Assert.Throws<ArgumentNullException>(() => transform.Summarize(null!));
        Assert.Throws<ArgumentException>(() => transform.Summarize([]));
        Assert.Throws<ArgumentNullException>(() => transform.ApplyAndSummarize(null!, []));
        Assert.Throws<ArgumentException>(() => transform.ApplyAndSummarize([], []));
        Assert.Throws<ArgumentNullException>(() => transform.ApplyAndSummarize([new Point3(0f, 0f, 0f)], null!));
    }

    [Fact]
    public void ApplyAndSummarizeWritesTheExactPointsUsedForFacts()
    {
        var transform = new UniformTransform(new Point3(1f, 0f, 0f), 2f, new Point3(0f, 3f, 0f));
        var points = new[] { new Point3(1f, 0f, 0f), new Point3(3f, 2f, -1f) };
        var transformed = new Point3[points.Length];

        var summary = transform.ApplyAndSummarize(points, transformed);

        AssertPointSequencesEqual(
            [new Point3(1f, 3f, 0f), new Point3(5f, 7f, -2f)],
            transformed);
        Assert.Equal(Bounds3.FromPoints(points), summary.BeforeBounds);
        Assert.Equal(Bounds3.FromPoints(transformed), summary.AfterBounds);
        Assert.Equal(2, summary.ChangedPointCount);
        Assert.Equal((float)Math.Sqrt(30.0), summary.MaximumDisplacement);
    }

    [Fact]
    public void ApplyAndSummarizeRequiresExactDestinationLength()
    {
        var transform = new UniformTransform(new Point3(0f, 0f, 0f), 1f, new Point3(1f, 0f, 0f));
        var points = new[] { new Point3(0f, 0f, 0f), new Point3(1f, 0f, 0f) };

        Assert.Throws<ArgumentException>(() => transform.ApplyAndSummarize(points, new Point3[1]));
        Assert.Throws<ArgumentException>(() => transform.ApplyAndSummarize(points, new Point3[3]));
    }

    [Fact]
    public void SummarizeReportsIdentityExactly()
    {
        var transform = new UniformTransform(new Point3(5f, -3f, 2f), 1f, new Point3(0f, 0f, 0f));
        var points = new[]
        {
            new Point3(0f, 0f, 0f),
            new Point3(1f, 2f, 3f),
            new Point3(-4.5f, 8.25f, -16.125f),
        };

        var summary = transform.Summarize(points);

        Assert.Equal(0, summary.ChangedPointCount);
        Assert.Equal(0f, summary.MaximumDisplacement);
        Assert.Equal(summary.BeforeBounds, summary.AfterBounds);
    }

    [Fact]
    public void SummarizeReportsTranslationOnly()
    {
        var transform = new UniformTransform(new Point3(0f, 0f, 0f), 1f, new Point3(4f, 0f, -1f));
        var points = new[] { new Point3(0f, 0f, 0f), new Point3(1f, 2f, 3f) };

        var summary = transform.Summarize(points);

        Assert.Equal(2, summary.ChangedPointCount);
        Assert.Equal((float)Math.Sqrt(17.0), summary.MaximumDisplacement);
        Assert.Equal(new Bounds3(new Point3(0f, 0f, 0f), new Point3(1f, 2f, 3f)), summary.BeforeBounds);
        Assert.Equal(new Bounds3(new Point3(4f, 0f, -1f), new Point3(5f, 2f, 2f)), summary.AfterBounds);
    }

    [Fact]
    public void SummarizeReportsScaleOnlyWithStationaryPivotPoint()
    {
        var transform = new UniformTransform(new Point3(2f, 0f, 0f), 0.5f, new Point3(0f, 0f, 0f));
        var points = new[] { new Point3(2f, 0f, 0f), new Point3(0f, 1f, 0f) };

        var summary = transform.Summarize(points);

        Assert.Equal(1, summary.ChangedPointCount);
        Assert.Equal((float)Math.Sqrt(1.25), summary.MaximumDisplacement);
        Assert.Equal(new Bounds3(new Point3(0f, 0f, 0f), new Point3(2f, 1f, 0f)), summary.BeforeBounds);
        Assert.Equal(new Bounds3(new Point3(1f, 0f, 0f), new Point3(2f, 0.5f, 0f)), summary.AfterBounds);
    }

    [Fact]
    public void SummarizeComputesDeterministicBoundsAndMaximumDisplacement()
    {
        var transform = new UniformTransform(new Point3(0f, 0f, 0f), 2f, new Point3(1f, 1f, 1f));
        var points = new[] { new Point3(0f, 0f, 0f), new Point3(2f, 1f, 0f), new Point3(-1f, 0f, 4f) };

        var first = transform.Summarize(points);
        var second = transform.Summarize(points);

        Assert.Equal(new Bounds3(new Point3(-1f, 0f, 0f), new Point3(2f, 1f, 4f)), first.BeforeBounds);
        Assert.Equal(new Bounds3(new Point3(-1f, 1f, 1f), new Point3(5f, 3f, 9f)), first.AfterBounds);
        Assert.Equal((float)Math.Sqrt(26.0), first.MaximumDisplacement);
        Assert.Equal(3, first.ChangedPointCount);
        Assert.Equal(first, second);
    }

    [Fact]
    public void SummarizeFailsClosedWhenAResultComponentOverflows()
    {
        var transform = new UniformTransform(new Point3(0f, 0f, 0f), 4f, new Point3(0f, 0f, 0f));

        Assert.Throws<ArgumentException>(
            () => transform.Summarize([new Point3(float.MaxValue, 0f, 0f)]));
    }

    [Fact]
    public void SummarizeFailsClosedWhenDisplacementMagnitudeOverflows()
    {
        var transform = new UniformTransform(
            new Point3(0f, 0f, 0f),
            1f,
            new Point3(float.MaxValue, float.MaxValue, 0f));

        Assert.Throws<OverflowException>(
            () => transform.Summarize([new Point3(0f, 0f, 0f)]));
    }

    private static void AssertPointSequencesEqual(Point3[] expected, Point3[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].X, actual[index].X);
            Assert.Equal(expected[index].Y, actual[index].Y);
            Assert.Equal(expected[index].Z, actual[index].Z);
        }
    }
}
