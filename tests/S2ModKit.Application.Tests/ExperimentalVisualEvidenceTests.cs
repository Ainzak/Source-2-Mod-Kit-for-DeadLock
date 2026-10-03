using System.Text;
using System.Text.Json.Nodes;
using NJsonSchema;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class ExperimentalVisualContractTests
{
    [Fact]
    public async Task PlannedAndObservedEvidenceUseSeparateStrictSchemaBranches()
    {
        var schema = await JsonSchema.FromFileAsync(SchemaPath("evidence.schema.json"), TestContext.Current.CancellationToken);
        var legacy = await JsonSchema.FromFileAsync(SchemaPath("v6/evidence.schema.json"), TestContext.Current.CancellationToken);
        foreach (var report in new[] { ExperimentalReport(false), ExperimentalReport(true) })
        {
            ExperimentalEvidenceValidator.Validate(report);
            var json = JsonDefaults.Serialize(report);
            Assert.Empty(schema.Validate(json));
            Assert.NotEmpty(legacy.Validate(json));
            Assert.Equal(json, JsonDefaults.Serialize(ReadEvidence(json)));
        }
    }

    [Fact]
    public void MissingDuplicateFailedAndPromotedRiskBoundariesReject()
    {
        var report = ExperimentalReport(true);
        foreach (var boundary in report.Boundaries)
        {
            Assert.Throws<S2ModKitException>(() => ExperimentalEvidenceValidator.Validate(report with
            { Boundaries = report.Boundaries.Where(item => item != boundary).ToArray() }));
            Assert.Throws<S2ModKitException>(() => ExperimentalEvidenceValidator.Validate(report with
            { Boundaries = report.Boundaries.Append(boundary).ToArray() }));
            Assert.Throws<S2ModKitException>(() => ExperimentalEvidenceValidator.Validate(report with
            { Boundaries = report.Boundaries.Select(item => item == boundary ? item with { Status = boundary.Status == "passed" ? "untested" : "passed" } : item).ToArray() }));
        }
        Assert.Throws<S2ModKitException>(() => ExperimentalEvidenceValidator.Validate(report with
        { Boundaries = report.Boundaries.Append(new("other_check", "failed", "failed")).ToArray() }));
    }

    [Fact]
    public void PassedEvidenceCannotInventOrOmitObservedValues()
    {
        var report = ExperimentalReport(true);
        var operation = report.Operations[0];
        var visual = operation.ExperimentalTransform!;
        foreach (var changed in new[]
        {
            visual with { Boxes = [visual.Boxes[0] with { ObservedWords = null }] },
            visual with { Boxes = [visual.Boxes[0] with { ObservedWords = new uint[6] }] },
            visual with { PreservedMetadata = [visual.PreservedMetadata[0] with { ObservedPayloadHash = null }] },
            visual with { PreservedMetadata = [visual.PreservedMetadata[0] with { ObservedWords = null }] },
            visual with { PreservedMetadata = [visual.PreservedMetadata[0] with { ObservedWords = [0] }] },
            visual with { Boxes = [] },
        })
            Assert.Throws<S2ModKitException>(() => ExperimentalEvidenceValidator.Validate(report with
            { Operations = [operation with { ExperimentalTransform = changed }] }));
    }

    [Fact]
    public async Task MissingObservedWirePropertiesFailReaderAndSchema()
    {
        var schema = await JsonSchema.FromFileAsync(SchemaPath("evidence.schema.json"), TestContext.Current.CancellationToken);
        foreach (var property in new[] { "observedWords", "observedPayloadHash", "status", "target" })
        {
            var node = JsonNode.Parse(JsonDefaults.Serialize(ExperimentalReport(true)))!;
            node["operations"]![0]!["experimentalTransform"]!["preservedMetadata"]![0]!.AsObject().Remove(property);
            Assert.Throws<S2ModKitException>(() => ReadEvidence(node.ToJsonString()));
            Assert.NotEmpty(schema.Validate(node.ToJsonString()));
        }
    }

    [Fact]
    public void DryRunCannotContainFabricatedObservations()
    {
        var report = ExperimentalReport(false);
        var operation = report.Operations[0];
        var visual = operation.ExperimentalTransform!;
        Assert.Throws<S2ModKitException>(() => ExperimentalEvidenceValidator.Validate(report with
        { Operations = [operation with { ExperimentalTransform = visual with { Boxes = [visual.Boxes[0] with { ObservedWords = visual.Boxes[0].Target.ExpectedWords }] } }] }));
    }

    [Fact]
    public void LegacyAndUnknownEvidenceVersionsCannotSmuggleExperimentalContracts()
    {
        foreach (var version in new[] { 1, 5, 6, 8, 0 })
            Assert.Throws<S2ModKitException>(() => ReadEvidence(JsonDefaults.Serialize(ExperimentalReport(true) with { SchemaVersion = version })));
    }

    [Fact]
    public void DuplicateAndMalformedEvidenceArraysRejectWithContractError()
    {
        var json = JsonDefaults.Serialize(ExperimentalReport(true));
        Assert.Throws<S2ModKitException>(() => ReadEvidence(json.Replace("\"schemaVersion\": 7", "\"schemaVersion\": 7, \"schemaVersion\": 7", StringComparison.Ordinal)));
        var node = JsonNode.Parse(json)!;
        node["operations"]![0]!["experimentalTransform"]!["boxes"] = "not an array";
        Assert.Throws<S2ModKitException>(() => ReadEvidence(node.ToJsonString()));
    }

    private static EvidenceReport ExperimentalReport(bool built)
    {
        var target = Plan().Operations[0].ExperimentalTransformTarget!;
        var box = target.BoxTargets[0] with { Growth = [new("volume", 8, 8, 0)] };
        var preserved = target.PreservationTargets[0];
        var geometry = target.GeometryTargets[0];
        var visual = new ExperimentalTransformEvidence(target.StructuralProfileId, 1, target.BoundsPolicyId, 1,
            target.RuntimeMetadataPolicy, target.Pivot, target.UniformScale, target.DisplacementLimit,
            [new(box, built ? box.ExpectedWords : null, built ? "passed" : "planned")],
            [new(preserved, built ? Hash : null, built ? preserved.OriginalWords : null, built ? "passed" : "planned")]);
        var boundaries = ExperimentalEvidenceValidator.PlannedBoundaries()
            .Select(boundary => built && boundary.Status == "not_applicable" ? boundary with { Status = "passed" } : boundary)
            .Append(new("runtime", "untested", "No player observation.")).ToArray();
        return new()
        {
            SchemaVersion = 7,
            ReportId = "synthetic-visual",
            Command = built ? "build" : "plan",
            Status = "passed",
            CreatedUtc = DateTimeOffset.UnixEpoch,
            Input = new("models/test.vmdl_c", Hash, 12),
            Output = built ? new("models/test.vmdl_c", Hash, 12) : null,
            PlanFingerprint = Plan().Fingerprint,
            Boundaries = boundaries,
            Warnings = ["Experimental visual-only edit; preserved metadata is unverified."],
            Operations = [new("scale", "transform_component", 5, ["dc_000000000000000000000000"], ["models/test.vmdl_c"])
            {
                ExperimentalTransform = visual,
                GeometryChanges = [new(0, geometry.ResourcePath, 0, 0, Hash, 3, geometry.BeforeBounds, geometry.ExpectedAfterBounds,
                    geometry.FrozenPivot, 1.5f, new(), 1, ["position"], Hash, built ? Hash : null, geometry.Codec)],
            }],
        };
    }

    private static EvidenceReport ReadEvidence(string json) => JsonDefaults.Deserialize<EvidenceReport>(Encoding.UTF8.GetBytes(json), "Evidence");
}
