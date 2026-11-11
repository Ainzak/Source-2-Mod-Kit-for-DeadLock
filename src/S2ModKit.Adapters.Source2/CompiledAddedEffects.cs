using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using S2ModKit.Domain;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

/// <summary>A point on a model for an added effect: an attachment <paramref name="Name"/> on <paramref name="Bone"/> at
/// <paramref name="Offset"/> in that bone's space (it follows the bone in every animation).</summary>
public sealed record EffectAttachment(string Name, string Bone, Vector3 Offset);

/// <summary>An effect to add to a model: the effect file (<c>.vpcf</c> path), the bone it sits on (null: at the model's origin,
/// "follows the hero") and its place in that bone's space.</summary>
/// <param name="Source">Where the effect comes from when it is not the game's or the edited mod's own: a mod's archive or a loose
/// compiled effect file. Its files are copied into the build under new names (<see cref="CompiledAddedEffects.Rename"/>).</param>
/// <param name="Parent">For an effect that plays during an ability: the ability's effect file it is added to as a child
/// (<see cref="CompiledAddedEffects.AddChild"/>); it then plays where and when that effect plays, and <paramref name="Bone"/> and
/// <paramref name="Offset"/> are not used. Null: always on.</param>
public sealed record AddedEffect(string Effect, string? Bone, Vector3 Offset, string? Source = null, string? Parent = null);

/// <summary>
/// Always-on effects added to a compiled model (<c>add_model_effects@1</c>), by the route nine of the game's own hero models use
/// (Celeste's horn, Abrams' book, Doorman's keys): the model's key-value text lists effects in <c>particle_cfg_list</c>
/// (<c>{ name = resource:"x.vpcf", config = "preview" }</c>), and each listed effect binds its control points to the model's
/// attachments in its own control point configuration of that name.
/// <list type="bullet">
/// <item><see cref="AddToModel"/> adds attachments to every embedded mesh that has them, lists the effects and adds them to the
/// model's external references (ids: MurmurHash64B of the lower-case path, seed 0xEDABCDEF, as every stored id).</item>
/// <item><see cref="Wrapper"/> makes the listed effect from the game's own wrapper of this kind
/// (<see cref="TemplateEffect"/>: a configuration, one operator that puts a control point at an attachment, one child): control
/// points 0 and 1 follow the attachment, the operator copies point 1 to point 0, and the child is the chosen effect.</item>
/// </list>
/// Changed blocks are re-serialized and spliced in (<see cref="CompiledModelEdit.ReplaceBlocks"/>); other blocks keep their bytes.
/// Offline evidence only until checked in game.
/// </summary>
public static class CompiledAddedEffects
{
    /// <summary>The game effect the wrappers are made from (read from the user's game at build time).</summary>
    public const string TemplateEffect = "particles/abilities/doorman/doorman_ambient.vpcf";

    /// <summary>The control point configuration the game's model-listed effects use.</summary>
    public const string Config = "preview";

    private const string ListKey = "particle_cfg_list";

