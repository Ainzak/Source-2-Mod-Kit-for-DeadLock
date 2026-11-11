using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace S2ModKit.Desktop;

/// <summary>
/// A mod's sounds: the sound events it changes or adds and the sound files it ships, each with "Play mod" and, when the game
/// has one, "Play game's" to compare. Events play their sounds one after another on repeated clicks (the game picks among
/// them). Read-only: nothing in the addons folder or the game is changed.
/// </summary>
internal sealed class SoundsPane : UserControl
{
    private static readonly IBrush Muted = AppTheme.Muted;
    private static readonly IBrush Accent = AppTheme.Good;
    private readonly SoundPlayer player;
    private readonly TextBox filter = new() { PlaceholderText = "Search sounds", MinWidth = 260 };
    private readonly TextBlock status = new() { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly ListBox list = new();
    private readonly Dictionary<(Row, bool), int> next = [];
    private IReadOnlyList<Row> rows = [];
    private SoundLibrary? library;

    public SoundsPane(AppSettings settings)
    {
        player = new SoundPlayer(Path.Combine(settings.Data, "sound-cache"));
        var stop = new Button { Content = "Stop" };
        stop.Click += (_, _) => player.Stop();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8), Children = { filter, stop } };
        var top = new StackPanel { Children = { bar, status } };
        var root = new DockPanel { Margin = new Thickness(16, 4, 16, 8) };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(list);
        Content = root;
        list.ItemTemplate = new FuncDataTemplate<Row>((row, _) => row is null ? new StackPanel() : RowView(row));   // null while a recycled row is cleared
        filter.TextChanged += (_, _) => ApplyFilter();
    }

    /// <summary>Reads the mod's sounds in the background and lists them.</summary>
    public async Task ShowAsync(ModEntry mod, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(settings);
        status.Text = "Reading sounds...";
        try
        {
            var opened = await Task.Run(() => SoundLibrary.Open(mod.Files, settings.Game)).ConfigureAwait(true);
            library?.Dispose();
            library = opened;
            rows =
            [
                .. opened.Events.Select(change => new Row(
                    change.Event,
                    $"{(change.New ? "New sound event" : "Changed sound event")} in {change.EventsFile}: {change.ModSounds.Count} sound(s) in the mod"
                        + (change.New ? string.Empty : $", {change.GameSounds.Count} in the game"),
                    change.ModSounds,
                    change.GameSounds)),
                .. opened.Files.Select(file => new Row(
                    file.Path,
                    file.ReplacesGame ? "Replaces the game's sound" : "New sound file (played through the mod's sound events)",
                    [file.Path],
                    file.ReplacesGame ? [file.Path] : [])),
            ];
            status.Text = $"{opened.Events.Count} changed or new sound event(s), {opened.Files.Count} sound file(s). "
                + (opened.Events.Count > 0 ? "Events play their sounds in turn on repeated clicks." : string.Empty);
            ApplyFilter();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            status.Text = "Could not read the sounds: " + error.Message;
        }
    }

    /// <summary>Stops playback, empties the sound cache and closes the game archives (when the pane is left).</summary>
    public void Release()
    {
        player.Release();
        library?.Dispose();
        library = null;
    }

    private void ApplyFilter()
    {
        var text = filter.Text?.Trim() ?? string.Empty;
        list.ItemsSource = text.Length == 0 ? rows : [.. rows.Where(row => row.Title.Contains(text, StringComparison.OrdinalIgnoreCase))];
    }

    private Grid RowView(Row row)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(PlayButton(row, fromGame: false));
        if (row.Game.Count > 0)
        {
            buttons.Children.Add(PlayButton(row, fromGame: true));
        }

        var text = new StackPanel
        {
            Children =
            {
                new SelectableTextBlock { Text = row.Title, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = row.Detail, Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap },
            },
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2) };
        grid.Children.Add(text);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        return grid;
    }

    private Button PlayButton(Row row, bool fromGame)
    {
        var sounds = fromGame ? row.Game : row.Mod;
        var button = new Button
        {
            Content = fromGame ? "▶ Game's" : "▶ Mod",
            IsEnabled = sounds.Count > 0,
        };
        if (!fromGame)
        {
            button.Foreground = Accent;                                     // a null foreground would hide the label
        }

        ToolTip.SetTip(button, fromGame ? "Play the game's original" : "Play the mod's sound");
        button.Click += async (_, _) => await PlayAsync(row, fromGame).ConfigureAwait(true);
        return button;
    }

    private async Task PlayAsync(Row row, bool fromGame)
    {
        if (library is not { } current)
        {
            return;
        }

        var sounds = fromGame ? row.Game : row.Mod;
        var index = next.GetValueOrDefault((row, fromGame)) % sounds.Count;
        next[(row, fromGame)] = index + 1;
        var path = sounds[index];
        status.Text = $"Decoding {path}...";
        try
        {
            var decoded = await Task.Run(() => current.Decode(path, fromGame)).ConfigureAwait(true);
            if (decoded is not { } sound)
            {
                status.Text = $"{path} is in neither the mod nor the game.";
                return;
            }

            var error = player.Play(sound.Data, sound.Extension);
            status.Text = error is null
                ? $"Playing {(fromGame ? "the game's" : "the mod's")} {path} ({sound.Seconds:0.0} s{(sounds.Count > 1 ? $", sound {index + 1} of {sounds.Count}" : string.Empty)})."
                : $"Windows could not play {path} ({sound.Extension}): {error}";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException)
        {
            status.Text = $"Could not decode {path}: {error.Message}";
        }
    }

    private sealed record Row(string Title, string Detail, IReadOnlyList<string> Mod, IReadOnlyList<string> Game);
}
