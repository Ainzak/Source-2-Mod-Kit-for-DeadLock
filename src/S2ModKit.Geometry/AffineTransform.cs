namespace S2ModKit.Geometry;

/// <summary>A finite 3-by-3 single-precision matrix using row-major storage.</summary>
public readonly record struct Matrix3
{
    public Matrix3(
        float m11, float m12, float m13,
        float m21, float m22, float m23,
        float m31, float m32, float m33)
    {
        foreach (var value in new[] { m11, m12, m13, m21, m22, m23, m31, m32, m33 })
        {
            ScalarValidation.RequireFinite(value, nameof(value));
        }

        M11 = m11; M12 = m12; M13 = m13;
        M21 = m21; M22 = m22; M23 = m23;
        M31 = m31; M32 = m32; M33 = m33;
    }

    public float M11 { get; }
    public float M12 { get; }
    public float M13 { get; }
    public float M21 { get; }
    public float M22 { get; }
    public float M23 { get; }
    public float M31 { get; }
    public float M32 { get; }
    public float M33 { get; }

    public static Matrix3 Identity { get; } = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public float Determinant =>
        (M11 * ((M22 * M33) - (M23 * M32)))
        - (M12 * ((M21 * M33) - (M23 * M31)))
        + (M13 * ((M21 * M32) - (M22 * M31)));

    public Matrix3 Transpose() => new(
        M11, M21, M31,
        M12, M22, M32,
        M13, M23, M33);

    public Matrix3 Inverse()
    {
        var determinant = Determinant;
        if (!float.IsFinite(determinant) || determinant == 0f)
        {
            throw new ArgumentException("Matrix must be finite and invertible.");
        }

        var inverse = 1f / determinant;
        return new Matrix3(
            ((M22 * M33) - (M23 * M32)) * inverse,
            ((M13 * M32) - (M12 * M33)) * inverse,
            ((M12 * M23) - (M13 * M22)) * inverse,
            ((M23 * M31) - (M21 * M33)) * inverse,
            ((M11 * M33) - (M13 * M31)) * inverse,
            ((M13 * M21) - (M11 * M23)) * inverse,
            ((M21 * M32) - (M22 * M31)) * inverse,
            ((M12 * M31) - (M11 * M32)) * inverse,
            ((M11 * M22) - (M12 * M21)) * inverse);
    }

    public Point3 Apply(Point3 value) => new(
        ((M11 * value.X) + (M12 * value.Y)) + (M13 * value.Z),
        ((M21 * value.X) + (M22 * value.Y)) + (M23 * value.Z),
        ((M31 * value.X) + (M32 * value.Y)) + (M33 * value.Z));

    public static Matrix3 operator *(Matrix3 left, Matrix3 right) => new(
        ((left.M11 * right.M11) + (left.M12 * right.M21)) + (left.M13 * right.M31),
        ((left.M11 * right.M12) + (left.M12 * right.M22)) + (left.M13 * right.M32),
        ((left.M11 * right.M13) + (left.M12 * right.M23)) + (left.M13 * right.M33),
        ((left.M21 * right.M11) + (left.M22 * right.M21)) + (left.M23 * right.M31),
        ((left.M21 * right.M12) + (left.M22 * right.M22)) + (left.M23 * right.M32),
        ((left.M21 * right.M13) + (left.M22 * right.M23)) + (left.M23 * right.M33),
        ((left.M31 * right.M11) + (left.M32 * right.M21)) + (left.M33 * right.M31),
        ((left.M31 * right.M12) + (left.M32 * right.M22)) + (left.M33 * right.M32),
        ((left.M31 * right.M13) + (left.M32 * right.M23)) + (left.M33 * right.M33));
}

public readonly record struct AffineScale
{
    public AffineScale(float x, float y, float z)
    {
        Validate(x, nameof(x));
        Validate(y, nameof(y));
        Validate(z, nameof(z));
        X = x; Y = y; Z = z;
    }

    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public bool IsIdentity => X == 1f && Y == 1f && Z == 1f;
    public Matrix3 ToMatrix() => new(X, 0, 0, 0, Y, 0, 0, 0, Z);

    private static void Validate(float value, string name)
    {
        ScalarValidation.RequireFinite(value, name);
        if (value < UniformTransform.MinimumScale || value > UniformTransform.MaximumScale)
        {
            throw new ArgumentOutOfRangeException(name, value, "Scale must be within [0.25, 4] inclusive.");
        }
    }
}

public sealed record AxisAngleRotation
{
    public const float UnitTolerance = 1e-4f;

    private AxisAngleRotation() => IsIdentity = true;

