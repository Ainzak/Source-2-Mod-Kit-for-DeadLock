using System.Text.Json;
using S2ModKit.Adapters.Source2;

namespace S2ModKit.Desktop;

/// <summary>A saved glow: a name and the Glow controls' values.</summary>
internal sealed record GlowPreset(string Name, float R, float G, float B, float Strength, float PulseSpeed, float PulseAmount, bool Edges)
{
    public MaterialLook ToLook() => new(R, G, B, Strength, PulseSpeed, PulseAmount, Edges);

    public static GlowPreset Of(string name, MaterialLook look) =>
        new(name, look.R, look.G, look.B, look.Strength, look.PulseSpeed, look.PulseAmount, look.Edges);

    public override string ToString() => Name;
}

/// <summary>
/// The saved glows (<c>glow-presets@1</c>, <c>glow-presets.json</c> in the data folder), shared by every mod. A name is saved
/// once (case-insensitive): saving it again replaces it. A file this does not understand is not overwritten.
/// </summary>
internal sealed class GlowPresets
{
    public const string Schema = "glow-presets@1";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly string path;
    private readonly List<GlowPreset> items;

    private GlowPresets(string path, List<GlowPreset> items, string? problem)
    {
        this.path = path;
        this.items = items;
        Problem = problem;
    }

    /// <summary>Why the saved file could not be read (null: it was read or there is none yet); saving is then refused.</summary>
    public string? Problem { get; }

    public IReadOnlyList<GlowPreset> Items => items;

    public static GlowPresets Load(string dataFolder)
    {
        var path = Path.Combine(dataFolder, "glow-presets.json");
        if (!File.Exists(path))
        {
            return new GlowPresets(path, [], null);
        }

        try
        {
            var file = JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(path));
            if (file?.Schema != Schema || file.Presets is null)
            {
                return new GlowPresets(path, [], $"'{path}' is not a {Schema} file.");
            }

            var valid = file.Presets.Where(preset => !string.IsNullOrWhiteSpace(preset.Name) && Valid(preset)).ToList();
            return new GlowPresets(path, valid, valid.Count == file.Presets.Count ? null : $"'{path}' has presets with values outside their ranges.");
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new GlowPresets(path, [], $"'{path}' could not be read: {exception.Message}");
        }
    }

    /// <summary>Saves (or replaces by name) a preset and writes the file.</summary>
    public GlowPreset Save(string name, MaterialLook look)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("Enter a name for the preset.", nameof(name));
        }

        var preset = GlowPreset.Of(name, look);
        if (!Valid(preset))
        {
            throw new ArgumentException("The glow has values outside their ranges.", nameof(look));
        }

        var at = items.FindIndex(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
        {
            items[at] = preset;
        }
        else
        {
            items.Add(preset);
        }

        Write();
        return preset;
    }

    public void Delete(string name)
    {
        if (items.RemoveAll(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Write();
        }
    }

    private void Write()
    {
        if (Problem is not null)
        {
            throw new InvalidOperationException(Problem + " Move it away to save presets again.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new PresetFile(Schema, items), Indented));
        File.Move(temporary, path, overwrite: true);
    }

    private static bool Valid(GlowPreset preset) =>
        new[] { preset.R, preset.G, preset.B, preset.PulseAmount }.All(value => float.IsFinite(value) && value is >= 0f and <= 1f)
        && float.IsFinite(preset.Strength) && preset.Strength is >= 0f and <= 10f
        && float.IsFinite(preset.PulseSpeed) && preset.PulseSpeed is >= 0f and <= 60f;

    private sealed record PresetFile(string Schema, List<GlowPreset>? Presets);
}
