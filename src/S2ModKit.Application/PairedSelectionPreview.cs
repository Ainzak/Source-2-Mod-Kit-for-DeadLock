using System.Buffers.Binary;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Application;

public interface IPairedPreviewGeometryReader
{
    Task<DirectionalPreviewGeometry> ReadPairedPreviewGeometryAsync(ArtifactContent input, MutationPlan plan, CancellationToken token = default);
}

public sealed record PairedPreviewBuffer(DirectionalContextBuffer Source, string? MemberId, IReadOnlyList<string> DrawCallIds,
    IReadOnlyList<EllipsoidPreviewPoint> Points, IReadOnlyList<int> TriangleIndices, IReadOnlyList<int> ProtectedIndices,
    IReadOnlyList<int> ProceduralIndices, IReadOnlyList<int> Partners);
public sealed record PairedRegionMeasurement(int Lod, string FieldId, int ChangedPositions, int ChangedFrames,
    TransformVector3 SpanBefore, TransformVector3 SpanAfter, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] double? XChangePercent,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] double? YChangePercent,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] double? ZChangePercent,
    float MaximumDisplacement, int ProtectedRecords, int ProceduralRecords)
{
    public TransformVector3 PositiveReachBefore { get; init; } = new();
    public TransformVector3 PositiveReachAfter { get; init; } = new();
    public TransformVector3 NegativeReachBefore { get; init; } = new();
    public TransformVector3 NegativeReachAfter { get; init; } = new();
}
public sealed record PairedSelectionPreview(ContentHash InputHash, ContentHash PlanFingerprint, ContentHash TargetFingerprint,
    ContentHash PreviewFingerprint, PairedDirectionalVisualTransform Transform, IReadOnlyList<PairedPreviewBuffer> Buffers,
    IReadOnlyList<PairedRegionMeasurement> Regions)
{
    public const string ProjectionProfile = "paired_surface_comparison@1";
}

