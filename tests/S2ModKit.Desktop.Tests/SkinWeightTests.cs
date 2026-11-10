using S2ModKit.Adapters.Preview;
using ValveResourceFormat.Blocks;

namespace S2ModKit.Desktop.Tests;

public sealed class SkinWeightTests
{
    private static VBIB.OnDiskBufferData Buffer(string format, byte[] data, int stride)
    {
        var field = new VBIB.RenderInputLayoutField { SemanticName = "BLENDWEIGHT", SemanticIndex = 0, Offset = 0 };
        var formatField = typeof(VBIB.RenderInputLayoutField).GetField(nameof(VBIB.RenderInputLayoutField.Format))!;
        object boxed = field;
        formatField.SetValue(boxed, Enum.Parse(formatField.FieldType, format));
        return new VBIB.OnDiskBufferData { ElementCount = (uint)(data.Length / stride), ElementSizeInBytes = (uint)stride, InputLayoutFields = [(VBIB.RenderInputLayoutField)boxed], Data = data };
    }

    [Fact]
    public void EightInfluenceWeightsStayWithTheirOwnVertex()
    {
        // Abrams' coat, first two vertices: eight one-byte weights in a 4 x 16-bit field
        var buffer = Buffer("R16G16B16A16_UNORM", [249, 5, 1, 0, 0, 0, 0, 0, 153, 86, 15, 1, 0, 0, 0, 0], 8);

        var weights = SkinWeights.Read(buffer, buffer.InputLayoutFields[0], 2, 8);

        Assert.Equal(16, weights.Length);
        Assert.Equal(153 / 255f, weights[8], 3);                            // vertex 1 keeps its own weights
        Assert.Equal(86 / 255f, weights[9], 3);
        Assert.Equal(1f, weights.AsSpan(8, 8).ToArray().Sum(), 2);
    }

    [Fact]
    public void FourInfluenceWeightsAndMissingWeights()
    {
        var buffer = Buffer("R8G8B8A8_UNORM", [255, 0, 0, 0, 128, 127, 0, 0], 4);

        var weights = SkinWeights.Read(buffer, buffer.InputLayoutFields[0], 2, 4);
        Assert.Equal(128 / 255f, weights[4], 3);

        var none = SkinWeights.Read(buffer, default, 2, 4);
        Assert.Equal([1f, 0, 0, 0, 1f, 0, 0, 0], none);                     // no weights: the first bone has all of it
    }
}
