using System.Buffers.Binary;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using ValveKeyValue;

namespace S2ModKit.Source2.Tests;

public sealed class Source2GeometryAnalyzerTests
{
    private static readonly uint[] ExpectedPlanningIndices = [2, 0, 2, 3, 3, 3];
    private static readonly int[] ExpectedFirstPlanningVertices = [0, 2];
    private static readonly int[] ExpectedSecondPlanningVertices = [3];

    [Fact]
    public void AnalyzeReportsBuffersBoundsAndLocalIndexOffsets()
    {
        var vertices = VertexBytes(16, (0, 0, 0), (1, 0, 0), (0, 1, 0), (10, 0, 0), (11, 0, 0));
        var indices = IndexBytes(0, 1, 2, 0, 1, 1);
        using var codec = new FakeCodec();

        var result = Source2GeometryAnalyzer.Analyze(
            Descriptor(VertexDescriptor(0, 5, 16), IndexDescriptor(1, 6, 2)),
            Envelope(("MVTX", vertices), ("MIDX", indices)),
            [DrawCall(0, 0, 3, 0, 3), DrawCall(1, 3, 3, 3, 5)],
            codec,
            "test mesh");

        Assert.Equal("ready", result.Status);
        Assert.Equal(codec.Identity, result.Codec);
        Assert.Single(result.VertexBuffers);
        Assert.Single(result.IndexBuffers);
        Assert.Equal(ContentHash.Compute(vertices), result.VertexBuffers[0].EncodedHash);
        Assert.Equal(ContentHash.Compute(indices), result.IndexBuffers[0].EncodedHash);
        Assert.Collection(
            result.DrawCalls,
            first =>
            {
                Assert.Equal(3, first.UniqueVertexCount);
                Assert.Equal(0, first.BaseVertex);
                Assert.True(first.ExclusivelyOwned);
                Assert.Equal(0, first.Bounds.Min.X);
                Assert.Equal(1, first.Bounds.Max.X);
                Assert.Equal(1, first.Bounds.Max.Y);
            },
            second =>
            {
                Assert.Equal(2, second.UniqueVertexCount);
                Assert.Equal(3, second.BaseVertex);
                Assert.True(second.ExclusivelyOwned);
                Assert.Equal(10, second.Bounds.Min.X);
                Assert.Equal(11, second.Bounds.Max.X);
            });
    }

    [Fact]
    public void AnalyzeDetailedRetainsCanonicalPlanningInputsWithoutChangingPublicSnapshot()
    {
        var vertices = VertexBytes(16, (0, 0, 0), (1, 0, 0), (0, 1, 0), (10, 0, 0));
        var indices = IndexBytes(2, 0, 2, 3, 3, 3);
        using var codec = new FakeCodec();

        var analysis = Source2GeometryAnalyzer.AnalyzeDetailed(
            Descriptor(VertexDescriptor(0, 4, 16), IndexDescriptor(1, 6, 2)),
            Envelope(("MVTX", vertices), ("MIDX", indices)),
            [DrawCall(0, 0, 3, 0, 4), DrawCall(1, 3, 3, 0, 4)],
            codec,
            "planning mesh");

        Assert.Equal("ready", analysis.Snapshot.Status);
        Assert.Equal(vertices, Assert.Single(analysis.VertexBuffers).Decoded);
        Assert.Equal(ExpectedPlanningIndices, Assert.Single(analysis.IndexBuffers).Indices);
        Assert.Collection(
            analysis.DrawCalls,
            first => Assert.Equal(ExpectedFirstPlanningVertices, first.VertexIndices),
            second => Assert.Equal(ExpectedSecondPlanningVertices, second.VertexIndices));
        Assert.Equal(
            analysis.DrawCalls.Select(item => item.Snapshot).ToArray(),
            analysis.Snapshot.DrawCalls);
    }

