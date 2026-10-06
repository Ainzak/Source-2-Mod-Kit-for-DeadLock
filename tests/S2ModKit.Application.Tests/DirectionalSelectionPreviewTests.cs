using System.Buffers.Binary;
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

public sealed class DirectionalSelectionPreviewTests
{
    [Fact]
    public async Task SurfaceSummaryHasAClosedVersionedSchemaWithoutSourceGeometry()
    {
        var (plan, geometry) = Fixture(new(1.5f, 1.5f, 1));
        var artifacts = DirectionalSelectionPreviewRenderer.Render(DirectionalSelectionPreviewBuilder.Create(plan, geometry));
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var schema = await JsonSchema.FromFileAsync(Path.Combine(directory.FullName, "schemas", "directional-selection-preview.schema.json"), TestContext.Current.CancellationToken);
        var json = Encoding.UTF8.GetString(artifacts.SummaryJson.Span);
        Assert.Empty(schema.Validate(json));
        Assert.DoesNotContain("\"points\"", json, StringComparison.Ordinal);
        var forged = JsonNode.Parse(json)!; forged["lods"]![0]!["claimedLiveFit"] = true;
        Assert.NotEmpty(schema.Validate(forged.ToJsonString()));
        forged = JsonNode.Parse(json)!; forged["proofLevel"] = "runtime_qualified";
        Assert.NotEmpty(schema.Validate(forged.ToJsonString()));
    }

    [Fact]
    public void RendererRejectsContentDriftBeforeProducingAnArtifact()
    {
        var (plan, geometry) = Fixture(new(1.5f, 1.5f, 1));
        var preview = DirectionalSelectionPreviewBuilder.Create(plan, geometry);
        Assert.Throws<S2ModKitException>(() => DirectionalSelectionPreviewRenderer.Render(preview with { Measurements = [] }));
    }
    [Theory]
    [InlineData(1.5f, 1.5f, 1f)]
    [InlineData(1f, 1f, 1.5f)]
    [InlineData(1.0001f, 1.0001f, 1f)]
    public void BulkLengthAndNegligibleEffectsUseExactMeasurementsAndMatchingCameras(float x, float y, float z)
    {
        var (plan, geometry) = Fixture(new(x, y, z));
        var preview = DirectionalSelectionPreviewBuilder.Create(plan, geometry);
        foreach (var m in preview.Measurements)
        {
            Assert.Equal(6, m.SelectedCount); Assert.Equal(3, m.ExcludedCount); Assert.Equal(2, m.PinnedCount); Assert.Equal(2, m.ProtectedCount);
            Assert.Equal(m.MaximumDisplacement / m.ModelDiagonal, m.MaximumDisplacementOverModelDiagonal);
            if (m.ChangedRegionSpanBefore.X > 0) Assert.Equal(x, m.ChangedRegionSpanAfter.X / m.ChangedRegionSpanBefore.X, .00001);
            if (m.ChangedRegionSpanBefore.Z > 0) Assert.Equal(z, m.ChangedRegionSpanAfter.Z / m.ChangedRegionSpanBefore.Z, .00001);
            if (x < 1.001 && z == 1) Assert.InRange(m.MaximumDisplacementOverModelDiagonal, 0, .00002);
            else Assert.True(m.MaximumDisplacementOverModelDiagonal > .02);
        }
        var artifacts = DirectionalSelectionPreviewRenderer.Render(preview);
        Assert.Equal(artifacts.ContactSheetHash, DirectionalSelectionPreviewRenderer.Render(preview).ContactSheetHash);
        var doc = XDocument.Parse(Encoding.UTF8.GetString(artifacts.ContactSheetSvg.Span));
        var comparisons = doc.Descendants().Where(e => e.Attribute("data-camera") is not null).ToArray();
        Assert.Equal(24, comparisons.Length);
        foreach (var camera in comparisons.GroupBy(e => (string)e.Attribute("data-camera")!))
            Assert.Single(camera.Select(e => ((string)e.Attribute("transform")!).Split("scale(", StringSplitOptions.None)[1]).Distinct());
        Assert.Equal(12, doc.Descendants().Count(e => (string?)e.Attribute("data-role") == "complete-surfaces"));
        Assert.Equal(12, doc.Descendants().Count(e => (string?)e.Attribute("data-role") == "protected-records"));
        Assert.Contains("FULL OUTLINE", doc.Root!.Value, StringComparison.Ordinal);
        Assert.Contains("FOCUS CROP", doc.Root.Value, StringComparison.Ordinal);
        Assert.Contains("BIND-SPACE PREDICTION", doc.Root.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName is "script" or "foreignObject");
    }

