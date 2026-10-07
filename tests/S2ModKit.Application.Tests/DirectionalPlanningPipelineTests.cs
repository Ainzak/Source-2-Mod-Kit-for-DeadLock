using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class SyntheticPipelineTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    public async Task DirectionalSourcePlansEmitBoundSchemaElevenEvidenceWithoutPublishingCandidates(int members, int lods)
    {
        var adapter = new DirectionalPlanningAdapter(members, lods); var workspace = new MemoryWorkspace(adapter.Input);
        var app = DirectionalApplication(workspace, adapter); var token = TestContext.Current.CancellationToken;
        var result = await app.PlanAsync("memory", adapter.Recipe, token);
        Assert.Equal(6, result.Plan.SchemaVersion); Assert.Equal(11, result.Evidence.SchemaVersion);
        Assert.Null(result.Evidence.Output); Assert.Null(result.Evidence.Operations[0].DirectionalTransform!.Observed);
        DirectionalContractValidator.ValidateEvidence(result.Evidence);
        Assert.Equal(result.Plan.Fingerprint, JsonDefaults.Deserialize<EvidenceReport>(JsonDefaults.SerializeToUtf8(result.Evidence), "Directional planned evidence").PlanFingerprint);
        Assert.Equal(members * lods, result.Plan.Operations[0].DirectionalTransformTarget!.Buffers.Count);
        Assert.Empty(workspace.Builds); Assert.Equal(0, adapter.Rewrites);
        Assert.Equal("DIRECTIONAL_VERIFICATION_UNAVAILABLE", (await Assert.ThrowsAsync<S2ModKitException>(() => app.BuildAsync("memory", adapter.Recipe, token))).Error.Code);
        Assert.Empty(workspace.Builds); Assert.Equal(0, adapter.Rewrites);
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("source")]
    [InlineData("mixed")]
    [InlineData("closure")]
    public async Task DirectionalPlannerRejectsForgedOrIncompleteAdapterFactsBeforeSavingAPlan(string failure)
    {
        var adapter = new DirectionalPlanningAdapter(2, 2) { Failure = failure }; var workspace = new MemoryWorkspace(adapter.Input);
        var e = await Assert.ThrowsAsync<S2ModKitException>(() => DirectionalApplication(workspace, adapter).PlanAsync("memory", adapter.Recipe, TestContext.Current.CancellationToken));
        Assert.Equal(failure == "closure" ? "DIRECTIONAL_INTENT_INVALID" : failure == "mixed" ? "COORDINATED_RESULT_DRIFT" : "DIRECTIONAL_RESULT_DRIFT", e.Error.Code);
        Assert.Empty(workspace.Builds); Assert.Equal(0, adapter.Rewrites);
    }

    private static S2ModKitApplication DirectionalApplication(MemoryWorkspace workspace, DirectionalPlanningAdapter adapter) =>
        new(workspace, adapter, adapter, adapter, new SkippedExternalVerifier(), new TestReportRenderer(), new FixedClock(), new UnusedResourceSourceFactory(), transformPlanner: adapter);

    private class DirectionalPlanningAdapter : IModelInspector, IResourceDependencyReader, IModelRewriter, ITransformOperationPlanner
    {
        protected readonly PlannedDirectionalTransformTarget target;
        private readonly IReadOnlyList<PlannedTargetBlock> changed;
        protected readonly ModelSnapshot snapshot;
        public DirectionalPlanningAdapter(int members, int lods)
        {
            var template = ExperimentalVisualContractTests.DirectionalPlan(members, lods);
            var original = template.Operations[0].DirectionalTransformTarget!;
            var calls = template.Operations[0].SelectedDrawCalls.Select(c => c with
            { DrawCallId = DrawCallSnapshot.Create(c.ResourcePath, c.Lod, c.MeshOrdinal, c.DrawCallOrdinal, c.MaterialPath, c.IndexStart, c.IndexCount).Id }).ToArray();
            var maps = original.DirectionalTransform.Members.Select(m => m with
            {
                Lods = m.Lods.Select(l => l with
                { DrawCallIds = l.DrawCallIds.Select(id => calls[template.Operations[0].SelectedDrawCalls.ToList().FindIndex(c => c.DrawCallId == id)].DrawCallId).Order(StringComparer.Ordinal).ToArray() }).ToArray()
            }).ToArray();
            target = original with
            {
                InputHash = Input.ContentHash,
                Selector = new() { Kind = "draw_call_ids", DrawCallIds = calls.Select(c => c.DrawCallId).Order(StringComparer.Ordinal).ToArray() },
                DirectionalTransform = original.DirectionalTransform with { Members = maps }
            };
            target = target with { TargetFingerprint = MutationPlanJson.ComputeDirectionalTargetFingerprint(target) };
            changed = template.Operations[0].TargetBlocks;
            var operation = new PlannedOperation("directional", "transform_component", 9, calls, changed) { DirectionalTransformTarget = target };
            Recipe = new() { SchemaVersion = 10, RecipeId = "synthetic-directional", InputHash = Input.ContentHash, Operations = [DirectionalContractValidator.Operation(operation)] };
            snapshot = new(new(Input.LogicalPath, Input.ContentHash, Input.Bytes.Length, target.SourceBlocks.Select(b => new ResourceBlockSnapshot(b.Type, b.Index, b.Index * 12, 12, b.InputHash)).ToArray()),
                calls.GroupBy(c => c.Lod).Select(g => new LodSnapshot(g.Key, g.GroupBy(c => c.MeshOrdinal).Select(mesh => new MeshSnapshot(Input.LogicalPath, mesh.Key,
                    mesh.First().ResourceBlockIndex, Input.ContentHash, mesh.Select(c => DrawCallSnapshot.Create(c.ResourcePath, c.Lod, c.MeshOrdinal, c.DrawCallOrdinal, c.MaterialPath, c.IndexStart, c.IndexCount)).ToArray())
                { Geometry = SyntheticDirectionalGeometry(g.Key, mesh.Key, target) }).ToArray())).ToArray());
        }
        public ArtifactContent Input { get; } = new("models/test.vmdl_c", ContentHash.Compute("synthetic-directional-input"u8), "synthetic-directional-input"u8.ToArray());
        public RecipeDocument Recipe { get; }
        public string? Failure { get; init; }
        public int Rewrites { get; private set; }
        public string AdapterName => "synthetic_directional";
        public string AdapterVersion => "1";
        public IReadOnlyDictionary<string, string> ComponentVersions { get; } = new Dictionary<string, string>();
        public bool CanInspect(ArtifactContent artifact) => true;
        public bool CanReadDependencies(ArtifactContent artifact) => true;
        public virtual Task<ModelSnapshot> InspectAsync(ArtifactContent artifact, CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
        public Task<IReadOnlyList<ResourceDependency>> ReadDependenciesAsync(ArtifactContent artifact, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResourceDependency>>([]);
        public virtual TransformPlanningResult PlanTransform(TransformPlanningRequest request)
        {
            var facts = Failure switch
            {
                "intent" => target with { DisplacementLimit = 32 },
                "source" => target with { InputHash = ContentHash.Compute("forged-source"u8) },
                _ => target,
            };
            facts = facts with { TargetFingerprint = MutationPlanJson.ComputeDirectionalTargetFingerprint(facts) };
            return new([], [], Failure == "closure" ? changed.Skip(1).ToArray() : changed)
            { DirectionalTransformTarget = facts, CoordinatedTransformTarget = Failure == "mixed" ? ExperimentalVisualContractTests.CoordinatedPlan().Operations[0].CoordinatedTransformTarget : null };
        }
        public bool CanRewrite(ModelSnapshot model, MutationPlan plan) => true;
        public virtual Task<RewriteCandidate> RewriteAsync(ArtifactContent input, ModelSnapshot model, MutationPlan plan, CancellationToken cancellationToken = default)
        {
            Rewrites++; throw new InvalidOperationException("Publication gate must run before the candidate writer.");
        }
    }
}
