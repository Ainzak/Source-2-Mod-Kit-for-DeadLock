using S2ModKit.Adapters.Source2;
using S2ModKit.Geometry;

namespace S2ModKit.Source2.Tests;

public sealed class PositionImportPackedFrameTests
{
    [Fact]
    public void IncidentTransportRetainsHandednessWithinNativeCodecErrorAtSeamsAndAliases()
    {
        Point3[] source = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
        Point3[] target = [new(0, 0, 0), new(1.1f, 0, .1f), new(0, .9f, .2f)];
        var random = new Random(70371);
        for (var i = 0; i < 1024; i++)
        {
            var word = (uint)random.NextInt64(0, 1L << 32); var frame = Source2PackedFrameCodec.Decode(word);
            var unchanged = IncidentFrameTransport.Evaluate(source, source, [0, 1, 2], [frame, frame, frame]);
            Assert.Empty(unchanged.Closure); // caller bypasses encoding and retains the original word
            var changed = IncidentFrameTransport.Evaluate(source, target, [0, 1, 2], [frame, frame, frame]);
            foreach (var expected in changed.Frames)
            {
                var decoded = Source2PackedFrameCodec.Decode(Source2PackedFrameCodec.Encode(expected));
                Assert.Equal(frame.Handedness, decoded.Handedness);
                Assert.True(Dot(expected.Normal, decoded.Normal) >= .9999);
                Assert.True(Dot(expected.Tangent, decoded.Tangent) >= .9999);
            }
        }
    }
    private static double Dot(Point3 a, Point3 b) => ((double)a.X * b.X + (double)a.Y * b.Y) + (double)a.Z * b.Z;
}
