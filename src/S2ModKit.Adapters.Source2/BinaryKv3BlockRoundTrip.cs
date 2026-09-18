using S2ModKit.Domain;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

internal sealed record BinaryKv3RoundTripResult(
    ReadOnlyMemory<byte> Payload,
    ContentHash SemanticHash,
    int SerializationVersion,
    KV3BinaryCompressionMethod CompressionMethod);

internal static class BinaryKv3BlockRoundTrip
{
    public static BinaryKv3RoundTripResult SerializeAndVerify(BinaryKV3 block, string context)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        var expectedHash = KvSemanticHasher.ComputeComplete(block.Data.Root);
        var first = Serialize(block, context);
        var second = Serialize(block, context);
        if (!first.AsSpan().SequenceEqual(second))
        {
            throw new InvalidDataException($"{context} BinaryKV3 serialization is not deterministic.");
        }

        using var resource = new ValveResourceFormat.Resource();
        var reopened = new BinaryKV3(block.Type)
        {
            Offset = 0,
            Size = checked((uint)first.Length),
            Resource = resource,
        };
        using var stream = new MemoryStream(first, writable: false);
        using var reader = new BinaryReader(stream);
        reopened.Read(reader);
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException($"{context} BinaryKV3 reader consumed {stream.Position} of {stream.Length} serialized bytes.");
        }

        var reopenedHash = KvSemanticHasher.ComputeComplete(reopened.Data.Root);
        if (reopenedHash != expectedHash)
        {
            throw new InvalidDataException($"{context} BinaryKV3 semantic hash changed after isolated serialization and reopen.");
        }

        if (reopened.SerializationVersion != block.SerializationVersion
            || reopened.SerializationCompressionMethod != block.SerializationCompressionMethod)
        {
            throw new InvalidDataException($"{context} BinaryKV3 serialization profile changed after reopen.");
        }

        return new BinaryKv3RoundTripResult(
            first,
            expectedHash,
            reopened.SerializationVersion,
            reopened.SerializationCompressionMethod);
    }

    private static byte[] Serialize(BinaryKV3 block, string context)
    {
        using var stream = new MemoryStream();
        block.Serialize(stream);
        if (stream.Length == 0 || stream.Length > int.MaxValue)
        {
            throw new InvalidDataException($"{context} BinaryKV3 serializer produced an empty or oversized block.");
        }

        return stream.ToArray();
    }
}
