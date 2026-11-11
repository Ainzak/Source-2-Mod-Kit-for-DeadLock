using System.Globalization;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Themes.Fluent;

namespace S2ModKit.Desktop;

/// <summary>A colour of the app the user can change: its key in the theme file, its name and what it paints.</summary>
internal sealed record ThemeRole(string Key, string Name, string Use);

/// <summary>What the theme file keeps: the chosen ready-made theme and the colours changed on top of it (hex, <c>#RRGGBB</c>).</summary>
internal sealed class ThemeChoice
{
    public string Name { get; set; } = AppTheme.DefaultName;

    public Dictionary<string, string> Colors { get; set; } = [];
}

/// <summary>
/// The app's colours: a few ready-made themes and the user's own changes on top, kept in <c>S2ModKit.theme.json</c>
/// beside the settings (apart from them, like the window layout). Every view paints with the shared brushes here, so a change shows at
/// once in every open window; the accent and the text colour go to the Fluent theme's palette (buttons that are on, selections,
/// plain text). Dark themes only: the controls' own colours (fields, menus) stay dark.
/// </summary>
internal static class AppTheme
{
    public const string DefaultName = ThemeColors.DefaultName;
    public const string Accent = ThemeColors.Accent;

    public static IReadOnlyList<ThemeRole> Roles => ThemeColors.Roles;

    public static IReadOnlyList<(string Name, IReadOnlyDictionary<string, Color> Colors)> Themes => ThemeColors.Themes;

    private static readonly Dictionary<string, SolidColorBrush> RoleBrushes = Roles.ToDictionary(role => role.Key, _ => new SolidColorBrush());
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static ThemeChoice choice = new();

    public static SolidColorBrush Background => RoleBrushes["background"];

    public static SolidColorBrush Side => RoleBrushes["side"];

    public static SolidColorBrush Main => RoleBrushes["main"];

    public static SolidColorBrush Header => RoleBrushes["header"];

    public static SolidColorBrush Section => RoleBrushes["section"];

    public static SolidColorBrush Text => RoleBrushes["text"];

    public static SolidColorBrush Muted => RoleBrushes["muted"];

    public static SolidColorBrush Outline => RoleBrushes["outline"];

    public static SolidColorBrush AccentBrush => RoleBrushes[Accent];

    public static SolidColorBrush Warning => RoleBrushes["warning"];

    public static SolidColorBrush Good => RoleBrushes["good"];

    /// <summary>A section header under the pointer: one step lighter than <see cref="Header"/>.</summary>
    public static SolidColorBrush HeaderHover { get; } = new();

    /// <summary>The Fluent theme's dark palette (accent, window background, text); the app adds it to its theme on start.</summary>
    public static ColorPaletteResources Palette { get; } = new();

    /// <summary>The system's accent colour, used when a theme has none (read by the app on start).</summary>
    public static Color SystemAccent { get; set; } = Color.FromRgb(0, 120, 215);

    public static string Name => choice.Name;

    /// <summary>Whether the user changed colours of the chosen theme.</summary>
    public static bool Changed => choice.Colors.Count > 0;

    private static string FilePath => Path.Combine(Path.GetDirectoryName(AppSettings.FilePath)!, "S2ModKit.theme.json");

    /// <summary>Reads the theme file (the default theme when it is missing or unreadable) and paints with it.</summary>
    public static void Load()
    {
        try
        {
            choice = File.Exists(FilePath) ? JsonSerializer.Deserialize<ThemeChoice>(File.ReadAllText(FilePath)) ?? new ThemeChoice() : new ThemeChoice();
            choice.Colors ??= [];
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Reading the theme", exception);
            choice = new ThemeChoice();
        }

        Apply();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(choice, Indented));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Saving the theme", exception);   // only the colours are lost
        }
    }

    /// <summary>Picks a ready-made theme; the user's changes go.</summary>
    public static void Use(string name)
    {
        choice = new ThemeChoice { Name = name };
        Apply();
    }

    /// <summary>Changes one colour; null for the accent means the system's accent again.</summary>
    public static void Set(string key, Color? color)
    {
        var theme = ThemeColors.Resolve(choice.Name, null);
        if (theme[key] == color)
        {
            choice.Colors.Remove(key);
        }
        else
        {
            choice.Colors[key] = color is { } value ? ThemeColors.Hex(value) : key == Accent ? ThemeColors.SystemValue : throw new ArgumentNullException(nameof(color));
        }

        Apply();
    }

    public static SolidColorBrush Brush(string key) => RoleBrushes[key];

    /// <summary>The colour a role paints with now (the system's accent for a theme without one).</summary>
    public static Color Current(string key) => RoleBrushes[key].Color;

    private static void Apply()
    {
        foreach (var (key, color) in ThemeColors.Resolve(choice.Name, choice.Colors))
        {
            RoleBrushes[key].Color = color ?? SystemAccent;
        }

        var header = Header.Color;
        HeaderHover.Color = Color.FromRgb(Lighter(header.R), Lighter(header.G), Lighter(header.B));
        Palette.Accent = AccentBrush.Color;
        Palette.RegionColor = Background.Color;
        Palette.BaseHigh = Text.Color;
    }

    private static byte Lighter(byte value) => (byte)Math.Min(255, value + 10);
}

