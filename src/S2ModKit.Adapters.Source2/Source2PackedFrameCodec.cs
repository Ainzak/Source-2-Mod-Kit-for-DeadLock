using System.Buffers.Binary;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

/// <summary>
/// Bounded codec for the characterized Source 2 NORMAL R32_UINT normal/tangent frame.
/// Planning uses it to freeze exact packed results; mutation remains a separate writer concern.
/// </summary>
internal static class Source2PackedFrameCodec
{
    public const string EncodingProfile = "source2_normal_tangent_v2";
    private const float MinimumDirectionDot = 0.9999f;

    public static TangentFrame Decode(uint packed)
    {
        var handedness = (packed & 1u) == 0u ? -1f : 1f;
        var tangentBits = (packed >> 1) & 0x7ffu;
        var x = (((packed >> 12) & 0x3ffu) / 1023f * 2f) - 1f;
        var y = (((packed >> 22) & 0x3ffu) / 1023f * 2f) - 1f;
        var z = 1f - MathF.Abs(x) - MathF.Abs(y);
        if (z < 0f)
        {
            var compensation = -z;
            x += x >= 0f ? -compensation : compensation;
            y += y >= 0f ? -compensation : compensation;
        }

        var normal = Normalize(new Point3(x, y, z), "normal");
        var basis = TangentBasis(normal);
        var perpendicular = Cross(normal, basis);
        var angle = tangentBits / 2047f * MathF.Tau;
        var tangent = Normalize((basis * MathF.Cos(angle)) + (perpendicular * MathF.Sin(angle)), "tangent");
        return new TangentFrame(normal, tangent, handedness);
    }

    public static uint Encode(TangentFrame frame)
    {
        ValidateFrame(frame);
        var denominator = MathF.Abs(frame.Normal.X) + MathF.Abs(frame.Normal.Y) + MathF.Abs(frame.Normal.Z);
        if (!float.IsFinite(denominator) || denominator <= 0f)
        {
            throw Unsupported("The normal cannot be projected onto the packed octahedral frame.");
        }

        var x = frame.Normal.X / denominator;
        var y = frame.Normal.Y / denominator;
        var z = frame.Normal.Z / denominator;
        if (z < 0f)
        {
            var oldX = x;
            x = (1f - MathF.Abs(y)) * SignNotZero(oldX);
            y = (1f - MathF.Abs(oldX)) * SignNotZero(y);
        }

        var xBits = Quantize(x, 1023);
        var yBits = Quantize(y, 1023);
        var decodedNormal = Decode((xBits << 12) | (yBits << 22)).Normal;
        var basis = TangentBasis(decodedNormal);
        var perpendicular = Cross(decodedNormal, basis);
        var projectedTangent = Normalize(
            frame.Tangent - (decodedNormal * Dot(decodedNormal, frame.Tangent)),
            "tangent");
        var angle = MathF.Atan2(Dot(projectedTangent, perpendicular), Dot(projectedTangent, basis));
        if (angle < 0f)
        {
            angle += MathF.Tau;
        }

        var tangentBits = checked((uint)Math.Clamp(
            (int)MathF.Round(angle / MathF.Tau * 2047f, MidpointRounding.AwayFromZero),
            0,
            2047));
        var packed = (frame.Handedness > 0f ? 1u : 0u)
            | (tangentBits << 1)
            | (xBits << 12)
            | (yBits << 22);
        var reopened = Decode(packed);
        if (Dot(frame.Normal, reopened.Normal) < MinimumDirectionDot
            || Dot(projectedTangent, reopened.Tangent) < MinimumDirectionDot
            || reopened.Handedness != frame.Handedness)
        {
            throw Unsupported("The packed normal/tangent frame exceeds the characterized angular error limit.");
        }

        return packed;
    }

