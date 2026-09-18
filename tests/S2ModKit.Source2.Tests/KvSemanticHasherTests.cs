using S2ModKit.Adapters.Source2;
using ValveKeyValue;

namespace S2ModKit.Source2.Tests;

public sealed class KvSemanticHasherTests
{
    [Fact]
    public void CompleteHashPreservesScalarTypesAndExactFloatingPointBits()
    {
        var positiveZero = Object(
            ("single", new KVObject(+0f)),
            ("double", new KVObject(+0d)),
            ("integer", new KVObject(1)));
        var negativeSingleZero = Object(
            ("single", new KVObject(-0f)),
            ("double", new KVObject(+0d)),
            ("integer", new KVObject(1)));
        var widenedInteger = Object(
            ("single", new KVObject(+0f)),
            ("double", new KVObject(+0d)),
            ("integer", new KVObject(1L)));

        Assert.NotEqual(KvSemanticHasher.ComputeComplete(positiveZero), KvSemanticHasher.ComputeComplete(negativeSingleZero));
        Assert.NotEqual(KvSemanticHasher.ComputeComplete(positiveZero), KvSemanticHasher.ComputeComplete(widenedInteger));
        Assert.Equal(KvSemanticHasher.ComputeComplete(positiveZero), KvSemanticHasher.ComputeComplete(positiveZero));
    }

    [Fact]
    public void CompleteHashPreservesCollectionAndArrayOrder()
    {
        var first = Object(
            ("a", new KVObject(1)),
            ("b", Array(new KVObject("x"), new KVObject("y"))));
        var reorderedProperties = Object(
            ("b", Array(new KVObject("x"), new KVObject("y"))),
            ("a", new KVObject(1)));
        var reorderedArray = Object(
            ("a", new KVObject(1)),
            ("b", Array(new KVObject("y"), new KVObject("x"))));

        Assert.NotEqual(KvSemanticHasher.ComputeComplete(first), KvSemanticHasher.ComputeComplete(reorderedProperties));
        Assert.NotEqual(KvSemanticHasher.ComputeComplete(first), KvSemanticHasher.ComputeComplete(reorderedArray));
    }

    [Fact]
    public void CompleteHashIncludesFlagsAndBlobBytes()
    {
        var plain = Object(("payload", KVObject.Blob([1, 2, 3])));
        var changedBlob = Object(("payload", KVObject.Blob([1, 2, 4])));
        var resource = Object(("payload", KVObject.Blob([1, 2, 3])));
        resource["payload"].Flag = KVFlag.Resource;

        Assert.NotEqual(KvSemanticHasher.ComputeComplete(plain), KvSemanticHasher.ComputeComplete(changedBlob));
        Assert.NotEqual(KvSemanticHasher.ComputeComplete(plain), KvSemanticHasher.ComputeComplete(resource));
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
