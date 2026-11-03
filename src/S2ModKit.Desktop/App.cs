using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace S2ModKit.Desktop;

internal sealed class App : Avalonia.Application
{
    public static AppOptions Options { get; set; } = AppOptions.Parse([]);

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(Options);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
