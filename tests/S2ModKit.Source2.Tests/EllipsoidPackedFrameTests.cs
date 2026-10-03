using S2ModKit.Adapters.Source2;
using S2ModKit.Geometry;

namespace S2ModKit.Source2.Tests;

public sealed class EllipsoidPackedFrameTests
{
    [Fact]
    public void TenThousandDifferentialFramesRoundTripThroughExistingCodec()
    {
        var field = new EllipsoidScale(default, new(8, 4, 2), 1f / 16, 2, 64);
        var random = new Random(70321);
        for (var i = 0; i < 10_000; i++)
        {
            var word = (uint)random.NextInt64(0, 1L << 32);
            var source = Source2PackedFrameCodec.Decode(word);
            var point = new Point3(random.Next(-512, 513) / 64f, random.Next(-512, 513) / 128f, random.Next(-512, 513) / 256f);
            var transformed = field.TransformFrame(point, source);
            if (field.Evaluate(point).Membership != EllipsoidMembership.Transition)
            {
                // A writer must copy this original word, including noncanonical aliases.
                Assert.Same(source, transformed);
                continue;
            }
            var encoded = Source2PackedFrameCodec.Encode(transformed);
            var reopened = Source2PackedFrameCodec.Decode(encoded);
            Assert.True(Dot(transformed.Normal, reopened.Normal) >= 0.9999);
            Assert.True(Dot(transformed.Tangent, reopened.Tangent) >= 0.9999);
            Assert.Equal(source.Handedness, reopened.Handedness);
        }
    }

    private static double Dot(Point3 a, Point3 b) => ((double)a.X * b.X) + ((double)a.Y * b.Y) + ((double)a.Z * b.Z);
}
