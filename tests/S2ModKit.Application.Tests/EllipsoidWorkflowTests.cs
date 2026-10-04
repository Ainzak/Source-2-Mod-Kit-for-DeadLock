using System.Text;
using System.Text.Json.Nodes;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class PreciseComponentDiscoveryServiceTests
{
    [Fact]
    public async Task EllipsoidDiscoveryWithoutPlannerCannotAdvertiseLocalEditingAndRetainsOldRoute()
    {
        var fixture = CreateGunFixture(3);
        var service = new ExperimentalComponentDiscoveryService(null, null);
        var old = await service.DiscoverAsync(fixture.Input, fixture.Model, TestContext.Current.CancellationToken);
        var current = await service.DiscoverEllipsoidAsync(fixture.Input, fixture.Model, TestContext.Current.CancellationToken);
        Assert.Equal(3, old.SchemaVersion);
        Assert.Equal(4, current.SchemaVersion);
        Assert.Empty(old.Candidates.Select(c => c.CandidateId).Intersect(current.Candidates.Select(c => c.CandidateId)));
        Assert.Equal(JsonDefaults.Serialize(current), JsonDefaults.Serialize(await service.DiscoverEllipsoidAsync(fixture.Input, fixture.Model, TestContext.Current.CancellationToken)));
        Assert.All(current.Candidates, c => Assert.Equal("unsupported", Assert.Single(c.Capabilities, p => p.OperationVersion == 7).Availability));
        Assert.DoesNotContain(GuidedWorkflow.CreateComponentChoices(current).SelectMany(c => c.Actions), a => a.OperationVersion == 7);
        var json = JsonDefaults.Serialize(current);
        Assert.Equal(json, JsonDefaults.Serialize(ReadDiscovery(json)));
        var schema = await JsonSchema.FromFileAsync(ExperimentalSchemaPath("component-discovery.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(schema.Validate(json));
        var legacy = await JsonSchema.FromFileAsync(ExperimentalSchemaPath("v3/component-discovery.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(legacy.Validate(JsonDefaults.Serialize(old)));
        Assert.NotEmpty(legacy.Validate(json));
        foreach (var property in new[] { "experimentalPolicy", "schemaVersion", "candidates" })
        {
            var bad = JsonNode.Parse(json)!; bad.AsObject().Remove(property);
            Assert.Throws<S2ModKitException>(() => ReadDiscovery(bad.ToJsonString()));
        }
        var downgraded = JsonNode.Parse(json)!; downgraded["schemaVersion"] = 3;
        Assert.Throws<S2ModKitException>(() => ReadDiscovery(downgraded.ToJsonString()));
        foreach (var property in new[] { "operationKind", "operationVersion", "availability", "geometryByLod" })
        {
            var bad = JsonNode.Parse(json)!; bad["candidates"]![0]!["capabilities"]![0]!.AsObject().Remove(property);
            Assert.Throws<S2ModKitException>(() => ReadDiscovery(bad.ToJsonString()));
            Assert.NotEmpty(schema.Validate(bad.ToJsonString()));
        }
    }

    [Fact]
    public async Task EllipsoidScaffoldingRequiresProbeAndProducesExplicitCanonicalAllLodIntent()
    {
        var fixture = CreateGunFixture(3);
        var template = ExperimentalVisualContractTests.EllipsoidPlan().Operations[0].EllipsoidTransformTarget!.Buffers[0];
        var model = fixture.Model with
        {
            Lods = fixture.Model.Lods.Select(l => l with
            {
                Meshes = l.Meshes.Select(m => m with
                {
                    Geometry = new("ready", "synthetic", [new(0, 1, 3, 28, template.VertexBlockInputHash, template.InputDecodedVertexBufferHash, template.PositionLayout)],
                [new(0, 2, 3, 2, template.IndexBlockInputHash, template.DecodedIndexBufferHash)],
                [new(m.DrawCalls[0].Id, 0, 0, 0, 3, 3, m.ImmutableSemanticHash, template.BeforeBounds, true)], template.Codec),
                }).ToArray()
            }).ToArray()
        };
        var discovery = await new ExperimentalComponentDiscoveryService(null, null).DiscoverEllipsoidAsync(fixture.Input, model, TestContext.Current.CancellationToken);
        var candidate = discovery.Candidates.OfType<MeshLineageComponentCandidateV2>().First();
        candidate = candidate with { Capabilities = [new("transform_component", 7, "available", [new("FIXTURE_PROBE", "Synthetic eligibility only.")], [])] };
        discovery = discovery with { Candidates = [candidate] };
        var intent = ((TransformComponentOperation)ExperimentalVisualContractTests.EllipsoidRecipe().Operations[0]).LocalTransform!;
        var request = new RecipeScaffoldRequest([candidate.CandidateId], "ellipsoid-scale", "unused.json", MaximumVertexDisplacement: 64,
            ExperimentalDiscovery: true, Ellipsoid: new(new("preserve_unverified", 1), intent), ExperimentalDiscoverySchemaVersion: 4);
        var scaffolder = new ComponentRecipeScaffolder(null);
        var recipe = await scaffolder.CreateAsync(fixture.Input, model, discovery, request, TestContext.Current.CancellationToken);
        Assert.Equal(8, recipe.SchemaVersion);
        var operation = Assert.IsType<TransformComponentOperation>(Assert.Single(recipe.Operations));
        Assert.Equal(7, operation.Version); Assert.Null(operation.Transform); Assert.Equal(intent, operation.LocalTransform);
        Assert.Equal(3, operation.ExpectedVerticesByLod!.Count);
        Assert.Equal(JsonDefaults.Serialize(recipe), JsonDefaults.Serialize(await scaffolder.CreateAsync(fixture.Input, model, discovery, request, TestContext.Current.CancellationToken)));
        foreach (var bad in new[] { request with { ExperimentalDiscovery = false }, request with { Ellipsoid = null }, request with { ReferenceLod = 0 },
            request with { TranslationX = 0 }, request with { MaximumVertexDisplacement = null }, request with { UniformScale = 2 } })
            await Assert.ThrowsAsync<S2ModKitException>(() => scaffolder.CreateAsync(fixture.Input, model, discovery, bad, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<S2ModKitException>(() => scaffolder.CreateAsync(fixture.Input, model, discovery with { Candidates = [candidate with { Capabilities = [] }] }, request, TestContext.Current.CancellationToken));
    }

    private static ComponentDiscoveryResultV2 ReadDiscovery(string json) => JsonDefaults.Deserialize<ComponentDiscoveryResultV2>(Encoding.UTF8.GetBytes(json), "discovery");
}

public sealed partial class GuidedWorkflowTests
{
    [Fact]
    public async Task LocalFieldSessionRoundTripsPreviewAndRejectsStaleOrIncompleteResume()
    {
        var transform = ((TransformComponentOperation)ExperimentalVisualContractTests.EllipsoidRecipe().Operations[0]).LocalTransform!;
        var plan = ContentHash.Compute("plan"u8);
        var session = GuidedWorkflow.CreateSession("catalogue.json", [new("source", GuidedWorkflowContract.CompiledModelSource, "Model", "model.vmdl_c")], false, true) with
        {
            Step = GuidedWorkflowContract.OutputSelectionStep,
            SelectedSourceId = "source",
            SelectedHeroId = "test",
            SelectedResourceId = "test.primary",
            SelectedLogicalPath = "models/test.vmdl_c",
            ProjectRoot = "memory",
            ProjectReady = true,
            SelectedComponentId = "cmp_0123456789abcdef01234567",
            RecipePath = "recipe.json",
            RecipeContentHash = ContentHash.Compute("recipe"u8),
            PlannedDrawCallCount = 1,
            PlannedLodCount = 1,
            PlannedTargetBlockCount = 2,
            PlannedVertexCount = 3,
            PlannedCoupledCollision = false,
            SelectedOperationKind = "transform_component",
            SelectedOperationVersion = 7,
            SelectedIntent = "ellipsoid-scale",
            UniformScale = 2,
            MaximumVertexDisplacement = 64,
            EllipsoidParameters = new(new("preserve_unverified", 1), transform),
            PlanFingerprint = plan,
            EllipsoidPreview = new(ContentHash.Compute("preview"u8), plan, ContentHash.Compute("summary"u8), ContentHash.Compute("sheet"u8), "summary.json", "sheet.svg"),
        };
        var json = JsonDefaults.Serialize(session);
        Assert.Equal(json, JsonDefaults.Serialize(ReadLocalSession(json)));
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "S2ModKit.slnx"))) root = root.Parent;
        var schema = await JsonSchema.FromFileAsync(Path.Combine(root!.FullName, "schemas/guided-session.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(schema.Validate(json));
        foreach (var property in new[] { "ellipsoidParameters", "ellipsoidPreview" })
        {
            var bad = JsonNode.Parse(json)!; bad.AsObject().Remove(property);
            Assert.NotEmpty(schema.Validate(bad.ToJsonString()));
        }
        foreach (var bad in new[] { session with { SchemaVersion = 2 }, session with { UniformScale = 1.5f }, session with { TranslationX = 0 },
            session with { MaximumVertexDisplacement = null }, session with { SelectedIntent = "uniform-scale" }, session with { EllipsoidPreview = null }, session with { EllipsoidParameters = null },
            session with { PlanFingerprint = ContentHash.Compute("stale"u8) },
            session with { EllipsoidParameters = session.EllipsoidParameters with { LocalTransform = transform with { Field = transform.Field with { OuterRadii = new() } } } } })
            Assert.Throws<S2ModKitException>(() => ReadLocalSession(JsonDefaults.Serialize(bad)));
        foreach (var property in new[] { "uniformScale", "field", "numericalPolicy" })
        {
            var bad = JsonNode.Parse(json)!; bad["ellipsoidParameters"]!["localTransform"]!.AsObject().Remove(property);
            Assert.Throws<S2ModKitException>(() => ReadLocalSession(bad.ToJsonString()));
            Assert.NotEmpty(schema.Validate(bad.ToJsonString()));
        }
        var old = GuidedWorkflow.CreateSession("catalogue.json", session.Sources, false, true) with { SchemaVersion = 2 };
        Assert.Equal(2, ReadLocalSession(JsonDefaults.Serialize(old)).SchemaVersion);
        Assert.Throws<S2ModKitException>(() => ReadLocalSession(JsonDefaults.Serialize(old with { SelectedOperationVersion = 7 })));
        foreach (var field in new[] { "ellipsoidParameters", "ellipsoidPreview" })
        { var bad = JsonNode.Parse(JsonDefaults.Serialize(old))!; bad[field] = null; Assert.Throws<S2ModKitException>(() => ReadLocalSession(bad.ToJsonString())); }
    }

    [Fact]
    public void LocalFieldMenuHasOneDistinctActionOnlyAfterAcknowledgedProbe()
    {
        var model = new ComponentModelIdentity("models/test.vmdl_c", ContentHash.Compute("model"u8), 1);
        ComponentCapability[] capabilities = [new("transform_component", 5, "available", [], []), new("transform_component", 6, "available", [], []), new("transform_component", 7, "available", [], [])];
        var discovery = new ComponentDiscoveryResultV2(4, model, ContentHash.Compute("discovery"u8), new("test", "1", new Dictionary<string, string>()),
            [new MaterialGroupComponentCandidateV2("cmp_0123456789abcdef01234567", model, "materials/test.vmat", "Part", [new(0, ["dc_0123456789abcdef01234567"], 1)], [.. capabilities, .. capabilities])], [])
        { ExperimentalPolicy = new("preserve_unverified", 1) };
        var actions = Assert.Single(GuidedWorkflow.CreateComponentChoices(discovery)).Actions;
        Assert.Equal(4, actions.Count); Assert.Equal(4, actions.Select(a => a.DisplayName).Distinct().Count());
        Assert.Equal("Experimental local ellipsoid scale", Assert.Single(actions, a => a.Intent == RecipeScaffoldContract.EllipsoidScaleIntent).DisplayName);
        Assert.Throws<S2ModKitException>(() => GuidedWorkflow.CreateComponentChoices(discovery with { ExperimentalPolicy = null }));
    }

    private static GuidedWorkflowSession ReadLocalSession(string json) => JsonDefaults.Deserialize<GuidedWorkflowSession>(Encoding.UTF8.GetBytes(json), "session");
}
