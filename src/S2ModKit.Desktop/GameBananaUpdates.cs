using System.Text.Json;

namespace S2ModKit.Desktop;

/// <summary>A newer GameBanana file for an installed mod: the GameBanana mod, the files uploaded after what is installed (newest
/// first) and when it was checked.</summary>
internal sealed record ModUpdate(long GameBananaId, string Name, IReadOnlyList<GameBananaFile> Files, DateTimeOffset Checked);

/// <summary>
/// Update checks for installed mods that came from GameBanana: a mod the app installed is compared by its recorded file (a newer
/// file id uploaded later means an update); a Deadlock Mod Manager mod by its download date (a file uploaded after it). Each
/// GameBanana mod is read once per check; the files seen are kept in the data folder (<c>gamebanana-updates@1</c>) so badges
/// survive a restart without asking GameBanana again.
/// </summary>
internal sealed class GameBananaUpdates(string path)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly TimeSpan Slack = TimeSpan.FromHours(1);         // clocks and upload times differ a little

    /// <summary>When the last full check ran (null: never).</summary>
    public DateTimeOffset? LastCheck => Read()?.Checked;

    /// <summary>Updates for <paramref name="mods"/> from the files seen in the last check.</summary>
    public IReadOnlyDictionary<string, ModUpdate> Known(IReadOnlyList<ModEntry> mods, IReadOnlyList<GameBananaSource> sources, Func<ModEntry, long?> idOf) =>
        Read() is { } cache ? Compare(mods, sources, idOf, cache.Files, cache.Names, cache.Checked) : new Dictionary<string, ModUpdate>();

    /// <summary>Reads every GameBanana mod the installed mods came from (one request each, one after another) and returns the updates.</summary>
    public async Task<IReadOnlyDictionary<string, ModUpdate>> CheckAsync(GameBananaClient client, IReadOnlyList<ModEntry> mods, IReadOnlyList<GameBananaSource> sources,
        Func<ModEntry, long?> idOf, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var ids = mods.Select(idOf).OfType<long>().Distinct().ToArray();
        var files = new Dictionary<long, IReadOnlyList<GameBananaFile>>();
        var names = new Dictionary<long, string>();
        var done = 0;
        foreach (var id in ids)
        {
            progress?.Report($"Checking GameBanana for updates: {++done} of {ids.Length}...");
            try
            {
                var page = await client.PageAsync(id, cancellationToken).ConfigureAwait(false);
                files[id] = page.Files;
                names[id] = page.Mod.Name;
            }
            catch (HttpRequestException)
            {
                // a removed or private mod: no update information for it
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);   // be gentle with GameBanana
        }

        var now = DateTimeOffset.UtcNow;
        Write(new Cache("gamebanana-updates@1", now, files, names));
        return Compare(mods, sources, idOf, files, names, now);
    }

    /// <summary>The comparison itself (no network): newer files per installed mod.</summary>
    internal static IReadOnlyDictionary<string, ModUpdate> Compare(IReadOnlyList<ModEntry> mods, IReadOnlyList<GameBananaSource> sources, Func<ModEntry, long?> idOf,
        IReadOnlyDictionary<long, IReadOnlyList<GameBananaFile>> files, IReadOnlyDictionary<long, string> names, DateTimeOffset checkedAt)
    {
        var updates = new Dictionary<string, ModUpdate>(StringComparer.Ordinal);
        foreach (var mod in mods)
        {
            if (idOf(mod) is not { } id || !files.TryGetValue(id, out var list) || list.Count == 0)
            {
                continue;
            }

            var source = mod.Install?.Receipt.Id is { } receipt ? sources.FirstOrDefault(item => item.ReceiptId == receipt) : null;
            var newer = source is not null
                ? list.Where(file => file.Id != source.FileId && file.Added > source.FileAdded + Slack)
                : list.Where(file => mod.Added != default && file.Added > mod.Added + Slack);
            var found = newer.OrderByDescending(file => file.Added).ToArray();
            if (found.Length > 0)
            {
                updates[mod.Key] = new ModUpdate(id, names.GetValueOrDefault(id) ?? mod.Name, found, checkedAt);
            }
        }

        return updates;
    }

    private Cache? Read()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Cache>(File.ReadAllText(path)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Write(Cache cache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(cache, Indented));
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record Cache(string Schema, DateTimeOffset Checked, IReadOnlyDictionary<long, IReadOnlyList<GameBananaFile>> Files, IReadOnlyDictionary<long, string> Names);
}
