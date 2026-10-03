using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Source2.Tests;

public sealed class Source2RewriteCapabilityTests
{
    private static readonly ContentHash InputHash = ContentHash.Compute("model"u8);
    private static readonly ContentHash BlockHash = ContentHash.Compute("block"u8);

    [Fact]
    public void EllipsoidContractCannotEnterAnyExistingWriter()
    {
        var adapter = new Source2CompiledModelAdapter();
        var original = RemovalPlan();
        Assert.False(adapter.CanRewrite(Model("MDAT", "MVTX", "MIDX"), original with { SchemaVersion = 4 }));
        Assert.False(adapter.CanRewrite(Model("MDAT", "MVTX", "MIDX"), original with
        { Operations = [original.Operations[0] with { Kind = "transform_component", Version = 7 }] }));
    }

    [Fact]
    public void RemovalRewriteRemainsRootBufferOnlyAfterMbufInspectionIsEnabled()
    {
        var adapter = new Source2CompiledModelAdapter();
        var plan = RemovalPlan();

        Assert.True(adapter.CanRewrite(Model("MDAT", "MVTX", "MIDX"), plan));
        Assert.False(adapter.CanRewrite(Model("MDAT", "MBUF", "PHYS"), plan));
    }

    [Fact]
    public void UniformAndPerAxisAffinePlansAreWritableWhenAttributeWriterIsAvailable()
    {
        var adapter = new Source2CompiledModelAdapter();
        var model = Model("MDAT", "MVTX", "MIDX");

        Assert.True(adapter.CanRewrite(model, AffinePlan(uniform: true)));
        Assert.True(adapter.CanRewrite(model, AffinePlan(uniform: false)));
    }

    [Fact]
    public async Task SharedLodMeshRemainsReadOnlyForEveryWriter()
    {
        var adapter = new Source2CompiledModelAdapter();
        var original = Model("MDAT", "MVTX", "MIDX");
        var shared = new MeshSnapshot("models/synthetic/accessory.vmdl_c", 0, 0, BlockHash, []);
        var model = original with
        {
            Lods = [new LodSnapshot(0, [shared]), new LodSnapshot(1, [shared])],
        };

        Assert.False(adapter.CanRewrite(model, RemovalPlan()));
        Assert.False(adapter.CanRewrite(model, AffinePlan(uniform: true)));
        var input = new ArtifactContent("models/synthetic/accessory.vmdl_c", InputHash, "model"u8.ToArray());
        Assert.Equal("SHARED_LOD_MESH_READ_ONLY", (await Assert.ThrowsAsync<S2ModKitException>(
            () => adapter.RewriteAsync(input, model, RemovalPlan(), TestContext.Current.CancellationToken))).Error.Code);
    }

    [Fact]
    public void SharedLodMaskExpansionIsBoundedByDeclaredLods()
    {
        Assert.Equal([0, 1, 2, 3], Source2CompiledModelAdapter.ExpandRootLodMask(15, 4, 0));
        Assert.Equal([0, 2], Source2CompiledModelAdapter.ExpandRootLodMask(5, 4, 0));
        Assert.Equal("LOD_MASK_UNSUPPORTED", Assert.Throws<S2ModKitException>(
            () => Source2CompiledModelAdapter.ExpandRootLodMask(0, 4, 0)).Error.Code);
        Assert.Equal("LOD_MASK_UNSUPPORTED", Assert.Throws<S2ModKitException>(
            () => Source2CompiledModelAdapter.ExpandRootLodMask(16, 4, 0)).Error.Code);
    }

    private static ModelSnapshot Model(params string[] blockTypes)
    {
        var blocks = blockTypes.Select((type, index) => new ResourceBlockSnapshot(type, index, index * 16, 16, BlockHash)).ToArray();
        var mdatIndex = Array.IndexOf(blockTypes, "MDAT");
        return new ModelSnapshot(
            new ArtifactSnapshot("models/synthetic/accessory.vmdl_c", InputHash, 128, blocks),
            mdatIndex < 0
                ? []
                : [new LodSnapshot(0, [new MeshSnapshot("models/synthetic/accessory.vmdl_c", 0, mdatIndex, BlockHash, [])])]);
    }

