using S2ModKit.Adapters.Source2;
using S2ModKit.Geometry;

namespace S2ModKit.Source2.Tests;

public sealed class DirectionalEllipsoidPackedFrameTests
{
    [Fact]
    public void TenThousandCoreTransitionAndPinnedFramesRetainTheCharacterizedCodecGates()
    {
        var field = new DirectionalEllipsoidScale(default, new(10, 8, 4), .4f, new(1.5f, 1.5f, 1), 64);
        var random = new Random(93405);
        var counts = new int[3];
        for (var i = 0; i < 10_000; i++)
        {
            var sourceWord = (uint)random.NextInt64(0, 1L << 32);
            var source = Source2PackedFrameCodec.Decode(sourceWord);
            var point = (i % 3) switch
            {
                0 => new Point3(random.Next(-128, 129) / 128f, random.Next(-128, 129) / 128f, .25f),
                1 => new Point3(5, random.Next(-128, 129) / 64f, 1),
                _ => new Point3(10, 8, 4)
            };
            var membership = field.Evaluate(point).Membership;
            var transformed = field.TransformFrame(point, source);
            counts[(int)membership]++;
            if (membership == EllipsoidMembership.Pinned)
            {
                Assert.Same(source, transformed); // writer copies sourceWord; never canonicalizes aliases
                continue;
            }
            var packed = Source2PackedFrameCodec.Encode(transformed);
            var independentlyDecoded = Source2PackedFrameCodec.Decode(packed);
            Assert.True(Dot(transformed.Normal, independentlyDecoded.Normal) >= .9999);
            Assert.True(Dot(transformed.Tangent, independentlyDecoded.Tangent) >= .9999);
            Assert.Equal(source.Handedness, independentlyDecoded.Handedness);
        }
        Assert.True(counts.All(count => count > 3000));
    }

    [Fact]
    public void PositionOnlyProtectionWouldMissChangedPackedCoreWords()
    {
        var field = new DirectionalEllipsoidScale(default, new(10, 8, 4), .4f, new(1.5f, 1.5f, 1), 64);
        var sourceWord = 0x9abcde01u;
        var source = Source2PackedFrameCodec.Decode(sourceWord);
        Assert.Equal(default, field.Evaluate(default).Position);
        var outputWord = Source2PackedFrameCodec.Encode(field.TransformFrame(default, source));
        Assert.NotEqual(sourceWord, outputWord);
        Assert.Equal(source.Handedness, Source2PackedFrameCodec.Decode(outputWord).Handedness);
    }

    private static double Dot(Point3 a, Point3 b) => (((double)a.X * b.X) + ((double)a.Y * b.Y)) + ((double)a.Z * b.Z);
}