    [Fact]
    public void AnalyzeReportsStableDisconnectedTriangleComponents()
    {
        var vertices = VertexBytes(
            16,
            (0, 0, 0), (1, 0, 0), (0, 1, 0),
            (10, 0, 0), (11, 0, 0), (10, 1, 0));
        var indices = IndexBytes(0, 1, 2, 3, 4, 5);
        using var codec = new FakeCodec();
        var descriptor = Descriptor(VertexDescriptor(0, 6, 16), IndexDescriptor(1, 6, 2));
        var envelope = Envelope(("MVTX", vertices), ("MIDX", indices));
        var drawCalls = new[] { DrawCall(0, 0, 6, 0, 6) };

        var first = Source2GeometryAnalyzer.AnalyzeDetailed(descriptor, envelope, drawCalls, codec, "component mesh");
        var second = Source2GeometryAnalyzer.AnalyzeDetailed(descriptor, envelope, drawCalls, codec, "component mesh");

        Assert.Equal(2, first.ConnectedComponents.Count);
        Assert.Equal(
            first.ConnectedComponents.Select(item => item.Snapshot.Id),
            second.ConnectedComponents.Select(item => item.Snapshot.Id));
        Assert.All(first.ConnectedComponents, item =>
        {
            Assert.Equal(3, item.Snapshot.VertexCount);
            Assert.Equal(1, item.Snapshot.TriangleCount);
            Assert.True(item.Snapshot.ExclusivelyOwned);
            Assert.Matches("^cc_[0-9a-f]{24}$", item.Snapshot.Id);
        });
        Assert.Equal(
            first.ConnectedComponents.Select(item => item.Snapshot).ToArray(),
            first.Snapshot.ConnectedComponents);
    }

    [Fact]
    public void AnalyzeDetectsVerticesSharedBetweenDrawCalls()
    {
        using var codec = new FakeCodec();
        var result = Source2GeometryAnalyzer.Analyze(
            Descriptor(VertexDescriptor(0, 4, 16), IndexDescriptor(1, 6, 2)),
            Envelope(
                ("MVTX", VertexBytes(16, (0, 0, 0), (1, 0, 0), (2, 0, 0), (3, 0, 0))),
                ("MIDX", IndexBytes(0, 1, 2, 2, 3, 3))),
            [DrawCall(0, 0, 3, 0, 4), DrawCall(1, 3, 3, 0, 4)],
            codec,
            "shared mesh");

        Assert.All(result.DrawCalls, drawCall => Assert.False(drawCall.ExclusivelyOwned));
    }

    [Fact]
    public void AnalyzeMapsMultipleDrawCallsToSeparateBufferPairs()
    {
        using var codec = new FakeCodec();
        var descriptor = Descriptor(
            [VertexDescriptor(0, 3, 16), VertexDescriptor(2, 3, 16)],
            [IndexDescriptor(1, 3, 2), IndexDescriptor(3, 3, 2)]);
        var result = Source2GeometryAnalyzer.Analyze(
            descriptor,
            Envelope(
                ("MVTX", VertexBytes(16, (0, 0, 0), (1, 0, 0), (0, 1, 0))),
                ("MIDX", IndexBytes(0, 1, 2)),
                ("MVTX", VertexBytes(16, (10, 0, 0), (11, 0, 0), (10, 1, 0))),
                ("MIDX", IndexBytes(0, 1, 2))),
            [DrawCall(0, 0, 3, 0, 3, 0, 0), DrawCall(1, 0, 3, 0, 3, 1, 1)],
            codec,
            "multi-buffer mesh");

        Assert.Equal(2, result.VertexBuffers.Count);
        Assert.Equal(2, result.IndexBuffers.Count);
        Assert.Collection(
            result.DrawCalls,
            first => Assert.Equal((0, 0), (first.VertexBufferOrdinal, first.IndexBufferOrdinal)),
            second => Assert.Equal((1, 1), (second.VertexBufferOrdinal, second.IndexBufferOrdinal)));
    }

