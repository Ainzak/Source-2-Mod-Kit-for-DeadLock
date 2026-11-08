using System.Text.Json;
using System.Text.RegularExpressions;
using SteamDatabase.ValvePak;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Desktop;

/// <summary>One of a hero's four abilities: its slot (1-4), internal id, English name, the animation-graph actions the game
/// triggers for it (<c>m_strAG2CastingAction</c>, <c>m_strAG2CastCompletedAction</c>), the clip found for it in the game's
/// archive (a <c>.vnmclip</c> path without <c>_c</c>; null when none matched) and the phases to play in order (the clip and its
/// siblings: charge, hook, intro, loops, release, end).</summary>
internal sealed record HeroAbility(int Slot, string Id, string Name, IReadOnlyList<string> Actions, string? Clip = null, IReadOnlyList<string>? Phases = null)
{
    /// <summary>What the ability's button plays, in order (empty: no button).</summary>
    public IReadOnlyList<string> PlayList => Phases is { Count: > 0 } phases ? phases : Clip is { } clip ? [clip] : [];
}

/// <summary>
/// The four abilities of each hero model, read from the game's own data: <c>scripts/heroes.vdata</c> (signature slots per hero and
/// its models), <c>scripts/abilities.vdata</c> (the animation-graph actions of each ability), the English hero localization (the
/// ability names) and the hero's clip folders in <c>pak01</c> (a hero's clips can sit in another folder than its current model,
/// e.g. <c>heroes_wip/yamato/clips</c> for <c>heroes_staging/yamato_v2</c>). Cached per game archive like <see cref="GameHeroes"/>.
/// </summary>
internal static partial class HeroAbilities
{
    private const string CacheVersion = "abilities-8";

    // The order of an ability's phases by the words after its name (ability_flyingstrike_shoot, _fly_intro, _fly_loop, _attack).
    private static readonly Dictionary<string, int> PhaseOrder = new(StringComparer.Ordinal)
    {
        ["precast"] = 0,
        ["charge"] = 1,
        ["shoot"] = 2,
        ["toss"] = 2,
        ["intro"] = 3,
        ["start"] = 3,
        ["grab"] = 4,
        ["fly"] = 4,
        ["lift"] = 4,
        ["loop"] = 5,
        ["casting"] = 5,
        ["cast"] = 6,
        ["attack"] = 7,
        ["release"] = 7,
        ["throw"] = 7,
        ["fire"] = 7,
        ["impact"] = 8,
        ["explosion"] = 8,
        ["explode"] = 8,
        ["land"] = 8,
        ["end"] = 9,
        ["exit"] = 9,
        ["outro"] = 9,
    };