    public AxisAngleRotation(Point3 axis, float degrees)
    {
        ScalarValidation.RequireFinite(degrees, nameof(degrees));
        if (degrees <= -180f || degrees > 180f || degrees == 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "Axis-angle degrees must be in (-180, 180] and non-zero.");
        }

        var length = Length(axis);
        if (MathF.Abs(length - 1f) > UnitTolerance)
        {
            throw new ArgumentException("Rotation axis must have unit length within tolerance.", nameof(axis));
        }

        Axis = new Point3(axis.X / length, axis.Y / length, axis.Z / length);
        Degrees = degrees;
    }

    public static AxisAngleRotation Identity { get; } = new();
    public bool IsIdentity { get; }
    public Point3 Axis { get; } = new(1, 0, 0);
    public float Degrees { get; }

    public Matrix3 ToMatrix()
    {
        if (IsIdentity)
        {
            return Matrix3.Identity;
        }

        var radians = Degrees * (MathF.PI / 180f);
        var cosine = MathF.Cos(radians);
        var sine = MathF.Sin(radians);
        var oneMinusCosine = 1f - cosine;
        var x = Axis.X; var y = Axis.Y; var z = Axis.Z;
        return new Matrix3(
            cosine + (x * x * oneMinusCosine),
            (x * y * oneMinusCosine) - (z * sine),
            (x * z * oneMinusCosine) + (y * sine),
            (y * x * oneMinusCosine) + (z * sine),
            cosine + (y * y * oneMinusCosine),
            (y * z * oneMinusCosine) - (x * sine),
            (z * x * oneMinusCosine) - (y * sine),
            (z * y * oneMinusCosine) + (x * sine),
            cosine + (z * z * oneMinusCosine));
    }

    private static float Length(Point3 value) =>
        (float)Math.Sqrt(((double)value.X * value.X) + ((double)value.Y * value.Y) + ((double)value.Z * value.Z));
}

public sealed record RigidFrame
{
    public const float Tolerance = 1e-4f;

    public RigidFrame(Matrix3 toModel)
    {
        var x = new Point3(toModel.M11, toModel.M21, toModel.M31);
        var y = new Point3(toModel.M12, toModel.M22, toModel.M32);
        var z = new Point3(toModel.M13, toModel.M23, toModel.M33);
        if (MathF.Abs(LengthSquared(x) - 1f) > Tolerance
            || MathF.Abs(LengthSquared(y) - 1f) > Tolerance
            || MathF.Abs(LengthSquared(z) - 1f) > Tolerance
            || MathF.Abs(Dot(x, y)) > Tolerance
            || MathF.Abs(Dot(x, z)) > Tolerance
            || MathF.Abs(Dot(y, z)) > Tolerance
            || toModel.Determinant <= 0f
            || MathF.Abs(toModel.Determinant - 1f) > Tolerance)
        {
            throw new ArgumentException("Frame basis must be finite, rigid, orthonormal, and proper.", nameof(toModel));
        }

        ToModel = toModel;
        ToFrame = toModel.Transpose();
    }

    public static RigidFrame Model { get; } = new(Matrix3.Identity);
    public Matrix3 ToModel { get; }
    public Matrix3 ToFrame { get; }

    private static float Dot(Point3 left, Point3 right) =>
        ((left.X * right.X) + (left.Y * right.Y)) + (left.Z * right.Z);

    private static float LengthSquared(Point3 value) => Dot(value, value);
}

public sealed record TangentFrame(Point3 Normal, Point3 Tangent, float Handedness);

