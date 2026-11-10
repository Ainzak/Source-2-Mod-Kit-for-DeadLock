using System.Globalization;
using System.Numerics;
using S2ModKit.Application;
using S2ModKit.Domain;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

/// <summary>
/// Colour edits of a compiled material (<c>.vmat_c</c>) with a part recolor: every colour setting <see cref="MaterialColors.Follows"/>
/// (tints, outline and glow colours) gets the part's change, in place in <c>m_vectorParams</c> (same number types); the DATA block is
/// re-serialized and spliced in as in <see cref="CompiledModelEdit"/>. Textures are not touched here; the glow colour texture's path
/// is returned so the caller can recolor it.
/// </summary>
public static class CompiledMaterialEdit
{
    public static (byte[] Bytes, IReadOnlyList<string> Changed, string? GlowTexture) Recolor(byte[] compiled, string logicalPath, ColorAdjustment change)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(change);
        using var resource = new Resource { FileName = logicalPath };
        resource.Read(new MemoryStream(compiled));
        if (resource.DataBlock is not Material material)
        {
            throw Errors.Input("MATERIAL_EDIT_NOT_A_MATERIAL", $"'{logicalPath}' is not a material.", "Pass a compiled .vmat_c.");
        }

        var root = material.Data;
        var glow = material.TextureParams.TryGetValue(MaterialColors.GlowTexture, out var texture) ? texture : null;
        var changed = new List<string>();
        if (root.TryGetValue("m_vectorParams", out var parameters) && parameters is { IsArray: true })
        {
            for (var i = 0; i < parameters.Count; i++)
            {
                var parameter = parameters[i];
                if (!parameter.TryGetValue("m_name", out var nameValue) || !parameter.TryGetValue("m_value", out var value) || !value.IsArray || value.Count < 3)
                {
                    continue;
                }

                var name = nameValue.ToString(CultureInfo.InvariantCulture);
                var vector = new Vector4(Number(value[0]), Number(value[1]), Number(value[2]), value.Count > 3 ? Number(value[3]) : 0);
                if (!MaterialColors.Follows(name, vector))
                {
                    continue;
                }

                var after = MaterialColors.Apply(vector, change);
                var replaced = KVObject.Array();
                replaced.Flag = value.Flag;
                replaced.Add(Same(value[0], after.X));
                replaced.Add(Same(value[1], after.Y));
                replaced.Add(Same(value[2], after.Z));
                for (var c = 3; c < value.Count; c++)
                {
                    replaced.Add(value[c]);
                }

                parameter["m_value"] = replaced;
                changed.Add(name);
            }
        }

        if (changed.Count == 0)
        {
            return (compiled, changed, glow);
        }

        using var stream = new MemoryStream();
        material.Serialize(stream);
        return (CompiledModelEdit.ReplaceBlocks(compiled, resource, new Dictionary<int, byte[]> { [resource.Blocks.IndexOf(material)] = stream.ToArray() }), changed, glow);
    }

    /// <summary>A material's texture settings (name to <c>.vtex</c> path without <c>_c</c>).</summary>
    public static IReadOnlyDictionary<string, string> Textures(byte[] compiled, string logicalPath)
    {
        using var resource = new Resource { FileName = logicalPath };
        resource.Read(new MemoryStream(compiled));
        return resource.DataBlock is Material material ? new Dictionary<string, string>(material.TextureParams, StringComparer.Ordinal) : new Dictionary<string, string>();
    }

    /// <summary>The materials a compiled model uses (<c>.vmat</c> paths without <c>_c</c>, from its reference list).</summary>
    public static IReadOnlyList<string> ModelMaterials(byte[] compiled, string logicalPath)
    {
        using var resource = new Resource { FileName = logicalPath };
        resource.Read(new MemoryStream(compiled));
        return [.. (resource.ExternalReferences?.ResourceRefInfoList ?? []).Select(info => info.Name).Where(name => name.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static float Number(KVObject value) => float.Parse(value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static KVObject Same(KVObject original, float number) => original.ValueType switch
    {
        KVValueType.FloatingPoint64 => (KVObject)(double)number,
        _ => (KVObject)number,
    };
}
