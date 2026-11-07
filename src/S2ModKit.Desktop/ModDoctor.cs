using System.Text;
using System.Text.Json;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Desktop;

/// <summary>
/// What a mod depends on in the game, recorded so that the next game update can be compared with it: the game files the
/// mod replaces (with the game's own CRC of each), the resources it references that exist nowhere (its own files or the
/// game), and for each hero model it replaces, the bones of the game's model it lacks.
/// </summary>
internal sealed record ModFacts(
    string Key,
    string Name,
    IReadOnlyDictionary<string, uint> Overrides,
    IReadOnlyList<string> MissingReferences,
    IReadOnlyDictionary<string, IReadOnlyList<string>> MissingBones);

/// <summary>One doctor run: the game build it saw and the facts of every mod (enabled or not).</summary>
internal sealed record DoctorSnapshot(int Build, DateTimeOffset CheckedUtc, IReadOnlyList<ModFacts> Mods);

/// <summary>What the doctor says about one mod after comparing two snapshots.</summary>
internal sealed record DoctorFinding(string Key, string Name, DoctorVerdict Verdict, IReadOnlyList<string> Reasons, IReadOnlyList<string> StaleOverrides);

internal enum DoctorVerdict
{
    Works,
    Look,
    Repairable,
    Broken,
}

