using System.Text;
using System.Text.Json.Nodes;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class PreciseComponentDiscoveryServiceTests
{
    [Fact]
    public async Task DirectionalDiscoverySeparatesStructuralMappingFromFieldAdmissionAndPreservesOldIdentities()
    {
        var fixture = CoordinatedFixture(); var token = TestContext.Current.CancellationToken;
        var old = await new ExperimentalComponentDiscoveryService(null, null).DiscoverCoordinatedAsync(fixture.Input, fixture.Model, token);
        var current = await DirectionalAuthoring.DiscoverAsync(fixture.Input, fixture.Model, token);
        Assert.Equal(6, current.SchemaVersion);
        Assert.Empty(old.Candidates.Select(c => c.CandidateId).Intersect(current.Candidates.Select(c => c.CandidateId)));
        Assert.All(current.Candidates, c => { var cap = Assert.Single(c.Capabilities); Assert.Equal(9, cap.OperationVersion); Assert.Equal("unsupported", cap.Availability); Assert.Equal("DIRECTIONAL_OPTIONS_REQUIRED", cap.Reasons[0].Code); });
        var json = JsonDefaults.Serialize(current);
        Assert.Equal(json, JsonDefaults.Serialize(ReadDiscovery(json)));
        Assert.Equal(json, JsonDefaults.Serialize(await DirectionalAuthoring.DiscoverAsync(fixture.Input, Reverse(fixture).Model, token)));
        var schema = await JsonSchema.FromFileAsync(ExperimentalSchemaPath("component-discovery.schema.json"), token);
        var historical = await JsonSchema.FromFileAsync(ExperimentalSchemaPath("v5/component-discovery.schema.json"), token);
        Assert.Empty(schema.Validate(json)); Assert.Empty(schema.Validate(JsonDefaults.Serialize(old)));
        Assert.Empty(historical.Validate(JsonDefaults.Serialize(old))); Assert.NotEmpty(historical.Validate(json));
        var downgraded = JsonNode.Parse(json)!; downgraded["schemaVersion"] = 5;
        Assert.Throws<S2ModKitException>(() => ReadDiscovery(downgraded.ToJsonString())); Assert.NotEmpty(schema.Validate(downgraded.ToJsonString()));
    }

    [Fact]
    public async Task DirectionalScaffoldResolvesOneMemberAndDeduplicatesOverlappingViewsWithoutRelaxingCoordination()
    {
        var fixture = CoordinatedFixture(); var token = TestContext.Current.CancellationToken;
        var discovery = await DirectionalAuthoring.DiscoverAsync(fixture.Input, fixture.Model, token);
        var lineage = discovery.Candidates.OfType<MeshLineageComponentCandidateV2>().First();
        var union = ComponentCandidateUnionBuilder.Create(fixture.Model, [lineage], 6);
        Assert.Throws<S2ModKitException>(() => CoordinatedSelection.ResolveMembers(fixture.Model, union));
        var options = AuthoringOptions(fixture, union);
        var request = new RecipeScaffoldRequest([lineage.CandidateId], "directional-field", "unused.json", ExperimentalDiscovery: true, ExperimentalDiscoverySchemaVersion: 6, Directional: options);
        var scaffolder = new ComponentRecipeScaffolder(null);
        var result = await scaffolder.CreateAsync(fixture.Input, fixture.Model, discovery, request, token);
        Assert.Equal(10, result.SchemaVersion); var op = Assert.IsType<TransformComponentOperation>(result.Operations[0]);
        Assert.Equal(9, op.Version); Assert.Single(op.DirectionalTransform!.Members); Assert.Null(op.Transform);
        Assert.All(op.ExpectedVerticesByLod, l => Assert.Equal(3, l.Value));
        DirectionalContractValidator.ValidateRecipe(result);
        var all = discovery.Candidates.OfType<MaterialGroupComponentCandidateV2>().Single();
        var fullUnion = ComponentCandidateUnionBuilder.Create(fixture.Model, [all], 6);
        var fullRequest = request with { ComponentIds = [all.CandidateId], Directional = AuthoringOptions(fixture, fullUnion) };
        var full = await scaffolder.CreateAsync(fixture.Input, fixture.Model, discovery, fullRequest, token);
        var overlap = await scaffolder.CreateAsync(fixture.Input, fixture.Model, discovery, fullRequest with { ComponentIds = [lineage.CandidateId, all.CandidateId] }, token);
        Assert.Equal(JsonDefaults.Serialize(full), JsonDefaults.Serialize(overlap));
        foreach (var invalid in new[] { request with { ExperimentalDiscovery = false }, request with { Directional = null }, request with { Intent = "remove" },
            request with { MaximumVertexDisplacement = 10 }, request with { TranslationX = 0 }, request with { UniformScale = 1.5f }, request with { ComponentIds = [lineage.CandidateId, lineage.CandidateId] },
            request with { ComponentIds = ["cmp_000000000000000000000000"] }, request with { Directional = options with { InputHash = ContentHash.Compute("stale"u8) } },
            request with { Directional = options with { ZeroBoneBoxPolicy = new("preserve_all_zero_single_contributor_unverified", 1) } },
            request with { Directional = options with { Protection = new("keep_fixed", 1, []) } } })
            await Assert.ThrowsAsync<S2ModKitException>(() => scaffolder.CreateAsync(fixture.Input, fixture.Model, discovery, invalid, token));
    }

    private static DirectionalScaffoldOptions AuthoringOptions(SyntheticFixture fixture, ComponentCandidateUnion union)
    {
        // Obtain the exact source-derived mappings through the public schema-10 scaffold path.
        // Explicit source-bone assertions are independent of member labels.
        var field = ExperimentalVisualContractTests.DirectionalPlan().Operations[0].DirectionalTransformTarget!.DirectionalTransform.Field;
        var bone = new DirectionalBoneAssertion
        {
            Version = 1,
            AssertionId = "fixed",
            BoneName = "source_bone",
            BoneIndex = 0,
            RootSkeletonHash = fixture.Input.ContentHash,
            Lods = fixture.Model.Lods.Select(l => new DirectionalBoneLod(l.Level, fixture.Input.ContentHash, 1)).ToArray()
        };
        return new(fixture.Input.ContentHash, new("preserve_unverified", 1), new("reject", 1), new("reject", 1), field, new("keep_fixed", 1, [bone]), 64);
    }

    [Fact]
    public async Task DirectionalOptionsRequireEveryFieldRejectUnknownPropertiesAndRoundTripWithSchema()
    {
        var fixture = CoordinatedFixture(); var token = TestContext.Current.CancellationToken;
        var discovery = await DirectionalAuthoring.DiscoverAsync(fixture.Input, fixture.Model, token);
        var options = AuthoringOptions(fixture, ComponentCandidateUnionBuilder.Create(fixture.Model, [discovery.Candidates[0]], 6));
        var json = JsonDefaults.Serialize(options);
        Assert.Equal(json, JsonDefaults.Serialize(DirectionalAuthoring.ReadOptions(Encoding.UTF8.GetBytes(json))));
        var schema = await JsonSchema.FromFileAsync(ExperimentalSchemaPath("directional-field-options.schema.json"), token);
        Assert.Empty(schema.Validate(json));
        foreach (var property in JsonNode.Parse(json)!.AsObject().Select(p => p.Key).ToArray())
        {
            var invalid = JsonNode.Parse(json)!; invalid.AsObject().Remove(property);
            Assert.Throws<S2ModKitException>(() => DirectionalAuthoring.ReadOptions(Encoding.UTF8.GetBytes(invalid.ToJsonString())));
            Assert.NotEmpty(schema.Validate(invalid.ToJsonString()));
        }
        var unknown = JsonNode.Parse(json)!; unknown["field"]!["anatomy"] = "hand";
        Assert.Throws<S2ModKitException>(() => DirectionalAuthoring.ReadOptions(Encoding.UTF8.GetBytes(unknown.ToJsonString())));
        Assert.NotEmpty(schema.Validate(unknown.ToJsonString()));
        foreach (var missing in new[] { "point-axis", "zero-lod" })
        {
            var incomplete = JsonNode.Parse(json)!;
            if (missing == "point-axis") incomplete["field"]!["pivot"]!["point"]!.AsObject().Remove("z");
            else incomplete["protection"]!["assertions"]![0]!["lods"]![0]!.AsObject().Remove("lod");
            Assert.Throws<S2ModKitException>(() => DirectionalAuthoring.ReadOptions(Encoding.UTF8.GetBytes(incomplete.ToJsonString())));
            Assert.NotEmpty(schema.Validate(incomplete.ToJsonString()));
        }
        var duplicate = json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1", StringComparison.Ordinal);
        Assert.Throws<S2ModKitException>(() => DirectionalAuthoring.ReadOptions(Encoding.UTF8.GetBytes(duplicate)));
    }
}
