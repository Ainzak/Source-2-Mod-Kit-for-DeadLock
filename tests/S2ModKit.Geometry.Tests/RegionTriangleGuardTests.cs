namespace S2ModKit.Geometry.Tests;

public sealed class RegionTriangleGuardTests
{
    [Fact]
    public void OrdinaryAndSubnormalOrientedTrianglesPassExactly()
    {
        Point3[] points = [default, new(1, 0, 0), new(0, 1, 0)];
        RegionTriangleGuard.Validate(points, points.Select(p => p * 2).ToArray(), [0, 1, 2]);
        Point3[] tiny = [default, new(float.Epsilon, 0, 0), new(0, float.Epsilon, 0)];
        RegionTriangleGuard.Validate(tiny, tiny, [0, 1, 2]);
    }

    [Fact]
    public void DegenerateReversedAndIncompleteInventoriesReject()
    {
        Point3[] source = [default, new(1, 0, 0), new(0, 1, 0)];
        Assert.Throws<ArgumentException>(() => RegionTriangleGuard.Validate(source, [source[0], source[1], source[1]], [0, 1, 2]));
        Assert.Throws<ArgumentException>(() => RegionTriangleGuard.Validate(source, [source[0], source[2], source[1]], [0, 1, 2]));
        Assert.Throws<ArgumentException>(() => RegionTriangleGuard.Validate(source, source, [0, 1]));
        Assert.Throws<ArgumentException>(() => RegionTriangleGuard.Validate(source, source, [0, 1, 3]));
        Assert.Throws<ArgumentException>(() => RegionTriangleGuard.Validate([source[0], source[0], source[2]], source, [0, 1, 2]));
    }
}
