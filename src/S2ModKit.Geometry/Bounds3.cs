namespace S2ModKit.Geometry;

/// <summary>
/// An immutable axis-aligned bounds pair. <see cref="Min"/> must not exceed
/// <see cref="Max"/> on any axis. Center arithmetic uses double intermediates
/// so every finite single-precision bounds pair has a finite midpoint.
/// </summary>
public readonly record struct Bounds3
{
    public Bounds3(Point3 min, Point3 max)
    {
        if (min.X > max.X || min.Y > max.Y || min.Z > max.Z)
        {
            throw new ArgumentException("Bounds minimum must not exceed its maximum on any axis.");
        }

        Min = min;
        Max = max;
    }

    public Point3 Min { get; }

    public Point3 Max { get; }

    public Point3 Center => new(
        Midpoint(Min.X, Max.X),
        Midpoint(Min.Y, Max.Y),
        Midpoint(Min.Z, Max.Z));

    public bool Contains(Point3 point) =>
        point.X >= Min.X && point.X <= Max.X
        && point.Y >= Min.Y && point.Y <= Max.Y
        && point.Z >= Min.Z && point.Z <= Max.Z;

    public static Bounds3 FromPoints(IReadOnlyList<Point3> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            throw new ArgumentException("At least one point is required to form bounds.", nameof(points));
        }

        var minX = points[0].X;
        var minY = points[0].Y;
        var minZ = points[0].Z;
        var maxX = minX;
        var maxY = minY;
        var maxZ = minZ;
        for (var index = 1; index < points.Count; index++)
        {
            var point = points[index];
            if (point.X < minX)
            {
                minX = point.X;
            }

            if (point.Y < minY)
            {
                minY = point.Y;
            }

            if (point.Z < minZ)
            {
                minZ = point.Z;
            }

            if (point.X > maxX)
            {
                maxX = point.X;
            }

            if (point.Y > maxY)
            {
                maxY = point.Y;
            }

            if (point.Z > maxZ)
            {
                maxZ = point.Z;
            }
        }

        return new Bounds3(new Point3(minX, minY, minZ), new Point3(maxX, maxY, maxZ));
    }

    private static float Midpoint(float minimum, float maximum) =>
        (float)(((double)minimum + maximum) * 0.5d);
}
