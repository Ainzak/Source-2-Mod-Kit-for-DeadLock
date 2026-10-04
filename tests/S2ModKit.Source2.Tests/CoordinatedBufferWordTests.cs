using System.Buffers.Binary;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Source2.Tests;

public sealed class CoordinatedBufferWordTests
{
    [Fact]
    public void CommonTiltedFieldRetainsPinnedRecordsFullFramesAndAllUnrelatedWords()
    {
        Point3[] points = [new(0, -0f, 0), new(2, 1, 2), new(4, 0, 4), new(2, 1, 2)];
        uint[] packed = [0xffffffff, 123456789, 0xffffffff, 987654321];
        var bytes = Enumerable.Repeat((byte)0xa5, points.Length * 28).ToArray();
        for (var v = 0; v < points.Length; v++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(v * 28), points[v].X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(v * 28 + 4), points[v].Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(v * 28 + 8), points[v].Z);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(v * 28 + 12), packed[v]);
        }
        var original = bytes.ToArray(); var math = Field(64);
        var result = Source2CompiledModelAdapter.CalculateCoordinatedWords(bytes, new("R32G32B32_FLOAT", 0, 28), new("R32_UINT", 12, 28, "source2_normal_tangent_v2"), points.Length, math);
        Assert.Equal(original, bytes);
        Assert.Equal(1, result.Full); Assert.Equal(2, result.Transition); Assert.Equal(1, result.Pinned);
        Assert.Equal(3, result.ChangedPositions); Assert.True(result.ChangedFrames > 0);
        Assert.Equal(original.AsSpan(0, 28).ToArray(), result.Bytes.AsSpan(0, 28).ToArray());
        Assert.Equal(packed[2], BinaryPrimitives.ReadUInt32LittleEndian(result.Bytes.AsSpan(68)));
        Assert.Equal(new Point3(6, 0, 6), result.Points[2]);
        Assert.Equal(result.Points[1], result.Points[3]);
        for (var v = 0; v < points.Length; v++) Assert.Equal(original.AsSpan(v * 28 + 16, 12).ToArray(), result.Bytes.AsSpan(v * 28 + 16, 12).ToArray());
        var repeated = Source2CompiledModelAdapter.CalculateCoordinatedWords(bytes, new("R32G32B32_FLOAT", 0, 28), new("R32_UINT", 12, 28, "source2_normal_tangent_v2"), points.Length, math);
        Assert.Equal(result.Bytes, repeated.Bytes); Assert.Equal(result.MaskHash, repeated.MaskHash); Assert.Equal(result.WeightHash, repeated.WeightHash);
    }

    [Fact]
    public void DisplacementAndMalformedStorageRejectWithoutChangingSource()
    {
        var bytes = new byte[28]; BinaryPrimitives.WriteSingleLittleEndian(bytes, 4); BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8), 4);
        var original = bytes.ToArray(); var p = new PositionLayout("R32G32B32_FLOAT", 0, 28); var f = new PackedFrameLayout("R32_UINT", 12, 28, "source2_normal_tangent_v2");
        Assert.Throws<ArgumentException>(() => Source2CompiledModelAdapter.CalculateCoordinatedWords(bytes, p, f, 1, Field(0.01f)));
        Assert.Equal(original, bytes);
        Assert.Throws<ArgumentException>(() => Source2CompiledModelAdapter.CalculateCoordinatedWords(new byte[27], p, f, 1, Field(64)));
        Assert.Throws<ArgumentException>(() => Source2CompiledModelAdapter.CalculateCoordinatedWords(bytes, p, f with { Offset = 4 }, 1, Field(64)));
    }

    private static CoordinatedFieldMath Field(float cap) => new(new CoordinatedTiltedRampField
    {
        Version = 1,
        CoordinateSpace = "model",
        FirstAxis = "x",
        FirstSign = 1,
        SecondAxis = "z",
        SecondSign = 1,
        PinnedThrough = 0,
        FullFrom = 8,
        Pivot = new(),
        UniformScale = 1.5f,
        NumericalPolicy = new("tilted_ramp_numeric", 1)
    }, cap);
}
