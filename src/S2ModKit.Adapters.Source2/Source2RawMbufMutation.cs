using S2ModKit.Domain;

namespace S2ModKit.Adapters.Source2;

internal static class Source2RawMbufMutation
{
    public static byte[] CreateTransformedPayload(
        Source2ResourceBlock block,
        Source2RawMbufAnalysis analysis,
        ReadOnlySpan<byte> transformedDecodedVertices)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(analysis);
        if (transformedDecodedVertices.Length != analysis.VertexDataLength)
        {
            throw new InvalidDataException("The intended raw MBUF vertex length differs from the source descriptor.");
        }

        var result = block.Payload.ToArray();
        transformedDecodedVertices.CopyTo(result.AsSpan(analysis.VertexDataOffset, analysis.VertexDataLength));
        return result;
    }
}
