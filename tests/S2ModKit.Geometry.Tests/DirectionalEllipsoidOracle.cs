using System.Numerics;

namespace S2ModKit.Geometry.Tests;

// Independent test arithmetic: integer significand rounding/sqrt rather than the
// production binary-search primitives; decimal cofactors rather than rank-one inversion.
internal readonly record struct DirectionalOracleFraction
{
    public DirectionalOracleFraction(BigInteger numerator, BigInteger denominator, bool negativeZero = false)
    {
        if (denominator.IsZero) throw new DivideByZeroException();
        if (denominator.Sign < 0) { numerator = -numerator; denominator = -denominator; }
        var gcd = BigInteger.GreatestCommonDivisor(numerator, denominator);
        N = numerator / gcd; D = denominator / gcd;
        NegativeZero = numerator.IsZero && negativeZero;
    }
    public BigInteger N { get; }
    public BigInteger D { get; }
    private bool NegativeZero { get; }
    private bool IsNegative => N.Sign < 0 || NegativeZero;
    public static DirectionalOracleFraction One => new(1, 1);
    public static DirectionalOracleFraction From(double value)
    {
        var bits = BitConverter.DoubleToUInt64Bits(value);
        var exponent = (int)((bits >> 52) & 2047);
        BigInteger mantissa = bits & 0x000fffffffffffffUL;
        if (exponent != 0) mantissa |= BigInteger.One << 52;
        if (bits >> 63 != 0) mantissa = -mantissa;
        var shift = exponent == 0 ? -1074 : exponent - 1075;
        var negativeZero = mantissa.IsZero && bits >> 63 != 0;
        return shift < 0 ? new(mantissa, BigInteger.One << -shift, negativeZero) : new(mantissa << shift, 1, negativeZero);
    }
    public static DirectionalOracleFraction operator +(DirectionalOracleFraction a, DirectionalOracleFraction b) =>
        new(a.N * b.D + b.N * a.D, a.D * b.D, a.N.IsZero && b.N.IsZero && a.NegativeZero && b.NegativeZero);
    public static DirectionalOracleFraction operator -(DirectionalOracleFraction a, DirectionalOracleFraction b) =>
        a + new DirectionalOracleFraction(-b.N, b.D, b.N.IsZero && !b.NegativeZero);
    public static DirectionalOracleFraction operator *(DirectionalOracleFraction a, DirectionalOracleFraction b) =>
        new(a.N * b.N, a.D * b.D, a.IsNegative != b.IsNegative);
    public static DirectionalOracleFraction operator /(DirectionalOracleFraction a, DirectionalOracleFraction b) =>
        new(a.N * b.D, a.D * b.N, a.IsNegative != b.IsNegative);
    public int Compare(DirectionalOracleFraction b) => (N * b.D).CompareTo(b.N * D);
    public decimal Decimal => (decimal)N / (decimal)D;
    public double Round() => BitConverter.UInt64BitsToDouble(RoundedBits(53, -1022, 1023));
    public float RoundFloat() => BitConverter.UInt32BitsToSingle((uint)RoundedBits(24, -126, 127));

    private ulong RoundedBits(int precision, int minimumExponent, int bias)
    {
        var signShift = precision == 53 ? 63 : 31;
        var sign = IsNegative ? 1UL << signShift : 0;
        var numerator = BigInteger.Abs(N);
        if (numerator.IsZero) return sign;
        var exponent = Log2(numerator, D);
        var shift = precision - 1 - Math.Max(exponent, minimumExponent);
        var n = shift >= 0 ? numerator << shift : numerator;
        var d = shift < 0 ? D << -shift : D;
        var word = BigInteger.DivRem(n, d, out var remainder);
        if (2 * remainder > d || (2 * remainder == d && !word.IsEven)) word++;
        if (word == BigInteger.One << precision) { word >>= 1; exponent++; }
        if (exponent < minimumExponent)
            return sign | (ulong)word; // rounding to the first normal also has the right bits
        return sign | ((ulong)(exponent + bias) << (precision - 1)) | (ulong)(word - (BigInteger.One << (precision - 1)));
    }

    public double Sqrt()
    {
        if (N.IsZero) return BitConverter.UInt64BitsToDouble(NegativeZero ? 0x8000000000000000UL : 0);
        var exponent = Log2(N, D) >> 1;
        var shift = 2 * (52 - exponent);
        var n = shift >= 0 ? N << shift : N;
        var d = shift < 0 ? D << -shift : D;
        var integer = IntegerSqrt(n / d);
        var middleSquare = d * (2 * integer + 1) * (2 * integer + 1);
        if (4 * n > middleSquare || (4 * n == middleSquare && !integer.IsEven)) integer++;
        if (integer == BigInteger.One << 53) { integer >>= 1; exponent++; }
        return BitConverter.UInt64BitsToDouble(((ulong)(exponent + 1023) << 52) | (ulong)(integer - (BigInteger.One << 52)));
    }

    private static int Log2(BigInteger n, BigInteger d)
    {
        var exponent = checked((int)(n.GetBitLength() - d.GetBitLength()));
        if ((exponent >= 0 ? n.CompareTo(d << exponent) : (n << -exponent).CompareTo(d)) < 0) exponent--;
        return exponent;
    }
    private static BigInteger IntegerSqrt(BigInteger n)
    {
        var current = BigInteger.One << checked((int)((n.GetBitLength() + 1) / 2));
        while (true) { var next = (current + n / current) / 2; if (next >= current) return current; current = next; }
    }
}

