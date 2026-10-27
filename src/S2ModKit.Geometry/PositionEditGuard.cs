using System.Numerics;

namespace S2ModKit.Geometry;

public static class PositionEditGuard
{
    public static bool SameWords(Point3 a, Point3 b) => BitConverter.SingleToUInt32Bits(a.X) == BitConverter.SingleToUInt32Bits(b.X)
        && BitConverter.SingleToUInt32Bits(a.Y) == BitConverter.SingleToUInt32Bits(b.Y) && BitConverter.SingleToUInt32Bits(a.Z) == BitConverter.SingleToUInt32Bits(b.Z);

    public static bool[] Validate(IReadOnlyList<Point3> before, IReadOnlyList<Point3> after, IReadOnlyList<int> indices)
    {
        if (before.Count != after.Count || indices.Count % 3 != 0 || before.Concat(after).Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)))
            throw new ArgumentException("Invalid position/triangle inventory.");
        var valid = new bool[indices.Count / 3];
        for (var face = 0; face < valid.Length; face++)
        {
            var a = indices[face * 3]; var b = indices[face * 3 + 1]; var c = indices[face * 3 + 2];
            if ((uint)a >= before.Count || (uint)b >= before.Count || (uint)c >= before.Count) throw new ArgumentException("Invalid triangle index.");
            var source = Cross(before[a], before[b], before[c]); var target = Cross(after[a], after[b], after[c]);
            if (source.All(x => x.IsZero))
            {
                var partition = Partition(before[a], before[b], before[c]);
                if (partition == 0)
                {
                    if (!SameWords(before[a], after[a]) || !SameWords(before[b], after[b]) || !SameWords(before[c], after[c]))
                        throw new ArgumentException("Distinct source-collinear face moved.");
                }
                else if (partition != Partition(after[a], after[b], after[c]) || target.Any(x => !x.IsZero))
                    throw new ArgumentException("Source collapsed partition changed.");
            }
            else
            {
                valid[face] = true;
                if (target.All(x => x.IsZero) || ((source[0] * target[0]) + (source[1] * target[1]) + (source[2] * target[2])).Sign <= 0)
                    throw new ArgumentException("Valid source face collapsed/reversed.");
            }
        }
        return valid;
    }
    private static byte Partition(Point3 a, Point3 b, Point3 c) => (byte)((a == b ? 1 : 0) | (b == c ? 2 : 0) | (a == c ? 4 : 0));
    private static BigInteger[] Cross(Point3 a, Point3 b, Point3 c)
    {
        static BigInteger Q(float f) => new(Math.ScaleB((double)f, 149));
        BigInteger[] u = [Q(b.X) - Q(a.X), Q(b.Y) - Q(a.Y), Q(b.Z) - Q(a.Z)];
        BigInteger[] v = [Q(c.X) - Q(a.X), Q(c.Y) - Q(a.Y), Q(c.Z) - Q(a.Z)];
        return [u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0]];
    }
}
