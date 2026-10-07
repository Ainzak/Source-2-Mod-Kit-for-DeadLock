using System.Buffers.Binary;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Source2.Tests;

public sealed partial class DirectionalSourceBoundaryTests
{
    [Fact]
    public void PairedStoredWordsUseOriginalDispatchAndPreserveExteriorAndImmutableAttributes()
    {
        Point3[] points = [new(-6, 1, -0f), new(6, 1, -0f), new(-9, 1, -0f), new(9, 1, -0f), new(0, 0, -0f)];
        var bytes = Bytes(points); var before = bytes.ToArray();
        var a = new DirectionalEllipsoidScale(new(-6, 0, 0), new(4, 4, 4), .4f, new(1.5f, 1.25f, 1), 12);
        var b = new DirectionalEllipsoidScale(new(6, 0, 0), new(4, 4, 4), .4f, new(1.25f, 1.5f, 1), 12);
        var result = Source2CompiledModelAdapter.CalculatePairedWords(bytes, Position, Frame, points.Length, new(a, b), ["left", "right"], "buffer", 0);
        var reverse = Source2CompiledModelAdapter.CalculatePairedWords(bytes, Position, Frame, points.Length, new(b, a), ["right", "left"], "buffer", 0);
        Assert.Equal(before, bytes); Assert.Equal(result.Combined.Bytes, reverse.Combined.Bytes);
        Assert.Equal([4], result.Dispatch.PinnedIndices); Assert.Equal(2, result.Dispatch.Fields.Count);
        Assert.All(result.Dispatch.Fields, f => Assert.NotEmpty(f.ChangedPositionIndices));
        Assert.Equal(before.AsSpan(4 * 24, 24).ToArray(), result.Combined.Bytes.AsSpan(4 * 24, 24).ToArray());
        for (var i = 0; i < points.Length; i++)
        {
            Assert.Equal(before.AsSpan(i * 24 + 16, 8).ToArray(), result.Combined.Bytes.AsSpan(i * 24 + 16, 8).ToArray());
            Assert.Equal(0x80000000u, BinaryPrimitives.ReadUInt32LittleEndian(result.Combined.Bytes.AsSpan(i * 24 + 8)));
            var field = i is 0 or 2 ? a : b;
            if (i != 4) Assert.Equal(field.Evaluate(points[i]).Position, result.Combined.Points[i]);
        }
    }

    [Fact]
    public void PairedStoredWordsRejectNonFiniteSecondRegionInputWithoutChangingSource()
    {
        Point3[] points = [new(-6, 1, 0), new(6, 1, 0)]; var bytes = Bytes(points);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(24), float.PositiveInfinity);
        var before = bytes.ToArray();
        var a = new DirectionalEllipsoidScale(new(-6, 0, 0), new(4, 4, 4), .4f, new(1.5f, 1.25f, 1), 12);
        var b = new DirectionalEllipsoidScale(new(6, 0, 0), new(4, 4, 4), .4f, new(1.25f, 1.5f, 1), 12);
        Assert.Throws<ArgumentException>(() => Source2CompiledModelAdapter.CalculatePairedWords(bytes, Position, Frame, points.Length, new(a, b), ["left", "right"], "buffer", 0));
        Assert.Equal(before, bytes);
    }
}
