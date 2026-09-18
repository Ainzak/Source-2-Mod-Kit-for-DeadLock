using System.Buffers.Binary;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Source2.Tests;

public sealed class DecodedPositionBufferTransformerTests
{
    [Fact]
    public void ApplyTransformsSelectedPositionsAtStrideTwentyEight()
    {
        var input = Records(28, 0, (0f, 0f, 0f), (10f, 0f, 0f), (20f, 0f, 0f), (30f, 0f, 0f));

        var result = DecodedPositionBufferTransformer.Apply(
            input,
            4,
            new PositionLayout("R32G32B32_FLOAT", 0, 28),
            [1, 2],
            new UniformTransform(new Point3(1f, 0f, 0f), 2f, new Point3(0f, 0f, 0f)));

        var expected = Records(28, 0, (0f, 0f, 0f), (19f, 0f, 0f), (39f, 0f, 0f), (30f, 0f, 0f));
        Assert.Equal(expected, result.TransformedDecoded.ToArray());
        Assert.Equal(2, result.SelectedVertexCount);
        Assert.Equal(ContentHash.Compute(input), result.InputHash);
        Assert.Equal(ContentHash.Compute(expected), result.OutputHash);
        Assert.Equal(new ContentHash(VertexSetHash.Compute([1, 2])), result.SelectedVertexSetHash);
        Assert.Equal(10f, result.BoundsBefore.Min.X);
        Assert.Equal(20f, result.BoundsBefore.Max.X);
        Assert.Equal(19f, result.BoundsAfter.Min.X);
        Assert.Equal(39f, result.BoundsAfter.Max.X);
        Assert.Equal(2, result.ChangedVertexCount);
        Assert.Equal(19f, result.MaximumDisplacement);
    }

    [Fact]
    public void ApplyCanonicalizesSelectionOrderAndDuplicates()
    {
        var input = Records(28, 0, (0f, 0f, 0f), (10f, 0f, 0f), (20f, 0f, 0f));
        var transform = new UniformTransform(new Point3(0f, 0f, 0f), 1f, new Point3(1f, 0f, 0f));
        var layout = new PositionLayout("R32G32B32_FLOAT", 0, 28);

        var result = DecodedPositionBufferTransformer.Apply(input, 3, layout, [2, 0, 2, 1, 0], transform);
        var canonical = DecodedPositionBufferTransformer.Apply(input, 3, layout, [0, 1, 2], transform);

        Assert.Equal(canonical.TransformedDecoded.ToArray(), result.TransformedDecoded.ToArray());
        Assert.Equal(canonical.InputHash, result.InputHash);
        Assert.Equal(canonical.OutputHash, result.OutputHash);
        Assert.Equal(canonical.SelectedVertexSetHash, result.SelectedVertexSetHash);
        Assert.Equal(canonical.SelectedVertexCount, result.SelectedVertexCount);
        Assert.Equal(canonical.BoundsBefore, result.BoundsBefore);
        Assert.Equal(canonical.BoundsAfter, result.BoundsAfter);
        Assert.Equal(canonical.ChangedVertexCount, result.ChangedVertexCount);
        Assert.Equal(canonical.MaximumDisplacement, result.MaximumDisplacement);
        Assert.Equal(3, result.SelectedVertexCount);
        Assert.Equal(new ContentHash(VertexSetHash.Compute([0, 1, 2])), result.SelectedVertexSetHash);
        Assert.Equal(3, result.ChangedVertexCount);
    }