/// <summary>
/// The mod doctor's rules. A first run only records a baseline (mods that work today are not flagged for oddities they always
/// had). After a game update, a mod is:
/// <list type="bullet">
/// <item>Broken when it now references a resource that exists nowhere and did before;</item>
/// <item>Repairable when the update changed game data files it replaces (scripts, sound events, text): a copy without those
/// files lets the game's new versions load;</item>
/// <item>Look when the update changed other files it replaces (models, materials, hero-select scenes, animation graphs,
/// particles) or the game's hero model gained bones the mod's model lacks (that part may not move; often harmless);</item>
/// <item>Works otherwise.</item>
/// </list>
/// </summary>
internal static class DoctorRules
{
    /// <summary>Game files a mod usually copies only to change a few values, and that are safe to drop: the game's new version
    /// loads instead. Hero-select scenes, animation graphs, models and materials are a mod's own content, never dropped.</summary>
    public static bool IsDataFile(string path) =>
        path.EndsWith(".vdata_c", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vsndevts_c", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("resource/localization/", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<DoctorFinding> Compare(DoctorSnapshot? baseline, DoctorSnapshot current)
    {
        var before = baseline?.Mods.ToDictionary(mod => mod.Key, StringComparer.Ordinal) ?? [];
        var findings = new List<DoctorFinding>();
        foreach (var mod in current.Mods)
        {
            var reasons = new List<string>();
            if (baseline is null || !before.TryGetValue(mod.Key, out var old))
            {
                findings.Add(new DoctorFinding(mod.Key, mod.Name, DoctorVerdict.Works, [], []));    // nothing to compare with yet
                continue;
            }

            var changed = mod.Overrides.Where(pair => old.Overrides.TryGetValue(pair.Key, out var crc) && crc != pair.Value).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();
            var stale = changed.Where(IsDataFile).ToArray();
            var newMissing = mod.MissingReferences.Except(old.MissingReferences, StringComparer.OrdinalIgnoreCase).ToArray();
            var newBones = mod.MissingBones.Select(pair => (pair.Key, Bones: pair.Value.Except(old.MissingBones.GetValueOrDefault(pair.Key) ?? []).ToArray()))
                .Where(item => item.Bones.Length > 0).ToArray();

            if (newMissing.Length > 0)
            {
                reasons.Add($"It now references {newMissing.Length} resource(s) the game no longer has: {string.Join(", ", newMissing.Take(5))}. It needs its author.");
            }

            if (stale.Length > 0)
            {
                reasons.Add($"The update changed {stale.Length} game data file(s) this mod replaces with older copies: {string.Join(", ", stale.Take(5))}. Repair makes a copy without them so the game's new versions load.");
            }

            if (changed.Length > stale.Length)
            {
                reasons.Add($"The update changed {changed.Length - stale.Length} other game file(s) this mod replaces: {string.Join(", ", changed.Except(stale).Take(5))}. The mod's versions still win; check it in game.");
            }

            foreach (var (model, bones) in newBones)
            {
                reasons.Add($"The game's {model} gained {bones.Length} bone(s) this mod's model lacks ({string.Join(", ", bones.Take(5))}): those parts may not move. Often harmless.");
            }

            var verdict = newMissing.Length > 0 ? DoctorVerdict.Broken : stale.Length > 0 ? DoctorVerdict.Repairable : reasons.Count > 0 ? DoctorVerdict.Look : DoctorVerdict.Works;
            findings.Add(new DoctorFinding(mod.Key, mod.Name, verdict, reasons, stale));
        }

        return findings;
    }

    /// <summary>A reference worth checking: a full path to a compiled kind that shows in game. Legacy animation-graph references,
    /// the game's default textures and bare editor names are left out: mods that work carry them.</summary>
    public static bool IsCheckedReference(string reference) =>
        reference.Contains('/', StringComparison.Ordinal) && !reference.StartsWith("materials/default/", StringComparison.OrdinalIgnoreCase)
        && !reference.EndsWith(".vanmgrph", StringComparison.OrdinalIgnoreCase)
        && CheckedKinds.Any(kind => reference.EndsWith(kind, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] CheckedKinds = [".vmat", ".vtex", ".vmdl", ".vpcf", ".vsnd"];

    /// <summary>Bones that only matter to the game's own cloth simulation or attachment helpers.</summary>
    public static bool IsCheckedBone(string bone) => !bone.StartsWith("$cloth", StringComparison.Ordinal) && bone != "attachWorld";
}

/// <summary>Reads <see cref="ModFacts"/> from mod archives and the game's <c>pak01</c> archives (read-only). Dispose it after a
/// check: it keeps the game's archives open while it runs.</summary>
internal sealed class DoctorScanner(string game) : IDisposable
{
    private readonly List<Package> open = [];

    private Dictionary<string, (Package Package, PackageEntry Entry)>? vanilla;

    public ModFacts Read(string key, string name, IReadOnlyList<string> archives)
    {
        LoadVanilla();
        var overrides = new SortedDictionary<string, uint>(StringComparer.Ordinal);
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var bones = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var packages = archives.Where(File.Exists).Select(path => { var package = new Package(); package.Read(path); return package; }).ToArray();
        try
        {
            var own = packages.SelectMany(package => package.Entries!.SelectMany(group => group.Value)).Select(entry => entry.GetFullPath()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var package in packages)
            {
                foreach (var entry in package.Entries!.SelectMany(group => group.Value))
                {
                    var path = entry.GetFullPath();
                    var original = vanilla!.GetValueOrDefault(path);
                    if (original.Entry is not null)
                    {
                        overrides[path] = original.Entry.CRC32;
                    }

                    if (!entry.TypeName.EndsWith("_c", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    package.ReadEntry(entry, out var bytes);
                    foreach (var reference in References(bytes).Where(DoctorRules.IsCheckedReference))
                    {
                        if (!own.Contains(reference + "_c") && !vanilla!.ContainsKey(reference + "_c"))
                        {
                            missing.Add(reference);
                        }
                    }

                    if (entry.TypeName == "vmdl_c" && original.Entry is not null && path.StartsWith("models/heroes", StringComparison.OrdinalIgnoreCase)
                        && BonesOf(bytes) is { } modBones && ReadVanilla(original) is { } gameBytes && BonesOf(gameBytes) is { } gameBones)
                    {
                        bones[path] = [.. gameBones.Except(modBones).Where(DoctorRules.IsCheckedBone).Order(StringComparer.Ordinal)];
                    }
                }
            }
        }
        finally
        {
            foreach (var package in packages)
            {
                package.Dispose();
            }
        }

        return new ModFacts(key, name, overrides, [.. missing], bones);
    }

    private void LoadVanilla()
    {
        if (vanilla is not null)
        {
            return;
        }

        vanilla = new Dictionary<string, (Package, PackageEntry)>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { "core", "citadel" })                    // citadel last: it wins, as in the game
        {
            var path = Path.Combine(game, root, "pak01_dir.vpk");
            if (!File.Exists(path))
            {
                continue;
            }

            var package = new Package();                                     // kept open for reading game models until Dispose
            open.Add(package);
            package.Read(path);
            foreach (var entry in package.Entries!.SelectMany(group => group.Value))
            {
                vanilla[entry.GetFullPath()] = (package, entry);
            }
        }
    }

    public void Dispose()
    {
        foreach (var package in open)
        {
            package.Dispose();
        }

        open.Clear();
        vanilla = null;
    }

    private static byte[] ReadVanilla((Package Package, PackageEntry Entry) original)
    {
        original.Package.ReadEntry(original.Entry, out var bytes);
        return bytes;
    }

    private static HashSet<string>? BonesOf(byte[] bytes)
    {
        try
        {
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            return resource.DataBlock is Model model ? [.. model.Skeleton.Bones.Select(bone => bone.Name)] : null;
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>The external reference list (RERL block) of a compiled resource, read from its raw bytes.</summary>
    public static IEnumerable<string> References(byte[] bytes)
    {
        if (bytes.Length < 16)
        {
            yield break;
        }

        var table = 8 + (int)BitConverter.ToUInt32(bytes, 8);
        var count = (int)BitConverter.ToUInt32(bytes, 12);
        if (count > 64 || table < 0 || table + (count * 12) > bytes.Length)
        {
            yield break;
        }

        for (var i = 0; i < count; i++)
        {
            var at = table + (i * 12);
            if (Encoding.ASCII.GetString(bytes, at, 4) != "RERL")
            {
                continue;
            }

            var block = at + 4 + (int)BitConverter.ToUInt32(bytes, at + 4);
            if (block < 0 || block + 8 > bytes.Length)
            {
                yield break;
            }

            var array = block + (int)BitConverter.ToUInt32(bytes, block);
            var entries = (int)BitConverter.ToUInt32(bytes, block + 4);
            for (var j = 0; j < entries && array + (j * 16) + 16 <= bytes.Length; j++)
            {
                var field = array + (j * 16) + 8;
                var name = field + (int)BitConverter.ToUInt32(bytes, field);
                var end = name >= 0 && name < bytes.Length ? Array.IndexOf(bytes, (byte)0, name) : -1;
                if (end < 0)
                {
                    yield break;
                }

                yield return Encoding.UTF8.GetString(bytes, name, end - name);
            }
        }
    }
}

/// <summary>Doctor snapshots in the app's data folder: <c>doctor/snapshot-&lt;build&gt;.json</c> (schema <c>mod-doctor@1</c>).</summary>
internal static class DoctorStore
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static void Save(string root, DoctorSnapshot snapshot)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, $"snapshot-{snapshot.Build}.json"), JsonSerializer.Serialize(snapshot, Indented));
    }

    /// <summary>The latest snapshot from an earlier game build than <paramref name="build"/> (the baseline to compare with), if any.</summary>
    public static DoctorSnapshot? Before(string root, int build) =>
        All(root).Where(snapshot => snapshot.Build < build).MaxBy(snapshot => snapshot.Build);

    public static DoctorSnapshot? Of(string root, int build) => All(root).FirstOrDefault(snapshot => snapshot.Build == build);

    /// <summary>The highest game build the doctor has seen; a newer game build means the game was updated since.</summary>
    public static int? LastBuild(string root) => All(root).Select(snapshot => (int?)snapshot.Build).Max();

    private static IEnumerable<DoctorSnapshot> All(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(root, "snapshot-*.json"))
        {
            DoctorSnapshot? snapshot = null;
            try
            {
                snapshot = JsonSerializer.Deserialize<DoctorSnapshot>(File.ReadAllText(file));
            }
            catch (JsonException)
            {
            }

            if (snapshot is not null)
            {
                yield return snapshot;
            }
        }
    }
}
