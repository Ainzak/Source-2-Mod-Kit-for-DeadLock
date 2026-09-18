using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using S2ModKit.Domain;
using ValveKeyValue;

namespace S2ModKit.Adapters.Source2;

internal static class KvSemanticHasher
{
    private const int MaximumDepth = 64;
    private const int MaximumNodes = 1_000_000;

    public static ContentHash Compute(KVObject root, string excludedProperty)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(excludedProperty);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var recursionPath = new HashSet<KVObject>(ReferenceEqualityComparer.Instance);
        var nodeCount = 0;
        AppendNode(hash, root, excludedProperty, recursionPath, 0, ref nodeCount);
        return new ContentHash(Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    public static ContentHash ComputeComplete(KVObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var recursionPath = new HashSet<KVObject>(ReferenceEqualityComparer.Instance);
        var nodeCount = 0;
        AppendCompleteNode(hash, root, recursionPath, 0, ref nodeCount);
        return new ContentHash(Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void AppendCompleteNode(
        IncrementalHash hash,
        KVObject value,
        HashSet<KVObject> recursionPath,
        int depth,
        ref int nodeCount)
    {
        CountNode(depth, ref nodeCount);
        AppendInt32(hash, (int)value.ValueType);
        AppendInt32(hash, (int)value.Flag);
        if (value.IsArray)
        {
            RequireAcyclic(value, recursionPath);
            try
            {
                var items = value.Values.ToArray();
                AppendInt32(hash, items.Length);
                foreach (var item in items)
                {
                    AppendCompleteNode(hash, item, recursionPath, depth + 1, ref nodeCount);
                }
            }
            finally
            {
                recursionPath.Remove(value);
            }

            return;
        }

        if (value.IsCollection)
        {
            RequireAcyclic(value, recursionPath);
            try
            {
                var properties = value.Children.ToArray();
                AppendInt32(hash, properties.Length);
                foreach (var property in properties)
                {
                    AppendString(hash, property.Key);
                    AppendCompleteNode(hash, property.Value, recursionPath, depth + 1, ref nodeCount);
                }
            }
            finally
            {
                recursionPath.Remove(value);
            }

            return;
        }

        AppendScalar(hash, value);
    }

    private static void AppendScalar(IncrementalHash hash, KVObject value)
    {
        switch (value.ValueType)
        {
            case KVValueType.Null:
                return;
            case KVValueType.BinaryBlob:
                AppendBytes(hash, value.AsBlob());
                return;
            case KVValueType.Boolean:
                hash.AppendData([value.ToBoolean(CultureInfo.InvariantCulture) ? (byte)1 : (byte)0]);
                return;
            case KVValueType.String:
                AppendString(hash, value.ToString(CultureInfo.InvariantCulture));
                return;
            case KVValueType.Int16:
                AppendInt16(hash, value.ToInt16(CultureInfo.InvariantCulture));
                return;
            case KVValueType.Int32:
                AppendInt32(hash, value.ToInt32(CultureInfo.InvariantCulture));
                return;
            case KVValueType.Int64:
            case KVValueType.Pointer:
                AppendInt64(hash, value.ToInt64(CultureInfo.InvariantCulture));
                return;
            case KVValueType.UInt16:
                AppendUInt16(hash, value.ToUInt16(CultureInfo.InvariantCulture));
                return;
            case KVValueType.UInt32:
                AppendUInt32(hash, value.ToUInt32(CultureInfo.InvariantCulture));
                return;
            case KVValueType.UInt64:
                AppendUInt64(hash, value.ToUInt64(CultureInfo.InvariantCulture));
                return;
            case KVValueType.FloatingPoint:
                AppendInt32(hash, BitConverter.SingleToInt32Bits(value.ToSingle(CultureInfo.InvariantCulture)));
                return;
            case KVValueType.FloatingPoint64:
                AppendInt64(hash, BitConverter.DoubleToInt64Bits(value.ToDouble(CultureInfo.InvariantCulture)));
                return;
            default:
                throw Errors.Unsupported(
                    "KV_SEMANTIC_SCALAR_UNSUPPORTED",
                    $"KV scalar type '{value.ValueType}' is not supported by the complete semantic hash.",
                    "Add an exact binary representation for this scalar type before rewriting its containing block.");
        }
    }

    private static void AppendNode(
        IncrementalHash hash,
        KVObject value,
        string excludedProperty,
        HashSet<KVObject> recursionPath,
        int depth,
        ref int nodeCount)
    {
        CountNode(depth, ref nodeCount);

        AppendString(hash, value.ValueType.ToString());
        AppendString(hash, value.Flag.ToString());
        if (value.IsArray)
        {
            RequireAcyclic(value, recursionPath);
            try
            {
                var items = value.Values.ToArray();
                AppendInt32(hash, items.Length);
                foreach (var item in items)
                {
                    AppendNode(hash, item, excludedProperty, recursionPath, depth + 1, ref nodeCount);
                }
            }
            finally
            {
                recursionPath.Remove(value);
            }

            return;
        }

        if (value.IsCollection)
        {
            RequireAcyclic(value, recursionPath);
            try
            {
                var properties = value.Children.Where(pair => !string.Equals(pair.Key, excludedProperty, StringComparison.Ordinal))
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToArray();
                AppendInt32(hash, properties.Length);
                foreach (var property in properties)
                {
                    AppendString(hash, property.Key);
                    AppendNode(hash, property.Value, excludedProperty, recursionPath, depth + 1, ref nodeCount);
                }
            }
            finally
            {
                recursionPath.Remove(value);
            }

            return;
        }

        if (value.ValueType == KVValueType.BinaryBlob)
        {
            AppendBytes(hash, value.AsBlob());
            return;
        }

        AppendString(hash, value.ToString(CultureInfo.InvariantCulture));
    }

    private static void RequireAcyclic(KVObject value, HashSet<KVObject> recursionPath)
    {
        if (!recursionPath.Add(value))
        {
            throw Errors.Unsupported("KV_SEMANTIC_CYCLE_UNSUPPORTED", "A mesh KV tree contains a reference cycle.", "Use an acyclic serialized KV resource or add a reviewed graph-hash profile.");
        }
    }

    private static void CountNode(int depth, ref int nodeCount)
    {
        nodeCount = checked(nodeCount + 1);
        if (depth > MaximumDepth || nodeCount > MaximumNodes)
        {
            throw Errors.Unsupported("KV_SEMANTIC_TREE_UNSUPPORTED", "A KV tree exceeds the supported semantic-hash bounds.", "Use a bounded compiled resource or add a reviewed larger-layout profile.");
        }
    }

    private static void AppendString(IncrementalHash hash, string value) => AppendBytes(hash, Encoding.UTF8.GetBytes(value));

    private static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt16(IncrementalHash hash, short value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(short)];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendUInt16(IncrementalHash hash, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendUInt32(IncrementalHash hash, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendUInt64(IncrementalHash hash, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
