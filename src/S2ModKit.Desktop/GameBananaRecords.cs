using System.Text.Json;

namespace S2ModKit.Desktop;

/// <summary>Where an installed mod came from on GameBanana: the mod and file ids, the file's name, checksum and upload date, and
/// when it was installed. Keyed by the install receipt (stable across enable, disable and renaming).</summary>
internal sealed record GameBananaSource(string ReceiptId, long ModId, long FileId, string FileName, string Md5, DateTimeOffset FileAdded, DateTimeOffset Installed, string ModName);

/// <summary>The app's record of mods installed from GameBanana (<c>gamebanana-sources@1</c> in the data folder).</summary>
internal sealed class GameBananaRecords(string path)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public IReadOnlyList<GameBananaSource> Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Document>(File.ReadAllText(path))?.Sources ?? [] : [];
        }
        catch (JsonException)
        {
            return [];                                                       // an unreadable record only loses update badges
        }
    }

    public void Add(GameBananaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var sources = Load().Where(item => item.ReceiptId != source.ReceiptId).Append(source).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Document("gamebanana-sources@1", sources), Indented));
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record Document(string Schema, IReadOnlyList<GameBananaSource> Sources);
}
