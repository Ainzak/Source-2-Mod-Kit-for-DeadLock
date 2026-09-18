using System.Globalization;
using S2ModKit.Domain;
using ValveKeyValue;

namespace S2ModKit.Adapters.Source2;

internal static class KvNumericMutation
{
    public static void ReplaceVector3(
        KVObject parent,
        string key,
        TransformVector3 expectedBefore,
        TransformVector3 replacement,
        string context)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(expectedBefore);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        if (!parent.TryGetValue(key, out var current) || current is null || !current.IsArray || current.Count != 3)
        {
            throw new InvalidDataException($"Expected three-component vector '{context}.{key}'.");
        }

        var expected = new[] { expectedBefore.X, expectedBefore.Y, expectedBefore.Z };
        var values = new[] { replacement.X, replacement.Y, replacement.Z };
        if (values.Any(value => !float.IsFinite(value)))
        {
            throw new InvalidDataException($"Replacement vector '{context}.{key}' contains a non-finite component.");
        }

        var result = KVObject.Array();
        result.Flag = current.Flag;
        for (var index = 0; index < values.Length; index++)
        {
            RequireExpectedSingle(current[index], expected[index], $"{context}.{key}[{index}]");
            result.Add(CreateFloatingPoint(current[index], values[index], $"{context}.{key}[{index}]"));
        }

        parent[key] = result;
    }

    public static void ReplaceSingle(
        KVObject parent,
        string key,
        float expectedBefore,
        float replacement,
        string context)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        if (!float.IsFinite(replacement)
            || !parent.TryGetValue(key, out var current)
            || current is null
            || current.IsArray
            || current.IsCollection)
        {
            throw new InvalidDataException($"Expected finite floating-point scalar '{context}.{key}'.");
        }

        RequireExpectedSingle(current, expectedBefore, $"{context}.{key}");
        parent[key] = CreateFloatingPoint(current, replacement, $"{context}.{key}");
    }

    public static void ReplaceFloatArray(
        KVObject parent,
        string key,
        IReadOnlyList<float> expected,
        IReadOnlyList<float> replacement,
        string context)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        if (!parent.TryGetValue(key, out var current)
            || current is null
            || !current.IsArray
            || current.Count != expected.Count
            || replacement.Count != expected.Count)
        {
            throw new InvalidDataException($"Expected {expected.Count}-component floating-point array '{context}.{key}'.");
        }

        var result = KVObject.Array();
        result.Flag = current.Flag;
        for (var index = 0; index < expected.Count; index++)
        {
            RequireExpectedSingle(current[index], expected[index], $"{context}.{key}[{index}]");
            if (!float.IsFinite(replacement[index]))
            {
                throw new InvalidDataException($"Replacement array '{context}.{key}' contains a non-finite component.");
            }

            result.Add(CreateFloatingPoint(current[index], replacement[index], $"{context}.{key}[{index}]"));
        }

        parent[key] = result;
    }

    private static KVObject CreateFloatingPoint(KVObject template, float value, string context)
    {
        var result = template.ValueType switch
        {
            KVValueType.FloatingPoint => new KVObject(value),
            KVValueType.FloatingPoint64 => new KVObject((double)value),
            _ => throw new InvalidDataException($"Expected floating-point storage at '{context}', found {template.ValueType}."),
        };
        result.Flag = template.Flag;
        return result;
    }

    private static void RequireExpectedSingle(KVObject value, float expected, string context)
    {
        if (value.ValueType is not (KVValueType.FloatingPoint or KVValueType.FloatingPoint64))
        {
            throw new InvalidDataException($"Expected floating-point storage at '{context}', found {value.ValueType}.");
        }

        float actual;
        try
        {
            actual = value.ToSingle(CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            throw new InvalidDataException($"Could not read finite floating-point storage at '{context}'.", exception);
        }

        if (!float.IsFinite(actual)
            || BitConverter.SingleToInt32Bits(actual) != BitConverter.SingleToInt32Bits(expected))
        {
            throw new InvalidDataException($"Floating-point value at '{context}' drifted from the mutation plan.");
        }
    }
}
