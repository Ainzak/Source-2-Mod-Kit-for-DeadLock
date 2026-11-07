using S2ModKit.Domain;

namespace S2ModKit.Application;

/// <summary>One version of a mod in the library. <see cref="Key"/> is the mod's list key: its Mod Manager id, or
/// <c>file:&lt;archive name&gt;</c> (see <see cref="AddonNaming.FileKey"/>), which the app updates when it renames the archive.</summary>
public sealed record ModLibraryVersion
{
    public string Key { get; init; } = string.Empty;

    /// <summary>For example "original", "repaired 2026-11-10", "edited: no jacket".</summary>
    public string Label { get; init; } = string.Empty;

    public DateTimeOffset? CreatedUtc { get; init; }
}

public sealed record ModLibraryMod
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<ModLibraryVersion> Versions { get; init; } = [];
}

/// <summary>The app's library (<c>mod-library@1</c>): which mods are versions of one mod. At most one version of a mod is enabled.</summary>
public sealed record ModLibraryDocument
{
    public const string CurrentSchema = "mod-library@1";

    public string Schema { get; init; } = CurrentSchema;

    public IReadOnlyList<ModLibraryMod> Mods { get; init; } = [];

    /// <summary>The library mod that lists <paramref name="key"/> as a version; null when none.</summary>
    public ModLibraryMod? FindByKey(string key) => Mods.FirstOrDefault(mod => mod.Versions.Any(version => version.Key == key));

    /// <summary>Adds versions to the library mod called <paramref name="name"/> (created when new). A key already listed in another
    /// library mod, or twice, is refused.</summary>
    public ModLibraryDocument WithVersions(string name, IReadOnlyList<ModLibraryVersion> versions, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(versions);
        var existing = Mods.FirstOrDefault(mod => mod.Name == name);
        var keys = versions.Select(version => version.Key).ToArray();
        if (keys.Distinct().Count() != keys.Length || keys.Any(key => string.IsNullOrWhiteSpace(key))
            || Mods.Where(mod => mod != existing).Any(mod => mod.Versions.Any(version => keys.Contains(version.Key)))
            || (existing?.Versions.Any(version => keys.Contains(version.Key)) ?? false))
        {
            throw Errors.InvalidRecipe("MOD_LIBRARY_DUPLICATE_VERSION", "A version key is empty, repeated or already in the library.", "List each mod once, as a version of one library mod.");
        }

        var added = versions.Select(version => version with { CreatedUtc = version.CreatedUtc ?? now }).ToArray();
        return existing is null
            ? this with { Mods = [.. Mods, new ModLibraryMod { Id = Guid.NewGuid().ToString("N"), Name = name, Versions = added }] }
            : this with { Mods = [.. Mods.Select(mod => mod == existing ? mod with { Versions = [.. mod.Versions, .. added] } : mod)] };
    }

    /// <summary>Follows a renamed archive: every version key <c>file:old</c> becomes <c>file:new</c>.</summary>
    public ModLibraryDocument WithRenames(IEnumerable<AddonRename> renames)
    {
        ArgumentNullException.ThrowIfNull(renames);
        var map = renames.ToDictionary(item => AddonNaming.FileKey(item.From), item => AddonNaming.FileKey(item.To), StringComparer.OrdinalIgnoreCase);
        if (!Mods.Any(mod => mod.Versions.Any(version => map.ContainsKey(version.Key))))
        {
            return this;
        }

        return this with
        {
            Mods = [.. Mods.Select(mod => mod with { Versions = [.. mod.Versions.Select(version => map.TryGetValue(version.Key, out var next) ? version with { Key = next } : version)] })],
        };
    }

    /// <summary>Drops a deleted mod from its library mod (and the library mod when fewer than two versions remain).</summary>
    public ModLibraryDocument Without(string key) => this with
    {
        Mods = [.. Mods.Select(mod => mod with { Versions = [.. mod.Versions.Where(version => version.Key != key)] }).Where(mod => mod.Versions.Count > 1)],
    };
}

public interface IModLibraryStore
{
    Task<ModLibraryDocument> LoadAsync(string path, CancellationToken cancellationToken = default);

    Task SaveAsync(string path, ModLibraryDocument document, CancellationToken cancellationToken = default);
}