    [Theory]
    [InlineData("source")]
    [InlineData("plan")]
    [InlineData("missing_lod")]
    [InlineData("missing_excluded")]
    [InlineData("duplicate")]
    [InlineData("source_position")]
    [InlineData("out_of_range")]
    [InlineData("missing_triangle")]
    [InlineData("degenerate")]
    [InlineData("member")]
    [InlineData("drawcalls")]
    public void IncompleteOrForgedGeometryFailsBeforePublication(string failure)
    {
        var (plan, geometry) = Fixture(new(1.5f, 1.5f, 1)); var b = geometry.Buffers[0];
        geometry = failure switch
        {
            "source" => geometry with { InputHash = ContentHash.Compute("bad"u8) },
            "plan" => geometry with { PlanFingerprint = ContentHash.Compute("bad"u8) },
            "missing_lod" => geometry with { Buffers = geometry.Buffers.Where(b => b.Source.Lod == 0).ToArray() },
            "missing_excluded" => geometry with { Buffers = geometry.Buffers.Where(b => b.Source.Selected).ToArray() },
            "duplicate" => geometry with { Buffers = [.. geometry.Buffers, b] },
            _ => geometry with
            {
                Buffers = [failure switch
            {
                "source_position" => b with { Points = [new(-2, 0, 0), .. b.Points.Skip(1)] },
                "out_of_range" => b with { TriangleIndices = [0, 1, 3] },
                "missing_triangle" => b with { TriangleIndices = [] },
                "degenerate" => b with { TriangleIndices = [0, 0, 2] },
                "member" => b with { MemberId = "invented" },
                _ => b with { DrawCallIds = [] }
            }, .. geometry.Buffers.Skip(1)]
            }
        };
        Assert.Equal("DIRECTIONAL_PREVIEW_INVALID", Assert.Throws<S2ModKitException>(() => DirectionalSelectionPreviewBuilder.Create(plan, geometry)).Error.Code);
    }

