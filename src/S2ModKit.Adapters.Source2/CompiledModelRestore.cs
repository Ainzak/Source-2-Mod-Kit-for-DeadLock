using System.Globalization;
using System.Text;
using S2ModKit.Domain;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

/// <summary>What a mod's hero model lacks that the game's current model of the same path has: settings blocks of the model's own
/// text that the app restores, attachment points it can restore (their bones are in the mod's skeleton) and attachment points
/// it cannot (bones missing or an unknown layout).</summary>
public sealed record ModelGaps(IReadOnlyList<string> Settings, IReadOnlyList<string> Attachments, IReadOnlyList<string> Skipped)
{
    public bool Any => Settings.Count > 0 || Attachments.Count > 0;
}

/// <summary>
/// Restores what later game updates added to a hero model and an older model mod lacks (<c>restore_model_settings@1</c>): whole
/// settings blocks of the model's text (<c>m_modelInfo.m_keyValueText</c>) from a fixed list (cameras, health bar and status
/// offsets, tagged sounds, hero-select scene, damage numbers) and attachment points whose bones the mod's skeleton has. Blocks
/// the mod has are never changed, nor anything else in the model; a block the mod left out on purpose is not on the list
/// (always-on effects, cloth, physics markup, body group mappings, bone constraints).
/// </summary>
public static class CompiledModelRestore
{
    /// <summary>The settings blocks that are restored when missing.</summary>
    public static readonly IReadOnlyList<string> RestorableSettings =
    [
        "CitadelCameraSettings_t", "AttachmentCameraData", "CitadelUnitStatusSettings_t", "TaggedSoundSettings_t",
        "CitadelModelSceneData_t", "CitadelModelDamageNumberSettings_t",
    ];

    public static ModelGaps Gaps(byte[] mod, byte[] game, string logicalPath)
    {
        using var modResource = CompiledAddedEffects.Open(mod, logicalPath, out var modModel);
        using var gameResource = CompiledAddedEffects.Open(game, logicalPath, out var gameModel);
        return Compare(modResource, modModel, gameResource, gameModel);
    }