internal static class DirectionalEllipsoidOracle
{
    internal sealed record Result(EllipsoidMembership Membership, double Weight, Point3 StoredPosition,
        decimal[] Normal, decimal[] Tangent, decimal[,] Jacobian, decimal[,] Inverse, decimal Determinant);

    public static Result Evaluate(DirectionalEllipsoidScale field, Point3 point, TangentFrame frame)
    {
        var p = Fractions(point); var c = Fractions(field.Pivot); var radii = Fractions(field.OuterRadii);
        var one = DirectionalOracleFraction.One; var h = DirectionalOracleFraction.From(field.CoreFraction);
        var q = new DirectionalOracleFraction(0, 1);
        for (var i = 0; i < 3; i++) { var d = (p[i] - c[i]) / radii[i]; q += d * d; }
        var membership = q.Compare(h * h) <= 0 ? EllipsoidMembership.Core
            : q.Compare(one) >= 0 ? EllipsoidMembership.Pinned : EllipsoidMembership.Transition;
        var weight = membership == EllipsoidMembership.Core ? 1d : 0d;
        var scale = Values(field.Scale);
        var actualScales = new[] { field.Scale.X, field.Scale.Y, field.Scale.Z };
        var output = new[] { point.X, point.Y, point.Z };
        if (membership == EllipsoidMembership.Transition)
        {
            var rho = DirectionalOracleFraction.From(q.Sqrt());
            var width = Rounded(one - h);
            var t = Rounded(Rounded(rho - h) / width);
            var z = Rounded(one - t);
            weight = Rounded(Rounded(z * z) * Rounded(one + Rounded(new DirectionalOracleFraction(2, 1) * t))).Round();
        }
        if (membership != EllipsoidMembership.Pinned)
            for (var i = 0; i < 3; i++)
            {
                if (actualScales[i] == 1) continue;
                var s = DirectionalOracleFraction.From(actualScales[i]);
                var a = membership == EllipsoidMembership.Core ? s : Rounded(one + Rounded(Rounded(s - one) * DirectionalOracleFraction.From(weight)));
                output[i] = Rounded(c[i] + Rounded(a * Rounded(p[i] - c[i]))).RoundFloat();
            }

        // Independently evaluate the IDEAL differential using 28-digit decimal
        // arithmetic and an expanded polynomial, then a complete cofactor inverse.
        var dValues = Enumerable.Range(0, 3).Select(i => p[i].Decimal - c[i].Decimal).ToArray();
        var radiusValues = radii.Select(value => value.Decimal).ToArray();
        decimal idealWeight = membership == EllipsoidMembership.Core ? 1 : 0;
        var gradient = new decimal[3];
        if (membership == EllipsoidMembership.Transition)
        {
            var norm = Sqrt(Enumerable.Range(0, 3).Sum(i => dValues[i] * dValues[i] / (radiusValues[i] * radiusValues[i])));
            var t = (norm - h.Decimal) / (1 - h.Decimal);
            idealWeight = 1 - 3 * t * t + 2 * t * t * t;
            for (var i = 0; i < 3; i++) gradient[i] = -6 * t * (1 - t) * dValues[i] / ((1 - h.Decimal) * radiusValues[i] * radiusValues[i] * norm);
        }
        var matrix = new decimal[3, 3];
        for (var row = 0; row < 3; row++) for (var column = 0; column < 3; column++)
            matrix[row, column] = (row == column ? 1 + (scale[row] - 1) * idealWeight : 0)
                + (scale[row] - 1) * dValues[row] * gradient[column];
        var cofactor = Cofactors(matrix);
        var determinant = Enumerable.Range(0, 3).Sum(i => matrix[0, i] * cofactor[0, i]);
        var inverse = new decimal[3, 3];
        for (var row = 0; row < 3; row++) for (var column = 0; column < 3; column++) inverse[row, column] = cofactor[column, row] / determinant;
        var inputN = Values(frame.Normal); var inputT = Values(frame.Tangent);
        var normal = Unit(Enumerable.Range(0, 3).Select(row => Enumerable.Range(0, 3).Sum(column => inverse[column, row] * inputN[column])).ToArray());
        var tangent = Enumerable.Range(0, 3).Select(row => Enumerable.Range(0, 3).Sum(column => matrix[row, column] * inputT[column])).ToArray();
        var projection = Enumerable.Range(0, 3).Sum(i => normal[i] * tangent[i]);
        tangent = Unit(Enumerable.Range(0, 3).Select(i => tangent[i] - normal[i] * projection).ToArray());
        return new(membership, weight, new(output[0], output[1], output[2]), normal, tangent, matrix, inverse, determinant);
    }