    /// <summary>
    /// The files of a mod that adds <paramref name="effects"/> to a model (compiled paths and bytes): the model with one attachment
    /// (<c>s2mk_fx_N</c>) per placed effect and the listed wrappers <c>particles/s2modkit/&lt;model&gt;/added_N.vpcf</c>, numbered after
    /// the ones an earlier build left in the model. <paramref name="template"/> is the compiled <see cref="TemplateEffect"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> Build(byte[] model, string modelPath, byte[] template, IReadOnlyList<AddedEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        var logical = modelPath.EndsWith("_c", StringComparison.Ordinal) ? modelPath : modelPath + "_c";
        var folder = $"particles/s2modkit/{Path.GetFileNameWithoutExtension(logical[..^2])}/added_";
        var next = ModelEffects(model, logical).Where(effect => effect.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            .Select(effect => int.TryParse(Path.GetFileNameWithoutExtension(effect)["added_".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0)
            .DefaultIfEmpty(0).Max() + 1;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var attachments = new List<EffectAttachment>();
        var wrappers = new List<string>();
        foreach (var effect in effects)
        {
            var number = (next++).ToString(CultureInfo.InvariantCulture);
            var attachment = effect.Bone is null ? null : new EffectAttachment("s2mk_fx_" + number, effect.Bone, effect.Offset);
            if (attachment is not null)
            {
                attachments.Add(attachment);
            }

            wrappers.Add(folder + number + ".vpcf");
            files[wrappers[^1] + "_c"] = Wrapper(template, effect.Effect, attachment?.Name);
        }

        files[logical] = AddToModel(model, logical, attachments, wrappers);
        return files;
    }

    /// <summary>The effects the model lists (<c>.vpcf</c> paths), in order.</summary>
    public static IReadOnlyList<string> ModelEffects(byte[] compiled, string logicalPath)
    {
        using var resource = Open(compiled, logicalPath, out var model);
        return [.. Listed(KeyValueText(model))];
    }

    /// <summary>The model's attachments (name and bone of the first influence) of its first mesh that has any.</summary>
    public static IReadOnlyList<(string Name, string Bone, Vector3 Offset)> Attachments(byte[] compiled, string logicalPath)
    {
        using var resource = Open(compiled, logicalPath, out _);
        var mesh = resource.Blocks.OfType<Mesh>().FirstOrDefault(block => AttachmentList(block) is { Count: > 0 });
        return mesh is null
            ? []
            : [.. AttachmentList(mesh)!.Values.Select(entry => entry["value"]).Select(value => (
                value["m_name"].ToString(CultureInfo.InvariantCulture),
                value["m_influenceNames"][0].ToString(CultureInfo.InvariantCulture),
                new Vector3(Float(value["m_vInfluenceOffsets"][0][0]), Float(value["m_vInfluenceOffsets"][0][1]), Float(value["m_vInfluenceOffsets"][0][2]))))];
    }

    /// <summary>A copy of the model with <paramref name="attachments"/> added (an attachment of the same name is replaced) and
    /// <paramref name="effects"/> (<c>.vpcf</c> paths) listed as always-on effects (already listed ones are kept once).</summary>
    public static byte[] AddToModel(byte[] compiled, string logicalPath, IReadOnlyList<EffectAttachment> attachments, IReadOnlyList<string> effects)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(effects);
        using var resource = Open(compiled, logicalPath, out var model);
        var payloads = new Dictionary<int, byte[]>();

        if (attachments.Count > 0)
        {
            var bones = model.Data["m_modelSkeleton"]["m_boneName"].Values.Select(name => name.ToString(CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
            if (attachments.FirstOrDefault(attachment => !bones.Contains(attachment.Bone)) is { } missing)
            {
                throw Errors.Input("ADDED_EFFECT_BONE", $"The model has no bone '{missing.Bone}'.", "Place the effect on the model again.");
            }

            var meshes = resource.Blocks.OfType<Mesh>().Where(block => AttachmentList(block) is { Count: > 0 }).ToArray();
            if (meshes.Length == 0)
            {
                throw Errors.Unsupported("ADDED_EFFECT_NO_ATTACHMENTS", "The model has no attachment points of its own to copy the layout from.", "Use 'follows the hero' for this model, or report the file.");
            }

            foreach (var mesh in meshes)
            {
                var list = AttachmentList(mesh)!;
                foreach (var attachment in attachments)
                {
                    var entry = NewAttachment(list[0], attachment);
                    for (var i = list.Count - 1; i >= 0; i--)
                    {
                        if (list[i]["key"].ToString(CultureInfo.InvariantCulture) == attachment.Name)
                        {
                            list.RemoveAt(i);
                        }
                    }

                    list.Add(entry);
                }

                payloads[resource.Blocks.IndexOf(mesh)] = Serialize(mesh);
            }
        }

        if (effects.Count > 0)
        {
            var text = KeyValueText(model);
            var listed = Listed(text).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = effects.Where(effect => listed.Add(effect)).ToArray();
            if (added.Length > 0)
            {
                model.Data["m_modelInfo"]["m_keyValueText"] = new KVObject(List(text, added));
                payloads[resource.Blocks.IndexOf(model)] = Serialize(model);
                var references = resource.Blocks.FirstOrDefault(block => block.Type == BlockType.RERL)
                    ?? throw Errors.Unsupported("ADDED_EFFECT_NO_REFERENCES", "The model has no list of the files it uses.", "Report the file.");
                payloads[resource.Blocks.IndexOf(references)] = WithReferences(compiled.AsSpan((int)references.Offset, (int)references.Size), added, replace: false);
            }
        }

        return payloads.Count == 0 ? compiled : CompiledModelEdit.ReplaceBlocks(compiled, resource, payloads);
    }

    /// <summary>
    /// The listed effect for one added effect, made from <paramref name="template"/> (the compiled <see cref="TemplateEffect"/>):
    /// it plays <paramref name="childEffect"/> at <paramref name="attachment"/>, or at the model's origin when that is null
    /// ("follows the hero").
    /// </summary>
    public static byte[] Wrapper(byte[] template, string childEffect, string? attachment)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(childEffect);
        using var resource = new Resource { FileName = TemplateEffect + "_c" };
        resource.Read(new MemoryStream(template));
        var root = resource.DataBlock switch
        {
            BinaryKV3 kv3 => kv3.Data.Root,
            KeyValuesOrNTRO kv => kv.Data,
            _ => throw Layout("it has no effect data"),
        };
        if (resource.ResourceType != ResourceType.Particle
            || !root.TryGetValue("m_controlPointConfigurations", out var configurations) || !configurations.IsArray
            || !root.TryGetValue("m_PreEmissionOperators", out var operators) || !operators.IsArray || operators.Count != 1
            || !root.TryGetValue("m_Children", out var children) || !children.IsArray || children.Count != 1)
        {
            throw Layout("its configuration, operator or child is not where it was");
        }

        var configuration = configurations.Values.FirstOrDefault(item => item["m_name"].ToString(CultureInfo.InvariantCulture) == Config) ?? throw Layout($"it has no '{Config}' configuration");
        var drivers = configuration["m_drivers"];
        var origin = drivers.Values.FirstOrDefault(driver => !driver.ContainsKey("m_attachmentName") && !driver.ContainsKey("m_iControlPoint")) ?? throw Layout("it has no driver at the model's origin");
        var follow = drivers.Values.FirstOrDefault(driver => driver.ContainsKey("m_attachmentName") && driver.ContainsKey("m_iControlPoint")) ?? throw Layout("it has no driver at an attachment");
        var operation = operators[0];
        if (operation["_class"].ToString(CultureInfo.InvariantCulture) != "C_OP_SetSingleControlPointPosition" || !operation.ContainsKey("m_nCP1")
            || !operation.TryGetValue("m_vecCP1Pos", out var position) || !position.ContainsKey("m_vLiteralValue"))
        {
            throw Layout("its operator changed");
        }

        var newDrivers = KVObject.Array();
        newDrivers.Flag = drivers.Flag;
        if (attachment is null)
        {
            newDrivers.Add(Clone(origin));
            root["m_PreEmissionOperators"] = Empty(operators);
        }
        else
        {
            var first = Clone(follow);                                       // control point 0 at the attachment
            first.Remove("m_iControlPoint");
            first["m_attachmentName"] = Text(follow["m_attachmentName"], attachment);
            var second = Clone(follow);                                      // and control point 1, which the operator copies to point 0
            second["m_iControlPoint"] = Integer(follow["m_iControlPoint"], 1);
            second["m_attachmentName"] = Text(follow["m_attachmentName"], attachment);
            newDrivers.Add(first);
            newDrivers.Add(second);
            operation["m_nCP1"] = Integer(operation["m_nCP1"], 0);
            var zero = KVObject.Array();
            zero.Flag = position["m_vLiteralValue"].Flag;
            for (var i = 0; i < 3; i++)
            {
                zero.Add(Number(position["m_vLiteralValue"][i], 0));
            }

            position["m_vLiteralValue"] = zero;
        }

        configuration["m_drivers"] = newDrivers;
        var onlyConfiguration = KVObject.Array();
        onlyConfiguration.Flag = configurations.Flag;
        onlyConfiguration.Add(configuration);
        root["m_controlPointConfigurations"] = onlyConfiguration;
        children[0]["m_ChildRef"] = Text(children[0]["m_ChildRef"], childEffect);

        var data = resource.DataBlock!;
        var references = resource.Blocks.FirstOrDefault(block => block.Type == BlockType.RERL) ?? throw Layout("it has no list of the files it uses");
        return CompiledModelEdit.ReplaceBlocks(template, resource, new Dictionary<int, byte[]>
        {
            [resource.Blocks.IndexOf(data)] = Serialize(data),
            [resource.Blocks.IndexOf(references)] = WithReferences(template.AsSpan((int)references.Offset, (int)references.Size), [childEffect], replace: true),
        });
    }

    /// <summary>
    /// A copy of a compiled effect with <paramref name="childEffect"/> added to its child effects, so the child starts with it and
    /// shares its control points (the way community mods add effects to abilities). An effect that already has the child is
    /// returned as it is.
    /// </summary>
    public static byte[] AddChild(byte[] parent, string logicalPath, string childEffect)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(childEffect);
        using var resource = new Resource { FileName = logicalPath };
        resource.Read(new MemoryStream(parent));
        var root = resource.DataBlock switch
        {
            BinaryKV3 kv3 => kv3.Data.Root,
            KeyValuesOrNTRO kv => kv.Data,
            _ => throw Errors.Input("EFFECT_EDIT_NOT_AN_EFFECT", $"'{logicalPath}' has no effect data.", "Pass a compiled .vpcf_c."),
        };
        if (resource.ResourceType != ResourceType.Particle)
        {
            throw Errors.Input("EFFECT_EDIT_NOT_AN_EFFECT", $"'{logicalPath}' is not an effect.", "Pass a compiled .vpcf_c.");
        }

        var references = resource.Blocks.FirstOrDefault(block => block.Type == BlockType.RERL)
            ?? throw Errors.Unsupported("ADDED_EFFECT_NO_REFERENCES", $"'{Path.GetFileName(logicalPath)}' uses no other files, so nothing can be added to it this way.", "Choose another of the ability's effects.");
        KVObject entry;
        if (root.TryGetValue("m_Children", out var children) && children.IsArray)
        {
            if (children.Values.Any(child => child.TryGetValue("m_ChildRef", out var existing) && string.Equals(existing.ToString(CultureInfo.InvariantCulture), childEffect, StringComparison.OrdinalIgnoreCase)))
            {
                return parent;
            }

            entry = KVObject.Collection();
            entry.Add("m_ChildRef", children.Count > 0 && children[0].TryGetValue("m_ChildRef", out var like) ? Text(like, childEffect) : ResourceText(childEffect));
            children.Add(entry);
        }
        else
        {
            entry = KVObject.Collection();
            entry.Add("m_ChildRef", ResourceText(childEffect));
            var list = KVObject.Array();
            list.Add(entry);
            root["m_Children"] = list;
        }

        var data = resource.DataBlock!;
        return CompiledModelEdit.ReplaceBlocks(parent, resource, new Dictionary<int, byte[]>
        {
            [resource.Blocks.IndexOf(data)] = Serialize(data),
            [resource.Blocks.IndexOf(references)] = WithReferences(parent.AsSpan((int)references.Offset, (int)references.Size), [childEffect], replace: false),
        });

        static KVObject ResourceText(string path) => new(path) { Flag = KVFlag.Resource };
    }

    /// <summary>The files a compiled resource refers to (paths without <c>_c</c>), in stored order.</summary>
    public static IReadOnlyList<string> References(byte[] compiled, string logicalPath)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        using var resource = new Resource { FileName = logicalPath };
        resource.Read(new MemoryStream(compiled));
        return [.. (resource.ExternalReferences?.ResourceRefInfoList ?? []).Select(reference => reference.Name)];
    }

