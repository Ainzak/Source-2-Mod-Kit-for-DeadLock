using System.Numerics;

namespace S2ModKit.Geometry;

/// <summary>
/// Independently checks observed numerical output, never calling CullingEnvelope or its encoders.
/// Shared primitives are exact float decoding, nearest-even midpoint rounding, validation and
/// bit comparison. Minimal half-extents/radii are proven by rejecting the preceding float,
/// rather than reproducing the calculator's outward search. This is not a resource reopen audit.
/// Failure precedence: invalid input, source shrinkage, escaped points, policy mismatch.
/// A changed sphere center is rejected as a policy mismatch before fixed-center containment.
/// </summary>
public static class CullingEnvelopeVerifier
{
    public static EnvelopeVerification Verify(Bounds3 original, IReadOnlyList<Point3>? points, Bounds3 observed)
    {
        if (points is null || !Valid(original) || !Valid(observed)) return Result(EnvelopeVerificationStatus.InvalidInput);
        if (!observed.Contains(original.Min) || !observed.Contains(original.Max)) return Result(EnvelopeVerificationStatus.OriginalEnvelopeShrinkage);
        if (points.Any(point => !observed.Contains(point))) return Result(EnvelopeVerificationStatus.EscapedGeometry);
        for (var axis = 0; axis < 3; axis++)
        {
            var low = EnvelopeNumbers.Component(original.Min, axis);
            var high = EnvelopeNumbers.Component(original.Max, axis);
            foreach (var point in points)
            {
                var value = EnvelopeNumbers.Component(point, axis);
                if (value < low) low = value == 0f ? 0f : value;
                if (value > high) high = value == 0f ? 0f : value;
            }

            if (!EnvelopeNumbers.Same(low, EnvelopeNumbers.Component(observed.Min, axis))
                || !EnvelopeNumbers.Same(high, EnvelopeNumbers.Component(observed.Max, axis)))
                return Result(EnvelopeVerificationStatus.PolicyOutputMismatch);
        }

        return Result(EnvelopeVerificationStatus.Passed);
    }

    public static EnvelopeVerification Verify(CenterHalfExtentBounds original, IReadOnlyList<Point3>? points, CenterHalfExtentBounds observed)
    {
        if (points is null || !Valid(original) || !Valid(observed)) return Result(EnvelopeVerificationStatus.InvalidInput);
        for (var axis = 0; axis < 3; axis++)
        {
            var c0 = EnvelopeNumbers.Component(original.Center, axis);
            var h0 = EnvelopeNumbers.Component(original.HalfExtents, axis);
            var c1 = EnvelopeNumbers.Component(observed.Center, axis);
            var h1 = EnvelopeNumbers.Component(observed.HalfExtents, axis);
            if (EnvelopeNumbers.Units(c1) - EnvelopeNumbers.Units(h1) > EnvelopeNumbers.Units(c0) - EnvelopeNumbers.Units(h0)
                || EnvelopeNumbers.Units(c1) + EnvelopeNumbers.Units(h1) < EnvelopeNumbers.Units(c0) + EnvelopeNumbers.Units(h0)
                || c1 - h1 > c0 - h0 || c1 + h1 < c0 + h0)
                return Result(EnvelopeVerificationStatus.OriginalEnvelopeShrinkage);
        }

        if (points.Any(point => !Contains(observed, point))) return Result(EnvelopeVerificationStatus.EscapedGeometry);
        for (var axis = 0; axis < 3; axis++)
        {
            var center = EnvelopeNumbers.Component(original.Center, axis);
            var half = EnvelopeNumbers.Component(original.HalfExtents, axis);
            var actualCenter = EnvelopeNumbers.Component(observed.Center, axis);
            var actualHalf = EnvelopeNumbers.Component(observed.HalfExtents, axis);
            var retain = points.All(point => Fits(center, half, EnvelopeNumbers.Units(EnvelopeNumbers.Component(point, axis))));
            if (retain)
            {
                if (!EnvelopeNumbers.Same(center, actualCenter) || !EnvelopeNumbers.Same(half, actualHalf))
                    return Result(EnvelopeVerificationStatus.PolicyOutputMismatch);
                continue;
            }

            var endpoints = new List<BigInteger>
            {
                EnvelopeNumbers.Units(center) - EnvelopeNumbers.Units(half),
                EnvelopeNumbers.Units(center) + EnvelopeNumbers.Units(half),
                EnvelopeNumbers.Units(center - half), EnvelopeNumbers.Units(center + half),
            };
            endpoints.AddRange(points.Select(point => EnvelopeNumbers.Units(EnvelopeNumbers.Component(point, axis))));
            var low = endpoints.Min();
            var high = endpoints.Max();
            // Reject unrepresentability rather than allowing an exception to escape.
            float expectedCenter;
            try { expectedCenter = EnvelopeNumbers.Midpoint(low, high); }
            catch (OverflowException) { return Result(EnvelopeVerificationStatus.InvalidInput); }
            if (!EnvelopeNumbers.Same(expectedCenter, actualCenter)
                || !Fits(actualCenter, actualHalf, low) || !Fits(actualCenter, actualHalf, high)
                || (Fits(actualCenter, MathF.BitDecrement(actualHalf), low) && Fits(actualCenter, MathF.BitDecrement(actualHalf), high)))
                return Result(EnvelopeVerificationStatus.PolicyOutputMismatch);
        }

        return Result(EnvelopeVerificationStatus.Passed);
    }

