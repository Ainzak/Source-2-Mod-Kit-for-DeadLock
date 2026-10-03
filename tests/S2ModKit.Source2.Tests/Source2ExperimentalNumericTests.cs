using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using ValveKeyValue;

namespace S2ModKit.Source2.Tests;

public sealed class Source2ExperimentalNumericTests
{
    [Fact]
    public void IntegerConversionRejectsBitsLostBeforeBinary32Comparison()
    {
        Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.ExperimentalFloat(new KVObject(9_007_199_254_740_993L)));
        Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.ExperimentalFloat(new KVObject(ulong.MaxValue)));
        Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.ExperimentalFloat(new KVObject(16_777_217)));
        Assert.Equal(MathF.Pow(2, 60), Source2CompiledModelAdapter.ExperimentalFloat(new KVObject(1L << 60)));
    }

    [Fact]
    public void ExactSerializedFloatWordsAndSignedZeroSurviveAdmission()
    {
        foreach (var value in new[] { -0f, float.Epsilon, MathF.BitIncrement(1), float.MaxValue })
        {
            var result = Source2CompiledModelAdapter.ExperimentalFloat(new KVObject((double)value));
            Assert.Equal(BitConverter.SingleToUInt32Bits(value), BitConverter.SingleToUInt32Bits(result));
        }
    }

    [Fact]
    public void NonfiniteInexactAndNonnumericFieldsReject()
    {
        foreach (var field in new[] { new KVObject(double.NaN), new KVObject(double.PositiveInfinity),
            new KVObject(double.MaxValue), new KVObject(0.1d), new KVObject("1") })
            Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.ExperimentalFloat(field));
    }
}
