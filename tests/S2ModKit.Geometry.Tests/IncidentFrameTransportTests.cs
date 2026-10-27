using S2ModKit.Geometry;

namespace S2ModKit.Geometry.Tests;

public sealed class IncidentFrameTransportTests
{
    private static readonly Point3[] Patch = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0), new(9, 9, 9)];
    private static TangentFrame[] Frames(int count) => Enumerable.Repeat(new TangentFrame(new(0, 0, 1), new(1, 0, 0), 1), count).ToArray();

    [Fact]
    public void NoOpKeepsAuthoredFramesAndUnindexedRecords()
    {
        var frames = Frames(5); var result = IncidentFrameTransport.Evaluate(Patch, Patch, [0, 1, 2, 1, 3, 2], frames);
        Assert.Empty(result.Closure); Assert.Equal(frames, result.Frames);
    }

    [Fact]
    public void IndependentAnalyticPlaneOracleChecksOneThousandDeterministicMaps()
    {
        var random = new Random(3705); Point3[] source = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
        for (var k = 0; k < 1000; k++)
        {
            var u = new Point3((float)(.8 + random.NextDouble() * .4), (float)(random.NextDouble() * .2), (float)(random.NextDouble() * .4 - .2));
            var v = new Point3((float)(random.NextDouble() * .2), (float)(.8 + random.NextDouble() * .4), (float)(random.NextDouble() * .4 - .2));
            Point3[] target = [new(0, 0, 0), u, v];
            var result = IncidentFrameTransport.Evaluate(source, target, [0, 1, 2], Frames(3));
            // Direct target-edge cross and tangent, independent of matrix inverse/cofactor/averaging code.
            var cross = new double[] { (double)u.Y * v.Z - (double)u.Z * v.Y, (double)u.Z * v.X - (double)u.X * v.Z, (double)u.X * v.Y - (double)u.Y * v.X };
            var length = Math.Sqrt(cross.Sum(x => x * x)); var tangentLength = Math.Sqrt((double)u.X * u.X + (double)u.Y * u.Y + (double)u.Z * u.Z);
            foreach (var f in result.Frames)
            {
                Assert.InRange(Math.Abs(f.Normal.X - cross[0] / length) + Math.Abs(f.Normal.Y - cross[1] / length) + Math.Abs(f.Normal.Z - cross[2] / length), 0, 2e-6);
                Assert.InRange(Math.Abs(f.Tangent.X - u.X / tangentLength) + Math.Abs(f.Tangent.Y - u.Y / tangentLength) + Math.Abs(f.Tangent.Z - u.Z / tangentLength), 0, 2e-6);
                Assert.Equal(1, f.Handedness);
            }
        }
    }

    [Fact]
    public void StationaryNeighborsNeedFramesButUnindexedEditsReject()
    {
        var target = Patch.ToArray(); target[3] = new(1, 1, .3f);
        var result = IncidentFrameTransport.Evaluate(Patch, target, [0, 1, 2, 1, 3, 2], Frames(5));
        Assert.Equal([1, 2, 3], result.Closure); Assert.NotEqual(new Point3(0, 0, 1), result.Frames[1].Normal);
        Assert.Equal(Frames(5)[4], result.Frames[4]); target[4] = new(9, 9, 9.1f);
        Assert.Throws<ArgumentException>(() => IncidentFrameTransport.Evaluate(Patch, target, [0, 1, 2, 1, 3, 2], Frames(5)));
    }

    [Fact]
    public void SeparateCoincidentHardSeamFramesSurviveRigidRotation()
    {
        Point3[] p = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
        var frames = Frames(6); for (var i = 3; i < 6; i++) frames[i] = new(new(0, .6f, .8f), new(1, 0, 0), -1);
        Point3 Rotate(Point3 v) => new(-v.Y, v.X, v.Z);
        var result = IncidentFrameTransport.Evaluate(p, p.Select(Rotate).ToArray(), [0, 1, 2, 3, 4, 5], frames);
        for (var i = 0; i < 6; i++) { Assert.Equal(Rotate(frames[i].Normal), result.Frames[i].Normal); Assert.Equal(frames[i].Handedness, result.Frames[i].Handedness); }
    }

    [Fact]
    public void ExactGuardPreservesCollapsedPartitionsAndFixedDistinctCollinearFaces()
    {
        Point3[] p = [new(0, 0, 0), new(0, 0, 0), new(1, 0, 0), new(2, 0, 0)];
        Assert.Equal([false, false], PositionEditGuard.Validate(p, p, [0, 1, 2, 0, 2, 3]));
        var split = p.ToArray(); split[1] = new(.1f, 0, 0);
        Assert.Throws<ArgumentException>(() => PositionEditGuard.Validate(p, split, [0, 1, 2]));
        var moved = p.Select(v => new Point3(v.X, v.Y, v.Z + 1)).ToArray();
        Assert.Throws<ArgumentException>(() => PositionEditGuard.Validate(p, moved, [0, 2, 3]));
    }

    [Fact]
    public void ValidCollapseReversalExtremeConditionAndInvalidAuthoredFrameReject()
    {
        Point3[] p = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
        foreach (var q in new Point3[][] { [p[0], p[1], p[1]], [p[0], p[2], p[1]], [p[0], new(128, 0, 0), p[2]] })
            Assert.Throws<ArgumentException>(() => IncidentFrameTransport.Evaluate(p, q, [0, 1, 2], Frames(3)));
        var invalid = Frames(3); invalid[1] = invalid[1] with { Handedness = 0 };
        Assert.Throws<ArgumentException>(() => IncidentFrameTransport.Evaluate(p, p, [0, 1, 2], invalid));
    }
}
