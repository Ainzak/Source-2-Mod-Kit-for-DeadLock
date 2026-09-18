using S2ModKit.Domain;

namespace S2ModKit.Adapters.Source2;

public sealed record MeshOptimizerCodecCapability(
    string Status,
    string Summary,
    GeometryCodecIdentity? Identity);

internal interface IMeshOptimizerCodec : IDisposable
{
    GeometryCodecIdentity Identity { get; }

    byte[] DecodeVertexBuffer(byte[] encoded, int vertexCount, int vertexStride);

    byte[] DecodeIndexBuffer(byte[] encoded, int indexCount, int indexStride);

    byte[] EncodeVertexBuffer(byte[] decoded, int vertexCount, int vertexStride, int version);
}

internal sealed record MeshOptimizerRoundTripResult(
    byte[] Decoded,
    byte[] Encoded,
    ContentHash DecodedHash);

internal static class MeshOptimizerRoundTrip
{
    public static MeshOptimizerRoundTripResult VerifyUnchangedVertexBuffer(
        IMeshOptimizerCodec codec,
        byte[] encoded,
        int vertexCount,
        int vertexStride,
        int version)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(encoded);

        var decoded = codec.DecodeVertexBuffer(encoded, vertexCount, vertexStride);
        var reencoded = codec.EncodeVertexBuffer(decoded, vertexCount, vertexStride, version);
        var reopened = codec.DecodeVertexBuffer(reencoded, vertexCount, vertexStride);
        if (!decoded.AsSpan().SequenceEqual(reopened))
        {
            throw new InvalidDataException("The meshoptimizer encode/decode round trip changed decoded vertex bytes.");
        }

        return new MeshOptimizerRoundTripResult(decoded, reencoded, ContentHash.Compute(decoded));
    }
}