    [Theory]
    [InlineData(2u, "POSITION is not the supported")]
    [InlineData(6u, "non-zero buffer bind offset")]
    public void AnalyzeRejectsUnsupportedLayoutAndBindOffset(uint positionFormat, string expectedMessage)
    {
        using var codec = new FakeCodec();
        var bindOffset = positionFormat == 6 ? 4 : 0;

        var exception = Assert.Throws<InvalidDataException>(() => Source2GeometryAnalyzer.Analyze(
            Descriptor(VertexDescriptor(0, 3, 16, positionFormat), IndexDescriptor(1, 3, 2)),
            Envelope(
                ("MVTX", VertexBytes(16, (0, 0, 0), (1, 0, 0), (0, 1, 0))),
                ("MIDX", IndexBytes(0, 1, 2))),
            [DrawCall(0, 0, 3, 0, 3, bindOffsetBytes: bindOffset)],
            codec,
            "invalid mesh"));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeRejectsIndexOutsideDeclaredVertexBuffer()
    {
        using var codec = new FakeCodec();

        Assert.Throws<ArgumentOutOfRangeException>(() => Source2GeometryAnalyzer.Analyze(
            Descriptor(VertexDescriptor(0, 3, 16), IndexDescriptor(1, 3, 2)),
            Envelope(
                ("MVTX", VertexBytes(16, (0, 0, 0), (1, 0, 0), (0, 1, 0))),
                ("MIDX", IndexBytes(0, 1, 3))),
            [DrawCall(0, 0, 3, 0, 3)],
            codec,
            "invalid index mesh"));
    }

    [Fact]
    public void AnalyzeRejectsNonFiniteSelectedPosition()
    {
        using var codec = new FakeCodec();

        Assert.Throws<ArgumentException>(() => Source2GeometryAnalyzer.Analyze(
            Descriptor(VertexDescriptor(0, 3, 16), IndexDescriptor(1, 3, 2)),
            Envelope(
                ("MVTX", VertexBytes(16, (float.NaN, 0, 0), (1, 0, 0), (0, 1, 0))),
                ("MIDX", IndexBytes(0, 1, 2))),
            [DrawCall(0, 0, 3, 0, 3)],
            codec,
            "non-finite mesh"));
    }

    [Fact]
    public void AnalyzeRejectsTruncatedDecodedVertexBuffer()
    {
        using var codec = new FakeCodec(truncateVertexDecode: true);

        Assert.Throws<InvalidDataException>(() => Source2GeometryAnalyzer.Analyze(
            Descriptor(VertexDescriptor(0, 3, 16), IndexDescriptor(1, 3, 2)),
            Envelope(
                ("MVTX", VertexBytes(16, (0, 0, 0), (1, 0, 0), (0, 1, 0))),
                ("MIDX", IndexBytes(0, 1, 2))),
            [DrawCall(0, 0, 3, 0, 3)],
            codec,
            "truncated mesh"));
    }

    [Fact]
    public void AnalyzeRejectsAmbiguousAppliedIndexOffset()
    {
        using var codec = new FakeCodec();

        var exception = Assert.Throws<InvalidDataException>(() => Source2GeometryAnalyzer.Analyze(
            Descriptor(VertexDescriptor(0, 5, 16), IndexDescriptor(1, 3, 2)),
            Envelope(
                ("MVTX", VertexBytes(16, (0, 0, 0), (1, 0, 0), (2, 0, 0), (3, 0, 0), (4, 0, 0))),
                ("MIDX", IndexBytes(1, 2, 2))),
            [DrawCall(0, 0, 3, 1, 5)],
            codec,
            "ambiguous mesh"));

        Assert.Contains("ambiguous local/global", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeAcceptsAlreadyGlobalIndicesWithAppliedOffset()
    {
        using var codec = new FakeCodec();

        var result = Source2GeometryAnalyzer.Analyze(
            Descriptor(VertexDescriptor(0, 5, 16), IndexDescriptor(1, 3, 2)),
            Envelope(
                ("MVTX", VertexBytes(16, (0, 0, 0), (1, 0, 0), (2, 0, 0), (3, 0, 0), (4, 0, 0))),
                ("MIDX", IndexBytes(3, 4, 4))),
            [DrawCall(0, 0, 3, 3, 5)],
            codec,
            "global-index mesh");

        var drawCall = Assert.Single(result.DrawCalls);
        Assert.Equal(0, drawCall.BaseVertex);
        Assert.Equal(2, drawCall.UniqueVertexCount);
        Assert.Equal(3, drawCall.Bounds.Min.X);
        Assert.Equal(4, drawCall.Bounds.Max.X);
    }

    [Fact]
    public void AnalyzeAcceptsThirtyTwoBitIndexBuffers()
    {
        using var codec = new FakeCodec();

        var result = Source2GeometryAnalyzer.Analyze(
            Descriptor(VertexDescriptor(0, 3, 16), IndexDescriptor(1, 3, 4)),
            Envelope(
                ("MVTX", VertexBytes(16, (0, 0, 0), (1, 0, 0), (0, 1, 0))),
                ("MIDX", IndexBytes32(0, 1, 2))),
            [DrawCall(0, 0, 3, 0, 3)],
            codec,
            "32-bit index mesh");

        Assert.Equal(4, Assert.Single(result.IndexBuffers).Stride);
        Assert.Equal(3, Assert.Single(result.DrawCalls).UniqueVertexCount);
    }

    [Fact]
    public void AnalyzeRejectsNonZeroDeclaredBaseVertex()
    {
        using var codec = new FakeCodec();

        var exception = Assert.Throws<InvalidDataException>(() => Source2GeometryAnalyzer.Analyze(
            Descriptor(VertexDescriptor(0, 3, 16), IndexDescriptor(1, 3, 2)),
            Envelope(
                ("MVTX", VertexBytes(16, (0, 0, 0), (1, 0, 0), (0, 1, 0))),
                ("MIDX", IndexBytes(0, 1, 2))),
            [DrawCall(0, 0, 3, 0, 3, declaredBaseVertex: 1)],
            codec,
            "base-vertex mesh"));

        Assert.Contains("unsupported non-zero m_nBaseVertex", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeRejectsUnreferencedBuffers()
    {
        using var codec = new FakeCodec();
        var descriptor = Descriptor(
            [VertexDescriptor(0, 3, 16), VertexDescriptor(2, 3, 16)],
            [IndexDescriptor(1, 3, 2), IndexDescriptor(3, 3, 2)]);

        var exception = Assert.Throws<InvalidDataException>(() => Source2GeometryAnalyzer.Analyze(
            descriptor,
            Envelope(
                ("MVTX", VertexBytes(16, (0, 0, 0), (1, 0, 0), (0, 1, 0))),
                ("MIDX", IndexBytes(0, 1, 2)),
                ("MVTX", VertexBytes(16, (10, 0, 0), (11, 0, 0), (10, 1, 0))),
                ("MIDX", IndexBytes(0, 1, 2))),
            [DrawCall(0, 0, 3, 0, 3)],
            codec,
            "unreferenced-buffer mesh"));

        Assert.Contains("unreferenced vertex or index buffer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeRejectsAggregateDecodedBuffersAboveLimitBeforeDecode()
    {
        using var codec = new FakeCodec();
        var descriptor = Descriptor(
            [VertexDescriptor(0, 20_000_000, 16), VertexDescriptor(2, 20_000_000, 16)],
            [IndexDescriptor(1, 3, 2)]);

        var exception = Assert.Throws<InvalidDataException>(() => Source2GeometryAnalyzer.Analyze(
            descriptor,
            Envelope(("MVTX", [1]), ("MIDX", [1]), ("MVTX", [1])),
            [DrawCall(0, 0, 3, 0, 20_000_000)],
            codec,
            "oversized mesh"));

        Assert.Contains("aggregate decoded", exception.Message, StringComparison.Ordinal);
    }

    private static GeometryDrawCallInput DrawCall(
        int ordinal,
        long start,
        long count,
        int appliedOffset,
        int vertexEnd,
        int vertexBuffer = 0,
        int indexBuffer = 0,
        int bindOffsetBytes = 0,
        int declaredBaseVertex = 0)
    {
        var snapshot = DrawCallSnapshot.Create(
            "models/test.vmdl_c",
            0,
            0,
            ordinal,
            $"materials/test-{ordinal}.vmat",
            start,
            count);
        return new GeometryDrawCallInput(
            snapshot,
            Object(
                ("m_nPrimitiveType", "RENDER_PRIM_TRIANGLES"),
                ("m_nBaseVertex", declaredBaseVertex),
                ("m_nAppliedIndexOffset", appliedOffset),
                ("m_nVertexCount", vertexEnd),
                ("m_indexBuffer", BufferReference(indexBuffer, bindOffsetBytes)),
                ("m_vertexBuffers", Array(BufferReference(vertexBuffer, bindOffsetBytes)))));
    }

    private static KVObject Descriptor(KVObject vertex, KVObject index) => Descriptor([vertex], [index]);

    private static KVObject Descriptor(KVObject[] vertices, KVObject[] indices) => Object(
        ("m_vertexBuffers", Array(vertices)),
        ("m_indexBuffers", Array(indices)));

    private static KVObject VertexDescriptor(int block, int count, int stride, uint format = 6) => Object(
        ("m_nBlockIndex", block),
        ("m_nElementCount", count),
        ("m_nElementSizeInBytes", stride),
        ("m_bMeshoptCompressed", true),
        ("m_bMeshoptIndexSequence", false),
        ("m_bCompressedZSTD", false),
        ("m_inputLayoutFields", Array(Object(
            ("m_pSemanticName", "POSITION"),
            ("m_nSemanticIndex", 0),
            ("m_Format", format),
            ("m_nOffset", 0),
            ("m_nSlot", 0),
            ("m_nSlotType", "RENDER_SLOT_PER_VERTEX")))));

    private static KVObject IndexDescriptor(int block, int count, int stride) => Object(
        ("m_nBlockIndex", block),
        ("m_nElementCount", count),
        ("m_nElementSizeInBytes", stride),
        ("m_bMeshoptCompressed", true),
        ("m_bMeshoptIndexSequence", false),
        ("m_bCompressedZSTD", false),
        ("m_inputLayoutFields", Array()));

    private static KVObject BufferReference(int handle, int bindOffset) => Object(
        ("m_hBuffer", handle),
        ("m_nBindOffsetBytes", bindOffset));

    private static KVObject Object(params (string Key, KVObject Value)[] values)
    {
        var result = KVObject.Collection();
        foreach (var (key, value) in values)
        {
            result.Add(key, value);
        }

        return result;
    }

    private static KVObject Array(params KVObject[] values)
    {
        var result = KVObject.Array();
        foreach (var value in values)
        {
            result.Add(value);
        }

        return result;
    }

    private static Source2ResourceEnvelope Envelope(params (string Type, byte[] Payload)[] blocks) => new(
        12,
        1,
        16,
        16,
        ReadOnlyMemory<byte>.Empty,
        blocks.Select((item, index) => new Source2ResourceBlock(
            index,
            item.Type,
            0,
            0,
            item.Payload,
            ReadOnlyMemory<byte>.Empty)).ToArray());

    private static byte[] VertexBytes(int stride, params (float X, float Y, float Z)[] positions)
    {
        var result = new byte[checked(stride * positions.Length)];
        result.AsSpan().Fill(0xa5);
        for (var index = 0; index < positions.Length; index++)
        {
            var offset = index * stride;
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), BitConverter.SingleToInt32Bits(positions[index].X));
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset + 4), BitConverter.SingleToInt32Bits(positions[index].Y));
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset + 8), BitConverter.SingleToInt32Bits(positions[index].Z));
        }

        return result;
    }

    private static byte[] IndexBytes(params ushort[] indices)
    {
        var result = new byte[checked(indices.Length * sizeof(ushort))];
        for (var index = 0; index < indices.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(index * sizeof(ushort)), indices[index]);
        }

        return result;
    }

    private static byte[] IndexBytes32(params uint[] indices)
    {
        var result = new byte[checked(indices.Length * sizeof(uint))];
        for (var index = 0; index < indices.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(index * sizeof(uint)), indices[index]);
        }

        return result;
    }

    private sealed class FakeCodec(bool truncateVertexDecode = false) : IMeshOptimizerCodec
    {
        public GeometryCodecIdentity Identity { get; } = new(
            "fake",
            "source2-vertex-v1",
            "test",
            ContentHash.Compute("geometry-analyzer-test"u8),
            "1");

        public byte[] DecodeVertexBuffer(byte[] encoded, int vertexCount, int vertexStride) =>
            truncateVertexDecode ? encoded[..^1] : encoded.ToArray();

        public byte[] DecodeIndexBuffer(byte[] encoded, int indexCount, int indexStride) => encoded.ToArray();

        public byte[] EncodeVertexBuffer(byte[] decoded, int vertexCount, int vertexStride, int version) => decoded.ToArray();

        public void Dispose()
        {
        }
    }
}
