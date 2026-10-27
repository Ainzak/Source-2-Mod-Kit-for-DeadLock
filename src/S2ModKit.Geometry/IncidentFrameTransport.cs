namespace S2ModKit.Geometry;

public sealed record IncidentFrameResult(IReadOnlyList<int> Closure, IReadOnlyList<TangentFrame> Frames);

/// <summary>Source-relative, per-record face differential transport. No welding or UV reconstruction.</summary>
public static class IncidentFrameTransport
{
    public static IncidentFrameResult Evaluate(IReadOnlyList<Point3> before, IReadOnlyList<Point3> after,
        IReadOnlyList<int> triangles, IReadOnlyList<TangentFrame> sourceFrames)
    {
        if (before.Count != after.Count || before.Count != sourceFrames.Count || triangles.Count % 3 != 0)
            throw new ArgumentException("Incomplete frame inventory.");
        var valid = PositionEditGuard.Validate(before, after, triangles);
        var sums = new double[before.Count][]; var weights = new double[before.Count]; var closure = new bool[before.Count];
        for (var face = 0; face < valid.Length; face++)
        {
            if (!valid[face]) continue;
            var a = triangles[face * 3]; var b = triangles[face * 3 + 1]; var c = triangles[face * 3 + 2];
            var e1 = Sub(V(before[b]), V(before[a])); var e2 = Sub(V(before[c]), V(before[a]));
            var cross = Cross(e1, e2); var weight = Length(cross);
            var changed = !PositionEditGuard.SameWords(before[a], after[a]) || !PositionEditGuard.SameWords(before[b], after[b]) || !PositionEditGuard.SameWords(before[c], after[c]);
            var f = Identity();
            if (changed)
            {
                var t1 = Sub(V(after[b]), V(after[a])); var t2 = Sub(V(after[c]), V(after[a]));
                f = Multiply(Columns(t1, t2, Unit(Cross(t1, t2))), Inverse(Columns(e1, e2, Unit(cross))));
                closure[a] = closure[b] = closure[c] = true;
            }
            foreach (var vertex in new[] { a, b, c })
            {
                sums[vertex] ??= new double[9];
                for (var i = 0; i < 9; i++) sums[vertex][i] += weight * f[i];
                weights[vertex] += weight;
            }
        }
        var result = sourceFrames.ToArray(); var ids = new List<int>();
        for (var vertex = 0; vertex < before.Count; vertex++)
        {
            ValidateFrame(sourceFrames[vertex]);
            if (!PositionEditGuard.SameWords(before[vertex], after[vertex]) && weights[vertex] == 0)
                throw new ArgumentException("Edited record has no valid incident source face.");
            if (!closure[vertex]) continue;
            ids.Add(vertex);
            var j = sums[vertex].Select(x => x / weights[vertex]).ToArray();
            Certify(j);
            var normal = Unit(Map(Cofactor(j), V(sourceFrames[vertex].Normal)));
            var tangent = Map(j, V(sourceFrames[vertex].Tangent));
            tangent = Unit(Sub(tangent, Scale(normal, Dot(normal, tangent))));
            var frame = new TangentFrame(P(normal), P(tangent), sourceFrames[vertex].Handedness);
            ValidateFrame(frame); result[vertex] = frame;
        }
        return new(ids, result);
    }

