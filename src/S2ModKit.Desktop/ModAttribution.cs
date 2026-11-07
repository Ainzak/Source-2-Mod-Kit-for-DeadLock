using S2ModKit.Application;

namespace S2ModKit.Desktop;

/// <summary>
/// Which heroes a mod changes and what it changes for each, read from its entry paths, and where a mod without a hero
/// belongs. A folder names a hero only when it equals a catalogue id, alias or main-model folder exactly (case and a
/// _v2 suffix ignored); a folder such as <c>bebopish</c> or <c>bebop_extra</c> names nobody. Only the file name stems
/// of <c>soundevents/hero</c> and <c>panorama/images/heroes</c> may carry <c>_</c> suffixes (<c>bebop_card</c>).
/// </summary>
internal static class ModAttribution
{
    public const string Model = "model";
    public const string Sound = "sound";
    public const string Ui = "UI";
    public const string Particles = "particles";
    public const string Animation = "animation";

    public const string Objects = "Objects";
    public const string World = "World";
    public const string Interface = "Interface";
    public const string Other = "Other";

    /// <summary>Tag order in the list and the order of the groups after the heroes.</summary>
    public static IReadOnlyList<string> Tags { get; } = [Model, Animation, Sound, Ui, Particles];

    public static IReadOnlyList<string> Groups { get; } = [Objects, World, Interface, Other];

    /// <summary>Hero (catalogue entry) to the kinds of change the entry paths make for it, in tag order.</summary>
    public static IReadOnlyDictionary<Hero, IReadOnlyList<string>> HeroKinds(IEnumerable<string> entries)
    {
        var found = new Dictionary<Hero, HashSet<string>>();
        foreach (var entry in entries)
        {
            var parts = entry.Replace('\\', '/').ToLowerInvariant().Split('/');
            if (HeroOf(parts) is { } use)
            {
                var (hero, tag) = use;
                if (!found.TryGetValue(hero, out var tags))
                {
                    found[hero] = tags = [];
                }

                tags.Add(tag);
            }
        }

        return found.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)[.. Tags.Where(pair.Value.Contains)]);
    }

    /// <summary>
    /// Heroes a mod touches only in passing: at most 3 of its files, and at most a tenth of the files of the hero it changes
    /// most (Mina Kurumi Tokisaki ships one prop under Nurse Harrow's folder). Such a hero is named in the details, not listed.
    /// </summary>
    public static IReadOnlySet<Hero> StrayHeroes(IEnumerable<string> entries)
    {
        var counts = entries.Select(entry => HeroOf(entry.Replace('\\', '/').ToLowerInvariant().Split('/'))?.Hero).OfType<Hero>()
            .GroupBy(hero => hero).ToDictionary(group => group.Key, group => group.Count());
        var most = counts.Values.DefaultIfEmpty(0).Max();
        return counts.Where(pair => pair.Value <= 3 && pair.Value * 10 <= most).Select(pair => pair.Key).ToHashSet();
    }

    /// <summary>
    /// Group of a mod without a hero: the group most of its entries belong to (a tie goes to the earlier of Objects, World,
    /// Interface, Other). Objects: <c>models/</c>, <c>particles/</c>, <c>materials/</c>; Interface: <c>panorama/</c>,
    /// <c>maps/ui/</c>, <c>resource/</c>; World: other <c>maps/</c>, <c>sounds/ambient*</c>, <c>soundevents/world_*</c>;
    /// everything else (also an unknown <c>models/heroes*</c> folder) is Other.
    /// </summary>
    public static string GroupOf(IEnumerable<string> entries) =>
        entries.GroupBy(GroupOfEntry).OrderByDescending(group => group.Count()).ThenBy(group => Groups.ToList().IndexOf(group.Key)).Select(group => group.Key).FirstOrDefault() ?? Other;

    private static string GroupOfEntry(string entry)
    {
        var path = entry.Replace('\\', '/').ToLowerInvariant();
        return path switch
        {
            _ when path.StartsWith("models/heroes", StringComparison.Ordinal) => Other,
            _ when path.StartsWith("models/", StringComparison.Ordinal) || path.StartsWith("particles/", StringComparison.Ordinal)
                || path.StartsWith("materials/", StringComparison.Ordinal) => Objects,
            _ when path.StartsWith("maps/ui/", StringComparison.Ordinal) || path.StartsWith("panorama/", StringComparison.Ordinal)
                || path.StartsWith("resource/", StringComparison.Ordinal) => Interface,
            _ when path.StartsWith("maps/", StringComparison.Ordinal) || path.StartsWith("sounds/ambient", StringComparison.Ordinal)
                || path.StartsWith("soundevents/world_", StringComparison.Ordinal) => World,
            _ => Other,
        };
    }

    private static (Hero Hero, string Tag)? HeroOf(string[] parts)
    {
        string? tag = null;
        Hero? hero = null;
        if (parts.Length > 3 && parts[0] == "models" && parts[1].StartsWith("heroes", StringComparison.Ordinal))
        {
            // materials and textures below the folder count as model; clips, graphs and sequences as animation
            (hero, tag) = (Hero.Find(parts[2]), AddonResourceKinds.Classify(string.Join('/', parts)) == "animation" ? Animation : Model);
        }
        else if (parts.Length > 4 && parts[0] == "animgraphs" && parts.Contains("hero"))
        {
            (hero, tag) = (Hero.Find(parts[Array.IndexOf(parts, "hero") + 1]), Animation);   // animgraphs/animgraph2/hero/<hero>/...
        }
        else if (parts.Length > 3 && parts[0] == "sounds" && parts[1] is "abilities" or "vo" or "heroes")
        {
            (hero, tag) = (Hero.Find(parts[2]), Sound);
        }
        else if (parts.Length == 3 && parts[0] == "soundevents" && parts[1] == "hero")
        {
            (hero, tag) = (FromStem(parts[2]), Sound);
        }
        else if (parts.Length == 4 && parts[0] == "maps" && parts[1] == "ui" && parts[2] == "hero_prefabs")
        {
            (hero, tag) = (Hero.Find(Stem(parts[3])), Ui);
        }
        else if (parts.Length > 3 && parts[0] == "panorama" && parts[1] == "images" && parts[2] == "heroes")
        {
            (hero, tag) = (parts[3..^1].Select(Hero.Find).FirstOrDefault(found => found is not null) ?? FromStem(parts[^1]), Ui);
        }
        else if (parts.Length > 3 && parts[0] == "particles" && parts[1] == "abilities")
        {
            (hero, tag) = (Hero.Find(parts[2]), Particles);
        }
        else if (parts.Length > 4 && parts[0] == "materials" && parts[1] == "particle" && parts[2] == "abilities")
        {
            (hero, tag) = (Hero.Find(parts[3]), Particles);
        }

        return hero is null || tag is null ? null : (hero, tag);
    }

    /// <summary>The file name up to its first dot (necro.vpk -> necro).</summary>
    private static string Stem(string fileName) => fileName.Split('.')[0];

    /// <summary>The hero a file name stem names, ignoring trailing _suffixes: bebop_card_psd -> bebop_card -> bebop.</summary>
    private static Hero? FromStem(string fileName)
    {
        var stem = Stem(fileName);
        while (stem.Length > 0)
        {
            if (Hero.Find(stem) is { } hero)
            {
                return hero;
            }

            var cut = stem.LastIndexOf('_');
            stem = cut < 0 ? string.Empty : stem[..cut];
        }

        return null;
    }
}
