using S2ModKit.Adapters.Preview;
using S2ModKit.Adapters.Source2;

namespace S2ModKit.Desktop.Tests;

public sealed class MaterialExpressionTests
{
    /// <summary>The glow pulse the build writes evaluates to a * sin(b * t) + c at any time, so the preview pulses like the game.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.4f)]
    [InlineData(3.7f)]
    public void TheBuiltPulseEvaluatesToItsSineWave(float seconds)
    {
        var program = CompiledMaterialLook.PulseExpression(0.6f, 2.5f, 1f);

        var value = MaterialExpression.Evaluate(program, seconds);

        Assert.NotNull(value);
        Assert.Equal((0.6f * MathF.Sin(2.5f * seconds)) + 1f, value.Value, 4);
        Assert.True(MaterialExpression.UsesTime(program));
    }

    [Fact]
    public void SubtractionDivisionAndNegationFollowTheStackOrder()
    {
        // -(8 - 2) / 3 = -2
        byte[] program = [0x07, .. BitConverter.GetBytes(8f), 0x07, .. BitConverter.GetBytes(2f), 0x14, 0x18, 0x07, .. BitConverter.GetBytes(3f), 0x16, 0x00];

        Assert.Equal(-2f, MaterialExpression.Evaluate(program, 0f));
        Assert.False(MaterialExpression.UsesTime(program));                // a fixed value: nothing to animate
    }

    /// <summary>Unknown operations, unknown functions, a missing end and a broken stack leave the material's fixed value.</summary>
    [Fact]
    public void AnExpressionThatIsNotUnderstoodGivesNothing()
    {
        byte[] constant = [0x07, .. BitConverter.GetBytes(1f)];

        Assert.Null(MaterialExpression.Evaluate([.. constant, 0x09, 0x00], 0f));             // a variable
        Assert.Null(MaterialExpression.Evaluate([.. constant, 0x06, 0x05, 0x00, 0x00], 0f));  // an unknown function
        Assert.Null(MaterialExpression.Evaluate(constant, 0f));                                // no end
        Assert.Null(MaterialExpression.Evaluate([.. constant, .. constant, 0x00], 0f));       // two values left
        Assert.Null(MaterialExpression.Evaluate([0x15, 0x00], 0f));                           // nothing to multiply
        Assert.Null(MaterialExpression.Evaluate([0x07, 0x00, 0x00], 0f));                     // a cut float
    }
}
