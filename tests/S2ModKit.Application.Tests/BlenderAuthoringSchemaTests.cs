using System.Text.Json.Nodes;
using NJsonSchema;

namespace S2ModKit.Application.Tests;

public sealed class BlenderAuthoringSchemaTests
{
    [Theory]
    [InlineData("handoff")]
    [InlineData("delta")]
    [InlineData("policy")]
    [InlineData("preflight")]
    public async Task StandaloneAuthoringContractsAcceptSyntheticFixturesAndRejectUnknownPropertiesAndVersions(string name)
    {
        var root = RepositoryRoot();
        var schema = await JsonSchema.FromFileAsync(
            Path.Combine(root, "tools", "blender", "schemas", "v1", name + ".schema.json"),
            TestContext.Current.CancellationToken);
        var json = File.ReadAllText(Path.Combine(root, "tools", "blender", "tests", "fixtures", name + ".json"));
        Assert.Empty(schema.Validate(json));

        var document = JsonNode.Parse(json)!.AsObject();
        document["unknown"] = true;
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
        document.Remove("unknown");
        document["schemaVersion"] = 2;
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
        document["schemaVersion"] = true;
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
    }

    [Fact]
    public async Task AuthoringBundleRejectsUnknownMemberAttributesAndMalformedPositionWords()
    {
        var root = RepositoryRoot();
        var schema = await JsonSchema.FromFileAsync(
            Path.Combine(root, "tools", "blender", "schemas", "v1", "handoff.schema.json"),
            TestContext.Current.CancellationToken);
        var document = JsonNode.Parse(File.ReadAllText(
            Path.Combine(root, "tools", "blender", "tests", "fixtures", "handoff.json")))!;
        var member = document["members"]![0]!;
        member["UV"] = true;
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
        member.AsObject().Remove("UV");
        member["positions"]![0]![0] = "NaN";
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
    }

    [Fact]
    public async Task NeckProfileAcceptsSyntheticFixtureAndRejectsUnsupportedFieldsAndSelectors()
    {
        var root = RepositoryRoot();
        var schema = await JsonSchema.FromFileAsync(
            Path.Combine(root, "tools", "blender", "schemas", "v1", "neck-profile.schema.json"),
            TestContext.Current.CancellationToken);
        var document = JsonNode.Parse(File.ReadAllText(
            Path.Combine(root, "tools", "blender", "tests", "fixtures", "neck-profile.json")))!;
        Assert.Empty(schema.Validate(document.ToJsonString()));
        document["gameReady"] = true;
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
        document.AsObject().Remove("gameReady");
        document["fade"]!["unknown"] = true;
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
        document["fade"]!.AsObject().Remove("unknown");
        document["version"] = true;
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
        document["version"] = 1;
        document["headBoundaryVertices"]![1] = 0;
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
        document["headBoundaryVertices"]![1] = 1;
        document["mergeTolerance"] = 0.1;
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
    }

    [Fact]
    public async Task ReadOnlyMeasurementProfileAcceptsSyntheticFixtureAndRejectsUnsupportedContracts()
    {
        var root = RepositoryRoot();
        var schema = await JsonSchema.FromFileAsync(
            Path.Combine(root, "tools", "blender", "schemas", "v1", "measure-profile.schema.json"),
            TestContext.Current.CancellationToken);
        var document = JsonNode.Parse(File.ReadAllText(
            Path.Combine(root, "tools", "blender", "tests", "fixtures", "measure-profile.json")))!;
        Assert.Empty(schema.Validate(document.ToJsonString()));
        document["frame"]!["pose"] = "evaluated_pose";
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
        document["frame"]!["pose"] = "mesh_basis/bone_rest";
        document["operation"] = "character_measure@2";
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
        document["operation"] = "character_measure@1";
        document["inputs"]![0]!["rig"]!["unknown"] = true;
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
        document["inputs"]![0]!["rig"]!.AsObject().Remove("unknown");
        document["version"] = true;
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
        document["version"] = 1;
        document["inputs"]![0]!["landmarks"]!["jaw"]!["ids"]![0] = true;
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
        document["inputs"]![0]!["landmarks"]!["jaw"]!["ids"]![0] = 7;
        document["inputs"]![0]!["units"] = "cm";
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "S2ModKit.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root unavailable.");
    }
}
