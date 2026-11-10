using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace S2ModKit.Desktop;

/// <summary>What the Settings window can do in the main window: each action returns a short message for the window, or null.</summary>
internal sealed record SettingsActions(
    Func<AppSettings> Current,
    Action<AppSettings> Apply,
    Func<Task<string?>> PickGame,
    Func<Task<string?>> PickData,
    Func<Task<string?>> OpenAddons,
    Func<Task<string?>> Refresh,
    Func<Task<string?>> CheckModUpdates,
    Func<Task<string?>> ShowHiddenConflicts,
    Func<Task<string?>> CheckAppUpdate,
    Func<Task<string?>> OpenLog,
    Func<Task<string?>> CopyLog,
    IReadOnlyList<int> PreviewSpeeds);

/// <summary>
/// Settings in one window instead of a drop-down: folders, mods, the preview, updates and the log, each group with its choices and
/// buttons. Choices are saved as they change.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(148, 155, 164));
    private readonly SettingsActions actions;
    private readonly SelectableTextBlock gamePath = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    private readonly SelectableTextBlock dataPath = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    private readonly SelectableTextBlock status = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };

    public SettingsWindow(SettingsActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        this.actions = actions;
        Title = "Settings";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 860;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var settings = actions.Current();

        var checkOnStart = new CheckBox { Content = "Look for app updates and new mod files on start", IsChecked = settings.CheckUpdates };
        checkOnStart.IsCheckedChanged += (_, _) => actions.Apply(actions.Current() with { CheckUpdates = checkOnStart.IsChecked == true });
        var autoPreview = new CheckBox { Content = "Open the 3D preview automatically when a model mod is picked", IsChecked = settings.PreviewAuto };
        autoPreview.IsCheckedChanged += (_, _) => actions.Apply(actions.Current() with { PreviewAuto = autoPreview.IsChecked == true });
        var speed = new ComboBox { ItemsSource = actions.PreviewSpeeds.Select(fps => $"{fps} fps").ToArray(), MinWidth = 110 };
        speed.SelectedIndex = Math.Max(0, actions.PreviewSpeeds.ToList().IndexOf(settings.PreviewFps));
        speed.SelectionChanged += (_, _) =>
        {
            if (speed.SelectedIndex >= 0)
            {
                actions.Apply(actions.Current() with { PreviewFps = actions.PreviewSpeeds[speed.SelectedIndex] });
            }
        };

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 16,
                Children =
                {
                    Group("Folders",
                        Row("Game", gamePath, Action("Change...", actions.PickGame, ShowPaths)),
                        Row("Data", dataPath, Action("Move...", actions.PickData, ShowPaths)),
                        Note("The data folder keeps install records, edits, profiles and the log. Moving it copies everything to an empty folder; the old one stays as it was."),
                        Buttons(Action("Open addons folder", actions.OpenAddons))),
                    Group("Mods",
                        Buttons(Action("Refresh the list", actions.Refresh), Action("Look for mod updates on GameBanana", actions.CheckModUpdates)),
                        Buttons(Action("Show hidden conflicts", actions.ShowHiddenConflicts)),
                        Note("Conflicts marked \"Fine, stop warning\" stay hidden until the two mods share more files.")),
                    Group("3D preview",
                        autoPreview,
                        Row("Frame rate", speed),
                        Note("30 fps uses less GPU while the preview runs.")),
                    Group("Updates",
                        checkOnStart,
                        Buttons(Action("Look for app updates now", actions.CheckAppUpdate)),
                        Note($"This is S2ModKit {AppUpdates.Current}.")),
                    Group("Log",
                        Buttons(Action("Open log", actions.OpenLog), Action("Copy log", actions.CopyLog)),
                        Note("Each session writes a log in the data folder (the last five are kept). Nothing is sent anywhere.")),
                    status,
                    close,
                },
            },
        };
        ShowPaths();
    }

    private void ShowPaths()
    {
        var settings = actions.Current();
        gamePath.Text = settings.Game;
        dataPath.Text = settings.Data;
    }

    /// <summary>A button that runs <paramref name="run"/> (one at a time), shows its message and then <paramref name="after"/>.</summary>
    private Button Action(string text, Func<Task<string?>> run, Action? after = null)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                status.Text = await run() ?? string.Empty;
                after?.Invoke();
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }

    private static StackPanel Group(string title, params Control[] children)
    {
        var group = new StackPanel { Spacing = 8 };
        group.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold });
        foreach (var child in children)
        {
            group.Children.Add(child);
        }

        return group;
    }

    private static Grid Row(string label, Control value, Control? action = null)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(value, 1);
        value.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(value);
        if (action is not null)
        {
            Grid.SetColumn(action, 2);
            row.Children.Add(action);
        }

        return row;
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var button in buttons)
        {
            row.Children.Add(button);
        }

        return row;
    }

    private static TextBlock Note(string text) => new() { Text = text, Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap };
}
