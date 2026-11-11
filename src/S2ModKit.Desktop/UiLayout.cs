using System.Text.Json;

namespace S2ModKit.Desktop;

/// <summary>
/// How the user arranged the window (kept apart from the settings, which other code saves as a whole): the edit pane's width,
/// and the sections folded in it. Stored beside the settings file as
/// <c>S2ModKit.layout.json</c>; a missing or unreadable file gives the defaults.
/// </summary>
internal sealed class UiLayout
{
    public const double MinPaneWidth = 360;
    public const double MaxPaneWidth = 800;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public double PaneWidth { get; set; } = 380;

    public List<string> Folded { get; set; } = [];

    private static string FilePath => Path.Combine(Path.GetDirectoryName(AppSettings.FilePath)!, "S2ModKit.layout.json");

    public static UiLayout Load()
    {
        try
        {
            var layout = File.Exists(FilePath) ? JsonSerializer.Deserialize<UiLayout>(File.ReadAllText(FilePath)) ?? new UiLayout() : new UiLayout();
            layout.PaneWidth = Math.Clamp(layout.PaneWidth, MinPaneWidth, MaxPaneWidth);
            layout.Folded ??= [];
            return layout;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Reading the window layout", exception);
            return new UiLayout();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Indented));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Saving the window layout", exception);   // only the arrangement is lost
        }
    }
}
