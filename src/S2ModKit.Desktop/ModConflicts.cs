using S2ModKit.Application;

namespace S2ModKit.Desktop;

/// <summary>One path two enabled mods both ship, and whether the mod it is listed for wins it (the lower pak slot wins).</summary>
internal sealed record SharedPath(string Path, string Kind, bool Wins);

/// <summary>What one enabled mod shares with one other enabled mod. <see cref="Notes"/> name shared interface files that decide
/// whether either mod works (the HUD layout).</summary>
internal sealed record ConflictWith(string OtherKey, string OtherName, IReadOnlyList<SharedPath> Paths, IReadOnlyList<string> Notes)
{
    public int Wins => Paths.Count(path => path.Wins);

    public int Loses => Paths.Count - Wins;

    /// <summary>Shared paths per kind (in <see cref="ModConflictAnalysis.KindOrder"/>) with the number won and lost.</summary>
    public IReadOnlyList<(string Kind, int Wins, int Loses)> ByKind =>
        [.. Paths.GroupBy(path => path.Kind).OrderBy(group => ModConflictAnalysis.KindRank(group.Key)).Select(group => (group.Key, group.Count(path => path.Wins), group.Count(path => !path.Wins)))];
}

internal sealed record ModConflicts(IReadOnlyList<ConflictWith> With, IReadOnlyList<string> ModelWarnings)
{
    public static ModConflicts None { get; } = new([], []);

    public int Wins => With.Sum(item => item.Wins);

    public int Loses => With.Sum(item => item.Loses);

    /// <summary>List badges: "conflict" when the mod loses a shared path, "wins" when it only wins, "two models" for a model overlap.</summary>
    public IReadOnlyList<string> Badges =>
    [
        .. Loses > 0 ? (string[])["conflict"] : Wins > 0 ? ["wins"] : [],
        .. ModelWarnings.Count > 0 ? (string[])["two models"] : [],
    ];
}

/// <summary>
/// Conflicts among the enabled mods. Deadlock loads <c>pakNN_dir.vpk</c> and, when two archives hold the same path, the lower slot
/// wins that path; a mod spread over several archives wins a path by its lowest slot that holds it (<see cref="ModEntry.PathSlots"/>).
/// Disabled mods are not compared. Junk that every mod may carry is ignored (see <see cref="IsIgnored"/>).
/// </summary>
internal static class ModConflictAnalysis
{
    public static IReadOnlyList<string> KindOrder { get; } = ["model", "animation", "material", "sound", "ui", "particles", "localization", "data", "other"];

    public static int KindRank(string kind) => KindOrder.ToList().IndexOf(kind) is var rank and >= 0 ? rank : KindOrder.Count;

    /// <summary>Paths that never make two mods conflict: files in the archive root (README.txt, addon info and images), text files
    /// outside <c>resource/localization/</c>, and cache or note files (.soc, .md, .url, .nfo, .log, .bak).</summary>
    public static bool IsIgnored(string path)
    {
        var normal = Normalize(path);
        if (normal.Length == 0 || !normal.Contains('/', StringComparison.Ordinal))
        {
            return true;
        }

        var extension = System.IO.Path.GetExtension(normal);
        return extension is ".soc" or ".md" or ".url" or ".nfo" or ".log" or ".bak"
            || (extension == ".txt" && !normal.StartsWith("resource/localization/", StringComparison.Ordinal));
    }

    public static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    public static IReadOnlyDictionary<string, ModConflicts> Analyze(IReadOnlyList<ModEntry> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);
        var enabled = mods.Where(mod => mod.Loaded && mod.PathSlots is not null).ToArray();
        var owners = new Dictionary<string, List<(int Mod, int Slot)>>(StringComparer.Ordinal);
        for (var i = 0; i < enabled.Length; i++)
        {
            foreach (var (path, slot) in enabled[i].PathSlots!)
            {
                if (IsIgnored(path))
                {
                    continue;
                }

                if (!owners.TryGetValue(path, out var list))
                {
                    owners[path] = list = [];
                }

                list.Add((i, slot));
            }
        }

        var shared = new Dictionary<(int, int), List<(string Path, int SlotA, int SlotB)>>();
        foreach (var (path, list) in owners.Where(pair => pair.Value.Count > 1))
        {
            for (var a = 0; a < list.Count; a++)
            {
                for (var b = a + 1; b < list.Count; b++)
                {
                    var (first, second) = list[a].Mod < list[b].Mod ? (list[a], list[b]) : (list[b], list[a]);
                    if (!shared.TryGetValue((first.Mod, second.Mod), out var pair))
                    {
                        shared[(first.Mod, second.Mod)] = pair = [];
                    }

                    pair.Add((path, first.Slot, second.Slot));
                }
            }
        }

        var perMod = enabled.Select(_ => new List<ConflictWith>()).ToArray();
        foreach (var ((a, b), paths) in shared)
        {
            var ordered = paths.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
            perMod[a].Add(Make(enabled[b], enabled[a], [.. ordered.Select(item => (item.Path, Wins: item.SlotA < item.SlotB))]));
            perMod[b].Add(Make(enabled[a], enabled[b], [.. ordered.Select(item => (item.Path, Wins: item.SlotB < item.SlotA))]));
        }

        var warnings = ModelWarnings(enabled);
        var result = new Dictionary<string, ModConflicts>(StringComparer.Ordinal);
        for (var i = 0; i < enabled.Length; i++)
        {
            var conflicts = new ModConflicts([.. perMod[i].OrderBy(item => item.OtherName, StringComparer.OrdinalIgnoreCase)], warnings.GetValueOrDefault(enabled[i].Key) ?? []);
            if (conflicts.With.Count > 0 || conflicts.ModelWarnings.Count > 0)
            {
                result[enabled[i].Key] = conflicts;
            }
        }