    [Theory]
    [InlineData("prediction")]
    [InlineData("protection")]
    public void RehashedPlanCannotSupplyFalsePredictionOrProtection(string failure)
    {
        var (plan, geometry) = Fixture(new(1.5f, 1.5f, 1)); var t = plan.Operations[0].DirectionalTransformTarget!;
        t = failure == "prediction" ? t with { Buffers = t.Buffers.Select(b => b with { ExpectedPositionHash = ContentHash.Compute("wrong"u8) }).ToArray() }
            : t with { Protection = t.Protection with { Union = t.Protection.Union.Select(s => s with { SourcePositionHash = ContentHash.Compute("wrong"u8), ExpectedPositionHash = ContentHash.Compute("wrong"u8) }).ToArray() } };
        if (failure == "protection")
        {
            var sets = t.Protection.Union.Where(s => s.VertexCount > 0).ToArray();
            t = t with { Protection = new([new("fixed", sets, DirectionalContractValidator.ContributorSetHash(sets))], t.Protection.Union, MutationPlanJson.ComputeDirectionalFactsHash(t.Protection.Union)) };
        }
        t = t with { TargetFingerprint = MutationPlanJson.ComputeDirectionalTargetFingerprint(t) };
        plan = plan with { Operations = [plan.Operations[0] with { DirectionalTransformTarget = t }] };
        plan = plan with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(plan) };
        geometry = geometry with { PlanFingerprint = plan.Fingerprint };
        Assert.Throws<S2ModKitException>(() => DirectionalSelectionPreviewBuilder.Create(plan, geometry));
    }

    [Fact]
    public async Task DirectionalPublicationIsDeterministicAndNeverOverwritesAnOwnedArtifact()
    {
        var (plan, geometry) = Fixture(new(1.5f, 1.5f, 1));
        var artifacts = DirectionalSelectionPreviewRenderer.Render(DirectionalSelectionPreviewBuilder.Create(plan, geometry));
        var root = Path.Combine(Path.GetTempPath(), $"s2modkit-directional-preview-{Guid.NewGuid():N}"); var token = TestContext.Current.CancellationToken;
        try
        {
            var output = await FileSystemEllipsoidPreviewPublisher.PublishDirectionalAsync(root, artifacts, token);
            Assert.StartsWith("directional-", Path.GetFileName(output.Directory), StringComparison.Ordinal);
            Assert.Equal(output, await FileSystemEllipsoidPreviewPublisher.PublishDirectionalAsync(root, artifacts, token));
            await File.WriteAllTextAsync(output.ContactSheetPath, "owner", token);
            await Assert.ThrowsAsync<S2ModKitException>(() => FileSystemEllipsoidPreviewPublisher.PublishDirectionalAsync(root, artifacts, token));
            Assert.Equal("owner", await File.ReadAllTextAsync(output.ContactSheetPath, token));
        }
        finally { Directory.Delete(root, true); }
    }

    internal static (MutationPlan Plan, DirectionalPreviewGeometry Geometry) Fixture(Point3 scale)
    {
        var plan = ExperimentalVisualContractTests.DirectionalPlan(2, 2); var t = plan.Operations[0].DirectionalTransformTarget!;
        Point3[] points = [new(-1, 0, .25f), new(1, 1, 1), new(10, 0, 1)]; Point3[] context = [new(0, -3, 0), new(0, -3, 1), new(0, -4, 0)];
        var field = t.DirectionalTransform.Field with { Scale = new() { X = scale.X, Y = scale.Y, Z = scale.Z } };
        var math = new DirectionalEllipsoidScale(new(), new(10, 8, 4), .4f, scale, 64); var results = points.Select(math.Evaluate).ToArray();
        var changed = Enumerable.Range(0, 3).Where(i => points[i] != results[i].Position).ToArray();
        var mask = new byte[24]; var weights = new byte[36];
        for (var i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(mask.AsSpan(i * 8), i); BinaryPrimitives.WriteInt32LittleEndian(mask.AsSpan(i * 8 + 4), (int)results[i].Membership);
            BinaryPrimitives.WriteInt32LittleEndian(weights.AsSpan(i * 12), i); BinaryPrimitives.WriteInt64LittleEndian(weights.AsSpan(i * 12 + 4), BitConverter.DoubleToInt64Bits(results[i].Weight));
        }
        var protection = t.Protection.Union.Select(s => s with
        {
            SourcePositionHash = PointHash(s.VertexIndices.Select(i => (s.VertexCount > 0 ? points : context)[i])),
            ExpectedPositionHash = PointHash(s.VertexIndices.Select(i => (s.VertexCount > 0 ? points : context)[i]))
        }).ToArray();
        var assertion = protection.Where(s => s.VertexCount > 0).ToArray();
        t = t with
        {
            DirectionalTransform = t.DirectionalTransform with { Field = field },
            Certificate = DirectionalContractValidator.Certificate(field),
            MaximumDisplacement = results.Max(r => r.MaximumDisplacement),
            Buffers = t.Buffers.Select(b => b with
            {
                MaskHash = ContentHash.Compute(mask),
                WeightHash = ContentHash.Compute(weights),
                InputPositionHash = PointHash(points),
                ExpectedPositionHash = PointHash(results.Select(r => r.Position)),
                ChangedPositionCount = changed.Length,
                FullVertexCount = 2,
                TransitionVertexCount = 0,
                PinnedVertexCount = 1,
                BeforeBounds = Bounds(points),
                ExpectedAfterBounds = Bounds(results.Select(r => r.Position)),
                MaximumDisplacement = results.Max(r => r.MaximumDisplacement)
            }).ToArray(),
            WordAudits = t.WordAudits.Select(a => a with
            {
                ChangedPositionIndices = changed,
                ChangedPositionSetHash = DirectionalContractValidator.VertexSetHash(changed),
                SourcePinnedPositionHash = PointHash([points[2]]),
                ExpectedPinnedPositionHash = PointHash([points[2]])
            }).ToArray(),
            ContextBuffers = t.ContextBuffers.Select(c => c with { PositionHash = PointHash(c.Selected ? points : context) }).ToArray(),
            Coincidences = t.Coincidences.Select(c => c with { MovingSelectedRecordCount = 2 * changed.Length }).ToArray(),
            Protection = new([new("fixed", assertion, DirectionalContractValidator.ContributorSetHash(assertion))], protection, MutationPlanJson.ComputeDirectionalFactsHash(protection))
        };
        t = t with
        {
            BoxClosures = t.BoxClosures.Select(c =>
        {
            var rows = c.Contributors.Select(r => r with
            {
                SourcePositionHash = PointHash(r.VertexBufferOrdinal < 2 ? points : context),
                ExpectedPositionHash = PointHash(r.VertexBufferOrdinal < 2 ? results.Select(r => r.Position) : context)
            }).ToArray();
            return c with { Contributors = rows, ClosureHash = MutationPlanJson.ComputeDirectionalFactsHash(rows) };
        }).ToArray()
        };
        t = t with { TargetFingerprint = MutationPlanJson.ComputeDirectionalTargetFingerprint(t) };
        plan = plan with { Operations = [plan.Operations[0] with { DirectionalTransformTarget = t }] };
        plan = plan with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(plan) };
        var buffers = t.ContextBuffers.Select(c => new DirectionalPreviewSourceBuffer(c, c.Selected ? t.Buffers.Single(b => b.Lod == c.Lod && b.VertexBufferOrdinal == c.VertexBufferOrdinal).MemberId : null,
            c.Selected ? t.DirectionalTransform.Members[c.VertexBufferOrdinal].Lods[c.Lod].DrawCallIds : ["excluded"], c.Selected ? points : context, [0, 1, 2])).ToArray();
        return (plan, new(plan.InputHash, plan.Fingerprint, buffers));
    }
    private static ContentHash PointHash(IEnumerable<Point3> points)
    { using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream); foreach (var p in points) { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); } return ContentHash.Compute(stream.ToArray()); }
    private static GeometryBounds Bounds(IEnumerable<Point3> points)
    { var b = Bounds3.FromPoints(points.ToArray()); return new(new() { X = b.Min.X, Y = b.Min.Y, Z = b.Min.Z }, new() { X = b.Max.X, Y = b.Max.Y, Z = b.Max.Z }); }
}
