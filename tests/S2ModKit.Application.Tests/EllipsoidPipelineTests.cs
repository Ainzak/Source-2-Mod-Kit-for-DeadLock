using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class SyntheticPipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EllipsoidPreviewNeverPersistsPlanEvidenceOrBuildEvenWhenGeometryDrifts(bool drift)
    {
        var adapter = new EllipsoidPipelineAdapter { Failure = drift ? "preview_geometry_drift" : null };
        var workspace = new MemoryWorkspace(adapter.Input);
        var application = EllipsoidApplication(workspace, adapter, adapter);
        if (drift)
            await Assert.ThrowsAsync<S2ModKitException>(() => application.PreviewEllipsoidSelectionAsync("memory", adapter.Recipe, TestContext.Current.CancellationToken));
        else
        {
            var preview = await application.PreviewEllipsoidSelectionAsync("memory", adapter.Recipe, TestContext.Current.CancellationToken);
            Assert.Equal(adapter.Input.ContentHash, preview.InputHash);
            Assert.Equal(["core", "transition", "pinned"], preview.Lods[0].Points.Select(p => p.Membership));
        }
        Assert.Equal(0, workspace.PlanCount);
        Assert.Equal(0, workspace.EvidenceWriteCount);
        Assert.Empty(workspace.Builds);
        Assert.Equal(0, adapter.AuditCount);
    }

    [Fact]
    public async Task EllipsoidPipelineEmitsPlannedAndObservedEvidenceAndReverifies()
    {
        var adapter = new EllipsoidPipelineAdapter();
        var workspace = new MemoryWorkspace(adapter.Input);
        var application = EllipsoidApplication(workspace, adapter, adapter);
        var token = TestContext.Current.CancellationToken;
        var first = await application.PlanAsync("memory", adapter.Recipe, token);
        var second = await application.PlanAsync("memory", adapter.Recipe, token);
        Assert.Equal(first.Plan.Fingerprint, second.Plan.Fingerprint);
        Assert.Equal(4, first.Plan.SchemaVersion);
        Assert.Equal(9, first.Evidence.SchemaVersion);
        Assert.Null(first.Evidence.Operations[0].EllipsoidTransform!.ObservedBuffers);
        var built = await application.BuildAsync("memory", adapter.Recipe, token);
        Assert.Single(workspace.Builds);
        Assert.Single(built.Evidence.Operations[0].EllipsoidTransform!.ObservedBuffers!);
        Assert.Equal("offline_static", built.Evidence.ProofLevel);
        Assert.Contains(built.Evidence.Boundaries, b => b.Name == "runtime" && b.Status == "untested");
        var verified = await application.VerifyAsync("memory", built.Build.BuildId, token);
        Assert.Equal("passed", verified.Evidence.Status);
        Assert.Equal(2, adapter.AuditCount);
    }

    [Theory]
    [InlineData("missing_verifier")]
    [InlineData("audit_throws")]
    [InlineData("audit_failed")]
    [InlineData("missing_observation")]
    [InlineData("missing_box")]
    [InlineData("missing_preservation")]
    [InlineData("rewrite_throws")]
    [InlineData("forged_planner_intent")]
    public async Task EllipsoidPublicationRequiresCompleteIndependentEvidence(string failure)
    {
        var adapter = new EllipsoidPipelineAdapter { Failure = failure };
        var workspace = new MemoryWorkspace(adapter.Input);
        IModelRewriter rewriter = failure == "missing_verifier" ? new EllipsoidWriterWithoutVerifier(adapter) : adapter;
        var application = EllipsoidApplication(workspace, adapter, rewriter);
        var error = await Assert.ThrowsAsync<S2ModKitException>(() => application.BuildAsync("memory", adapter.Recipe, TestContext.Current.CancellationToken));
        if (failure == "missing_verifier") Assert.Equal("ELLIPSOID_VERIFIER_REQUIRED", error.Error.Code);
        Assert.Empty(workspace.Builds);
        Assert.Equal(ContentHash.Compute(adapter.Input.Bytes.Span), adapter.Input.ContentHash);
    }

    private static S2ModKitApplication EllipsoidApplication(MemoryWorkspace workspace, EllipsoidPipelineAdapter adapter, IModelRewriter rewriter) =>
        new(workspace, adapter, adapter, rewriter, new SkippedExternalVerifier(), new TestReportRenderer(), new FixedClock(), new UnusedResourceSourceFactory(), transformPlanner: adapter);

    // Synthetic Application fixture. This proves publication/evidence orchestration, not
    // Source 2 encoding or geometry correctness (tested separately by resource fixtures).
    private sealed class EllipsoidPipelineAdapter : IModelInspector, IResourceDependencyReader, IModelRewriter, ITransformOperationPlanner, IEllipsoidTransformVerifier, IEllipsoidPreviewGeometryReader
    {
        private readonly byte[] outputBytes = "synthetic-output"u8.ToArray();
        private readonly PlannedEllipsoidTransformTarget target;
        public EllipsoidPipelineAdapter()
        {
            var original = EllipsoidSelectionPreviewTests.Fixture().Plan.Operations[0].EllipsoidTransformTarget!;
            var buffer = original.Buffers[0];
            var draw = DrawCallSnapshot.Create(Input.LogicalPath, 0, 0, 0, original.Selector.MaterialPath!, 0, 3);
            target = original with { InputHash = Input.ContentHash };
            target = target with { TargetFingerprint = MutationPlanJson.ComputeEllipsoidTargetFingerprint(target) };
            Recipe = ExperimentalVisualContractTests.EllipsoidRecipe() with { InputHash = Input.ContentHash };
            var geometry = new MeshGeometrySnapshot("ready", "synthetic", [new(0, 1, 3, 28, buffer.VertexBlockInputHash, buffer.InputDecodedVertexBufferHash, buffer.PositionLayout)],
                [new(0, 2, 3, 2, buffer.IndexBlockInputHash, buffer.DecodedIndexBufferHash)], [], buffer.Codec);
            Before = new(new(Input.LogicalPath, Input.ContentHash, Input.Bytes.Length,
                target.SourceBlocks.Select(b => new ResourceBlockSnapshot(b.Type, b.Index, b.Index * 12, 12, b.InputHash)).ToArray()),
                [new(0, [new(Input.LogicalPath, 0, 0, buffer.VertexSetHash, [draw]) { Geometry = geometry }])]);
            var outputHash = ContentHash.Compute(outputBytes);
            var mesh = Before.Lods[0].Meshes[0];
            After = Before with
            {
                Artifact = Before.Artifact with
                {
                    ContentHash = outputHash,
                    Size = outputBytes.Length,
                    Blocks = Before.Artifact.Blocks.Select(b => b.Index == 1 ? b with { ContentHash = buffer.ExpectedDecodedVertexBufferHash } : b).ToArray()
                },
                Lods = [new(0, [mesh with { Geometry = geometry with { VertexBuffers = [geometry.VertexBuffers[0] with { EncodedHash = buffer.ExpectedDecodedVertexBufferHash, DecodedHash = buffer.ExpectedDecodedVertexBufferHash }] } }])],
            };
        }
        public ArtifactContent Input { get; } = new("models/test.vmdl_c", ContentHash.Compute("synthetic-input"u8), "synthetic-input"u8.ToArray());
        public RecipeDocument Recipe { get; }
        private ModelSnapshot Before { get; }
        private ModelSnapshot After { get; }
        public string? Failure { get; init; }
        public int AuditCount { get; private set; }
        public string AdapterName => "synthetic_ellipsoid";
        public string AdapterVersion => "1";
        public IReadOnlyDictionary<string, string> ComponentVersions { get; } = new Dictionary<string, string>();
        public bool CanInspect(ArtifactContent artifact) => true;
        public bool CanReadDependencies(ArtifactContent artifact) => true;
        public Task<IReadOnlyList<ResourceDependency>> ReadDependenciesAsync(ArtifactContent artifact, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResourceDependency>>([]);
        public Task<ModelSnapshot> InspectAsync(ArtifactContent artifact, CancellationToken cancellationToken = default) => Task.FromResult(artifact.ContentHash == Input.ContentHash ? Before : After);
        public Task<EllipsoidPreviewGeometry> ReadEllipsoidPreviewGeometryAsync(ArtifactContent input, MutationPlan plan, CancellationToken cancellationToken = default)
        {
            var geometry = EllipsoidSelectionPreviewTests.Fixture().Geometry;
            var lod = geometry.Lods[0] with { DrawCallIds = plan.Operations[0].SelectedDrawCalls.Select(c => c.DrawCallId).Order(StringComparer.Ordinal).ToArray() };
            if (Failure == "preview_geometry_drift") lod = lod with { Points = lod.Points.Skip(1).ToArray() };
            return Task.FromResult(geometry with { InputHash = input.ContentHash, PlanFingerprint = plan.Fingerprint, Lods = [lod] });
        }
        public TransformPlanningResult PlanTransform(TransformPlanningRequest request)
        {
            var frozen = target;
            if (Failure == "forged_planner_intent")
            {
                frozen = frozen with { DisplacementLimit = 32 };
                frozen = frozen with { TargetFingerprint = MutationPlanJson.ComputeEllipsoidTargetFingerprint(frozen) };
            }
            return new([], [], target.SourceBlocks.Where(b => b.Index is 0 or 1).ToArray()) { EllipsoidTransformTarget = frozen };
        }
        public bool CanRewrite(ModelSnapshot model, MutationPlan plan) => true;
        public Task<RewriteCandidate> RewriteAsync(ArtifactContent input, ModelSnapshot model, MutationPlan plan, CancellationToken cancellationToken = default)
        {
            if (Failure == "rewrite_throws") throw Errors.Verification("INJECTED_REWRITE_FAILURE", "Injected serialization failure.", "Reject.");
            return Task.FromResult(new RewriteCandidate(input.LogicalPath, outputBytes, After));
        }
        public Task<EllipsoidTransformVerification> VerifyEllipsoidTransformAsync(ArtifactContent input, ArtifactContent output, MutationPlan plan, CancellationToken cancellationToken = default)
        {
            AuditCount++;
            if (Failure == "audit_throws") throw Errors.Verification("ELLIPSOID_RESULT_DRIFT", "Injected independent audit failure.", "Reject.");
            var boundaries = EllipsoidContractValidator.PlannedBoundaries().Where(b => b.Name != "runtime")
                .Select(b => b.Status == "not_applicable" ? b with { Status = Failure == "audit_failed" ? "failed" : "passed" } : b).ToArray();
            var buffers = target.Buffers.Select(b => new EllipsoidBufferObservation(b.Lod, b.MeshOrdinal, b.VertexBufferOrdinal,
                b.ExpectedPositionHash, b.ExpectedPackedFrameHash, b.ExpectedDecodedVertexBufferHash, b.MaskHash, b.WeightHash, b.MaximumDisplacement)).ToArray();
            return Task.FromResult(new EllipsoidTransformVerification(boundaries, Failure == "missing_observation" ? [] : buffers,
                Failure == "missing_box" ? [] : target.BoxTargets.Select(b => new ExperimentalBoxEvidence(b, b.ExpectedWords, "passed")).ToArray(),
                Failure == "missing_preservation" ? [] : target.PreservationTargets.Select(p => new ExperimentalPreservationEvidence(p, p.SourcePayloadHash, p.OriginalWords, "passed")).ToArray()));
        }
    }

    private sealed class EllipsoidWriterWithoutVerifier(EllipsoidPipelineAdapter inner) : IModelRewriter
    {
        public bool CanRewrite(ModelSnapshot model, MutationPlan plan) => inner.CanRewrite(model, plan);
        public Task<RewriteCandidate> RewriteAsync(ArtifactContent input, ModelSnapshot model, MutationPlan plan, CancellationToken cancellationToken = default) => inner.RewriteAsync(input, model, plan, cancellationToken);
    }
}
