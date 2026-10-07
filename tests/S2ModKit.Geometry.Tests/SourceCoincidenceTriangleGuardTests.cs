namespace S2ModKit.Geometry.Tests;

public sealed class SourceCoincidenceTriangleGuardTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(7)]
    public void ExistingCornerPartitionsMayMoveWhileStrictGuardStillRejects(int partition)
    {
        Point3[] source = partition switch
        {
            1 => [default, default, new(1, 0, 0)],
            2 => [new(1, 0, 0), default, default],
            4 => [default, new(1, 0, 0), default],
            _ => [default, default, default]
        };
        var output = source.Select(p => p + new Point3(3, 4, 5)).ToArray();
        var audit = SourceCoincidenceTriangleGuard.Validate(source, output, [0, 1, 2]);
        Assert.Equal([checked((byte)partition)], audit.SourcePartitions);
        Assert.Equal(1, audit.CollapsedTriangleCount);
        Assert.Equal(1, audit.TouchedCollapsedTriangleCount);
        Assert.Equal(0, audit.ValidTriangleCount);
        Assert.Throws<ArgumentException>(() => RegionTriangleGuard.Validate(source, output, [0, 1, 2]));
    }

    [Fact]
    public void SignedZeroIsCoincidentButChangedWordsAreStillCounted()
    {
        Point3[] source = [new(-0f, 0, 0), new(0, -0f, 0), new(float.Epsilon, 0, 0)];
        Point3[] output = [default, default, source[2]];
        var audit = SourceCoincidenceTriangleGuard.Validate(source, output, [0, 1, 2]);
        Assert.Equal((byte)1, audit.SourcePartitions[0]);
        Assert.Equal(1, audit.TouchedCollapsedTriangleCount);
    }

    [Fact]
    public void SubnormalValidFacesRetainExactOrientationBesideCollapsedFaces()
    {
        Point3[] source = [default, new(float.Epsilon, 0, 0), new(0, float.Epsilon, 0), default];
        var audit = SourceCoincidenceTriangleGuard.Validate(source, source, [0, 1, 2, 0, 3, 1]);
        Assert.Equal(1, audit.ValidTriangleCount);
        Assert.Equal(1, audit.CollapsedTriangleCount);
        Assert.Equal(0, audit.TouchedCollapsedTriangleCount);
        Assert.Equal(new byte[] { 0, 1 }, audit.SourcePartitions);
    }

    [Fact]
    public void CollinearDistinctSourceCornersRejectEvenForIdentityOutput()
    {
        Point3[] source = [default, new(1, 1, 1), new(2, 2, 2)];
        Assert.Throws<ArgumentException>(() => SourceCoincidenceTriangleGuard.Validate(source, source, [0, 1, 2]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void OpeningChangingAndMergingSourcePartitionReject(int defect)
    {
        Point3[] source = [default, default, new(1, 0, 0)];
        Point3[] output = defect switch
        {
            0 => [default, new(0, 1, 0), source[2]],
            1 => [default, new(1, 0, 0), new(1, 0, 0)],
            _ => [default, default, default]
        };
        Assert.Throws<ArgumentException>(() => SourceCoincidenceTriangleGuard.Validate(source, output, [0, 1, 2]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewCollapseAndValidFaceReversalStillReject(bool reversal)
    {
        Point3[] source = [default, new(1, 0, 0), new(0, 1, 0)];
        Point3[] output = reversal ? [source[0], source[2], source[1]] : [source[0], source[1], source[1]];
        Assert.Throws<ArgumentException>(() => SourceCoincidenceTriangleGuard.Validate(source, output, [0, 1, 2]));
    }

    [Fact]
    public void IncompleteOutOfRangeAndRepeatedIndexInventoriesReject()
    {
        Point3[] source = [default, default, new(1, 0, 0)];
        Assert.Throws<ArgumentException>(() => SourceCoincidenceTriangleGuard.Validate(source, source, [0, 1]));
        Assert.Throws<ArgumentException>(() => SourceCoincidenceTriangleGuard.Validate(source, source, [0, 1, 3]));
        Assert.Throws<ArgumentException>(() => SourceCoincidenceTriangleGuard.Validate(source, source, [0, 0, 2]));
        Assert.Throws<ArgumentException>(() => SourceCoincidenceTriangleGuard.Validate(source, source[..2], [0, 1, 2]));
    }
}
