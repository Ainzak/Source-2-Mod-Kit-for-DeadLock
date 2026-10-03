using System.Buffers.Binary;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Source2.Tests;

public sealed class EllipsoidBufferWordTests
{
    [Fact]
    public void CompleteRecordsRetainPinnedSignedZeroCoreFramesAndUnrelatedWordsWithoutWelding()
    {
        var position = new PositionLayout("R32G32B32_FLOAT", 0, 28);
        var frame = new PackedFrameLayout("R32_UINT", 12, 28, "source2_normal_tangent_v2");
        Point3[] points = [new(0.25f, 0, 0), new(0, 5, 0), new(10, -0f, 0), new(11, 0, 0), new(0, 5, 0)];
        uint[] packed = [0xffffffff, 123456789, 0xffffffff, 0, 987654321];
        var original = Enumerable.Repeat((byte)0xa5, points.Length * 28).ToArray();
        for (var v = 0; v < points.Length; v++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(original.AsSpan(v * 28), points[v].X);
            BinaryPrimitives.WriteSingleLittleEndian(original.AsSpan(v * 28 + 4), points[v].Y);
            BinaryPrimitives.WriteSingleLittleEndian(original.AsSpan(v * 28 + 8), points[v].Z);
            BinaryPrimitives.WriteUInt32LittleEndian(original.AsSpan(v * 28 + 12), packed[v]);
        }
        var before = original.ToArray();
        var math = new EllipsoidScale(default, new(10, 10, 10), 1f / 16, 2, 64);
        var result = Source2CompiledModelAdapter.CalculateEllipsoidWords(original, position, frame, points.Length, math);
        Assert.Equal(before, original);
        Assert.Equal(1, result.Core); Assert.Equal(2, result.Transition); Assert.Equal(2, result.Pinned);
        Assert.Equal(3, result.ChangedPositions);
        Assert.True(result.ChangedFrames > 0);
        Assert.Equal(0.5f, result.Points[0].X);
        Assert.Equal(packed[0], BinaryPrimitives.ReadUInt32LittleEndian(result.Bytes.AsSpan(12)));
        Assert.Equal(original.AsSpan(56, 56).ToArray(), result.Bytes.AsSpan(56, 56).ToArray());
        Assert.Equal(0x80000000u, BinaryPrimitives.ReadUInt32LittleEndian(result.Bytes.AsSpan(60)));
        Assert.Equal(result.Points[1], result.Points[4]);
        Assert.NotEqual(BinaryPrimitives.ReadUInt32LittleEndian(result.Bytes.AsSpan(40)), BinaryPrimitives.ReadUInt32LittleEndian(result.Bytes.AsSpan(124)));
        for (var v = 0; v < points.Length; v++)
            Assert.Equal(original.AsSpan(v * 28 + 16, 12).ToArray(), result.Bytes.AsSpan(v * 28 + 16, 12).ToArray());
        var again = Source2CompiledModelAdapter.CalculateEllipsoidWords(original, position, frame, points.Length, math);
        Assert.Equal(result.Bytes, again.Bytes);
        Assert.Equal(result.MaskHash, again.MaskHash);
        Assert.Equal(result.WeightHash, again.WeightHash);
        Assert.Equal(result.MaximumDisplacement, again.MaximumDisplacement);
        RegionTriangleGuard.Validate(points, result.Points, [0, 1, 2, 0, 4, 3]);
    }

    [Fact]
    public void StoredDisplacementFailureReturnsNoMutableResultAndTruncatedBufferRejects()
    {
        var bytes = new byte[28];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, 5);
        var original = bytes.ToArray();
        var position = new PositionLayout("R32G32B32_FLOAT", 0, 28);
        var frame = new PackedFrameLayout("R32_UINT", 12, 28, "source2_normal_tangent_v2");
        var field = new EllipsoidScale(default, new(10, 10, 10), 1f / 16, 2, 0.001f);
        var error = Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.CalculateEllipsoidWords(bytes, position, frame, 1, field));
        Assert.Equal("TRANSFORM_DISPLACEMENT_EXCEEDED", error.Error.Code);
        Assert.Equal(original, bytes);
        Assert.Throws<ArgumentException>(() => Source2CompiledModelAdapter.CalculateEllipsoidWords(new byte[27], position, frame, 1, field));
    }
}
