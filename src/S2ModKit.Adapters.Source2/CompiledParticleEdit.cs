using S2ModKit.Application;
using S2ModKit.Domain;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

/// <summary>One colour value in a compiled effect: where it is (field path) and its 0-255 channels (alpha, when stored, is kept).</summary>
public sealed record EffectColorValue(string Field, int R, int G, int B);

/// <summary>
/// Colour edits of a compiled particle system (<c>.vpcf_c</c>): the colour fields the game's effects use
/// (<c>m_Color</c> in gradient stops, <c>m_ConstantColor</c>, <c>m_ColorMin</c>/<c>m_ColorMax</c>, <c>m_LiteralColor</c>,
/// <c>m_ColorFade</c>, <c>m_TintColor</c>, <c>m_OutlineColor</c> and other <c>...Color</c> lists of three or four integers 0-255).
/// Only those numbers change, in their own integer type; the DATA block is re-serialized with ValveResourceFormat and spliced
/// in (as <see cref="CompiledModelEdit"/>), every other block keeps its bytes. Child effects and textures are not touched.
/// </summary>
public static class CompiledParticleEdit
{
    /// <summary>The effect's colour values (empty for an effect without colours).</summary>
    public static IReadOnlyList<EffectColorValue> ReadColors(byte[] compiled, string logicalPath)
    {
        var (_, root) = Open(compiled, logicalPath);
        var found = new List<EffectColorValue>();
        Visit(root, string.Empty, (field, value) =>
        {
            found.Add(new EffectColorValue(field, Channel(value[0]), Channel(value[1]), Channel(value[2])));
            return null;
        });
        return found;
    }

    /// <summary>The child effects the file references (<c>.vpcf</c> paths without <c>_c</c>), for grouping an ability's effects.</summary>
    public static IReadOnlyList<string> ChildEffects(byte[] compiled, string logicalPath)
    {
        using var resource = new Resource { FileName = logicalPath };
        resource.Read(new MemoryStream(compiled));
        return [.. (resource.ExternalReferences?.ResourceRefInfoList ?? []).Select(info => info.Name).Where(name => name.EndsWith(".vpcf", StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>A copy of the effect with every colour changed by <paramref name="change"/>; returns the bytes and how many values changed.</summary>
    public static (byte[] Bytes, int Changed) Recolor(byte[] compiled, string logicalPath, EffectColorChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var (resource, root) = Open(compiled, logicalPath);
        using (resource)
        {
            var changed = 0;
            Visit(root, string.Empty, (_, value) =>
            {
                var before = (Channel(value[0]), Channel(value[1]), Channel(value[2]));
                var after = EffectColors.Apply(before, change);
                if (after == before)
                {
                    return null;
                }

                changed++;
                return after;
            });
            if (changed == 0)
            {
                return (compiled, 0);
            }

            var data = resource.DataBlock!;
            using var stream = new MemoryStream();
            data.Serialize(stream);
            return (CompiledModelEdit.ReplaceBlocks(compiled, resource, new Dictionary<int, byte[]> { [resource.Blocks.IndexOf(data)] = stream.ToArray() }), changed);
        }
    }

    private static (Resource Resource, KVObject Root) Open(byte[] compiled, string logicalPath)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        var resource = new Resource { FileName = logicalPath };
        try
        {
            resource.Read(new MemoryStream(compiled));
            var root = resource.DataBlock switch
            {
                BinaryKV3 kv3 => kv3.Data.Root,
                KeyValuesOrNTRO kv => kv.Data,
                _ => throw Errors.Input("EFFECT_EDIT_NOT_AN_EFFECT", $"'{logicalPath}' has no effect data.", "Pass a compiled .vpcf_c."),
            };
            return resource.ResourceType == ResourceType.Particle
                ? (resource, root)
                : throw Errors.Input("EFFECT_EDIT_NOT_AN_EFFECT", $"'{logicalPath}' is not an effect.", "Pass a compiled .vpcf_c.");
        }
        catch
        {
            resource.Dispose();
            throw;
        }
    }

    /// <summary>Walks the tree; <paramref name="visit"/> gets each colour list (field path, channels) and returns new RGB or null.</summary>
    private static void Visit(KVObject node, string at, Func<string, KVObject, (int R, int G, int B)?> visit)
    {
        if (node.IsArray)
        {
            for (var i = 0; i < node.Count; i++)
            {
                Visit(node[i], $"{at}[{i}]", visit);
            }

            return;
        }

        if (node.ValueType != KVValueType.Collection)
        {
            return;
        }

        foreach (var key in node.Keys.ToArray())
        {
            var child = node[key];
            var field = $"{at}/{key}";
            if (IsColor(key, child))
            {
                if (visit(field, child) is { } rgb)
                {
                    var replaced = KVObject.Array();                          // as KvNumericMutation: a new list, same flag and types
                    replaced.Flag = child.Flag;
                    replaced.Add(Same(child[0], rgb.R));
                    replaced.Add(Same(child[1], rgb.G));
                    replaced.Add(Same(child[2], rgb.B));
                    for (var i = 3; i < child.Count; i++)
                    {
                        replaced.Add(child[i]);                               // alpha unchanged
                    }

                    node[key] = replaced;
                }
            }
            else
            {
                Visit(child, field, visit);
            }
        }
    }

    private static bool IsColor(string key, KVObject value) =>
        key.EndsWith("Color", StringComparison.Ordinal) || key is "m_ColorMin" or "m_ColorMax" or "m_ColorFade"
            ? value.IsArray && value.Count is 3 or 4 && Enumerable.Range(0, value.Count).All(i => IsInteger(value[i]) && Channel(value[i]) is >= 0 and <= 255)
            : false;

    private static bool IsInteger(KVObject value) => value.ValueType is KVValueType.Int32 or KVValueType.UInt32 or KVValueType.Int64 or KVValueType.UInt64 or KVValueType.Int16 or KVValueType.UInt16;

    private static int Channel(KVObject value) => (int)Convert.ToInt64(value.ToString(null), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The number in the value's own integer type (the game reads typed values).</summary>
    private static KVObject Same(KVObject original, int channel) => original.ValueType switch
    {
        KVValueType.UInt32 => (KVObject)(uint)channel,
        KVValueType.Int64 => (KVObject)(long)channel,
        KVValueType.UInt64 => (KVObject)(ulong)channel,
        KVValueType.Int16 => (KVObject)(short)channel,
        KVValueType.UInt16 => (KVObject)(ushort)channel,
        _ => (KVObject)channel,
    };
}
