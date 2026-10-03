using System.Collections.Immutable;

namespace S2ModKit.Geometry;

/// <summary>Raw serialized box words; admission is checked by the calculator/verifier.</summary>
public readonly record struct CenterHalfExtentBounds(Point3 Center, Point3 HalfExtents);

/// <summary>A mathematical Euclidean sphere, not a claim about any resource's coordinate frame.</summary>
public readonly record struct FixedCenterSphere(Point3 Center, float Radius);

/// <summary>Diagnostic measurements only. Containment never depends on these rounded doubles.</summary>
public readonly record struct EnvelopeGrowth(string Measure, double OriginalValue, double PlannedValue, double Delta);

public sealed record EnvelopeCalculation<T>(T Bounds, bool Changed, ImmutableArray<EnvelopeGrowth> Growth);

public enum EnvelopeVerificationStatus
{
    Passed,
    InvalidInput,
    OriginalEnvelopeShrinkage,
    EscapedGeometry,
    PolicyOutputMismatch,
}

/// <summary>Numerical verification only: does not verify resources, ownership, or contributor completeness.</summary>
public readonly record struct EnvelopeVerification(EnvelopeVerificationStatus Status)
{
    public bool Passed => Status == EnvelopeVerificationStatus.Passed;
}

/// <summary>
/// Retain-and-expand math for complete point sets in an already characterized field space.
/// Null means unavailable and is rejected; an explicitly empty set preserves valid original words.
/// Callers must supply final serialized positions, including all unchanged contributors.
/// No Source 2 planner or writer calls this experimental library.
/// </summary>
public static class CullingEnvelope
{
    public static EnvelopeCalculation<Bounds3> Expand(Bounds3 original, IReadOnlyList<Point3> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        EnvelopeNumbers.Validate(original);
        var minimum = new float[3];
        var maximum = new float[3];
        var growth = ImmutableArray.CreateBuilder<EnvelopeGrowth>(6);
        for (var axis = 0; axis < 3; axis++)
        {
            var low = EnvelopeNumbers.Component(original.Min, axis);
            var high = EnvelopeNumbers.Component(original.Max, axis);
            minimum[axis] = low;
            maximum[axis] = high;
            foreach (var point in points)
            {
                var value = EnvelopeNumbers.Component(point, axis);
                // Strict comparisons preserve authored signed-zero words on ties.
                if (value < minimum[axis]) minimum[axis] = value == 0f ? 0f : value;
                if (value > maximum[axis]) maximum[axis] = value == 0f ? 0f : value;
            }

            growth.Add(EnvelopeNumbers.Growth($"minimum_{axis}", EnvelopeNumbers.Units(low), EnvelopeNumbers.Units(minimum[axis])));
            growth.Add(EnvelopeNumbers.Growth($"maximum_{axis}", EnvelopeNumbers.Units(high), EnvelopeNumbers.Units(maximum[axis])));
        }

        var result = new Bounds3(new(minimum[0], minimum[1], minimum[2]), new(maximum[0], maximum[1], maximum[2]));
        return new(result, !EnvelopeNumbers.Same(original.Min, result.Min) || !EnvelopeNumbers.Same(original.Max, result.Max), growth.ToImmutable());
    }

    public static EnvelopeCalculation<CenterHalfExtentBounds> Expand(CenterHalfExtentBounds original, IReadOnlyList<Point3> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        EnvelopeNumbers.Validate(original);
        var centers = new float[3];
        var halves = new float[3];
        var growth = ImmutableArray.CreateBuilder<EnvelopeGrowth>(6);
        for (var axis = 0; axis < 3; axis++)
        {
            var center = EnvelopeNumbers.Component(original.Center, axis);
            var half = EnvelopeNumbers.Component(original.HalfExtents, axis);
            var oldLow = EnvelopeNumbers.Units(center) - EnvelopeNumbers.Units(half);
            var oldHigh = EnvelopeNumbers.Units(center) + EnvelopeNumbers.Units(half);
            var low = System.Numerics.BigInteger.Min(oldLow, EnvelopeNumbers.Units(center - half));
            var high = System.Numerics.BigInteger.Max(oldHigh, EnvelopeNumbers.Units(center + half));
            var retained = true;
            foreach (var point in points)
            {
                var value = EnvelopeNumbers.Units(EnvelopeNumbers.Component(point, axis));
                retained &= EnvelopeNumbers.Contains(center, half, value, value);
                low = System.Numerics.BigInteger.Min(low, value);
                high = System.Numerics.BigInteger.Max(high, value);
            }

            centers[axis] = retained ? center : EnvelopeNumbers.Midpoint(low, high);
            halves[axis] = retained ? half : EnvelopeNumbers.EncodeHalf(centers[axis], low, high);
            growth.Add(EnvelopeNumbers.Growth($"minimum_{axis}", oldLow, EnvelopeNumbers.Units(centers[axis]) - EnvelopeNumbers.Units(halves[axis])));
            growth.Add(EnvelopeNumbers.Growth($"maximum_{axis}", oldHigh, EnvelopeNumbers.Units(centers[axis]) + EnvelopeNumbers.Units(halves[axis])));
        }

        var result = new CenterHalfExtentBounds(new(centers[0], centers[1], centers[2]), new(halves[0], halves[1], halves[2]));
        return new(result, !EnvelopeNumbers.Same(original.Center, result.Center) || !EnvelopeNumbers.Same(original.HalfExtents, result.HalfExtents), growth.ToImmutable());
    }

    public static EnvelopeCalculation<FixedCenterSphere> Expand(FixedCenterSphere original, IReadOnlyList<Point3> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        EnvelopeNumbers.Validate(original);
        var requiredSquared = EnvelopeNumbers.Square(EnvelopeNumbers.Units(original.Radius));
        foreach (var point in points)
        {
            requiredSquared = System.Numerics.BigInteger.Max(requiredSquared, EnvelopeNumbers.DistanceSquared(original.Center, point));
        }

        var radius = EnvelopeNumbers.CeilingRadius(requiredSquared);
        var result = new FixedCenterSphere(original.Center, radius);
        return new(result, !EnvelopeNumbers.Same(original.Radius, radius),
            [EnvelopeNumbers.Growth("radius", EnvelopeNumbers.Units(original.Radius), EnvelopeNumbers.Units(radius))]);
    }
}
