using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class SyntheticPipelineTests
{
    [Fact]
    public async Task CoordinatedPipelineEmitsStrictEvidenceAndReverifiesTwoBuffersInOneMesh()
    {
        var adapter = new CoordinatedPipelineAdapter(); var workspace = new MemoryWorkspace(adapter.Input);
        var app = CoordinatedApplication(workspace, adapter, adapter); var token = TestContext.Current.CancellationToken;
        var first = await app.PlanAsync("memory", adapter.Recipe, token);
        Assert.Equal(5, first.Plan.SchemaVersion); Assert.Equal(10, first.Evidence.SchemaVersion);
        Assert.Null(first.Evidence.Operations[0].CoordinatedTransform!.ObservedBuffers);
        var built = await app.BuildAsync("memory", adapter.Recipe, token);
        Assert.Single(workspace.Builds);
        Assert.Equal(2, built.Evidence.Operations[0].CoordinatedTransform!.ObservedBuffers!.Count);
        Assert.Equal("untested", built.Evidence.Operations[0].CoordinatedTransform!.ZeroRenderSpheres[0].ConsumerStatus);
        var verified = await app.VerifyAsync("memory", built.Build.BuildId, token);
        Assert.Equal("passed", verified.Evidence.Status); Assert.Equal(2, adapter.AuditCount);
    }

    [Theory]
    [InlineData("missing_verifier")]
    [InlineData("rewrite_throws")]
    [InlineData("audit_throws")]
    [InlineData("audit_failed")]
    [InlineData("missing_observation")]
    [InlineData("missing_box")]
    [InlineData("missing_zero_box")]
    [InlineData("missing_zero_sphere")]
    [InlineData("missing_preservation")]
    [InlineData("forged_planner_intent")]
    public async Task CoordinatedPublicationRejectsIncompleteOrFailedIndependentEvidence(string failure)
    {
        var adapter = new CoordinatedPipelineAdapter { Failure = failure }; var workspace = new MemoryWorkspace(adapter.Input);
        IModelRewriter rewriter = failure == "missing_verifier" ? new CoordinatedWriterWithoutVerifier(adapter) : adapter;
        var app = CoordinatedApplication(workspace, adapter, rewriter);
        var error = await Assert.ThrowsAsync<S2ModKitException>(() => app.BuildAsync("memory", adapter.Recipe, TestContext.Current.CancellationToken));
        if (failure == "missing_verifier") Assert.Equal("COORDINATED_VERIFIER_REQUIRED", error.Error.Code);
        Assert.Empty(workspace.Builds);
        Assert.Equal(ContentHash.Compute(adapter.Input.Bytes.Span), adapter.Input.ContentHash);
    }

    private static S2ModKitApplication CoordinatedApplication(MemoryWorkspace workspace, CoordinatedPipelineAdapter adapter, IModelRewriter rewriter) =>
        new(workspace, adapter, adapter, rewriter, new SkippedExternalVerifier(), new TestReportRenderer(), new FixedClock(), new UnusedResourceSourceFactory(), transformPlanner: adapter);

    // This synthetic adapter qualifies Application publication/evidence orchestration.
    // Source 2 word/layout correctness and actual resources are checked separately.
    private sealed class CoordinatedPipelineAdapter : IModelInspector, IResourceDependencyReader, IModelRewriter, ITransformOperationPlanner, ICoordinatedTransformVerifier
    {
        private readonly byte[] outputBytes = "synthetic-coordinated-output"u8.ToArray();
        private readonly PlannedCoordinatedTransformTarget target;
        public CoordinatedPipelineAdapter()
        {
            var template = ExperimentalVisualContractTests.CoordinatedPlan().Operations[0].CoordinatedTransformTarget!;
            var draws = new[] { DrawCallSnapshot.Create(Input.LogicalPath, 0, 0, 0, "materials/first.vmat", 0, 3),
                DrawCallSnapshot.Create(Input.LogicalPath, 0, 0, 1, "materials/second.vmat", 3, 3) };
            var intent = (TransformComponentOperation)ExperimentalVisualContractTests.CoordinatedRecipe().Operations[0];
            var selector = new ComponentSelector { Kind = "draw_call_ids", DrawCallIds = draws.Select(d => d.Id).Order(StringComparer.Ordinal).ToArray() };
            var common = intent.CoordinatedTransform! with { Members = [new("first", [new(0, [draws[0].Id], 3)]), new("second", [new(0, [draws[1].Id], 3)])] };
            target = template with { InputHash = Input.ContentHash, Selector = selector, CoordinatedTransform = common };
            target = target with { TargetFingerprint = MutationPlanJson.ComputeCoordinatedTargetFingerprint(target) };
            Recipe = ExperimentalVisualContractTests.CoordinatedRecipe() with { InputHash = Input.ContentHash, Operations = [intent with { Selector = selector, CoordinatedTransform = common }] };
            var geometry = new MeshGeometrySnapshot("ready", "synthetic",
                target.Buffers.Select(b => new VertexBufferSnapshot(b.VertexBufferOrdinal, b.VertexResourceBlockIndex, b.VertexCount, b.PositionLayout.Stride, b.VertexBlockInputHash, b.InputDecodedVertexBufferHash, b.PositionLayout)).ToArray(),
                target.Buffers.Select(b => new IndexBufferSnapshot(b.IndexBufferOrdinal, b.IndexResourceBlockIndex, 3, 2, b.IndexBlockInputHash, b.DecodedIndexBufferHash)).ToArray(), [], target.Buffers[0].Codec);
            Before = new(new(Input.LogicalPath, Input.ContentHash, Input.Bytes.Length, target.SourceBlocks.Select(b => new ResourceBlockSnapshot(b.Type, b.Index, b.Index * 12, 12, b.InputHash)).ToArray()),
                [new(0, [new(Input.LogicalPath, 0, 0, target.Buffers[0].VertexSetHash, draws) { Geometry = geometry }])]);
            After = Before with
            {
                Artifact = Before.Artifact with
                {
                    ContentHash = ContentHash.Compute(outputBytes),
                    Size = outputBytes.Length,
                    Blocks = Before.Artifact.Blocks.Select(b => target.Buffers.Any(v => v.VertexResourceBlockIndex == b.Index) ? b with { ContentHash = target.Buffers.Single(v => v.VertexResourceBlockIndex == b.Index).ExpectedDecodedVertexBufferHash } : b).ToArray()
                },
                Lods = [new(0, [Before.Lods[0].Meshes[0] with { Geometry = geometry with
                { VertexBuffers = geometry.VertexBuffers.Select((b, index) => b with { EncodedHash = target.Buffers[index].ExpectedDecodedVertexBufferHash, DecodedHash = target.Buffers[index].ExpectedDecodedVertexBufferHash }).ToArray() } }])],
            };
        }
        public ArtifactContent Input { get; } = new("models/test.vmdl_c", ContentHash.Compute("synthetic-coordinated-input"u8), "synthetic-coordinated-input"u8.ToArray());
        public RecipeDocument Recipe { get; }
        private ModelSnapshot Before { get; }
        private ModelSnapshot After { get; }
        public string? Failure { get; init; }
        public int AuditCount { get; private set; }
        public string AdapterName => "synthetic_coordinated";
        public string AdapterVersion => "1";
        public IReadOnlyDictionary<string, string> ComponentVersions { get; } = new Dictionary<string, string>();
        public bool CanInspect(ArtifactContent artifact) => true;
        public bool CanReadDependencies(ArtifactContent artifact) => true;
        public Task<IReadOnlyList<ResourceDependency>> ReadDependenciesAsync(ArtifactContent artifact, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResourceDependency>>([]);
        public Task<ModelSnapshot> InspectAsync(ArtifactContent artifact, CancellationToken cancellationToken = default) => Task.FromResult(artifact.ContentHash == Input.ContentHash ? Before : After);
        public TransformPlanningResult PlanTransform(TransformPlanningRequest request)
        {
            var frozen = target;
            if (Failure == "forged_planner_intent")
            {
                frozen = frozen with { DisplacementLimit = 32 }; frozen = frozen with { TargetFingerprint = MutationPlanJson.ComputeCoordinatedTargetFingerprint(frozen) };
            }
            return new([], [], target.SourceBlocks.Where(b => b.Index is 0 or 1 or 4).ToArray()) { CoordinatedTransformTarget = frozen };
        }
        public bool CanRewrite(ModelSnapshot model, MutationPlan plan) => true;
        public Task<RewriteCandidate> RewriteAsync(ArtifactContent input, ModelSnapshot model, MutationPlan plan, CancellationToken cancellationToken = default)
        {
            if (Failure == "rewrite_throws") throw Errors.Verification("INJECTED_REWRITE_FAILURE", "Injected failure.", "Reject.");
            return Task.FromResult(new RewriteCandidate(input.LogicalPath, outputBytes, After));
        }
        public Task<CoordinatedTransformVerification> VerifyCoordinatedTransformAsync(ArtifactContent input, ArtifactContent output, MutationPlan plan, CancellationToken cancellationToken = default)
        {
            AuditCount++;
            if (Failure == "audit_throws") throw Errors.Verification("COORDINATED_RESULT_DRIFT", "Injected independent audit failure.", "Reject.");
            var boundaries = CoordinatedContractValidator.PlannedBoundaries().Where(b => b.Name != "runtime")
                .Select(b => b.Status == "not_applicable" ? b with { Status = Failure == "audit_failed" ? "failed" : "passed" } : b).ToArray();
            return Task.FromResult(new CoordinatedTransformVerification(boundaries,
                Failure == "missing_observation" ? [] : target.Buffers.Select(b => new CoordinatedBufferObservation(b.MemberId, b.Lod, b.MeshOrdinal, b.VertexBufferOrdinal, b.ExpectedPositionHash,
                    b.ExpectedPackedFrameHash, b.ExpectedDecodedVertexBufferHash, b.MaskHash, b.WeightHash, b.MaximumDisplacement)).ToArray(),
                Failure == "missing_box" ? [] : target.BoxTargets.Select(b => new ExperimentalBoxEvidence(b, b.ExpectedWords, "passed")).ToArray(),
                Failure == "missing_zero_box" ? [] : target.ZeroBoxTargets.Select(b => new ZeroBoneBoxPreservationEvidence(b, b.OriginalWords, "passed", "untested", "untested")).ToArray(),
                Failure == "missing_zero_sphere" ? [] : target.ZeroRenderSphereTargets.Select(b => new ZeroRenderSpherePreservationEvidence(b, b.Storage, b.OriginalWord, "passed", "untested", "untested")).ToArray(),
                Failure == "missing_preservation" ? [] : target.PreservationTargets.Select(p => new ExperimentalPreservationEvidence(p, p.SourcePayloadHash, p.OriginalWords, "passed")).ToArray()));
        }
    }

    private sealed class CoordinatedWriterWithoutVerifier(CoordinatedPipelineAdapter inner) : IModelRewriter
    {
        public bool CanRewrite(ModelSnapshot model, MutationPlan plan) => inner.CanRewrite(model, plan);
        public Task<RewriteCandidate> RewriteAsync(ArtifactContent input, ModelSnapshot model, MutationPlan plan, CancellationToken cancellationToken = default) => inner.RewriteAsync(input, model, plan, cancellationToken);
    }
}
