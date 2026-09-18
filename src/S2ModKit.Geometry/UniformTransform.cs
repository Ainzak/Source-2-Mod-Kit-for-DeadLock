namespace S2ModKit.Geometry;

/// <summary>
/// An immutable uniform positive component transform: one frozen pivot, one scale
/// in <see cref="MinimumScale"/>..<see cref="MaximumScale"/> inclusive, and one
/// translation. A point maps to exactly <c>pivot + (position - pivot) * scale + translation</c>,
/// evaluated left to right in single precision. The kernel accepts an identity
/// transform; recipe validation rejects it at a later boundary.
/// </summary>
public sealed record UniformTransform
{
    public const float MinimumScale = 0.25f;

    public const float MaximumScale = 4.0f;

    public UniformTransform(Point3 pivot, float scale, Point3 translation)
    {
        ScalarValidation.RequireFinite(scale, nameof(scale));
        if (scale < MinimumScale || scale > MaximumScale)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scale),
                scale,
                $"Scale must be within [{MinimumScale.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}..{MaximumScale.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}] inclusive.");
        }

        Pivot = pivot;
        Scale = scale;
        Translation = translation;
    }

    public Point3 Pivot { get; }

    public float Scale { get; }

    public Point3 Translation { get; }

    public Point3 Apply(Point3 point) => Pivot + ((point - Pivot) * Scale) + Translation;

    public Point3[] Apply(IReadOnlyList<Point3> points)
    {
        ValidatePoints(points);
        var transformed = new Point3[points.Count];
        for (var index = 0; index < points.Count; index++)
        {
            transformed[index] = Apply(points[index]);
        }

        return transformed;
    }

    /// <summary>
    /// Applies this transform exactly once to each source point, writes the results into
    /// <paramref name="transformedPoints"/>, and returns facts derived from those exact results.
    /// </summary>
    public TransformSummary ApplyAndSummarize(
        IReadOnlyList<Point3> points,
        Point3[] transformedPoints)
    {
        ValidatePoints(points);
        ArgumentNullException.ThrowIfNull(transformedPoints);
        if (transformedPoints.Length != points.Count)
        {
            throw new ArgumentException(
                $"The destination contains {transformedPoints.Length} points; exactly {points.Count} are required.",
                nameof(transformedPoints));
        }

        var beforeBounds = Bounds3.FromPoints(points);
        var changedPointCount = 0;
        var maximumSquaredDisplacement = 0.0;
        for (var index = 0; index < points.Count; index++)
        {
            var original = points[index];
            var transformed = Apply(original);
            transformedPoints[index] = transformed;
            if (!HasIdenticalBits(original, transformed))
            {
                changedPointCount = checked(changedPointCount + 1);
            }

            var deltaX = (double)transformed.X - (double)original.X;
            var deltaY = (double)transformed.Y - (double)original.Y;
            var deltaZ = (double)transformed.Z - (double)original.Z;
            var squaredDisplacement = (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ);
            if (squaredDisplacement > maximumSquaredDisplacement)
            {
                maximumSquaredDisplacement = squaredDisplacement;
            }
        }

        var afterBounds = Bounds3.FromPoints(transformedPoints);
        var maximumDisplacement = (float)Math.Sqrt(maximumSquaredDisplacement);
        if (!float.IsFinite(maximumDisplacement))
        {
            throw new OverflowException(
                "The maximum displacement of the transformed points exceeds the finite float range.");
        }

        return new TransformSummary(beforeBounds, afterBounds, changedPointCount, maximumDisplacement);
    }

    public TransformSummary Summarize(IReadOnlyList<Point3> points)
    {
        ValidatePoints(points);
        var transformedPoints = new Point3[points.Count];
        return ApplyAndSummarize(points, transformedPoints);
    }

    private static void ValidatePoints(IReadOnlyList<Point3> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            throw new ArgumentException("At least one point is required.", nameof(points));
        }
    }

    private static bool HasIdenticalBits(Point3 left, Point3 right) =>
        BitConverter.SingleToInt32Bits(left.X) == BitConverter.SingleToInt32Bits(right.X)
        && BitConverter.SingleToInt32Bits(left.Y) == BitConverter.SingleToInt32Bits(right.Y)
        && BitConverter.SingleToInt32Bits(left.Z) == BitConverter.SingleToInt32Bits(right.Z);
}
