using System.Text.Json;
using System.Text.RegularExpressions;
using SteamDatabase.ValvePak;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Desktop;

/// <summary>
/// The heroes of the installed game, read from its own data the way the game lists them: <c>scripts/heroes.vdata</c> in
/// <c>citadel/pak01_dir.vpk</c> (internal id, model) and the English hero-name localization. A hero counts when it is not
/// disabled and has its own model (placeholders use <c>gen_man</c>); in-development heroes such as Violet are included.
/// Folders are the internal id, its model folders and the shipped catalogue's aliases for the same hero. The result is
/// cached per game archive (size and write time), so it is read again after a game update.
/// </summary>
internal static partial class GameHeroes
{
    private const string Placeholder = "models/heroes_wip/gen_man/";

    private static readonly HashSet<string> NotHeroes = new(StringComparer.Ordinal) { "base", "targetdummy", "genericperson", "testhero" };

    /// <summary>The game's heroes, or null when its data cannot be read (no game, layout changed).</summary>
    public static IReadOnlyList<Hero>? Read(string game, string cachePath, IReadOnlyList<Hero> shipped)
    {
        var archive = Path.Combine(game, "citadel", "pak01_dir.vpk");
        var names = Path.Combine(game, "citadel", "resource", "localization", "citadel_gc_hero_names", "citadel_gc_hero_names_english.txt");
        if (!File.Exists(archive) || !File.Exists(names))
        {
            return null;
        }

        var stamp = $"{new FileInfo(archive).Length}-{File.GetLastWriteTimeUtc(archive).Ticks}-{File.GetLastWriteTimeUtc(names).Ticks}";
        if (ReadCache(cachePath, stamp) is { } cached)
        {
            return cached;
        }

        try
        {
            var heroes = Parse(archive, names, shipped);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, JsonSerializer.Serialize(new Cache(stamp, [.. heroes.Select(hero => new CachedHero(hero.Name, [.. hero.Folders], hero.Model))])));
            return heroes;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidCastException or KeyNotFoundException or NotSupportedException)
        {
            return null;                                                     // the shipped catalogue stays in use
        }
    }

    private static List<Hero> Parse(string archive, string namesPath, IReadOnlyList<Hero> shipped)
    {
        using var package = new Package();
        package.Read(archive);
        var entry = package.FindEntry("scripts/heroes.vdata_c") ?? throw new InvalidDataException("scripts/heroes.vdata_c is missing.");
        package.ReadEntry(entry, out var bytes);
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        var root = resource.DataBlock switch
        {
            BinaryKV3 kv3 => kv3.Data.Root,
            KeyValuesOrNTRO kv => kv.Data,
            _ => throw new InvalidDataException("scripts/heroes.vdata_c has no key-value data."),
        };
        var localized = HeroName().Matches(File.ReadAllText(namesPath)).ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value, StringComparer.Ordinal);

        var heroes = new List<Hero>();
        foreach (var key in root.Keys.Where(key => key.StartsWith("hero_", StringComparison.Ordinal)))
        {
            var id = key["hero_".Length..];
            var data = root[key];
            var model = Text(data, "m_strModelName");
            if (NotHeroes.Contains(id) || Flag(data, "m_bDisabled") || model.Length == 0 || model.StartsWith(Placeholder, StringComparison.OrdinalIgnoreCase)
                || !localized.TryGetValue(id, out var name))
            {
                continue;
            }

            var folders = new List<string> { id };
            folders.AddRange(new[] { model, Text(data, "m_strWIPModelName") }.Select(path => path.Split('/')).Where(parts => parts.Length > 3).Select(parts => Hero.StripVersion(parts[2])));
            folders.AddRange(shipped.Where(known => known.Folders.Intersect(folders, StringComparer.OrdinalIgnoreCase).Any()).SelectMany(known => known.Folders));
            heroes.Add(new Hero(name, [.. folders.Where(folder => folder.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)], model));
        }

        return [.. heroes.OrderBy(hero => hero.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static string Text(KVObject data, string key) => data.TryGetValue(key, out var value) && !value.IsNull ? value.ToString(null) : string.Empty;

    private static bool Flag(KVObject data, string key) => data.TryGetValue(key, out var value) && !value.IsNull && value.ToBoolean(null);

    private static List<Hero>? ReadCache(string path, string stamp)
    {
        try
        {
            var cache = File.Exists(path) ? JsonSerializer.Deserialize<Cache>(File.ReadAllText(path)) : null;
            return cache?.Stamp == stamp ? [.. cache.Heroes.Select(hero => new Hero(hero.Name, hero.Folders, hero.Model))] : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Cache(string Stamp, CachedHero[] Heroes);

    private sealed record CachedHero(string Name, string[] Folders, string Model);

    // "hero_<id>:n"   "<display name>"
    [GeneratedRegex("\"hero_([a-z0-9_]+):n\"\\s+\"([^\"]*)\"")]
    private static partial Regex HeroName();
}
