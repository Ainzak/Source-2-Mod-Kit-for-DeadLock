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

    /// <summary>
    /// A copy of a compiled material drawn see-through (<c>set_material_opacity@1</c>): the hero shader's own translucency
    /// (<c>F_TRANSLUCENT</c> with <c>F_WRITE_DEPTH_BEFORE_ALPHA_BLENDING</c>, as the game's translucent hero materials such as
    /// Shiv's glasses have them), no outline (<c>F_DISABLE_NPR_OUTLINE</c>) and <c>g_flOpacityScale1</c> = <paramref name="opacity"/> (1 solid, 0 invisible). Settings the
    /// material lacks are added in the layout of its own settings; everything else stays. Only <c>pbr.vfx</c> materials.
    /// </summary>
    public static byte[] SetOpacity(byte[] compiled, string logicalPath, float opacity)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        using var resource = new Resource { FileName = logicalPath };
        resource.Read(new MemoryStream(compiled));
        if (resource.DataBlock is not Material material)
        {
            throw Errors.Input("MATERIAL_EDIT_NOT_A_MATERIAL", $"'{logicalPath}' is not a material.", "Pass a compiled .vmat_c.");
        }

        if (!string.Equals(material.ShaderName, "pbr.vfx", StringComparison.OrdinalIgnoreCase))
        {
            throw Errors.Unsupported("MATERIAL_OPACITY_SHADER", $"'{Path.GetFileName(logicalPath)}' uses the shader '{material.ShaderName}', which has no see-through setting the app knows.", "Only parts with the game's usual hero material can be made see-through.");
        }

        var root = material.Data;
        if (!root.TryGetValue("m_intParams", out var ints) || !ints.IsArray || ints.Count == 0 || !root.TryGetValue("m_floatParams", out var floats) || !floats.IsArray || floats.Count == 0)
        {
            throw Errors.Unsupported("MATERIAL_OPACITY_LAYOUT", $"'{Path.GetFileName(logicalPath)}' has no settings to copy the layout from.", "Report the file.");
        }

        Set(ints, "F_TRANSLUCENT", "m_nValue", like => Whole(like, 1));
        Set(ints, "F_WRITE_DEPTH_BEFORE_ALPHA_BLENDING", "m_nValue", like => Whole(like, 1));
        Set(ints, "F_DISABLE_NPR_OUTLINE", "m_nValue", like => Whole(like, 1));   // a dark outline shell would show through the part
        Set(floats, "g_flOpacityScale1", "m_flValue", like => Same(like, Math.Clamp(opacity, 0f, 1f)));
        if (!floats.Values.Any(item => item["m_name"].ToString(CultureInfo.InvariantCulture) == "g_flAlphaAnglePower1"))
        {
            Set(floats, "g_flAlphaAnglePower1", "m_flValue", like => Same(like, 0f));
        }

        using var stream = new MemoryStream();
        material.Serialize(stream);
        return CompiledModelEdit.ReplaceBlocks(compiled, resource, new Dictionary<int, byte[]> { [resource.Blocks.IndexOf(material)] = stream.ToArray() });

        static void Set(KVObject list, string name, string valueKey, Func<KVObject, KVObject> value)
        {
            var found = list.Values.FirstOrDefault(item => item["m_name"].ToString(CultureInfo.InvariantCulture) == name);
            if (found is not null)
            {
                found[valueKey] = value(found[valueKey]);
                return;
            }

            var template = list[0];
            var entry = KVObject.Collection();
            entry.Flag = template.Flag;
            foreach (var (key, item) in template.Children)
            {
                entry.Add(key, key == "m_name" ? new KVObject(name) { Flag = item.Flag } : key == valueKey ? value(item) : item);
            }

            list.Add(entry);
        }

        static KVObject Whole(KVObject like, int number) => like.ValueType switch
        {
            KVValueType.UInt64 => (KVObject)(ulong)number,
            KVValueType.UInt32 => (KVObject)(uint)number,
            KVValueType.Int64 => (KVObject)(long)number,
            _ => (KVObject)number,
        };
    }

    /// <summary>
    /// A copy of a compiled material under another name that draws texture setting <paramref name="parameter"/> from
    /// <paramref name="texturePath"/> (<c>.vtex</c>, without <c>_c</c>) instead: the setting and the material's name change, and the new
    /// texture joins the material's list of the files it uses. Everything else stays.
    /// </summary>
    public static byte[] WithTexture(byte[] compiled, string logicalPath, string newMaterialName, string parameter, string texturePath)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        using var resource = new Resource { FileName = logicalPath };
        resource.Read(new MemoryStream(compiled));
        var material = resource.DataBlock as Material ?? throw Errors.Input("MATERIAL_EDIT_NOT_A_MATERIAL", $"'{logicalPath}' is not a material.", "Pass a compiled .vmat_c.");
        var textures = material.Data.TryGetValue("m_textureParams", out var list) && list.IsArray ? list : null;
        var entry = textures?.Values.FirstOrDefault(item => item["m_name"].ToString(CultureInfo.InvariantCulture) == parameter)
            ?? throw Errors.Unsupported("MATERIAL_TEXTURE_MISSING", $"'{Path.GetFileName(logicalPath)}' has no '{parameter}' texture.", "Report the file.");
        entry["m_pValue"] = new KVObject(texturePath) { Flag = entry["m_pValue"].Flag };
        if (material.Data.ContainsKey("m_materialName"))
        {
            material.Data["m_materialName"] = new KVObject(newMaterialName) { Flag = material.Data["m_materialName"].Flag };
        }

        using var stream = new MemoryStream();
        material.Serialize(stream);
        var payloads = new Dictionary<int, byte[]> { [resource.Blocks.IndexOf(material)] = stream.ToArray() };
        var references = resource.Blocks.FirstOrDefault(block => block.Type == BlockType.RERL)
            ?? throw Errors.Unsupported("MATERIAL_TEXTURE_MISSING", $"'{Path.GetFileName(logicalPath)}' has no list of the files it uses.", "Report the file.");
        payloads[resource.Blocks.IndexOf(references)] = CompiledAddedEffects.WithReferences(compiled.AsSpan((int)references.Offset, (int)references.Size), [texturePath], replace: false);
        return CompiledModelEdit.ReplaceBlocks(compiled, resource, payloads);
    }

    /// <summary>The opacity a compiled material is drawn with: null when it is not see-through.</summary>
    public static float? Opacity(byte[] compiled, string logicalPath)
    {
        using var resource = new Resource { FileName = logicalPath };
        resource.Read(new MemoryStream(compiled));
        return resource.DataBlock is Material material && material.IntParams.GetValueOrDefault("F_TRANSLUCENT") == 1
            ? material.FloatParams.GetValueOrDefault("g_flOpacityScale1", 1f)
            : null;
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
