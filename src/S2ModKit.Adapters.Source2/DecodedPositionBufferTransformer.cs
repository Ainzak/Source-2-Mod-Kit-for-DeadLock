using System.Buffers.Binary;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

internal sealed record DecodedPositionTransformResult(
    ReadOnlyMemory<byte> TransformedDecoded,
    ContentHash InputHash,
    ContentHash OutputHash,
    ContentHash SelectedVertexSetHash,
    int SelectedVertexCount,
    GeometryBounds BoundsBefore,
    GeometryBounds BoundsAfter,
    int ChangedVertexCount,
    float MaximumDisplacement);

internal static class DecodedPositionBufferTransformer
{
    private const string SupportedFormat = "R32G32B32_FLOAT";
    private const int PositionByteLength = sizeof(float) * 3;
    private const int MinimumStride = PositionByteLength;
    private const int MaximumStride = 256;

    public static DecodedPositionTransformResult Apply(
        ReadOnlyMemory<byte> decoded,
        int vertexCount,
        PositionLayout positionLayout,
        IReadOnlyList<int> selectedVertices,
        UniformTransform transform)
    {
        ArgumentNullException.ThrowIfNull(positionLayout);
        ArgumentNullException.ThrowIfNull(selectedVertices);
        ArgumentNullException.ThrowIfNull(transform);
        if (vertexCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(vertexCount), vertexCount, "Vertex count must be positive.");
        }

        if (!string.Equals(positionLayout.Format, SupportedFormat, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Position format '{positionLayout.Format}' is not the supported {SupportedFormat} layout.",
                nameof(positionLayout));
        }

        var stride = positionLayout.Stride;
        if (stride < MinimumStride || stride > MaximumStride || stride % sizeof(float) != 0)
        {
            throw new ArgumentException(
                $"Vertex stride {stride} is outside the supported aligned range [{MinimumStride}, {MaximumStride}].",
                nameof(positionLayout));
        }

        var positionOffset = positionLayout.Offset;
        if (positionOffset < 0 || positionOffset > stride - PositionByteLength)
        {
            throw new ArgumentException(
                $"Position offset {positionOffset} escapes stride {stride}.",
                nameof(positionLayout));
        }

        var expectedLength = checked(vertexCount * stride);
        if (decoded.Length != expectedLength)
        {
            throw new ArgumentException(
                $"The decoded buffer is {decoded.Length} bytes; vertex count {vertexCount} and stride {stride} require exactly {expectedLength}.",
                nameof(decoded));
        }

        if (selectedVertices.Count == 0)
        {
            throw new ArgumentException("At least one selected vertex is required.", nameof(selectedVertices));
        }

        var canonical = CanonicalizeSelection(selectedVertices, vertexCount);
        var sourcePoints = new Point3[canonical.Length];
        for (var index = 0; index < sourcePoints.Length; index++)
        {
            sourcePoints[index] = ReadPosition(decoded.Span, canonical[index], positionLayout);
        }

        var transformedPoints = new Point3[sourcePoints.Length];
        var summary = transform.ApplyAndSummarize(sourcePoints, transformedPoints);
        var transformed = decoded.ToArray();
        for (var index = 0; index < transformedPoints.Length; index++)
        {
            WritePosition(transformed, canonical[index], positionLayout, transformedPoints[index]);
        }

        return new DecodedPositionTransformResult(
            transformed,
            ContentHash.Compute(decoded.Span),
            ContentHash.Compute(transformed),
            new ContentHash(VertexSetHash.Compute(canonical)),
            canonical.Length,
            ToDomainBounds(summary.BeforeBounds),
            ToDomainBounds(summary.AfterBounds),
            summary.ChangedPointCount,
            summary.MaximumDisplacement);
    }

    private static int[] CanonicalizeSelection(IReadOnlyList<int> selectedVertices, int vertexCount)
    {
        var canonical = new int[selectedVertices.Count];
        for (var index = 0; index < canonical.Length; index++)
        {
            var vertex = selectedVertices[index];
            if ((uint)vertex >= (uint)vertexCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(selectedVertices),
                    vertex,
                    $"Selected vertex {vertex} is outside the buffer's [0, {vertexCount}) vertex range.");
            }

            canonical[index] = vertex;
        }

        Array.Sort(canonical);
        var distinctCount = 0;
        for (var index = 0; index < canonical.Length; index++)
        {
            if (index == 0 || canonical[index] != canonical[distinctCount - 1])
            {
                canonical[distinctCount++] = canonical[index];
            }
        }

        return canonical[..distinctCount];
    }

    private static Point3 ReadPosition(ReadOnlySpan<byte> decoded, int vertex, PositionLayout positionLayout)
    {
        var offset = checked((vertex * positionLayout.Stride) + positionLayout.Offset);
        var bytes = decoded.Slice(offset, PositionByteLength);
        return new Point3(
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[sizeof(float)..])),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[(sizeof(float) * 2)..])));
    }

    private static void WritePosition(byte[] transformed, int vertex, PositionLayout positionLayout, Point3 point)
    {
        var offset = checked((vertex * positionLayout.Stride) + positionLayout.Offset);
        var bytes = transformed.AsSpan(offset, PositionByteLength);
        BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(point.X));
        BinaryPrimitives.WriteInt32LittleEndian(bytes[sizeof(float)..], BitConverter.SingleToInt32Bits(point.Y));
        BinaryPrimitives.WriteInt32LittleEndian(bytes[(sizeof(float) * 2)..], BitConverter.SingleToInt32Bits(point.Z));
    }

    private static GeometryBounds ToDomainBounds(Bounds3 bounds) => new(
        new TransformVector3 { X = bounds.Min.X, Y = bounds.Min.Y, Z = bounds.Min.Z },
        new TransformVector3 { X = bounds.Max.X, Y = bounds.Max.Y, Z = bounds.Max.Z });
}
