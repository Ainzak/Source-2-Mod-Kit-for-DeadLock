using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed class TransformPlanningTests
{
    [Fact]
    public void CreatePlanUsesTypedAdapterResultAndIsDeterministic()
    {
        var (artifact, model) = CreateModel();
        var recipe = CreateRecipe(artifact.ContentHash, 1.2f);
        var planner = new SyntheticTransformPlanner(model);

        var first = MutationPlanner.CreatePlan(model, recipe, artifact, planner);
        var second = MutationPlanner.CreatePlan(model, recipe, artifact, planner);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        var operation = Assert.Single(first.Operations);
        Assert.Equal("transform_component", operation.Kind);
        Assert.Equal(3, operation.SelectedDrawCalls.Count);
        Assert.Equal(3, operation.GeometryTargets.Count);
        Assert.Empty(operation.DistanceFieldTargets);
        Assert.Equal([0, 1, 3, 4, 6, 7], operation.TargetBlocks.Select(block => block.Index));
        Assert.All(operation.GeometryTargets, target =>
        {
            Assert.Equal(3, target.SelectedVertexCount);
            Assert.Equal(["position"], target.AllowedChangedAttributes);
            Assert.Equal(1.2f, target.UniformScale);
        });
    }

    [Fact]
    public void CreatePlanFingerprintIncludesTransformAndAdapterFacts()
    {
        var (artifact, model) = CreateModel();
        var baseline = MutationPlanner.CreatePlan(model, CreateRecipe(artifact.ContentHash, 1.2f), artifact, new SyntheticTransformPlanner(model));
        var changedTransform = MutationPlanner.CreatePlan(model, CreateRecipe(artifact.ContentHash, 1.3f), artifact, new SyntheticTransformPlanner(model));
        var changedCodec = MutationPlanner.CreatePlan(
            model,
            CreateRecipe(artifact.ContentHash, 1.2f),
            artifact,
            new SyntheticTransformPlanner(model, ContentHash.Compute("different-codec"u8)));
        var changedBoneFacts = MutationPlanner.CreatePlan(
            model,
            CreateRecipe(artifact.ContentHash, 1.2f),
            artifact,
            new SyntheticTransformPlanner(model, boneIdentityHash: ContentHash.Compute("different-bone"u8)));

        Assert.NotEqual(baseline.Fingerprint, changedTransform.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, changedCodec.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, changedBoneFacts.Fingerprint);
    }

    [Fact]
    public void CreatePlanRejectsAdapterVertexCardinalityDisagreement()
    {
        var (artifact, model) = CreateModel();
        var exception = Assert.Throws<S2ModKitException>(() => MutationPlanner.CreatePlan(
            model,
            CreateRecipe(artifact.ContentHash, 1.2f),
            artifact,
            new SyntheticTransformPlanner(model, selectedVertexCount: 4)));

        Assert.Equal("VERTEX_CARDINALITY_MISMATCH", exception.Error.Code);
        Assert.Equal(ErrorCategory.SelectionOrLod, exception.Error.Category);
    }

    [Fact]
    public void CreatePlanRejectsStaleAdapterTargetBlock()
    {
        var (artifact, model) = CreateModel();
        var exception = Assert.Throws<S2ModKitException>(() => MutationPlanner.CreatePlan(
            model,
            CreateRecipe(artifact.ContentHash, 1.2f),
            artifact,
            new SyntheticTransformPlanner(model, staleTargetBlock: true)));

        Assert.Equal("TRANSFORM_TARGET_BLOCK_INVALID", exception.Error.Code);
        Assert.Equal(ErrorCategory.UnsupportedCapability, exception.Error.Category);
    }

    [Fact]
    public void CreatePlanRejectsAdapterDisplacementAboveRecipeCap()
    {
        var (artifact, model) = CreateModel();
        var exception = Assert.Throws<S2ModKitException>(() => MutationPlanner.CreatePlan(
            model,
            CreateRecipe(artifact.ContentHash, 1.2f),
            artifact,
            new SyntheticTransformPlanner(model, maximumDisplacement: 33f)));

        Assert.Equal("TRANSFORM_DISPLACEMENT_EXCEEDED", exception.Error.Code);
        Assert.Equal(ErrorCategory.SelectionOrLod, exception.Error.Category);
    }

    [Fact]
    public void CreatePlanRejectsAdapterExpectedBoundsThatDoNotMatchGeometryKernel()
    {
        var (artifact, model) = CreateModel();
        var exception = Assert.Throws<S2ModKitException>(() => MutationPlanner.CreatePlan(
            model,
            CreateRecipe(artifact.ContentHash, 1.2f),
            artifact,
            new SyntheticTransformPlanner(model, forgeExpectedBounds: true)));

        Assert.Equal("TRANSFORM_GEOMETRY_FACTS_INVALID", exception.Error.Code);
        Assert.Equal(ErrorCategory.UnsupportedCapability, exception.Error.Category);
    }

    [Fact]
    public void CreatePlanRejectsAdapterBoneBoundsThatDoNotMatchGeometryKernel()
    {
        var (artifact, model) = CreateModel();
        var exception = Assert.Throws<S2ModKitException>(() => MutationPlanner.CreatePlan(
            model,
            CreateRecipe(artifact.ContentHash, 1.2f),
            artifact,
            new SyntheticTransformPlanner(model, forgeBoneBounds: true)));

        Assert.Equal("TRANSFORM_BONE_BOUNDS_FACTS_INVALID", exception.Error.Code);
        Assert.Equal(ErrorCategory.UnsupportedCapability, exception.Error.Category);
    }

    [Fact]
    public void CreatePlanPublishesDeterministicAffineContract()
    {
        var (artifact, model) = CreateModel();
        var recipe = CreateAffineRecipe(artifact.ContentHash);
        var planner = new SyntheticAffinePlanner(model);

        var first = MutationPlanner.CreatePlan(model, recipe, artifact, planner);
        var second = MutationPlanner.CreatePlan(model, recipe, artifact, planner);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        var target = Assert.Single(first.Operations).AffineTransformTarget;
        Assert.NotNull(target);
        Assert.Equal("root-mvtx-affine", target.StructuralProfileId);
        Assert.Equal(3, target.GeometryTargets.Count);
        Assert.Empty(first.Operations.Single().GeometryTargets);
    }

    [Fact]
    public void CreatePlanFingerprintIncludesResolvedAffineEvidence()
    {
        var (artifact, model) = CreateModel();
        var recipe = CreateAffineRecipe(artifact.ContentHash);
        var baseline = MutationPlanner.CreatePlan(model, recipe, artifact, new SyntheticAffinePlanner(model));
        var changedProfile = MutationPlanner.CreatePlan(model, recipe, artifact, new SyntheticAffinePlanner(model, profileId: "root-mvtx-affine-v2"));
        var changedPackedEvidence = MutationPlanner.CreatePlan(model, recipe, artifact, new SyntheticAffinePlanner(model, packedSuffix: "different"));

        Assert.NotEqual(baseline.Fingerprint, changedProfile.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, changedPackedEvidence.Fingerprint);
    }

    [Fact]
    public void CreatePlanRejectsAffinePivotAndMatrixDrift()
    {
        var (artifact, model) = CreateModel();
        var recipe = CreateAffineRecipe(artifact.ContentHash);
        var pivotDrift = Assert.Throws<S2ModKitException>(() => MutationPlanner.CreatePlan(
            model, recipe, artifact, new SyntheticAffinePlanner(model, pivotPoint: new TransformVector3 { X = 1 })));
        var matrixDrift = Assert.Throws<S2ModKitException>(() => MutationPlanner.CreatePlan(
            model, recipe, artifact, new SyntheticAffinePlanner(model, forgeLinearMap: true)));
        var hashDrift = Assert.Throws<S2ModKitException>(() => MutationPlanner.CreatePlan(
            model, recipe, artifact, new SyntheticAffinePlanner(model, pivotHash: ContentHash.Compute("different-pivot"u8))));

        Assert.Equal("AFFINE_PIVOT_DRIFT", pivotDrift.Error.Code);
        Assert.Equal("AFFINE_RESULT_DRIFT", matrixDrift.Error.Code);
        Assert.Equal("AFFINE_PIVOT_DRIFT", hashDrift.Error.Code);
    }

    [Fact]
    public void CreatePlanAcceptsUniformAffinePositionOnlyRoute()
    {
        var (artifact, model) = CreateModel();
        var recipe = CreateAffineRecipe(artifact.ContentHash, uniform: true);

        var plan = MutationPlanner.CreatePlan(
            model,
            recipe,
            artifact,
            new SyntheticAffinePlanner(model, uniform: true));

        var affine = Assert.Single(plan.Operations).AffineTransformTarget;
        Assert.NotNull(affine);
        Assert.All(affine.GeometryTargets, target =>
        {
            Assert.Equal(["position"], target.AllowedChangedAttributes);
            Assert.Equal(target.InputPackedFrameHash, target.ExpectedPackedFrameHash);
        });
    }

    private static RecipeDocument CreateRecipe(ContentHash inputHash, float scale) => new()
    {
        SchemaVersion = 2,
        RecipeId = "transform-accessory",
        InputHash = inputHash,
        Operations =
        [
            new TransformComponentOperation
            {
                OperationId = "transform-accessory",
                Selector = new ComponentSelector { Kind = "material_exact", MaterialPath = "materials/accessory.vmat" },
                ExpectedMatchesByLod = new Dictionary<string, int> { ["0"] = 1, ["1"] = 1, ["2"] = 1 },
                ExpectedVerticesByLod = new Dictionary<string, int> { ["0"] = 3, ["1"] = 3, ["2"] = 3 },
                Transform = new ComponentTransform
                {
                    Pivot = new TransformPivot { Kind = "selection_bounds_center", ReferenceLod = 0 },
                    UniformScale = scale,
                },
                Limits = new TransformLimits { MaximumVertexDisplacement = 32f },
            },
        ],
    };

    private static RecipeDocument CreateAffineRecipe(ContentHash inputHash, bool uniform = false) => new()
    {
        SchemaVersion = 5,
        RecipeId = "affine-accessory",
        InputHash = inputHash,
        Operations =
        [
            new TransformComponentOperation
            {
                OperationId = "affine-accessory",
                Version = 4,
                Granularity = "draw_call_vertices",
                Selector = new ComponentSelector { Kind = "material_exact", MaterialPath = "materials/accessory.vmat" },
                ExpectedMatchesByLod = new Dictionary<string, int> { ["0"] = 1, ["1"] = 1, ["2"] = 1 },
                ExpectedVerticesByLod = new Dictionary<string, int> { ["0"] = 3, ["1"] = 3, ["2"] = 3 },
                Transform = new ComponentTransform
                {
                    Pivot = new TransformPivot { Kind = "explicit_point", Point = new TransformVector3() },
                    Scale = uniform
                        ? new TransformVector3 { X = 2, Y = 2, Z = 2 }
                        : new TransformVector3 { X = 2, Y = 1, Z = 1 },
                    Rotation = new TransformRotation { Kind = "identity" },
                    Frame = new TransformFrame { Kind = "model" },
                    Translation = new TransformVector3(),
                },
                Limits = new TransformLimits { MaximumVertexDisplacement = 32 },
            },
        ],
    };

    private static (ArtifactContent Artifact, ModelSnapshot Model) CreateModel()
    {
        var bytes = "synthetic-transform-input"u8.ToArray();
        var artifact = new ArtifactContent("models/hero.vmdl_c", ContentHash.Compute(bytes), bytes);
        var blocks = Enumerable.Range(0, 9).Select(index => new ResourceBlockSnapshot(
            index % 3 == 0 ? "MDAT" : index % 3 == 1 ? "MVTX" : "MIDX",
            index,
            index * 16,
            4,
            ContentHash.Compute([(byte)index]))).ToArray();
        var lods = Enumerable.Range(0, 3).Select(lod => new LodSnapshot(
            lod,
            [
                new MeshSnapshot(
                    artifact.LogicalPath,
                    lod,
                    lod * 3,
                    ContentHash.Compute([(byte)(20 + lod)]),
                    [DrawCallSnapshot.Create(artifact.LogicalPath, lod, lod, 0, "materials/accessory.vmat", 0, 3)]),
            ])).ToArray();
        return (artifact, new ModelSnapshot(
            new ArtifactSnapshot(artifact.LogicalPath, artifact.ContentHash, artifact.Bytes.Length, blocks),
            lods));
    }

    private sealed class SyntheticTransformPlanner(
        ModelSnapshot model,
        ContentHash? codecHash = null,
        int selectedVertexCount = 3,
        float maximumDisplacement = 0.2f,
        bool staleTargetBlock = false,
        bool forgeExpectedBounds = false,
        bool forgeBoneBounds = false,
        ContentHash? boneIdentityHash = null) : ITransformOperationPlanner
    {
        public TransformPlanningResult PlanTransform(TransformPlanningRequest request)
        {
            var codec = new GeometryCodecIdentity(
                "synthetic-codec",
                "vertex-v1",
                "portable",
                codecHash ?? ContentHash.Compute("codec"u8),
                "1");
            var before = new GeometryBounds(
                new TransformVector3 { X = -1f, Y = -1f, Z = -1f },
                new TransformVector3 { X = 1f, Y = 1f, Z = 1f });
            var scale = request.Operation.Transform.UniformScale;
            var after = new GeometryBounds(
                new TransformVector3 { X = -scale, Y = -scale, Z = -scale },
                new TransformVector3
                {
                    X = forgeExpectedBounds ? scale + 1f : scale,
                    Y = scale,
                    Z = scale,
                });
            var targets = request.SelectedDrawCalls.Select(selected =>
            {
                var vertexBlock = model.Artifact.Blocks.Single(block => block.Index == selected.Lod * 3 + 1);
                var indexBlock = model.Artifact.Blocks.Single(block => block.Index == selected.Lod * 3 + 2);
                return new PlannedGeometryTarget(
                    selected.Lod,
                    selected.ResourcePath,
                    selected.MeshOrdinal,
                    selected.ResourceBlockIndex,
                    0,
                    0,
                    vertexBlock.Index,
                    indexBlock.Index,
                    vertexBlock.ContentHash,
                    indexBlock.ContentHash,
                    ContentHash.Compute([(byte)(40 + selected.Lod)]),
                    ContentHash.Compute([(byte)(45 + selected.Lod)]),
                    ContentHash.Compute([(byte)(50 + selected.Lod)]),
                    ContentHash.Compute("vertex-set"u8),
                    selectedVertexCount,
                    new PositionLayout("R32G32B32_FLOAT", 0, 28),
                    before,
                    after,
                    new TransformVector3(),
                    scale,
                    new TransformVector3(),
                    maximumDisplacement,
                    ["position"],
                    codec)
                {
                    BoneBoundsTargets =
                    [
                        new PlannedBoneBoundsTarget(
                            0,
                            "synthetic_bone",
                            boneIdentityHash ?? ContentHash.Compute("inverse-bind"u8),
                            ContentHash.Compute("influenced-vertices"u8),
                            selectedVertexCount,
                            new TransformVector3(),
                            new TransformVector3 { X = 2f, Y = 2f, Z = 2f },
                            before,
                            new TransformVector3(),
                            new TransformVector3 { X = 2f * scale, Y = 2f * scale, Z = 2f * scale },
                            forgeBoneBounds
                                ? new GeometryBounds(after.Min, new TransformVector3 { X = after.Max.X + 1f, Y = after.Max.Y, Z = after.Max.Z })
                                : after,
                            new TransformVector3(),
                            new TransformVector3(),
                            1f,
                            scale),
                    ],
                };
            }).ToArray();
            var blocks = targets.SelectMany(target => new[]
                {
                    model.Artifact.Blocks.Single(block => block.Index == target.ResourceBlockIndex),
                    model.Artifact.Blocks.Single(block => block.Index == target.VertexResourceBlockIndex),
                })
                .DistinctBy(block => block.Index)
                .Select(block => new PlannedTargetBlock(
                    block.Index,
                    block.Type,
                    staleTargetBlock && block.Index == 0 ? ContentHash.Compute("stale"u8) : block.ContentHash))
                .ToArray();
            return new TransformPlanningResult(targets, [], blocks);
        }
    }

    private sealed class SyntheticAffinePlanner(
        ModelSnapshot model,
        string profileId = "root-mvtx-affine",
        ContentHash? pivotHash = null,
        TransformVector3? pivotPoint = null,
        bool forgeLinearMap = false,
        string packedSuffix = "default",
        bool uniform = false) : ITransformOperationPlanner
    {
        public TransformPlanningResult PlanTransform(TransformPlanningRequest request)
        {
            var identity = new TransformMatrix3(1, 0, 0, 0, 1, 0, 0, 0, 1);
            var linear = forgeLinearMap
                ? identity
                : uniform
                    ? new TransformMatrix3(2, 0, 0, 0, 2, 0, 0, 0, 2)
                    : new TransformMatrix3(2, 0, 0, 0, 1, 0, 0, 0, 1);
            var codec = new GeometryCodecIdentity("synthetic-codec", "affine-v1", "portable", ContentHash.Compute("codec"u8), "1");
            var before = new GeometryBounds(new TransformVector3 { X = -1, Y = -1, Z = -1 }, new TransformVector3 { X = 1, Y = 1, Z = 1 });
            var after = uniform
                ? new GeometryBounds(new TransformVector3 { X = -2, Y = -2, Z = -2 }, new TransformVector3 { X = 2, Y = 2, Z = 2 })
                : new GeometryBounds(new TransformVector3 { X = -2, Y = -1, Z = -1 }, new TransformVector3 { X = 2, Y = 1, Z = 1 });
            var geometry = request.SelectedDrawCalls.Select(selected =>
            {
                var vertex = model.Artifact.Blocks.Single(block => block.Index == (selected.Lod * 3) + 1);
                var index = model.Artifact.Blocks.Single(block => block.Index == (selected.Lod * 3) + 2);
                return new PlannedAffineGeometryTarget(
                    selected.Lod, selected.ResourcePath, selected.MeshOrdinal, selected.ResourceBlockIndex,
                    0, 0, vertex.Index, index.Index, vertex.ContentHash, index.ContentHash,
                    ContentHash.Compute(System.Text.Encoding.UTF8.GetBytes($"decoded-{selected.Lod}")),
                    ContentHash.Compute(System.Text.Encoding.UTF8.GetBytes($"expected-{selected.Lod}")),
                    ContentHash.Compute(System.Text.Encoding.UTF8.GetBytes($"indices-{selected.Lod}")),
                    ContentHash.Compute(System.Text.Encoding.UTF8.GetBytes($"vertices-{selected.Lod}")),
                    3,
                    new PositionLayout("R32G32B32_FLOAT", 0, 24),
                    new PackedFrameLayout("R32_UINT", 16, 24, "source2_normal_tangent_v2"),
                    before, after, before, after, 1,
                    ContentHash.Compute(System.Text.Encoding.UTF8.GetBytes($"packed-{selected.Lod}")),
                    uniform
                        ? ContentHash.Compute(System.Text.Encoding.UTF8.GetBytes($"packed-{selected.Lod}"))
                        : ContentHash.Compute(System.Text.Encoding.UTF8.GetBytes($"packed-expected-{selected.Lod}-{packedSuffix}")),
                    uniform ? ["position"] : ["normal_tangent", "position"], codec)
                {
                    BoneBoundsTargets =
                    [
                        new PlannedAffineBoneBoundsTarget(
                            0,
                            "synthetic_bone",
                            ContentHash.Compute("inverse-bind"u8),
                            ContentHash.Compute("influenced"u8),
                            3,
                            before,
                            after,
                            1,
                            2),
                    ],
                };
            }).OrderBy(item => item.Lod).ToArray();
            var lods = request.Operation.ExpectedVerticesByLod.Keys.Select(int.Parse).Order().ToArray();
            var resolver = new AffineEvidenceResolver();
            var resolvedPivot = resolver.ResolvePivot(new TypedPivotResolutionRequest(request.Operation.Transform.Pivot, lods, [], []));
            if (pivotPoint is not null || pivotHash is not null)
            {
                resolvedPivot = resolvedPivot with
                {
                    Point = pivotPoint ?? resolvedPivot.Point,
                    SourceHash = pivotHash ?? resolvedPivot.SourceHash,
                };
            }

            var resolvedFrame = resolver.ResolveFrame(new TypedFrameResolutionRequest(request.Operation.Transform.Frame!, lods, []));
            var affine = new PlannedAffineTransformTarget(
                "draw_call_vertices", profileId, 1,
                resolvedPivot,
                resolvedFrame,
                request.Operation.Transform.Scale!, request.Operation.Transform.Rotation!, request.Operation.Transform.Translation,
                linear, 1, request.Operation.Limits.MaximumVertexDisplacement, geometry);
            var blocks = geometry.SelectMany(item => new[] { item.ResourceBlockIndex, item.VertexResourceBlockIndex })
                .Distinct().Order()
                .Select(index => model.Artifact.Blocks.Single(block => block.Index == index))
                .Select(block => new PlannedTargetBlock(block.Index, block.Type, block.ContentHash))
                .ToArray();
            return new TransformPlanningResult([], [], blocks) { AffineTransformTarget = affine };
        }
    }
}
