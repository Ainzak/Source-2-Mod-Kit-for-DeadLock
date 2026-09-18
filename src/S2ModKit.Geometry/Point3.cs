namespace S2ModKit.Geometry;

/// <summary>
/// An immutable three-component single-precision point or direction.
/// Every constructed value is finite; non-finite components are rejected.
/// </summary>
public readonly record struct Point3
{
    public Point3(float x, float y, float z)
    {
        ScalarValidation.RequireFinite(x, nameof(x));
        ScalarValidation.RequireFinite(y, nameof(y));
        ScalarValidation.RequireFinite(z, nameof(z));
        X = x;
        Y = y;
        Z = z;
    }

    public float X { get; }

    public float Y { get; }

    public float Z { get; }

    public static Point3 operator +(Point3 left, Point3 right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    public static Point3 operator -(Point3 left, Point3 right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    public static Point3 operator *(Point3 left, float scalar) =>
        new(left.X * scalar, left.Y * scalar, left.Z * scalar);
}

internal static class ScalarValidation
{
    public static void RequireFinite(float value, string parameterName)
    {
        if (float.IsFinite(value))
        {
            return;
        }

        throw new ArgumentException(
            $"Value must be finite, but '{parameterName}' was {Display(value)}.",
            parameterName);
    }

    private static string Display(float value) => value switch
    {
        float.NaN => "NaN",
        float.PositiveInfinity => "+infinity",
        float.NegativeInfinity => "-infinity",
        _ => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
    };
}