    public static EnvelopeVerification Verify(FixedCenterSphere original, IReadOnlyList<Point3>? points, FixedCenterSphere observed)
    {
        if (points is null || !Valid(original) || !Valid(observed)) return Result(EnvelopeVerificationStatus.InvalidInput);
        if (!EnvelopeNumbers.Same(original.Center, observed.Center)) return Result(EnvelopeVerificationStatus.PolicyOutputMismatch);
        if (observed.Radius < original.Radius) return Result(EnvelopeVerificationStatus.OriginalEnvelopeShrinkage);
        var actualSquared = EnvelopeNumbers.Units(observed.Radius) * EnvelopeNumbers.Units(observed.Radius);
        var previousRadius = MathF.BitDecrement(observed.Radius);
        var previousSquared = EnvelopeNumbers.Units(previousRadius) * EnvelopeNumbers.Units(previousRadius);
        var previousWouldPass = previousRadius >= original.Radius;
        foreach (var point in points)
        {
            BigInteger distance = 0;
            for (var axis = 0; axis < 3; axis++)
            {
                var difference = EnvelopeNumbers.Units(EnvelopeNumbers.Component(point, axis)) - EnvelopeNumbers.Units(EnvelopeNumbers.Component(original.Center, axis));
                distance += difference * difference;
            }

            if (distance > actualSquared) return Result(EnvelopeVerificationStatus.EscapedGeometry);
            previousWouldPass &= distance <= previousSquared;
        }

        return Result(previousWouldPass ? EnvelopeVerificationStatus.PolicyOutputMismatch : EnvelopeVerificationStatus.Passed);
    }

    private static bool Contains(CenterHalfExtentBounds box, Point3 point) =>
        Enumerable.Range(0, 3).All(axis => Fits(EnvelopeNumbers.Component(box.Center, axis), EnvelopeNumbers.Component(box.HalfExtents, axis), EnvelopeNumbers.Units(EnvelopeNumbers.Component(point, axis))));

    private static bool Fits(float center, float half, BigInteger point) =>
        EnvelopeNumbers.Units(center) - EnvelopeNumbers.Units(half) <= point
        && EnvelopeNumbers.Units(center) + EnvelopeNumbers.Units(half) >= point
        && EnvelopeNumbers.Units(center - half) <= point
        && EnvelopeNumbers.Units(center + half) >= point;

    private static bool Valid(Bounds3 value)
    {
        try { EnvelopeNumbers.Validate(value); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool Valid(CenterHalfExtentBounds value)
    {
        try { EnvelopeNumbers.Validate(value); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool Valid(FixedCenterSphere value)
    {
        try { EnvelopeNumbers.Validate(value); return true; }
        catch (ArgumentException) { return false; }
    }

    private static EnvelopeVerification Result(EnvelopeVerificationStatus status) => new(status);
}
