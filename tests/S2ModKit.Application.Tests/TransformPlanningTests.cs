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
}
