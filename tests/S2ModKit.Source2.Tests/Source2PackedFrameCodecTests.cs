using System.Buffers.Binary;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Source2.Tests;

public sealed class Source2PackedFrameCodecTests
{
    [Fact]
    public void CanonicalPositiveZFrameHasStableEncoding()
    {
        var frame = new TangentFrame(
            new Point3(0f, 0f, 1f),
            new Point3(1f, 0f, 0f),
            1f);

        var packed = Source2PackedFrameCodec.Encode(frame);
        var reopened = Source2PackedFrameCodec.Decode(packed);

        Assert.Equal(0x80200fffu, packed);
        Assert.True(Dot(frame.Normal, reopened.Normal) > 0.9999f);
        Assert.True(Dot(frame.Tangent, reopened.Tangent) > 0.9999f);
        Assert.Equal(1f, reopened.Handedness);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(0x12345678u)]
    [InlineData(0x80200001u)]
    [InlineData(uint.MaxValue)]
    public void DecodeEncodeRoundTripRetainsCharacterizedPackedWord(uint packed)
    {
        var decoded = Source2PackedFrameCodec.Decode(packed);

        var encoded = Source2PackedFrameCodec.Encode(decoded);
        var reopened = Source2PackedFrameCodec.Decode(encoded);

        Assert.True(Dot(decoded.Normal, reopened.Normal) > 0.9999f);
        Assert.True(Dot(decoded.Tangent, reopened.Tangent) > 0.9999f);
        Assert.Equal(decoded.Handedness, reopened.Handedness);
    }

    [Fact]
    public void PerAxisTransformChangesOnlySelectedPackedFrame()
    {
        var layout = new PackedFrameLayout("R32_UINT", 16, 24, Source2PackedFrameCodec.EncodingProfile);
        var bytes = new byte[48];
        var initial = Source2PackedFrameCodec.Encode(new TangentFrame(
            Normalize(new Point3(1f, 1f, 1f)),
            Normalize(new Point3(1f, -1f, 0f)),
            -1f));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), initial);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), initial);
        var transform = new AffineTransform(
            new Point3(0f, 0f, 0f),
            new AffineScale(2f, 1f, 0.5f),
            AxisAngleRotation.Identity,
            RigidFrame.Model,
            new Point3(0f, 0f, 0f));

        Source2PackedFrameCodec.TransformSelected(bytes, layout, [1], transform);

        Assert.Equal(initial, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
        Assert.NotEqual(initial, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
        Assert.Equal(
            ContentHash.Compute(bytes.AsSpan(40, sizeof(uint))),
            Source2PackedFrameCodec.HashSelected(bytes, layout, [1]));
    }

    [Fact]
    public void InvalidFrameFailsClosed()
    {
        var exception = Assert.Throws<S2ModKitException>(() => Source2PackedFrameCodec.Encode(
            new TangentFrame(new Point3(1f, 0f, 0f), new Point3(1f, 0f, 0f), 1f)));

        Assert.Equal("AFFINE_PACKED_FRAME_REENCODE_MISMATCH", exception.Error.Code);
    }

    private static Point3 Normalize(Point3 value)
    {
        var inverse = 1f / MathF.Sqrt((value.X * value.X) + (value.Y * value.Y) + (value.Z * value.Z));
        return new Point3(value.X * inverse, value.Y * inverse, value.Z * inverse);
    }

    private static float Dot(Point3 left, Point3 right) =>
        (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);
}
