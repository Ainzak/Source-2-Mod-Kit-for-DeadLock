using System.Buffers.Binary;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Application;

public interface IDirectionalPreviewGeometryReader
{
    Task<DirectionalPreviewGeometry> ReadDirectionalPreviewGeometryAsync(ArtifactContent input, MutationPlan plan, CancellationToken token = default);
}

public sealed record DirectionalPreviewSourceBuffer(DirectionalContextBuffer Source, string? MemberId,
    IReadOnlyList<string> DrawCallIds, IReadOnlyList<Point3> Points, IReadOnlyList<int> TriangleIndices);
public sealed record DirectionalPreviewGeometry(ContentHash InputHash, ContentHash PlanFingerprint,
    IReadOnlyList<DirectionalPreviewSourceBuffer> Buffers);
public sealed record DirectionalPreviewBuffer(DirectionalContextBuffer Source, string? MemberId,
    IReadOnlyList<string> DrawCallIds, IReadOnlyList<EllipsoidPreviewPoint> Points, IReadOnlyList<int> TriangleIndices,
    IReadOnlyList<int> ProtectedIndices);
public sealed record DirectionalPreviewMeasurements(int Lod, int SelectedCount, int ExcludedCount, int ChangedCount,
    int PinnedCount, int ProtectedCount, float MaximumDisplacement, double ModelDiagonal,
    double MaximumDisplacementOverModelDiagonal, Point3 SelectedSpanBefore, Point3 SelectedSpanAfter,
    Point3 ChangedRegionSpanBefore, Point3 ChangedRegionSpanAfter);
public sealed record DirectionalPreviewBufferSummary(DirectionalContextBuffer Source,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? MemberId,
    IReadOnlyList<string> DrawCallIds, int TriangleCount, int ProtectedCount, ContentHash ProtectedIndexHash);
public sealed record DirectionalSelectionPreview(ContentHash InputHash, ContentHash PlanFingerprint, ContentHash TargetFingerprint,
    ContentHash PreviewFingerprint, DirectionalVisualTransform Transform, DirectionalPivotEvidence Pivot,
    IReadOnlyList<DirectionalPreviewBuffer> Buffers, IReadOnlyList<DirectionalPreviewMeasurements> Measurements)
{
    public const string ProjectionProfile = "directional_surface_comparison@1";
}

