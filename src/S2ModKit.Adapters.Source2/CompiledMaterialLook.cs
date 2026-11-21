using System.Buffers.Binary;
using System.Globalization;
using S2ModKit.Domain;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

/// <summary>
/// A glow for a part's material: the whole part lights up in one colour (R, G, B) by <paramref name="Strength"/>, rising and falling when
/// <paramref name="PulseSpeed"/> (radians per second) and <paramref name="PulseAmount"/> (a fraction) are above 0. <paramref name="Edges"/>:
/// brighter at the part's edges than in its middle and the part tinted towards the colour (the settings of the game's spectral figure).
/// A see-through part (see-through slider) with a glow reads as a glowing figure.
/// </summary>
public sealed record MaterialLook(float R, float G, float B, float Strength, float PulseSpeed, float PulseAmount, bool Edges)
{
    /// <summary>True when the glow strength rises and falls over time.</summary>
    public bool Pulses => PulseSpeed > 0 && PulseAmount > 0;
}

/// <summary>
/// A copy of a compiled hero material (<c>pbr.vfx</c>) with a <see cref="MaterialLook"/> (<c>set_material_look@3</c>): the shader's own
/// self-illumination (<c>F_SELF_ILLUM</c>, the chosen colour alone, a white mask texture) with an optional pulse, the game's compiled
/// expression <c>a * sin(b * time()) + c</c> on the glow scale, and optionally the edge settings of the game's spectral figure material
/// (fresnel mask, colour tint, translucency angle). Settings the material lacks are added in the layout of its own settings; everything
/// else stays, and a layout this does not understand is refused. Nothing here touches depth testing.
/// </summary>
public static class CompiledMaterialLook
{
    private const string PulseSetting = "g_flSelfIllumScale1";

