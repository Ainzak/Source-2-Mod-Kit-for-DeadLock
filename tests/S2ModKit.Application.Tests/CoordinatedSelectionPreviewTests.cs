using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using S2ModKit.Infrastructure;
using S2ModKit.Reporting;

namespace S2ModKit.Application.Tests;

public sealed class CoordinatedSelectionPreviewTests
{
    [Fact]
    public void OneFieldPredictsEveryMemberAndRetainsExactExcludedContext()
    {
        var (plan, geometry, model) = Fixture();
        var preview = CoordinatedSelectionPreviewBuilder.Create(plan, geometry, model);
        Assert.Equal(2, preview.Lods[0].Buffers.Count);
        Assert.All(preview.Lods[0].Buffers, b =>
        {
            Assert.Equal(["full", "transition", "pinned"], b.Points.Select(p => p.Membership));
            Assert.Equal(b.Points[2].Original, b.Points[2].Predicted);
            Assert.Equal([0, 1, 2], b.TriangleIndices);
        });
        var artifacts = CoordinatedSelectionPreviewRenderer.Render(preview);
        var json = Encoding.UTF8.GetString(artifacts.SummaryJson.Span);
        Assert.Contains("excludedBuffers", json, StringComparison.Ordinal);
        Assert.Contains("unverified containment", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"points\"", json, StringComparison.Ordinal);
        var svg = XDocument.Parse(Encoding.UTF8.GetString(artifacts.ContactSheetSvg.Span));
        Assert.Equal(12, svg.Descendants().Count(e => e.Attribute("data-member") is not null));
        Assert.Equal(6, svg.Descendants().Count(e => (string?)e.Attribute("data-role") == "excluded-context"));
        Assert.DoesNotContain(svg.Descendants(), e => e.Name.LocalName is "script" or "foreignObject");
        Assert.Contains("No inferred hair", svg.Root!.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("plan")]
    [InlineData("lod")]
    [InlineData("missing_member")]
    [InlineData("reordered_members")]
    [InlineData("duplicate_member")]
    [InlineData("point")]
    [InlineData("topology")]
    [InlineData("reversed")]
    [InlineData("drawcalls")]
    [InlineData("missing_context")]
    [InlineData("context_overlap")]
    [InlineData("extra_context")]
    [InlineData("context_count")]
    [InlineData("context_drawcalls")]
    [InlineData("context_materials")]
    [InlineData("context_label")]
    public void IncompleteForgedOrOverlappingGeometryCannotRender(string failure)
    {
        var (plan, geometry, model) = Fixture(); var lod = geometry.Lods[0]; var b = lod.Buffers[0];
        geometry = failure switch
        {
            "source" => geometry with { InputHash = ContentHash.Compute("wrong"u8) },
            "plan" => geometry with { PlanFingerprint = ContentHash.Compute("wrong"u8) },
            "lod" => geometry with { Lods = [] },
            "missing_member" => geometry with { Lods = [lod with { Buffers = [b] }] },
            "reordered_members" => geometry with { Lods = [lod with { Buffers = lod.Buffers.Reverse().ToArray() }] },
            "duplicate_member" => geometry with { Lods = [lod with { Buffers = [b, b] }] },
            "point" => geometry with { Lods = [lod with { Buffers = [b with { Points = [new(0, 0, 0), .. b.Points.Skip(1)] }, lod.Buffers[1]] }] },
            "topology" => geometry with { Lods = [lod with { Buffers = [b with { TriangleIndices = [0, 1, 9] }, lod.Buffers[1]] }] },
            "reversed" => geometry with { Lods = [lod with { Buffers = [b with { TriangleIndices = [0, 0, 1] }, lod.Buffers[1]] }] },
            "drawcalls" => geometry with { Lods = [lod with { Buffers = [b with { DrawCallIds = [] }, lod.Buffers[1]] }] },
            "missing_context" => geometry with { Lods = [lod with { Context = [] }] },
            "context_overlap" => geometry with { Lods = [lod with { Context = [new(0, 0, [new(1, 2, 3)])] }] },
            "extra_context" => geometry with { Lods = [lod with { Context = [.. lod.Context, lod.Context[0]] }] },
            "context_drawcalls" => geometry with { Lods = [lod with { Context = [lod.Context[0] with { DrawCallIds = ["forged"] }] }] },
            "context_materials" => geometry with { Lods = [lod with { Context = [lod.Context[0] with { MaterialPaths = ["materials/forged.vmat_c"] }] }] },
            "context_label" => geometry with { Lods = [lod with { Context = [lod.Context[0] with { SourceLabel = "inferred anatomy" }] }] },
            _ => geometry with { Lods = [lod with { Context = [lod.Context[0] with { Points = [] }] }] },
        };
        Assert.Throws<S2ModKitException>(() => CoordinatedSelectionPreviewBuilder.Create(plan, geometry, model));
    }

    [Fact]
    public void RehashedForgedMaskCannotSubstitutePlannersClaimForPrediction()
    {
        var (plan, geometry, model) = Fixture(); var target = plan.Operations[0].CoordinatedTransformTarget!;
        target = target with { Buffers = [target.Buffers[0] with { MaskHash = ContentHash.Compute("forged"u8) }, target.Buffers[1]] };
        target = target with { TargetFingerprint = MutationPlanJson.ComputeCoordinatedTargetFingerprint(target) };
        plan = plan with { Operations = [plan.Operations[0] with { CoordinatedTransformTarget = target }] };
        plan = plan with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(plan) };
        Assert.Throws<S2ModKitException>(() => CoordinatedSelectionPreviewBuilder.Create(plan, geometry with { PlanFingerprint = plan.Fingerprint }, model));
    }