    [Fact]
    public void ApplyLeavesCallerBytesAndSelectionUnchangedWithoutAliasing()
    {
        var input = Records(28, 0, (0f, 0f, 0f), (10f, 0f, 0f), (20f, 0f, 0f), (30f, 0f, 0f));
        var inputSnapshot = input.ToArray();
        int[] selection = [3, 1];

        var result = DecodedPositionBufferTransformer.Apply(
            input,
            4,
            new PositionLayout("R32G32B32_FLOAT", 0, 28),
            selection,
            new UniformTransform(new Point3(0f, 0f, 0f), 1f, new Point3(5f, 0f, 0f)));

        Assert.Equal(inputSnapshot, input);
        Assert.Equal([3, 1], selection);
        input[27] = 0xEE;
        Assert.Equal(
            Records(28, 0, (0f, 0f, 0f), (15f, 0f, 0f), (20f, 0f, 0f), (35f, 0f, 0f)),
            result.TransformedDecoded.ToArray());
    }

    [Fact]
    public void ApplyPreservesUnselectedRecordsAndSelectedNonPositionBytes()
    {
        var input = Records(28, 4, (0f, 0f, 0f), (10f, 0f, 0f), (20f, 0f, 0f), (30f, 0f, 0f));

        var result = DecodedPositionBufferTransformer.Apply(
            input,
            4,
            new PositionLayout("R32G32B32_FLOAT", 4, 28),
            [1],
            new UniformTransform(new Point3(0f, 0f, 0f), 1f, new Point3(1f, 0f, 0f)));

        var output = result.TransformedDecoded.ToArray();
        foreach (var vertex in new[] { 0, 2, 3 })
        {
            Assert.Equal(input[(vertex * 28)..((vertex + 1) * 28)], output[(vertex * 28)..((vertex + 1) * 28)]);
        }

        Assert.Equal(input[28..32], output[28..32]);
        Assert.Equal(input[44..56], output[44..56]);
        var expected = Records(28, 4, (0f, 0f, 0f), (11f, 0f, 0f), (20f, 0f, 0f), (30f, 0f, 0f));
        Assert.Equal(expected[32..44], output[32..44]);
    }

    [Fact]
    public void ApplySupportsStrideFortyFourWithAlignedPositionOffset()
    {
        var input = Records(44, 12, (1f, 2f, 3f), (4f, 5f, 6f), (7f, 8f, 9f));

        var result = DecodedPositionBufferTransformer.Apply(
            input,
            3,
            new PositionLayout("R32G32B32_FLOAT", 12, 44),
            [0, 1, 2],
            new UniformTransform(new Point3(0f, 0f, 0f), 1f, new Point3(4f, 0f, -1f)));

        Assert.Equal(
            Records(44, 12, (5f, 2f, 2f), (8f, 5f, 5f), (11f, 8f, 8f)),
            result.TransformedDecoded.ToArray());
        Assert.Equal(3, result.SelectedVertexCount);
        Assert.Equal(3, result.ChangedVertexCount);
        Assert.Equal((float)Math.Sqrt(17.0), result.MaximumDisplacement);
        Assert.Equal(1f, result.BoundsBefore.Min.X);
        Assert.Equal(7f, result.BoundsBefore.Max.X);
        Assert.Equal(5f, result.BoundsAfter.Min.X);
        Assert.Equal(11f, result.BoundsAfter.Max.X);
    }

    [Fact]
    public void ApplyReportsIdentityTransformAsUnchangedBytesAndFacts()
    {
        var input = Records(28, 0, (1.5f, -2.25f, 0.5f), (100f, 0f, -7.75f));

        var result = DecodedPositionBufferTransformer.Apply(
            input,
            2,
            new PositionLayout("R32G32B32_FLOAT", 0, 28),
            [0, 1],
            new UniformTransform(new Point3(0f, 0f, 0f), 1f, new Point3(0f, 0f, 0f)));

        Assert.Equal(input, result.TransformedDecoded.ToArray());
        Assert.Equal(result.InputHash, result.OutputHash);
        Assert.Equal(0, result.ChangedVertexCount);
        Assert.Equal(0f, result.MaximumDisplacement);
        Assert.Equal(result.BoundsBefore, result.BoundsAfter);
        Assert.Equal(1.5f, result.BoundsBefore.Min.X);
        Assert.Equal(100f, result.BoundsBefore.Max.X);
    }

