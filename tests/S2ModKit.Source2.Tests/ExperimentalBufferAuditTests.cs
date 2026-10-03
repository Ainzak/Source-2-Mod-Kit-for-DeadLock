using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;

namespace S2ModKit.Source2.Tests;

public sealed class ExperimentalBufferAuditTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnchangedBuffersPassWhetherSelectedOrNot(bool selected)
    {
        var original = Buffer();
        Source2CompiledModelAdapter.VerifyExperimentalBufferLayout(original, original, [1, 2, 3], [1, 2, 3], selected);
    }

    [Fact]
    public void SelectedBytesAreAuditedSeparatelyButUnselectedBytesCannotChange()
    {
        var original = Buffer();
        Source2CompiledModelAdapter.VerifyExperimentalBufferLayout(original, original, [1, 2, 3], [1, 9, 3], selected: true);
        var exception = Assert.Throws<S2ModKitException>(() =>
            Source2CompiledModelAdapter.VerifyExperimentalBufferLayout(original, original, [1, 2, 3], [1, 9, 3], selected: false));
        Assert.Equal("EXPERIMENTAL_REOPEN_AUDIT_FAILED", exception.Error.Code);
    }

    [Theory]
    [InlineData("ordinal")]
    [InlineData("count")]
    [InlineData("stride")]
    [InlineData("format")]
    [InlineData("offset")]
    [InlineData("position-stride")]
    public void LayoutDriftRejectsEvenWhenSelectedAndBytesAreIdentical(string changedField)
    {
        var original = Buffer();
        var observed = changedField switch
        {
            "ordinal" => original with { Ordinal = 1 },
            "count" => original with { VertexCount = 2 },
            "stride" => original with { Stride = 16 },
            "format" => original with { PositionLayout = original.PositionLayout with { Format = "other" } },
            "offset" => original with { PositionLayout = original.PositionLayout with { Offset = 4 } },
            "position-stride" => original with { PositionLayout = original.PositionLayout with { Stride = 16 } },
            _ => throw new ArgumentOutOfRangeException(nameof(changedField)),
        };
        var exception = Assert.Throws<S2ModKitException>(() =>
            Source2CompiledModelAdapter.VerifyExperimentalBufferLayout(original, observed, [1, 2, 3], [1, 2, 3], selected: true));
        Assert.Equal("EXPERIMENTAL_REOPEN_AUDIT_FAILED", exception.Error.Code);
    }

    private static VertexBufferSnapshot Buffer()
    {
        var hash = ContentHash.Compute("buffer"u8);
        return new(0, 1, 1, 12, hash, hash, new("R32G32B32_FLOAT", 0, 12));
    }
}