    /// <summary>A copy of the mod's model with the gaps <see cref="Gaps"/> finds filled from the game's model (the mod's model
    /// unchanged when there are none).</summary>
    public static byte[] Restore(byte[] mod, byte[] game, string logicalPath)
    {
        using var modResource = CompiledAddedEffects.Open(mod, logicalPath, out var modModel);
        using var gameResource = CompiledAddedEffects.Open(game, logicalPath, out var gameModel);
        var gaps = Compare(modResource, modModel, gameResource, gameModel);
        if (!gaps.Any)
        {
            return mod;
        }

        var payloads = new Dictionary<int, byte[]>();
        if (gaps.Settings.Count > 0)
        {
            var text = CompiledAddedEffects.KeyValueText(modModel);
            var gameText = CompiledAddedEffects.KeyValueText(gameModel);
            var line = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var blocks = new StringBuilder();
            foreach (var key in gaps.Settings)
            {
                var block = Block(gameText, key) ?? throw Errors.Unsupported("MODEL_RESTORE_LAYOUT", $"The game's '{key}' settings have an unknown layout.", "Report the file.");
                blocks.Append(block.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", line, StringComparison.Ordinal)).Append(line);
            }

            var last = text.LastIndexOf('}');
            if (last < 0)
            {
                throw Errors.Unsupported("MODEL_RESTORE_LAYOUT", "The mod model's own settings text has an unknown layout.", "Report the file.");
            }

            modModel.Data["m_modelInfo"]["m_keyValueText"] = new KVObject(text.Insert(last, blocks.ToString()));
            payloads[modResource.Blocks.IndexOf(modModel)] = CompiledAddedEffects.Serialize(modModel);

            // files the restored settings name (tagged sounds) go into the model's list of the files it uses, as in the game's model
            var known = CompiledAddedEffects.References(mod, logicalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var named = System.Text.RegularExpressions.Regex.Matches(blocks.ToString(), "resource(?:_name)?:\"([^\"]+)\"").Select(match => match.Groups[1].Value)
                .Where(path => !known.Contains(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (named.Length > 0)
            {
                var references = modResource.Blocks.FirstOrDefault(block => block.Type == BlockType.RERL)
                    ?? throw Errors.Unsupported("MODEL_RESTORE_LAYOUT", "The mod model has no list of the files it uses.", "Report the file.");
                payloads[modResource.Blocks.IndexOf(references)] = CompiledAddedEffects.WithReferences(mod.AsSpan((int)references.Offset, (int)references.Size), named, replace: false);
            }
        }

        if (gaps.Attachments.Count > 0)
        {
            var source = GameAttachments(gameResource);
            foreach (var mesh in modResource.Blocks.OfType<Mesh>())
            {
                if (CompiledAddedEffects.AttachmentList(mesh) is not { Count: > 0 } list)
                {
                    continue;
                }

                var have = Names(list);
                var template = list[0];
                foreach (var name in gaps.Attachments.Where(name => !have.Contains(name)))
                {
                    list.Add(Copy(template, source[name]));
                }

                payloads[modResource.Blocks.IndexOf(mesh)] = CompiledAddedEffects.Serialize(mesh);
            }
        }

        return CompiledModelEdit.ReplaceBlocks(mod, modResource, payloads);
    }

    private static ModelGaps Compare(Resource modResource, Model modModel, Resource gameResource, Model gameModel)
    {
        var modText = Text(modModel);
        var gameText = Text(gameModel);
        var modKeys = TopKeys(modText);
        var settings = RestorableSettings.Where(key => !modKeys.Contains(key) && TopKeys(gameText).Contains(key) && Block(gameText, key) is not null).ToArray();
        if (modText.Length == 0)
        {
            settings = [];                                                     // no text of its own to add to
        }

        var modMeshes = modResource.Blocks.OfType<Mesh>().Select(CompiledAddedEffects.AttachmentList).OfType<KVObject>().Where(list => list.Count > 0).ToArray();
        var modNames = modMeshes.SelectMany(Names).ToHashSet(StringComparer.Ordinal);
        var bones = modModel.Data["m_modelSkeleton"]["m_boneName"].Values.Select(name => name.ToString(CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
        var restorable = new List<string>();
        var skipped = new List<string>();
        foreach (var (name, entry) in GameAttachments(gameResource).Where(pair => !modNames.Contains(pair.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var influences = Influences(entry);
            if (modMeshes.Length > 0 && influences.Count > 0 && influences.All(bones.Contains) && Fits(modMeshes[0][0], entry))
            {
                restorable.Add(name);
            }
            else
            {
                skipped.Add(name);
            }
        }

        return new ModelGaps(settings, restorable, skipped);
    }

    private static string Text(Model model)
    {
        try
        {
            return CompiledAddedEffects.KeyValueText(model);
        }
        catch (KeyNotFoundException)
        {
            return string.Empty;
        }
    }

    /// <summary>The keys of the text's top-level blocks (a tab, the key, " = ").</summary>
    public static IReadOnlySet<string> TopKeys(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 1 && trimmed[0] == '\t' && trimmed[1] != '\t' && trimmed.IndexOf(" = ", StringComparison.Ordinal) is > 1 and var at)
            {
                keys.Add(trimmed[1..at]);
            }
        }

        return keys;
    }

    /// <summary>One top-level block of the text, from its tab and key to the end of its value (a braced or bracketed value is
    /// matched outside quoted strings); null when the key is missing or the value never closes.</summary>
    public static string? Block(string text, string key)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = -1;
        foreach (var marker in new[] { "\n\t" + key + " = ", "\r\n\t" + key + " = " })
        {
            var at = text.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                start = at + marker.IndexOf('\t', StringComparison.Ordinal);
                break;
            }
        }

        if (start < 0)
        {
            return null;
        }

        var value = start + 1 + key.Length + 3;
        var depth = 0;
        var quoted = false;
        for (var i = value; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                quoted = !(c == '"' && text[i - 1] != '\\');
                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case '{' or '[':
                    depth++;
                    break;
                case '}' or ']':
                    depth--;
                    if (depth == 0)
                    {
                        return text[start..(i + 1)];
                    }

                    if (depth < 0)
                    {
                        return null;
                    }

                    break;
                case '\n' when depth == 0 && text[value..i].Trim().Length > 0:
                    return text[start..i].TrimEnd('\r');                     // a plain value on one line
            }
        }

        return null;
    }

    private static Dictionary<string, KVObject> GameAttachments(Resource game)
    {
        var found = new Dictionary<string, KVObject>(StringComparer.Ordinal);
        foreach (var list in game.Blocks.OfType<Mesh>().Select(CompiledAddedEffects.AttachmentList).OfType<KVObject>())
        {
            foreach (var entry in list.Values)
            {
                found.TryAdd(entry["key"].ToString(CultureInfo.InvariantCulture), entry);
            }
        }

        return found;
    }

    private static HashSet<string> Names(KVObject list) => [.. list.Values.Select(entry => entry["key"].ToString(CultureInfo.InvariantCulture))];

    private static List<string> Influences(KVObject entry)
    {
        var value = entry["value"];
        var count = Math.Min((int)value["m_nInfluences"].ToInt64(CultureInfo.InvariantCulture), value["m_influenceNames"].Count);
        return [.. Enumerable.Range(0, count).Select(i => value["m_influenceNames"][i].ToString(CultureInfo.InvariantCulture)).Where(name => name.Length > 0)];
    }

    private static readonly string[] Copied = ["m_influenceNames", "m_vInfluenceRotations", "m_vInfluenceOffsets", "m_influenceWeights", "m_bInfluenceRootTransform", "m_nInfluences"];

    /// <summary>Whether the game's attachment can be written in the mod's attachment layout (the copied fields are there).</summary>
    private static bool Fits(KVObject template, KVObject entry) =>
        template.ContainsKey("key") && template.ContainsKey("value") && Copied.All(field => template["value"].ContainsKey(field) && entry["value"].ContainsKey(field))
        && template["value"].ContainsKey("m_name");

    /// <summary>The game's attachment in the layout and number types of the mod's own attachments.</summary>
    private static KVObject Copy(KVObject template, KVObject game)
    {
        var entry = CompiledAddedEffects.Clone(template);
        var value = entry["value"];
        var from = game["value"];
        var name = game["key"].ToString(CultureInfo.InvariantCulture);
        entry["key"] = CompiledAddedEffects.Text(entry["key"], name);
        value["m_name"] = CompiledAddedEffects.Text(value["m_name"], name);
        value["m_influenceNames"] = CompiledAddedEffects.Each(value["m_influenceNames"], (old, i) =>
            CompiledAddedEffects.Text(old, i < from["m_influenceNames"].Count ? from["m_influenceNames"][i].ToString(CultureInfo.InvariantCulture) : string.Empty));
        value["m_vInfluenceRotations"] = CompiledAddedEffects.Each(value["m_vInfluenceRotations"], (old, i) => CompiledAddedEffects.Each(old, (number, k) =>
            CompiledAddedEffects.Number(number, i < from["m_vInfluenceRotations"].Count ? CompiledAddedEffects.Float(from["m_vInfluenceRotations"][i][k]) : k == 3 ? 1 : 0)));
        value["m_vInfluenceOffsets"] = CompiledAddedEffects.Each(value["m_vInfluenceOffsets"], (old, i) => CompiledAddedEffects.Each(old, (number, k) =>
            CompiledAddedEffects.Number(number, i < from["m_vInfluenceOffsets"].Count ? CompiledAddedEffects.Float(from["m_vInfluenceOffsets"][i][k]) : 0)));
        value["m_influenceWeights"] = CompiledAddedEffects.Each(value["m_influenceWeights"], (old, i) =>
            CompiledAddedEffects.Number(old, i < from["m_influenceWeights"].Count ? CompiledAddedEffects.Float(from["m_influenceWeights"][i]) : 0));
        value["m_bInfluenceRootTransform"] = CompiledAddedEffects.Each(value["m_bInfluenceRootTransform"], (old, i) =>
            CompiledAddedEffects.Flagged(new KVObject(i < from["m_bInfluenceRootTransform"].Count && from["m_bInfluenceRootTransform"][i].ToBoolean(CultureInfo.InvariantCulture)), old));
        value["m_nInfluences"] = CompiledAddedEffects.Integer(value["m_nInfluences"], (int)from["m_nInfluences"].ToInt64(CultureInfo.InvariantCulture));
        foreach (var flag in new[] { "m_bIgnoreRotation" })
        {
            if (value.ContainsKey(flag) && from.ContainsKey(flag))
            {
                value[flag] = CompiledAddedEffects.Flagged(new KVObject(from[flag].ToBoolean(CultureInfo.InvariantCulture)), value[flag]);
            }
        }

        return entry;
    }
}
