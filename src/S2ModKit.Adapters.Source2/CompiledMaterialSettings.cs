using System.Globalization;
using System.Numerics;
using S2ModKit.Domain;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

/// <summary>Settings to write into a compiled material: whole numbers (shader features), numbers, colours or vectors, texture
/// paths (<c>.vtex</c>, without <c>_c</c>) and compiled expressions (<c>m_dynamicParams</c>, evaluated by the game every frame). A
/// setting the material lacks is added in the layout of its own settings of that kind.</summary>
public sealed record MaterialSettings(
    IReadOnlyDictionary<string, int>? Ints = null,
    IReadOnlyDictionary<string, float>? Floats = null,
    IReadOnlyDictionary<string, Vector4>? Vectors = null,
    IReadOnlyDictionary<string, string>? Textures = null,
    IReadOnlyDictionary<string, byte[]>? Expressions = null);

/// <summary>
/// Writes <see cref="MaterialSettings"/> into a compiled <c>pbr.vfx</c> material (the finishes of the X10 looks: glass, crystal,
/// pearlescent). Only the DATA block (and the list of used files, for new textures) changes; the result is read back and every
/// written value checked. Nothing here touches depth testing.
/// </summary>
public static class CompiledMaterialSettings
{
    public static byte[] Apply(byte[] compiled, string logicalPath, MaterialSettings settings)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(settings);
        using var resource = new Resource { FileName = logicalPath };
        resource.Read(new MemoryStream(compiled));
        var name = Path.GetFileName(logicalPath);
        if (resource.DataBlock is not Material material)
        {
            throw Errors.Input("MATERIAL_EDIT_NOT_A_MATERIAL", $"'{logicalPath}' is not a material.", "Pass a compiled .vmat_c.");
        }

        if (!string.Equals(material.ShaderName, "pbr.vfx", StringComparison.OrdinalIgnoreCase))
        {
            throw Errors.Unsupported("MATERIAL_SETTINGS_SHADER", $"'{name}' uses the shader '{material.ShaderName}'.", "Only parts with the game's usual hero material can get a finish.");
        }

        var root = material.Data;
        foreach (var (setting, value) in settings.Ints ?? new Dictionary<string, int>())
        {
            CompiledMaterialEdit.Set(List(root, "m_intParams", "m_nValue", name), setting, "m_nValue", like => CompiledMaterialEdit.Whole(like, value));
        }

        foreach (var (setting, value) in settings.Floats ?? new Dictionary<string, float>())
        {
            Check(setting, value);
            CompiledMaterialEdit.SetFloat(root, List(root, "m_floatParams", "m_flValue", name), setting, value);
        }

        foreach (var (setting, value) in settings.Vectors ?? new Dictionary<string, Vector4>())
        {
            foreach (var number in new[] { value.X, value.Y, value.Z, value.W })
            {
                Check(setting, number);
            }

            CompiledMaterialEdit.Set(List(root, "m_vectorParams", "m_value", name, array: true), setting, "m_value", like =>
            {
                var vector = KVObject.Array();
                vector.Flag = like.Flag;
                foreach (var number in new[] { value.X, value.Y, value.Z, value.W })
                {
                    vector.Add(CompiledMaterialEdit.Same(like[0], number));
                }

                return vector;
            });
        }

