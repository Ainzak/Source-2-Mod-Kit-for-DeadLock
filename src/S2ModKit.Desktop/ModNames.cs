using System.Text.Json;

namespace S2ModKit.Desktop;

/// <summary>
/// Names the user gave mods in the app (<c>mod-names.json</c> in the data folder). Only the app's list shows them: the files keep
/// their names, which are their load-order slots and what other mod managers rely on. Keyed like profiles (an app install by its
/// package, a Mod Manager mod by its id, anything else by its file).
/// </summary>
internal sealed class ModNames(string path)
{
    private const string Schema = "mod-names@1";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public IReadOnlyDictionary<string, string> Load()
    {
        try
        {
            return File.Exists(path) && JsonSerializer.Deserialize<Document>(File.ReadAllText(path)) is { Schema: Schema } document
                ? document.Names
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Reading mod names", exception);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Gives <paramref name="mod"/> a name; null or blank takes the name it had back.</summary>
    public void Set(ModEntry mod, string? name)
    {
        ArgumentNullException.ThrowIfNull(mod);
        var names = new Dictionary<string, string>(Load(), StringComparer.Ordinal);
        var key = ModProfiles.IdentityOf(mod);
        if (string.IsNullOrWhiteSpace(name))
        {
            names.Remove(key);
        }
        else
        {
            names[key] = name.Trim();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new Document(Schema, names), Indented));
    }

    /// <summary>The mods with the user's names on them; <see cref="ModEntry.OriginalName"/> keeps the name each had.</summary>
    public IReadOnlyList<ModEntry> Apply(IReadOnlyList<ModEntry> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);
        var names = Load();
        return names.Count == 0 ? mods
            : [.. mods.Select(mod => names.TryGetValue(ModProfiles.IdentityOf(mod), out var name) ? mod with { Name = name, OriginalName = mod.Name } : mod)];
    }

    private sealed record Document(string Schema, Dictionary<string, string> Names);
}
