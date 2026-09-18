using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;

namespace S2ModKit.Source2.Tests;

public sealed class Source2RewriteCapabilityTests
{
    private static readonly ContentHash InputHash = ContentHash.Compute("model"u8);
    private static readonly ContentHash BlockHash = ContentHash.Compute("block"u8);

    [Fact]
    public void RemovalRewriteRemainsRootBufferOnlyAfterMbufInspectionIsEnabled()
    {
        var adapter = new Source2CompiledModelAdapter();
        var plan = RemovalPlan();

        Assert.True(adapter.CanRewrite(Model("MDAT", "MVTX", "MIDX"), plan));
        Assert.False(adapter.CanRewrite(Model("MDAT", "MBUF", "PHYS"), plan));
    }

    private static ModelSnapshot Model(params string[] blockTypes) => new(
        new ArtifactSnapshot(
            "models/synthetic/accessory.vmdl_c",
            InputHash,
            128,
            blockTypes.Select((type, index) => new ResourceBlockSnapshot(type, index, index * 16, 16, BlockHash)).ToArray()),
        []);

    private static MutationPlan RemovalPlan()
    {
        var selected = new SelectedDrawCall(
            0,
            "models/synthetic/accessory.vmdl_c",
            0,
            0,
            "draw-call",
            "materials/synthetic/accessory.vmat",
            0,
            0,
            3);
        var operation = new PlannedOperation(
            "remove-accessory",
            "remove_component",
            1,
            [selected],
            [new PlannedTargetBlock(0, "MDAT", BlockHash)]);
        return new MutationPlan("synthetic-removal", InputHash, ContentHash.Compute("plan"u8), [operation]);
    }
}
