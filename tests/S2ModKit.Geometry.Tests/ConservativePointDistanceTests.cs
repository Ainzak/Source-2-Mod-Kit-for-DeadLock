namespace S2ModKit.Geometry.Tests;

public sealed class ConservativePointDistanceTests
{
    [Fact]
    public void ExactDistancesAndSubnormalComponentsRoundOutwardWithoutPadding()
    {
        Assert.Equal(5, ConservativePointDistance.RoundUp(default, new(3, 4, 0)));
        Assert.Equal(0, ConservativePointDistance.RoundUp(new(-0f, 0, 0), default));
        Assert.Equal(float.Epsilon, ConservativePointDistance.RoundUp(default, new(float.Epsilon, 0, 0)));
        Assert.Equal(2 * float.Epsilon, ConservativePointDistance.RoundUp(default, new(float.Epsilon, float.Epsilon, float.Epsilon)));
        // Double subtraction/sqrt both lose this positive tail. The exact dyadic check
        // must not let a 64-unit cap accept a distance strictly greater than 64.
        Assert.Equal(MathF.BitIncrement(64), ConservativePointDistance.RoundUp(new(-float.Epsilon, 0, 0), new(64, 0, 0)));
        Assert.Equal(64, ConservativePointDistance.RoundUp(new(float.Epsilon, 0, 0), new(64, 0, 0)));
    }

    [Fact]
    public void LargeOriginDifferencesAreExactAndUnrepresentableDistancesReject()
    {
        var origin = MathF.ScaleB(1, 100);
        var adjacent = MathF.BitIncrement(origin);
        Assert.Equal(adjacent - origin, ConservativePointDistance.RoundUp(new(origin, origin, origin), new(adjacent, origin, origin)));
        Assert.Throws<ArgumentException>(() => ConservativePointDistance.RoundUp(new(-float.MaxValue, 0, 0), new(float.MaxValue, 0, 0)));
    }
}
