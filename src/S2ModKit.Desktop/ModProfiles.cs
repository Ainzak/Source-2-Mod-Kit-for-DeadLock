using System.Text.Json;
using S2ModKit.Application;

namespace S2ModKit.Desktop;

/// <summary>One mod of a profile: its lasting identity (see <see cref="ModProfiles.IdentityOf"/>) and its name when the profile was saved.</summary>
internal sealed record ProfileMod(string Id, string Name);

/// <summary>A named set of enabled mods, in load order (the first wins over later ones).</summary>
internal sealed record ModProfile(string Name, DateTimeOffset Saved, IReadOnlyList<ProfileMod> Mods);

/// <summary>What switching to a profile changes now: the mods to disable, the mods to enable (in the profile's load order) and the
/// profile's mods that are no longer in the folder.</summary>
internal sealed record ProfilePlan(IReadOnlyList<ModEntry> Disable, IReadOnlyList<ModEntry> Enable, IReadOnlyList<ProfileMod> Missing)
{
    public bool IsEmpty => Disable.Count == 0 && Enable.Count == 0;
}

/// <summary>
/// Profiles (<c>profiles.json</c> in the data folder, <c>mod-profiles@1</c>): named sets of enabled mods. A mod is known by an identity
/// that survives enable and disable: a Mod Manager mod by its mod id, an app install by its package; any other mod by its file
/// name, which the app follows through its own renames (as the version library does). Switching renames in place like Enable and
/// Disable; nothing is copied or deleted.
/// </summary>
internal sealed class ModProfiles(string path)
{
    private const string Schema = "mod-profiles@1";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The identity of <paramref name="mod"/> in profiles.</summary>
    public static string IdentityOf(ModEntry mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        return mod.Install is { } install ? "package:" + install.Receipt.PackagePath
            : mod.Source == ModSource.Manager && mod.ModId is { Length: > 0 } id ? "mod:" + id
            : mod.Key;
    }

    public IReadOnlyList<ModProfile> Load()
    {
        try
        {
            return File.Exists(path) && JsonSerializer.Deserialize<Document>(File.ReadAllText(path)) is { Schema: Schema } document ? document.Profiles : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Saves the enabled mods of <paramref name="mods"/> as <paramref name="name"/> (replacing a profile of that name).</summary>
    public ModProfile Save(string name, IReadOnlyList<ModEntry> mods, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(mods);
        var profile = new ModProfile(name.Trim(), now, [.. mods.Where(mod => mod.Loaded).OrderBy(mod => mod.FirstSlot).ThenBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .Select(mod => new ProfileMod(IdentityOf(mod), mod.Name))]);
        Write([.. Load().Where(other => !other.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)), profile]);
        return profile;
    }

    public void Delete(string name) => Write([.. Load().Where(other => !other.Name.Equals(name, StringComparison.OrdinalIgnoreCase))]);

    /// <summary>File-name identities follow the app's renames (all renames of one operation at once, as a chain of slots can move).</summary>
    public void FollowRenames(IReadOnlyList<AddonRename> renames)
    {
        ArgumentNullException.ThrowIfNull(renames);
        var map = renames.ToDictionary(item => AddonNaming.FileKey(item.From), item => AddonNaming.FileKey(item.To), StringComparer.OrdinalIgnoreCase);
        var profiles = Load();
        if (map.Count == 0 || !profiles.Any(profile => profile.Mods.Any(mod => map.ContainsKey(mod.Id))))
        {
            return;
        }

        Write([.. profiles.Select(profile => profile with { Mods = [.. profile.Mods.Select(mod => map.TryGetValue(mod.Id, out var next) ? mod with { Id = next } : mod)] })]);
    }

    /// <summary>What switching to <paramref name="profile"/> changes in <paramref name="mods"/>.</summary>
    public static ProfilePlan Plan(ModProfile profile, IReadOnlyList<ModEntry> mods)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(mods);
        var byId = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods.OrderByDescending(mod => mod.Loaded))                  // an enabled copy wins over a disabled one with the same id
        {
            byId.TryAdd(IdentityOf(mod), mod);
        }

        var wanted = profile.Mods.Select(mod => byId.GetValueOrDefault(mod.Id)).OfType<ModEntry>().ToArray();
        var keep = wanted.Select(mod => mod.Key).ToHashSet(StringComparer.Ordinal);
        return new ProfilePlan(
            [.. mods.Where(mod => mod.Loaded && !keep.Contains(mod.Key))],
            [.. wanted.Where(mod => !mod.Loaded)],
            [.. profile.Mods.Where(mod => !byId.ContainsKey(mod.Id))]);
    }

    private void Write(IReadOnlyList<ModProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(Schema, [.. profiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)]), Indented));
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record Document(string Schema, IReadOnlyList<ModProfile> Profiles);
}
