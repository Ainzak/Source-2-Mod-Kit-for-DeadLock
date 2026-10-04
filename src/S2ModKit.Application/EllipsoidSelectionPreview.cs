using System.Buffers.Binary;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Application;

/// <summary>Reads immutable model-space geometry for a frozen plan; never writes a resource.</summary>
public interface IEllipsoidPreviewGeometryReader
{
    Task<EllipsoidPreviewGeometry> ReadEllipsoidPreviewGeometryAsync(ArtifactContent input, MutationPlan plan, CancellationToken cancellationToken = default);
}

public sealed record EllipsoidPreviewContext(int MeshOrdinal, int VertexBufferOrdinal, IReadOnlyList<Point3> Points);
public sealed record EllipsoidPreviewLodGeometry(int Lod, int MeshOrdinal, int VertexBufferOrdinal,
    IReadOnlyList<string> DrawCallIds, IReadOnlyList<Point3> Points, IReadOnlyList<int> TriangleIndices, IReadOnlyList<EllipsoidPreviewContext> Context);
public sealed record EllipsoidPreviewGeometry(ContentHash InputHash, ContentHash PlanFingerprint, IReadOnlyList<EllipsoidPreviewLodGeometry> Lods);
public sealed record EllipsoidPreviewPoint(Point3 Original, Point3 Predicted, string Membership, double Weight);
public sealed record EllipsoidSelectionPreviewLod(PlannedEllipsoidBuffer Buffer, IReadOnlyList<string> DrawCallIds,
    IReadOnlyList<EllipsoidPreviewPoint> Points, IReadOnlyList<int> TriangleIndices, IReadOnlyList<EllipsoidPreviewContext> Context);
public sealed record EllipsoidSelectionPreview(ContentHash InputHash, ContentHash PlanFingerprint, ContentHash TargetFingerprint,
    ContentHash PreviewFingerprint, string ResourcePath, EllipsoidVisualTransform LocalTransform, IReadOnlyList<EllipsoidSelectionPreviewLod> Lods)
{
    public const string ProjectionProfile = "orthographic_selection@1";
    public const int MaximumLods = 8;
    public const int MaximumPoints = 1_000_000;
    public const int MaximumTriangleIndices = 3_000_000;
    public string ProofLevel { get; } = "bind_space_prediction";
}

public sealed record EllipsoidPreviewLodSummary(int Lod, int MeshOrdinal, int ResourceBlockIndex, int VertexBufferOrdinal,
    int VertexResourceBlockIndex, IReadOnlyList<string> DrawCallIds, int VertexCount, int TriangleCount,
    int ContextBufferCount, int ContextPointCount, int CoreVertexCount, int TransitionVertexCount, int PinnedVertexCount,
    int ChangedPositionCount, int ChangedFrameCount, ContentHash MaskHash, ContentHash WeightHash,
    ContentHash InputPositionHash, ContentHash ExpectedPositionHash, float MaximumDisplacement)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<EllipsoidFieldMask>? MirroredMasks { get; init; }
}
public sealed record EllipsoidSelectionPreviewSummary(int SchemaVersion, string Kind, string ProjectionProfile, string ProofLevel,
    ContentHash InputHash, ContentHash PlanFingerprint, ContentHash TargetFingerprint, ContentHash PreviewFingerprint,
    string ResourcePath, EllipsoidVisualTransform LocalTransform, IReadOnlyList<EllipsoidPreviewLodSummary> Lods,
    ContentHash ContactSheetHash, IReadOnlyList<string> Limitations);
public sealed record EllipsoidPreviewArtifacts(ReadOnlyMemory<byte> SummaryJson, ReadOnlyMemory<byte> ContactSheetSvg,
    ContentHash PreviewFingerprint, ContentHash SummaryHash, ContentHash ContactSheetHash);
public sealed record EllipsoidPreviewPublication(string Directory, string SummaryPath, string ContactSheetPath,
    ContentHash PreviewFingerprint, ContentHash SummaryHash, ContentHash ContactSheetHash);

