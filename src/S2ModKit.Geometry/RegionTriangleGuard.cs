using System.Numerics;

namespace S2ModKit.Geometry;

/// <summary>Exact dyadic orientation/degeneracy check; not a self-intersection or pose simulator.</summary>
public static class RegionTriangleGuard
{
    public static void Validate(IReadOnlyList<Point3> before, IReadOnlyList<Point3> after, IReadOnlyList<int> triangles)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(triangles);
        if (before.Count != after.Count || triangles.Count % 3 != 0) throw new ArgumentException("Triangle inventory is incomplete.");
        for (var index = 0; index < triangles.Count; index += 3)
        {
            var a = triangles[index]; var b = triangles[index + 1]; var c = triangles[index + 2];
            if ((uint)a >= before.Count || (uint)b >= before.Count || (uint)c >= before.Count) throw new ArgumentException("Triangle index is outside the buffer.");
            var original = Cross(before[a], before[b], before[c]);
            var output = Cross(after[a], after[b], after[c]);
            if (original.All(v => v.IsZero) || output.All(v => v.IsZero)
                || ((original[0] * output[0]) + (original[1] * output[1]) + (original[2] * output[2])).Sign <= 0)
                throw new ArgumentException("Source/output triangle is degenerate or the output reverses its source-oriented hemisphere.");
        }
    }

    private static BigInteger[] Cross(Point3 a, Point3 b, Point3 c)
    {
        // All binary32 coordinates are integer multiples of 2^-149. Scaling through
        // binary64 is exact, so subtraction, cross and dot signs need no tolerance.
        static BigInteger Q(float value) => new(Math.ScaleB((double)value, 149));
        BigInteger[] u = [Q(b.X) - Q(a.X), Q(b.Y) - Q(a.Y), Q(b.Z) - Q(a.Z)];
        BigInteger[] v = [Q(c.X) - Q(a.X), Q(c.Y) - Q(a.Y), Q(c.Z) - Q(a.Z)];
        return [(u[1] * v[2]) - (u[2] * v[1]), (u[2] * v[0]) - (u[0] * v[2]), (u[0] * v[1]) - (u[1] * v[0])];
    }
}