        var textures = settings.Textures ?? new Dictionary<string, string>();
        foreach (var (setting, path) in textures)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase))
            {
                throw Errors.Input("MATERIAL_SETTINGS_TEXTURE", $"'{path}' for '{setting}' is not a texture path (.vtex).", "Report the file.");
            }

            CompiledMaterialEdit.Set(List(root, "m_textureParams", "m_pValue", name), setting, "m_pValue", like => new KVObject(path) { Flag = like.Flag });
        }

        foreach (var (setting, program) in settings.Expressions ?? new Dictionary<string, byte[]>())
        {
            var dynamics = root.TryGetValue("m_dynamicParams", out var list) && list.IsArray ? list
                : throw Errors.Unsupported("MATERIAL_SETTINGS_LAYOUT", $"'{name}' has no list of dynamic settings.", "Report the file.");
            CompiledMaterialLook.SetDynamic(dynamics, setting, program);
        }

        if (root.TryGetValue("m_materialName", out var materialName) && logicalPath.EndsWith("_c", StringComparison.Ordinal))
        {
            root["m_materialName"] = new KVObject(logicalPath[..^2]) { Flag = materialName.Flag };
        }

        using var stream = new MemoryStream();
        material.Serialize(stream);
        var payloads = new Dictionary<int, byte[]> { [resource.Blocks.IndexOf(material)] = stream.ToArray() };
        if (textures.Count > 0)
        {
            var references = resource.Blocks.FirstOrDefault(block => block.Type == BlockType.RERL)
                ?? throw Errors.Unsupported("MATERIAL_SETTINGS_LAYOUT", $"'{name}' has no list of the files it uses.", "Report the file.");
            payloads[resource.Blocks.IndexOf(references)] = CompiledAddedEffects.WithReferences(compiled.AsSpan((int)references.Offset, (int)references.Size), [.. textures.Values.Distinct()], replace: false);
        }

        var result = CompiledModelEdit.ReplaceBlocks(compiled, resource, payloads);
        Verify(result, logicalPath, settings);
        return result;
    }

    /// <summary>
    /// The compiled expression of a flipbook: <paramref name="frames"/> pictures in a grid of <paramref name="columns"/> x
    /// <paramref name="rows"/> cells (row by row from the top left), shown one at a time at <paramref name="framesPerSecond"/>, looping.
    /// Put it on <c>g_vAlbedoTexcoordOffset1</c> with <c>F_ENABLE_TEXTURE_TRANSFORMS</c> and <c>g_vAlbedoTexcoordScale1</c> =
    /// (1 / columns, 1 / rows). The shader scales texture coordinates around the middle of the texture (hideout portrait test,
    /// 2026-11-17: half of frames 4 and 5 at offset 0), so cell (c, r) needs the offset ((c + 0.5) / columns - 0.5, (r + 0.5) / rows -
    /// 0.5). With i = floor(frac(time() * fps / frames) * frames): c = frac(i / columns) * columns, r = floor(i / columns). Columns
    /// should be a power of two so i / columns is exact. Stack program: <c>07</c> pushes a float, <c>06 nn 00</c> calls function nn
    /// (frac 3, floor 4, float2 26, time 27), <c>13</c>/<c>14</c>/<c>15</c>/<c>16</c> add, subtract, multiply, divide, <c>00</c> ends.
    /// <c>time()</c> runs on baked map geometry too (the hideout portrait counted 1 to 8 in game, 2026-11-17). A surface whose texture
    /// coordinates do not span exactly 0 to 1 (the hideout portrait's run 0 to 1.03125 across, which showed a strip of the next frame
    /// at its right edge) passes its range: each cell is then fitted to it, and <see cref="FlipbookScale"/> gives the matching scale.
    /// In general the cell's offset is (c + (0.5 - min) / (max - min)) / columns - 0.5.
    /// </summary>
    public static byte[] FlipbookOffsetExpression(int frames, float framesPerSecond, int columns = 0, int rows = 1, Vector4? range = null)
    {
        columns = columns <= 0 ? frames : columns;
        var (uMin, vMin, uMax, vMax) = Range(range);
        if (frames < 2 || frames > 1024 || !float.IsFinite(framesPerSecond) || framesPerSecond <= 0f || framesPerSecond > 120f
            || rows < 1 || columns * rows < frames || columns > 1024 || rows > 1024)
        {
            throw Errors.Input("MATERIAL_FLIPBOOK_VALUE", string.Create(CultureInfo.InvariantCulture, $"A flipbook needs 2 to 1024 frames at more than 0 and at most 120 frames a second, in a grid that holds them all ({frames} frames, {framesPerSecond}/s, {columns} x {rows})."), "Change the values.");
        }

        var program = new List<byte>();
        void Op(byte code) => program.Add(code);
        void Function(byte id) => program.AddRange([0x06, id, 0x00]);
        void Number(float value)
        {
            var bytes = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
            program.Add(0x07);
            program.AddRange(bytes);
        }

        void Frame()                                                        // i = floor(frac(time() * fps / frames) * frames)
        {
            Function(27);
            Number(framesPerSecond / frames);
            Op(0x15);
            Function(3);
            Number(frames);
            Op(0x15);
            Function(4);
        }

        void Centre(int count, float min, float max)                        // (value + (0.5 - min) / (max - min)) / count - 0.5
        {
            Number((0.5f - min) / (max - min));
            Op(0x13);
            Number(count);
            Op(0x16);
            Number(0.5f);
            Op(0x14);
        }

        Frame();
        if (rows > 1)
        {
            Number(columns);                                                // column: frac(i / columns) * columns
            Op(0x16);
            Function(3);
            Number(columns);
            Op(0x15);
        }

        Centre(columns, uMin, uMax);
        if (rows > 1)
        {
            Frame();                                                        // row: floor(i / columns)
            Number(columns);
            Op(0x16);
            Function(4);
            Centre(rows, vMin, vMax);
        }
        else
        {
            Number(((0.5f - vMin) / (vMax - vMin)) - 0.5f);                 // one row: only the range's own shift
        }

        Function(26);                                                       // float2(u, v)
        Op(0x00);
        return [.. program];
    }

    /// <summary>The <c>g_vAlbedoTexcoordScale1</c> for a flipbook of <paramref name="columns"/> x <paramref name="rows"/> cells on a surface
    /// whose texture coordinates span <paramref name="range"/> (u min, v min, u max, v max; default 0 to 1): one cell across that span.</summary>
    public static Vector4 FlipbookScale(int columns, int rows, Vector4? range = null)
    {
        var (uMin, vMin, uMax, vMax) = Range(range);
        return new Vector4(1f / (columns * (uMax - uMin)), 1f / (rows * (vMax - vMin)), 0f, 0f);
    }

    private static (float UMin, float VMin, float UMax, float VMax) Range(Vector4? range)
    {
        var r = range ?? new Vector4(0f, 0f, 1f, 1f);
        if (!float.IsFinite(r.X) || !float.IsFinite(r.Y) || !float.IsFinite(r.Z) || !float.IsFinite(r.W) || r.Z - r.X < 0.01f || r.W - r.Y < 0.01f)
        {
            throw Errors.Input("MATERIAL_FLIPBOOK_RANGE", string.Create(CultureInfo.InvariantCulture, $"The surface's texture range ({r.X}, {r.Y}) to ({r.Z}, {r.W}) is empty."), "Measure the surface again.");
        }

        return (r.X, r.Y, r.Z, r.W);
    }

    private static KVObject List(KVObject root, string list, string valueKey, string name, bool array = false) =>
        root.TryGetValue(list, out var found) && found.IsArray && found.Count > 0 && found[0].TryGetValue(valueKey, out var first) && (!array || (first.IsArray && first.Count > 0)) ? found
            : throw Errors.Unsupported("MATERIAL_SETTINGS_LAYOUT", $"'{name}' has no '{list}' settings to copy the layout from.", "Report the file.");

    private static void Check(string setting, float value)
    {
        if (!float.IsFinite(value))
        {
            throw Errors.Input("MATERIAL_SETTINGS_VALUE", $"'{setting}' is not a number.", "Change the value.");
        }
    }

    private static byte[]? Expression(Material material, string setting) =>
        material.Data.TryGetValue("m_dynamicParams", out var list) && list.IsArray
            && list.Values.FirstOrDefault(item => item["m_name"].ToString(CultureInfo.InvariantCulture) == setting) is { } entry
            && entry["m_value"].ValueType == KVValueType.BinaryBlob ? entry["m_value"].AsBlob() : null;

    private static void Verify(byte[] result, string logicalPath, MaterialSettings settings)
    {
        using var resource = new Resource { FileName = logicalPath };
        resource.Read(new MemoryStream(result));
        var material = resource.DataBlock as Material;
        var wrong = material is null
            || (settings.Ints ?? new Dictionary<string, int>()).Any(pair => material.IntParams.GetValueOrDefault(pair.Key) != pair.Value)
            || (settings.Floats ?? new Dictionary<string, float>()).Any(pair => Math.Abs(material.FloatParams.GetValueOrDefault(pair.Key, float.NaN) - pair.Value) > 1e-4f || material.IntParams.ContainsKey(pair.Key))
            || (settings.Vectors ?? new Dictionary<string, Vector4>()).Any(pair => !material.VectorParams.TryGetValue(pair.Key, out var v) || Vector4.Distance(v, pair.Value) > 1e-4f)
            || (settings.Textures ?? new Dictionary<string, string>()).Any(pair => material.TextureParams.GetValueOrDefault(pair.Key) != pair.Value)
            || (settings.Expressions ?? new Dictionary<string, byte[]>()).Any(pair => !Expression(material, pair.Key).AsSpan().SequenceEqual(pair.Value));
        if (wrong)
        {
            throw Errors.Verification("MATERIAL_SETTINGS_VERIFY", string.Create(CultureInfo.InvariantCulture, $"'{Path.GetFileName(logicalPath)}' does not read back with the settings written."), "Report the file.");
        }
    }
}
