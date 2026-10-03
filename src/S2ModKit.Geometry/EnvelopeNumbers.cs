using System.Numerics;

namespace S2ModKit.Geometry;

/// <summary>
/// Shared low-level float representation/rounding primitives, not policy decisions.
/// Every finite binary32 value is an integer multiple of 2^-149. Exact integer comparisons
/// correct double seeds (including double-rounding ties) without tolerances or lost tiny terms.
/// Integers are bounded by the float format: at most 279 bits for endpoints, 558 for distances.
/// </summary>
internal static class EnvelopeNumbers
{
    private const int MaximumFloatBits = 0x7f7fffff;
    private static readonly BigInteger MaximumUnits = Units(float.MaxValue);

    public static float Component(Point3 point, int axis) => axis switch { 0 => point.X, 1 => point.Y, _ => point.Z };
    public static bool Same(float left, float right) => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);
    public static bool Same(Point3 left, Point3 right) => Same(left.X, right.X) && Same(left.Y, right.Y) && Same(left.Z, right.Z);
    public static BigInteger Square(BigInteger value) => value * value;

    public static BigInteger Units(float value)
    {
        ScalarValidation.RequireFinite(value, nameof(value));
        var bits = BitConverter.SingleToInt32Bits(value);
        var exponent = (bits >> 23) & 255;
        var mantissa = bits & 0x7fffff;
        var magnitude = exponent == 0 ? new BigInteger(mantissa) : new BigInteger(mantissa | 0x800000) << (exponent - 1);
        return bits < 0 ? -magnitude : magnitude;
    }

    public static EnvelopeGrowth Growth(string name, BigInteger before, BigInteger after) =>
        new(name, ToDouble(before), ToDouble(after), ToDouble(after - before));

    private static double ToDouble(BigInteger value) => Math.ScaleB((double)value, -149);

    public static void Validate(Bounds3 bounds)
    {
        for (var axis = 0; axis < 3; axis++)
        {
            if (Component(bounds.Min, axis) >= Component(bounds.Max, axis))
                throw new ArgumentException("Culling boxes must have strictly positive extents.", nameof(bounds));
        }
    }

    public static void Validate(CenterHalfExtentBounds bounds)
    {
        for (var axis = 0; axis < 3; axis++)
        {
            var center = Component(bounds.Center, axis);
            var half = Component(bounds.HalfExtents, axis);
            if (half <= 0 || !float.IsFinite(center - half) || !float.IsFinite(center + half))
                throw new ArgumentException("Culling half-extents must be positive with finite reconstructed endpoints.", nameof(bounds));
        }
    }

    public static void Validate(FixedCenterSphere sphere)
    {
        if (!float.IsFinite(sphere.Radius) || sphere.Radius <= 0)
            throw new ArgumentException("Culling sphere radius must be finite and positive.", nameof(sphere));
    }

    // An unchanged box is interpreted in both exact c +/- h and binary32 c +/- h arithmetic.
    // Preservation compares each interpretation to itself; contributors must fit both.
    // A grown axis encloses their union, uses the nearest-even midpoint (+0 for exact zero),
    // then the least positive float half-extent satisfying BOTH containment interpretations.
    public static bool Contains(float center, float half, BigInteger low, BigInteger high)
    {
        var c = Units(center);
        var h = Units(half);
        if (c - h > low || c + h < high) return false;
        var reconstructedLow = center - half;
        var reconstructedHigh = center + half;
        return (float.IsNegativeInfinity(reconstructedLow) || Units(reconstructedLow) <= low)
            && (float.IsPositiveInfinity(reconstructedHigh) || Units(reconstructedHigh) >= high);
    }

    public static float Midpoint(BigInteger low, BigInteger high)
    {
        var sum = low + high;
        if (BigInteger.Abs(sum) > MaximumUnits * 2) throw new OverflowException("Unrepresentable culling center.");
        if (sum.IsZero) return 0f;
        var seed = (float)Math.ScaleB((double)sum, -150);
        var result = seed;
        var distance = BigInteger.Abs(Units(seed) * 2 - sum);
        foreach (var candidate in new[] { MathF.BitDecrement(seed), MathF.BitIncrement(seed) })
        {
            if (!float.IsFinite(candidate)) continue;
            var candidateDistance = BigInteger.Abs(Units(candidate) * 2 - sum);
            if (candidateDistance < distance || (candidateDistance == distance && (BitConverter.SingleToInt32Bits(candidate) & 1) == 0))
            {
                result = candidate;
                distance = candidateDistance;
            }
        }

        return result;
    }

    private static float Ceiling(BigInteger value)
    {
        if (value.Sign < 0 || value > MaximumUnits) throw new OverflowException("Unrepresentable culling extent.");
        var result = (float)ToDouble(value);
        while (Units(result) < value) result = MathF.BitIncrement(result);
        while (result > 0 && Units(MathF.BitDecrement(result)) >= value) result = MathF.BitDecrement(result);
        return result;
    }

    public static float EncodeHalf(float center, BigInteger low, BigInteger high)
    {
        var c = Units(center);
        var half = Ceiling(BigInteger.Max(c - low, high - c));
        // Binary-search only when float endpoint reconstruction rounds inward. Avoid a potentially
        // enormous linear ULP walk when a small extent is added to a large center.
        var left = BitConverter.SingleToInt32Bits(half);
        var right = MaximumFloatBits;
        if (!Contains(center, half, low, high))
        {
            if (!Contains(center, float.MaxValue, low, high)) throw new OverflowException("Unrepresentable culling box.");
            while (left < right)
            {
                var middle = left + ((right - left) / 2);
                if (Contains(center, BitConverter.Int32BitsToSingle(middle), low, high)) right = middle;
                else left = middle + 1;
            }

            half = BitConverter.Int32BitsToSingle(left);
        }

        if (half <= 0 || !float.IsFinite(center - half) || !float.IsFinite(center + half))
            throw new OverflowException("Unrepresentable serialized culling box.");
        return half;
    }

    public static BigInteger DistanceSquared(Point3 center, Point3 point) =>
        Square(Units(point.X) - Units(center.X)) + Square(Units(point.Y) - Units(center.Y)) + Square(Units(point.Z) - Units(center.Z));

    public static float CeilingRadius(BigInteger squaredUnits)
    {
        if (squaredUnits.Sign <= 0 || squaredUnits > Square(MaximumUnits))
            throw new OverflowException("Unrepresentable culling radius.");
        var radius = (float)Math.ScaleB(Math.Sqrt((double)squaredUnits), -149);
        while (Square(Units(radius)) < squaredUnits) radius = MathF.BitIncrement(radius);
        while (radius > 0 && Square(Units(MathF.BitDecrement(radius))) >= squaredUnits) radius = MathF.BitDecrement(radius);
        return radius;
    }
}
