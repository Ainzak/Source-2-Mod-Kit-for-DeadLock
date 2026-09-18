using S2ModKit.Adapters.Source2;
using ValveKeyValue;
using ValveKeyValue.KeyValues3;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Source2.Tests;

public sealed class BinaryKv3BlockRoundTripTests
{
    [Fact]
    public void Lz4VersionFiveRoundTripIsDeterministicAndSemanticallyExact()
    {
        using var resource = new Resource();
        var root = Object(
            ("m_bounds", Object(
                ("m_vMinBounds", Array(-1d, -2d, -3d)),
                ("m_vMaxBounds", Array(1d, 2d, 3d)))),
            ("m_flGridCellSize", new KVObject(0.5d)),
            ("m_quantizedData", KVObject.Blob(Enumerable.Range(0, 64).Select(value => (byte)value).ToArray())));
        var block = new BinaryKV3(root, new KV3ID("generic", Guid.Parse("7412167c-06e9-4698-aff2-e63eb59037e7")), BlockType.DSTF)
        {
            SerializationVersion = 5,
            SerializationCompressionMethod = KV3BinaryCompressionMethod.Lz4,
            Resource = resource,
        };

        var first = BinaryKv3BlockRoundTrip.SerializeAndVerify(block, "synthetic DSTF");
        var second = BinaryKv3BlockRoundTrip.SerializeAndVerify(block, "synthetic DSTF");

        Assert.Equal(first.SemanticHash, second.SemanticHash);
        Assert.Equal(first.Payload.ToArray(), second.Payload.ToArray());
        Assert.Equal(5, first.SerializationVersion);
        Assert.Equal(KV3BinaryCompressionMethod.Lz4, first.CompressionMethod);
    }

    [Fact]
    public void RoundTripReflectsIntentionalScalarAndVectorMutation()
    {
        using var resource = new Resource();
        var root = Object(
            ("m_bounds", Object(
                ("m_vMinBounds", Array(-1d, -2d, -3d)),
                ("m_vMaxBounds", Array(1d, 2d, 3d)))),
            ("m_flGridCellSize", new KVObject(0.5d)));
        var block = new BinaryKV3(root, new KV3ID("generic", Guid.Parse("7412167c-06e9-4698-aff2-e63eb59037e7")), BlockType.DSTF)
        {
            SerializationVersion = 5,
            SerializationCompressionMethod = KV3BinaryCompressionMethod.Lz4,
            Resource = resource,
        };
        var before = KvSemanticHasher.ComputeComplete(root);

        root["m_flGridCellSize"] = new KVObject(0.75d);
        root["m_bounds"]["m_vMaxBounds"] = Array(1d, 2d, 4d);
        var result = BinaryKv3BlockRoundTrip.SerializeAndVerify(block, "mutated DSTF");

        Assert.NotEqual(before, result.SemanticHash);
        Assert.Equal(KvSemanticHasher.ComputeComplete(root), result.SemanticHash);
    }

    private static KVObject Object(params (string Key, KVObject Value)[] values)
    {
        var result = KVObject.Collection();
        foreach (var (key, value) in values)
        {
            result.Add(key, value);
        }

        return result;
    }

    private static KVObject Array(params double[] values)
    {
        var result = KVObject.Array();
        foreach (var value in values)
        {
            result.Add(new KVObject(value));
        }

        return result;
    }
}
