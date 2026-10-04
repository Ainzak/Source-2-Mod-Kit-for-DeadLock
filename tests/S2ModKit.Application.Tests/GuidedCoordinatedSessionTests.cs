using System.Text;
using System.Text.Json.Nodes;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed class GuidedCoordinatedSessionTests
{
    [Fact]
    public async Task CoordinatedReviewSessionBindsUnionFieldPoliciesAndPreviewUnderANewVersion()
    {
        var session = Session(); var json = JsonDefaults.Serialize(session);
        Assert.Equal(json, JsonDefaults.Serialize(Read(json)));
        var schema = await JsonSchema.FromFileAsync(SchemaPath("guided-session.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(schema.Validate(json));
        var legacy = await JsonSchema.FromFileAsync(SchemaPath("v3/guided-session.schema.json"), TestContext.Current.CancellationToken);
        Assert.NotEmpty(legacy.Validate(json));
        foreach (var invalid in new[] { session with { SelectedComponentIds = null }, session with { CoordinatedParameters = null }, session with { CoordinatedPreview = null },
            session with { PlanFingerprint = ContentHash.Compute("wrong"u8) }, session with { MaximumVertexDisplacement = 32 }, session with { SelectedOperationVersion = 7 },
            session with { CoordinatedParameters = session.CoordinatedParameters! with { ZeroBoneBoxPolicy = new("reject", 1) } } })
            Assert.Throws<S2ModKitException>(() => Read(JsonDefaults.Serialize(invalid)));
        foreach (var name in new[] { "coordinatedParameters", "coordinatedPreview", "selectedComponentIds" })
        {
            var altered = JsonNode.Parse(json)!; altered.AsObject().Remove(name);
            Assert.NotEmpty(schema.Validate(altered.ToJsonString()));
        }
        foreach (var name in new[] { "coordinatedParameters", "coordinatedPreview", "selectedComponentIds" })
        {
            var old = JsonNode.Parse(JsonDefaults.Serialize(GuidedWorkflow.CreateSession("catalogue.json", session.Sources, false, true)))!;
            old[name] = null;
            Assert.Throws<S2ModKitException>(() => Read(old.ToJsonString()));
            Assert.NotEmpty(schema.Validate(old.ToJsonString()));
        }
    }

    [Fact]
    public async Task CoordinatedPreviewSummaryUsesAClosedSchemaWithoutGeometryPayloads()
    {
        var (plan, geometry, model) = CoordinatedSelectionPreviewTests.Fixture();
        var preview = CoordinatedSelectionPreviewBuilder.Create(plan, geometry, model);
        var artifacts = S2ModKit.Reporting.CoordinatedSelectionPreviewRenderer.Render(preview);
        var schema = await JsonSchema.FromFileAsync(SchemaPath("coordinated-selection-preview.schema.json"), TestContext.Current.CancellationToken);
        var json = Encoding.UTF8.GetString(artifacts.SummaryJson.Span); Assert.Empty(schema.Validate(json));
        foreach (var property in new[] { "transform", "policy", "zeroBoneBoxPolicy", "zeroRenderSpherePolicy", "lods", "contactSheetHash", "limitations" })
        {
            var invalid = JsonNode.Parse(json)!; invalid.AsObject().Remove(property);
            Assert.NotEmpty(schema.Validate(invalid.ToJsonString()));
        }
        var unknown = JsonNode.Parse(json)!; unknown["anatomyQualified"] = true; Assert.NotEmpty(schema.Validate(unknown.ToJsonString()));
    }

    private static GuidedWorkflowSession Session()
    {
        var op = (TransformComponentOperation)ExperimentalVisualContractTests.CoordinatedRecipe().Operations[0];
        string[] ids = ["cmp_000000000000000000000000", "cmp_111111111111111111111111"]; var hash = ContentHash.Compute("plan"u8);
        return GuidedWorkflow.CreateSession("catalogue.json", [new("source", "compiled_model", "Model", "model.vmdl_c")], false, true) with
        {
            SchemaVersion = 4,
            Step = "output_selection",
            SelectedSourceId = "source",
            SelectedHeroId = "test",
            SelectedResourceId = "test.primary",
            SelectedLogicalPath = "models/test.vmdl_c",
            ProjectRoot = "memory",
            ProjectReady = true,
            SelectedComponentId = GuidedWorkflow.CoordinatedChoiceId(ids),
            SelectedComponentLabel = "Ordinary buffers",
            SelectedComponentIds = ids,
            SelectedOperationKind = "transform_component",
            SelectedOperationVersion = 8,
            SelectedIntent = "coordinated-field",
            CoordinatedParameters = new(op.RuntimeMetadataPolicy!, op.ZeroBoneBoxPolicy!, op.ZeroRenderSpherePolicy!, op.CoordinatedTransform!.Field, 64),
            MaximumVertexDisplacement = 64,
            RecipePath = "recipe.json",
            RecipeContentHash = ContentHash.Compute("recipe"u8),
            PlanFingerprint = hash,
            PlannedDrawCallCount = 2,
            PlannedLodCount = 1,
            PlannedVertexCount = 6,
            PlannedTargetBlockCount = 3,
            PlannedCoupledCollision = false,
            CoordinatedPreview = new(ContentHash.Compute("preview"u8), hash, ContentHash.Compute("summary"u8), ContentHash.Compute("sheet"u8), "summary.json", "sheet.svg"),
        };
    }
    private static GuidedWorkflowSession Read(string json) => JsonDefaults.Deserialize<GuidedWorkflowSession>(Encoding.UTF8.GetBytes(json), "Session");
    private static string SchemaPath(string name)
    { var root = new DirectoryInfo(AppContext.BaseDirectory); while (!File.Exists(Path.Combine(root!.FullName, "S2ModKit.slnx"))) root = root.Parent; return Path.Combine(root.FullName, "schemas", name); }
}
