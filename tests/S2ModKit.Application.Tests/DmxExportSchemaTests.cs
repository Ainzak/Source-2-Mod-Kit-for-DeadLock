using System.Text.Json.Nodes;
using NJsonSchema;

namespace S2ModKit.Application.Tests;

public sealed class DmxExportSchemaTests
{
    [Fact]
    public async Task OfflineDmxSchemaAcceptsFixtureAndRejectsVersionAndNestedAttributeChanges()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "S2ModKit.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root unavailable.");
        var schema = await JsonSchema.FromFileAsync(
            Path.Combine(root, "tools", "dmx", "schemas", "v1", "source.schema.json"),
            TestContext.Current.CancellationToken);
        var fixture = File.ReadAllText(Path.Combine(root, "tools", "dmx", "tests", "fixtures", "source.json"));
        Assert.Empty(schema.Validate(fixture));

        var document = JsonNode.Parse(fixture)!;
        document["operation"] = "export_mesh_unit@2";
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
        document["operation"] = "export_mesh_unit@1";
        var record = document["members"]![0]!["records"]![0]!;
        record["extra"] = true;
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
        record.AsObject().Remove("extra");
        record["frameWord"] = true;
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
        record["frameWord"] = 4294967296L;
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
        record["frameWord"] = 0;
        record["weights"]![0]!["byte"] = 0;
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
    }
}
