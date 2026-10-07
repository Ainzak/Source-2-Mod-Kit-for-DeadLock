using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using S2ModKit.Infrastructure;
using S2ModKit.Reporting;

namespace S2ModKit.Application.Tests;

public sealed class PairedAuthoringTests
{
    [Fact]
    public void ScaffoldBindsCurrentMembersAndProducesCanonicalDeterministicIntent()
    {
        var options = Options(); var source = new DirectionalAuthoringSource(options.InputHash, options.PairedTransform.Members, [], [], []);
        var recipe = PairedAuthoring.CreateRecipe(source, options);
        Assert.Equal(JsonDefaults.Serialize(recipe), JsonDefaults.Serialize(PairedAuthoring.CreateRecipe(source, options)));
        PairedContractValidator.ValidateRecipe(recipe);
        Assert.Equal(11, recipe.SchemaVersion); Assert.StartsWith("scaffold-", recipe.RecipeId, StringComparison.Ordinal);
        Assert.Equal(options.PairedTransform.Members, ((TransformComponentOperation)recipe.Operations[0]).PairedTransform!.Members);
    }
    [Theory]
    [InlineData("source_hash")]
    [InlineData("member_id")]
    [InlineData("draw_map")]
    public void StaleSourceCannotBecomeARecipe(string defect)
    {
        var o = Options(); var m = o.PairedTransform.Members[0];
        var source = new DirectionalAuthoringSource(o.InputHash, o.PairedTransform.Members, [], [], []);
        source = defect switch
        {
            "source_hash" => source with { InputHash = ContentHash.Compute("stale"u8) },
            "member_id" => source with { Members = [m with { MemberId = "stale" }] },
            _ => source with { Members = [m with { Lods = [m.Lods[0] with { DrawCallIds = ["stale"] }] }] }
        };
        Assert.Equal("PAIRED_AUTHORING_SOURCE_DRIFT", Assert.Throws<S2ModKitException>(() => PairedAuthoring.CreateRecipe(source, o)).Error.Code);
    }
    [Fact]
    public async Task ComparisonIsClosedDeterministicAndUsesMatchingCamerasForBothPartners()
    {
        var preview = Fixture(); var artifacts = PairedSelectionPreviewRenderer.Render(preview);
        Assert.Equal(artifacts.ContactSheetHash, PairedSelectionPreviewRenderer.Render(preview).ContactSheetHash);
        var summary = PairedPreviewSummaryJson.Read(artifacts.SummaryJson.Span);
        Assert.Equal(2, summary.Regions.Count); Assert.All(summary.Regions, r => Assert.Null(r.XChangePercent));
        var json = Encoding.UTF8.GetString(artifacts.SummaryJson.Span);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var schema = await JsonSchema.FromFileAsync(Path.Combine(directory.FullName, "schemas", "paired-selection-preview.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(schema.Validate(json)); Assert.DoesNotContain("\"points\"", json, StringComparison.Ordinal);
        var doc = XDocument.Parse(Encoding.UTF8.GetString(artifacts.ContactSheetSvg.Span));
        var cameras = doc.Descendants().Where(e => e.Attribute("data-camera") is not null).ToArray();
        Assert.Equal(24, cameras.Length);
        foreach (var group in cameras.GroupBy(e => (string)e.Attribute("data-camera")!))
            Assert.Single(group.Select(e => ((string)e.Attribute("transform")!).Split("scale(", StringSplitOptions.None)[1]).Distinct());
        Assert.Contains("PARTNER left", doc.Root!.Value, StringComparison.Ordinal); Assert.Contains("PARTNER right", doc.Root.Value, StringComparison.Ordinal);
        Assert.Contains("procedural", doc.Root.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName is "script" or "foreignObject");
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("live_claim")]
    [InlineData("missing_partner")]
    [InlineData("changed_percent")]
    [InlineData("negative_span")]
    [InlineData("negative_reach")]
    [InlineData("fixed_count")]
    [InlineData("missing_nullable")]
    [InlineData("duplicate")]
    public void ClosedSummaryRejectsForgedOrIncompleteObservations(string defect)
    {
        var a = PairedSelectionPreviewRenderer.Render(Fixture()); var json = JsonNode.Parse(a.SummaryJson.Span)!;
        switch (defect)
        {
            case "unknown": json["claimedLiveFit"] = true; break;
            case "live_claim": json["proofLevel"] = "runtime_qualified"; break;
            case "missing_partner": ((JsonArray)json["regions"]!).RemoveAt(1); break;
            case "changed_percent": json["regions"]![0]!["xChangePercent"] = 100; break;
            case "negative_span": json["regions"]![0]!["spanBefore"]!["x"] = -1; break;
            case "negative_reach": json["regions"]![0]!["positiveReachBefore"]!["x"] = -1; break;
            case "fixed_count": json["regions"]![0]!["protectedRecords"] = 2; break;
            case "missing_nullable": ((JsonObject)json["regions"]![0]!).Remove("xChangePercent"); break;
        }
        var bytes = Encoding.UTF8.GetBytes(defect == "duplicate" ? json.ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal) : json.ToJsonString());
        Assert.Throws<S2ModKitException>(() => PairedPreviewSummaryJson.Read(bytes));
    }
    [Fact]
    public void GeometryDriftCannotBeRenderedWithAnOldPreviewFingerprint()
    {
        var preview = Fixture();
        Assert.Throws<S2ModKitException>(() => PairedSelectionPreviewRenderer.Render(preview with { Regions = [] }));
    }
    [Fact]
    public async Task PublicationIsIdempotentAndNeverOverwritesUserOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2modkit-paired-preview-" + Guid.NewGuid().ToString("N")); var token = TestContext.Current.CancellationToken;
        try
        {
            var artifacts = PairedSelectionPreviewRenderer.Render(Fixture());
            var result = await FileSystemEllipsoidPreviewPublisher.PublishPairedAsync(root, artifacts, token);
            Assert.Equal(result, await FileSystemEllipsoidPreviewPublisher.PublishPairedAsync(root, artifacts, token));
            await File.WriteAllTextAsync(result.ContactSheetPath, "owner", token);
            await Assert.ThrowsAsync<S2ModKitException>(() => FileSystemEllipsoidPreviewPublisher.PublishPairedAsync(root, artifacts, token));
            Assert.Equal("owner", await File.ReadAllTextAsync(result.ContactSheetPath, token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static PairedFieldOptions Options()
    {
        var plan = ExperimentalVisualContractTests.PairedPlan(); var op = PairedContractValidator.Operation(plan.Operations[0]);
        return new(1, "paired_directional_field_options", 1, plan.InputHash, op.RuntimeMetadataPolicy!, op.ZeroBoneBoxPolicy!, op.ZeroRenderSpherePolicy!, op.SourceTrianglePolicy!, op.ProceduralInputPolicy!, op.PairedTransform!, op.Limits.MaximumVertexDisplacement);
    }
    // Synthetic renderer observations, not an admitted model/resource or a verification oracle.
    private static PairedSelectionPreview Fixture()
    {
        var plan = ExperimentalVisualContractTests.PairedPlan(); var t = plan.Operations[0].PairedTransformTarget!;
        Point3[] points = [new(-1, -16, 0), new(1, 16, 0), new(30, 0, 0)];
        var parameters = t.PairedTransform.Fields.Select(f => new DirectionalFieldParameters(new(f.Field.Pivot.Point!.X, f.Field.Pivot.Point.Y, f.Field.Pivot.Point.Z), new(f.Field.OuterRadii.X, f.Field.OuterRadii.Y, f.Field.OuterRadii.Z), f.Field.CoreFraction, new(f.Field.Scale.X, f.Field.Scale.Y, f.Field.Scale.Z), 64)).ToArray();
        var math = new PairedDirectionalFieldReconstruction(parameters[0], parameters[1]);
        var buffers = t.ContextBuffers.Select(c => new PairedPreviewBuffer(c, c.Selected ? t.Buffers[0].MemberId : null, [c.Selected ? "selected" : "context"],
            points.Select(p => new EllipsoidPreviewPoint(p, c.Selected ? math.ReconstructPosition(p).Position : p, "core", 1)).ToArray(), [0, 1, 2], c.Selected ? [2] : [], c.Selected ? [2] : [], c.Selected ? [0, 1, -1] : [-1, -1, -1])).ToArray();
        var regions = t.PairedTransform.Fields.Select((f, i) => new PairedRegionMeasurement(0, f.FieldId, 1, 1, new(), new(), null, null, null, .5f, 1, 1)).ToArray();
        var preview = new PairedSelectionPreview(plan.InputHash, plan.Fingerprint, t.TargetFingerprint, plan.InputHash, t.PairedTransform, buffers, regions);
        return preview with { PreviewFingerprint = PairedSelectionPreviewBuilder.ComputeFingerprint(preview) };
    }
}
