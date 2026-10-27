using System.Text.Json.Nodes;
using NJsonSchema;

namespace S2ModKit.Application.Tests;

public sealed class SdkWorkflowSchemaTests
{
    [Theory]
    [InlineData("config")]
    [InlineData("manifest")]
    [InlineData("intent")]
    [InlineData("intent-v2")]
    [InlineData("intent-v3")]
    [InlineData("intent-v4")]
    [InlineData("surface-views")]
    [InlineData("plan")]
    [InlineData("evidence")]
    public async Task ExperimentalSdkSchemasAcceptSyntheticFixtureAndRejectUnknownFieldsAndVersions(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "S2ModKit.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root unavailable.");
        var schema = await JsonSchema.FromFileAsync(
            Path.Combine(root, "tools", "sdk", "schemas", name.StartsWith("intent-v", StringComparison.Ordinal) ? name[7..] : "v1",
                (name.StartsWith("intent-v", StringComparison.Ordinal) ? "intent" : name) + ".schema.json"),
            TestContext.Current.CancellationToken);
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "tools", "sdk", "tests", "fixtures", name + ".json")))!;
        Assert.Empty(schema.Validate(fixture.ToJsonString()));
        var version = name == "intent-v4" ? 4 : name == "intent-v3" ? 3 : name == "intent-v2" ? 2 : 1;
        fixture["version"] = 99;
        Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
        fixture["version"] = version;
        fixture["extra"] = true;
        Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
        fixture.AsObject().Remove("extra");
        if (name == "intent-v2")
        {
            fixture["replacement"]!["extra"] = true;
            Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
            fixture["replacement"]!.AsObject().Remove("extra");
            fixture["replacement"]!["geometryMode"] = "arbitrary";
            Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
        }
        if (name == "intent-v3")
        {
            fixture["patch"]!["extra"] = true;
            Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
            fixture["patch"]!.AsObject().Remove("extra");
            fixture["patch"]!["scale"] = 2;
            Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
        }
        if (name == "intent-v4")
        {
            fixture["surface"]!["extra"] = true;
            Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
            fixture["surface"]!.AsObject().Remove("extra");
            fixture["bindTransport"] = "automatic";
            Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
            fixture["bindTransport"] = "exporter";
            fixture["surface"]!["maxBoundaryAngleChange"] = 90;
            Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
        }
        if (name == "intent")
        {
            fixture["scales"]![0]!["axes"]![0] = -1;
            Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
            fixture["scales"]![0]!["axes"]![0] = 1;
            fixture["scales"]![0]!["undeclared"] = true;
            Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
        }

        if (name == "evidence")
        {
            fixture["productionAdmission"] = true;
            Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
            fixture["productionAdmission"] = false;
            fixture["metadataExact"] = false;
            Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
        }
    }
}