    public static decimal[,] Cofactors(decimal[,] matrix)
    {
        var result = new decimal[3, 3];
        for (var row = 0; row < 3; row++) for (var column = 0; column < 3; column++)
            result[row, column] = matrix[(row + 1) % 3, (column + 1) % 3] * matrix[(row + 2) % 3, (column + 2) % 3]
                - matrix[(row + 1) % 3, (column + 2) % 3] * matrix[(row + 2) % 3, (column + 1) % 3];
        return result;
    }

    // Software-round every prescribed differential operation. The full-matrix
    // decimal oracle above independently checks its mathematical meaning.
    public static TangentFrame RoundedFrame(DirectionalEllipsoidScale field, Point3 point, TangentFrame source, Result expected)
    {
        if (expected.Membership == EllipsoidMembership.Pinned || (expected.Membership == EllipsoidMembership.Core
            && field.Scale.X == field.Scale.Y && field.Scale.Y == field.Scale.Z)) return source;
        var one = DirectionalOracleFraction.One;
        var zero = new DirectionalOracleFraction(0, 1);
        var scale = Fractions(field.Scale); var p = Fractions(point); var pivot = Fractions(field.Pivot);
        var radius = Fractions(field.OuterRadii); var h = DirectionalOracleFraction.From(field.CoreFraction);
        var a = scale.ToArray(); var u = new[] { zero, zero, zero }; var g = new[] { zero, zero, zero };
        if (expected.Membership == EllipsoidMembership.Transition)
        {
            var distance = zero;
            for (var i = 0; i < 3; i++) { var normalized = (p[i] - pivot[i]) / radius[i]; distance += normalized * normalized; }
            var rho = DirectionalOracleFraction.From(distance.Sqrt());
            var width = Rounded(one - h); var t = Rounded(Rounded(rho - h) / width);
            var derivative = Rounded(Rounded(Rounded(new DirectionalOracleFraction(-6, 1) * t) * Rounded(one - t)) / width);
            for (var i = 0; i < 3; i++)
            {
                var delta = Rounded(scale[i] - one); var d = Rounded(p[i] - pivot[i]);
                a[i] = Rounded(one + Rounded(delta * DirectionalOracleFraction.From(expected.Weight)));
                u[i] = Rounded(delta * d);
                g[i] = Rounded(Rounded(Rounded(Rounded(derivative * d) / radius[i]) / radius[i]) / rho);
            }
        }
        var beta = Rounded(one + RoundedDot(g, Enumerable.Range(0, 3).Select(i => Rounded(u[i] / a[i])).ToArray()));
        var n = Fractions(source.Normal); var tangent = Fractions(source.Tangent);
        var r = Enumerable.Range(0, 3).Select(i => Rounded(n[i] / a[i])).ToArray();
        var z = Enumerable.Range(0, 3).Select(i => Rounded(g[i] / a[i])).ToArray();
        var k = Rounded(RoundedDot(u, r) / beta);
        for (var i = 0; i < 3; i++) n[i] = Rounded(r[i] - Rounded(z[i] * k));
        RoundedNormalize(n);
        var projection = RoundedDot(g, tangent);
        for (var i = 0; i < 3; i++) tangent[i] = Rounded(Rounded(a[i] * tangent[i]) + Rounded(u[i] * projection));
        projection = RoundedDot(n, tangent);
        for (var i = 0; i < 3; i++) tangent[i] = Rounded(tangent[i] - Rounded(n[i] * projection));
        RoundedNormalize(tangent);
        return new(new(n[0].RoundFloat(), n[1].RoundFloat(), n[2].RoundFloat()),
            new(tangent[0].RoundFloat(), tangent[1].RoundFloat(), tangent[2].RoundFloat()), source.Handedness);
    }

