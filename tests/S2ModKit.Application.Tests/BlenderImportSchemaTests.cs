using System.Text.Json.Nodes;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class ExperimentalVisualContractTests
{
    [Theory]
    [InlineData("intent")]
    [InlineData("observation")]
    public async Task ImportDesignContractsRejectUnknownMissingNullAndWrongVersionFacts(string name)
    {
        var schema = await ImportProposalSchema(name);
        var fixture = ImportProposalFixture(name);
        Assert.Empty(schema.Validate(fixture.ToJsonString()));
        fixture["unknown"] = true;
        Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
        fixture.Remove("unknown");
        var required = name == "intent" ? "framePolicy" : "consumerStatus";
        var value = fixture[required]!.DeepClone();
        fixture.Remove(required);
        Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
        fixture[required] = null;
        Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
        fixture[required] = value;
        if (name == "intent") fixture["framePolicy"]!["version"] = 2;
        else fixture["schemaVersion"] = 12;
        Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
    }

    [Fact]
    public async Task ImportBoundaryExceptionRequiresExactSourceBoundNonemptyRecordSets()
    {
        var schema = await ImportProposalSchema("intent");
        var fixture = ImportProposalFixture("intent");
        fixture["boundaryFramePolicy"] = new JsonObject
        {
            ["kind"] = "keep_positions_allow_source_derived_boundary_frames",
            ["version"] = 1,
            ["allowedBoundary"] = fixture["protection"]!.DeepClone()
        };
        Assert.Empty(schema.Validate(fixture.ToJsonString()));
        fixture["boundaryFramePolicy"]!["allowedBoundary"]![0]!["vertexIds"] = new JsonArray();
        Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
        fixture["boundaryFramePolicy"]!["allowedBoundary"]![0]!["vertexIds"] = new JsonArray(0, 0);
        Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
        fixture["boundaryFramePolicy"]!["allowedBoundary"]![0]!["vertexIds"] = new JsonArray(0);
        fixture["boundaryFramePolicy"]!["allowedBoundary"]![0]!["sourceDecodedHash"] = "stale";
        Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
    }

    [Fact]
    public async Task ImportRecipeRejectsForgedMemberBindingAndMixedLegacyFields()
    {
        var schema = await ImportProposalSchema("recipe");
        var document = JsonNode.Parse(JsonDefaults.Serialize(PairedRecipe()))!.AsObject();
        document["schemaVersion"] = 12;
        var operation = document["operations"]![0]!.AsObject();
        operation.Remove("pairedTransform");
        operation["kind"] = "import_positions";
        operation["version"] = 1;
        operation["granularity"] = "returned_buffer_positions";
        var intent = ImportProposalFixture("intent");
        intent["members"] = JsonNode.Parse(JsonDefaults.Serialize(PairedRecipe()))!["operations"]![0]!["pairedTransform"]!["members"]!.DeepClone();
        operation["positionImport"] = intent;
        Assert.Empty(schema.Validate(document.ToJsonString()));
        Assert.Throws<S2ModKitException>(() => JsonDefaults.Deserialize<RecipeDocument>(System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()), "Proposed import"));
        operation["pairedTransform"] = null;
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
        operation.Remove("pairedTransform");
        document["schemaVersion"] = 11;
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
    }

    [Fact]
    public async Task ImportObservationCannotClaimConsumerOrRuntimeQualification()
    {
        var schema = await ImportProposalSchema("observation");
        var fixture = ImportProposalFixture("observation");
        foreach (var property in new[] { "consumerStatus", "poseStatus", "runtimeStatus" })
        {
            fixture[property] = "passed";
            Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
            fixture[property] = "untested";
        }
        fixture["buffers"]![0]!["status"] = "untested";
        Assert.NotEmpty(schema.Validate(fixture.ToJsonString()));
    }

    [Fact]
    public void ExistingPairedRecipeKeepsItsCanonicalBytesAndRejectsImportIntent()
    {
        var bytes = JsonDefaults.SerializeToUtf8(PairedRecipe());
        var read = JsonDefaults.Deserialize<RecipeDocument>(bytes, "Legacy control");
        Assert.Equal(bytes, JsonDefaults.SerializeToUtf8(read));
        var document = JsonNode.Parse(bytes)!;
        document["operations"]![0]!["positionImport"] = ImportProposalFixture("intent");
        Assert.Throws<S2ModKitException>(() => JsonDefaults.Deserialize<RecipeDocument>(System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()), "Mixed control"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportPlanAndEvidenceShapesRemainClosedAndUnsupportedByLegacyReaders(bool evidence)
    {
        var document = JsonNode.Parse(evidence ? JsonDefaults.Serialize(PairedReport(true)) : JsonDefaults.Serialize(PairedPlan()))!.AsObject();
        document["schemaVersion"] = evidence ? 13 : 8;
        var operation = document["operations"]![0]!.AsObject();
        operation["kind"] = "import_positions";
        operation["version"] = 1;
        var old = evidence ? operation["pairedTransform"]!["target"]!.AsObject() : operation["pairedTransformTarget"]!.AsObject();
        var targetSchema = await JsonSchema.FromFileAsync(Path.Combine(ImportProposalRoot(), "schemas", "position-import-contracts.schema.json"), TestContext.Current.CancellationToken);
        var target = new JsonObject();
        foreach (var name in targetSchema.Definitions["target"].Properties.Keys)
            if (old[name] is { } value) target[name] = value.DeepClone();
        target["structuralProfileId"] = "root_returned_positions_preservation_visual";
        target["boundsPolicyId"] = "retain_expand_boxes_preserve_runtime_import";
        target["positionImport"] = ImportProposalFixture("intent");
        foreach (var property in new[] { "contextInventoryHash", "sourceBlocksHash", "sourceCoincidenceHash" }) target[property] = new string('a', 64);
        var buffer = new JsonObject { ["memberId"] = "body", ["lod"] = 0, ["meshOrdinal"] = 0, ["bufferOrdinal"] = 0, ["vertexCount"] = 3, ["positionChangeCount"] = 1, ["frameClosureIds"] = new JsonArray(), ["frameWords"] = new JsonArray(), ["boundaryChangedFrameIds"] = new JsonArray() };
        foreach (var property in new[] { "sourceDecodedHash", "sourceEncodedHash", "sourceIndexHash", "layoutHash", "expectedPositionHash", "expectedFrameHash", "expectedDecodedHash", "protectedPositionHash", "protectedFrameHash" }) buffer[property] = new string('a', 64);
        target["buffers"] = new JsonArray(buffer);
        string schemaPath;
        if (evidence)
        {
            var visual = operation["pairedTransform"]!.DeepClone().AsObject();
            visual["target"] = target;
            visual["observed"] = ImportProposalFixture("observation");
            operation.Remove("pairedTransform");
            operation["positionImport"] = visual;
            schemaPath = Path.Combine("v13", "evidence.schema.json");
        }
        else
        {
            operation.Remove("pairedTransformTarget");
            operation["positionImportTarget"] = target;
            schemaPath = Path.Combine("v8", "mutation-plan.schema.json");
        }
        var schema = await JsonSchema.FromFileAsync(Path.Combine(ImportProposalRoot(), "schemas", schemaPath), TestContext.Current.CancellationToken);
        Assert.Empty(schema.Validate(document.ToJsonString()));
        var bytes = System.Text.Encoding.UTF8.GetBytes(document.ToJsonString());
        if (evidence) Assert.Throws<S2ModKitException>(() => JsonDefaults.Deserialize<EvidenceReport>(bytes, "Reserved import evidence"));
        else Assert.Throws<S2ModKitException>(() => JsonDefaults.Deserialize<MutationPlan>(bytes, "Reserved import plan"));
        target["unknown"] = true;
        Assert.NotEmpty(schema.Validate(document.ToJsonString()));
    }

    private static string ImportProposalRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "S2ModKit.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root unavailable.");
    }

    private static Task<JsonSchema> ImportProposalSchema(string name) => JsonSchema.FromFileAsync(
        Path.Combine(ImportProposalRoot(), "schemas", name == "recipe" ? Path.Combine("v12", "recipe.schema.json") : "position-import-" + name + ".schema.json"), TestContext.Current.CancellationToken);

    private static JsonObject ImportProposalFixture(string name) => JsonNode.Parse(
        File.ReadAllText(Path.Combine(ImportProposalRoot(), "tools", "blender", "tests", "fixtures", "import-" + name + ".json")))!.AsObject();
}