public static class DirectionalSelectionPreviewBuilder
{
    public static DirectionalSelectionPreview Create(MutationPlan plan, DirectionalPreviewGeometry geometry)
    {
        _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(plan));
        if (plan.SchemaVersion != 6 || geometry.InputHash != plan.InputHash || geometry.PlanFingerprint != plan.Fingerprint)
            throw Invalid("Preview source/plan identity differs.");
        var operation = plan.Operations.Single(); var target = operation.DirectionalTransformTarget!;
        if (!geometry.Buffers.Select(b => b.Source).SequenceEqual(target.ContextBuffers)
            || geometry.Buffers.Select(b => b.Source.Lod).Distinct().Count() > EllipsoidSelectionPreview.MaximumLods
            || geometry.Buffers.Sum(b => (long)b.Points.Count) > EllipsoidSelectionPreview.MaximumPoints
            || geometry.Buffers.Sum(b => (long)b.TriangleIndices.Count) > EllipsoidSelectionPreview.MaximumTriangleIndices)
            throw Invalid("Complete selected/excluded source inventory or geometry budget differs; no decimation is permitted.");
        var field = target.DirectionalTransform.Field;
        var math = new DirectionalFieldReconstruction(Point(target.Pivot.Point), Point(field.OuterRadii), field.CoreFraction, Point(field.Scale), target.DisplacementLimit);
        var buffers = new List<DirectionalPreviewBuffer>();
        foreach (var source in geometry.Buffers)
        {
            var facts = source.Source;
            var selected = target.Buffers.SingleOrDefault(b => b.Lod == facts.Lod && b.MeshOrdinal == facts.MeshOrdinal && b.VertexBufferOrdinal == facts.VertexBufferOrdinal);
            if (facts.Selected != (selected is not null) || source.MemberId != selected?.MemberId
                || source.Points.Count != facts.VertexCount || source.Points.Any(p => !Finite(p))
                || source.DrawCallIds.Count == 0 || source.DrawCallIds.Distinct(StringComparer.Ordinal).Count() != source.DrawCallIds.Count
                || source.TriangleIndices.Count == 0 || source.TriangleIndices.Count % 3 != 0
                || source.TriangleIndices.Any(i => (uint)i >= (uint)source.Points.Count) || HashPoints(source.Points) != facts.PositionHash)
                throw Invalid("Buffer identity, complete source words or topology is invalid.");
            if (selected is not null)
            {
                var calls = target.DirectionalTransform.Members.Single(m => m.MemberId == selected.MemberId).Lods.Single(l => l.Lod == facts.Lod).DrawCallIds;
                if (!calls.SequenceEqual(source.DrawCallIds) || source.TriangleIndices.Count != operation.SelectedDrawCalls.Where(c => calls.Contains(c.DrawCallId)).Sum(c => c.IndexCount))
                    throw Invalid("Selected topology/draw-call coverage differs.");
            }
            var points = source.Points.Select(p =>
            {
                var result = facts.Selected ? math.ReconstructPosition(p) : new EllipsoidPointResult(p, EllipsoidMembership.Pinned, 0, 0);
                return new EllipsoidPreviewPoint(p, result.Position, result.Membership.ToString().ToLowerInvariant(), result.Weight);
            }).ToArray();
            if (selected is not null) CheckPrediction(selected, points);
            var protectedSet = target.Protection.Union.SingleOrDefault(s => s.Lod == facts.Lod && s.MeshOrdinal == facts.MeshOrdinal && s.VertexBufferOrdinal == facts.VertexBufferOrdinal);
            var protectedIndices = protectedSet?.VertexIndices.ToArray() ?? [];
            if (protectedSet is not null && (protectedSet.SourceDecodedBufferHash != facts.DecodedBufferHash
                || protectedIndices.Any(i => (uint)i >= (uint)points.Length)
                || HashPoints(protectedIndices.Select(i => points[i].Original)) != protectedSet.SourcePositionHash
                || HashPoints(protectedIndices.Select(i => points[i].Predicted)) != protectedSet.ExpectedPositionHash
                || protectedIndices.Any(i => !SameWords(points[i].Original, points[i].Predicted))))
                throw Invalid("Protected source/predicted position words differ.");
            if (selected is not null)
                try { RegionTriangleGuard.Validate(source.Points, points.Select(p => p.Predicted).ToArray(), source.TriangleIndices); }
                catch (ArgumentException) { throw Invalid("Predicted triangle validity failed."); }
            buffers.Add(new(facts, source.MemberId, source.DrawCallIds.ToArray(), points, source.TriangleIndices.ToArray(), protectedIndices));
        }
        var measures = buffers.GroupBy(b => b.Source.Lod).Select(g => Measure(g.Key, g.ToArray(), target)).ToArray();
        var preview = new DirectionalSelectionPreview(plan.InputHash, plan.Fingerprint, target.TargetFingerprint, plan.InputHash,
            target.DirectionalTransform, target.Pivot, buffers, measures);
        return preview with { PreviewFingerprint = ComputeFingerprint(preview) };
    }

    private static void CheckPrediction(PlannedCoordinatedBuffer expected, EllipsoidPreviewPoint[] points)
    {
        var mask = new byte[checked(points.Length * 8)]; var weights = new byte[checked(points.Length * 12)];
        var counts = new int[3]; int changed = 0; float maximum = 0;
        for (var i = 0; i < points.Length; i++)
        {
            var p = points[i]; var region = Enum.Parse<EllipsoidMembership>(p.Membership, true); counts[(int)region]++;
            BinaryPrimitives.WriteInt32LittleEndian(mask.AsSpan(i * 8), i);
            BinaryPrimitives.WriteInt32LittleEndian(mask.AsSpan(i * 8 + 4), (int)region);
            BinaryPrimitives.WriteInt32LittleEndian(weights.AsSpan(i * 12), i);
            BinaryPrimitives.WriteInt64LittleEndian(weights.AsSpan(i * 12 + 4), BitConverter.DoubleToInt64Bits(p.Weight));
            if (!SameWords(p.Original, p.Predicted)) changed++;
            maximum = Math.Max(maximum, ConservativePointDistance.RoundUp(p.Original, p.Predicted));
        }
        if (HashPoints(points.Select(p => p.Original)) != expected.InputPositionHash || HashPoints(points.Select(p => p.Predicted)) != expected.ExpectedPositionHash
            || ContentHash.Compute(mask) != expected.MaskHash || ContentHash.Compute(weights) != expected.WeightHash
            || counts[0] != expected.FullVertexCount || counts[1] != expected.TransitionVertexCount || counts[2] != expected.PinnedVertexCount
            || changed != expected.ChangedPositionCount || maximum != expected.MaximumDisplacement
            || Bounds(points.Select(p => p.Original)) != expected.BeforeBounds || Bounds(points.Select(p => p.Predicted)) != expected.ExpectedAfterBounds)
            throw Invalid("Independently predicted words, masks, weights, counts or displacement differ from the plan.");
    }

    private static DirectionalPreviewMeasurements Measure(int lod, DirectionalPreviewBuffer[] buffers, PlannedDirectionalTransformTarget target)
    {
        var selected = buffers.Where(b => b.Source.Selected).SelectMany(b => b.Points).ToArray();
        var changed = selected.Where(p => !SameWords(p.Original, p.Predicted)).ToArray();
        var extent = Span(buffers.SelectMany(b => b.Points.Select(p => p.Original)));
        var diagonal = Math.Sqrt((double)extent.X * extent.X + (double)extent.Y * extent.Y + (double)extent.Z * extent.Z);
        if (!double.IsFinite(diagonal) || diagonal <= 0 || changed.Length == 0) throw Invalid("Model-relative measurement is undefined.");
        var maximum = target.Buffers.Where(b => b.Lod == lod).Max(b => b.MaximumDisplacement);
        return new(lod, selected.Length, buffers.Where(b => !b.Source.Selected).Sum(b => b.Points.Count), changed.Length,
            selected.Count(p => p.Membership == "pinned"), buffers.Sum(b => b.ProtectedIndices.Count), maximum, diagonal,
            maximum / diagonal, Span(selected.Select(p => p.Original)), Span(selected.Select(p => p.Predicted)),
            Span(changed.Select(p => p.Original)), Span(changed.Select(p => p.Predicted)));
    }

    public static ContentHash ComputeFingerprint(DirectionalSelectionPreview preview) => ContentHash.Compute(JsonDefaults.SerializeToUtf8(new
    {
        ProjectionProfile = DirectionalSelectionPreview.ProjectionProfile,
        preview.InputHash,
        preview.PlanFingerprint,
        preview.TargetFingerprint,
        preview.Transform,
        preview.Pivot,
        preview.Buffers,
        preview.Measurements
    }));
    private static Point3 Span(IEnumerable<Point3> points)
    {
        var b = Bounds3.FromPoints(points.ToArray());
        float x = b.Max.X - b.Min.X, y = b.Max.Y - b.Min.Y, z = b.Max.Z - b.Min.Z;
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) throw Invalid("Preview extent cannot be represented finitely.");
        return new(x, y, z);
    }
    private static GeometryBounds Bounds(IEnumerable<Point3> points)
    { var b = Bounds3.FromPoints(points.ToArray()); return new(new() { X = b.Min.X, Y = b.Min.Y, Z = b.Min.Z }, new() { X = b.Max.X, Y = b.Max.Y, Z = b.Max.Z }); }
    internal static ContentHash HashPoints(IEnumerable<Point3> points)
    {
        var values = points.ToArray(); var bytes = new byte[checked(values.Length * 12)];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 12), values[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 12 + 4), values[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 12 + 8), values[i].Z);
        }
        return ContentHash.Compute(bytes);
    }
    private static bool SameWords(Point3 a, Point3 b) => BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
        && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y) && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);
    private static Point3 Point(TransformVector3 p) => new(p.X, p.Y, p.Z);
    private static bool Finite(Point3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static S2ModKitException Invalid(string message) => Errors.Verification("DIRECTIONAL_PREVIEW_INVALID", message, "Regenerate the complete preview from immutable input and its exact directional plan.");
}