    /// <summary>Exact rational Frobenius certificates enclose the spectral norms of rounded J and J^-1.</summary>
    internal static void Certify(double[] j)
    {
        if (j.Length != 9 || j.Any(x => !double.IsFinite(x))) throw new ArgumentException("Nonfinite differential.");
        var a = j.Select(EllipsoidRational.FromDouble).ToArray();
        var c = new EllipsoidRational[]
        {
            a[4]*a[8]-a[5]*a[7], a[5]*a[6]-a[3]*a[8], a[3]*a[7]-a[4]*a[6],
            a[2]*a[7]-a[1]*a[8], a[0]*a[8]-a[2]*a[6], a[1]*a[6]-a[0]*a[7],
            a[1]*a[5]-a[2]*a[4], a[2]*a[3]-a[0]*a[5], a[0]*a[4]-a[1]*a[3]
        };
        var determinant = (a[0] * c[0] + a[1] * c[1]) + a[2] * c[2];
        if (determinant.Numerator.Sign <= 0) throw new ArgumentException("Differential determinant is not positive.");
        var zero = new EllipsoidRational(0, 1);
        var normSquared = a.Aggregate(zero, (sum, x) => sum + x * x);
        var inverseSquared = c.Aggregate(zero, (sum, x) => sum + x * x) / (determinant * determinant);
        var bound = new EllipsoidRational(4096, 1);
        if (normSquared.Compare(bound) > 0 || inverseSquared.Compare(bound) > 0 || (normSquared * inverseSquared).Compare(bound) > 0)
            throw new ArgumentException("Differential spectral/condition bounds are unproven.");
    }

    private static void ValidateFrame(TangentFrame f)
    {
        var n = V(f.Normal); var t = V(f.Tangent);
        if (n.Concat(t).Any(x => !double.IsFinite(x)) || Math.Abs(Length(n) - 1) > 1e-4 || Math.Abs(Length(t) - 1) > 1e-4
            || Math.Abs(Dot(n, t)) > 1e-4 || f.Handedness is not (1 or -1)) throw new ArgumentException("Invalid authored/transported frame.");
    }
    private static double[] V(Point3 p) => [p.X, p.Y, p.Z];
    private static Point3 P(double[] p) => new((float)p[0], (float)p[1], (float)p[2]);
    private static double Dot(double[] a, double[] b) => ((a[0] * b[0]) + (a[1] * b[1])) + (a[2] * b[2]);
    private static double Length(double[] a) => Math.Sqrt(Dot(a, a));
    private static double[] Scale(double[] a, double s) => [a[0] * s, a[1] * s, a[2] * s];
    private static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
    private static double[] Cross(double[] a, double[] b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    private static double[] Unit(double[] a)
    {
        var length = Length(a);
        if (!double.IsFinite(length) || length <= 0) throw new ArgumentException("Unrepresentable frame basis.");
        return [a[0] / length, a[1] / length, a[2] / length];
    }
    private static double[] Columns(double[] a, double[] b, double[] c) => [a[0], b[0], c[0], a[1], b[1], c[1], a[2], b[2], c[2]];
    private static double[] Identity() => [1, 0, 0, 0, 1, 0, 0, 0, 1];
    private static double[] Cofactor(double[] a) =>
    [a[4]*a[8]-a[5]*a[7], a[5]*a[6]-a[3]*a[8], a[3]*a[7]-a[4]*a[6],
     a[2]*a[7]-a[1]*a[8], a[0]*a[8]-a[2]*a[6], a[1]*a[6]-a[0]*a[7],
     a[1]*a[5]-a[2]*a[4], a[2]*a[3]-a[0]*a[5], a[0]*a[4]-a[1]*a[3]];
    private static double[] Inverse(double[] a)
    {
        var c = Cofactor(a); var d = ((a[0] * c[0]) + (a[1] * c[1])) + (a[2] * c[2]);
        if (!double.IsFinite(d) || d == 0) throw new ArgumentException("Unrepresentable source basis.");
        return [c[0] / d, c[3] / d, c[6] / d, c[1] / d, c[4] / d, c[7] / d, c[2] / d, c[5] / d, c[8] / d];
    }
    private static double[] Map(double[] a, double[] b) =>
        [((a[0] * b[0]) + (a[1] * b[1])) + (a[2] * b[2]), ((a[3] * b[0]) + (a[4] * b[1])) + (a[5] * b[2]), ((a[6] * b[0]) + (a[7] * b[1])) + (a[8] * b[2])];
    private static double[] Multiply(double[] a, double[] b)
    {
        var c = new double[9];
        for (var row = 0; row < 3; row++) for (var col = 0; col < 3; col++)
            c[row * 3 + col] = ((a[row * 3] * b[col]) + (a[row * 3 + 1] * b[col + 3])) + (a[row * 3 + 2] * b[col + 6]);
        return c;
    }
}