    /// <summary>
    /// A copy of a compiled effect whose references follow <paramref name="renames"/> (old path to new path, without <c>_c</c>, any
    /// case): the path strings in its data and its external reference list. An effect that refers to none of them is returned as it is.
    /// </summary>
    public static byte[] Rename(byte[] effect, string logicalPath, IReadOnlyDictionary<string, string> renames)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(renames);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in renames)
        {
            map[from.Replace('\\', '/')] = to;
        }

        using var resource = new Resource { FileName = logicalPath };
        resource.Read(new MemoryStream(effect));
        var root = resource.DataBlock switch
        {
            BinaryKV3 kv3 => kv3.Data.Root,
            KeyValuesOrNTRO kv => kv.Data,
            _ => throw Errors.Input("EFFECT_EDIT_NOT_AN_EFFECT", $"'{logicalPath}' has no effect data.", "Pass a compiled .vpcf_c."),
        };
        var references = resource.Blocks.FirstOrDefault(block => block.Type == BlockType.RERL);
        var names = (resource.ExternalReferences?.ResourceRefInfoList ?? []).Select(reference => reference.Name).ToArray();
        if (references is null || !names.Any(map.ContainsKey))
        {
            return effect;
        }

        Retarget(root, map);
        var data = resource.DataBlock!;
        return CompiledModelEdit.ReplaceBlocks(effect, resource, new Dictionary<int, byte[]>
        {
            [resource.Blocks.IndexOf(data)] = Serialize(data),
            [resource.Blocks.IndexOf(references)] = WithReferences(effect.AsSpan((int)references.Offset, (int)references.Size), [.. names.Select(name => map.GetValueOrDefault(name, name))], replace: true),
        });
    }

    private static void Retarget(KVObject node, Dictionary<string, string> map)
    {
        if (node.IsArray)
        {
            for (var i = 0; i < node.Count; i++)
            {
                if (node[i].ValueType == KVValueType.String && map.TryGetValue(node[i].ToString(CultureInfo.InvariantCulture).Replace('\\', '/'), out var to))
                {
                    var replaced = Each(node, (item, at) => at == i ? Text(item, to) : item);
                    node.Clear();
                    foreach (var item in replaced.Values.ToArray())
                    {
                        node.Add(item);
                    }
                }
                else
                {
                    Retarget(node[i], map);
                }
            }

            return;
        }

        if (!node.IsCollection)
        {
            return;
        }

        foreach (var key in node.Keys.ToArray())
        {
            var child = node[key];
            if (child.ValueType == KVValueType.String && map.TryGetValue(child.ToString(CultureInfo.InvariantCulture).Replace('\\', '/'), out var to))
            {
                node[key] = Text(child, to);
            }
            else
            {
                Retarget(child, map);
            }
        }
    }

    /// <summary>The id the game stores for a referenced file: MurmurHash64B of the lower-case path with seed 0xEDABCDEF.</summary>
    public static ulong ResourceId(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
#pragma warning disable CA1308 // the engine hashes the lower-case path
        var data = Encoding.UTF8.GetBytes(path.Replace('\\', '/').ToLowerInvariant());
#pragma warning restore CA1308
        const uint M = 0x5bd1e995;
        var length = data.Length;
        var at = 0;
        var h1 = 0xEDABCDEF ^ (uint)length;
        uint h2 = 0;

        static uint Mix(uint k) => unchecked(((k * M) ^ ((k * M) >> 24)) * M);

        while (length >= 8)
        {
            h1 = unchecked(h1 * M) ^ Mix(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)));
            h2 = unchecked(h2 * M) ^ Mix(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4)));
            at += 8;
            length -= 8;
        }

        if (length >= 4)
        {
            h1 = unchecked(h1 * M) ^ Mix(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)));
            at += 4;
            length -= 4;
        }

        if (length == 3)
        {
            h2 ^= (uint)data[at + 2] << 16;
        }

        if (length >= 2)
        {
            h2 ^= (uint)data[at + 1] << 8;
        }

        if (length >= 1)
        {
            h2 = unchecked((h2 ^ data[at]) * M);
        }

        h1 = unchecked((h1 ^ (h2 >> 18)) * M);
        h2 = unchecked((h2 ^ (h1 >> 22)) * M);
        h1 = unchecked((h1 ^ (h2 >> 17)) * M);
        h2 = unchecked((h2 ^ (h1 >> 19)) * M);
        return ((ulong)h1 << 32) | h2;
    }

    /// <summary>
    /// The external reference block with <paramref name="added"/> paths (appended, or alone when <paramref name="replace"/>).
    /// Layout: offset to the entries (from byte 0), entry count, padding, then 16 bytes per entry (id, offset from that field to
    /// the name) and the zero-terminated names.
    /// </summary>
    internal static byte[] WithReferences(ReadOnlySpan<byte> block, IReadOnlyList<string> added, bool replace)
    {
        var start = (int)BinaryPrimitives.ReadUInt32LittleEndian(block);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        if (start < 8 || start + (count * 16) > block.Length)
        {
            throw Errors.Unsupported("ADDED_EFFECT_REFERENCES", "The file's list of the files it uses has an unknown layout.", "Report the file.");
        }

        var entries = new List<(ulong Id, string Name)>();
        for (var i = 0; i < count && !replace; i++)
        {
            var at = start + (i * 16);
            var name = at + 8 + (int)BinaryPrimitives.ReadInt64LittleEndian(block[(at + 8)..]);
            var end = name + block[name..].IndexOf((byte)0);
            entries.Add((BinaryPrimitives.ReadUInt64LittleEndian(block[at..]), Encoding.UTF8.GetString(block[name..end])));
        }

        foreach (var path in added)
        {
            var id = ResourceId(path);
            if (entries.All(entry => entry.Id != id))
            {
                entries.Add((id, path));
            }
        }

        var names = entries.Select(entry => Encoding.UTF8.GetBytes(entry.Name)).ToArray();
        var bytes = new byte[start + (entries.Count * 16) + names.Sum(name => name.Length + 1)];
        block[..start].CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)entries.Count);
        var text = start + (entries.Count * 16);
        for (var i = 0; i < entries.Count; i++)
        {
            var at = start + (i * 16);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), entries[i].Id);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at + 8), text - (at + 8));
            names[i].CopyTo(bytes.AsSpan(text));
            text += names[i].Length + 1;
        }

        return bytes;
    }

    internal static Resource Open(byte[] compiled, string logicalPath, out Model model)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        var resource = new Resource { FileName = logicalPath };
        try
        {
            resource.Read(new MemoryStream(compiled));
            model = resource.DataBlock as Model ?? throw Errors.Input("MODEL_EDIT_NOT_A_MODEL", $"'{logicalPath}' is not a model.", "Pass a compiled .vmdl_c.");
            return resource;
        }
        catch
        {
            resource.Dispose();
            throw;
        }
    }

    private static S2ModKitException Layout(string what) =>
        Errors.Unsupported("ADDED_EFFECT_TEMPLATE", $"The game's effect '{TemplateEffect}' changed ({what}), so added effects cannot be built from it.", "Update S2ModKit.");

    internal static string KeyValueText(Model model) => model.Data["m_modelInfo"]["m_keyValueText"].ToString(CultureInfo.InvariantCulture);

    internal static KVObject? AttachmentList(Mesh mesh) => mesh.Data.TryGetValue("m_attachments", out var list) && list.IsArray ? list : null;

    /// <summary>The effect paths in the text's <c>particle_cfg_list</c>.</summary>
    private static IEnumerable<string> Listed(string text)
    {
        var (start, end) = ListSpan(text);
        if (start < 0)
        {
            yield break;
        }

        const string Marker = "name = resource:\"";
        for (var at = text.IndexOf(Marker, start, end - start, StringComparison.Ordinal); at >= 0; at = text.IndexOf(Marker, at + 1, end - at - 1, StringComparison.Ordinal))
        {
            var from = at + Marker.Length;
            yield return text[from..text.IndexOf('"', from)];
        }
    }

    /// <summary>Where the list's entries are in the text: after its opening bracket, up to its closing one; (-1, -1) without a list.</summary>
    private static (int Start, int End) ListSpan(string text)
    {
        var key = text.IndexOf("\n\t" + ListKey + " = ", StringComparison.Ordinal);
        if (key < 0)
        {
            return (-1, -1);
        }

        var open = text.IndexOf('[', key);
        var close = text.IndexOf("\n\t]", open, StringComparison.Ordinal);
        return open < 0 || close < 0
            ? throw Errors.Unsupported("ADDED_EFFECT_LIST", "The model's list of always-on effects has an unknown layout.", "Report the file.")
            : (open + 1, close + 1);
    }

    /// <summary>The text with <paramref name="effects"/> listed (the list is created before the closing brace when the model has none).</summary>
    private static string List(string text, IReadOnlyList<string> effects)
    {
        var line = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var entries = string.Concat(effects.Select(effect => $"\t\t{{{line}\t\t\tname = resource:\"{effect}\"{line}\t\t\tconfig = \"{Config}\"{line}\t\t}},{line}"));
        var (start, end) = ListSpan(text);
        if (start >= 0)
        {
            return text.Insert(end, entries);
        }

        var last = text.LastIndexOf('}');
        return last < 0
            ? throw Errors.Unsupported("ADDED_EFFECT_LIST", "The model's own settings text has an unknown layout.", "Report the file.")
            : text.Insert(last, $"\t{ListKey} = {line}\t[{line}{entries}\t]{line}");
    }

    private static KVObject NewAttachment(KVObject template, EffectAttachment attachment)
    {
        var entry = Clone(template);
        var value = entry["value"];
        entry["key"] = Text(entry["key"], attachment.Name);
        value["m_name"] = Text(value["m_name"], attachment.Name);
        var slots = value["m_influenceNames"].Count;
        float[] offset = [attachment.Offset.X, attachment.Offset.Y, attachment.Offset.Z];
        float[] upright = [0, 0, 0, 1];
        value["m_influenceNames"] = Each(value["m_influenceNames"], (old, i) => Text(old, i == 0 ? attachment.Bone : string.Empty));
        value["m_vInfluenceRotations"] = Each(value["m_vInfluenceRotations"], (old, _) => Each(old, (number, k) => Number(number, upright[k])));
        value["m_vInfluenceOffsets"] = Each(value["m_vInfluenceOffsets"], (old, i) => Each(old, (number, k) => Number(number, i == 0 ? offset[k] : 0)));
        value["m_influenceWeights"] = Each(value["m_influenceWeights"], (old, i) => Number(old, i == 0 ? 1 : 0));
        value["m_bInfluenceRootTransform"] = Each(value["m_bInfluenceRootTransform"], (old, _) => Flagged(new KVObject(false), old));
        value["m_nInfluences"] = Integer(value["m_nInfluences"], 1);
        if (value.ContainsKey("m_bIgnoreRotation"))
        {
            value["m_bIgnoreRotation"] = Flagged(new KVObject(false), value["m_bIgnoreRotation"]);
        }

        return slots >= 1 ? entry : throw Errors.Unsupported("ADDED_EFFECT_NO_ATTACHMENTS", "The model's attachment points have an unknown layout.", "Report the file.");
    }

    internal static KVObject Each(KVObject list, Func<KVObject, int, KVObject> item)
    {
        var copy = KVObject.Array();
        copy.Flag = list.Flag;
        for (var i = 0; i < list.Count; i++)
        {
            copy.Add(item(list[i], i));
        }

        return copy;
    }

    private static KVObject Empty(KVObject list)
    {
        var copy = KVObject.Array();
        copy.Flag = list.Flag;
        return copy;
    }

    /// <summary>A deep copy (lists and collections are new; plain values are shared, they are never changed in place).</summary>
    internal static KVObject Clone(KVObject node)
    {
        if (node.IsArray)
        {
            return Each(node, (item, _) => Clone(item));
        }

        if (!node.IsCollection)
        {
            return node;
        }

        var copy = KVObject.Collection();
        copy.Flag = node.Flag;
        foreach (var (key, value) in node.Children)
        {
            copy.Add(key, Clone(value));
        }

        return copy;
    }

    internal static KVObject Flagged(KVObject value, KVObject like)
    {
        value.Flag = like.Flag;
        return value;
    }

    internal static KVObject Text(KVObject like, string text) => Flagged(new KVObject(text), like);

    internal static float Float(KVObject value) => value.ToSingle(CultureInfo.InvariantCulture);

    /// <summary>The number in the value's own type (the game reads typed values).</summary>
    internal static KVObject Number(KVObject like, float number) => Flagged(like.ValueType switch
    {
        KVValueType.FloatingPoint64 => new KVObject((double)number),
        KVValueType.FloatingPoint => new KVObject(number),
        _ => Integer(like, (int)number),
    }, like);

    internal static KVObject Integer(KVObject like, int number) => Flagged(like.ValueType switch
    {
        KVValueType.UInt64 => new KVObject((ulong)number),
        KVValueType.UInt32 => new KVObject((uint)number),
        KVValueType.Int64 => new KVObject((long)number),
        KVValueType.UInt16 => new KVObject((ushort)number),
        KVValueType.Int16 => new KVObject((short)number),
        _ => new KVObject(number),
    }, like);

    internal static byte[] Serialize(Block block)
    {
        using var stream = new MemoryStream();
        block.Serialize(stream);
        return stream.ToArray();
    }
}
