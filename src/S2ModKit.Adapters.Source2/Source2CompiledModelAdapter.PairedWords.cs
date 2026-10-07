using System.Buffers.Binary;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    internal sealed record PairedWordCalculation(DirectionalWordCalculation Combined, PairedBufferDispatch Dispatch);

    private static PairedDirectionalEllipsoidScale PairedMath(PairedDirectionalVisualTransform intent, float limit)
    {
        DirectionalEllipsoidScale Field(PairedDirectionalField f) => new(ToAffinePoint(f.Field.Pivot.Point!),
            ToAffinePoint(f.Field.OuterRadii), f.Field.CoreFraction, ToAffinePoint(f.Field.Scale), limit);
        return new(Field(intent.Fields[0]), Field(intent.Fields[1]));
    }

    // Both qualified single-field calculations consume the same immutable input. Their disjoint
    // source memberships select final words; outputs are never fed into a second field.
    internal static PairedWordCalculation CalculatePairedWords(ReadOnlySpan<byte> decoded, PositionLayout position,
        PackedFrameLayout frame, int count, PairedDirectionalEllipsoidScale math, IReadOnlyList<string> ids, string member, int lod)
    {
        if (ids.Count != 2 || ids[0] == ids[1]) throw new ArgumentException("Require two unique field identities.");
        var fields = new[] { math.First, math.Second };
        var sourceBytes = decoded.ToArray();
        var calculations = fields.Select(f => CalculateDirectionalWords(sourceBytes, position, frame, count, f)).ToArray();
        var bytes = decoded.ToArray(); var points = new Point3[count]; var dispatch = new List<PairedFieldDispatch>();
        var active = new HashSet<int>();
        for (var fi = 0; fi < 2; fi++)
        {
            var c = calculations[fi]; var core = new List<int>(); var transition = new List<int>();
            for (var i = 0; i < count; i++)
            {
                var o = checked(i * position.Stride + position.Offset);
                var source = new Point3(BinaryPrimitives.ReadSingleLittleEndian(decoded[o..]),
                    BinaryPrimitives.ReadSingleLittleEndian(decoded[(o + 4)..]), BinaryPrimitives.ReadSingleLittleEndian(decoded[(o + 8)..]));
                var membership = fields[fi].Evaluate(source).Membership;
                if (membership == EllipsoidMembership.Pinned) continue;
                if (!active.Add(i)) throw DirectionalFailure("PAIRED_DISPATCH_INVALID", "Source interiors overlap.");
                (membership == EllipsoidMembership.Core ? core : transition).Add(i);
                c.Bytes.AsSpan(o, 12).CopyTo(bytes.AsSpan(o, 12));
                var fo = checked(i * frame.Stride + frame.Offset);
                c.Bytes.AsSpan(fo, 4).CopyTo(bytes.AsSpan(fo, 4));
            }
            var row = new PairedFieldDispatch(ids[fi], core, transition, c.ChangedPositions, c.ChangedFrames, default, c.WeightHash);
            dispatch.Add(row with { MembershipHash = PairedContractValidator.FieldMembershipHash(count, row) });
        }
        for (var i = 0; i < count; i++)
        {
            var o = checked(i * position.Stride + position.Offset);
            points[i] = new(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(o)),
                BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(o + 4)), BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(o + 8)));
        }
        var pinned = Enumerable.Range(0, count).Where(i => !active.Contains(i)).ToArray();
        var mask = PairedContractValidator.DispatchMembershipHash(count, dispatch);
        var weight = PairedContractValidator.DispatchWeightHash(dispatch);
        var combined = new DirectionalWordCalculation(bytes, points, mask, weight, dispatch.Sum(f => f.CoreIndices.Count),
            dispatch.Sum(f => f.TransitionIndices.Count), pinned.Length, dispatch.SelectMany(f => f.ChangedPositionIndices).Order().ToArray(),
            dispatch.SelectMany(f => f.ChangedFrameIndices).Order().ToArray(), pinned, calculations.Max(c => c.MaximumDisplacement));
        return new(combined, new(member, lod, dispatch, pinned, mask, weight));
    }

    private static PairedSourceTriangleFacts PairedTriangles(CoordinatedResolvedBuffer member, DirectionalWordCalculation words)
    {
        var p = member.Profile;
        var source = p.SelectedVertices.Select(v => Source2GeometryAnalyzer.ReadPosition(p.Vertices, v)).ToArray();
        var calls = p.Mesh.GeometryAnalysis!.DrawCalls.Where(d => d.Snapshot.VertexBufferOrdinal == p.Vertices.Snapshot.Ordinal)
            .Select(d => (Source: p.Mesh.DrawCalls.Single(c => c.Snapshot.Id == d.Snapshot.DrawCallId).Snapshot, d.Snapshot.BaseVertex))
            .OrderBy(d => d.Source.IndexStart).ToArray();
        if (calls.GroupBy(d => (d.Source.IndexStart, d.Source.IndexCount)).Any(g => g.Select(d => d.BaseVertex).Distinct().Count() != 1))
            throw DirectionalFailure("PAIRED_TRIANGLE_INVALID", "An aliased index range has ambiguous base vertices.");
        var triangles = calls.DistinctBy(d => (d.Source.IndexStart, d.Source.IndexCount)).SelectMany(d =>
            Enumerable.Range(checked((int)d.Source.IndexStart), checked((int)d.Source.IndexCount))
                .Select(i => checked((int)p.Indices.Indices[i] + d.BaseVertex))).ToArray();
        SourceCoincidenceTriangleAudit audit;
        try { audit = SourceCoincidenceTriangleGuard.Validate(source, words.Points, triangles); }
        catch (ArgumentException e) { throw DirectionalFailure("PAIRED_TRIANGLE_INVALID", e.Message); }
        var changed = words.ChangedPositions.ToHashSet();
        var changedValid = Enumerable.Range(0, audit.TriangleCount).Count(i => audit.SourcePartitions[i] == 0
            && triangles.Skip(i * 3).Take(3).Any(changed.Contains));
        var partitionHash = ContentHash.Compute(audit.SourcePartitions.ToArray());
        return new(member.MemberId, p.Mesh.Lod, p.Indices.Snapshot.DecodedHash, triangles,
            DirectionalContractValidator.VertexSetHash(triangles), audit.SourcePartitions, partitionHash, partitionHash,
            audit.ValidTriangleCount, changedValid, audit.CollapsedTriangleCount, audit.TouchedCollapsedTriangleCount);
    }
}
