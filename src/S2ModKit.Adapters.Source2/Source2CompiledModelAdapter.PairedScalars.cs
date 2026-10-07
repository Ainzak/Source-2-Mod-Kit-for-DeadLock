using System.Globalization;
using ValveKeyValue;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    // Preserve source scalar kinds: conversions must not admit strings, fractions or overflow.
    private static int PairedSourceInt(KVObject value)
    {
        if (value.ValueType is KVValueType.UInt32 or KVValueType.UInt64)
        {
            var number = value.ToUInt64(CultureInfo.InvariantCulture);
            if (number <= int.MaxValue) return (int)number;
        }
        else if (value.ValueType is KVValueType.Int32 or KVValueType.Int64)
        {
            var number = value.ToInt64(CultureInfo.InvariantCulture);
            if (number is >= int.MinValue and <= int.MaxValue) return (int)number;
        }
        throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "A source integer has an unsupported kind or range.");
    }

    private static uint PairedSourceUInt(KVObject value)
    {
        if (value.ValueType is KVValueType.UInt32 or KVValueType.UInt64)
        {
            var number = value.ToUInt64(CultureInfo.InvariantCulture);
            if (number <= uint.MaxValue) return (uint)number;
        }
        else if (value.ValueType is KVValueType.Int32 or KVValueType.Int64)
        {
            var number = value.ToInt64(CultureInfo.InvariantCulture);
            if (number is >= 0 and <= uint.MaxValue) return (uint)number;
        }
        throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "A source token or flag has an unsupported kind or range.");
    }

    private static bool PairedSourceBool(KVObject value)
    {
        if (value.ValueType != KVValueType.Boolean)
            throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "A source boolean has an unsupported kind.");
        return value.ToBoolean(CultureInfo.InvariantCulture);
    }

    private static string PairedSourceString(KVObject value)
    {
        if (value.ValueType != KVValueType.String)
            throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "A source name has an unsupported kind.");
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
