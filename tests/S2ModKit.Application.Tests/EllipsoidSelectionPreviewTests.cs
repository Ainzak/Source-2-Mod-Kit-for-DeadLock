using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using S2ModKit.Infrastructure;
using S2ModKit.Reporting;

namespace S2ModKit.Application.Tests;

public sealed class EllipsoidSelectionPreviewTests
{
    [Fact]
    public void PreviewRetainsAllPointsTopologyAndContextAndMatchesFrozenMembership()
    {
        var (plan, geometry) = Fixture();
        var preview = EllipsoidSelectionPreviewBuilder.Create(plan, geometry);
        Assert.Equal(plan.Fingerprint, preview.PlanFingerprint);
        Assert.Equal(["core", "transition", "pinned"], preview.Lods[0].Points.Select(p => p.Membership));
        Assert.Equal(geometry.Lods[0].TriangleIndices, preview.Lods[0].TriangleIndices);
        Assert.Equal(geometry.Lods[0].Context[0].MeshOrdinal, preview.Lods[0].Context[0].MeshOrdinal);
        Assert.Equal(geometry.Lods[0].Context[0].Points, preview.Lods[0].Context[0].Points);
        Assert.Equal(geometry.Lods[0].Points[2], preview.Lods[0].Points[2].Predicted);
        Assert.Equal(0.5f, preview.Lods[0].Points[0].Predicted.X);
        var artifacts = EllipsoidSelectionPreviewRenderer.Render(preview);
        using var summary = JsonDocument.Parse(artifacts.SummaryJson);
        Assert.Equal(plan.Fingerprint.Value, summary.RootElement.GetProperty("planFingerprint").GetString());
        Assert.Equal("bind_space_prediction", summary.RootElement.GetProperty("proofLevel").GetString());
        Assert.Equal(artifacts.ContactSheetHash.Value, summary.RootElement.GetProperty("contactSheetHash").GetString());
        Assert.True(artifacts.SummaryJson.Length < 8000);
        Assert.DoesNotContain("\"points\"", Encoding.UTF8.GetString(artifacts.SummaryJson.Span), StringComparison.Ordinal);
        var svg = XDocument.Parse(Encoding.UTF8.GetString(artifacts.ContactSheetSvg.Span));
        XNamespace ns = "http://www.w3.org/2000/svg";
        Assert.Equal(6, svg.Descendants(ns + "path").Count(p => (string?)p.Attribute("data-role") == "enclosing-topology"));
        foreach (var membership in new[] { "core", "transition", "pinned" })
        {
            var paths = svg.Descendants(ns + "path").Where(p => (string?)p.Attribute("data-membership") == membership).ToArray();
            Assert.Equal(9, paths.Length); // three overview + six focused views
            Assert.All(paths, p => Assert.Equal("1", (string?)p.Attribute("data-count")));
        }
        Assert.DoesNotContain(svg.Descendants(), e => e.Name.LocalName is "script" or "foreignObject");
        Assert.Contains("NOT EDITABLE ANATOMY", svg.Root!.Value, StringComparison.Ordinal);
        Assert.Contains("collision or live Deadlock", svg.Root.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void PointBudgetIncludesNonEditableContextAndRejectsBeforeRendering()
    {
        var (plan, geometry) = Fixture();
        var context = new EllipsoidPreviewContext(1, 0, new Point3[EllipsoidSelectionPreview.MaximumPoints]);
        var error = Assert.Throws<S2ModKitException>(() => EllipsoidSelectionPreviewBuilder.Create(plan,
            geometry with { Lods = [geometry.Lods[0] with { Context = [context] }] }));
        Assert.Equal("ELLIPSOID_PREVIEW_INVALID", error.Error.Code);
        Assert.Contains("budget", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryLodHasSeparateIdentityAndIncompleteCoverageRejects()
    {
        var (plan, geometry) = Fixture();
        var operation = plan.Operations[0];
        var target = operation.EllipsoidTransformTarget!;
        var secondCall = operation.SelectedDrawCalls[0] with { Lod = 1, MeshOrdinal = 2, DrawCallId = "dc_222222222222222222222222" };
        target = target with { Buffers = [target.Buffers[0], target.Buffers[0] with { Lod = 1, MeshOrdinal = 2 }] };
        target = target with { TargetFingerprint = MutationPlanJson.ComputeEllipsoidTargetFingerprint(target) };
        plan = plan with { Operations = [operation with { SelectedDrawCalls = [operation.SelectedDrawCalls[0], secondCall], EllipsoidTransformTarget = target }] };
        plan = plan with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(plan) };
        geometry = geometry with { PlanFingerprint = plan.Fingerprint, Lods = [geometry.Lods[0], geometry.Lods[0] with { Lod = 1, MeshOrdinal = 2, DrawCallIds = [secondCall.DrawCallId] }] };
        var preview = EllipsoidSelectionPreviewBuilder.Create(plan, geometry);
        Assert.Equal([0, 1], preview.Lods.Select(l => l.Buffer.Lod));
        var svg = XDocument.Parse(Encoding.UTF8.GetString(EllipsoidSelectionPreviewRenderer.Render(preview).ContactSheetSvg.Span));
        Assert.Equal(12, svg.Descendants().Count(e => (string?)e.Attribute("data-role") == "enclosing-topology"));
        Assert.Throws<S2ModKitException>(() => EllipsoidSelectionPreviewBuilder.Create(plan, geometry with { Lods = [geometry.Lods[0]] }));
        Assert.Throws<S2ModKitException>(() => EllipsoidSelectionPreviewBuilder.Create(plan, geometry with { Lods = geometry.Lods.Reverse().ToArray() }));
    }

    [Fact]
    public void IdentityIncludesContextAndTopologyAndRenderingIsCultureIndependent()
    {
        var (plan, geometry) = Fixture();
        var first = EllipsoidSelectionPreviewBuilder.Create(plan, geometry);
        var reversedOrder = geometry with { Lods = [geometry.Lods[0] with { TriangleIndices = [1, 2, 0] }] };
        var changedContext = geometry with { Lods = [geometry.Lods[0] with { Context = [new(1, 0, [new(20, 20, 20)])] }] };
        Assert.NotEqual(first.PreviewFingerprint, EllipsoidSelectionPreviewBuilder.Create(plan, reversedOrder).PreviewFingerprint);
        Assert.NotEqual(first.PreviewFingerprint, EllipsoidSelectionPreviewBuilder.Create(plan, changedContext).PreviewFingerprint);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var a = EllipsoidSelectionPreviewRenderer.Render(first);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var b = EllipsoidSelectionPreviewRenderer.Render(first);
            Assert.Equal(a.ContactSheetSvg.ToArray(), b.ContactSheetSvg.ToArray());
            Assert.Equal(a.SummaryJson.ToArray(), b.SummaryJson.ToArray());
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Assert.Throws<S2ModKitException>(() => EllipsoidSelectionPreviewRenderer.Render(first with
        { Lods = [first.Lods[0] with { Points = first.Lods[0].Points.Skip(1).ToArray() }] }));
    }

    [Fact]
    public void IncompleteForgedOrInvalidGeometryCannotProduceAContactSheet()
    {
        var (plan, geometry) = Fixture();
        foreach (var bad in new[]
        {
            geometry with { InputHash = ContentHash.Compute("wrong-input"u8) },
            geometry with { PlanFingerprint = ContentHash.Compute("wrong-plan"u8) },
            geometry with { Lods = [] },
            geometry with { Lods = [geometry.Lods[0], geometry.Lods[0]] },
            geometry with { Lods = [geometry.Lods[0] with { Points = geometry.Lods[0].Points.Skip(1).ToArray() }] },
            geometry with { Lods = [geometry.Lods[0] with { Points = [new(0.3f, 0, 0), .. geometry.Lods[0].Points.Skip(1)] }] },
            geometry with { Lods = [geometry.Lods[0] with { TriangleIndices = [0, 1, 99] }] },
            geometry with { Lods = [geometry.Lods[0] with { TriangleIndices = [0, 0, 1] }] },
            geometry with { Lods = [geometry.Lods[0] with { DrawCallIds = [] }] },
            geometry with { Lods = [geometry.Lods[0] with { Context = [new(0, 0, [new(1, 2, 3)])] }] },
            geometry with { Lods = [geometry.Lods[0] with { TriangleIndices = [0, 1, 2, 0, 1, 2] }] },
        }) Assert.Throws<S2ModKitException>(() => EllipsoidSelectionPreviewBuilder.Create(plan, bad));
        var target = plan.Operations[0].EllipsoidTransformTarget!;
        target = target with { Buffers = [target.Buffers[0] with { MaskHash = ContentHash.Compute("forged"u8) }] };
        target = target with { TargetFingerprint = MutationPlanJson.ComputeEllipsoidTargetFingerprint(target) };
        var forged = plan with { Operations = [plan.Operations[0] with { EllipsoidTransformTarget = target }] };
        forged = forged with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(forged) };
        Assert.Throws<S2ModKitException>(() => EllipsoidSelectionPreviewBuilder.Create(forged, geometry with { PlanFingerprint = forged.Fingerprint }));
    }

    [Fact]
    public async Task PublicationIsCompleteDeterministicAndNeverOverwritesExistingBytes()
    {
        var (plan, geometry) = Fixture();
        var artifacts = EllipsoidSelectionPreviewRenderer.Render(EllipsoidSelectionPreviewBuilder.Create(plan, geometry));
        var root = Path.Combine(Path.GetTempPath(), $"s2modkit-preview-{Guid.NewGuid():N}");
        try
        {
            var token = TestContext.Current.CancellationToken;
            var first = await FileSystemEllipsoidPreviewPublisher.PublishAsync(root, artifacts, token);
            var repeated = await FileSystemEllipsoidPreviewPublisher.PublishAsync(root, artifacts, token);
            Assert.Equal(first, repeated);
            Assert.Equal(artifacts.SummaryHash, ContentHash.Compute(await File.ReadAllBytesAsync(first.SummaryPath, token)));
            await File.WriteAllTextAsync(first.ContactSheetPath, "existing-file", token);
            var error = await Assert.ThrowsAsync<S2ModKitException>(() => FileSystemEllipsoidPreviewPublisher.PublishAsync(root, artifacts, token));
            Assert.Equal("ELLIPSOID_PREVIEW_OUTPUT_CONFLICT", error.Error.Code);
            Assert.Equal("existing-file", await File.ReadAllTextAsync(first.ContactSheetPath, token));
            Assert.Empty(Directory.GetDirectories(root, ".ellipsoid-preview-*"));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var newRoot = Path.Combine(root, "cancelled");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileSystemEllipsoidPreviewPublisher.PublishAsync(newRoot, artifacts, cancelled.Token));
            Assert.Empty(Directory.GetDirectories(newRoot));
            await Assert.ThrowsAsync<S2ModKitException>(() => FileSystemEllipsoidPreviewPublisher.PublishAsync(root, artifacts with { ContactSheetHash = ContentHash.Compute("wrong"u8) }, token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    internal static (MutationPlan Plan, EllipsoidPreviewGeometry Geometry) Fixture()
    {
        var plan = ExperimentalVisualContractTests.EllipsoidPlan();
        var target = plan.Operations[0].EllipsoidTransformTarget!;
        Point3[] points = [new(0.25f, 0, 0), new(2, 1, 1), new(10, 0, 0)];
        var math = new EllipsoidScale(default, new(8, 4, 2), 1f / 16, 2, 64);
        var results = points.Select(math.Evaluate).ToArray();
        var masks = new byte[24]; var weights = new byte[36];
        for (var index = 0; index < points.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(masks.AsSpan(index * 8), index);
            BinaryPrimitives.WriteInt32LittleEndian(masks.AsSpan(index * 8 + 4), (int)results[index].Membership);
            BinaryPrimitives.WriteInt32LittleEndian(weights.AsSpan(index * 12), index);
            BinaryPrimitives.WriteInt64LittleEndian(weights.AsSpan(index * 12 + 4), BitConverter.DoubleToInt64Bits(results[index].Weight));
        }
        var maximum = results.Max(r => r.MaximumDisplacement);
        target = target with
        {
            MaximumDisplacement = maximum,
            Buffers = [target.Buffers[0] with
        {
            MaskHash = ContentHash.Compute(masks), WeightHash = ContentHash.Compute(weights), MaximumDisplacement = maximum,
            InputPositionHash = HashPoints(points), ExpectedPositionHash = HashPoints(results.Select(r => r.Position)),
            BeforeBounds = ToBounds(points), ExpectedAfterBounds = ToBounds(results.Select(r => r.Position).ToArray()),
        }]
        };
        target = target with { TargetFingerprint = MutationPlanJson.ComputeEllipsoidTargetFingerprint(target) };
        plan = plan with { Operations = [plan.Operations[0] with { EllipsoidTransformTarget = target }] };
        plan = plan with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(plan) };
        return (plan, new(plan.InputHash, plan.Fingerprint, [new(0, 0, 0, [plan.Operations[0].SelectedDrawCalls[0].DrawCallId], points, [0, 1, 2], [new(1, 0, [new(0, -4, 0), new(1, -4, 0)])])]));
    }

    private static ContentHash HashPoints(IEnumerable<Point3> points)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var point in points) { writer.Write(point.X); writer.Write(point.Y); writer.Write(point.Z); }
        return ContentHash.Compute(stream.ToArray());
    }

    private static GeometryBounds ToBounds(IReadOnlyList<Point3> points)
    {
        var bounds = Bounds3.FromPoints(points);
        return new(new() { X = bounds.Min.X, Y = bounds.Min.Y, Z = bounds.Min.Z }, new() { X = bounds.Max.X, Y = bounds.Max.Y, Z = bounds.Max.Z });
    }
}