    [Fact]
    public async Task IncompleteMdatCoverageRejectsRemovalAndEveryTransformWriter()
    {
        var adapter = new Source2CompiledModelAdapter();
        var blocks = new[]
        {
            new ResourceBlockSnapshot("MDAT", 0, 0, 16, BlockHash),
            new ResourceBlockSnapshot("MVTX", 1, 16, 16, BlockHash),
            new ResourceBlockSnapshot("MIDX", 2, 32, 16, BlockHash),
            new ResourceBlockSnapshot("MDAT", 3, 48, 16, BlockHash),
        };
        var model = new ModelSnapshot(
            new ArtifactSnapshot("models/synthetic/accessory.vmdl_c", InputHash, 128, blocks),
            [new LodSnapshot(0, [new MeshSnapshot("models/synthetic/accessory.vmdl_c", 0, 0, BlockHash, [])])]);

        Assert.False(adapter.CanRewrite(model, RemovalPlan()));
        Assert.False(adapter.CanRewrite(model, AffinePlan(uniform: true)));
        Assert.False(adapter.CanRewrite(model, AffinePlan(uniform: false)));
        var input = new ArtifactContent("models/synthetic/accessory.vmdl_c", InputHash, "model"u8.ToArray());
        Assert.Equal("MDAT_COVERAGE_INCOMPLETE", (await Assert.ThrowsAsync<S2ModKitException>(
            () => adapter.RewriteAsync(input, model, RemovalPlan(), TestContext.Current.CancellationToken))).Error.Code);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void IncompleteMdatCoverageRejectsLegacyTransformVersionsWithCompleteCoverageControls(int version)
    {
        var adapter = new Source2CompiledModelAdapter();
        var plan = LegacyTransformPlan(version);

        Assert.False(adapter.CanRewrite(ModelWithMdatCoverage(includeSecondMdat: false), plan));
        Assert.True(adapter.CanRewrite(ModelWithMdatCoverage(includeSecondMdat: true), plan));
    }

    [Fact]
    public async Task ForgedCompleteSnapshotCannotBypassStrictInputValidation()
    {
        var adapter = new Source2CompiledModelAdapter();
        var model = Model("MDAT", "MVTX", "MIDX");
        var input = new ArtifactContent("models/synthetic/accessory.vmdl_c", InputHash, "model"u8.ToArray());

        var exception = await Assert.ThrowsAsync<S2ModKitException>(
            () => adapter.RewriteAsync(input, model, RemovalPlan(), TestContext.Current.CancellationToken));

        // The strict input reparse rejects the non-model bytes before any snapshot comparison or
        // write, so a complete fabricated snapshot alone can never authorize mutation.
        Assert.Equal("RESOURCE_SIZE_UNSUPPORTED", exception.Error.Code);
    }

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

    private static ModelSnapshot ModelWithMdatCoverage(bool includeSecondMdat)
    {
        var types = new[] { "MDAT", "MVTX", "MIDX", "MDAT", "MBUF", "PHYS" };
        var blocks = types.Select((type, index) => new ResourceBlockSnapshot(type, index, index * 16, 16, BlockHash)).ToArray();
        var meshes = new List<MeshSnapshot>
        {
            new("models/synthetic/accessory.vmdl_c", 0, 0, BlockHash, []),
        };
        if (includeSecondMdat)
        {
            meshes.Add(new MeshSnapshot("models/synthetic/accessory.vmdl_c", 1, 3, BlockHash, []));
        }

        return new ModelSnapshot(
            new ArtifactSnapshot("models/synthetic/accessory.vmdl_c", InputHash, 128, blocks),
            [new LodSnapshot(0, meshes)]);
    }

    private static MutationPlan LegacyTransformPlan(int version)
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
        var zero = new TransformVector3();
        var geometry = new PlannedGeometryTarget(
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
            bounds,
            bounds,
            zero,
            2f,
            zero,
            1f,
            ["position"],
            new GeometryCodecIdentity("meshoptimizer", "vertex-v1", "win-x64", ContentHash.Compute("codec"u8), "1"))
        {
            BoneBoundsTargets = version == 1
                ?
                [
                    new PlannedBoneBoundsTarget(
                        0,
                        "root",
                        ContentHash.Compute("bind"u8),
                        ContentHash.Compute("influenced"u8),
                        3,
                        zero,
                        zero,
                        bounds,
                        zero,
                        zero,
                        bounds,
                        zero,
                        zero,
                        1f,
                        1f),
                ]
                : [],
            ConnectedComponentIds = version == 3 ? ["island-0"] : [],
        };

        // Version 3 plans carry only the vertex-buffer target block, matching the
        // connected-component planner; versions 1 additionally target the MDAT whose bone
        // bounds change, and version 2 targets the coupled MDAT/MBUF/PHYS trio.
        var blocks = version == 2
            ? new[]
            {
                new PlannedTargetBlock(0, "MDAT", BlockHash),
                new PlannedTargetBlock(4, "MBUF", BlockHash),
                new PlannedTargetBlock(5, "PHYS", BlockHash),
            }
            : version == 3
                ?
                [
                    new PlannedTargetBlock(1, "MVTX", BlockHash),
                ]
                :
                [
                    new PlannedTargetBlock(0, "MDAT", BlockHash),
                    new PlannedTargetBlock(1, "MVTX", BlockHash),
                ];
        var operation = new PlannedOperation("legacy-transform", "transform_component", version, [selected], blocks);
        if (version == 2)
        {
            var derived = new PlannedConvexDerivedValues(bounds, zero, 1f, 1f, 1f, zero, []);
            var visual = new PlannedRawMbufTransformTarget(
                0, 4, BlockHash, BlockHash, ContentHash.Compute("mbuf-after"u8),
                ContentHash.Compute("decoded-mbuf"u8), ContentHash.Compute("expected-mbuf"u8),
                ContentHash.Compute("index-data"u8), ContentHash.Compute("vertex-set"u8),
                selected.DrawCallId, selected.MaterialPath, "root", 1, 3,
                new PositionLayout("R32G32B32_FLOAT", 0, 24), bounds, bounds, zero,
                1f, 1f, 2f, [], []);
            var collision = new PlannedConvexPhysTransformTarget(
                5, BlockHash, ContentHash.Compute("phys-positions"u8), ContentHash.Compute("phys-after"u8),
                4, zero, 1f, 1f, 2f, derived, derived, [], [],
                ContentHash.Compute("hull-edges"u8), ContentHash.Compute("half-edges"u8),
                ContentHash.Compute("faces"u8), ContentHash.Compute("region-nodes"u8),
                ContentHash.Compute("hull-planes"u8), ContentHash.Compute("region-planes"u8),
                0, 0, 0, zero, "default", []);
            operation = operation with
            {
                CoupledTransformTarget = new PlannedCoupledTransformTarget(visual, collision, blocks),
            };
        }
        else
        {
            operation = operation with { GeometryTargets = [geometry] };
        }

        return new MutationPlan($"synthetic-transform-v{version}", InputHash, ContentHash.Compute("legacy-plan"u8), [operation]);
    }
}
