using SteamDatabase.ValvePak;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Desktop;

/// <summary>A sound file in a mod: its compiled path and whether the game has a sound at the same path (a replacement).</summary>
internal sealed record SoundFile(string Path, bool ReplacesGame);

/// <summary>
/// A sound event a mod changes or adds (<c>.vsndevts_c</c>): the sounds it plays in the mod and, when the game has the
/// event, the game's sounds. Sound paths are compiled paths (<c>.vsnd_c</c>).
/// </summary>
internal sealed record SoundEventChange(string EventsFile, string Event, IReadOnlyList<string> ModSounds, IReadOnlyList<string> GameSounds)
{
    public bool New => GameSounds.Count == 0;
}

/// <summary>
/// Reads a mod's sounds for listening (read-only): the sound files it ships, the sound events it changes, and decoded
/// sounds from the mod or from the game (WAV or MP3 as stored; VRF adds the WAV header Valve strips). The game's archives
/// stay open; the mod's are opened only while reading, so the mod can still be renamed (enabled or disabled).
/// </summary>
internal sealed class SoundLibrary : IDisposable
{
    private static readonly string[] GameRoots = ["citadel", "core"];
    private readonly string[] modArchives;
    private readonly Package[] game;

    private SoundLibrary(string[] modArchives, Package[] game)
    {
        this.modArchives = modArchives;
        this.game = game;
        using var mod = new Packages(modArchives);
        Files = [.. mod.All.SelectMany(Entries).Where(path => path.EndsWith(".vsnd_c", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).Select(path => new SoundFile(path, Find(game, path) is not null))];
        Events = [.. mod.All.SelectMany(Entries).Where(path => path.EndsWith(".vsndevts_c", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).SelectMany(file => ChangedEvents(mod.All, file))];
    }

    public IReadOnlyList<SoundFile> Files { get; }

    public IReadOnlyList<SoundEventChange> Events { get; }

    /// <summary>Opens the mod's archives and the game's (<c>citadel</c>, then <c>core</c>).</summary>
    public static SoundLibrary Open(IEnumerable<string> modArchives, string gameFolder)
    {
        static Package Read(string path)
        {
            var package = new Package();
            package.Read(path);
            return package;
        }

        var gameArchives = GameRoots.Select(root => Path.Combine(gameFolder, root, "pak01_dir.vpk")).Where(File.Exists);
        return new SoundLibrary([.. modArchives.Where(File.Exists)], [.. gameArchives.Select(Read)]);
    }

    /// <summary>Whether the sound exists in the mod (or, with <paramref name="fromGame"/>, in the game).</summary>
    public bool Has(string path, bool fromGame)
    {
        if (fromGame)
        {
            return Find(game, path) is not null;
        }

        using var mod = new Packages(modArchives);
        return Find(mod.All, path) is not null;
    }

    /// <summary>
    /// The playable sound at <paramref name="path"/> from the mod (falling back to the game, as the game loads it) or, with
    /// <paramref name="fromGame"/>, from the game only; null when neither has it. The extension is ".wav", ".mp3" or ".aac".
    /// </summary>
    public (byte[] Data, string Extension, float Seconds)? Decode(string path, bool fromGame)
    {
        using var mod = new Packages(fromGame ? [] : modArchives);
        if ((Find(mod.All, path) ?? Find(game, path)) is not { } found)
        {
            return null;
        }

        found.Package.ReadEntry(found.Entry, out var bytes);
        using var resource = new Resource { FileName = path };
        resource.Read(new MemoryStream(bytes));
        if (resource.DataBlock is not Sound sound)
        {
            return null;
        }

        using var stream = sound.GetSoundStream();
        var extension = sound.SoundType switch
        {
            Sound.AudioFileType.MP3 => ".mp3",
            Sound.AudioFileType.AAC => ".aac",
            _ => ".wav",
        };
        return (((MemoryStream)stream).ToArray(), extension, sound.Duration);
    }

    public void Dispose()
    {
        foreach (var package in game)
        {
            package.Dispose();
        }
    }

    private List<SoundEventChange> ChangedEvents(IReadOnlyList<Package> mod, string eventsFile)
    {
        var changes = new List<SoundEventChange>();
        var modEvents = ReadEvents(Find(mod, eventsFile));
        var gameEvents = ReadEvents(Find(game, eventsFile));
        foreach (var (name, sounds) in modEvents.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var original = gameEvents.GetValueOrDefault(name) ?? [];
            if (!sounds.SequenceEqual(original, StringComparer.OrdinalIgnoreCase))
            {
                changes.Add(new SoundEventChange(eventsFile, name, sounds, original));
            }
        }

        return changes;
    }

    /// <summary>Event name to the sound files it names anywhere inside it (source paths <c>.vsnd</c> become compiled <c>.vsnd_c</c>).</summary>
    private static Dictionary<string, IReadOnlyList<string>> ReadEvents((Package Package, PackageEntry Entry)? found)
    {
        var events = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (found is not { } at)
        {
            return events;
        }

        at.Package.ReadEntry(at.Entry, out var bytes);
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        if (resource.DataBlock is not BinaryKV3 kv3)
        {
            return events;
        }

        var root = kv3.Data.Root;
        foreach (var name in root.Keys)
        {
            var sounds = new List<string>();
            Collect(root[name], sounds, 0);
            events[name] = sounds;
        }

        return events;
    }

    private static void Collect(KVObject node, List<string> sounds, int depth)
    {
        if (depth > 32)
        {
            return;
        }

        if (node.IsCollection || node.IsArray)
        {
            foreach (var child in node.Values)
            {
                Collect(child, sounds, depth + 1);
            }
        }
        else if (node.ValueType == KVValueType.String && node.ToString(null) is { } text
            && (text.EndsWith(".vsnd", StringComparison.OrdinalIgnoreCase) || text.EndsWith(".vsnd_c", StringComparison.OrdinalIgnoreCase)))
        {
            var path = text.Replace('\\', '/').ToLowerInvariant();
            sounds.Add(path.EndsWith("_c", StringComparison.Ordinal) ? path : path + "_c");
        }
    }

    private static IEnumerable<string> Entries(Package package) =>
        package.Entries?.Values.SelectMany(list => list).Select(entry => entry.GetFullPath().Replace('\\', '/')) ?? [];

    /// <summary>Archives opened for one read and closed again.</summary>
    private sealed class Packages : IDisposable
    {
        public Packages(IEnumerable<string> paths)
        {
            All = [.. paths.Select(path =>
            {
                var package = new Package();
                package.Read(path);
                return package;
            })];
        }

        public IReadOnlyList<Package> All { get; }

        public void Dispose()
        {
            foreach (var package in All)
            {
                package.Dispose();
            }
        }
    }

    private static (Package Package, PackageEntry Entry)? Find(IEnumerable<Package> packages, string path)
    {
        foreach (var package in packages)
        {
            if (package.FindEntry(path) is { } entry)
            {
                return (package, entry);
            }
        }

        return null;
    }
}
