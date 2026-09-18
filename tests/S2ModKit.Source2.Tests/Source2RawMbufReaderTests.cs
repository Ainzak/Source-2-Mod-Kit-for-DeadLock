using System.Buffers.Binary;
using System.Text;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using ValveKeyValue;

namespace S2ModKit.Source2.Tests;

public sealed class Source2RawMbufReaderTests
{
    private const int VertexDescriptorOffset = 0x10;
    private const int IndexDescriptorOffset = 0x28;
    private const int LayoutOffset = 0x40;
    private const int VertexDataOffset = 0x120;
    private const int IndexDataOffset = 0x168;

    [Fact]
    public void ReadsRawMbufIntoExistingGeometryAndOwnershipFacts()
    {
        var payload = Fixture();

        var result = Source2RawMbufReader.AnalyzeDetailed(
            Block(payload),
            [DrawCall(3, 3)],
            "synthetic accessory");

        Assert.Equal(VertexDataOffset, result.VertexDataOffset);
        Assert.Equal(72, result.VertexDataLength);
        Assert.Equal(IndexDataOffset, result.IndexDataOffset);
        Assert.Equal(6, result.IndexDataLength);
        Assert.Equal(20, result.BlendIndicesOffset);
        Assert.Equal("ready", result.Geometry.Snapshot.Status);
        Assert.Null(result.Geometry.Snapshot.Codec);
        var vertex = Assert.Single(result.Geometry.VertexBuffers);
        Assert.Equal(3, vertex.Snapshot.VertexCount);
        Assert.Equal(24, vertex.Snapshot.Stride);
        Assert.Equal(vertex.Snapshot.EncodedHash, vertex.Snapshot.DecodedHash);
        var indices = Assert.Single(result.Geometry.IndexBuffers);
        Assert.Equal([0u, 1u, 2u], indices.Indices);
        var drawCall = Assert.Single(result.Geometry.DrawCalls).Snapshot;
        Assert.Equal(3, drawCall.UniqueVertexCount);
        Assert.True(drawCall.ExclusivelyOwned);
        Assert.Equal(-2, drawCall.Bounds.Min.X);
        Assert.Equal(4, drawCall.Bounds.Max.Y);
    }

    [Fact]
    public void RejectsDescriptorCountsOutsideTheSingleBufferProfile()
    {
        var payload = Fixture();
        WriteUInt32(payload, 4, 2);

        AssertCode("MBUF_COUNT_UNSUPPORTED", payload);
    }

    [Fact]
    public void RejectsDescriptorOffsetOutsidePayload()
    {
        var payload = Fixture();
        WriteInt32(payload, 0, payload.Length);

        AssertCode("MBUF_DESCRIPTOR_RANGE_INVALID", payload);
    }

    [Theory]
    [InlineData(VertexDescriptorOffset + 4, 20u)]
    [InlineData(LayoutOffset + 36, 2u)]
    public void RejectsStrideOrFormatDrift(int offset, uint value)
    {
        var payload = Fixture();
        WriteUInt32(payload, offset, value);

        AssertCode("MBUF_LAYOUT_UNSUPPORTED", payload);
    }

    [Fact]
    public void RejectsIndexOutsideVertexRange()
    {
        var payload = Fixture();
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(IndexDataOffset + 4), 3);

