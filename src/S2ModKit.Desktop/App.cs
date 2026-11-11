using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Platform;

namespace S2ModKit.Desktop;

internal sealed class App : Avalonia.Application
{
    public static AppOptions Options { get; set; } = AppOptions.Parse([]);

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        var fluent = new FluentTheme();
        fluent.Palettes[ThemeVariant.Dark] = AppTheme.Palette;              // accent, window background and text follow the user's theme
        Styles.Add(fluent);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (PlatformSettings?.GetColorValues() is { } system)
        {
            AppTheme.SystemAccent = system.AccentColor1;
        }

        AppTheme.Load();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(Options);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
