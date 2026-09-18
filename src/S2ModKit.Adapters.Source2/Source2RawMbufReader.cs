using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;

namespace S2ModKit.Adapters.Source2;

internal sealed record Source2RawMbufAnalysis(
    Source2GeometryAnalysis Geometry,
    DrawCallSnapshot DrawCall,
    int VertexDataOffset,
    int VertexDataLength,
    int IndexDataOffset,
    int IndexDataLength,
    int BlendIndicesOffset);

internal static class Source2RawMbufReader
{
    private const int HeaderSize = 16;
    private const int BufferDescriptorSize = 24;
    private const int LayoutFieldSize = 56;
    private const int MaximumElementCount = 16 * 1024 * 1024;
    private const int MaximumBufferBytes = 512 * 1024 * 1024;
    private const int VertexStride = 24;
    private const int IndexStride = 2;
    private const uint R32G32B32Float = 6;
    private const uint R8G8B8A8UInt = 30;
    private const uint R16G16Snorm = 37;
    private const uint R32UInt = 42;

    public static Source2RawMbufAnalysis AnalyzeDetailed(
        Source2ResourceBlock block,
        IReadOnlyList<GeometryDrawCallInput> drawCalls,
        string context)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(drawCalls);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        if (!string.Equals(block.Type, "MBUF", StringComparison.Ordinal))
        {
            throw Unsupported(
                "MBUF_LAYOUT_UNSUPPORTED",
                $"{context} references block {block.Index} of type {block.Type}, not MBUF.");
        }

