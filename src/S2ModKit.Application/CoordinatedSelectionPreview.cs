using System.Buffers.Binary;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Application;

public interface ICoordinatedPreviewGeometryReader
{
    Task<CoordinatedPreviewGeometry> ReadCoordinatedPreviewGeometryAsync(ArtifactContent input, MutationPlan plan, CancellationToken token = default);
}
public sealed record CoordinatedPreviewBufferGeometry(string MemberId, int Lod, int MeshOrdinal, int VertexBufferOrdinal,
    IReadOnlyList<string> DrawCallIds, IReadOnlyList<Point3> Points, IReadOnlyList<int> TriangleIndices);
public sealed record CoordinatedPreviewContext(int MeshOrdinal, int VertexBufferOrdinal, IReadOnlyList<Point3> Points)
{
    public IReadOnlyList<string> DrawCallIds { get; init; } = [];
    public IReadOnlyList<string> MaterialPaths { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceLabel { get; init; }
}
public sealed record CoordinatedPreviewLodGeometry(int Lod, IReadOnlyList<CoordinatedPreviewBufferGeometry> Buffers, IReadOnlyList<CoordinatedPreviewContext> Context);
public sealed record CoordinatedPreviewGeometry(ContentHash InputHash, ContentHash PlanFingerprint, IReadOnlyList<CoordinatedPreviewLodGeometry> Lods);
public sealed record CoordinatedPreviewBuffer(PlannedCoordinatedBuffer Buffer, IReadOnlyList<string> DrawCallIds,
    IReadOnlyList<EllipsoidPreviewPoint> Points, IReadOnlyList<int> TriangleIndices);
public sealed record CoordinatedPreviewLod(int Lod, IReadOnlyList<CoordinatedPreviewBuffer> Buffers, IReadOnlyList<CoordinatedPreviewContext> Context);
public sealed record CoordinatedSelectionPreview(ContentHash InputHash, ContentHash PlanFingerprint, ContentHash TargetFingerprint,
    ContentHash PreviewFingerprint, CoordinatedVisualTransform Transform, RuntimeMetadataPolicy Policy, ZeroBoneBoxPolicy ZeroBoneBoxPolicy,
    ZeroRenderSpherePolicy ZeroRenderSpherePolicy, IReadOnlyList<CoordinatedPreviewLod> Lods);

public static class CoordinatedSelectionPreviewBuilder
{
    public static CoordinatedSelectionPreview Create(MutationPlan plan, CoordinatedPreviewGeometry geometry, ModelSnapshot sourceModel)
    {
        _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(plan));
        if (plan.SchemaVersion != 5 || geometry.InputHash != plan.InputHash || geometry.PlanFingerprint != plan.Fingerprint || sourceModel.Artifact.ContentHash != plan.InputHash)
            throw Invalid("Preview source or plan identity drifted.");
        var operation = plan.Operations.Single(); var target = operation.CoordinatedTransformTarget!;
        if (!geometry.Lods.Select(l => l.Lod).SequenceEqual(target.Buffers.Select(b => b.Lod).Distinct())
            || geometry.Lods.Sum(l => l.Buffers.Sum(b => (long)b.Points.Count) + l.Context.Sum(c => (long)c.Points.Count)) > EllipsoidSelectionPreview.MaximumPoints
            || geometry.Lods.Sum(l => l.Buffers.Sum(b => (long)b.TriangleIndices.Count)) > EllipsoidSelectionPreview.MaximumTriangleIndices)
            throw Invalid("Preview coverage or complete geometry budget is invalid; no points may be discarded.");
        var math = new CoordinatedFieldMath(target.CoordinatedTransform.Field, target.DisplacementLimit);
        var lods = new List<CoordinatedPreviewLod>();
        foreach (var source in geometry.Lods)
        {
            var expected = target.Buffers.Where(b => b.Lod == source.Lod).ToArray();
            var sourceLod = sourceModel.Lods.SingleOrDefault(l => l.Level == source.Lod) ?? throw Invalid("Source LOD inventory is incomplete.");
            if (sourceLod.Meshes.Any(m => m.Geometry is not { Status: "ready" })) throw Invalid("Source context inventory is not completely decoded.");
            var excluded = sourceLod.Meshes.SelectMany(m => m.Geometry!.VertexBuffers.Select(b => (m.MeshOrdinal, b.Ordinal, b.VertexCount)))
                .Where(b => !expected.Any(e => e.MeshOrdinal == b.MeshOrdinal && e.VertexBufferOrdinal == b.Ordinal)).OrderBy(b => b.MeshOrdinal).ThenBy(b => b.Ordinal);
            if (!source.Context.Select(c => (c.MeshOrdinal, c.VertexBufferOrdinal, c.Points.Count)).SequenceEqual(excluded))
                throw Invalid("Excluded sibling buffer inventory or point coverage differs from immutable inspection.");
            foreach (var context in source.Context)
            {
                var mesh = sourceLod.Meshes.Single(m => m.MeshOrdinal == context.MeshOrdinal);
                var ids = mesh.Geometry!.DrawCalls.Where(d => d.VertexBufferOrdinal == context.VertexBufferOrdinal).Select(d => d.DrawCallId).Order(StringComparer.Ordinal).ToArray();
                var materials = mesh.DrawCalls.Where(d => ids.Contains(d.Id)).Select(d => d.MaterialPath).Distinct().Order(StringComparer.Ordinal).ToArray();
                if (!ids.SequenceEqual(context.DrawCallIds) || !materials.SequenceEqual(context.MaterialPaths) || context.SourceLabel != mesh.MechanicalLineage?.SourceLabel)
                    throw Invalid("Excluded sibling draw calls, materials or advisory source label drifted.");
            }
            if (!source.Buffers.Select(b => (b.MemberId, b.MeshOrdinal, b.VertexBufferOrdinal)).SequenceEqual(expected.Select(b => (b.MemberId, b.MeshOrdinal, b.VertexBufferOrdinal)))
                || source.Context.Select(c => (c.MeshOrdinal, c.VertexBufferOrdinal)).Distinct().Count() != source.Context.Count
                || source.Context.Any(c => c.MeshOrdinal < 0 || c.VertexBufferOrdinal < 0 || c.Points.Count == 0 || c.Points.Any(p => !Finite(p))
                    || expected.Any(b => b.MeshOrdinal == c.MeshOrdinal && b.VertexBufferOrdinal == c.VertexBufferOrdinal)))
                throw Invalid("Participating buffers or excluded context overlap or differ from the plan.");
            var buffers = new List<CoordinatedPreviewBuffer>();
            foreach (var buffer in source.Buffers)
            {
                var facts = expected.Single(b => b.MemberId == buffer.MemberId);
                var ids = target.CoordinatedTransform.Members.Single(m => m.MemberId == buffer.MemberId).Lods.Single(l => l.Lod == source.Lod).DrawCallIds;
                if (!ids.SequenceEqual(buffer.DrawCallIds) || buffer.Points.Count != facts.VertexCount || buffer.Points.Any(p => !Finite(p))
                    || buffer.TriangleIndices.Count == 0 || buffer.TriangleIndices.Count % 3 != 0
                    || buffer.TriangleIndices.Count != operation.SelectedDrawCalls.Where(c => ids.Contains(c.DrawCallId)).Sum(c => c.IndexCount)
                    || buffer.TriangleIndices.Any(i => (uint)i >= (uint)buffer.Points.Count))
                    throw Invalid("Member topology, points or exact draw-call coverage drifted.");
                var mask = new byte[checked(buffer.Points.Count * 8)]; var weights = new byte[checked(buffer.Points.Count * 12)];
                var original = new byte[weights.Length]; var predicted = new byte[weights.Length];
                var points = new EllipsoidPreviewPoint[buffer.Points.Count]; var counts = new int[3]; int changes = 0; float maximum = 0;
                for (var i = 0; i < points.Length; i++)
                {
                    var input = buffer.Points[i]; var result = math.Evaluate(input);
                    points[i] = new(input, result.Position, result.Membership.ToString().ToLowerInvariant(), result.Weight);
                    counts[(int)result.Membership]++;
                    BinaryPrimitives.WriteInt32LittleEndian(mask.AsSpan(i * 8), i);
                    BinaryPrimitives.WriteInt32LittleEndian(mask.AsSpan(i * 8 + 4), (int)result.Membership);
                    BinaryPrimitives.WriteInt32LittleEndian(weights.AsSpan(i * 12), i);
                    BinaryPrimitives.WriteInt64LittleEndian(weights.AsSpan(i * 12 + 4), BitConverter.DoubleToInt64Bits(result.Weight));
                    WritePoint(original.AsSpan(i * 12), input); WritePoint(predicted.AsSpan(i * 12), result.Position);
                    if (!original.AsSpan(i * 12, 12).SequenceEqual(predicted.AsSpan(i * 12, 12))) changes++;
                    maximum = Math.Max(maximum, result.MaximumDisplacement);
                }
                if (ContentHash.Compute(mask) != facts.MaskHash || ContentHash.Compute(weights) != facts.WeightHash
                    || ContentHash.Compute(original) != facts.InputPositionHash || ContentHash.Compute(predicted) != facts.ExpectedPositionHash
                    || counts[0] != facts.FullVertexCount || counts[1] != facts.TransitionVertexCount || counts[2] != facts.PinnedVertexCount
                    || changes != facts.ChangedPositionCount || maximum != facts.MaximumDisplacement
                    || Bounds(buffer.Points) != facts.BeforeBounds || Bounds(points.Select(p => p.Predicted).ToArray()) != facts.ExpectedAfterBounds)
                    throw Invalid("Common-field predicted words, masks, weights, counts or bounds disagree with the exact plan.");
                try { RegionTriangleGuard.Validate(buffer.Points, points.Select(p => p.Predicted).ToArray(), buffer.TriangleIndices); }
                catch (ArgumentException) { throw Invalid("Source/predicted triangles are degenerate or reversed."); }
                buffers.Add(new(facts, ids.ToArray(), points, buffer.TriangleIndices.ToArray()));
            }
            lods.Add(new(source.Lod, buffers, source.Context.Select(c => c with { Points = c.Points.ToArray() }).ToArray()));
        }
        var preview = new CoordinatedSelectionPreview(plan.InputHash, plan.Fingerprint, target.TargetFingerprint, plan.InputHash,
            target.CoordinatedTransform, target.RuntimeMetadataPolicy, target.ZeroBoneBoxPolicy, target.ZeroRenderSpherePolicy, lods);
        return preview with { PreviewFingerprint = ComputeFingerprint(preview) };
    }

    public static ContentHash ComputeFingerprint(CoordinatedSelectionPreview preview) => ContentHash.Compute(JsonDefaults.SerializeToUtf8(new
    {
        Profile = "coordinated_orthographic_selection@1",
        preview.InputHash,
        preview.PlanFingerprint,
        preview.TargetFingerprint,
        preview.Transform,
        preview.Policy,
        preview.ZeroBoneBoxPolicy,
        preview.ZeroRenderSpherePolicy,
        preview.Lods
    }));
    private static void WritePoint(Span<byte> bytes, Point3 p)
    { BinaryPrimitives.WriteSingleLittleEndian(bytes, p.X); BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], p.Y); BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], p.Z); }
    private static GeometryBounds Bounds(IReadOnlyList<Point3> points)
    { var b = Bounds3.FromPoints(points); return new(new() { X = b.Min.X, Y = b.Min.Y, Z = b.Min.Z }, new() { X = b.Max.X, Y = b.Max.Y, Z = b.Max.Z }); }
    private static bool Finite(Point3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static S2ModKitException Invalid(string message) => Errors.Verification("COORDINATED_PREVIEW_INVALID", message, "Regenerate from immutable input and the exact accepted common-field plan.");
}
