using System.Buffers.Binary;
using S2ModKit.Adapters.Source2;
using ValveKeyValue;
using ValveKeyValue.KeyValues3;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Source2.Tests;

public sealed class BinaryKv3ContainerCountsTests
{
    [Theory]
    [InlineData(KV3BinaryCompressionMethod.Uncompressed)]
    [InlineData(KV3BinaryCompressionMethod.Lz4)]
    public void Version4WritesActualContainerCountsWithoutChangingBody(KV3BinaryCompressionMethod compression)
    {
        var root = Tree();
        var original = Serialize(root, 4, compression);
        var result = BinaryKv3ContainerCounts.CompleteVersion4Header(original.ToArray(), root);

        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(44)));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(46)));
        Assert.Equal(original[..44], result[..44]);
        Assert.Equal(original[48..], result[48..]);
        using var resource = new Resource();
        var reopened = new BinaryKV3(BlockType.PHYS) { Resource = resource, Size = (uint)result.Length };
        using var stream = new MemoryStream(result);
        using var reader = new BinaryReader(stream);
        reopened.Read(reader);
        Assert.Equal(KvSemanticHasher.ComputeComplete(root), KvSemanticHasher.ComputeComplete(reopened.Data.Root));
        Assert.Equal(result, BinaryKv3ContainerCounts.CompleteVersion4Header(result.ToArray(), root));
    }

    [Fact]
    public void Version5RemainsByteIdentical()
    {
        var root = Tree();
        var original = Serialize(root, 5, KV3BinaryCompressionMethod.Lz4);
        Assert.Equal(original, BinaryKv3ContainerCounts.CompleteVersion4Header(original.ToArray(), root));
    }

    [Fact]
    public void RejectsConflictingCount()
    {
        var root = Tree();
        var bytes = Serialize(root, 4, KV3BinaryCompressionMethod.Uncompressed);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(44), 99);
        Assert.Throws<InvalidDataException>(() => BinaryKv3ContainerCounts.CompleteVersion4Header(bytes, root));
    }

    [Fact]
    public void RejectsTruncatedHeader()
    {
        Assert.Throws<InvalidDataException>(() => BinaryKv3ContainerCounts.CompleteVersion4Header([4, 0x33, 0x56, 0x4B], Tree()));
    }

    private static KVObject Tree()
    {
        var root = KVObject.Collection();
        var array = KVObject.Array();
        var child = KVObject.Collection();
        child.Add("empty", KVObject.Array());
        var numbers = KVObject.Array();
        numbers.Add(new KVObject(1d));
        numbers.Add(new KVObject(2d));
        child.Add("numbers", numbers);
        array.Add(child);
        root.Add("children", array);
        return root;
    }

    private static byte[] Serialize(KVObject root, int version, KV3BinaryCompressionMethod compression)
    {
        var block = new BinaryKV3(root, new KV3ID("generic", Guid.Parse("7412167c-06e9-4698-aff2-e63eb59037e7")), BlockType.PHYS)
        {
            Resource = null!,
            SerializationVersion = version,
            SerializationCompressionMethod = compression,
        };
        using var stream = new MemoryStream();
        block.Serialize(stream);
        return stream.ToArray();
    }
}