    [Fact]
    public void ApplyReportsTranslationOnly()
    {
        var input = Records(28, 0, (1f, 2f, 3f), (0f, 0f, 0f));

        var result = DecodedPositionBufferTransformer.Apply(
            input,
            2,
            new PositionLayout("R32G32B32_FLOAT", 0, 28),
            [0, 1],
            new UniformTransform(new Point3(0f, 0f, 0f), 1f, new Point3(4f, 0f, -1f)));

        Assert.Equal(Records(28, 0, (5f, 2f, 2f), (4f, 0f, -1f)), result.TransformedDecoded.ToArray());
        Assert.Equal(2, result.ChangedVertexCount);
        Assert.Equal((float)Math.Sqrt(17.0), result.MaximumDisplacement);
        Assert.Equal(0f, result.BoundsBefore.Min.X);
        Assert.Equal(1f, result.BoundsBefore.Max.X);
        Assert.Equal(4f, result.BoundsAfter.Min.X);
        Assert.Equal(5f, result.BoundsAfter.Max.X);
    }

    [Fact]
    public void ApplyReportsScaleAroundNonZeroPivotWithStationaryPoint()
    {
        var input = Records(28, 0, (1f, 0f, 0f), (3f, 0f, 0f));

        var result = DecodedPositionBufferTransformer.Apply(
            input,
            2,
            new PositionLayout("R32G32B32_FLOAT", 0, 28),
            [0, 1],
            new UniformTransform(new Point3(1f, 0f, 0f), 2f, new Point3(0f, 0f, 0f)));

        Assert.Equal(Records(28, 0, (1f, 0f, 0f), (5f, 0f, 0f)), result.TransformedDecoded.ToArray());
        Assert.Equal(1, result.ChangedVertexCount);
        Assert.Equal(2f, result.MaximumDisplacement);
        Assert.Equal(1f, result.BoundsBefore.Min.X);
        Assert.Equal(3f, result.BoundsBefore.Max.X);
        Assert.Equal(1f, result.BoundsAfter.Min.X);
        Assert.Equal(5f, result.BoundsAfter.Max.X);
    }

    [Fact]
    public void ApplyTransformsFirstAndLastSelectedVertices()
    {
        var input = Records(
            28,
            0,
            (0f, 0f, 0f),
            (1f, 0f, 0f),
            (2f, 0f, 0f),
            (3f, 0f, 0f),
            (4f, 0f, 0f));

        var result = DecodedPositionBufferTransformer.Apply(
            input,
            5,
            new PositionLayout("R32G32B32_FLOAT", 0, 28),
            [0, 4],
            new UniformTransform(new Point3(0f, 0f, 0f), 1f, new Point3(10f, 0f, 0f)));

        var output = result.TransformedDecoded.ToArray();
        Assert.Equal(Records(28, 0, (10f, 0f, 0f), (1f, 0f, 0f), (2f, 0f, 0f), (3f, 0f, 0f), (14f, 0f, 0f)), output);
        foreach (var vertex in new[] { 1, 2, 3 })
        {
            Assert.Equal(input[(vertex * 28)..((vertex + 1) * 28)], output[(vertex * 28)..((vertex + 1) * 28)]);
        }

        Assert.Equal(2, result.SelectedVertexCount);
        Assert.Equal(2, result.ChangedVertexCount);
        Assert.Equal(10f, result.MaximumDisplacement);
        Assert.Equal(0f, result.BoundsBefore.Min.X);
        Assert.Equal(4f, result.BoundsBefore.Max.X);
        Assert.Equal(10f, result.BoundsAfter.Min.X);
        Assert.Equal(14f, result.BoundsAfter.Max.X);
    }

