using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using S2ModKit.Adapters.Preview;

namespace S2ModKit.Desktop;

/// <summary>
/// Preview of one mod on its model: compare this mod / vanilla / another mod on the same model (camera, clip and time
/// kept), alone or with the other enabled mods; hero-select lighting (a studio light was rejected); animation picker with a
/// filter, play/pause, timeline; camera presets. Opening a session takes a moment; the overlay says so.
/// </summary>
internal sealed class PreviewPane : UserControl
{
    private const string Vanilla = "Vanilla";
    private readonly AppSettings settings;
    private readonly PreviewControl preview = new();
    private readonly TextBlock overlay = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 16 };
    private readonly ComboBox compare = new() { MinWidth = 260 };
    private readonly ComboBox model = new() { MinWidth = 260 };
    private readonly CheckBox withLoaded = new() { Content = "Include my enabled mods" };
    private readonly TextBox filter = new() { PlaceholderText = "Filter animations", Width = 180 };
    private readonly ComboBox animations = new() { MinWidth = 320 };
    private readonly ToggleButton play = new() { Content = "Play", IsChecked = true, Width = 70 };
    private readonly Slider timeline = new() { Minimum = 0, Maximum = 1, MinWidth = 300 };
    private readonly TextBlock timeText = new() { VerticalAlignment = VerticalAlignment.Center, Width = 110 };
    private readonly TextBlock stats = new() { Foreground = Brushes.Gray };
    private IReadOnlyList<string> allAnimations = [];
    private ModEntry? mod;
    private IReadOnlyList<ModEntry> others = [];
    private bool updating;

    public PreviewPane(AppSettings settings)
    {
        this.settings = settings;
        var top = Row(new TextBlock { Text = "Show", VerticalAlignment = VerticalAlignment.Center }, compare, model, withLoaded);
        var anim = Row(filter, animations, play, timeline, timeText);
        var cameras = Row([.. PreviewControl.Presets.Select(preset => Button(preset.Name, () => preview.SetCamera(preset))), stats]);
        var view = new Grid { Background = new SolidColorBrush(Color.FromRgb(30, 31, 34)) };
        view.Children.Add(preview);
        view.Children.Add(overlay);
        var root = new DockPanel();
        foreach (var bar in new[] { top, anim, cameras })
        {
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
        }

        root.Children.Add(view);
        Content = root;
        ToolTip.SetTip(withLoaded, "Off: this mod alone on the unmodded game.\nOn: also load every mod the game currently loads (enabled pakNN_dir.vpk files)"
            + " beneath it, to see how this mod looks together with them, for example with a texture or animation mod for the same hero.");
        view.AddHandler(PointerPressedEvent, (_, e) => preview.BeginDrag(e.GetPosition(view), !e.GetCurrentPoint(view).Properties.IsLeftButtonPressed),
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        view.AddHandler(PointerMovedEvent, (_, e) => preview.DragTo(e.GetPosition(view), view.Bounds.Height),
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        view.AddHandler(PointerReleasedEvent, (_, _) => preview.EndDrag(),
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        view.AddHandler(PointerWheelChangedEvent, (_, e) => preview.Zoom(e.Delta.Y),
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);

        compare.SelectionChanged += (_, _) => Reopen();
        model.SelectionChanged += (_, _) => Reopen();
        withLoaded.IsCheckedChanged += (_, _) => Reopen();
        filter.TextChanged += (_, _) => ApplyFilter();
        animations.SelectionChanged += (_, _) =>
        {
            if (!updating && animations.SelectedItem is string clip)
            {
                preview.SetClip(clip);
                play.IsChecked = true;
            }
        };
        play.IsCheckedChanged += (_, _) =>
        {
            preview.Playing = play.IsChecked == true;
            play.Content = preview.Playing ? "Pause" : "Play";
        };
        timeline.PropertyChanged += (_, e) =>
        {
            if (!updating && e.Property == RangeBase.ValueProperty)
            {
                preview.Seek((float)timeline.Value);
                play.IsChecked = false;
            }
        };
        preview.Opened += OnOpened;
        preview.Progress += (t, length) =>
        {
            updating = true;
            timeline.Maximum = Math.Max(length, 0.001);
            timeline.Value = t;
            timeText.Text = $"{t:F2} / {length:F2} s";
            updating = false;
        };
        preview.FrameStats += (fps, w, h) => stats.Text = $"   {fps:F0} fps, {w} x {h}, {preview.GlInfo}";
    }

    public PreviewControl Control => preview;

    /// <summary>Closes the preview session (it keeps the shown archives open) and waits until it is released.</summary>
    public async Task ReleaseAsync() => await Task.WhenAny(preview.CloseAsync(), Task.Delay(3000));

    /// <summary>Shows <paramref name="entry"/> (null: vanilla <paramref name="vanillaModel"/>); others on the same model can be compared.</summary>
    public void Show(ModEntry? entry, IReadOnlyList<ModEntry> all, string? vanillaModel = null)
    {
        mod = entry;
        updating = true;
        model.ItemsSource = entry?.Models ?? (vanillaModel is null ? [] : [vanillaModel]);
        model.SelectedIndex = 0;
        model.IsVisible = (entry?.Models.Count ?? 1) > 1;
        others = entry is null ? [] : [.. all.Where(other => other != entry && other.Models.Intersect(entry.Models, StringComparer.OrdinalIgnoreCase).Any())];
        compare.ItemsSource = entry is null ? new[] { Vanilla } : (string[])["This mod", Vanilla, .. others.Select(other => other.Name)];
        compare.SelectedIndex = 0;
        updating = false;
        Reopen();
    }

    private void Reopen()
    {
        if (updating || model.SelectedItem is not string modelPath)
        {
            return;
        }

        var shown = compare.SelectedIndex switch
        {
            0 => mod,
            1 => null,
            var i => others[i - 2],
        };
        var archives = new List<string>();
        if (shown is not null)
        {
            archives.AddRange(shown.Files);
        }

        if (withLoaded.IsChecked == true)                                   // the other loaded mods below it, in game order
        {
            archives.AddRange(Directory.EnumerateFiles(settings.AddonsRoot, "pak*_dir.vpk").Order(StringComparer.OrdinalIgnoreCase)
                .Where(file => !archives.Contains(file, StringComparer.OrdinalIgnoreCase)));
        }

        archives.Add(Path.Combine(settings.Game, "citadel", "pak01_dir.vpk"));
        archives.Add(Path.Combine(settings.Game, "core", "pak01_dir.vpk"));
        archives.Add(Path.Combine(settings.Game, "citadel", "maps", "ui", "hero_select.vpk"));

        overlay.Text = $"Loading {(shown?.Name ?? Vanilla)}...";
        overlay.IsVisible = true;
        preview.Open(new PreviewSessionOptions(archives, modelPath, SceneMap: "maps/ui/hero_select.vmap"));
    }

    private void OnOpened(PreviewSession? session, TimeSpan elapsed, string? error)
    {
        if (session is null)
        {
            overlay.Text = $"Could not open the preview: {error}";
            return;
        }

        overlay.IsVisible = false;
        stats.Text = $"   opened in {elapsed.TotalSeconds:F1} s";
        allAnimations = [.. session.Animations.Order(StringComparer.OrdinalIgnoreCase)];
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        updating = true;
        var text = filter.Text ?? string.Empty;
        var shown = allAnimations.Where(name => name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        animations.ItemsSource = shown;
        animations.SelectedItem = preview.ClipName is { } current && shown.Contains(current) ? current : null;
        updating = false;
    }

    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 6) };
        foreach (var child in children)
        {
            row.Children.Add(child);
        }

        return row;
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => action();
        return button;
    }
}
