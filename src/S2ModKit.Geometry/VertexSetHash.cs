using System.Buffers.Binary;
using System.Security.Cryptography;

namespace S2ModKit.Geometry;

/// <summary>
/// Deterministic identity for a vertex set: the vertices are canonicalized to
/// strictly ascending distinct values and hashed as four-byte little-endian
/// integers through SHA-256. The result is a 64-character lower-case hex
/// string that is stable across processes and independent of input order or
/// duplicates. String identifiers are never hashed with process-randomized
/// hash codes anywhere in this module.
/// </summary>
public static class VertexSetHash
{
    public static string Compute(IReadOnlyList<int> vertices)
    {
        var canonical = VertexSetCanonicalizer.Canonicalize(vertices, nameof(vertices));
        var payload = new byte[checked(canonical.Length * sizeof(int))];
        for (var index = 0; index < canonical.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                payload.AsSpan(index * sizeof(int), sizeof(int)),
                canonical[index]);
        }

        var digest = SHA256.HashData(payload);
        return Convert.ToHexStringLower(digest);
    }
}
