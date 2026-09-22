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

    [Fact]
    public void OnlyUniformPositionOnlyAffinePlanIsWritableBeforeAttributeWriter()
    {
        var adapter = new Source2CompiledModelAdapter();
        var model = Model("MDAT", "MVTX", "MIDX");

        Assert.True(adapter.CanRewrite(model, AffinePlan(uniform: true)));
        Assert.False(adapter.CanRewrite(model, AffinePlan(uniform: false)));
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

    private static MutationPlan AffinePlan(bool uniform)
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
        var bounds = new GeometryBounds(new TransformVector3(), new TransformVector3 { X = 1, Y = 1, Z = 1 });
        var frameHash = ContentHash.Compute("frame"u8);
        var geometry = new PlannedAffineGeometryTarget(
            0,
            selected.ResourcePath,
            0,
            0,
            0,
            0,
            1,
            2,
            BlockHash,
            BlockHash,
            ContentHash.Compute("decoded"u8),
            ContentHash.Compute("expected"u8),
            ContentHash.Compute("indices"u8),
            ContentHash.Compute("vertices"u8),
            3,
            new PositionLayout("R32G32B32_FLOAT", 0, 24),
            new PackedFrameLayout("R32_UINT", 16, 24, Source2PackedFrameCodec.EncodingProfile),
            bounds,
            bounds,
            bounds,
            bounds,
            1,
            frameHash,
            uniform ? frameHash : ContentHash.Compute("changed-frame"u8),
            uniform ? ["position"] : ["normal_tangent", "position"],
            new GeometryCodecIdentity("meshoptimizer", "vertex-v1", "win-x64", ContentHash.Compute("codec"u8), "1"))
        {
            BoneBoundsTargets =
            [
                new PlannedAffineBoneBoundsTarget(
                    0,
                    "bone",
                    ContentHash.Compute("bind"u8),
                    ContentHash.Compute("influenced"u8),
                    3,
                    bounds,
                    bounds,
                    1,
                    1),
            ],
        };
        var identity = new TransformMatrix3(1, 0, 0, 0, 1, 0, 0, 0, 1);
        var affine = new PlannedAffineTransformTarget(
            "draw_call_vertices",
            "root_mvtx_affine",
            1,
            new ResolvedTransformPivot("explicit_point", new TransformVector3(), "model", "point", ContentHash.Compute("pivot"u8), null),
            new ResolvedTransformFrame("model", null, identity, identity, "model", ContentHash.Compute("frame-source"u8)),
            uniform
                ? new TransformVector3 { X = 2, Y = 2, Z = 2 }
                : new TransformVector3 { X = 2, Y = 1, Z = 1 },
            new TransformRotation { Kind = "identity" },
            new TransformVector3(),
            identity,
            1,
            32,
            [geometry]);
        var operation = new PlannedOperation(
            "affine-accessory",
            "transform_component",
            4,
            [selected],
            [new PlannedTargetBlock(0, "MDAT", BlockHash), new PlannedTargetBlock(1, "MVTX", BlockHash)])
        {
            AffineTransformTarget = affine,
        };
        return new MutationPlan("synthetic-affine", InputHash, ContentHash.Compute("affine-plan"u8), [operation]);
    }
}
