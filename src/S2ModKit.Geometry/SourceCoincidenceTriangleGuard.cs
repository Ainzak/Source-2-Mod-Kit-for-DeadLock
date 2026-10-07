using System.Numerics;

namespace S2ModKit.Geometry;

/// <summary>Ordered corner-coincidence bits and source face categories, never a topology repair.</summary>
public sealed record SourceCoincidenceTriangleAudit(
    int TriangleCount, int ValidTriangleCount, int CollapsedTriangleCount,
    int TouchedCollapsedTriangleCount, IReadOnlyList<byte> SourcePartitions);

/// <summary>Preserves source coincident-corner faces; distinct collinear and new defects reject.</summary>
public static class SourceCoincidenceTriangleGuard
{
    public static SourceCoincidenceTriangleAudit Validate(
        IReadOnlyList<Point3> before, IReadOnlyList<Point3> after, IReadOnlyList<int> triangles)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(triangles);
        if (before.Count != after.Count || triangles.Count % 3 != 0)
            throw new ArgumentException("Triangle inventory is incomplete.");
        var partitions = new byte[triangles.Count / 3];
        var collapsed = 0; var touched = 0;
        for (var index = 0; index < triangles.Count; index += 3)
        {
            var a = triangles[index]; var b = triangles[index + 1]; var c = triangles[index + 2];
            if ((uint)a >= before.Count || (uint)b >= before.Count || (uint)c >= before.Count || a == b || b == c || a == c)
                throw new ArgumentException("Triangle indices are out of range or repeated.", nameof(triangles));
            var source = Cross(before[a], before[b], before[c]);
            var output = Cross(after[a], after[b], after[c]);
            var partition = Partition(before[a], before[b], before[c]);
            partitions[index / 3] = partition;
            if (Zero(source))
            {
                if (partition == 0) throw new ArgumentException("A source face is collinear without coincident corners.", nameof(before));
                if (partition != Partition(after[a], after[b], after[c]) || !Zero(output))
                    throw new ArgumentException("A collapsed source face changed its corner-coincidence partition.", nameof(after));
                collapsed++;
                if (!SameWords(before[a], after[a]) || !SameWords(before[b], after[b]) || !SameWords(before[c], after[c])) touched++;
            }
            else if (Zero(output) || ((source[0] * output[0]) + (source[1] * output[1]) + (source[2] * output[2])).Sign <= 0)
                throw new ArgumentException("A valid source face collapsed or reversed its oriented hemisphere.", nameof(after));
        }
        return new(partitions.Length, partitions.Length - collapsed, collapsed, touched, Array.AsReadOnly(partitions));
    }

    // Float equality deliberately normalizes signed zero for geometric coincidence. Raw-word
    // protection/unchanged audits remain separate and cannot use this equivalence.
    private static byte Partition(Point3 a, Point3 b, Point3 c) =>
        (byte)((a == b ? 1 : 0) | (b == c ? 2 : 0) | (c == a ? 4 : 0));
    private static bool SameWords(Point3 a, Point3 b) => BitConverter.SingleToUInt32Bits(a.X) == BitConverter.SingleToUInt32Bits(b.X)
        && BitConverter.SingleToUInt32Bits(a.Y) == BitConverter.SingleToUInt32Bits(b.Y)
        && BitConverter.SingleToUInt32Bits(a.Z) == BitConverter.SingleToUInt32Bits(b.Z);
    private static bool Zero(BigInteger[] value) => value.All(component => component.IsZero);

    private static BigInteger[] Cross(Point3 a, Point3 b, Point3 c)
    {
        static BigInteger Q(float value) => new(Math.ScaleB((double)value, 149));
        BigInteger[] u = [Q(b.X) - Q(a.X), Q(b.Y) - Q(a.Y), Q(b.Z) - Q(a.Z)];
        BigInteger[] v = [Q(c.X) - Q(a.X), Q(c.Y) - Q(a.Y), Q(c.Z) - Q(a.Z)];
        return [(u[1] * v[2]) - (u[2] * v[1]), (u[2] * v[0]) - (u[0] * v[2]), (u[0] * v[1]) - (u[1] * v[0])];
    }
}
