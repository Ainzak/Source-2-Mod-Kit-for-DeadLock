using System.Collections.Immutable;

namespace S2ModKit.Geometry;

internal static class VertexSetCanonicalizer
{
    public static ImmutableArray<int> Canonicalize(IReadOnlyList<int> vertices, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(vertices, parameterName);

        var canonical = new int[vertices.Count];
        for (var index = 0; index < vertices.Count; index++)
        {
            var vertex = vertices[index];
            if (vertex < 0)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    vertex,
                    "Vertex identifiers must not be negative.");
            }

            canonical[index] = vertex;
        }

        Array.Sort(canonical);
        var writeIndex = 0;
        for (var readIndex = 0; readIndex < canonical.Length; readIndex++)
        {
            if (writeIndex == 0 || canonical[writeIndex - 1] != canonical[readIndex])
            {
                canonical[writeIndex++] = canonical[readIndex];
            }
        }

        return ImmutableArray.Create(canonical, 0, writeIndex);
    }
}
