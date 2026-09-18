using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using ValveKeyValue;

namespace S2ModKit.Source2.Tests;

public sealed class KvNumericMutationTests
{
    [Fact]
    public void ReplaceVectorPreservesElementTypesAndFlags()
    {
        var vector = KVObject.Array();
        vector.Flag = KVFlag.ResourceName;
        vector.Add(new KVObject(1f) { Flag = KVFlag.None });
        vector.Add(new KVObject(2d) { Flag = KVFlag.Resource });
        vector.Add(new KVObject(3f) { Flag = KVFlag.SoundEvent });
        var root = KVObject.Collection();
        root.Add("value", vector);

        KvNumericMutation.ReplaceVector3(
            root,
            "value",
            new TransformVector3 { X = 1f, Y = 2f, Z = 3f },
            new TransformVector3 { X = 4f, Y = 5f, Z = 6f },
            "root");

        Assert.Equal(KVFlag.ResourceName, root["value"].Flag);
        Assert.Equal(KVValueType.FloatingPoint, root["value"][0].ValueType);
        Assert.Equal(KVValueType.FloatingPoint64, root["value"][1].ValueType);
        Assert.Equal(KVFlag.Resource, root["value"][1].Flag);
        Assert.Equal(6f, root["value"][2].ToSingle(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ReplaceScalarRejectsPlanDriftWithoutMutation()
    {
        var root = KVObject.Collection();
        root.Add("value", new KVObject(2d));

        var exception = Assert.Throws<InvalidDataException>(() => KvNumericMutation.ReplaceSingle(
            root,
            "value",
            3f,
            4f,
            "root"));

        Assert.Contains("drifted", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2d, root["value"].ToDouble(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ReplaceFloatArrayPreservesElementTypesAndFlags()
    {
        var values = KVObject.Array();
        values.Flag = KVFlag.ResourceName;
        values.Add(new KVObject(1d) { Flag = KVFlag.Resource });
        values.Add(new KVObject(2f) { Flag = KVFlag.SoundEvent });
        var root = KVObject.Collection();
        root.Add("value", values);

        KvNumericMutation.ReplaceFloatArray(root, "value", [1f, 2f], [3f, 4f], "root");

        Assert.Equal(KVFlag.ResourceName, root["value"].Flag);
        Assert.Equal(KVValueType.FloatingPoint64, root["value"][0].ValueType);
        Assert.Equal(KVFlag.Resource, root["value"][0].Flag);
        Assert.Equal(KVValueType.FloatingPoint, root["value"][1].ValueType);
        Assert.Equal(KVFlag.SoundEvent, root["value"][1].Flag);
    }

    [Fact]
    public void ReplaceFloatArrayRejectsNonFloatingStorage()
    {
        var values = KVObject.Array();
        values.Add(new KVObject(1));
        values.Add(new KVObject(2f));
        var root = KVObject.Collection();
        root.Add("value", values);

        Assert.Throws<InvalidDataException>(() =>
            KvNumericMutation.ReplaceFloatArray(root, "value", [1f, 2f], [3f, 4f], "root"));
    }

    [Fact]
    public void ReplaceVectorRejectsNonFloatingStorage()
    {
        var vector = KVObject.Array();
        vector.Add(new KVObject(1));
        vector.Add(new KVObject(2f));
        vector.Add(new KVObject(3f));
        var root = KVObject.Collection();
        root.Add("value", vector);

        Assert.Throws<InvalidDataException>(() => KvNumericMutation.ReplaceVector3(
            root,
            "value",
            new TransformVector3 { X = 1f, Y = 2f, Z = 3f },
            new TransformVector3 { X = 4f, Y = 5f, Z = 6f },
            "root"));
    }
}
