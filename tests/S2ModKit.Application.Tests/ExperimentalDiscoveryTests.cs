using System.Text;
using System.Text.Json.Nodes;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class PreciseComponentDiscoveryServiceTests
{
    [Fact]
    public async Task ExperimentalDiscoveryIsDeterministicAndCannotPromoteWithoutPlanner()
    {
        var fixture = CreateGunFixture(3);
        var strict = await new PreciseComponentDiscoveryService().DiscoverAsync(fixture.Input, fixture.Model, TestContext.Current.CancellationToken);
        var service = new ExperimentalComponentDiscoveryService(null, null);
        var experimental = await service.DiscoverAsync(fixture.Input, fixture.Model, TestContext.Current.CancellationToken);
        var repeated = await service.DiscoverAsync(fixture.Input, fixture.Model, TestContext.Current.CancellationToken);
        Assert.Equal(3, experimental.SchemaVersion);
        Assert.Null(strict.ExperimentalPolicy);
        Assert.Equal(new RuntimeMetadataPolicy("preserve_unverified", 1), experimental.ExperimentalPolicy);
        Assert.Equal(JsonDefaults.Serialize(experimental), JsonDefaults.Serialize(repeated));
        Assert.NotEqual(strict.DiscoveryFingerprint, experimental.DiscoveryFingerprint);
        Assert.Empty(strict.Candidates.Select(c => c.CandidateId).Intersect(experimental.Candidates.Select(c => c.CandidateId)));
        Assert.All(experimental.Candidates, candidate =>
        {
            var capabilities = candidate.Capabilities.Where(c => c.OperationVersion is 5 or 6).ToArray();
            Assert.Equal(2, capabilities.Length);
            Assert.All(capabilities, c => { Assert.Equal("unsupported", c.Availability); Assert.Equal("EXPERIMENTAL_PLANNER_UNAVAILABLE", Assert.Single(c.Reasons).Code); });
        });
        var schema = await JsonSchema.FromFileAsync(ExperimentalSchemaPath("component-discovery.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(schema.Validate(JsonDefaults.Serialize(strict)));
        Assert.Empty(schema.Validate(JsonDefaults.Serialize(experimental)));
        var legacy = await JsonSchema.FromFileAsync(ExperimentalSchemaPath("v2/component-discovery.schema.json"), TestContext.Current.CancellationToken);
        Assert.NotEmpty(legacy.Validate(JsonDefaults.Serialize(experimental)));
        var forged = JsonNode.Parse(JsonDefaults.Serialize(experimental))!;
        forged.AsObject().Remove("experimentalPolicy");
        Assert.NotEmpty(schema.Validate(forged.ToJsonString()));
        forged["schemaVersion"] = 2;
        Assert.NotEmpty(schema.Validate(forged.ToJsonString()));
    }

    [Fact]
    public async Task ExperimentalDiscoveryKeepsStrictRemoveWithExplicitDiscoveryContext()
    {
        var fixture = CreateGunFixture(3);
        var discovery = await new ExperimentalComponentDiscoveryService(null, null).DiscoverAsync(fixture.Input, fixture.Model, TestContext.Current.CancellationToken);
        var request = new RecipeScaffoldRequest([discovery.Candidates[0].CandidateId], "remove", "unused.json", ExperimentalDiscovery: true);
        var scaffolder = new ComponentRecipeScaffolder(null);
        var recipe = await scaffolder.CreateAsync(fixture.Input, fixture.Model, discovery, request, TestContext.Current.CancellationToken);
        Assert.Equal(1, recipe.SchemaVersion);
        Assert.IsType<RemoveComponentOperation>(Assert.Single(recipe.Operations));
        await Assert.ThrowsAsync<S2ModKitException>(() => scaffolder.CreateAsync(fixture.Input, fixture.Model, discovery, request with { ExperimentalDiscovery = false }, TestContext.Current.CancellationToken));
    }

    private static string ExperimentalSchemaPath(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "S2ModKit.slnx"))) root = root.Parent;
        return Path.Combine(root!.FullName, "schemas", name);
    }
}

