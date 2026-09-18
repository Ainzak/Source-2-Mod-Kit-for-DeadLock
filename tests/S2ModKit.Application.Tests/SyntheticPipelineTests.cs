using System.Text.Json;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed class SyntheticPipelineTests
{
    [Fact]
    public async Task BuildAndVerifyRemovesOneDrawCallFromEveryLodAndPreservesInput()
    {
        var input = SyntheticAdapter.CreateThreeLodArtifact();
        var originalBytes = input.Bytes.ToArray();
        var workspace = new MemoryWorkspace(input);
        var adapter = new SyntheticAdapter();
        var application = new S2ModKitApplication(workspace, adapter, adapter, adapter, new SkippedExternalVerifier(), new TestReportRenderer(), new FixedClock(), new UnusedResourceSourceFactory());
        var recipe = CreateRecipe(input.ContentHash, new Dictionary<string, int> { ["0"] = 1, ["1"] = 1, ["2"] = 1 });

        var cancellationToken = TestContext.Current.CancellationToken;
        var firstPlan = await application.PlanAsync("memory", recipe, cancellationToken);
        var secondPlan = await application.PlanAsync("memory", recipe, cancellationToken);
        var result = await application.BuildAsync("memory", recipe, cancellationToken);
        var verified = await application.VerifyAsync("memory", result.Build.BuildId, cancellationToken);

        Assert.Equal(firstPlan.Plan.Fingerprint, secondPlan.Plan.Fingerprint);
        Assert.Equal(3, result.Evidence.Operations.Single().SelectedDrawCallIds.Count);
        Assert.Equal("passed", result.Evidence.Status);
        Assert.Equal("offline_static", result.Evidence.ProofLevel);
        Assert.NotEqual(input.ContentHash, result.Build.ContentHash);
        Assert.Equal(originalBytes, input.Bytes.ToArray());
        Assert.Contains(firstPlan.Evidence.Blocks, block => block.Type == "DATA" && block.Disposition == "planned_change" && block.OutputHash is null);
        Assert.Contains(firstPlan.Evidence.Blocks, block => block.Type == "VBIB" && block.Disposition == "planned_unchanged" && block.OutputHash is null);
        Assert.Contains(result.Evidence.Blocks, block => block.Type == "DATA" && block.Disposition == "changed" && block.InputHash != block.OutputHash);
        Assert.Contains(result.Evidence.Blocks, block => block.Type == "VBIB" && block.Disposition == "unchanged" && block.InputHash == block.OutputHash);
        Assert.Contains("operating_system", result.Evidence.ToolVersions.Keys);
        Assert.Contains("dotnet.runtime", result.Evidence.ToolVersions.Keys);
        Assert.Equal("1", result.Evidence.ToolVersions["s2modkit.adapter.synthetic"]);
        Assert.Contains(result.Evidence.Warnings, warning => warning.Contains("unused geometry bytes", StringComparison.Ordinal));
        Assert.Equal("passed", verified.Evidence.Status);
        Assert.Contains(verified.Evidence.Boundaries, boundary => boundary.Name == "non_target_blocks_byte_identical" && boundary.Status == "passed");
        Assert.Contains(verified.Evidence.Boundaries, boundary => boundary.Name == "runtime" && boundary.Status == "untested");
    }

    [Fact]
    public async Task PlanRejectsIncompleteLodExpectations()
    {
        var input = SyntheticAdapter.CreateThreeLodArtifact();
        var workspace = new MemoryWorkspace(input);
        var adapter = new SyntheticAdapter();
        var application = new S2ModKitApplication(workspace, adapter, adapter, adapter, new SkippedExternalVerifier(), new TestReportRenderer(), new FixedClock(), new UnusedResourceSourceFactory());
        var recipe = CreateRecipe(input.ContentHash, new Dictionary<string, int> { ["0"] = 1, ["1"] = 1 });

        var cancellationToken = TestContext.Current.CancellationToken;
        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => application.PlanAsync("memory", recipe, cancellationToken));

        Assert.Equal("LOD_COVERAGE_INCOMPLETE", exception.Error.Code);
        Assert.Empty(workspace.Builds);
    }

    [Fact]
    public async Task PlanRejectsCardinalityMismatchBeforeRewrite()
    {
        var input = SyntheticAdapter.CreateThreeLodArtifact();
        var workspace = new MemoryWorkspace(input);
        var adapter = new SyntheticAdapter();
        var application = new S2ModKitApplication(workspace, adapter, adapter, adapter, new SkippedExternalVerifier(), new TestReportRenderer(), new FixedClock(), new UnusedResourceSourceFactory());
        var recipe = CreateRecipe(input.ContentHash, new Dictionary<string, int> { ["0"] = 2, ["1"] = 1, ["2"] = 1 });

        var cancellationToken = TestContext.Current.CancellationToken;
        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => application.BuildAsync("memory", recipe, cancellationToken));

        Assert.Equal("SELECTOR_CARDINALITY_MISMATCH", exception.Error.Code);
        Assert.Empty(workspace.Builds);
    }

    [Fact]
    public async Task PlanRejectsInputHashDrift()
    {
        var input = SyntheticAdapter.CreateThreeLodArtifact();
        var workspace = new MemoryWorkspace(input);
        var adapter = new SyntheticAdapter();
        var application = new S2ModKitApplication(workspace, adapter, adapter, adapter, new SkippedExternalVerifier(), new TestReportRenderer(), new FixedClock(), new UnusedResourceSourceFactory());
        var recipe = CreateRecipe(ContentHash.Compute([99]), new Dictionary<string, int> { ["0"] = 1, ["1"] = 1, ["2"] = 1 });

        var cancellationToken = TestContext.Current.CancellationToken;
        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => application.PlanAsync("memory", recipe, cancellationToken));

        Assert.Equal("INPUT_HASH_DRIFT", exception.Error.Code);
    }

    [Fact]
    public async Task PlanRejectsTransformBeforeGeometryPlanningIsAvailable()
    {
        var input = SyntheticAdapter.CreateThreeLodArtifact();
        var workspace = new MemoryWorkspace(input);
        var adapter = new SyntheticAdapter();
        var application = new S2ModKitApplication(workspace, adapter, adapter, adapter, new SkippedExternalVerifier(), new TestReportRenderer(), new FixedClock(), new UnusedResourceSourceFactory());
        var recipe = new RecipeDocument
        {
            SchemaVersion = 2,
            RecipeId = "scale-accessory",
            InputHash = input.ContentHash,
            Operations =
            [
                new TransformComponentOperation
                {
                    OperationId = "scale-accessory",
                    Selector = new ComponentSelector { Kind = "material_exact", MaterialPath = "materials/accessory.vmat" },
                    ExpectedMatchesByLod = new Dictionary<string, int> { ["0"] = 1, ["1"] = 1, ["2"] = 1 },
                    ExpectedVerticesByLod = new Dictionary<string, int> { ["0"] = 3, ["1"] = 3, ["2"] = 3 },
                    Transform = new ComponentTransform
                    {
                        Pivot = new TransformPivot { Kind = "selection_bounds_center", ReferenceLod = 0 },
                        UniformScale = 1.2f,
                    },
                    Limits = new TransformLimits { MaximumVertexDisplacement = 32f },
                },
            ],
        };

        var exception = await Assert.ThrowsAsync<S2ModKitException>(
            () => application.PlanAsync("memory", recipe, TestContext.Current.CancellationToken));

        Assert.Equal("TRANSFORM_PLANNING_UNAVAILABLE", exception.Error.Code);
        Assert.Equal(ErrorCategory.UnsupportedCapability, exception.Error.Category);
        Assert.Empty(workspace.Builds);
    }

    [Fact]
    public async Task VerificationRejectsMeshChangesOutsideDrawCalls()
    {
        var input = SyntheticAdapter.CreateThreeLodArtifact();
        var adapter = new SyntheticAdapter();
        var before = await adapter.InspectAsync(input, TestContext.Current.CancellationToken);
        var recipe = CreateRecipe(input.ContentHash, new Dictionary<string, int> { ["0"] = 1, ["1"] = 1, ["2"] = 1 });
        var plan = MutationPlanner.CreatePlan(before, recipe);
        var candidate = await adapter.RewriteAsync(input, before, plan, TestContext.Current.CancellationToken);
        var changedMesh = candidate.Snapshot.Lods[0].Meshes[0] with
        {
            ImmutableSemanticHash = ContentHash.Compute([99]),
        };
        var changedLod = candidate.Snapshot.Lods[0] with { Meshes = [changedMesh] };
        var changedSnapshot = candidate.Snapshot with
        {
            Lods = [changedLod, .. candidate.Snapshot.Lods.Skip(1)],
        };

        var verification = ModelVerifier.Verify(before, changedSnapshot, plan);

        Assert.False(verification.IsValid);
        Assert.Contains(
            verification.Boundaries,
            boundary => boundary.Name == "mesh_non_draw_call_semantics_preserved" && boundary.Status == "failed");
    }

    [Fact]
    public async Task PlanFingerprintIncludesImmutableDependencyGraph()
    {
        var input = SyntheticAdapter.CreateThreeLodArtifact();
        var adapter = new SyntheticAdapter();
        var model = await adapter.InspectAsync(input, TestContext.Current.CancellationToken);
        var recipe = CreateRecipe(input.ContentHash, new Dictionary<string, int> { ["0"] = 1, ["1"] = 1, ["2"] = 1 });
        var firstBytes = "dependency-a"u8.ToArray();
        var secondBytes = "dependency-b"u8.ToArray();
        var firstDependency = new ArtifactContent("materials/accessory.vmat_c", ContentHash.Compute(firstBytes), firstBytes);
        var secondDependency = new ArtifactContent("materials/accessory.vmat_c", ContentHash.Compute(secondBytes), secondBytes);

        var first = MutationPlanner.CreatePlan(model, recipe, [firstDependency]);
        var second = MutationPlanner.CreatePlan(model, recipe, [secondDependency]);

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        Assert.Equal(2, first.Inputs.Count);
        Assert.Contains(first.Inputs, plannedInput => plannedInput.LogicalPath == firstDependency.LogicalPath && plannedInput.ContentHash == firstDependency.ContentHash);
    }

    private static RecipeDocument CreateRecipe(ContentHash inputHash, IReadOnlyDictionary<string, int> expected) => new()
    {
        SchemaVersion = 1,
        RecipeId = "remove-accessory",
        InputHash = inputHash,
        Operations =
        [
            new RemoveComponentOperation
            {
                OperationId = "remove-accessory",
                Selector = new ComponentSelector { Kind = "material_exact", MaterialPath = "materials/accessory.vmat" },
                ExpectedMatchesByLod = expected,
            },
        ],
    };

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class TestReportRenderer : IReportRenderer
    {
        public string RenderJson(EvidenceReport report) => JsonDefaults.Serialize(report);

        public string RenderMarkdown(EvidenceReport report) => $"# {report.ReportId}";
    }

    private sealed class MemoryWorkspace : IProjectWorkspace
    {
        private readonly ArtifactContent input;
        private readonly Dictionary<ContentHash, MutationPlan> plans = [];
        private readonly Dictionary<string, (PublishedBuild Build, ArtifactContent Content)> builds = new(StringComparer.Ordinal);

        public MemoryWorkspace(ArtifactContent input)
        {
            this.input = input;
            Project = new ProjectManifest
            {
                ProjectId = "synthetic",
                CreatedUtc = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero),
                Input = new ProjectArtifactManifest
                {
                    LogicalPath = input.LogicalPath,
                    ContentHash = input.ContentHash,
                    Size = input.Bytes.Length,
                    ObjectRelativePath = "objects/input",
                    SourcePath = "synthetic",
                },
                DependencyGraphComplete = true,
                ResourceRoots = ["synthetic"],
            };
        }

        public IReadOnlyDictionary<string, (PublishedBuild Build, ArtifactContent Content)> Builds => builds;

        private ProjectManifest Project { get; }

        public Task<ProjectManifest> PublishProjectAsync(ProjectPublicationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Project);

        public Task<ProjectManifest> LoadProjectAsync(string projectRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(Project);

        public Task<ArtifactContent> LoadInputAsync(string projectRoot, ProjectManifest project, CancellationToken cancellationToken = default) =>
            Task.FromResult(input);

        public Task<IReadOnlyList<ArtifactContent>> LoadDependenciesAsync(string projectRoot, ProjectManifest project, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ArtifactContent>>([]);

        public Task SavePlanAsync(string projectRoot, MutationPlan plan, CancellationToken cancellationToken = default)
        {
            plans[plan.Fingerprint] = plan;
            return Task.CompletedTask;
        }

        public Task<MutationPlan> LoadPlanAsync(string projectRoot, ContentHash fingerprint, CancellationToken cancellationToken = default) =>
            Task.FromResult(plans[fingerprint]);

        public Task<BuildPublicationResult> PublishBuildAsync(string projectRoot, BuildPublication publication, CancellationToken cancellationToken = default)
        {
            var build = new PublishedBuild(
                publication.BuildId,
                publication.Candidate.LogicalPath,
                publication.Candidate.Snapshot.Artifact.ContentHash,
                publication.Candidate.Content.Length,
                publication.Plan.Fingerprint,
                ContentHash.Compute(System.Text.Encoding.UTF8.GetBytes(publication.EvidenceJson)),
                ContentHash.Compute(System.Text.Encoding.UTF8.GetBytes(publication.EvidenceMarkdown)));
            builds[build.BuildId] = (build, new ArtifactContent(build.LogicalPath, build.ContentHash, publication.Candidate.Content));
            return Task.FromResult(new BuildPublicationResult(build, publication.EvidenceJson, publication.EvidenceMarkdown));
        }

        public Task<(PublishedBuild Build, ArtifactContent Content)> LoadBuildAsync(string projectRoot, string buildId, CancellationToken cancellationToken = default) =>
            Task.FromResult(builds[buildId]);

        public Task SaveEvidenceAsync(string projectRoot, string reportId, string json, string markdown, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class UnusedResourceSourceFactory : IProjectResourceSourceFactory
    {
        public ProjectResourceSource Create(ProjectCreationRequest request) =>
            throw new InvalidOperationException("Synthetic pipeline tests do not create filesystem projects.");
    }

    private sealed class SyntheticAdapter : IModelInspector, IResourceDependencyReader, IModelRewriter
    {
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
        private static readonly byte[] VertexPayload = [11, 22, 33, 44];

        public string AdapterName => "synthetic_model";

        public string AdapterVersion => "1";

        public IReadOnlyDictionary<string, string> ComponentVersions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["s2modkit.adapter.synthetic"] = "1",
        };

        public static ArtifactContent CreateThreeLodArtifact()
        {
            var document = new SyntheticDocument
            {
                Lods = Enumerable.Range(0, 3).Select(lod => new SyntheticLod
                {
                    Level = lod,
                    DrawCalls =
                    [
                        new SyntheticDrawCall { MaterialPath = "materials/body.vmat", IndexStart = lod * 100, IndexCount = 60 },
                        new SyntheticDrawCall { MaterialPath = "materials/accessory.vmat", IndexStart = lod * 100 + 60, IndexCount = 12 },
                    ],
                }).ToArray(),
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
            return new ArtifactContent("models/synthetic.vmdl_c.s2synthetic", ContentHash.Compute(bytes), bytes);
        }

        public bool CanInspect(ArtifactContent artifact) => artifact.LogicalPath.EndsWith(".s2synthetic", StringComparison.Ordinal);

        public bool CanReadDependencies(ArtifactContent artifact) => CanInspect(artifact);

        public Task<IReadOnlyList<ResourceDependency>> ReadDependenciesAsync(ArtifactContent artifact, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ResourceDependency>>([]);

        public Task<ModelSnapshot> InspectAsync(ArtifactContent artifact, CancellationToken cancellationToken = default)
        {
            var document = JsonSerializer.Deserialize<SyntheticDocument>(artifact.Bytes.Span, Options)
                ?? throw Errors.Unsupported("SYNTHETIC_DOCUMENT_INVALID", "Synthetic fixture is empty.", "Regenerate the fixture.");
            var dataHash = ContentHash.Compute(artifact.Bytes.Span);
            var lods = document.Lods.Select(lod => new LodSnapshot(
                lod.Level,
                [
                    new MeshSnapshot(
                        $"models/synthetic_lod{lod.Level}.vmesh_c",
                        0,
                        0,
                        ContentHash.Compute(VertexPayload),
                        lod.DrawCalls.Select((drawCall, ordinal) => DrawCallSnapshot.Create(
                            $"models/synthetic_lod{lod.Level}.vmesh_c",
                            lod.Level,
                            0,
                            ordinal,
                            drawCall.MaterialPath,
                            drawCall.IndexStart,
                            drawCall.IndexCount)).ToArray()),
                ])).ToArray();
            var snapshot = new ModelSnapshot(
                new ArtifactSnapshot(
                    artifact.LogicalPath,
                    artifact.ContentHash,
                    artifact.Bytes.Length,
                    [
                        new ResourceBlockSnapshot("DATA", 0, 0, artifact.Bytes.Length, dataHash),
                        new ResourceBlockSnapshot("VBIB", 1, artifact.Bytes.Length, VertexPayload.Length, ContentHash.Compute(VertexPayload)),
                    ]),
                lods);
            return Task.FromResult(snapshot);
        }

        public bool CanRewrite(ModelSnapshot model, MutationPlan plan) => true;

        public async Task<RewriteCandidate> RewriteAsync(ArtifactContent input, ModelSnapshot model, MutationPlan plan, CancellationToken cancellationToken = default)
        {
            var document = JsonSerializer.Deserialize<SyntheticDocument>(input.Bytes.Span, Options)!;
            var selectedIds = plan.Operations.SelectMany(operation => operation.SelectedDrawCalls).Select(call => call.DrawCallId).ToHashSet(StringComparer.Ordinal);
            foreach (var lod in document.Lods)
            {
                var resourcePath = $"models/synthetic_lod{lod.Level}.vmesh_c";
                lod.DrawCalls = lod.DrawCalls.Where((drawCall, ordinal) => !selectedIds.Contains(DrawCallSnapshot.Create(
                    resourcePath,
                    lod.Level,
                    0,
                    ordinal,
                    drawCall.MaterialPath,
                    drawCall.IndexStart,
                    drawCall.IndexCount).Id)).ToArray();
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
            var artifact = new ArtifactContent(input.LogicalPath, ContentHash.Compute(bytes), bytes);
            var snapshot = await InspectAsync(artifact, cancellationToken);
            return new RewriteCandidate(input.LogicalPath, bytes, snapshot);
        }

        private sealed record SyntheticDocument
        {
            public IReadOnlyList<SyntheticLod> Lods { get; init; } = [];
        }

        private sealed record SyntheticLod
        {
            public int Level { get; init; }

            public IReadOnlyList<SyntheticDrawCall> DrawCalls { get; set; } = [];
        }

        private sealed record SyntheticDrawCall
        {
            public string MaterialPath { get; init; } = string.Empty;

            public long IndexStart { get; init; }

            public long IndexCount { get; init; }
        }
    }
}
