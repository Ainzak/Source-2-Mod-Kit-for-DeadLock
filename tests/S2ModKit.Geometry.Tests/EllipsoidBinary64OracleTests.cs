using System.Numerics;

namespace S2ModKit.Geometry.Tests;

public sealed class EllipsoidBinary64OracleTests
{
    [Fact]
    public void CertificateDoubleEnclosureIsExactAndOutward()
    {
        var c = EllipsoidJacobianCertificate.Create(1f / 16, 2, new(8, 4, 2));
        var exact = new Fraction(c.Numerator, c.Denominator);
        Assert.True(Fraction.FromDouble(c.LowerBound).Compare(exact) <= 0);
        Assert.True(Fraction.FromDouble(c.UpperBound).Compare(exact) >= 0);
        Assert.Equal(Math.BitIncrement(c.LowerBound), c.UpperBound);
        var singular = new Fraction(c.Numerator, c.Denominator * 4);
        Assert.True(Fraction.FromDouble(c.MinimumSingularValueLowerBound).Compare(singular) <= 0);
        Assert.True(Fraction.FromDouble(Math.BitIncrement(c.MinimumSingularValueLowerBound)).Compare(singular) >= 0);
    }
    [Fact]
    public void NonDyadicNormalizedDistancesMatchIndependentIntegerSquareRootRounding()
    {
        var field = new EllipsoidScale(new(3, -5, 7), new(7, 6, 4), 1f / 16, 2, 64);
        var random = new Random(32711);
        for (var i = 0; i < 1024; i++)
        {
            var p = new Point3(3 + random.Next(-8192, 8193) / 1024f, -5 + random.Next(-8192, 8193) / 1024f, 7 + random.Next(-8192, 8193) / 1024f);
            var q = Square(p.X, 3, 7) + Square(p.Y, -5, 6) + Square(p.Z, 7, 4);
            var h = Fraction.FromFloat(field.CoreFraction);
            var membership = q.Compare(h * h) <= 0 ? EllipsoidMembership.Core : q.Compare(new(1, 1)) >= 0 ? EllipsoidMembership.Pinned : EllipsoidMembership.Transition;
            var weight = membership == EllipsoidMembership.Core ? 1d : 0d;
            if (membership == EllipsoidMembership.Transition)
            {
                var rho = Sqrt(q);
                var t = (rho - field.CoreFraction) / (1 - (double)field.CoreFraction);
                var u = 1 - t;
                weight = (u * u) * (1 + (2 * t));
            }
            var actual = field.Evaluate(p);
            Assert.Equal(membership, actual.Membership);
            Assert.Equal(BitConverter.DoubleToUInt64Bits(weight), BitConverter.DoubleToUInt64Bits(actual.Weight));
            if (membership == EllipsoidMembership.Pinned) Assert.Equal(p, actual.Position);
            else
            {
                var a = 1 + weight;
                AssertWord((float)(3 + (a * ((double)p.X - 3))), actual.Position.X);
                AssertWord((float)(-5 + (a * ((double)p.Y + 5))), actual.Position.Y);
                AssertWord((float)(7 + (a * ((double)p.Z - 7))), actual.Position.Z);
            }
        }
    }

    // Normalize the rational BEFORE integer square root. This independently rounds
    // a 53-bit significand using a remainder/midpoint test; no double-bit search,
    // Math.Sqrt or production rational arithmetic is used.
    private static double Sqrt(Fraction q)
    {
        var exponent = checked((int)(q.N.GetBitLength() - q.D.GetBitLength()));
        if ((exponent >= 0 ? q.N.CompareTo(q.D << exponent) : (q.N << -exponent).CompareTo(q.D)) < 0) exponent--;
        var rootExponent = exponent >= 0 ? exponent / 2 : (exponent - 1) / 2;
        var unitExponent = rootExponent - 52;
        var n = q.N << (-2 * unitExponent);
        var floor = IntegerSqrt(n / q.D);
        var midpointComparison = (4 * n).CompareTo(q.D * ((2 * floor) + 1) * ((2 * floor) + 1));
        if (midpointComparison > 0 || (midpointComparison == 0 && !floor.IsEven)) floor++;
        return Math.ScaleB((double)floor, unitExponent);
    }

    private static BigInteger IntegerSqrt(BigInteger n)
    {
        var x = BigInteger.One << checked((int)((n.GetBitLength() + 1) / 2));
        while (true) { var next = (x + (n / x)) / 2; if (next >= x) return x; x = next; }
    }

    private static Fraction Square(float p, float c, float r)
    {
        var d = Fraction.FromFloat(p) - Fraction.FromFloat(c);
        var radius = Fraction.FromFloat(r);
        return new(d.N * d.N * radius.D * radius.D, d.D * d.D * radius.N * radius.N);
    }

    private readonly record struct Fraction
    {
        public Fraction(BigInteger numerator, BigInteger denominator)
        {
            var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
            N = numerator / divisor; D = denominator / divisor;
        }
        public BigInteger N { get; }
        public BigInteger D { get; }
        public int Compare(Fraction b) => (N * b.D).CompareTo(b.N * D);
        public static Fraction operator +(Fraction a, Fraction b) => new((a.N * b.D) + (b.N * a.D), a.D * b.D);
        public static Fraction operator -(Fraction a, Fraction b) => new((a.N * b.D) - (b.N * a.D), a.D * b.D);
        public static Fraction operator *(Fraction a, Fraction b) => new(a.N * b.N, a.D * b.D);
        public static Fraction FromFloat(float value)
        {
            var bits = BitConverter.SingleToUInt32Bits(value);
            var exponent = (int)((bits >> 23) & 255);
            BigInteger n = bits & 0x7fffff;
            if (exponent != 0) n += 1 << 23;
            if (bits >> 31 != 0) n = -n;
            var shift = exponent == 0 ? -149 : exponent - 150;
            return shift >= 0 ? new(n << shift, 1) : new(n, BigInteger.One << -shift);
        }
        public static Fraction FromDouble(double value)
        {
            var bits = BitConverter.DoubleToUInt64Bits(value);
            var exponent = (int)((bits >> 52) & 2047);
            BigInteger n = bits & 0x000fffffffffffffUL;
            if (exponent != 0) n += BigInteger.One << 52;
            if (bits >> 63 != 0) n = -n;
            var shift = exponent == 0 ? -1074 : exponent - 1075;
            return shift >= 0 ? new(n << shift, 1) : new(n, BigInteger.One << -shift);
        }
    }
    private static void AssertWord(float expected, float actual) => Assert.Equal(BitConverter.SingleToUInt32Bits(expected), BitConverter.SingleToUInt32Bits(actual));
}
