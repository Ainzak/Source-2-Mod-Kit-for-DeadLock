using System.Globalization;
using System.Numerics;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static PlannedPairSeparation AuditPairSeparation(IReadOnlyList<PairedDirectionalField> fields)
    {
        static BigInteger Q(float value) => new(Math.ScaleB((double)value, 149));
        static DirectionalRationalBound Bound(BigInteger value)
        {
            var denominator = BigInteger.One << 149; var divisor = BigInteger.GreatestCommonDivisor(value, denominator);
            return new((value / divisor).ToString(CultureInfo.InvariantCulture), (denominator / divisor).ToString(CultureInfo.InvariantCulture));
        }
        var a = fields[0].Field; var b = fields[1].Field;
        float[] ca = [a.Pivot.Point!.X, a.Pivot.Point.Y, a.Pivot.Point.Z], cb = [b.Pivot.Point!.X, b.Pivot.Point.Y, b.Pivot.Point.Z];
        float[] ra = [a.OuterRadii.X, a.OuterRadii.Y, a.OuterRadii.Z], rb = [b.OuterRadii.X, b.OuterRadii.Y, b.OuterRadii.Z];
        string[] axes = ["x", "y", "z"];
        for (var axis = 0; axis < 3; axis++)
        {
            var distance = BigInteger.Abs(Q(ca[axis]) - Q(cb[axis])); var radii = Q(ra[axis]) + Q(rb[axis]);
            if (distance >= radii) return new("axis_projection_exact", 1, axes[axis], Bound(distance), Bound(radii), Bound(distance - radii));
        }
        throw PairedDrift("No original-source axis certifies exact pair separation.");
    }

    private static PairedSourceTriangleFacts AuditPairedTriangles(CoordinatedResolvedBuffer member, CoordinatedResolvedBuffer actual)
    {
        var p = member.Profile;
        var ranges = p.Mesh.GeometryAnalysis!.DrawCalls.Where(d => d.Snapshot.VertexBufferOrdinal == p.Vertices.Snapshot.Ordinal)
            .Select(d => (Source: p.Mesh.DrawCalls.Single(c => c.Snapshot.Id == d.Snapshot.DrawCallId).Snapshot, d.Snapshot.BaseVertex))
            .OrderBy(d => d.Source.IndexStart).ToArray();
        if (ranges.GroupBy(r => (r.Source.IndexStart, r.Source.IndexCount)).Any(g => g.Select(r => r.BaseVertex).Distinct().Count() != 1))
            throw PairedDrift("A source index range has conflicting base vertices.");
        var indices = new List<int>(); var seen = new HashSet<(long, long)>();
        foreach (var range in ranges)
        {
            if (!seen.Add((range.Source.IndexStart, range.Source.IndexCount))) continue;
            for (var i = range.Source.IndexStart; i < range.Source.IndexStart + range.Source.IndexCount; i++)
                indices.Add(checked((int)p.Indices.Indices[checked((int)i)] + range.BaseVertex));
        }
        var before = Enumerable.Range(0, p.Vertices.Snapshot.VertexCount).Select(i => Source2GeometryAnalyzer.ReadPosition(p.Vertices, i)).ToArray();
        var after = Enumerable.Range(0, actual.Profile.Vertices.Snapshot.VertexCount).Select(i => Source2GeometryAnalyzer.ReadPosition(actual.Profile.Vertices, i)).ToArray();
        var audit = SourceCoincidenceTriangleGuard.Validate(before, after, indices);
        static byte Partition(Point3 a, Point3 b, Point3 c) => (byte)((a == b ? 1 : 0) | (b == c ? 2 : 0) | (c == a ? 4 : 0));
        var outputPartitions = Enumerable.Range(0, indices.Count / 3).Select(i => Partition(after[indices[3 * i]], after[indices[3 * i + 1]], after[indices[3 * i + 2]])).ToArray();
        var changed = Enumerable.Range(0, before.Length).Where(i =>
            BitConverter.SingleToUInt32Bits(before[i].X) != BitConverter.SingleToUInt32Bits(after[i].X)
            || BitConverter.SingleToUInt32Bits(before[i].Y) != BitConverter.SingleToUInt32Bits(after[i].Y)
            || BitConverter.SingleToUInt32Bits(before[i].Z) != BitConverter.SingleToUInt32Bits(after[i].Z)).ToHashSet();
        var validTouched = Enumerable.Range(0, audit.TriangleCount).Count(i => audit.SourcePartitions[i] == 0
            && (changed.Contains(indices[3 * i]) || changed.Contains(indices[3 * i + 1]) || changed.Contains(indices[3 * i + 2])));
        return new(member.MemberId, p.Mesh.Lod, p.Indices.Snapshot.DecodedHash, indices, DirectionalContractValidator.VertexSetHash(indices),
            audit.SourcePartitions, ContentHash.Compute(audit.SourcePartitions.ToArray()), ContentHash.Compute(outputPartitions),
            audit.ValidTriangleCount, validTouched, audit.CollapsedTriangleCount, audit.TouchedCollapsedTriangleCount);
    }
}
