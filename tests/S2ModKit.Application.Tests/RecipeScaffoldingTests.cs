using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed class RecipeScaffoldingTests
{
    [Fact]
    public async Task RemoveScaffoldIsDeterministicAndContainsExactUnionMembership()
    {
        var fixture = await CreateFixtureAsync();
        var analyzer = new RecordingAnalyzer(ComponentDiscoveryContract.Unsupported);
        var request = Request(
            [fixture.Sword.CandidateId, fixture.Gem.CandidateId],
            RecipeScaffoldContract.RemoveIntent);
        var reverse = request with { ComponentIds = request.ComponentIds.Reverse().ToArray() };
        var service = new ComponentRecipeScaffolder(analyzer);

        var first = await service.CreateAsync(fixture.Input, fixture.Model, fixture.Discovery, request, TestContext.Current.CancellationToken);
        var second = await service.CreateAsync(fixture.Input, fixture.Model, fixture.Discovery, reverse, TestContext.Current.CancellationToken);
        var operation = Assert.IsType<RemoveComponentOperation>(Assert.Single(first.Operations));

        Assert.Equal(1, first.SchemaVersion);
        Assert.Equal(JsonDefaults.SerializeToUtf8(first), JsonDefaults.SerializeToUtf8(second));
        Assert.Collection(
            operation.ExpectedMatchesByLod.Keys,
            key => Assert.Equal("0", key),
            key => Assert.Equal("1", key));
        Assert.All(operation.ExpectedMatchesByLod.Values, value => Assert.Equal(2, value));
        Assert.Equal(
            fixture.AllCalls.Select(item => item.Id),
            operation.Selector.DrawCallIds);
        Assert.Equal(0, analyzer.CallCount);
        RecipeValidator.Validate(JsonDefaults.Deserialize<RecipeDocument>(JsonDefaults.SerializeToUtf8(first), "Scaffolded remove recipe"));

        var plan = MutationPlanner.CreatePlan(fixture.Model, first);
        Assert.Equal(4, Assert.Single(plan.Operations).SelectedDrawCalls.Count);
    }

    [Fact]
    public async Task TransformScaffoldRecomputesExactMultiMaterialUnion()
    {
        var fixture = await CreateFixtureAsync();
        var analyzer = new RecordingAnalyzer(ComponentDiscoveryContract.Available);
        var request = Request(
            [fixture.Gem.CandidateId, fixture.Sword.CandidateId],
            RecipeScaffoldContract.UniformScaleIntent) with
        {
            UniformScale = 2f,
            TranslationX = 1f,
            ReferenceLod = 1,
            MaximumVertexDisplacement = 64f,
        };

        var recipe = await new ComponentRecipeScaffolder(analyzer).CreateAsync(
            fixture.Input,
            fixture.Model,
            fixture.Discovery,
            request,
            TestContext.Current.CancellationToken);
        var operation = Assert.IsType<TransformComponentOperation>(Assert.Single(recipe.Operations));
        var selection = Assert.Single(analyzer.LastRequest!.Selections);

        Assert.Equal(2, recipe.SchemaVersion);
        Assert.Equal(ComponentDiscoveryV2Contract.CandidateUnionKind, selection.CandidateKind);
        Assert.Equal(["materials/gem.vmat", "materials/sword.vmat"], selection.MaterialPaths);
        Assert.Equal(fixture.AllCalls.Select(item => item.Id), selection.SelectedDrawCalls.Select(item => item.DrawCallId));
        Assert.Equal(4, operation.Selector.DrawCallIds!.Count);
        Assert.Equal(33, operation.ExpectedVerticesByLod["0"]);
        Assert.Equal(21, operation.ExpectedVerticesByLod["1"]);
        Assert.Equal(2f, operation.Transform.UniformScale);
        Assert.Equal(1f, operation.Transform.Translation.X);
        Assert.Equal(1, operation.Transform.Pivot.ReferenceLod);
        Assert.Equal(64f, operation.Limits.MaximumVertexDisplacement);
        RecipeValidator.Validate(recipe);
    }

    [Fact]
    public async Task UniformScaleScaffoldUsesAffineVersionWhenLegacyProfileIsUnavailable()
    {
        var fixture = await CreateFixtureAsync();
        var request = Request([fixture.Sword.CandidateId], RecipeScaffoldContract.UniformScaleIntent) with
        {
            UniformScale = 1.5f,
            MaximumVertexDisplacement = 64f,
        };
        var recipe = await new ComponentRecipeScaffolder(new AffineOnlyAnalyzer()).CreateAsync(
            fixture.Input, fixture.Model, fixture.Discovery, request, TestContext.Current.CancellationToken);

        var operation = Assert.IsType<TransformComponentOperation>(Assert.Single(recipe.Operations));
        Assert.Equal(5, recipe.SchemaVersion);
        Assert.Equal(4, operation.Version);
        Assert.Equal(new TransformVector3 { X = 1.5f, Y = 1.5f, Z = 1.5f }, operation.Transform.Scale);
        Assert.Equal("identity", operation.Transform.Rotation!.Kind);
        Assert.Equal("selection_bounds_center", operation.Transform.Pivot.Kind);
        RecipeValidator.Validate(recipe);
    }

    [Fact]
    public async Task ScaffoldedRecipesConformToTheirPublishedSchemas()
    {
        var fixture = await CreateFixtureAsync();
        var service = new ComponentRecipeScaffolder(new RecordingAnalyzer(ComponentDiscoveryContract.Available));
        var remove = await service.CreateAsync(
            fixture.Input,
            fixture.Model,
            fixture.Discovery,
            Request([fixture.Sword.CandidateId], RecipeScaffoldContract.RemoveIntent),
            TestContext.Current.CancellationToken);
        var transform = await service.CreateAsync(
            fixture.Input,
            fixture.Model,
            fixture.Discovery,
            Request([fixture.Sword.CandidateId, fixture.Gem.CandidateId], RecipeScaffoldContract.UniformScaleIntent) with
            {
                UniformScale = 1.5f,
                MaximumVertexDisplacement = 32f,
            },
            TestContext.Current.CancellationToken);
        var v1 = await JsonSchema.FromFileAsync(GetSchemaPath("v1/recipe.schema.json"), TestContext.Current.CancellationToken);
        var v2 = await JsonSchema.FromFileAsync(GetSchemaPath("v2/recipe.schema.json"), TestContext.Current.CancellationToken);

        Assert.Empty(v1.Validate(JsonDefaults.Serialize(remove)));
        Assert.Empty(v2.Validate(JsonDefaults.Serialize(transform)));
    }

    [Fact]
    public async Task TranslateScaffoldDefaultsUnspecifiedAxesAndReferenceLod()
    {
        var fixture = await CreateFixtureAsync();
        var request = Request(
            [fixture.Sword.CandidateId, fixture.Gem.CandidateId],
            RecipeScaffoldContract.TranslateIntent) with
        {
            TranslationY = -3.5f,
            MaximumVertexDisplacement = 8f,
        };

        var recipe = await new ComponentRecipeScaffolder(new RecordingAnalyzer(ComponentDiscoveryContract.Available))
            .CreateAsync(fixture.Input, fixture.Model, fixture.Discovery, request, TestContext.Current.CancellationToken);
        var operation = Assert.IsType<TransformComponentOperation>(Assert.Single(recipe.Operations));

        Assert.Equal(1f, operation.Transform.UniformScale);
        Assert.Equal(0f, operation.Transform.Translation.X);
        Assert.Equal(-3.5f, operation.Transform.Translation.Y);
        Assert.Equal(0f, operation.Transform.Translation.Z);
        Assert.Equal(0, operation.Transform.Pivot.ReferenceLod);
    }

    [Fact]
    public async Task CoupledProfileScaffoldsVersionThreeRecipeWithIndependentLimits()
    {
        var fixture = await CreateFixtureAsync();
        var recipe = await new ComponentRecipeScaffolder(
            new RecordingAnalyzer(ComponentDiscoveryContract.Available, operationVersion: 2)).CreateAsync(
                fixture.Input,
                fixture.Model,
                fixture.Discovery,
                Request([fixture.Sword.CandidateId, fixture.Gem.CandidateId], RecipeScaffoldContract.UniformScaleIntent) with
                {
                    UniformScale = 2f,
                    MaximumVertexDisplacement = 64f,
                    MaximumCollisionDisplacement = 48f,
                },
                TestContext.Current.CancellationToken);

        var operation = Assert.IsType<TransformComponentOperation>(Assert.Single(recipe.Operations));
        Assert.Equal(3, recipe.SchemaVersion);
        Assert.Equal(2, operation.Version);
        Assert.Equal("transform_coupled_convex", operation.PhysicsPolicy);
        Assert.Equal(48f, operation.Limits.MaximumCollisionDisplacement);
        var schema = await JsonSchema.FromFileAsync(GetSchemaPath("v3/recipe.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(schema.Validate(JsonDefaults.Serialize(recipe)));
    }

    [Theory]
    [InlineData(ComponentDiscoveryContract.Blocked, "SCAFFOLD_TRANSFORM_BLOCKED", ErrorCategory.UnsupportedCapability)]
    [InlineData(ComponentDiscoveryContract.Unsupported, "SCAFFOLD_TRANSFORM_UNSUPPORTED", ErrorCategory.UnsupportedCapability)]
    [InlineData(ComponentDiscoveryContract.Ambiguous, "SCAFFOLD_TRANSFORM_AMBIGUOUS", ErrorCategory.SelectionOrLod)]
    public async Task TransformScaffoldFailsClosedForUnavailableExactUnion(
        string availability,
        string expectedCode,
        ErrorCategory expectedCategory)
    {
        var fixture = await CreateFixtureAsync();
        var request = Request(
            [fixture.Sword.CandidateId, fixture.Gem.CandidateId],
            RecipeScaffoldContract.UniformScaleIntent) with
        {
            UniformScale = 1.5f,
            MaximumVertexDisplacement = 32f,
        };

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() =>
            new ComponentRecipeScaffolder(new RecordingAnalyzer(availability)).CreateAsync(
                fixture.Input,
                fixture.Model,
                fixture.Discovery,
                request,
                TestContext.Current.CancellationToken));

        Assert.Equal(expectedCode, exception.Error.Code);
        Assert.Equal(expectedCategory, exception.Error.Category);
    }

    [Fact]
    public async Task ScaffoldRejectsDuplicateUnknownAndStaleMembership()
    {
        var fixture = await CreateFixtureAsync();
        var service = new ComponentRecipeScaffolder(new RecordingAnalyzer(ComponentDiscoveryContract.Available));

        var duplicate = await Assert.ThrowsAsync<S2ModKitException>(() => service.CreateAsync(
            fixture.Input,
            fixture.Model,
            fixture.Discovery,
            Request([fixture.Sword.CandidateId, fixture.Sword.CandidateId], RecipeScaffoldContract.RemoveIntent),
            TestContext.Current.CancellationToken));
        Assert.Equal("SCAFFOLD_COMPONENT_DUPLICATE", duplicate.Error.Code);

        var unknown = await Assert.ThrowsAsync<S2ModKitException>(() => service.CreateAsync(
            fixture.Input,
            fixture.Model,
            fixture.Discovery,
            Request(["cmp_ffffffffffffffffffffffff"], RecipeScaffoldContract.RemoveIntent),
            TestContext.Current.CancellationToken));
        Assert.Equal("SCAFFOLD_COMPONENT_STALE", unknown.Error.Code);

        var staleSword = new MaterialGroupComponentCandidateV2(
            fixture.Sword.CandidateId,
            fixture.Sword.Model,
            fixture.Sword.MaterialPath,
            fixture.Sword.DisplayLabel,
            [fixture.Sword.Lods[0] with { DrawCallCount = 2 }, fixture.Sword.Lods[1]],
            fixture.Sword.Capabilities);
        var staleDiscovery = fixture.Discovery with
        {
            Candidates = [staleSword, fixture.Gem],
        };
        var stale = await Assert.ThrowsAsync<S2ModKitException>(() => service.CreateAsync(
            fixture.Input,
            fixture.Model,
            staleDiscovery,
            Request([staleSword.CandidateId], RecipeScaffoldContract.RemoveIntent),
            TestContext.Current.CancellationToken));
        Assert.Equal("SCAFFOLD_COMPONENT_STALE", stale.Error.Code);
    }

    [Fact]
    public async Task ScaffoldRejectsIncompleteUnionAndInvalidIntentParameters()
    {
        var fixture = await CreateFixtureAsync();
        var lodZero = fixture.Model.Lods.Single(item => item.Level == 0);
        var lodOne = fixture.Model.Lods.Single(item => item.Level == 1);
        var gemOnly = lodOne.Meshes.Single() with
        {
            DrawCalls = [lodOne.Meshes.Single().DrawCalls.Single(item => item.MaterialPath == "materials/gem.vmat")],
            MechanicalLineage = null,
        };
        var incompleteModel = new ModelSnapshot(
            fixture.Model.Artifact,
            [lodZero with { Meshes = [lodZero.Meshes.Single() with { MechanicalLineage = null }] }, lodOne with { Meshes = [gemOnly] }]);
        var discovery = await new PreciseComponentDiscoveryService().DiscoverAsync(
            fixture.Input,
            incompleteModel,
            TestContext.Current.CancellationToken);
        var incomplete = Assert.IsType<MaterialGroupComponentCandidateV2>(
            discovery.Candidates.Single(item => item is MaterialGroupComponentCandidateV2 material
                && material.MaterialPath == "materials/sword.vmat"));
        var service = new ComponentRecipeScaffolder(new RecordingAnalyzer(ComponentDiscoveryContract.Available));

        var missingLod = await Assert.ThrowsAsync<S2ModKitException>(() => service.CreateAsync(
            fixture.Input,
            incompleteModel,
            discovery,
            Request([incomplete.CandidateId], RecipeScaffoldContract.RemoveIntent),
            TestContext.Current.CancellationToken));
        Assert.Equal("COMPONENT_INCOMPLETE_LOD_COVERAGE", missingLod.Error.Code);

        var missingScale = await Assert.ThrowsAsync<S2ModKitException>(() => service.CreateAsync(
            fixture.Input,
            fixture.Model,
            fixture.Discovery,
            Request([fixture.Sword.CandidateId], RecipeScaffoldContract.UniformScaleIntent) with
            {
                MaximumVertexDisplacement = 16f,
            },
            TestContext.Current.CancellationToken));
        Assert.Equal("SCAFFOLD_SCALE_REQUIRED", missingScale.Error.Code);

        var identityTranslate = await Assert.ThrowsAsync<S2ModKitException>(() => service.CreateAsync(
            fixture.Input,
            fixture.Model,
            fixture.Discovery,
            Request([fixture.Sword.CandidateId], RecipeScaffoldContract.TranslateIntent) with
            {
                TranslationX = 0f,
                MaximumVertexDisplacement = 16f,
            },
            TestContext.Current.CancellationToken));
        Assert.Equal("TRANSFORM_IDENTITY", identityTranslate.Error.Code);
    }

    [Fact]
    public async Task MeshLineageScaffoldUsesExactWholeMeshMembershipWithoutManualIds()
    {
        var fixture = await CreateFixtureAsync();

        var recipe = await new ComponentRecipeScaffolder(new RecordingAnalyzer(ComponentDiscoveryContract.Available))
            .CreateAsync(
                fixture.Input,
                fixture.Model,
                fixture.Discovery,
                Request([fixture.Lineage.CandidateId], RecipeScaffoldContract.RemoveIntent),
                TestContext.Current.CancellationToken);
        var operation = Assert.IsType<RemoveComponentOperation>(Assert.Single(recipe.Operations));

        Assert.Equal(fixture.AllCalls.Select(item => item.Id), operation.Selector.DrawCallIds);
        Assert.All(operation.ExpectedMatchesByLod.Values, count => Assert.Equal(2, count));
    }

    [Fact]
    public async Task MixedCandidateUnionDeduplicatesOverlapBeforeCapabilityAnalysis()
    {
        var fixture = await CreateFixtureAsync();
        var analyzer = new RecordingAnalyzer(ComponentDiscoveryContract.Available);

        await new ComponentRecipeScaffolder(analyzer).CreateAsync(
            fixture.Input,
            fixture.Model,
            fixture.Discovery,
            Request([fixture.Sword.CandidateId, fixture.Lineage.CandidateId], RecipeScaffoldContract.UniformScaleIntent) with
            {
                UniformScale = 1.25f,
                MaximumVertexDisplacement = 32f,
            },
            TestContext.Current.CancellationToken);
        var selection = Assert.Single(analyzer.LastRequest!.Selections);

        Assert.Equal(ComponentDiscoveryV2Contract.CandidateUnionKind, selection.CandidateKind);
        Assert.Equal(fixture.AllCalls.Select(item => item.Id), selection.SelectedDrawCalls.Select(item => item.DrawCallId));
        Assert.Equal(selection.SelectedDrawCalls.Count, selection.SelectedDrawCalls.Select(item => item.DrawCallId).Distinct().Count());
    }

    private static RecipeScaffoldRequest Request(IReadOnlyList<string> ids, string intent) => new(ids, intent, "recipe.json");

    private static string GetSchemaPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "schemas", fileName);
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        const string resourcePath = "models/test/hero.vmdl_c";
        var bytes = "immutable-model"u8.ToArray();
        var hash = ContentHash.Compute(bytes);
        var lods = new List<LodSnapshot>();
        var calls = new List<DrawCallSnapshot>();
        for (var lod = 0; lod < 2; lod++)
        {
            var sword = DrawCallSnapshot.Create(resourcePath, lod, lod, 0, "materials/sword.vmat", 0, 3);
            var gem = DrawCallSnapshot.Create(resourcePath, lod, lod, 1, "materials/gem.vmat", 3, 3);
            calls.Add(sword);
            calls.Add(gem);
            var sourceName = lod == 0 ? "weapon" : $"weapon_lod{lod}";
            lods.Add(new LodSnapshot(
                lod,
                [new MeshSnapshot(resourcePath, lod, 10 + lod, ContentHash.Compute(new byte[] { (byte)lod }), [sword, gem])
                {
                    MechanicalLineage = new MechanicalMeshLineage("weapon", "weapon", sourceName),
                }]));
        }

        var blocks = lods.Select(lod => new ResourceBlockSnapshot(
            "MDAT",
            10 + lod.Level,
            lod.Level,
            1,
            lod.Meshes.Single().ImmutableSemanticHash)).ToArray();
        var model = new ModelSnapshot(new ArtifactSnapshot(resourcePath, hash, bytes.Length, blocks), lods);
        var input = new ArtifactContent(resourcePath, hash, bytes);
        var discovery = await new PreciseComponentDiscoveryService().DiscoverAsync(
            input,
            model,
            TestContext.Current.CancellationToken);
        var swordCandidate = Assert.IsType<MaterialGroupComponentCandidateV2>(
            discovery.Candidates.Single(item => item is MaterialGroupComponentCandidateV2 material
                && material.MaterialPath == "materials/sword.vmat"));
        var gemCandidate = Assert.IsType<MaterialGroupComponentCandidateV2>(
            discovery.Candidates.Single(item => item is MaterialGroupComponentCandidateV2 material
                && material.MaterialPath == "materials/gem.vmat"));
        var lineageCandidate = Assert.IsType<MeshLineageComponentCandidateV2>(
            discovery.Candidates.Single(item => item is MeshLineageComponentCandidateV2));
        return new Fixture(
            input,
            model,
            discovery,
            swordCandidate,
            gemCandidate,
            lineageCandidate,
            calls);
    }

    private sealed record Fixture(
        ArtifactContent Input,
        ModelSnapshot Model,
        ComponentDiscoveryResultV2 Discovery,
        MaterialGroupComponentCandidateV2 Sword,
        MaterialGroupComponentCandidateV2 Gem,
        MeshLineageComponentCandidateV2 Lineage,
        IReadOnlyList<DrawCallSnapshot> AllCalls);

    private sealed class RecordingAnalyzer(string availability, int operationVersion = 1) : IComponentCapabilityAnalyzer
    {
        public int CallCount { get; private set; }

        public ComponentCapabilityAnalysisRequest? LastRequest { get; private set; }

        public string AnalyzerName => "recording";

        public string AnalyzerVersion => "1";

        public IReadOnlyDictionary<string, string> ComponentVersions { get; } = new Dictionary<string, string>();

        public bool CanAnalyze(ArtifactContent input, ModelSnapshot model) => true;

        public Task<IReadOnlyList<ComponentCapabilityAnalysis>> AnalyzeAsync(
            ComponentCapabilityAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            var selection = Assert.Single(request.Selections);
            var geometry = availability == ComponentDiscoveryContract.Available
                ? new[]
                {
                    new ComponentGeometryLodFacts(0, 33, ContentHash.Compute("lod-0"u8), true),
                    new ComponentGeometryLodFacts(1, 21, ContentHash.Compute("lod-1"u8), true),
                }
                : [];
            return Task.FromResult<IReadOnlyList<ComponentCapabilityAnalysis>>(
                [new ComponentCapabilityAnalysis(
                    selection.SelectionId,
                    "transform_component",
                    operationVersion,
                    availability,
                    [new ComponentCapabilityReason("TEST_REASON", "Synthetic assessment.")],
                    geometry)]);
        }
    }

    private sealed class AffineOnlyAnalyzer : IComponentCapabilityAnalyzer, IAffineComponentCapabilityAnalyzer
    {
        public string AnalyzerName => "synthetic-affine";

        public string AnalyzerVersion => "1";

        public IReadOnlyDictionary<string, string> ComponentVersions { get; } = new Dictionary<string, string>();

        public bool CanAnalyze(ArtifactContent input, ModelSnapshot model) => true;

        public Task<IReadOnlyList<ComponentCapabilityAnalysis>> AnalyzeAsync(
            ComponentCapabilityAnalysisRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ComponentCapabilityAnalysis>>(
                [Assessment(request, 1, ComponentDiscoveryContract.Unsupported, [])]);

        public Task<IReadOnlyList<ComponentCapabilityAnalysis>> AnalyzeAffineAsync(
            ComponentCapabilityAnalysisRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ComponentCapabilityAnalysis>>(
                [Assessment(request, 4, ComponentDiscoveryContract.Available,
                    [new ComponentGeometryLodFacts(0, 33, ContentHash.Compute("lod-0"u8), true),
                        new ComponentGeometryLodFacts(1, 21, ContentHash.Compute("lod-1"u8), true)])]);

        private static ComponentCapabilityAnalysis Assessment(
            ComponentCapabilityAnalysisRequest request,
            int version,
            string availability,
            IReadOnlyList<ComponentGeometryLodFacts> geometry) => new(
                Assert.Single(request.Selections).SelectionId,
                "transform_component",
                version,
                availability,
                [new ComponentCapabilityReason("TEST_REASON", "Synthetic assessment.")],
                geometry);
    }
}