/// <summary>Deterministic positive affine transform with the ADR-0026 composition order.</summary>
public sealed record AffineTransform
{
    public AffineTransform(
        Point3 pivot,
        AffineScale scale,
        AxisAngleRotation rotation,
        RigidFrame frame,
        Point3 translation)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        ArgumentNullException.ThrowIfNull(frame);
        Pivot = pivot;
        Scale = scale;
        Rotation = rotation;
        Frame = frame;
        Translation = translation;
        LinearMap = (frame.ToModel * rotation.ToMatrix() * scale.ToMatrix()) * frame.ToFrame;
        if (!float.IsFinite(LinearMap.Determinant) || LinearMap.Determinant <= 0f)
        {
            throw new ArgumentException("Affine linear map must be finite, invertible, and orientation-preserving.");
        }
    }

    public Point3 Pivot { get; }
    public AffineScale Scale { get; }
    public AxisAngleRotation Rotation { get; }
    public RigidFrame Frame { get; }
    public Point3 Translation { get; }
    public Matrix3 LinearMap { get; }
    public bool IsIdentity => Scale.IsIdentity && Rotation.IsIdentity && Translation == new Point3(0, 0, 0);

    public Point3 Apply(Point3 point) => Pivot + LinearMap.Apply(point - Pivot) + Translation;

    public TangentFrame Apply(TangentFrame value)
    {
        if (value.Handedness is not (-1f or 1f))
        {
            throw new ArgumentException("Tangent handedness must be exactly -1 or 1.", nameof(value));
        }

        var inputNormalLength = Dot(value.Normal, value.Normal);
        var inputTangentLength = Dot(value.Tangent, value.Tangent);
        if (MathF.Abs(inputNormalLength - 1f) > 1e-4f
            || MathF.Abs(inputTangentLength - 1f) > 1e-4f
            || MathF.Abs(Dot(value.Normal, value.Tangent)) > 1e-4f)
        {
            throw new ArgumentException("Input normal and tangent must be finite, unit length, and orthogonal.", nameof(value));
        }

        var normal = Normalize(LinearMap.Inverse().Transpose().Apply(value.Normal), "normal");
        var transformedTangent = LinearMap.Apply(value.Tangent);
        var projection = Dot(normal, transformedTangent);
        var orthogonal = transformedTangent - (normal * projection);
        var tangent = Normalize(orthogonal, "tangent");
        if (MathF.Abs(Dot(normal, tangent)) > 1e-4f)
        {
            throw new ArgumentException("Transformed tangent frame is not orthogonal.", nameof(value));
        }

        return new TangentFrame(normal, tangent, value.Handedness);
    }

    public TransformSummary ApplyAndSummarize(IReadOnlyList<Point3> points, Point3[] transformedPoints)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(transformedPoints);
        if (points.Count == 0 || transformedPoints.Length != points.Count)
        {
            throw new ArgumentException("A non-empty destination with exactly one entry per source point is required.", nameof(transformedPoints));
        }

        var changed = 0;
        var maximumSquared = 0d;
        for (var index = 0; index < points.Count; index++)
        {
            var before = points[index];
            var after = Apply(before);
            transformedPoints[index] = after;
            if (!HasIdenticalBits(before, after))
            {
                changed++;
            }

            var x = (double)after.X - before.X;
            var y = (double)after.Y - before.Y;
            var z = (double)after.Z - before.Z;
            maximumSquared = Math.Max(maximumSquared, (x * x) + (y * y) + (z * z));
        }

        var maximum = (float)Math.Sqrt(maximumSquared);
        if (!float.IsFinite(maximum))
        {
            throw new OverflowException("Maximum displacement exceeds the finite float range.");
        }

        return new TransformSummary(Bounds3.FromPoints(points), Bounds3.FromPoints(transformedPoints), changed, maximum);
    }

    public TransformSummary Summarize(IReadOnlyList<Point3> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        return ApplyAndSummarize(points, new Point3[points.Count]);
    }

    public static Point3 ResolveBoundsFace(Bounds3 bounds, string face) => face switch
    {
        "min_x" => new Point3(bounds.Min.X, bounds.Center.Y, bounds.Center.Z),
        "max_x" => new Point3(bounds.Max.X, bounds.Center.Y, bounds.Center.Z),
        "min_y" => new Point3(bounds.Center.X, bounds.Min.Y, bounds.Center.Z),
        "max_y" => new Point3(bounds.Center.X, bounds.Max.Y, bounds.Center.Z),
        "min_z" => new Point3(bounds.Center.X, bounds.Center.Y, bounds.Min.Z),
        "max_z" => new Point3(bounds.Center.X, bounds.Center.Y, bounds.Max.Z),
        _ => throw new ArgumentException("Bounds face must be min_x, max_x, min_y, max_y, min_z, or max_z.", nameof(face)),
    };

    private static Point3 Normalize(Point3 value, string name)
    {
        var squared = ((double)value.X * value.X) + ((double)value.Y * value.Y) + ((double)value.Z * value.Z);
        if (!double.IsFinite(squared) || squared <= 1e-20d)
        {
            throw new ArgumentException($"Transformed {name} is degenerate.", name);
        }

        var inverse = (float)(1d / Math.Sqrt(squared));
        return new Point3(value.X * inverse, value.Y * inverse, value.Z * inverse);
    }

    private static float Dot(Point3 left, Point3 right) =>
        ((left.X * right.X) + (left.Y * right.Y)) + (left.Z * right.Z);

    private static bool HasIdenticalBits(Point3 left, Point3 right) =>
        BitConverter.SingleToInt32Bits(left.X) == BitConverter.SingleToInt32Bits(right.X)
        && BitConverter.SingleToInt32Bits(left.Y) == BitConverter.SingleToInt32Bits(right.Y)
        && BitConverter.SingleToInt32Bits(left.Z) == BitConverter.SingleToInt32Bits(right.Z);
}