        AssertCode("MBUF_INDEX_RANGE_INVALID", payload);
    }

    [Fact]
    public void RejectsOverlappingDataRanges()
    {
        var payload = Fixture();
        WriteRelative(payload, IndexDescriptorOffset + 16, VertexDataOffset);

        AssertCode("MBUF_DESCRIPTOR_RANGE_INVALID", payload);
    }

    [Fact]
    public void RejectsTruncatedDataRange()
    {
        var payload = Fixture()[..^1];

        AssertCode("MBUF_DESCRIPTOR_RANGE_INVALID", payload);
    }

    [Fact]
    public void RejectsCompressedStoredLength()
    {
        var payload = Fixture();
        WriteUInt32(payload, VertexDescriptorOffset + 20, 71);

        AssertCode("MBUF_COMPRESSION_UNSUPPORTED", payload);
    }

    [Fact]
    public void RejectsIncompleteVertexOwnership()
    {
        var payload = Fixture();
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(IndexDataOffset + 4), 1);

        AssertCode("MBUF_OWNERSHIP_UNSUPPORTED", payload);
    }

    [Fact]
    public void RejectsPartialDrawCallOwnership()
    {
        var payload = Fixture();

        var exception = Assert.Throws<S2ModKitException>(() => Source2RawMbufReader.AnalyzeDetailed(
            Block(payload),
            [DrawCall(2, 3)],
            "synthetic accessory"));

        Assert.Equal("MBUF_OWNERSHIP_UNSUPPORTED", exception.Error.Code);
    }

    [Fact]
    public void RejectsNonzeroOptionalAppliedIndexOffset()
    {
        var payload = Fixture();

        var exception = Assert.Throws<S2ModKitException>(() => Source2RawMbufReader.AnalyzeDetailed(
            Block(payload),
            [DrawCall(3, 3, 1)],
            "synthetic accessory"));

        Assert.Equal("MBUF_OWNERSHIP_UNSUPPORTED", exception.Error.Code);
    }

    private static void AssertCode(string expected, byte[] payload)
    {
        var exception = Assert.Throws<S2ModKitException>(() => Source2RawMbufReader.AnalyzeDetailed(
            Block(payload),
            [DrawCall(3, 3)],
            "synthetic accessory"));

        Assert.Equal(expected, exception.Error.Code);
    }

    private static Source2ResourceBlock Block(byte[] payload) => new(
        1,
        "MBUF",
        0,
        0,
        payload,
        ReadOnlyMemory<byte>.Empty);

    private static GeometryDrawCallInput DrawCall(int vertexCount, int indexCount, int? appliedIndexOffset = null)
    {
        var snapshot = DrawCallSnapshot.Create(
            "models/synthetic/accessory.vmdl_c",
            0,
            0,
            0,
            "materials/synthetic/accessory.vmat",
            0,
            indexCount);
        var data = Object(
            ("m_nPrimitiveType", "RENDER_PRIM_TRIANGLES"),
            ("m_nBaseVertex", 0),
            ("m_nVertexCount", vertexCount),
            ("m_indexBuffer", BufferReference()),
            ("m_vertexBuffers", Array(BufferReference())));
        if (appliedIndexOffset is not null)
        {
            data.Add("m_nAppliedIndexOffset", appliedIndexOffset.Value);
        }

        return new GeometryDrawCallInput(snapshot, data);
    }

    private static byte[] Fixture()
    {
        var result = new byte[IndexDataOffset + 6];
        WriteRelative(result, 0, VertexDescriptorOffset);
        WriteUInt32(result, 4, 1);
        WriteRelative(result, 8, IndexDescriptorOffset);
        WriteUInt32(result, 12, 1);

        WriteUInt32(result, VertexDescriptorOffset, 3);
        WriteUInt32(result, VertexDescriptorOffset + 4, 24);
        WriteRelative(result, VertexDescriptorOffset + 8, LayoutOffset);
        WriteUInt32(result, VertexDescriptorOffset + 12, 4);
        WriteRelative(result, VertexDescriptorOffset + 16, VertexDataOffset);
        WriteUInt32(result, VertexDescriptorOffset + 20, 72);

        WriteUInt32(result, IndexDescriptorOffset, 3);
        WriteUInt32(result, IndexDescriptorOffset + 4, 2);
        WriteInt32(result, IndexDescriptorOffset + 8, 0);
        WriteUInt32(result, IndexDescriptorOffset + 12, 0);
        WriteRelative(result, IndexDescriptorOffset + 16, IndexDataOffset);
        WriteUInt32(result, IndexDescriptorOffset + 20, 6);

        WriteLayout(result, 0, "POSITION", 6, 0);
        WriteLayout(result, 1, "TEXCOORD", 37, 12);
        WriteLayout(result, 2, "NORMAL", 42, 16);
        WriteLayout(result, 3, "BLENDINDICES", 30, 20);
        WriteVertex(result, 0, -2, 0, 1);
        WriteVertex(result, 1, 1, 4, 2);
        WriteVertex(result, 2, 3, 2, -1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(IndexDataOffset), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(IndexDataOffset + 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(IndexDataOffset + 4), 2);
        return result;
    }

    private static void WriteLayout(byte[] bytes, int ordinal, string semantic, uint format, uint offset)
    {
        var start = LayoutOffset + (ordinal * 56);
        Encoding.ASCII.GetBytes(semantic).CopyTo(bytes, start);
        WriteUInt32(bytes, start + 32, 0);
        WriteUInt32(bytes, start + 36, format);
        WriteUInt32(bytes, start + 40, offset);
        WriteUInt32(bytes, start + 44, 0);
        WriteUInt32(bytes, start + 48, 0);
        WriteUInt32(bytes, start + 52, 0);
    }

    private static void WriteVertex(byte[] bytes, int ordinal, float x, float y, float z)
    {
        var start = VertexDataOffset + (ordinal * 24);
        WriteSingle(bytes, start, x);
        WriteSingle(bytes, start + 4, y);
        WriteSingle(bytes, start + 8, z);
        bytes.AsSpan(start + 12, 8).Fill(0x5a);
        bytes[start + 20] = 0;
        bytes.AsSpan(start + 21, 3).Fill(0x7f);
    }

    private static void WriteRelative(byte[] bytes, int fieldOffset, int targetOffset) =>
        WriteInt32(bytes, fieldOffset, checked(targetOffset - fieldOffset));

    private static void WriteSingle(byte[] bytes, int offset, float value) =>
        WriteInt32(bytes, offset, BitConverter.SingleToInt32Bits(value));

    private static void WriteInt32(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, sizeof(int)), value);

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)), value);

    private static KVObject BufferReference() => Object(
        ("m_hBuffer", 0),
        ("m_nBindOffsetBytes", 0));

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
}