public static class EllipsoidSelectionPreviewBuilder
{
    public static EllipsoidSelectionPreview Create(MutationPlan plan, EllipsoidPreviewGeometry geometry)
    {
        _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(plan));
        if (plan.SchemaVersion != 4 || geometry.InputHash != plan.InputHash || geometry.PlanFingerprint != plan.Fingerprint)
            throw Invalid("Preview source or plan identity drifted.");
        var target = plan.Operations.Single().EllipsoidTransformTarget!;
        if (geometry.Lods.Count is < 1 or > EllipsoidSelectionPreview.MaximumLods
            || !geometry.Lods.Select(l => l.Lod).SequenceEqual(target.Buffers.Select(b => b.Lod))) throw Invalid("Preview LOD coverage is incomplete or out of order.");
        if (geometry.Lods.Sum(l => (long)l.Points.Count + l.Context.Sum(c => (long)c.Points.Count)) > EllipsoidSelectionPreview.MaximumPoints
            || geometry.Lods.Sum(l => (long)l.TriangleIndices.Count) > EllipsoidSelectionPreview.MaximumTriangleIndices)
            throw Invalid("Preview geometry exceeds the bounded point/topology budget; no points may be silently discarded.");
        var math = EllipsoidFieldMath.Create(target.LocalTransform, target.DisplacementLimit);
        var lods = new List<EllipsoidSelectionPreviewLod>();
        foreach (var source in geometry.Lods)
        {
            var buffer = target.Buffers.Single(b => b.Lod == source.Lod);
            var ids = plan.Operations[0].SelectedDrawCalls.Where(c => c.Lod == source.Lod).Select(c => c.DrawCallId).Order(StringComparer.Ordinal).ToArray();
            if (source.MeshOrdinal != buffer.MeshOrdinal || source.VertexBufferOrdinal != buffer.VertexBufferOrdinal
                || source.Points.Count != buffer.VertexCount || !ids.SequenceEqual(source.DrawCallIds)
                || source.TriangleIndices.Count == 0 || source.TriangleIndices.Count % 3 != 0
                || source.TriangleIndices.Count != plan.Operations[0].SelectedDrawCalls.Where(c => c.Lod == source.Lod).Sum(c => c.IndexCount)
                || source.Points.Any(p => !Finite(p))
                || source.TriangleIndices.Any(i => (uint)i >= (uint)source.Points.Count)
                || source.Context.Any(c => c.MeshOrdinal < 0 || c.VertexBufferOrdinal < 0 || c.Points.Any(p => !Finite(p)))
                || source.Context.Select(c => (c.MeshOrdinal, c.VertexBufferOrdinal)).Distinct().Count() != source.Context.Count
                || source.Context.Any(c => c.MeshOrdinal == source.MeshOrdinal && c.VertexBufferOrdinal == source.VertexBufferOrdinal))
                throw Invalid("Enclosing component, topology or non-editable context identities drifted.");
            var mask = new byte[checked(source.Points.Count * 8)];
            var weights = new byte[checked(source.Points.Count * 12)];
            var originalWords = new byte[checked(source.Points.Count * 12)];
            var predictedWords = new byte[originalWords.Length];
            var points = new EllipsoidPreviewPoint[source.Points.Count];
            int core = 0, transition = 0, pinned = 0, changed = 0;
            float maximum = 0;
            for (var index = 0; index < points.Length; index++)
            {
                var original = source.Points[index];
                var result = math.Evaluate(original);
                var membership = result.Membership switch { EllipsoidMembership.Core => "core", EllipsoidMembership.Transition => "transition", _ => "pinned" };
                points[index] = new(original, result.Position, membership, result.Weight);
                if (membership == "core") core++; else if (membership == "transition") transition++; else pinned++;
                WritePoint(originalWords.AsSpan(index * 12), original); WritePoint(predictedWords.AsSpan(index * 12), result.Position);
                if (!originalWords.AsSpan(index * 12, 12).SequenceEqual(predictedWords.AsSpan(index * 12, 12))) changed++;
                maximum = Math.Max(maximum, result.MaximumDisplacement);
                BinaryPrimitives.WriteInt32LittleEndian(mask.AsSpan(index * 8), index);
                BinaryPrimitives.WriteInt32LittleEndian(mask.AsSpan(index * 8 + 4), (int)result.Membership);
                BinaryPrimitives.WriteInt32LittleEndian(weights.AsSpan(index * 12), index);
                BinaryPrimitives.WriteInt64LittleEndian(weights.AsSpan(index * 12 + 4), BitConverter.DoubleToInt64Bits(result.Weight));
            }
            if (math is MirroredEllipsoidScale pair)
            {
                var verified = new List<EllipsoidFieldMask>();
                foreach (var member in new[] { pair.Base, pair.Reflected })
                {
                    var memberMask = new byte[source.Points.Count * 8]; var memberWeights = new byte[source.Points.Count * 12];
                    var counts = new int[3]; var effects = 0;
                    var words = new byte[24];
                    for (var i = 0; i < source.Points.Count; i++)
                    {
                        var result = member.Evaluate(source.Points[i]); counts[(int)result.Membership]++;
                        BinaryPrimitives.WriteInt32LittleEndian(memberMask.AsSpan(i * 8), i);
                        BinaryPrimitives.WriteInt32LittleEndian(memberMask.AsSpan(i * 8 + 4), (int)result.Membership);
                        BinaryPrimitives.WriteInt32LittleEndian(memberWeights.AsSpan(i * 12), i);
                        BinaryPrimitives.WriteInt64LittleEndian(memberWeights.AsSpan(i * 12 + 4), BitConverter.DoubleToInt64Bits(result.Weight));
                        WritePoint(words, source.Points[i]); WritePoint(words.AsSpan(12), result.Position);
                        if (!words.AsSpan(0, 12).SequenceEqual(words.AsSpan(12))) effects++;
                    }
                    verified.Add(new(ContentHash.Compute(memberMask), ContentHash.Compute(memberWeights), counts[0], counts[1], counts[2], effects));
                }
                if (verified.Any(m => m.ChangedPositionCount == 0) || JsonDefaults.Serialize(verified) != JsonDefaults.Serialize(buffer.MirroredMasks))
                    throw Invalid("Preview paired masks or effects disagree with the exact plan.");
            }
            if (ContentHash.Compute(mask) != buffer.MaskHash || ContentHash.Compute(weights) != buffer.WeightHash
                || ContentHash.Compute(originalWords) != buffer.InputPositionHash || ContentHash.Compute(predictedWords) != buffer.ExpectedPositionHash
                || core != buffer.CoreVertexCount || transition != buffer.TransitionVertexCount || pinned != buffer.PinnedVertexCount
                || changed != buffer.ChangedPositionCount || maximum != buffer.MaximumDisplacement
                || ToBounds(source.Points) != buffer.BeforeBounds || ToBounds(points.Select(p => p.Predicted).ToArray()) != buffer.ExpectedAfterBounds)
                throw Invalid("Preview membership, weights or predicted position words disagree with the exact plan.");
            try { RegionTriangleGuard.Validate(source.Points, points.Select(p => p.Predicted).ToArray(), source.TriangleIndices); }
            catch (ArgumentException) { throw Invalid("Preview source/predicted triangles are degenerate or reverse their source orientation."); }
            lods.Add(new(buffer, ids, points, source.TriangleIndices.ToArray(), source.Context.Select(c => c with { Points = c.Points.ToArray() }).ToArray()));
        }
        var preview = new EllipsoidSelectionPreview(plan.InputHash, plan.Fingerprint, target.TargetFingerprint, plan.InputHash,
            target.Buffers[0].ResourcePath, target.LocalTransform, lods);
        return preview with { PreviewFingerprint = ComputeFingerprint(preview) };
    }

    public static ContentHash ComputeFingerprint(EllipsoidSelectionPreview preview) => ContentHash.Compute(JsonDefaults.SerializeToUtf8(new
    {
        Projection = EllipsoidSelectionPreview.ProjectionProfile,
        preview.InputHash,
        preview.PlanFingerprint,
        preview.TargetFingerprint,
        preview.ResourcePath,
        preview.LocalTransform,
        preview.Lods,
    }));

    private static void WritePoint(Span<byte> destination, Point3 p)
    {
        BinaryPrimitives.WriteSingleLittleEndian(destination, p.X);
        BinaryPrimitives.WriteSingleLittleEndian(destination[4..], p.Y);
        BinaryPrimitives.WriteSingleLittleEndian(destination[8..], p.Z);
    }
    private static GeometryBounds ToBounds(IReadOnlyList<Point3> points)
    {
        var bounds = Bounds3.FromPoints(points);
        return new(new() { X = bounds.Min.X, Y = bounds.Min.Y, Z = bounds.Min.Z }, new() { X = bounds.Max.X, Y = bounds.Max.Y, Z = bounds.Max.Z });
    }
    private static bool Finite(Point3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static S2ModKitException Invalid(string message) => Errors.Verification("ELLIPSOID_PREVIEW_INVALID", message, "Regenerate the read-only preview from the immutable input and exact accepted plan.");
}
