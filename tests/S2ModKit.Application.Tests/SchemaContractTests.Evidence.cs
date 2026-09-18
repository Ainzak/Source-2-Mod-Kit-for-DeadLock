using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class SchemaContractTests
{
    [Fact]
    public async Task SerializedEvidenceReportConformsToPublishedSchema()
    {
        var inputHash = ContentHash.Compute("input"u8);
        var blockHash = ContentHash.Compute("block"u8);
        var report = new EvidenceReport
        {
            ReportId = "evidence-report",
            CreatedUtc = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero),
            Command = "plan",
            Status = "passed",
            Input = new ArtifactEvidence("models/heroes/test/test.vmdl_c", inputHash, 5),
            PlanFingerprint = ContentHash.Compute("plan"u8),
            Operations =
            [
                new OperationEvidence(
                    "remove-accessory",
                    "remove_component",
                    1,
                    ["dc_0123456789abcdef01234567"],
                    ["models/accessory.vmesh_c"]),
            ],
            Boundaries = [new BoundaryEvidence("runtime", "untested", "No runtime validation was performed.")],
            Blocks = [new ResourceBlockEvidence(0, "DATA", blockHash, null, "planned_unchanged")],
            ToolVersions = new Dictionary<string, string>(StringComparer.Ordinal) { ["s2modkit"] = "1" },
            Warnings = [],
        };
        var schema = await JsonSchema.FromFileAsync(GetSchemaPath("evidence.schema.json"), TestContext.Current.CancellationToken);

        var errors = schema.Validate(JsonDefaults.Serialize(report));

        Assert.Empty(errors);
    }

    [Fact]
    public async Task SerializedTransformEvidenceConformsToVersionThreeSchema()
    {
        var inputHash = ContentHash.Compute("input"u8);
        var vertexBlockHash = ContentHash.Compute("vertex-block"u8);
        var outputBlockHash = ContentHash.Compute("output-block"u8);
        var codecHash = ContentHash.Compute("codec"u8);
        var bounds = new GeometryBounds(
            new TransformVector3 { X = -1f, Y = -2f, Z = -3f },
            new TransformVector3 { X = 1f, Y = 2f, Z = 3f });
        var operation = new OperationEvidence(
            "scale-accessory",
            "transform_component",
            1,
            ["dc_0123456789abcdef01234567"],
            ["models/accessory.vmesh_c"])
        {
            GeometryChanges =
            [
                new GeometryChangeEvidence(
                    0,
                    "models/accessory.vmesh_c",
                    0,
                    2,
                    ContentHash.Compute("vertices"u8),
                    12,
                    bounds,
                    bounds,
                    new TransformVector3(),
                    1.2f,
                    new TransformVector3(),
                    1f,
                    ["position"],
                    vertexBlockHash,
                    outputBlockHash,
                    new GeometryCodecIdentity("meshoptimizer", "s2-v1", "win-x64", codecHash, "configured"))
                {
                    InputDecodedVertexBufferHash = ContentHash.Compute("decoded-before"u8),
                    ExpectedDecodedVertexBufferHash = ContentHash.Compute("decoded-after"u8),
                },
            ],
        };
        var report = new EvidenceReport
        {
            SchemaVersion = 3,
            ReportId = "transform-evidence",
            CreatedUtc = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero),
            Command = "build",
            Status = "passed",
            Input = new ArtifactEvidence("models/hero.vmdl_c", inputHash, 5),
            Output = new ArtifactEvidence("models/hero.vmdl_c", outputBlockHash, 5),
            PlanFingerprint = ContentHash.Compute("plan"u8),
            Operations = [operation],
        };
        var schema = await JsonSchema.FromFileAsync(GetSchemaPath("v3/evidence.schema.json"), TestContext.Current.CancellationToken);

        var json = JsonDefaults.Serialize(report);
        var errors = schema.Validate(json);
        var operationJson = JsonDefaults.Serialize(operation);
        var operationErrors = schema.Definitions["transformOperationEvidence"].Validate(operationJson);
        var removeOperationErrors = schema.Definitions["removeOperationEvidence"].Validate(operationJson);

        Assert.Equal(3, report.SchemaVersion);
        Assert.True(operationErrors.Count == 0, $"{string.Join(Environment.NewLine, operationErrors)}{Environment.NewLine}{operationJson}");
        Assert.True(removeOperationErrors.Count > 0, $"Transform evidence also matched remove evidence:{Environment.NewLine}{operationJson}");
        Assert.True(errors.Count == 0, $"{string.Join(Environment.NewLine, errors)}{Environment.NewLine}{json}");

        var planOperation = operation with
        {
            GeometryChanges = operation.GeometryChanges.Select(change => change with { OutputVertexBlockHash = null }).ToArray(),
        };
        var planReport = report with
        {
            Command = "plan",
            Output = null,
            Operations = [planOperation],
        };
        var planJson = JsonDefaults.Serialize(planReport);
        Assert.Contains("\"outputVertexBlockHash\": null", planJson, StringComparison.Ordinal);
        Assert.Empty(schema.Validate(planJson));
    }

    [Fact]
    public async Task EvidenceReaderRetainsVersionTwoCompatibility()
    {
        var hash = ContentHash.Compute("legacy-evidence"u8);
        var report = new EvidenceReport
        {
            SchemaVersion = 2,
            ReportId = "legacy-evidence",
            CreatedUtc = DateTimeOffset.UnixEpoch,
            Command = "plan",
            Status = "passed",
            Input = new ArtifactEvidence("models/hero.vmdl_c", hash, 1),
            PlanFingerprint = hash,
            Operations = [new OperationEvidence("remove", "remove_component", 1, [], [])],
        };
        var document = System.Text.Json.Nodes.JsonNode.Parse(JsonDefaults.Serialize(report))!.AsObject();
        foreach (var operation in document["operations"]!.AsArray())
        {
            operation!.AsObject().Remove("geometryChanges");
        }

        var legacyJson = document.ToJsonString(JsonDefaults.Options);
        var schema = await JsonSchema.FromFileAsync(GetSchemaPath("v2/evidence.schema.json"), TestContext.Current.CancellationToken);

        Assert.Empty(schema.Validate(legacyJson));
        var loaded = JsonDefaults.Deserialize<EvidenceReport>(System.Text.Encoding.UTF8.GetBytes(legacyJson), "Evidence");
        Assert.Equal(2, loaded.SchemaVersion);
        Assert.Empty(loaded.Operations.Single().GeometryChanges);
    }

    [Fact]
    public async Task CoupledTransformEvidenceConformsToVersionFiveSchema()
    {
        var before = new GeometryBounds(new TransformVector3(), new TransformVector3 { X = 1, Y = 1, Z = 1 });
        var after = new GeometryBounds(new TransformVector3 { X = -0.5f, Y = -0.5f, Z = -0.5f }, new TransformVector3 { X = 1.5f, Y = 1.5f, Z = 1.5f });
        var operation = new OperationEvidence(
            "scale-coupled", "transform_component", 2,
            ["dc_0123456789abcdef01234567"], ["models/accessory.vmdl_c"])
        {
            CoupledTransform = new CoupledTransformEvidence(
                "transform_coupled_convex",
                new TransformVector3 { X = 0.5f, Y = 0.5f, Z = 0.5f },
                2f,
                new CoupledTransformHalfEvidence(2, 24, ContentHash.Compute("visual-before"u8), ContentHash.Compute("visual-after"u8), before, after, 1f, 96f),
                new CoupledTransformHalfEvidence(3, 8, ContentHash.Compute("collision-before"u8), ContentHash.Compute("collision-after"u8), before, after, 1f, 96f),
                ["collision:positions", "visual:positions"]),
        };
        var report = new EvidenceReport
        {
            ReportId = "coupled-evidence",
            CreatedUtc = DateTimeOffset.UnixEpoch,
            Command = "build",
            Status = "passed",
            Input = new ArtifactEvidence("models/accessory.vmdl_c", ContentHash.Compute("input"u8), 5),
            PlanFingerprint = ContentHash.Compute("plan"u8),
            Operations = [operation],
        };
        var schema = await JsonSchema.FromFileAsync(GetSchemaPath("evidence.schema.json"), TestContext.Current.CancellationToken);

        Assert.Empty(schema.Validate(JsonDefaults.Serialize(report)));
        Assert.Empty(schema.Definitions["coupledTransformOperationEvidence"].Validate(JsonDefaults.Serialize(operation)));
    }

}
