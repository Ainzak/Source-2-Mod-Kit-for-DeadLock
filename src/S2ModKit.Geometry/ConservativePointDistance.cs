using System.Numerics;

namespace S2ModKit.Geometry;

/// <summary>The least finite binary32 distance that encloses the exact distance between stored points.</summary>
public static class ConservativePointDistance
{
    public static float RoundUp(Point3 before, Point3 after)
    {
        // Binary32 coordinates are exact integer multiples of 2^-149. Compare squared
        // distances in these units; binary64 sqrt only supplies an initial candidate.
        static BigInteger Units(float value) => new(Math.ScaleB((double)value, 149));
        var x = Units(after.X) - Units(before.X);
        var y = Units(after.Y) - Units(before.Y);
        var z = Units(after.Z) - Units(before.Z);
        var squared = (x * x) + (y * y) + (z * z);
        if (squared.IsZero) return 0;
        var dx = (double)after.X - before.X;
        var dy = (double)after.Y - before.Y;
        var dz = (double)after.Z - before.Z;
        var candidate = (float)Math.Sqrt(((dx * dx) + (dy * dy)) + (dz * dz));
        while (true)
        {
            if (!float.IsFinite(candidate)) throw new ArgumentException("The point distance cannot be enclosed by a finite float.");
            var units = Units(candidate);
            if (units * units >= squared) break;
            candidate = MathF.BitIncrement(candidate);
        }
        while (candidate > 0)
        {
            var previous = MathF.BitDecrement(candidate);
            var units = Units(previous);
            if (units * units < squared) break;
            candidate = previous;
        }
        return candidate;
    }
}