public sealed partial class GuidedWorkflowTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task ExperimentalSessionParametersAreTypedAndSurviveResume(int version)
    {
        var options = new ExperimentalScaffoldOptions(new("preserve_unverified", 1),
            new() { Kind = "explicit_point", Point = new() { X = 3, Y = 2, Z = 10 } },
            version == 6 ? new("axis_ramp", 1, "z", 4, 6) : null);
        var session = GuidedWorkflow.CreateSession("catalogue.json", [new("source", GuidedWorkflowContract.CompiledModelSource, "Model", "model.vmdl_c")], false, true)
            with
        { SelectedOperationVersion = version, ExperimentalParameters = options };
        GuidedWorkflow.ValidateSession(session);
        var json = JsonDefaults.Serialize(session);
        var parsed = JsonDefaults.Deserialize<GuidedWorkflowSession>(Encoding.UTF8.GetBytes(json), "session");
        GuidedWorkflow.ValidateSession(parsed);
        Assert.Equal(options, parsed.ExperimentalParameters);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "S2ModKit.slnx"))) root = root.Parent;
        var schema = await JsonSchema.FromFileAsync(Path.Combine(root!.FullName, "schemas/guided-session.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(schema.Validate(json));
        var forged = JsonNode.Parse(json)!;
        forged["experimentalParameters"]!["pivot"]!["point"]!.AsObject().Remove("x");
        Assert.NotEmpty(schema.Validate(forged.ToJsonString()));
        Assert.Throws<S2ModKitException>(() => JsonDefaults.Deserialize<GuidedWorkflowSession>(Encoding.UTF8.GetBytes(forged.ToJsonString()), "session"));
        Assert.Throws<S2ModKitException>(() => GuidedWorkflow.ValidateSession(session with { SelectedOperationVersion = 1 }));
        Assert.Throws<S2ModKitException>(() => GuidedWorkflow.ValidateSession(session with { ExperimentalParameters = options with { Policy = new("verified", 1) } }));
        if (version == 6) Assert.Throws<S2ModKitException>(() => GuidedWorkflow.ValidateSession(session with { ExperimentalParameters = options with { Region = options.Region! with { FullFrom = 3 } } }));
    }

    [Fact]
    public async Task ExperimentalSessionsRoundTripWithoutChangingLegacyEnvelope()
    {
        GuidedSourceOption[] sources = [new("source", GuidedWorkflowContract.CompiledModelSource, "Model", "model.vmdl_c")];
        var strict = GuidedWorkflow.CreateSession("catalogue.json", sources, false);
        var experimental = GuidedWorkflow.CreateSession("catalogue.json", sources, false, experimental: true);
        GuidedWorkflow.ValidateSession(strict);
        GuidedWorkflow.ValidateSession(experimental);
        var json = JsonDefaults.Serialize(experimental);
        Assert.Equal(json, JsonDefaults.Serialize(JsonDefaults.Deserialize<GuidedWorkflowSession>(Encoding.UTF8.GetBytes(json), "session")));
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "S2ModKit.slnx"))) root = root.Parent;
        var schema = await JsonSchema.FromFileAsync(Path.Combine(root!.FullName, "schemas/guided-session.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(schema.Validate(JsonDefaults.Serialize(strict)));
        Assert.Empty(schema.Validate(json));
        var legacy = await JsonSchema.FromFileAsync(Path.Combine(root.FullName, "schemas/v1/guided-session.schema.json"), TestContext.Current.CancellationToken);
        Assert.NotEmpty(legacy.Validate(json));
        foreach (var field in new[] { "experimentalPolicy", "experimentalParameters" })
        {
            var forged = JsonNode.Parse(JsonDefaults.Serialize(strict))!;
            forged[field] = null;
            Assert.NotEmpty(schema.Validate(forged.ToJsonString()));
            Assert.Throws<S2ModKitException>(() => JsonDefaults.Deserialize<GuidedWorkflowSession>(Encoding.UTF8.GetBytes(forged.ToJsonString()), "session"));
        }
        foreach (var field in new[] { "experimentalPolicy", "schemaVersion", "sources" })
        {
            var forged = JsonNode.Parse(json)!;
            forged.AsObject().Remove(field);
            Assert.Throws<S2ModKitException>(() => JsonDefaults.Deserialize<GuidedWorkflowSession>(Encoding.UTF8.GetBytes(forged.ToJsonString()), "session"));
        }
        Assert.Throws<S2ModKitException>(() => GuidedWorkflow.ValidateSession(strict with { SelectedOperationVersion = 6 }));
        Assert.Throws<S2ModKitException>(() => GuidedWorkflow.ValidateSession(experimental with { ExperimentalPolicy = new("verified", 1) }));
        Assert.Throws<S2ModKitException>(() => JsonDefaults.Deserialize<GuidedWorkflowSession>(Encoding.UTF8.GetBytes(json.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 2, \"schemaVersion\": 1", StringComparison.Ordinal)), "session"));
    }

    [Fact]
    public void ExperimentalMenuChoicesRequireVersionedAcknowledgementAndDistinctLabels()
    {
        var model = new ComponentModelIdentity("models/test.vmdl_c", ContentHash.Compute("model"u8), 10);
        ComponentCapability[] capabilities = [new("transform_component", 5, "available", [new("AVAILABLE", "Probe passed.")], []), new("transform_component", 6, "available", [new("AVAILABLE", "Probe passed.")], [])];
        var discovery = new ComponentDiscoveryResultV2(3, model, ContentHash.Compute("discovery"u8), new("test", "1", new Dictionary<string, string>()),
            [new MaterialGroupComponentCandidateV2("cmp_0123456789abcdef01234567", model, "materials/test.vmat", "Part", [new(0, ["dc_0123456789abcdef01234567"], 1)], capabilities)], [])
        { ExperimentalPolicy = new("preserve_unverified", 1) };
        var actions = Assert.Single(GuidedWorkflow.CreateComponentChoices(discovery)).Actions;
        Assert.Equal(2, actions.Count);
        Assert.Equal(2, actions.Select(action => action.DisplayName).Distinct().Count());
        Assert.Contains(actions, action => action.Intent == "region-scale" && action.OperationVersion == 6);
        Assert.Throws<S2ModKitException>(() => GuidedWorkflow.CreateComponentChoices(discovery with { SchemaVersion = 2 }));
        Assert.Throws<S2ModKitException>(() => GuidedWorkflow.CreateComponentChoices(discovery with { ExperimentalPolicy = null }));
    }
}