        return result;
    }

    /// <summary>
    /// What enabling the disabled <paramref name="mod"/> would clash with, before it is enabled: one line per enabled mod that
    /// ships the same game files (count and kinds; which one wins depends on the slot it gets) and per hero that already has an
    /// enabled model mod. Empty when nothing clashes.
    /// </summary>
    public static IReadOnlyList<string> WouldConflict(ModEntry mod, IReadOnlyList<ModEntry> mods)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(mods);
        var own = mod.Entries.Where(path => !IsIgnored(path)).Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var other in mods.Where(other => other.Loaded && other.Key != mod.Key).OrderBy(other => other.Name, StringComparer.OrdinalIgnoreCase))
        {
            var shared = (other.PathSlots?.Keys ?? other.Entries).Select(Normalize).Where(own.Contains).Distinct(StringComparer.Ordinal).ToArray();
            if (shared.Length > 0)
            {
                var kinds = shared.GroupBy(AddonResourceKinds.Classify).OrderBy(group => KindRank(group.Key)).Select(group => $"{group.Count()} {group.Key}");
                lines.Add($"'{other.Name}' replaces the same {shared.Length} file(s) ({string.Join(", ", kinds)}): only one of the two is used for them.");
            }
        }

        static IEnumerable<string> ModelHeroes(ModEntry entry) =>
            (entry.HeroKinds ?? new Dictionary<string, IReadOnlyList<string>>()).Where(pair => pair.Value.Contains(ModAttribution.Model)).Select(pair => pair.Key);
        foreach (var hero in ModelHeroes(mod).Order(StringComparer.OrdinalIgnoreCase))
        {
            var holders = mods.Where(other => other.Loaded && other.Key != mod.Key && ModelHeroes(other).Contains(hero, StringComparer.OrdinalIgnoreCase)).Select(other => $"'{other.Name}'").ToArray();
            if (holders.Length > 0)
            {
                lines.Add($"{hero} already has an enabled model mod ({string.Join(", ", holders)}): only one model shows where they overlap.");
            }
        }

        return lines;
    }

    private static ConflictWith Make(ModEntry other, ModEntry self, (string Path, bool Wins)[] paths) =>
        new(other.Key, other.Name, [.. paths.Select(item => new SharedPath(item.Path, AddonResourceKinds.Classify(item.Path), item.Wins))], HudNotes(self, other, paths));

    /// <summary>"Both replace the HUD layout (base_hud): only X works" for a shared <c>panorama/layout/*.vxml_c</c> or
    /// <c>panorama/scripts/*</c> file: the panorama file the game uses is that of the winner alone.</summary>
    private static List<string> HudNotes(ModEntry self, ModEntry other, (string Path, bool Wins)[] paths)
    {
        var notes = new List<string>();
        string Winner(bool wins) => wins ? self.Name : other.Name;
        var hud = paths.Where(item => item.Path == "panorama/layout/base_hud.vxml_c").ToArray();
        foreach (var item in hud)
        {
            notes.Add($"Both replace the HUD layout (base_hud): only {Winner(item.Wins)} works.");
        }

        var rest = paths.Except(hud).Where(item => (item.Path.StartsWith("panorama/layout/", StringComparison.Ordinal) && item.Path.EndsWith(".vxml_c", StringComparison.Ordinal))
            || item.Path.StartsWith("panorama/scripts/", StringComparison.Ordinal)).ToArray();
        foreach (var group in rest.GroupBy(item => item.Wins))
        {
            var names = group.Select(item => System.IO.Path.GetFileNameWithoutExtension(item.Path)).Take(4).ToArray();
            notes.Add($"Both replace {group.Count()} interface layout or script file(s) ({string.Join(", ", names)}{(group.Count() > names.Length ? ", ..." : string.Empty)}): only {Winner(group.Key)} works for them.");
        }

        return notes;
    }

    /// <summary>Per mod key: a warning for each hero that this and another enabled mod both change with a model; only the mod with the
    /// lower slot shows where the two overlap.</summary>
    private static Dictionary<string, IReadOnlyList<string>> ModelWarnings(ModEntry[] enabled)
    {
        static int FirstSlot(ModEntry mod) => mod.Slots.Where(slot => slot is not null).Select(slot => slot!.Value).DefaultIfEmpty(int.MaxValue).Min();
        var warnings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var heroes = enabled.SelectMany(mod => (mod.HeroKinds ?? new Dictionary<string, IReadOnlyList<string>>()).Where(pair => pair.Value.Contains(ModAttribution.Model)).Select(pair => pair.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var hero in heroes)
        {
            var holders = enabled.Where(mod => mod.HeroKinds is { } kinds && kinds.TryGetValue(hero, out var tags) && tags.Contains(ModAttribution.Model))
                .OrderBy(FirstSlot).ThenBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (holders.Length < 2)
            {
                continue;
            }

            var names = string.Join(" and ", holders.Select(mod => $"'{mod.Name}'"));
            var count = holders.Length == 2 ? "Two" : holders.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var shared = holders.Skip(1).Any(mod => mod.Entries.Intersect(holders[0].Entries, StringComparer.OrdinalIgnoreCase).Any(path => !IsIgnored(path)));
            var text = shared
                ? $"{count} model mods for {hero} ({names}): only '{holders[0].Name}' shows where they overlap."
                : $"{count} model mods for {hero} ({names}): they share no files and all load, but they may not look right together.";
            foreach (var mod in holders)
            {
                if (!warnings.TryGetValue(mod.Key, out var list))
                {
                    warnings[mod.Key] = list = [];
                }

                list.Add(text);
            }
        }

        return warnings.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value);
    }
}
