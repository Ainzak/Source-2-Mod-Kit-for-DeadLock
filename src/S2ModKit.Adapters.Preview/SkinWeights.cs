using ValveResourceFormat.Blocks;

namespace S2ModKit.Adapters.Preview;

/// <summary>
/// Blend weights aligned with <see cref="VBIB.GetBlendIndicesArray"/>'s indices (<c>perVertex</c> of each per vertex). Heroes with eight
/// influences (Abrams, Apollo: eight one-byte indices and eight one-byte weights in 4 x 16-bit fields) come back from VRF as two
/// weight vectors per vertex; taking one vector per vertex misplaced every weight after the first vertex and pulled the outline and
/// the picker's posed parts to the wrong bones.
/// </summary>
public static class SkinWeights
{
    /// <summary>The weights, <paramref name="perVertex"/> per vertex; without a weight field the first influence has all the weight.</summary>
    public static float[] Read(VBIB.OnDiskBufferData buffer, VBIB.RenderInputLayoutField field, int vertexCount, int perVertex)
    {
        var flat = new float[vertexCount * perVertex];
        if (field.SemanticName is null)
        {
            for (var v = 0; v < vertexCount; v++)
            {
                flat[v * perVertex] = 1;
            }

            return flat;
        }

        var vectors = VBIB.GetBlendWeightsArray(buffer, field);
        var groups = Math.Max(1, vectors.Length / Math.Max(1, vertexCount));     // weight vectors per vertex (two for eight influences)
        for (var v = 0; v < vertexCount; v++)
        {
            for (var k = 0; k < perVertex && k / 4 < groups; k++)
            {
                var vector = vectors[(v * groups) + (k / 4)];
                flat[(v * perVertex) + k] = (k % 4) switch { 0 => vector.X, 1 => vector.Y, 2 => vector.Z, _ => vector.W };
            }
        }

        return flat;
    }
}