    public static ContentHash HashSelected(ReadOnlySpan<byte> decoded, PackedFrameLayout layout, IReadOnlyList<int> vertices)
    {
        ValidateLayout(decoded, layout);
        var bytes = new byte[checked(vertices.Count * sizeof(uint))];
        for (var index = 0; index < vertices.Count; index++)
        {
            var vertex = vertices[index];
            ValidateVertex(vertex, decoded.Length / layout.Stride);
            decoded.Slice(checked((vertex * layout.Stride) + layout.Offset), sizeof(uint))
                .CopyTo(bytes.AsSpan(index * sizeof(uint), sizeof(uint)));
        }

        return ContentHash.Compute(bytes);
    }

    public static void TransformSelected(
        Span<byte> decoded,
        PackedFrameLayout layout,
        IReadOnlyList<int> vertices,
        AffineTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        ValidateLayout(decoded, layout);
        var vertexCount = decoded.Length / layout.Stride;
        foreach (var vertex in vertices)
        {
            ValidateVertex(vertex, vertexCount);
            var bytes = decoded.Slice(checked((vertex * layout.Stride) + layout.Offset), sizeof(uint));
            var before = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Encode(transform.Apply(Decode(before))));
        }
    }

    private static void ValidateLayout(ReadOnlySpan<byte> decoded, PackedFrameLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Format != "R32_UINT"
            || layout.EncodingProfile != EncodingProfile
            || layout.Stride < sizeof(uint)
            || layout.Offset < 0
            || layout.Offset > layout.Stride - sizeof(uint)
            || decoded.Length == 0
            || decoded.Length % layout.Stride != 0)
        {
            throw Unsupported("The packed normal/tangent layout is malformed or unsupported.");
        }
    }

    private static void ValidateFrame(TangentFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Handedness is not (-1f or 1f)
            || MathF.Abs(Dot(frame.Normal, frame.Normal) - 1f) > 1e-4f
            || MathF.Abs(Dot(frame.Tangent, frame.Tangent) - 1f) > 1e-4f
            || MathF.Abs(Dot(frame.Normal, frame.Tangent)) > 1e-4f)
        {
            throw Unsupported("The normal/tangent frame is non-finite, non-unit, non-orthogonal, or has invalid handedness.");
        }
    }

    private static Point3 TangentBasis(Point3 normal)
    {
        var sign = normal.Z >= 0f ? 1f : -1f;
        var reciprocal = 1f / (sign + normal.Z);
        return Normalize(new Point3(
            (-sign * normal.X * normal.X * reciprocal) + 1f,
            -sign * normal.X * normal.Y * reciprocal,
            -sign * normal.X), "tangent basis");
    }

    private static uint Quantize(float value, int maximum) => checked((uint)Math.Clamp(
        (int)MathF.Round(((value * 0.5f) + 0.5f) * maximum, MidpointRounding.AwayFromZero),
        0,
        maximum));

    private static float SignNotZero(float value) => value >= 0f ? 1f : -1f;

    private static Point3 Normalize(Point3 value, string name)
    {
        var squared = (double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z;
        if (!double.IsFinite(squared) || squared <= 1e-20d)
        {
            throw Unsupported($"The packed {name} is degenerate.");
        }

        var inverse = (float)(1d / Math.Sqrt(squared));
        return new Point3(value.X * inverse, value.Y * inverse, value.Z * inverse);
    }

    private static Point3 Cross(Point3 left, Point3 right) => new(
        (left.Y * right.Z) - (left.Z * right.Y),
        (left.Z * right.X) - (left.X * right.Z),
        (left.X * right.Y) - (left.Y * right.X));

    private static float Dot(Point3 left, Point3 right) =>
        ((left.X * right.X) + (left.Y * right.Y)) + (left.Z * right.Z);

    private static void ValidateVertex(int vertex, int vertexCount)
    {
        if ((uint)vertex >= (uint)vertexCount)
        {
            throw Unsupported("A selected packed-frame vertex is outside the decoded buffer.");
        }
    }

    private static S2ModKitException Unsupported(string summary) => new(new S2Error(
        "AFFINE_PACKED_FRAME_REENCODE_MISMATCH",
        "source2_adapter",
        summary,
        "Use the characterized NORMAL R32_UINT frame or reject the affine candidate.",
        ErrorCategory.UnsupportedCapability));
}
