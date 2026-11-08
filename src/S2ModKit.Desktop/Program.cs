using Avalonia;
using Avalonia.OpenGL;

namespace S2ModKit.Desktop;

internal static class Program
{
    /// <summary>
    /// S2ModKit desktop app. The preview renderer (ValveResourceFormat.Renderer) needs desktop OpenGL 4.6, so the app runs
    /// Avalonia on native WGL instead of its default ANGLE (OpenGL ES over Direct3D).
    /// Arguments: --list-mods &lt;out.json&gt; | [--game &lt;Deadlock game folder&gt;] [--selftest &lt;out prefix&gt; [--model &lt;path.vmdl&gt;] [--addon &lt;vpk&gt;] [--outline &lt;thickness&gt;]]
    /// (--selftest: preview the vanilla model about 9 s, write &lt;prefix&gt;.json with open time, frame rates and the render
    /// thread, and &lt;prefix&gt;-plain/-highlight.png, then exit).
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--list-mods")                // the mod list as JSON, without a window (checks)
        {
            var catalog = new ModCatalog(AppSettings.Load(null));
            var mods = catalog.LoadAsync().GetAwaiter().GetResult();
            File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(mods.Select(mod => new
            {
                mod.Name,
                Source = mod.Source.ToString(),
                mod.Loaded,
                Files = mod.Files.Select(Path.GetFileName),
                mod.Slots,
                Conflicts = mod.ConflictInfo.With.Select(with => new
                {
                    Mod = with.OtherName,
                    with.Wins,
                    with.Loses,
                    Result = with.Loses > 0 ? (with.Wins > 0 ? "wins some, loses some" : "loses") : "wins",
                    ByKind = with.ByKind.Select(item => $"{item.Kind}: wins {item.Wins}, loses {item.Loses}"),
                    with.Notes,
                    Examples = with.Paths.Take(5).Select(path => $"{(path.Wins ? "wins" : "loses")} {path.Path}"),
                }),
                ModelWarnings = mod.ConflictInfo.ModelWarnings,
                mod.Models,
                mod.KindText,
                mod.Group,
                Heroes = mod.HeroKinds?.Select(pair => $"{pair.Key}: {string.Join(", ", pair.Value)}"),
                Added = mod.Added.ToString("u", System.Globalization.CultureInfo.InvariantCulture),
                Entries = mod.Entries.Count,
                Install = mod.Install?.Receipt.Status,
                Toggle = catalog.PlanToggleAsync(mod).GetAwaiter().GetResult().Select(item => $"{item.From} -> {item.To}"),   // dry run: nothing is renamed
            })));
            return 0;
        }

        if (args.Length == 2 && args[0] == "--check-mods")               // the mod doctor without a window (checks); read-only for addons
        {
            var catalog = new ModCatalog(AppSettings.Load(null));
            var report = catalog.CheckModsAsync(catalog.LoadAsync().GetAwaiter().GetResult()).GetAwaiter().GetResult();
            File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(report));
            return 0;
        }

        App.Options = AppOptions.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Wgl],
                WglProfiles = [new GlVersion(GlProfileType.OpenGL, 4, 6)],
            });
}

/// <summary>Command-line options. <c>--ui-shot &lt;png&gt;</c> opens the window, shows <c>--select &lt;mod name&gt;</c> (and, in its preview,
/// tool <c>--tab Parts|Colour|Shape</c> with part <c>--part &lt;number&gt;</c>), saves the window as a picture and closes (layout checks without
/// controlling the desktop); <c>--by-hero</c> starts in the per-hero view.</summary>
internal sealed record AppOptions(string? Game, string Model, string? SelfTest, string? Addon = null, float? Outline = null,
    string? UiShot = null, string? Select = null, string? Tab = null, int? Part = null, bool ByHero = false)
{
    public static AppOptions Parse(string[] args)
    {
        string? game = null, selfTest = null, addon = null, uiShot = null, select = null, tab = null;
        int? part = null;
        var byHero = false;
        float? outline = null;
        var model = "models/heroes_wip/wraith/wraith.vmdl";
        for (var i = 0; i < args.Length; i += 2)
        {
            if (args[i] == "--by-hero")
            {
                byHero = true;
                i--;
                continue;
            }

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Option '{args[i]}' needs a value.");
            }

            switch (args[i])
            {
                case "--ui-shot": uiShot = args[i + 1]; break;
                case "--select": select = args[i + 1]; break;
                case "--tab": tab = args[i + 1]; break;
                case "--part": part = int.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--game": game = args[i + 1]; break;
                case "--model": model = args[i + 1]; break;
                case "--selftest": selfTest = args[i + 1]; break;
                case "--addon": addon = args[i + 1]; break;
                case "--outline": outline = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"Unknown option '{args[i]}'.");
            }
        }

        return new AppOptions(game, model, selfTest, addon, outline, uiShot, select, tab, part, byHero);
    }
}