    [Fact]
    public async Task PublicationIsImmutableAndRenderingIsCultureIndependent()
    {
        var (plan, geometry, model) = Fixture(); var preview = CoordinatedSelectionPreviewBuilder.Create(plan, geometry, model);
        var culture = CultureInfo.CurrentCulture;
        EllipsoidPreviewArtifacts artifacts;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US"); artifacts = CoordinatedSelectionPreviewRenderer.Render(preview);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); var repeated = CoordinatedSelectionPreviewRenderer.Render(preview);
            Assert.Equal(artifacts.ContactSheetSvg.ToArray(), repeated.ContactSheetSvg.ToArray());
            Assert.Equal(artifacts.SummaryJson.ToArray(), repeated.SummaryJson.ToArray());
        }
        finally { CultureInfo.CurrentCulture = culture; }
        var root = Path.Combine(Path.GetTempPath(), "s2modkit-common-preview-" + Guid.NewGuid().ToString("N")); var token = TestContext.Current.CancellationToken;
        try
        {
            var output = await FileSystemEllipsoidPreviewPublisher.PublishCoordinatedAsync(root, artifacts, token);
            Assert.StartsWith("coordinated-", Path.GetFileName(output.Directory), StringComparison.Ordinal);
            Assert.Equal(output, await FileSystemEllipsoidPreviewPublisher.PublishCoordinatedAsync(root, artifacts, token));
            await File.WriteAllTextAsync(output.ContactSheetPath, "owned-file", token);
            await Assert.ThrowsAsync<S2ModKitException>(() => FileSystemEllipsoidPreviewPublisher.PublishCoordinatedAsync(root, artifacts, token));
            Assert.Equal("owned-file", await File.ReadAllTextAsync(output.ContactSheetPath, token));
            Assert.Empty(Directory.GetDirectories(root, ".ellipsoid-preview-*"));
        }
        finally { Directory.Delete(root, true); }
    }

    internal static (MutationPlan Plan, CoordinatedPreviewGeometry Geometry, ModelSnapshot Model) Fixture()
    {
        var plan = ExperimentalVisualContractTests.CoordinatedPlan(); var target = plan.Operations[0].CoordinatedTransformTarget!;
        Point3[] points = [new(9, 0, 0), new(2, 1, 1), new(-1, 0, 0)]; var math = new CoordinatedFieldMath(target.CoordinatedTransform.Field, target.DisplacementLimit);
        var results = points.Select(math.Evaluate).ToArray(); var mask = new byte[24]; var weights = new byte[36];
        for (var i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(mask.AsSpan(i * 8), i); BinaryPrimitives.WriteInt32LittleEndian(mask.AsSpan(i * 8 + 4), (int)results[i].Membership);
            BinaryPrimitives.WriteInt32LittleEndian(weights.AsSpan(i * 12), i); BinaryPrimitives.WriteInt64LittleEndian(weights.AsSpan(i * 12 + 4), BitConverter.DoubleToInt64Bits(results[i].Weight));
        }
        target = target with
        {
            MaximumDisplacement = results.Max(r => r.MaximumDisplacement),
            Buffers = target.Buffers.Select(b => b with
            {
                MaskHash = ContentHash.Compute(mask),
                WeightHash = ContentHash.Compute(weights),
                InputPositionHash = PointHash(points),
                ExpectedPositionHash = PointHash(results.Select(r => r.Position)),
                BeforeBounds = Bounds(points),
                ExpectedAfterBounds = Bounds(results.Select(r => r.Position).ToArray()),
                MaximumDisplacement = results.Max(r => r.MaximumDisplacement)
            }).ToArray()
        };
        target = target with { TargetFingerprint = MutationPlanJson.ComputeCoordinatedTargetFingerprint(target) };
        plan = plan with { Operations = [plan.Operations[0] with { CoordinatedTransformTarget = target }] };
        plan = plan with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(plan) };
        var buffers = target.Buffers.Select(b => new CoordinatedPreviewBufferGeometry(b.MemberId, 0, b.MeshOrdinal, b.VertexBufferOrdinal,
            target.CoordinatedTransform.Members.Single(m => m.MemberId == b.MemberId).Lods[0].DrawCallIds, points, [0, 1, 2])).ToArray();
        var sourceBuffers = target.Buffers.Select(b => new VertexBufferSnapshot(b.VertexBufferOrdinal, b.VertexResourceBlockIndex, 3, 28, b.VertexBlockInputHash, b.InputDecodedVertexBufferHash, b.PositionLayout)).ToArray();
        var geometry = new MeshGeometrySnapshot("ready", "synthetic", [.. sourceBuffers, sourceBuffers[0] with { Ordinal = 2, VertexCount = 1 }], [], [], target.Buffers[0].Codec);
        var model = new ModelSnapshot(new("models/test.vmdl_c", plan.InputHash, 12, []), [new(0, [new("models/test.vmdl_c", 0, 0, plan.InputHash, []) { Geometry = geometry }])]);
        return (plan, new(plan.InputHash, plan.Fingerprint, [new(0, buffers, [new(0, 2, [new(0, -4, 0)])])]), model);
    }
    private static ContentHash PointHash(IEnumerable<Point3> points)
    { using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream); foreach (var p in points) { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); } return ContentHash.Compute(stream.ToArray()); }
    private static GeometryBounds Bounds(IReadOnlyList<Point3> points)
    { var b = Bounds3.FromPoints(points); return new(new() { X = b.Min.X, Y = b.Min.Y, Z = b.Min.Z }, new() { X = b.Max.X, Y = b.Max.Y, Z = b.Max.Z }); }
}
