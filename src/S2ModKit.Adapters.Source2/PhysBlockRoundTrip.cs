using S2ModKit.Domain;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

internal sealed class PhysBlockRoundTripResult : IDisposable
{
    private readonly Resource resource;

    public PhysBlockRoundTripResult(
        ReadOnlyMemory<byte> payload,
        PhysAggregateData reopened,
        ContentHash semanticHash,
        Resource resource)
    {
        Payload = payload;
        Reopened = reopened;
        SemanticHash = semanticHash;
        this.resource = resource;
    }

    public ReadOnlyMemory<byte> Payload { get; }

    public PhysAggregateData Reopened { get; }

    public ContentHash SemanticHash { get; }

    public void Dispose() => resource.Dispose();
}

internal static class PhysBlockRoundTrip
{
    public static PhysBlockRoundTripResult SerializeAndVerify(PhysAggregateData block, string context)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        var expectedHash = KvSemanticHasher.ComputeComplete(block.Data);
        var first = Serialize(block, context);
        var second = Serialize(block, context);
        if (!first.AsSpan().SequenceEqual(second))
        {
            throw Unsupported($"{context} PHYS serialization is not deterministic.");
        }

        var resource = new Resource();
        var reopened = new PhysAggregateData(BlockType.PHYS)
        {
            Offset = 0,
            Size = checked((uint)first.Length),
            Resource = resource,
        };
        try
        {
            using var stream = new MemoryStream(first, writable: false);
            using var reader = new BinaryReader(stream);
            reopened.Read(reader);
            if (stream.Position != stream.Length
                || KvSemanticHasher.ComputeComplete(reopened.Data) != expectedHash)
            {
                throw Unsupported($"{context} PHYS semantic data changed during isolated reopen.");
            }

            return new PhysBlockRoundTripResult(first, reopened, expectedHash, resource);
        }
        catch (S2ModKitException)
        {
            resource.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or EndOfStreamException
            or InvalidDataException
            or NotSupportedException
            or OverflowException)
        {
            resource.Dispose();
            throw new S2ModKitException(
                new S2Error(
                    "PHYS_SERIALIZATION_UNSUPPORTED",
                    "source2_adapter",
                    $"{context} PHYS payload could not be reopened through the pinned adapter.",
                    "Reject the isolated collision result; do not publish a coupled candidate.",
                    ErrorCategory.UnsupportedCapability),
                exception);
        }
    }

    private static byte[] Serialize(PhysAggregateData block, string context)
    {
        try
        {
            using var stream = new MemoryStream();
            block.Serialize(stream);
            if (stream.Length == 0 || stream.Length > int.MaxValue)
            {
                throw Unsupported($"{context} PHYS serializer produced an empty or oversized payload.");
            }

            return BinaryKv3ContainerCounts.CompleteVersion4Header(stream.ToArray(), block.Data);
        }
        catch (S2ModKitException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or NotSupportedException or OverflowException)
        {
            throw new S2ModKitException(
                new S2Error(
                    "PHYS_SERIALIZATION_UNSUPPORTED",
                    "source2_adapter",
                    $"{context} PHYS payload could not be serialized through the pinned adapter.",
                    "Reject the isolated collision result; do not publish a coupled candidate.",
                    ErrorCategory.UnsupportedCapability),
                exception);
        }
    }

    private static S2ModKitException Unsupported(string summary) => Errors.Unsupported(
        "PHYS_SERIALIZATION_UNSUPPORTED",
        summary,
        "Reject the isolated collision result; do not publish a coupled candidate.");
}