    // Clips that are variants of a phase, not phases: in-air, additive, alternative, leaning, directional and idle versions.
    private static readonly HashSet<string> VariantWords = new(StringComparer.Ordinal) { "air", "inair", "add", "alt", "lean", "idle", "run", "walk", "n", "s", "e", "w", "ne", "nw", "se", "sw", "l", "r", "center", "null", "fail", "haspath", "additive", "crouch", "crossbow", "jump", "grenade" };
    private static readonly string[] Suffixes = ["_cast_completed", "_casting", "_completed", "_cast"];
    private static readonly string[] ActionFields = ["m_strAG2CastingAction", "m_strAG2CastCompletedAction"];
    private static readonly string[] Preferred = ["", "cast", "attack", "intro", "start", "throw", "explosion"];
    private static readonly string[] Secondary = ["add", "air", "loop", "lean", "alt", "charge", "end", "exit", "fail", "idle", "walk", "run"];
    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal) { "ability", "citadel", "cast", "casting", "completed", "complete", "the", "and", "with", "hero", "self", "down", "left", "right", "start", "loop", "idle", "intro" };
    private static readonly char[] KeySeparators = [' ', '_', '-', ',', '!', '\'', '.'];

    /// <summary>Abilities by model path (lower case, without <c>_c</c>), or null when the game data cannot be read.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<HeroAbility>>? Read(string game, string cachePath)
    {
        var archive = Path.Combine(game, "citadel", "pak01_dir.vpk");
        var names = Path.Combine(game, "citadel", "resource", "localization", "citadel_heroes", "citadel_heroes_english.txt");
        if (!File.Exists(archive))
        {
            return null;
        }

        var stamp = $"{CacheVersion}-{new FileInfo(archive).Length}-{File.GetLastWriteTimeUtc(archive).Ticks}-{(File.Exists(names) ? File.GetLastWriteTimeUtc(names).Ticks : 0)}";
        try
        {
            if (File.Exists(cachePath) && JsonSerializer.Deserialize<Cache>(File.ReadAllText(cachePath)) is { } cache && cache.Stamp == stamp)
            {
                return cache.Models;
            }
        }
        catch (JsonException)
        {
            // read the game again
        }

        try
        {
            var models = Parse(archive, names);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, JsonSerializer.Serialize(new Cache(stamp, models)));
            return models;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidCastException or KeyNotFoundException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The abilities of the hero whose model is <paramref name="modelPath"/> (empty when unknown).</summary>
    public static IReadOnlyList<HeroAbility> Of(IReadOnlyDictionary<string, IReadOnlyList<HeroAbility>>? models, string modelPath) =>
        models?.GetValueOrDefault(Key(modelPath)) ?? [];

    /// <summary>
    /// The clip of <paramref name="ability"/> among <paramref name="animations"/>. The keys are the ability's actions without
    /// <c>_casting</c> / <c>_cast_completed</c>, its id without <c>citadel_ability_</c> and its English name. Best first:
    /// <list type="number">
    /// <item>the start of the clip's name, squashed (lower case, no underscores, no leading <c>ability</c>), equals a squashed key;</item>
    /// <item>it is a long start of one (<c>shadowtransform</c> for "Shadow Transformation");</item>
    /// <item>it shares words with the keys (<c>ability_groundstrike_legsmash</c> for <c>lash_ground_strike</c>, <c>ability_charge</c>
    /// for <c>bull_charge</c>); more shared words first, and <paramref name="commonWords"/> (the hero's own name in every key) count for nothing;</item>
    /// <item>the clip is exactly an action (the game's generic <c>throw</c>).</item>
    /// </list>
    /// Within a rank the main clip wins (no further words, then <c>cast</c>, <c>attack</c>, <c>intro</c>...; loops, in-air and charge
    /// clips last), <c>ability_</c> clips and <c>.vnmclip</c> files first. Aim poses, additive clips and <paramref name="taken"/> clips
    /// (another ability's) are skipped.
    /// </summary>
    public static string? ClipFor(HeroAbility ability, IEnumerable<string> animations, IReadOnlySet<string>? taken = null, IReadOnlySet<string>? commonWords = null) =>
        Match(ability, animations, taken, commonWords).Best;

    /// <summary>
    /// The phases of <paramref name="ability"/>: when its clip was found by name (<see cref="ClipFor"/> ranks 1 and 2), every clip that
    /// starts with the same name (<c>ability_powerslash_charge</c> beside <c>ability_powerslash_cast</c>), without in-air, additive,
    /// alternative or directional variants, ordered charge / hook / intro / loops / cast / attack / impact / end. Otherwise just the clip.
    /// </summary>
    public static IReadOnlyList<string> PhasesFor(HeroAbility ability, IReadOnlyList<string> animations, IReadOnlySet<string>? taken = null, IReadOnlySet<string>? commonWords = null)
    {
        ArgumentNullException.ThrowIfNull(animations);
        var (best, prefix) = Match(ability, animations, taken, commonWords);
        if (best is null)
        {
            return [];
        }

        if (prefix is null)
        {
            return [best];
        }

        var file = best.Contains('/', StringComparison.Ordinal);
        var phases = animations
            .Where(name => name.Contains('/', StringComparison.Ordinal) == file && taken?.Contains(name) != true && !name.Contains('+', StringComparison.Ordinal))
            .Select(name => (Name: name, Leaf: Leaf(name)))
            .Where(item => item.Leaf == prefix || item.Leaf.StartsWith(prefix + "_", StringComparison.Ordinal))
            .Select(item => (item.Name, Words: item.Leaf.Length > prefix.Length ? item.Leaf[(prefix.Length + 1)..].Split('_', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>()))
            .Where(item => item.Name == best || !item.Words.Any(VariantWords.Contains))
            .GroupBy(item => string.Join('_', item.Words), StringComparer.Ordinal).Select(group => group.First())
            .OrderBy(item => Phase(item.Words)).ThenBy(item => item.Words.Length).ThenBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => item.Name).ToList();
        return phases.Count > 0 ? phases : [best];
    }

    private static double Phase(string[] rest)
    {
        var known = rest.Select(word => PhaseOrder.TryGetValue(word, out var order) ? order : -1).Where(order => order >= 0).ToArray();
        return (known.Length == 0 ? 6 : known.Min()) + (rest.Contains("loop") ? 0.5 : 0);
    }

    private static (string? Best, string? Prefix) Match(HeroAbility ability, IEnumerable<string> animations, IReadOnlySet<string>? taken, IReadOnlySet<string>? commonWords)
    {
        ArgumentNullException.ThrowIfNull(ability);
        ArgumentNullException.ThrowIfNull(animations);
        var sources = KeySources(ability);
        var keys = sources.Select(words => string.Concat(words)).Where(key => key.Length >= 4).Distinct(StringComparer.Ordinal).ToArray();
        var pieces = sources.SelectMany(words => words.Concat(words.Zip(words.Skip(1), (a, b) => a + b)))
            .Where(word => word.Length >= 4 && !GenericWords.Contains(word) && commonWords?.Contains(word) != true).ToHashSet(StringComparer.Ordinal);
        var actions = ability.Actions.Select(action => Squash(action)).ToHashSet(StringComparer.Ordinal);
        string? best = null;
        string? bestPrefix = null;
        var bestScore = int.MaxValue;
        foreach (var name in animations)
        {
            var leaf = Leaf(name);
            if (taken?.Contains(name) == true || leaf.StartsWith("aim_", StringComparison.Ordinal) || leaf.StartsWith("ui_", StringComparison.Ordinal)
                || leaf.Contains("non_additive", StringComparison.Ordinal) || leaf.EndsWith("_add", StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = leaf.Split('_', StringSplitOptions.RemoveEmptyEntries).ToList();
            var abilityClip = tokens.Count > 0 && tokens[0] == "ability";
            if (abilityClip)
            {
                tokens.RemoveAt(0);
            }

            int? rank = null;
            string[] rest = [];
            string? prefix = null;
            for (var take = tokens.Count; take >= 1 && rank is null; take--)
            {
                var head = string.Concat(tokens.Take(take));
                if (keys.Contains(head))
                {
                    (rank, rest) = (0, [.. tokens.Skip(take)]);
                }
                else if (keys.Any(key => key.StartsWith(head, StringComparison.Ordinal) && head.Length >= 8 && head.Length * 10 >= key.Length * 7))
                {
                    (rank, rest) = (1000, [.. tokens.Skip(take)]);
                }

                if (rank is not null)
                {
                    prefix = (abilityClip ? "ability_" : string.Empty) + string.Join('_', tokens.Take(take));
                }
            }

            if (rank is null)
            {
                var clipPieces = tokens.Concat(tokens.Zip(tokens.Skip(1), (a, b) => a + b)).ToArray();
                var shared = clipPieces.Where(pieces.Contains).Distinct(StringComparer.Ordinal).Count();
                if (shared > 0)
                {
                    (rank, rest) = (2000 - (shared * 30), [.. tokens.Where(token => !pieces.Contains(token))]);
                }
                else if (actions.Contains(string.Concat(tokens)))
                {
                    rank = 3000;
                }
            }

            if (rank is not { } found)
            {
                continue;
            }

            var score = found + (abilityClip ? 0 : 60) + (name.Contains('/', StringComparison.Ordinal) ? 0 : 100) + Rank(rest);
            if (score < bestScore || (score == bestScore && best is not null && name.Length < best.Length))
            {
                (best, bestScore, bestPrefix) = (name, score, prefix);
            }
        }

        return (best, bestPrefix);
    }

    /// <summary>Words that appear in the keys of two or more of a hero's abilities (the hero's name, a shared prefix).</summary>
    internal static HashSet<string> CommonWords(IEnumerable<HeroAbility> abilities) =>
        abilities.SelectMany(ability => KeySources(ability).SelectMany(words => words).Distinct(StringComparer.Ordinal))
            .GroupBy(word => word, StringComparer.Ordinal).Where(group => group.Count() >= 2).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>The words of each key: actions (without endings), the id (without prefixes) and the English name.</summary>
    private static string[][] KeySources(HeroAbility ability) =>
        [.. ability.Actions.Select(Stem).Append(IdStem(ability.Id)).Append(ability.Name)
            .Select(text => text.ToLowerInvariant().Split(KeySeparators, StringSplitOptions.RemoveEmptyEntries).Where(word => word != "ability").ToArray())
            .Where(words => words.Length > 0)];

    internal static string Stem(string action)
    {
        var stem = action.Trim().ToLowerInvariant();
        foreach (var suffix in Suffixes)
        {
            if (stem.EndsWith(suffix, StringComparison.Ordinal))
            {
                return stem[..^suffix.Length];
            }
        }

        return stem;
    }

    private static int Rank(string[] rest)
    {
        var joined = string.Join('_', rest);
        var preferred = Array.IndexOf(Preferred, joined);
        var word = rest.Select(item => Array.IndexOf(Preferred, item)).Where(index => index > 0).DefaultIfEmpty(-1).Min();
        return preferred >= 0 ? preferred
            : (word > 0 ? 5 + word : 10 + joined.Length) + (rest.Any(item => Secondary.Contains(item)) ? 20 : 0);
    }

    private static string IdStem(string id)
    {
        var stem = id.ToLowerInvariant();
        foreach (var prefix in new[] { "citadel_ability_", "ability_" })
        {
            if (stem.StartsWith(prefix, StringComparison.Ordinal))
            {
                return stem[prefix.Length..];
            }
        }

        return stem;
    }

    private static string Squash(string text)
    {
        var squashed = new string([.. text.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit)]);
        return squashed.StartsWith("ability", StringComparison.Ordinal) ? squashed["ability".Length..] : squashed;
    }

    private static string Leaf(string name)
    {
        var leaf = name.Replace('\\', '/');
        leaf = leaf[(leaf.LastIndexOf('/') + 1)..].ToLowerInvariant();
        leaf = leaf.EndsWith("_c", StringComparison.Ordinal) ? leaf[..^2] : leaf;
        return leaf.EndsWith(".vnmclip", StringComparison.Ordinal) ? leaf[..^".vnmclip".Length] : leaf;
    }

    private static string Key(string modelPath)
    {
        var key = modelPath.Replace('\\', '/').ToLowerInvariant();
        return key.EndsWith("_c", StringComparison.Ordinal) ? key[..^2] : key;
    }

    private static string Folder(string path) => path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : string.Empty;

    private static Dictionary<string, IReadOnlyList<HeroAbility>> Parse(string archive, string namesPath)
    {
        using var package = new Package();
        package.Read(archive);
        var heroes = ReadData(package, "scripts/heroes.vdata_c");
        var abilities = ReadData(package, "scripts/abilities.vdata_c");
        var localized = File.Exists(namesPath)
            ? Localized().Matches(File.ReadAllText(namesPath)).GroupBy(match => match.Groups[1].Value, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Groups[2].Value, StringComparer.Ordinal)
            : [];
        // Hero clips by hero folder name without its version (heroes_staging/lash_v2/clips and heroes_wip/lash both count as "lash").
        var clipsByHero = (package.Entries?.GetValueOrDefault("vnmclip_c") ?? [])
            .Select(entry => entry.GetFullPath().Replace('\\', '/').ToLowerInvariant()).Select(path => (Path: path[..^2], Parts: path.Split('/')))
            .Where(item => item.Parts.Length > 3 && item.Parts[0] == "models" && item.Parts[1] is "heroes_wip" or "heroes_staging")
            .GroupBy(item => Hero.StripVersion(item.Parts[2]), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Select(item => item.Path).ToArray(), StringComparer.Ordinal);
        var clipsByGraph = GraphClips(package);
        var models = new Dictionary<string, IReadOnlyList<HeroAbility>>(StringComparer.Ordinal);
        foreach (var key in heroes.Keys.Where(key => key.StartsWith("hero_", StringComparison.Ordinal)))
        {
            var hero = heroes[key];
            if (!hero.TryGetValue("m_mapBoundAbilities", out var boundValue) || boundValue is not KVObject bound)
            {
                continue;
            }

            var heroModels = new[] { Text(hero, "m_strModelName"), Text(hero, "m_strWIPModelName") }.Where(model => model.Length > 0).Select(Key).ToArray();
            var id = key["hero_".Length..];
            var names = heroModels.Select(model => model.Split('/')).Where(parts => parts.Length > 3).Select(parts => Hero.StripVersion(parts[2])).Append(id).Distinct(StringComparer.Ordinal).ToArray();
            var folder = names.SelectMany(name => clipsByHero.GetValueOrDefault(name) ?? []).Distinct(StringComparer.Ordinal).ToArray();
            var used = names.SelectMany(name => clipsByGraph.GetValueOrDefault(name) ?? []).ToHashSet(StringComparer.Ordinal);
            var clips = used.Count > 0 ? folder.Where(used.Contains).ToArray() : folder;   // only what the hero's current graphs play
            var found = new List<HeroAbility>();
            for (var slot = 1; slot <= 4; slot++)
            {
                if (!bound.TryGetValue($"ESlot_Signature_{slot}", out var idValue) || idValue.IsNull || idValue.ToString(null) is not { Length: > 0 } abilityId)
                {
                    continue;
                }

                var actions = abilities.TryGetValue(abilityId, out var abilityValue) && abilityValue is KVObject ability
                    ? ActionFields.Select(field => Text(ability, field)).Where(text => text.Length > 0).ToArray()
                    : [];
                found.Add(new HeroAbility(slot, abilityId, localized.GetValueOrDefault(abilityId) ?? abilityId, actions));
            }

            var common = CommonWords(found);                                // e.g. "lash" in every Lash ability names nothing
            var taken = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<HeroAbility>();
            foreach (var entry in found)
            {
                var phases = PhasesFor(entry, clips, taken, common);
                var clip = ClipFor(entry, clips, taken, common);
                taken.UnionWith(phases);
                list.Add(entry with { Clip = clip, Phases = phases });
            }

            foreach (var model in heroModels)
            {
                models.TryAdd(model, list);
            }
        }

        return models;
    }

    /// <summary>
    /// The clips each hero's current animation graphs use, by hero name: the graph variations <c>hero_x.vnmgraph+yamato.vnmgraph</c>
    /// and the hero's own graphs <c>animgraphs/animgraph2/hero/yamato/...</c>, read from their resource references. Old clips that
    /// are still in the archive but no longer played (Yamato's <c>ability_shadowtransform_intro</c>) are not in any of them.
    /// </summary>
    private static Dictionary<string, HashSet<string>> GraphClips(Package package)
    {
        var byHero = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var entry in package.Entries?.GetValueOrDefault("vnmgraph_c") ?? [])
        {
            var path = entry.GetFullPath().Replace('\\', '/').ToLowerInvariant();
            var file = path[(path.LastIndexOf('/') + 1)..];
            var parts = path.Split('/');
            var owner = file.Contains('+', StringComparison.Ordinal) ? file[(file.LastIndexOf('+') + 1)..].Replace(".vnmgraph_c", string.Empty, StringComparison.Ordinal)
                : parts.Length > 4 && parts[0] == "animgraphs" && parts[2] == "hero" ? parts[3] : null;
            if (owner is null)
            {
                continue;
            }

            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            var clips = resource.ExternalReferences?.ResourceRefInfoList.Select(info => info.Name.Replace('\\', '/').ToLowerInvariant())
                .Where(name => name.EndsWith(".vnmclip", StringComparison.Ordinal)) ?? [];
            if (!byHero.TryGetValue(Hero.StripVersion(owner), out var set))
            {
                byHero[Hero.StripVersion(owner)] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.UnionWith(clips);
        }

        return byHero;
    }

    private static KVObject ReadData(Package package, string path)
    {
        var entry = package.FindEntry(path) ?? throw new InvalidDataException($"{path} is missing.");
        package.ReadEntry(entry, out var bytes);
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        return resource.DataBlock switch
        {
            BinaryKV3 kv3 => kv3.Data.Root,
            KeyValuesOrNTRO kv => kv.Data,
            _ => throw new InvalidDataException($"{path} has no key-value data."),
        };
    }

    private static string Text(KVObject data, string key) => data.TryGetValue(key, out var value) && !value.IsNull ? value.ToString(null) : string.Empty;

    private sealed record Cache(string Stamp, Dictionary<string, IReadOnlyList<HeroAbility>> Models);

    // "<ability id>"   "<English name>" (the first entry of an id is its name; later ones are descriptions with suffixes)
    [GeneratedRegex("^\\s*\"((?:citadel_)?ability_[a-z0-9_]+)\"\\s+\"([^\"]*)\"", RegexOptions.Multiline)]
    private static partial Regex Localized();
}
