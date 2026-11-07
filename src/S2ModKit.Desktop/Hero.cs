using System.Text.Json;
using System.Text.RegularExpressions;

namespace S2ModKit.Desktop;

/// <summary>
/// A hero: display name, folder names (internal id, aliases, model folders), main model. The list comes from the game's own
/// hero data when it can be read (<see cref="GameHeroes"/>, current names and every hero), else from the shipped catalogue
/// (catalogues/deadlock-current.json).
/// </summary>
internal sealed partial record Hero(string Name, IReadOnlyList<string> Folders, string Model)
{
    public static IReadOnlyList<Hero> Shipped { get; } = Load(Path.Combine(AppContext.BaseDirectory, "catalogues", "deadlock-current.json"));

    public static IReadOnlyList<Hero> Catalogue { get; private set; } = Shipped;

    private static IReadOnlyDictionary<string, Hero> byFolder = Index(Shipped);     // after Shipped: static initializers run in order

    /// <summary>Switches to the heroes of this game folder (read once per game build, cached under <paramref name="dataRoot"/>);
    /// keeps the current list when the game data cannot be read.</summary>
    public static void UseGame(string game, string dataRoot)
    {
        if (GameHeroes.Read(game, Path.Combine(dataRoot, "heroes-cache.json"), Shipped) is { Count: > 0 } heroes && !ReferenceEquals(heroes, Catalogue))
        {
            (Catalogue, byFolder) = (heroes, Index(heroes));
        }
    }

    /// <summary>The hero whose internal id, alias or model folder is exactly <paramref name="folder"/> (case and a _v2-style suffix ignored), if any.</summary>
    public static Hero? Find(string folder) => byFolder.GetValueOrDefault(StripVersion(folder));

    /// <summary>The hero with this display name (Mod Manager's hero field), if any.</summary>
    public static Hero? FindByName(string? name) => Catalogue.FirstOrDefault(hero => Bare(hero.Name).Equals(Bare(name ?? string.Empty), StringComparison.OrdinalIgnoreCase));

    /// <summary>A display name without a leading "The " (the game says "The Doorman", Mod Manager "Doorman").</summary>
    private static string Bare(string name) => name.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;

    /// <summary>A model folder without a version suffix (hornet_v3 -> hornet).</summary>
    public static string StripVersion(string folder) => VersionSuffix().Replace(folder, string.Empty);

    public static Hero[] Load(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.GetProperty("heroes").EnumerateArray().Select(Read).Where(hero => hero.Model.Length > 0)];
    }

    private static Hero Read(JsonElement hero)
    {
        var model = hero.GetProperty("resources").EnumerateArray().Select(resource => resource.GetProperty("logicalPath").GetString() ?? string.Empty)
            .FirstOrDefault(path => path.EndsWith(".vmdl_c", StringComparison.Ordinal)) ?? string.Empty;
        var folders = new List<string> { hero.GetProperty("heroId").GetString() ?? string.Empty };
        folders.AddRange(hero.GetProperty("aliases").EnumerateArray().Select(alias => alias.GetString() ?? string.Empty));
        var parts = model.Split('/');
        if (parts.Length > 3)
        {
            folders.Add(StripVersion(parts[2]));
        }

        return new Hero(hero.GetProperty("displayName").GetString() ?? string.Empty, folders, model.EndsWith("_c", StringComparison.Ordinal) ? model[..^2] : model);
    }

    private static Dictionary<string, Hero> Index(IReadOnlyList<Hero> heroes) =>
        heroes.SelectMany(hero => hero.Folders.Where(folder => folder.Length > 0).Select(folder => (folder, hero)))
            .GroupBy(pair => pair.folder, StringComparer.OrdinalIgnoreCase).Where(group => group.Select(pair => pair.hero).Distinct().Count() == 1)   // an ambiguous folder names no hero
            .ToDictionary(group => group.Key, group => group.First().hero, StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"_v\d+$")]
    private static partial Regex VersionSuffix();
}