    [Theory]
    [InlineData("R32G32B32A32_FLOAT")]
    [InlineData("r32g32b32_float")]
    [InlineData("")]
    public void ApplyRejectsUnsupportedPositionFormat(string format)
    {
        var input = Records(28, 0, (0f, 0f, 0f), (1f, 0f, 0f));

        Assert.Throws<ArgumentException>(() => DecodedPositionBufferTransformer.Apply(
            input,
            2,
            new PositionLayout(format, 0, 28),
            [0],
            TranslateX()));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(260)]
    [InlineData(14)]
    public void ApplyRejectsInvalidStride(int stride)
    {
        var input = new byte[checked(2 * stride)];

        Assert.Throws<ArgumentException>(() => DecodedPositionBufferTransformer.Apply(
            input,
            2,
            new PositionLayout("R32G32B32_FLOAT", 0, stride),
            [0],
            TranslateX()));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(17)]
    public void ApplyRejectsInvalidPositionOffset(int positionOffset)
    {
        var input = Records(28, 0, (0f, 0f, 0f), (1f, 0f, 0f));

        Assert.Throws<ArgumentException>(() => DecodedPositionBufferTransformer.Apply(
            input,
            2,
            new PositionLayout("R32G32B32_FLOAT", positionOffset, 28),
            [0],
            TranslateX()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(27)]
    [InlineData(57)]
    public void ApplyRejectsBufferLengthMismatch(int length)
    {
        var input = new byte[length];

        Assert.Throws<ArgumentException>(() => DecodedPositionBufferTransformer.Apply(
            input,
            2,
            new PositionLayout("R32G32B32_FLOAT", 0, 28),
            [0],
            TranslateX()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ApplyRejectsNonPositiveVertexCount(int vertexCount)
    {
        var input = Records(28, 0, (0f, 0f, 0f), (1f, 0f, 0f));

        Assert.Throws<ArgumentOutOfRangeException>(() => DecodedPositionBufferTransformer.Apply(
            input,
            vertexCount,
            new PositionLayout("R32G32B32_FLOAT", 0, 28),
            [0],
            TranslateX()));
    }

    [Fact]
    public void ApplyRejectsEmptySelection()
    {
        var input = Records(28, 0, (0f, 0f, 0f), (1f, 0f, 0f));

        Assert.Throws<ArgumentException>(() => DecodedPositionBufferTransformer.Apply(
            input,
            2,
            new PositionLayout("R32G32B32_FLOAT", 0, 28),
            [],
            TranslateX()));
        Assert.Throws<ArgumentNullException>(() => DecodedPositionBufferTransformer.Apply(
            input,
            2,
            new PositionLayout("R32G32B32_FLOAT", 0, 28),
            null!,
            TranslateX()));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(99)]
    public void ApplyRejectsOutOfRangeSelectedIndices(int vertex)
    {
        var input = Records(28, 0, (0f, 0f, 0f), (1f, 0f, 0f));

        Assert.Throws<ArgumentOutOfRangeException>(() => DecodedPositionBufferTransformer.Apply(
            input,
            2,
            new PositionLayout("R32G32B32_FLOAT", 0, 28),
            [vertex],
            TranslateX()));
    }

    [Fact]
    public void ApplyRejectsNullLayoutAndTransform()
    {
        var input = Records(28, 0, (0f, 0f, 0f), (1f, 0f, 0f));

        Assert.Throws<ArgumentNullException>(() => DecodedPositionBufferTransformer.Apply(
            input,
            2,
            null!,
            [0],
            TranslateX()));
        Assert.Throws<ArgumentNullException>(() => DecodedPositionBufferTransformer.Apply(
            input,
            2,
            new PositionLayout("R32G32B32_FLOAT", 0, 28),
            [0],
            null!));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ApplyFailsClosedOnNonFiniteSelectedSourcePosition(float component)
    {
        var input = Records(28, 0, (component, 0f, 0f), (1f, 0f, 0f));

        Assert.Throws<ArgumentException>(() => DecodedPositionBufferTransformer.Apply(
            input,
            2,
            new PositionLayout("R32G32B32_FLOAT", 0, 28),
            [0, 1],
            TranslateX()));
    }

    [Fact]
    public void ApplyIgnoresNonFiniteUnselectedPosition()
    {
        var input = Records(28, 0, (float.NaN, 0f, 0f), (1f, 0f, 0f));

        var result = DecodedPositionBufferTransformer.Apply(
            input,
            2,
            new PositionLayout("R32G32B32_FLOAT", 0, 28),
            [1],
            TranslateX());

        Assert.Equal(Records(28, 0, (float.NaN, 0f, 0f), (2f, 0f, 0f)), result.TransformedDecoded.ToArray());
    }

    [Fact]
    public void ApplyFailsClosedWhenTransformedPointOverflows()
    {
        var input = Records(28, 0, (1e38f, 0f, 0f), (1f, 0f, 0f));

        Assert.Throws<ArgumentException>(() => DecodedPositionBufferTransformer.Apply(
            input,
            2,
            new PositionLayout("R32G32B32_FLOAT", 0, 28),
            [0],
            new UniformTransform(new Point3(0f, 0f, 0f), 4f, new Point3(0f, 0f, 0f))));
    }

    [Fact]
    public void ApplyFailsClosedWhenDisplacementMagnitudeOverflows()
    {
        var input = Records(28, 0, (0f, 0f, 0f), (1f, 0f, 0f));

        Assert.Throws<OverflowException>(() => DecodedPositionBufferTransformer.Apply(
            input,
            2,
            new PositionLayout("R32G32B32_FLOAT", 0, 28),
            [0],
            new UniformTransform(
                new Point3(0f, 0f, 0f),
                1f,
                new Point3(float.MaxValue, float.MaxValue, 0f))));
    }

    [Fact]
    public void ApplyLeavesCallerBytesUnchangedWhenItThrows()
    {
        var input = Records(28, 0, (1e38f, 0f, 0f), (1f, 0f, 0f), (2f, 0f, 0f));
        var snapshot = input.ToArray();
        var layout = new PositionLayout("R32G32B32_FLOAT", 0, 28);
        var overflowing = new UniformTransform(new Point3(0f, 0f, 0f), 4f, new Point3(0f, 0f, 0f));

        Assert.Throws<ArgumentOutOfRangeException>(() => DecodedPositionBufferTransformer.Apply(
            input,
            3,
            layout,
            [99],
            TranslateX()));
        Assert.Throws<ArgumentException>(() => DecodedPositionBufferTransformer.Apply(
            input,
            3,
            layout,
            [0, 1],
            overflowing));
        Assert.Throws<OverflowException>(() => DecodedPositionBufferTransformer.Apply(
            input,
            3,
            layout,
            [2],
            new UniformTransform(
                new Point3(0f, 0f, 0f),
                1f,
                new Point3(float.MaxValue, float.MaxValue, 0f))));
        Assert.Equal(snapshot, input);
    }

    private static UniformTransform TranslateX() => new(new Point3(0f, 0f, 0f), 1f, new Point3(1f, 0f, 0f));

    private static byte[] Records(int stride, int positionOffset, params (float X, float Y, float Z)[] vertices)
    {
        var result = new byte[checked(stride * vertices.Length)];
        for (var vertex = 0; vertex < vertices.Length; vertex++)
        {
            var record = vertex * stride;
            result.AsSpan(record, positionOffset).Fill((byte)(0x40 + vertex));
            result.AsSpan(record + positionOffset + (sizeof(float) * 3), stride - positionOffset - (sizeof(float) * 3))
                .Fill((byte)(0x80 + vertex));
            var position = record + positionOffset;
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(position), BitConverter.SingleToInt32Bits(vertices[vertex].X));
            BinaryPrimitives.WriteInt32LittleEndian(
                result.AsSpan(position + sizeof(float)),
                BitConverter.SingleToInt32Bits(vertices[vertex].Y));
            BinaryPrimitives.WriteInt32LittleEndian(
                result.AsSpan(position + (sizeof(float) * 2)),
                BitConverter.SingleToInt32Bits(vertices[vertex].Z));
        }

        return result;
    }
}