public static class PairedSelectionPreviewBuilder
{
    public static PairedSelectionPreview Create(MutationPlan plan, DirectionalPreviewGeometry geometry)
    {
        _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(plan));
        if (plan.SchemaVersion != 7 || geometry.InputHash != plan.InputHash || geometry.PlanFingerprint != plan.Fingerprint)
            throw Invalid("Source/plan identities differ.");
        var operation = plan.Operations.Single(); var t = operation.PairedTransformTarget!;
        if (!geometry.Buffers.Select(b => b.Source).SequenceEqual(t.ContextBuffers)
            || geometry.Buffers.Sum(b => (long)b.Points.Count) > EllipsoidSelectionPreview.MaximumPoints
            || geometry.Buffers.Sum(b => (long)b.TriangleIndices.Count) > EllipsoidSelectionPreview.MaximumTriangleIndices)
            throw Invalid("Incomplete selected/excluded geometry or exceeded budget; no sampling is allowed.");
        var parameters = t.PairedTransform.Fields.Select(f => new DirectionalFieldParameters(Point(f.Field.Pivot.Point!), Point(f.Field.OuterRadii),
            f.Field.CoreFraction, Point(f.Field.Scale), t.DisplacementLimit)).ToArray();
        var pair = new PairedDirectionalFieldReconstruction(parameters[0], parameters[1]);
        var singles = parameters.Select(p => new DirectionalFieldReconstruction(p.Center, p.OuterRadii, p.CoreFraction, p.Scale, p.DisplacementLimit)).ToArray();
        var buffers = new List<PairedPreviewBuffer>();
        foreach (var source in geometry.Buffers)
        {
            var facts = source.Source;
            var expected = t.Buffers.SingleOrDefault(b => b.Lod == facts.Lod && b.MeshOrdinal == facts.MeshOrdinal && b.VertexBufferOrdinal == facts.VertexBufferOrdinal);
            if (facts.Selected != (expected is not null) || source.MemberId != expected?.MemberId || source.Points.Count != facts.VertexCount
                || source.Points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
                || Hash(source.Points) != facts.PositionHash || source.DrawCallIds.Count == 0
                || source.DrawCallIds.Count != source.DrawCallIds.Distinct(StringComparer.Ordinal).Count()
                || source.TriangleIndices.Count == 0 || source.TriangleIndices.Count % 3 != 0 || source.TriangleIndices.Any(i => (uint)i >= (uint)facts.VertexCount))
                throw Invalid("Complete source words, buffer identity or topology is invalid.");
            var points = new List<EllipsoidPreviewPoint>(); var partners = new List<int>();
            foreach (var p in source.Points)
            {
                if (expected is null) { points.Add(new(p, p, "pinned", 0)); partners.Add(-1); continue; }
                var r = pair.ReconstructPosition(p); var active = r.ActiveFieldIndex;
                var state = active is { } fi ? singles[fi].ReconstructPosition(p) : new EllipsoidPointResult(p, EllipsoidMembership.Pinned, 0, 0);
                points.Add(new(p, r.Position, state.Membership.ToString().ToLowerInvariant(), state.Weight)); partners.Add(active ?? -1);
            }
            if (expected is not null)
            {
                var topology = t.SourceTriangles.Single(s => s.MemberId == expected.MemberId && s.Lod == expected.Lod);
                var ids = t.PairedTransform.Members.Single(m => m.MemberId == expected.MemberId).Lods.Single(l => l.Lod == expected.Lod).DrawCallIds;
                if (!source.DrawCallIds.SequenceEqual(ids) || !source.TriangleIndices.SequenceEqual(topology.TriangleIndices)
                    || Hash(points.Select(p => p.Predicted)) != expected.ExpectedPositionHash
                    || points.Count(p => !Same(p.Original, p.Predicted)) != expected.ChangedPositionCount
                    || points.Max(p => ConservativePointDistance.RoundUp(p.Original, p.Predicted)) != expected.MaximumDisplacement)
                    throw Invalid("Predicted topology/position words, changed counts or displacement differ from the frozen plan.");
                var dispatch = t.Dispatch.Single(d => d.MemberId == expected.MemberId && d.Lod == expected.Lod);
                for (var fi = 0; fi < 2; fi++)
                {
                    var states = source.Points.Select(p => singles[fi].ReconstructPosition(p)).ToArray(); var d = dispatch.Fields[fi];
                    var words = new byte[checked(facts.VertexCount * 12)];
                    for (var i = 0; i < facts.VertexCount; i++)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(words.AsSpan(i * 12), i);
                        BinaryPrimitives.WriteInt64LittleEndian(words.AsSpan(i * 12 + 4), BitConverter.DoubleToInt64Bits(states[i].Weight));
                    }
                    if (!Enumerable.Range(0, facts.VertexCount).Where(i => states[i].Membership == EllipsoidMembership.Core).SequenceEqual(d.CoreIndices)
                        || !Enumerable.Range(0, facts.VertexCount).Where(i => states[i].Membership == EllipsoidMembership.Transition).SequenceEqual(d.TransitionIndices)
                        || !Enumerable.Range(0, facts.VertexCount).Where(i => partners[i] == fi && !Same(points[i].Original, points[i].Predicted)).SequenceEqual(d.ChangedPositionIndices)
                        || ContentHash.Compute(words) != d.WeightHash) throw Invalid("Original-source partner dispatch or weight words differ.");
                }
                try { SourceCoincidenceTriangleGuard.Validate(source.Points, points.Select(p => p.Predicted).ToArray(), source.TriangleIndices); }
                catch (ArgumentException e) { throw Invalid($"Predicted source-partition/triangle guard failed: {e.Message}"); }
            }
            int[] Fixed(IReadOnlyList<DirectionalProtectedSet> sets)
            {
                var set = sets.SingleOrDefault(s => s.Lod == facts.Lod && s.MeshOrdinal == facts.MeshOrdinal && s.VertexBufferOrdinal == facts.VertexBufferOrdinal);
                if (set is null) return [];
                if (set.SourceDecodedBufferHash != facts.DecodedBufferHash || set.VertexIndices.Any(i => (uint)i >= (uint)facts.VertexCount)
                    || Hash(set.VertexIndices.Select(i => points[i].Original)) != set.SourcePositionHash
                    || Hash(set.VertexIndices.Select(i => points[i].Predicted)) != set.ExpectedPositionHash
                    || set.VertexIndices.Any(i => !Same(points[i].Original, points[i].Predicted))) throw Invalid("Fixed source/procedural words differ.");
                return set.VertexIndices.ToArray();
            }
            buffers.Add(new(facts, source.MemberId, source.DrawCallIds, points, source.TriangleIndices, Fixed(t.Protection.Union), Fixed(t.ProceduralInputs.Union), partners));
        }
        var regions = buffers.GroupBy(b => b.Source.Lod).SelectMany(g => Enumerable.Range(0, 2).Select(fi =>
        {
            var points = g.SelectMany(b => b.Points.Where((p, i) => b.Partners[i] == fi && !Same(p.Original, p.Predicted))).ToArray();
            if (points.Length == 0) throw Invalid("Partner has no stored effect in a required LOD.");
            var before = Span(points.Select(p => p.Original)); var after = Span(points.Select(p => p.Predicted));
            return new PairedRegionMeasurement(g.Key, t.PairedTransform.Fields[fi].FieldId, points.Length,
                t.Dispatch.Where(d => d.Lod == g.Key).Sum(d => d.Fields[fi].ChangedFrameIndices.Count), before, after,
                Change(before.X, after.X), Change(before.Y, after.Y), Change(before.Z, after.Z),
                points.Max(p => ConservativePointDistance.RoundUp(p.Original, p.Predicted)), g.Sum(b => b.ProtectedIndices.Count), g.Sum(b => b.ProceduralIndices.Count))
            {
                PositiveReachBefore = Reach(points.Select(p => p.Original), parameters[fi].Center, true),
                PositiveReachAfter = Reach(points.Select(p => p.Predicted), parameters[fi].Center, true),
                NegativeReachBefore = Reach(points.Select(p => p.Original), parameters[fi].Center, false),
                NegativeReachAfter = Reach(points.Select(p => p.Predicted), parameters[fi].Center, false)
            };
        })).ToArray();
        var preview = new PairedSelectionPreview(plan.InputHash, plan.Fingerprint, t.TargetFingerprint, plan.InputHash, t.PairedTransform, buffers, regions);
        return preview with { PreviewFingerprint = ComputeFingerprint(preview) };
    }

    public static ContentHash ComputeFingerprint(PairedSelectionPreview preview) => ContentHash.Compute(JsonDefaults.SerializeToUtf8(new
    { PairedSelectionPreview.ProjectionProfile, preview.InputHash, preview.PlanFingerprint, preview.TargetFingerprint, preview.Transform, preview.Buffers, preview.Regions }));
    private static TransformVector3 Reach(IEnumerable<Point3> source, Point3 center, bool positive)
    {
        var b = Bounds3.FromPoints(source.ToArray());
        return positive ? new() { X = Math.Max(0, b.Max.X - center.X), Y = Math.Max(0, b.Max.Y - center.Y), Z = Math.Max(0, b.Max.Z - center.Z) }
            : new() { X = Math.Max(0, center.X - b.Min.X), Y = Math.Max(0, center.Y - b.Min.Y), Z = Math.Max(0, center.Z - b.Min.Z) };
    }
    private static double? Change(float before, float after) => before > 0 ? 100 * ((double)after / before - 1) : null;
    private static TransformVector3 Span(IEnumerable<Point3> points)
    {
        var b = Bounds3.FromPoints(points.ToArray()); var result = new Point3(b.Max.X - b.Min.X, b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z);
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) || !float.IsFinite(result.Z)) throw Invalid("Unrepresentable region span.");
        return new() { X = result.X, Y = result.Y, Z = result.Z };
    }
    private static ContentHash Hash(IEnumerable<Point3> points) => DirectionalSelectionPreviewBuilder.HashPoints(points);
    private static bool Same(Point3 a, Point3 b) => BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
        && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y) && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);
    private static Point3 Point(TransformVector3 p) => new(p.X, p.Y, p.Z);
    private static S2ModKitException Invalid(string message) => Errors.Verification("PAIRED_PREVIEW_INVALID", message, "Regenerate complete bind-space comparison from immutable source and exact paired plan.");
}
