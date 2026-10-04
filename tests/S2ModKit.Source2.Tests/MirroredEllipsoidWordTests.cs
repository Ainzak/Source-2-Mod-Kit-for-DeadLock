using System.Buffers.Binary;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Source2.Tests;

public sealed class MirroredEllipsoidWordTests
{
    [Fact]
    public void PairedMasksRetainPinnedWordsAndRequireEffectsOnBothSides()
    {
        Point3[] points = [new(4.125f, 0, 0), new(-4.125f, 0, 0), new(6, 1, 0), new(-6, 1, 0), new(-0f, -0f, -0f)];
        var bytes = Enumerable.Repeat((byte)0xa5, points.Length * 28).ToArray();
        for (var i = 0; i < points.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 28), points[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 28 + 4), points[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 28 + 8), points[i].Z);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 28 + 12), 123456789);
        }
        var field = new MirroredEllipsoidScale(new(4, 0, 0), new(4, 4, 4), 1f / 16, 1.25f, 64, 0, 0);
        var position = new PositionLayout("R32G32B32_FLOAT", 0, 28); var frame = new PackedFrameLayout("R32_UINT", 12, 28, "source2_normal_tangent_v2");
        var result = Source2CompiledModelAdapter.CalculateEllipsoidWords(bytes, position, frame, points.Length, field);
        Assert.Equal(2, result.MirroredMasks!.Count); Assert.All(result.MirroredMasks, m => Assert.Equal(2, m.ChangedPositionCount));
        Assert.NotEqual(result.MirroredMasks[0].MaskHash, result.MirroredMasks[1].MaskHash);
        Assert.Equal(-result.Points[0].X, result.Points[1].X); Assert.Equal(-result.Points[2].X, result.Points[3].X);
        Assert.Equal(bytes.AsSpan(4 * 28, 28).ToArray(), result.Bytes.AsSpan(4 * 28, 28).ToArray());
        for (var i = 0; i < points.Length; i++) Assert.Equal(bytes.AsSpan(i * 28 + 16, 12).ToArray(), result.Bytes.AsSpan(i * 28 + 16, 12).ToArray());
        var error = Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.CalculateEllipsoidWords(bytes.AsSpan(0, 28), position, frame, 1, field));
        Assert.Equal("ELLIPSOID_EMPTY_LOD_EFFECT", error.Error.Code);
    }
}
