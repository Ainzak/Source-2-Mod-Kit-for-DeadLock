using System.Text;
using System.Text.Json.Nodes;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class SchemaContractTests
{
    [Fact]
    public async Task SourceAuthoringSchemasClosePoliciesAndDoNotClaimCompiledEvidence()
    {
        var f = PositionReturnValidatorTests.Fixture();
        var selection = await JsonSchema.FromFileAsync(GetSchemaPath("position-authoring-selection.schema.json"), TestContext.Current.CancellationToken);
        var json = JsonNode.Parse(PositionAuthoringJson.Canonical(f.Selection))!;
        Assert.Empty(selection.Validate(json.ToJsonString()));
        json["zeroBoneBoxPolicy"]!["kind"] = "preserve"; Assert.NotEmpty(selection.Validate(json.ToJsonString()));
        json = JsonNode.Parse(PositionAuthoringJson.Canonical(f.Selection))!; json["unknown"] = true; Assert.NotEmpty(selection.Validate(json.ToJsonString()));
        var validation = await JsonSchema.FromFileAsync(GetSchemaPath("position-authoring-validation.schema.json"), TestContext.Current.CancellationToken);
        var result = PositionReturnValidator.Validate(f.Source, f.Selection, f.Bytes, ContentHash.Compute(f.Bytes), PositionAuthoringJson.Canonical(f.Delta), _ => 42);
        json = JsonNode.Parse(PositionAuthoringJson.Canonical(result))!; Assert.Empty(validation.Validate(json.ToJsonString()));
        json["modelBytesBuilt"] = true; Assert.NotEmpty(validation.Validate(json.ToJsonString()));
    }
}
