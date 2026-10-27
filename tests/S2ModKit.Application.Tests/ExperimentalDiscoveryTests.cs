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
