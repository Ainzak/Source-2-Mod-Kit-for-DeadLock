using System.Numerics;

namespace S2ModKit.Geometry;

/// <summary>Exact finite IEEE values and rational comparisons for ellipsoid endpoints and certificates.</summary>
internal readonly record struct EllipsoidRational
{
    public EllipsoidRational(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.Sign <= 0) throw new ArgumentOutOfRangeException(nameof(denominator));
        var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / divisor;
        Denominator = denominator / divisor;
    }

    public BigInteger Numerator { get; }
    public BigInteger Denominator { get; }
    public static EllipsoidRational One => new(1, 1);
    public static EllipsoidRational FromDouble(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        var bits = BitConverter.DoubleToUInt64Bits(value);
        var exponent = (int)((bits >> 52) & 2047);
        var mantissa = new BigInteger(bits & 0x000fffffffffffffUL);
        if (exponent != 0) mantissa += BigInteger.One << 52;
        var shift = exponent == 0 ? -1074 : exponent - 1075;
        if ((bits >> 63) != 0) mantissa = -mantissa;
        return shift >= 0 ? new(mantissa << shift, 1) : new(mantissa, BigInteger.One << -shift);
    }

    public static EllipsoidRational operator +(EllipsoidRational a, EllipsoidRational b) =>
        new((a.Numerator * b.Denominator) + (b.Numerator * a.Denominator), a.Denominator * b.Denominator);
    public static EllipsoidRational operator -(EllipsoidRational a, EllipsoidRational b) =>
        new((a.Numerator * b.Denominator) - (b.Numerator * a.Denominator), a.Denominator * b.Denominator);
    public static EllipsoidRational operator *(EllipsoidRational a, EllipsoidRational b) =>
        new(a.Numerator * b.Numerator, a.Denominator * b.Denominator);
    public static EllipsoidRational operator /(EllipsoidRational a, EllipsoidRational b) =>
        b.Numerator.Sign > 0 ? new(a.Numerator * b.Denominator, a.Denominator * b.Numerator)
            : throw new ArgumentOutOfRangeException(nameof(b));
    public int Compare(EllipsoidRational other) => (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);

    // Fixed exact binary-search rounding. No conversion of the rational to double decides a bit.
    public double SqrtNearest()
    {
        if (Numerator.Sign <= 0 || Compare(One) >= 0) throw new ArgumentOutOfRangeException(nameof(Numerator));
        var low = SearchFloor(0x3ff0000000000000UL, square: true);
        var lo = BitConverter.UInt64BitsToDouble(low);
        var exactLo = FromDouble(lo);
        if ((exactLo * exactLo).Compare(this) == 0) return lo;
        var hi = BitConverter.UInt64BitsToDouble(low + 1);
        var midpoint = (exactLo + FromDouble(hi)) / new EllipsoidRational(2, 1);
        var comparison = Compare(midpoint * midpoint);
        return comparison < 0 || (comparison == 0 && (low & 1) == 0) ? lo : hi;
    }

    public (double Lower, double Upper) EnclosePositive()
    {
        if (Numerator.Sign <= 0) throw new ArgumentOutOfRangeException(nameof(Numerator));
        var bits = SearchFloor(0x7fefffffffffffffUL, square: false);
        var lower = BitConverter.UInt64BitsToDouble(bits);
        return (lower, FromDouble(lower).Compare(this) == 0 ? lower : Math.BitIncrement(lower));
    }

    private ulong SearchFloor(ulong high, bool square)
    {
        ulong low = 0;
        while (low < high)
        {
            var middle = low + ((high - low + 1) / 2);
            var candidate = FromDouble(BitConverter.UInt64BitsToDouble(middle));
            if ((square ? candidate * candidate : candidate).Compare(this) <= 0) low = middle;
            else high = middle - 1;
        }
        return low;
    }
}