/// <summary>The roles, the ready-made themes and how a theme file's colours are read (no UI objects, so it is testable alone).</summary>
internal static class ThemeColors
{
    public const string DefaultName = "Default";
    public const string Accent = "accent";
    public const string SystemValue = "system";                            // the accent: the system's accent colour

    public static readonly IReadOnlyList<ThemeRole> Roles =
    [
        new("background", "Background", "Behind everything, the 3D view's frame and the hint cards"),
        new("side", "Side panels", "The mod list, the edit pane and the effects window"),
        new("main", "Main area", "The mod's page in the middle"),
        new("header", "Section headers", "The folding headers in the edit pane"),
        new("section", "Section bodies", "Inside an open section"),
        new("text", "Text", "Plain text"),
        new("muted", "Secondary text", "Notes, details and hints"),
        new("outline", "Outlines", "Borders, list frames, numbers and the hint icon"),
        new(Accent, "Accent", "Buttons that are on, selections and the hint icon under the pointer"),
        new("warning", "Warnings", "Warnings and marks that need a look"),
        new("good", "Good marks", "Marks that say something is fine"),
    ];

    /// <summary>The ready-made themes; a missing accent means the system's accent colour.</summary>
    public static readonly IReadOnlyList<(string Name, IReadOnlyDictionary<string, Color> Colors)> Themes =
    [
        (DefaultName, Colors((30, 31, 34), (43, 45, 49), (49, 51, 56), (53, 55, 60), (47, 49, 54), (242, 243, 245), (181, 186, 193), (92, 95, 102), null, (240, 170, 70), (90, 190, 100))),
        ("Midnight", Colors((14, 18, 30), (21, 27, 43), (26, 33, 52), (34, 43, 66), (24, 31, 49), (232, 236, 247), (165, 177, 205), (72, 88, 126), (92, 142, 255), (245, 180, 80), (95, 200, 140))),
        ("Forest", Colors((16, 22, 18), (23, 31, 26), (28, 37, 31), (37, 49, 41), (26, 35, 29), (228, 238, 229), (168, 189, 173), (72, 98, 80), (86, 176, 104), (232, 178, 72), (120, 210, 120))),
        ("Rose", Colors((26, 18, 28), (35, 25, 39), (42, 30, 46), (55, 39, 60), (39, 28, 43), (244, 232, 244), (204, 176, 206), (108, 78, 114), (222, 92, 168), (245, 175, 85), (110, 205, 140))),
        ("High contrast", Colors((0, 0, 0), (16, 16, 16), (22, 22, 22), (40, 40, 40), (26, 26, 26), (255, 255, 255), (215, 215, 215), (150, 150, 150), (255, 200, 0), (255, 140, 40), (80, 230, 100))),
    ];

    /// <summary>The colours of <paramref name="name"/> (the default theme for an unknown name) with <paramref name="overrides"/> on top;
    /// unknown keys and unreadable colours are left out. A missing accent is the system's accent.</summary>
    public static Dictionary<string, Color?> Resolve(string? name, IReadOnlyDictionary<string, string>? overrides)
    {
        var theme = Themes.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        var colors = theme.Colors ?? Themes[0].Colors;
        var resolved = Roles.ToDictionary(role => role.Key, role => colors.TryGetValue(role.Key, out var color) ? color : (Color?)null);
        foreach (var (key, value) in overrides ?? new Dictionary<string, string>())
        {
            if (key == Accent && value == SystemValue)
            {
                resolved[key] = null;
            }
            else if (resolved.ContainsKey(key) && TryParse(value, out var color))
            {
                resolved[key] = color;
            }
        }

        return resolved;
    }

    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        var hex = text?.Trim().TrimStart('#');
        if (hex is not { Length: 6 } || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        color = Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Dictionary<string, Color> Colors(
        (byte, byte, byte) background, (byte, byte, byte) side, (byte, byte, byte) main, (byte, byte, byte) header, (byte, byte, byte) section,
        (byte, byte, byte) text, (byte, byte, byte) muted, (byte, byte, byte) outline, (byte, byte, byte)? accent, (byte, byte, byte) warning, (byte, byte, byte) good)
    {
        static Color C((byte R, byte G, byte B) rgb) => Color.FromRgb(rgb.R, rgb.G, rgb.B);
        var colors = new Dictionary<string, Color>
        {
            ["background"] = C(background),
            ["side"] = C(side),
            ["main"] = C(main),
            ["header"] = C(header),
            ["section"] = C(section),
            ["text"] = C(text),
            ["muted"] = C(muted),
            ["outline"] = C(outline),
            ["warning"] = C(warning),
            ["good"] = C(good),
        };
        if (accent is { } value)
        {
            colors[Accent] = C(value);
        }

        return colors;
    }
}
