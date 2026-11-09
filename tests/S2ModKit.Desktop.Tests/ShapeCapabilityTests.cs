using S2ModKit.Desktop;
using S2ModKit.Domain;

namespace S2ModKit.Desktop.Tests;

public sealed class ShapeCapabilityTests
{
    private static ComponentCapability Transform(int version, bool available, string code = "COMPONENT_CAPABILITY_AVAILABLE", string summary = "ok") =>
        new("transform_component", version, available ? CapabilityAvailability.Available : "unsupported", [new ComponentCapabilityReason(code, summary)], []);

    [Fact]
    public void TheHighestAvailableVersionDecidesWhatTheShapeToolAllows()
    {
        Assert.Equal(ShapeLevel.Full, ShapeCapabilities.Level([Transform(1, true), Transform(4, true)]).Level);
        Assert.Equal(ShapeLevel.EvenSizeAndMove, ShapeCapabilities.Level([Transform(1, true), Transform(4, false, "AFFINE_BOUNDS_UNSUPPORTED")]).Level);
        Assert.Equal(ShapeLevel.EvenSize, ShapeCapabilities.Level([Transform(1, false), Transform(2, true)]).Level);
        Assert.Equal(ShapeLevel.None, ShapeCapabilities.Level([Transform(1, false, "TRANSFORM_SOURCE2_PROFILE_UNSUPPORTED"), Transform(4, false)]).Level);
        Assert.Equal(ShapeLevel.None, ShapeCapabilities.Level([new ComponentCapability("remove_component", 1, CapabilityAvailability.Available, [], [])]).Level);
    }

    [Fact]
    public void RefusalsAreExplainedInPlainWords()
    {
        var shared = ShapeCapabilities.Level([Transform(1, false, "TRANSFORM_SOURCE2_PROFILE_UNSUPPORTED"), Transform(4, false, "AFFINE_LAYOUT_UNSUPPORTED")]);
        Assert.Contains("shares its internal mesh", shared.Reason, StringComparison.Ordinal);

        var partial = ShapeCapabilities.Level([Transform(1, true), Transform(4, false, "AFFINE_BOUNDS_UNSUPPORTED")]);
        Assert.StartsWith("Only an even size and a move:", partial.Reason, StringComparison.Ordinal);
        Assert.Contains("size data", partial.Reason, StringComparison.Ordinal);                // why per-axis and rotation are missing

        Assert.Contains("several bones", ShapeCapabilities.Plain("AFFINE_LAYOUT_UNSUPPORTED", "declares blend weight count 4"), StringComparison.Ordinal);
        Assert.Contains("vertex layout", ShapeCapabilities.Plain("AFFINE_LAYOUT_UNSUPPORTED", "packed frame"), StringComparison.Ordinal);
        Assert.Contains("no levels of detail", ShapeCapabilities.Plain("LOD_MASK_UNSUPPORTED", "x"), StringComparison.Ordinal);
        Assert.Equal("an analyzer sentence", ShapeCapabilities.Plain("SOMETHING_NEW", "an analyzer sentence"));   // unknown codes keep their own words
    }

    [Theory]
    [InlineData("models/heroes_wip/x/materials/Gun.vmat", "models/heroes_wip/x/materials/gun.vmat")]
    [InlineData("models\\heroes_wip\\x\\gun.vmat_c", "models/heroes_wip/x/gun.vmat")]
    public void MaterialsMatchRegardlessOfCaseSlashesAndCompiledSuffix(string material, string expected) =>
        Assert.Equal(expected, ShapeCapabilities.Normalize(material));

    [Fact]
    public async Task ABuildWithNothingChangedIsRefused()
    {
        using var root = new ModTestRoot();
        var error = await Assert.ThrowsAsync<S2ModKitException>(() => root.Catalog().BuildEditsAsync(null, "models/x/x.vmdl", new HashSet<int>(), [], null, null, TestContext.Current.CancellationToken));
        Assert.Equal("MODEL_EDIT_NOTHING", error.Error.Code);
    }
}