    public static byte[] Apply(byte[] compiled, string logicalPath, MaterialLook look, string maskTexturePath)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(look);
        Validate(look);
        if (string.IsNullOrWhiteSpace(maskTexturePath) || !maskTexturePath.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase))
        {
            throw Errors.Input("MATERIAL_LOOK_MASK", $"The glow mask '{maskTexturePath}' is not a texture path (.vtex).", "Report the file.");
        }

        using var resource = new Resource { FileName = logicalPath };
        resource.Read(new MemoryStream(compiled));
        if (resource.DataBlock is not Material material)
        {
            throw Errors.Input("MATERIAL_EDIT_NOT_A_MATERIAL", $"'{logicalPath}' is not a material.", "Pass a compiled .vmat_c.");
        }

        var name = Path.GetFileName(logicalPath);
        if (!string.Equals(material.ShaderName, "pbr.vfx", StringComparison.OrdinalIgnoreCase))
        {
            throw Errors.Unsupported("MATERIAL_LOOK_SHADER", $"'{name}' uses the shader '{material.ShaderName}', which has no glow settings the app knows.", "Only parts with the game's usual hero material can get a look.");
        }

        var root = material.Data;
        var ints = Settings(root, "m_intParams", "m_nValue", name);
        var floats = Settings(root, "m_floatParams", "m_flValue", name);
        var vectors = Settings(root, "m_vectorParams", "m_value", name, array: true);
        var textures = Settings(root, "m_textureParams", "m_pValue", name);
        var dynamics = root.TryGetValue("m_dynamicParams", out var dynamic) && dynamic.IsArray ? dynamic
            : throw Errors.Unsupported("MATERIAL_LOOK_LAYOUT", $"'{name}' has no list of dynamic settings.", "Report the file.");

        if (root.TryGetValue("m_materialName", out var materialName) && logicalPath.EndsWith("_c", StringComparison.Ordinal))
        {
            root["m_materialName"] = new KVObject(logicalPath[..^2]) { Flag = materialName.Flag };   // the copy carries its own name
        }

        void Int(string setting, int value) => CompiledMaterialEdit.Set(ints, setting, "m_nValue", like => CompiledMaterialEdit.Whole(like, value));
        void Float(string setting, float value) => CompiledMaterialEdit.SetFloat(root, floats, setting, value);
        void Vector(string setting, float x, float y, float z, float w) => CompiledMaterialEdit.Set(vectors, setting, "m_value", like =>
        {
            var vector = KVObject.Array();
            vector.Flag = like.Flag;
            foreach (var number in new[] { x, y, z, w })
            {
                vector.Add(CompiledMaterialEdit.Same(like[0], number));
            }

            return vector;
        });

        Int("F_SELF_ILLUM", 1);
        Vector("g_vSelfIllumTint1", look.R, look.G, look.B, 0f);
        Float("g_flSelfIllumAlbedoFactor1", 0f);                           // the chosen colour itself, not the part's colours
        Float(PulseSetting, look.Strength);
        CompiledMaterialEdit.Set(textures, "g_tSelfIllumMask", "m_pValue", like => new KVObject(maskTexturePath) { Flag = like.Flag });
        if (look.Edges)
        {
            // the game's spectral figure material: brighter towards the edges, the part tinted towards the colour, its see-through edges
            // more solid than its middle
            Float("g_flSelfIllumFresnelMaskExponent", 10f);
            Vector("g_vSelfIllumFresnelMaskTint1", 0.85f * look.R, 0.85f * look.G, 0.85f * look.B, 0f);
            Vector("g_vColorTint1", 0.4f + (0.6f * look.R), 0.4f + (0.6f * look.G), 0.4f + (0.6f * look.B), 1f);
            Float("g_flAlphaAnglePower1", -1.19f);
        }

        var program = look.Pulses ? PulseExpression(look.Strength * look.PulseAmount, look.PulseSpeed, look.Strength) : null;
        SetDynamic(dynamics, PulseSetting, program);

        using var stream = new MemoryStream();
        material.Serialize(stream);
        var references = resource.Blocks.FirstOrDefault(block => block.Type == BlockType.RERL)
            ?? throw Errors.Unsupported("MATERIAL_LOOK_LAYOUT", $"'{name}' has no list of the files it uses.", "Report the file.");
        var payloads = new Dictionary<int, byte[]>
        {
            [resource.Blocks.IndexOf(material)] = stream.ToArray(),
            [resource.Blocks.IndexOf(references)] = CompiledAddedEffects.WithReferences(compiled.AsSpan((int)references.Offset, (int)references.Size), [maskTexturePath], replace: false),
        };
        var result = CompiledModelEdit.ReplaceBlocks(compiled, resource, payloads);
        Verify(result, logicalPath, look, maskTexturePath, program);
        return result;
    }

    /// <summary>
    /// The compiled form of <c>amplitude * sin(speed * time()) + offset</c> as the game's material compiler writes it: a stack program,
    /// <c>07</c> pushes a 32-bit float, <c>06 1B 00</c> calls <c>time()</c>, <c>15</c> multiplies, <c>06 00 00</c> calls <c>sin()</c>,
    /// <c>13</c> adds and <c>00</c> ends it.
    /// </summary>
    public static byte[] PulseExpression(float amplitude, float speed, float offset)
    {
        var bytes = new byte[25];
        bytes[0] = 0x07;
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(1), amplitude);
        bytes[5] = 0x07;
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(6), speed);
        new byte[] { 0x06, 0x1B, 0x00, 0x15, 0x06, 0x00, 0x00, 0x15, 0x07 }.CopyTo(bytes, 10);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(19), offset);
        bytes[23] = 0x13;
        bytes[24] = 0x00;
        return bytes;
    }

    /// <summary>Puts an expression program on a setting (null: removes one the material had, so the fixed value counts).</summary>
    internal static void SetDynamic(KVObject dynamics, string setting, byte[]? program)
    {
        var index = -1;
        for (var i = 0; i < dynamics.Count; i++)
        {
            if (dynamics[i]["m_name"].ToString(CultureInfo.InvariantCulture) == setting)
            {
                index = i;
            }
        }

        if (program is null)
        {
            if (index >= 0)
            {
                dynamics.RemoveAt(index);
            }

            return;
        }

        var value = KVObject.Blob(program);
        if (index >= 0)
        {
            value.Flag = dynamics[index]["m_value"].Flag;
            dynamics[index]["m_value"] = value;
            return;
        }

        var entry = KVObject.Collection();
        var name = new KVObject(setting);
        if (dynamics.Count > 0)
        {
            entry.Flag = dynamics[0].Flag;
            name.Flag = dynamics[0]["m_name"].Flag;
            value.Flag = dynamics[0]["m_value"].Flag;
        }

        entry.Add("m_name", name);
        entry.Add("m_value", value);
        dynamics.Add(entry);
    }

    /// <summary>A parameter list that has entries (their layout is the template for added ones) with the value key the edit writes.</summary>
    private static KVObject Settings(KVObject root, string list, string valueKey, string name, bool array = false) =>
        root.TryGetValue(list, out var found) && found.IsArray && found.Count > 0 && found[0].TryGetValue(valueKey, out var first) && (!array || (first.IsArray && first.Count > 0)) ? found
            : throw Errors.Unsupported("MATERIAL_LOOK_LAYOUT", $"'{name}' has no '{list}' settings to copy the layout from.", "Report the file.");

    private static void Validate(MaterialLook look)
    {
        foreach (var (label, value, max) in new[]
        {
            ("red", look.R, 1f), ("green", look.G, 1f), ("blue", look.B, 1f), ("strength", look.Strength, 10f), ("pulse speed", look.PulseSpeed, 60f), ("pulse amount", look.PulseAmount, 1f),
        })
        {
            if (!float.IsFinite(value) || value < 0f || value > max)
            {
                throw Errors.Input("MATERIAL_LOOK_VALUE", $"The glow's {label} ({value.ToString(CultureInfo.InvariantCulture)}) is outside 0 to {max.ToString(CultureInfo.InvariantCulture)}.", "Change the value.");
            }
        }
    }

    /// <summary>Reopens the written material: still <c>pbr.vfx</c>, with the look's settings, the mask texture and the pulse bytes exactly as written.</summary>
    private static void Verify(byte[] result, string logicalPath, MaterialLook look, string maskTexturePath, byte[]? program)
    {
        using var resource = new Resource { FileName = logicalPath };
        resource.Read(new MemoryStream(result));
        var material = resource.DataBlock as Material;
        var dynamic = material is not null && material.Data.TryGetValue("m_dynamicParams", out var list) && list.IsArray
            ? list.Values.FirstOrDefault(item => item["m_name"].ToString(CultureInfo.InvariantCulture) == PulseSetting)
            : null;
        var pulse = dynamic is not null && dynamic["m_value"].ValueType == KVValueType.BinaryBlob ? dynamic["m_value"].AsBlob() : null;
        if (material is null
            || !string.Equals(material.ShaderName, "pbr.vfx", StringComparison.OrdinalIgnoreCase)
            || material.IntParams.GetValueOrDefault("F_SELF_ILLUM") != 1
            || Math.Abs(material.FloatParams.GetValueOrDefault(PulseSetting) - look.Strength) > 0.001f
            || material.TextureParams.GetValueOrDefault("g_tSelfIllumMask") != maskTexturePath
            || (look.Edges && Math.Abs(material.FloatParams.GetValueOrDefault("g_flSelfIllumFresnelMaskExponent") - 10f) > 0.001f)
            || (program is null ? dynamic is not null : pulse is null || !pulse.AsSpan().SequenceEqual(program)))
        {
            throw Errors.Verification("MATERIAL_LOOK_VERIFY", $"'{Path.GetFileName(logicalPath)}' does not read back with the glow that was written.", "Report the file.");
        }
    }
}