        try
        {
            return AnalyzeCore(block, drawCalls, context);
        }
        catch (S2ModKitException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or OverflowException
            or IndexOutOfRangeException)
        {
            throw new S2ModKitException(
                new S2Error(
                    "MBUF_DESCRIPTOR_RANGE_INVALID",
                    "source2_adapter",
                    $"{context} contains a malformed or out-of-range raw MBUF descriptor.",
                    "Use an intact raw embedded-MBUF resource matching the accepted bounded profile.",
                    ErrorCategory.UnsupportedCapability),
                exception);
        }
    }

    private static Source2RawMbufAnalysis AnalyzeCore(
        Source2ResourceBlock block,
        IReadOnlyList<GeometryDrawCallInput> drawCalls,
        string context)
    {
        var bytes = block.Payload.Span;
        if (bytes.Length < HeaderSize)
        {
            throw Unsupported("MBUF_DESCRIPTOR_RANGE_INVALID", $"{context} MBUF header is truncated.");
        }

        var vertexDescriptorCount = ReadCount(bytes, 4, $"{context} vertex-buffer descriptor count");
        var indexDescriptorCount = ReadCount(bytes, 12, $"{context} index-buffer descriptor count");
        if (vertexDescriptorCount != 1 || indexDescriptorCount != 1)
        {
            throw Unsupported(
                "MBUF_COUNT_UNSUPPORTED",
                $"{context} declares {vertexDescriptorCount} vertex-buffer and {indexDescriptorCount} index-buffer descriptors; exactly one of each is required.");
        }

        var vertexDescriptors = ReadRelativeRange(bytes, 0, vertexDescriptorCount, BufferDescriptorSize, "vertex descriptor table");
        var indexDescriptors = ReadRelativeRange(bytes, 8, indexDescriptorCount, BufferDescriptorSize, "index descriptor table");
        var vertex = ReadDescriptor(bytes, vertexDescriptors.Start, vertexBuffer: true, context);
        var index = ReadDescriptor(bytes, indexDescriptors.Start, vertexBuffer: false, context);

        if (vertex.Count < 1 || vertex.Count > MaximumElementCount
            || index.Count < 1 || index.Count > MaximumElementCount)
        {
            throw Unsupported(
                "MBUF_COUNT_UNSUPPORTED",
                $"{context} element counts {vertex.Count}/{index.Count} are outside the supported range [1, {MaximumElementCount}].");
        }

        if (vertex.Stride != VertexStride || index.Stride != IndexStride || index.Count % 3 != 0)
        {
            throw Unsupported(
                "MBUF_LAYOUT_UNSUPPORTED",
                $"{context} requires 24-byte vertices, 16-bit indices, and a triangle-list index count.");
        }

        var expectedVertexBytes = CheckedBufferLength(vertex.Count, vertex.Stride, context);
        var expectedIndexBytes = CheckedBufferLength(index.Count, index.Stride, context);
        if (vertex.DataLength != expectedVertexBytes || index.DataLength != expectedIndexBytes)
        {
            throw Unsupported(
                "MBUF_COMPRESSION_UNSUPPORTED",
                $"{context} stored buffer sizes do not equal their decoded sizes; only raw uncompressed MBUF data is supported.");
        }

        if (vertex.LayoutCount != 4 || index.LayoutCount != 0 || index.LayoutRelativeOffset != 0)
        {
            throw Unsupported(
                "MBUF_LAYOUT_UNSUPPORTED",
                $"{context} does not use the characterized four-field vertex layout and empty index layout.");
        }

        var vertexLayout = ReadRelativeRange(
            bytes,
            checked(vertexDescriptors.Start + 8),
            vertex.LayoutCount,
            LayoutFieldSize,
            "vertex input layout");
        var vertexData = ReadRelativeRange(
            bytes,
            checked(vertexDescriptors.Start + 16),
            vertex.DataLength,
            1,
            "vertex data");
        var indexData = ReadRelativeRange(
            bytes,
            checked(indexDescriptors.Start + 16),
            index.DataLength,
            1,
            "index data");
        RequireNonOverlapping(
            [
                new NamedRange("header", 0, HeaderSize),
                new NamedRange("vertex descriptors", vertexDescriptors.Start, vertexDescriptors.Length),
                new NamedRange("index descriptors", indexDescriptors.Start, indexDescriptors.Length),
                new NamedRange("vertex layout", vertexLayout.Start, vertexLayout.Length),
                new NamedRange("vertex data", vertexData.Start, vertexData.Length),
                new NamedRange("index data", indexData.Start, indexData.Length),
            ],
            context);

        var fields = new LayoutField[vertex.LayoutCount];
        for (var ordinal = 0; ordinal < fields.Length; ordinal++)
        {
            fields[ordinal] = ReadLayoutField(
                bytes,
                checked(vertexLayout.Start + (ordinal * LayoutFieldSize)),
                context);
        }
        RequireLayout(fields, context);

        var vertexBytes = block.Payload.Slice(vertexData.Start, vertexData.Length).ToArray();
        var indexBytes = block.Payload.Slice(indexData.Start, indexData.Length).ToArray();
        var positions = new Point3[vertex.Count];
        for (var ordinal = 0; ordinal < positions.Length; ordinal++)
        {
            var record = vertexBytes.AsSpan(ordinal * vertex.Stride, vertex.Stride);
            var point = new Point3(ReadSingle(record, 0), ReadSingle(record, 4), ReadSingle(record, 8));
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
            {
                throw Unsupported("MBUF_LAYOUT_UNSUPPORTED", $"{context} vertex {ordinal} contains a non-finite position.");
            }

            if (record[20] != 0)
            {
                throw Unsupported(
                    "MBUF_LAYOUT_UNSUPPORTED",
                    $"{context} vertex {ordinal} does not use rigid blend-index slot zero.");
            }

            positions[ordinal] = point;
        }

        var indices = new uint[index.Count];
        var referencedVertices = new bool[vertex.Count];
        for (var ordinal = 0; ordinal < indices.Length; ordinal++)
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(indexBytes.AsSpan(ordinal * IndexStride, IndexStride));
            if (value >= vertex.Count)
            {
                throw Unsupported(
                    "MBUF_INDEX_RANGE_INVALID",
                    $"{context} index {ordinal} references vertex {value} outside [0, {vertex.Count}).");
            }

            indices[ordinal] = value;
            referencedVertices[value] = true;
        }

        if (referencedVertices.Any(value => !value))
        {
            throw Unsupported(
                "MBUF_OWNERSHIP_UNSUPPORTED",
                $"{context} index data does not reference every visual vertex.");
        }

        var drawCall = RequireWholeMeshDrawCall(drawCalls, vertex.Count, index.Count, context);
        var bounds = Bounds3.FromPoints(positions);
        var selectedVertices = Enumerable.Range(0, vertex.Count).ToImmutableArray();
        var vertexSnapshot = new VertexBufferSnapshot(
            0,
            block.Index,
            vertex.Count,
            vertex.Stride,
            ContentHash.Compute(vertexBytes),
            ContentHash.Compute(vertexBytes),
            new PositionLayout("R32G32B32_FLOAT", 0, vertex.Stride));
        var indexSnapshot = new IndexBufferSnapshot(
            0,
            block.Index,
            index.Count,
            index.Stride,
            ContentHash.Compute(indexBytes),
            ContentHash.Compute(indexBytes));
        var drawCallSnapshot = new DrawCallGeometrySnapshot(
            drawCall.Snapshot.Id,
            0,
            0,
            0,
            vertex.Count,
            vertex.Count,
            new ContentHash(VertexSetHash.Compute(selectedVertices)),
            new GeometryBounds(
                new TransformVector3 { X = bounds.Min.X, Y = bounds.Min.Y, Z = bounds.Min.Z },
                new TransformVector3 { X = bounds.Max.X, Y = bounds.Max.Y, Z = bounds.Max.Z }),
            true);
        var vertexAnalysis = new Source2VertexBufferAnalysis(vertexSnapshot, vertexBytes);
        var indexAnalysis = new Source2IndexBufferAnalysis(indexSnapshot, indices);
        var drawCallAnalysis = new Source2DrawCallAnalysis(drawCallSnapshot, selectedVertices);
        var connectedComponents = Source2GeometryAnalyzer.AnalyzeConnectedComponents(
            drawCalls,
            [drawCallAnalysis],
            [vertexAnalysis],
            [indexAnalysis],
            context);
        var snapshot = new MeshGeometrySnapshot(
            "ready",
            "One raw embedded-MBUF vertex buffer, index buffer, and complete exclusively owned draw call passed the Stage 7 profile.",
            [vertexSnapshot],
            [indexSnapshot],
            [drawCallSnapshot],
            null)
        {
            ConnectedComponents = connectedComponents.Select(item => item.Snapshot).ToArray(),
        };
        var geometry = new Source2GeometryAnalysis(
            snapshot,
            [vertexAnalysis],
            [indexAnalysis],
            [drawCallAnalysis])
        {
            ConnectedComponents = connectedComponents,
        };
        return new Source2RawMbufAnalysis(
            geometry,
            drawCall.Snapshot,
            vertexData.Start,
            vertexData.Length,
            indexData.Start,
            indexData.Length,
            20);
    }

    private static BufferDescriptor ReadDescriptor(
        ReadOnlySpan<byte> bytes,
        int offset,
        bool vertexBuffer,
        string context)
    {
        var count = ReadCount(bytes, offset, $"{context} element count");
        var stride = ReadCount(bytes, checked(offset + 4), $"{context} element stride");
        var layoutRelativeOffset = ReadInt32(bytes, checked(offset + 8));
        var layoutCount = ReadCount(bytes, checked(offset + 12), $"{context} layout count");
        var dataLength = ReadCount(bytes, checked(offset + 20), $"{context} stored data size");
        if (!vertexBuffer && layoutCount == 0 && layoutRelativeOffset != 0)
        {
            throw Unsupported("MBUF_LAYOUT_UNSUPPORTED", $"{context} empty index layout has a non-zero relative offset.");
        }

        return new BufferDescriptor(count, stride, layoutRelativeOffset, layoutCount, dataLength);
    }

    private static LayoutField ReadLayoutField(ReadOnlySpan<byte> bytes, int offset, string context)
    {
        var semanticBytes = bytes.Slice(offset, 32);
        var nul = semanticBytes.IndexOf((byte)0);
        if (nul <= 0 || semanticBytes[(nul + 1)..].ContainsAnyExcept((byte)0))
        {
            throw Unsupported("MBUF_LAYOUT_UNSUPPORTED", $"{context} contains an invalid input-layout semantic name.");
        }

        var printable = semanticBytes[..nul];
        if (printable.ContainsAnyInRange((byte)0, (byte)0x1f)
            || printable.ContainsAnyInRange((byte)0x7f, byte.MaxValue))
        {
            throw Unsupported("MBUF_LAYOUT_UNSUPPORTED", $"{context} contains a non-ASCII input-layout semantic name.");
        }

        return new LayoutField(
            Encoding.ASCII.GetString(printable),
            ReadUInt32(bytes, checked(offset + 32)),
            ReadUInt32(bytes, checked(offset + 36)),
            ReadUInt32(bytes, checked(offset + 40)),
            ReadUInt32(bytes, checked(offset + 44)),
            ReadUInt32(bytes, checked(offset + 48)),
            ReadUInt32(bytes, checked(offset + 52)));
    }

    private static void RequireLayout(IReadOnlyList<LayoutField> fields, string context)
    {
        var expected = new[]
        {
            new LayoutField("POSITION", 0, R32G32B32Float, 0, 0, 0, 0),
            new LayoutField("TEXCOORD", 0, R16G16Snorm, 12, 0, 0, 0),
            new LayoutField("NORMAL", 0, R32UInt, 16, 0, 0, 0),
            new LayoutField("BLENDINDICES", 0, R8G8B8A8UInt, 20, 0, 0, 0),
        };
        if (!fields.SequenceEqual(expected))
        {
            throw Unsupported(
                "MBUF_LAYOUT_UNSUPPORTED",
                $"{context} vertex fields do not match the characterized POSITION/TEXCOORD/NORMAL/BLENDINDICES layout.");
        }
    }

    private static GeometryDrawCallInput RequireWholeMeshDrawCall(
        IReadOnlyList<GeometryDrawCallInput> drawCalls,
        int vertexCount,
        int indexCount,
        string context)
    {
        if (drawCalls.Count != 1)
        {
            throw Unsupported(
                "MBUF_OWNERSHIP_UNSUPPORTED",
                $"{context} declares {drawCalls.Count} draw calls; exactly one whole-mesh draw call is required.");
        }

        var input = drawCalls[0];
        var data = input.Data;
        var primitive = RequireString(data, "m_nPrimitiveType", context);
        var indexReference = RequireCollection(data, "m_indexBuffer", context);
        var vertexReferences = RequireArray(data, "m_vertexBuffers", context);
        if (!string.Equals(primitive, "RENDER_PRIM_TRIANGLES", StringComparison.Ordinal)
            || vertexReferences.Count != 1
            || ReadBufferReference(indexReference, context) != 0
            || ReadBufferReference(RequireCollection(vertexReferences[0], context), context) != 0
            || RequireInt32(data, "m_nBaseVertex", context) != 0
            || ReadOptionalInt32(data, "m_nAppliedIndexOffset", context) != 0
            || RequireInt32(data, "m_nVertexCount", context) != vertexCount
            || input.Snapshot.IndexStart != 0
            || input.Snapshot.IndexCount != indexCount)
        {
            throw Unsupported(
                "MBUF_OWNERSHIP_UNSUPPORTED",
                $"{context} draw call does not own the complete characterized MBUF geometry.");
        }

        return input;
    }

    private static int ReadBufferReference(KVObject value, string context)
    {
        if (RequireInt32(value, "m_nBindOffsetBytes", context) != 0)
        {
            return -1;
        }

        return RequireInt32(value, "m_hBuffer", context);
    }

    private static KVObject RequireArray(KVObject parent, string key, string context)
    {
        if (!parent.TryGetValue(key, out var value) || value is null || !value.IsArray)
        {
            throw Unsupported("MBUF_OWNERSHIP_UNSUPPORTED", $"{context} is missing array {key}.");
        }

        return value;
    }

    private static KVObject RequireCollection(KVObject parent, string key, string context)
    {
        if (!parent.TryGetValue(key, out var value) || value is null)
        {
            throw Unsupported("MBUF_OWNERSHIP_UNSUPPORTED", $"{context} is missing collection {key}.");
        }

        return RequireCollection(value, context);
    }

    private static KVObject RequireCollection(KVObject value, string context)
    {
        if (!value.IsCollection)
        {
            throw Unsupported("MBUF_OWNERSHIP_UNSUPPORTED", $"{context} contains an invalid draw-call collection.");
        }

        return value;
    }

    private static string RequireString(KVObject parent, string key, string context)
    {
        if (!parent.TryGetValue(key, out var value)
            || value is null
            || value.IsArray
            || value.IsCollection
            || value.ValueType != KVValueType.String)
        {
            throw Unsupported("MBUF_OWNERSHIP_UNSUPPORTED", $"{context} is missing string {key}.");
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static int RequireInt32(KVObject parent, string key, string context)
    {
        if (!parent.TryGetValue(key, out var value) || value is null || value.IsArray || value.IsCollection)
        {
            throw Unsupported("MBUF_OWNERSHIP_UNSUPPORTED", $"{context} is missing integer {key}.");
        }

        try
        {
            return checked((int)value.ToInt64(CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            throw new S2ModKitException(
                new S2Error(
                    "MBUF_OWNERSHIP_UNSUPPORTED",
                    "source2_adapter",
                    $"{context} contains invalid integer {key}.",
                    "Use one complete triangle-list draw call with zero-offset buffer references.",
                    ErrorCategory.UnsupportedCapability),
                exception);
        }
    }

    private static int ReadOptionalInt32(KVObject parent, string key, string context) =>
        parent.TryGetValue(key, out _) ? RequireInt32(parent, key, context) : 0;

    private static RelativeRange ReadRelativeRange(
        ReadOnlySpan<byte> bytes,
        int relativeFieldOffset,
        int count,
        int itemSize,
        string label)
    {
        var relative = ReadInt32(bytes, relativeFieldOffset);
        var length = checked(count * itemSize);
        var start = checked(relativeFieldOffset + relative);
        var end = checked(start + length);
        if (count < 0 || itemSize < 1 || start < 0 || end < start || end > bytes.Length)
        {
            throw Unsupported(
                "MBUF_DESCRIPTOR_RANGE_INVALID",
                $"MBUF {label} range [{start}, {end}) escapes the {bytes.Length}-byte payload.");
        }

        return new RelativeRange(start, length);
    }

    private static void RequireNonOverlapping(IReadOnlyList<NamedRange> ranges, string context)
    {
        var ordered = ranges.Where(range => range.Length > 0).OrderBy(range => range.Start).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            var previous = ordered[index - 1];
            var current = ordered[index];
            if (previous.End > current.Start)
            {
                throw Unsupported(
                    "MBUF_DESCRIPTOR_RANGE_INVALID",
                    $"{context} ranges {previous.Name} and {current.Name} overlap.");
            }
        }
    }

    private static int CheckedBufferLength(int count, int stride, string context)
    {
        var length = checked(count * stride);
        if (length > MaximumBufferBytes)
        {
            throw Unsupported(
                "MBUF_COUNT_UNSUPPORTED",
                $"{context} buffer length {length} exceeds the {MaximumBufferBytes}-byte limit.");
        }

        return length;
    }

    private static int ReadCount(ReadOnlySpan<byte> bytes, int offset, string label)
    {
        var value = ReadUInt32(bytes, offset);
        if (value > int.MaxValue)
        {
            throw Unsupported("MBUF_COUNT_UNSUPPORTED", $"{label} exceeds the signed bounded range.");
        }

        return (int)value;
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || offset > bytes.Length - sizeof(int))
        {
            throw Unsupported("MBUF_DESCRIPTOR_RANGE_INVALID", "MBUF descriptor integer is truncated.");
        }

        return BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, sizeof(int)));
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        unchecked((uint)ReadInt32(bytes, offset));

    private static float ReadSingle(ReadOnlySpan<byte> bytes, int offset) =>
        BitConverter.Int32BitsToSingle(ReadInt32(bytes, offset));

    private static S2ModKitException Unsupported(string code, string summary) => Errors.Unsupported(
        code,
        summary,
        "Use an intact raw embedded-MBUF resource matching the accepted bounded profile.");

    private sealed record BufferDescriptor(
        int Count,
        int Stride,
        int LayoutRelativeOffset,
        int LayoutCount,
        int DataLength);

    private sealed record LayoutField(
        string Semantic,
        uint SemanticIndex,
        uint Format,
        uint Offset,
        uint Slot,
        uint SlotType,
        uint InstanceStepRate);

    private readonly record struct RelativeRange(int Start, int Length);

    private sealed record NamedRange(string Name, int Start, int Length)
    {
        public int End => checked(Start + Length);
    }
}
