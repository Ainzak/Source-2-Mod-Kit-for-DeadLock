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
            var mods = new ModCatalog(AppSettings.Load(null)).LoadAsync().GetAwaiter().GetResult();
            File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(mods.Select(mod => new
            {
                mod.Name,
                Source = mod.Source.ToString(),
                mod.Loaded,
                Files = mod.Files.Select(Path.GetFileName),
                mod.Models,
                mod.KindText,
                Entries = mod.Entries.Count,
                Install = mod.Install?.Receipt.Status,
            })));
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

internal sealed record AppOptions(string? Game, string Model, string? SelfTest, string? Addon = null, float? Outline = null)
{
    public static AppOptions Parse(string[] args)
    {
        string? game = null, selfTest = null, addon = null;
        float? outline = null;
        var model = "models/heroes_wip/wraith/wraith.vmdl";
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--game": game = args[i + 1]; break;
                case "--model": model = args[i + 1]; break;
                case "--selftest": selfTest = args[i + 1]; break;
                case "--addon": addon = args[i + 1]; break;
                case "--outline": outline = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"Unknown option '{args[i]}'.");
            }
        }

        return new AppOptions(game, model, selfTest, addon, outline);
    }
}
