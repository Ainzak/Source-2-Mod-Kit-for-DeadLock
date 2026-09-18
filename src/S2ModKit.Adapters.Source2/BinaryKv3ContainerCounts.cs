using System.Buffers.Binary;
using ValveKeyValue;

namespace S2ModKit.Adapters.Source2;

/// <summary>Repairs the pinned serializer's zero KV3-v4 container counts, which its reader ignores.</summary>
internal static class BinaryKv3ContainerCounts
{
    public static byte[] CompleteVersion4Header(byte[] payload, KVObject root)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(root);
        if (payload.Length < 4)
        {
            throw new InvalidDataException("Truncated BinaryKV3 header.");
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        if (magic == 0x4B563305)
        {
            // The pinned version-5 writer already emits these counts and its separate object lane.
            return payload;
        }

        if (magic != 0x4B563304 || payload.Length < 72)
        {
            throw new InvalidDataException("Unsupported BinaryKV3 container-count header.");
        }

        // Apply the same cycle, depth, and node bounds as the semantic reopen audit before traversal.
        _ = KvSemanticHasher.ComputeComplete(root);
        var objects = 0;
        var arrays = 0;
        Count(root, ref objects, ref arrays);
        WriteCount(payload.AsSpan(44, 2), checked((ushort)objects));
        WriteCount(payload.AsSpan(46, 2), checked((ushort)arrays));
        return payload;
    }

    private static void Count(KVObject node, ref int objects, ref int arrays)
    {
        if (node.IsCollection)
        {
            objects++;
        }
        else if (node.IsArray)
        {
            arrays++;
        }
        else
        {
            return;
        }

        foreach (var child in node.Values)
        {
            Count(child, ref objects, ref arrays);
        }
    }

    private static void WriteCount(Span<byte> destination, ushort expected)
    {
        var actual = BinaryPrimitives.ReadUInt16LittleEndian(destination);
        if (actual != 0 && actual != expected)
        {
            throw new InvalidDataException("BinaryKV3 serialized container count disagrees with its semantic tree.");
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination, expected);
    }
}
