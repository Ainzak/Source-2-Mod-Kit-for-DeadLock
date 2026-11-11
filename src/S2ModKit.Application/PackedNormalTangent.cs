using System.Numerics;

namespace S2ModKit.Application;

/// <summary>
/// A normal and tangent packed into one 32-bit value (<c>NORMAL</c> as <c>R32_UINT</c>, how most Deadlock hero meshes store them):
/// bit 0 the tangent's sign (set: +1), bits 1-11 the tangent's angle around the normal (value / 2047 of a full turn, from the first
/// vector of the normal's orthonormal basis by Duff et al. 2017), bits 12-21 and 22-31 the normal's octahedral x and y (0-1023 for
/// -1..1). Measured against the reader VRF uses: decoding matches it, and encoding a decoded value gives the same normal bits for
/// 98.7 % of 180 351 hero vertices (the rest one step away) and the tangent within a fraction of a step.
/// </summary>
public static class PackedNormalTangent
{
    public static (Vector3 Normal, Vector4 Tangent) Decode(uint packed)
    {
        var x = Unit((packed >> 12) & 0x3FF);
        var y = Unit((packed >> 22) & 0x3FF);
        var z = 1f - MathF.Abs(x) - MathF.Abs(y);
        var fold = MathF.Max(-z, 0f);
        x += x >= 0 ? -fold : fold;
        y += y >= 0 ? -fold : fold;
        var normal = Vector3.Normalize(new Vector3(x, y, z));
        var (first, second) = Basis(normal);
        var angle = ((packed >> 1) & 0x7FF) / 2047f * 2f * MathF.PI;
        var tangent = (MathF.Cos(angle) * first) + (MathF.Sin(angle) * second);
        return (normal, new Vector4(tangent, (packed & 1) != 0 ? 1f : -1f));
    }

    public static uint Encode(Vector3 normal, Vector4 tangent)
    {
        var n = normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : Vector3.UnitZ;
        var (a, b) = Octahedral(n);
        var packed = (Quantize(b) << 22) | (Quantize(a) << 12);
        var stored = Decode(packed).Normal;                                   // the basis is built from the stored normal, as the reader does
        var (first, second) = Basis(stored);
        var t = new Vector3(tangent.X, tangent.Y, tangent.Z);
        var angle = MathF.Atan2(Vector3.Dot(t, second), Vector3.Dot(t, first));
        var steps = (uint)(((int)MathF.Round(angle / (2f * MathF.PI) * 2047f) % 2047 + 2047) % 2047);
        return packed | (steps << 1) | (tangent.W > 0 ? 1u : 0u);
    }

    /// <summary>The packed value with its normal turned by <paramref name="normals"/> (the inverse transpose of the shape's matrix)
    /// and its tangent by <paramref name="linear"/> (the matrix itself); the tangent's sign stays.</summary>
    public static uint Turn(uint packed, Matrix4x4 normals, Matrix4x4 linear)
    {
        var (normal, tangent) = Decode(packed);
        var n = Vector3.TransformNormal(normal, normals);
        var t = Vector3.TransformNormal(new Vector3(tangent.X, tangent.Y, tangent.Z), linear);
        n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : normal;
        t -= Vector3.Dot(t, n) * n;                                           // keep it at a right angle to the turned normal
        t = t.LengthSquared() > 1e-12f ? Vector3.Normalize(t) : Basis(n).First;
        return Encode(n, new Vector4(t, tangent.W));
    }

    private static float Unit(uint field) => (field / 1023f * 2f) - 1f;

    private static uint Quantize(float value) => (uint)Math.Clamp(MathF.Round((value + 1f) / 2f * 1023f), 0f, 1023f);

    private static (float A, float B) Octahedral(Vector3 n)
    {
        n /= MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z);
        return n.Z >= 0
            ? (n.X, n.Y)
            : ((1 - MathF.Abs(n.Y)) * (n.X >= 0 ? 1 : -1), (1 - MathF.Abs(n.X)) * (n.Y >= 0 ? 1 : -1));
    }

    /// <summary>The orthonormal basis of Duff et al. 2017 ("Building an Orthonormal Basis, Revisited").</summary>
    private static (Vector3 First, Vector3 Second) Basis(Vector3 n)
    {
        var sign = n.Z >= 0 ? 1f : -1f;
        var a = -1f / (sign + n.Z);
        var b = n.X * n.Y * a;
        return (new Vector3(1f + (sign * n.X * n.X * a), sign * b, -sign * n.X), new Vector3(b, sign + (n.Y * n.Y * a), -n.Y));
    }
}
