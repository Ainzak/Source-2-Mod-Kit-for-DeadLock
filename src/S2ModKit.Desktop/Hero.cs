using System.Text.Json;
using System.Text.RegularExpressions;

namespace S2ModKit.Desktop;

/// <summary>A hero from the shipped catalogue (catalogues/deadlock-current.json): display name, folder names, main model.</summary>
internal sealed partial record Hero(string Name, IReadOnlyList<string> Folders, string Model)
{
    public static IReadOnlyList<Hero> Catalogue { get; } = Load(Path.Combine(AppContext.BaseDirectory, "catalogues", "deadlock-current.json"));

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

    [GeneratedRegex(@"_v\d+$")]
    private static partial Regex VersionSuffix();
}