    private static DirectionalOracleFraction RoundedDot(DirectionalOracleFraction[] a, DirectionalOracleFraction[] b) =>
        Rounded(Rounded(Rounded(a[0] * b[0]) + Rounded(a[1] * b[1])) + Rounded(a[2] * b[2]));
    private static void RoundedNormalize(DirectionalOracleFraction[] vector)
    {
        var length = DirectionalOracleFraction.From(RoundedDot(vector, vector).Sqrt());
        for (var i = 0; i < 3; i++) vector[i] = Rounded(vector[i] / length);
    }
    public static decimal Sqrt(decimal value)
    {
        if (value == 0) return 0;
        var current = value > 1 ? value : 1;
        for (var i = 0; i < 120; i++)
        {
            var next = (current + value / current) / 2;
            if (Math.Abs(next - current) <= 0.000000000000000000000000001m) return next;
            current = next;
        }
        throw new InvalidOperationException("Independent decimal sqrt did not converge.");
    }
    public static decimal[] Unit(decimal[] values) { var length = Sqrt(values.Sum(value => value * value)); return values.Select(value => value / length).ToArray(); }
    public static decimal[] Values(Point3 point) => Fractions(point).Select(value => value.Decimal).ToArray();
    private static DirectionalOracleFraction[] Fractions(Point3 point) => [DirectionalOracleFraction.From(point.X), DirectionalOracleFraction.From(point.Y), DirectionalOracleFraction.From(point.Z)];
    private static DirectionalOracleFraction Rounded(DirectionalOracleFraction value) => DirectionalOracleFraction.From(value.Round());
}
