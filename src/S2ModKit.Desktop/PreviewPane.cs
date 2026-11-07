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
    private readonly ListBox partList = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBlock partsTitle = new() { Text = "Parts", FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 6) };
    private IReadOnlyList<PartRow> parts = [];
    private Point pressedAt;
    private readonly Button buildEdited = new() { Content = "Build edited mod", IsEnabled = false, Margin = new Thickness(8, 0, 8, 6) };
    private readonly StackPanel colorPanel = new() { Spacing = 2, Margin = new Thickness(8, 6), IsVisible = false };
    private readonly TextBlock colorTitle = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly Slider hue = new() { Minimum = -180, Maximum = 180, Value = 0 };
    private readonly Slider saturation = new() { Minimum = 0, Maximum = 2, Value = 1 };
    private readonly Slider brightness = new() { Minimum = 0, Maximum = 3, Value = 1 };
    private readonly CheckBox paint = new() { Content = "Paint a colour" };
    private readonly ColorPicker paintColor = new();
    private readonly Slider paintStrength = new() { Minimum = 0, Maximum = 1, Value = 1 };
    private readonly ScrollViewer colorScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Button buildRecolor = new() { Content = "Build recolored mod", IsEnabled = false };
    private readonly Avalonia.Threading.DispatcherTimer colorDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool settingSliders;
    private bool sampling;
    private readonly CheckBox onlyLike = new() { Content = "Only colours like" };
    private readonly Border sampleSwatch = new() { Width = 40, Height = 16, CornerRadius = new CornerRadius(3), Background = Brushes.Gray };
    private readonly Button pickColor = new() { Content = "Pick on model" };
    private readonly Slider tolerance = new() { Minimum = 0.02, Maximum = 0.5, Value = 0.15 };
    private readonly Dictionary<int, (S2ModKit.Application.RgbColor Target, float Tolerance)> ranges = [];

    /// <summary>Raised by "Build recolored mod": the shown mod (null for the base game), its model and the recolored parts.</summary>
    public event Action<ModEntry?, string, IReadOnlyDictionary<int, S2ModKit.Application.ColorAdjustment>>? BuildRecolorRequested;

    /// <summary>Raised by "Build edited mod": the shown mod (null for the base game), its model and the hidden parts.</summary>
    public event Action<ModEntry?, string, IReadOnlySet<int>>? BuildEditedRequested;
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

        // Two full-height columns: the parts list and, beside it, the colour of the selected part (no stacking, no squeezed list).
        var side = new Grid { ColumnDefinitions = new ColumnDefinitions("240,1,270") };
        var partsColumn = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        partsColumn.Children.Add(new StackPanel
        {
            Children =
            {
                partsTitle,
                Wrap(Button("Show all", () => SetHidden(parts.Select(part => part.Index), false)), Button("Hide selected", () => ToggleSelected()), Button("Cancel all changes", CancelAll)),
                new TextBlock { Text = "Hover a part to highlight it, or click the model to find one. Untick to hide.", Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 8, 6) },
                buildEdited,
            },
        });
        Grid.SetRow(partList, 1);
        partsColumn.Children.Add(partList);
        side.Children.Add(partsColumn);
        var divider = new Border { Background = new SolidColorBrush(Color.FromRgb(60, 62, 66)) };
        Grid.SetColumn(divider, 1);
        side.Children.Add(divider);
        colorPanel.Children.Add(colorTitle);
        foreach (var (label, slider) in new[] { ("Hue shift", hue), ("Saturation", saturation), ("Brightness", brightness) })
        {
            colorPanel.Children.Add(Labelled(label, slider));
        }

        colorPanel.Children.Add(paint);
        colorPanel.Children.Add(paintColor);
        colorPanel.Children.Add(Labelled("Strength", paintStrength));
        colorPanel.Children.Add(Wrap(onlyLike, sampleSwatch, pickColor));
        colorPanel.Children.Add(Labelled("Tolerance", tolerance));
        colorPanel.Children.Add(Wrap(Button("Reset part", ResetColor), buildRecolor));
        var colorHint = new TextBlock { Text = "Select a part to recolor it.", Foreground = Brushes.Gray, Margin = new Thickness(8, 10), TextWrapping = TextWrapping.Wrap };
        colorPanel.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                colorHint.IsVisible = !colorPanel.IsVisible;
            }
        };
        colorScroll.Content = new StackPanel { Children = { colorHint, colorPanel } };   // scrolls only in a very small window
        Grid.SetColumn(colorScroll, 2);
        side.Children.Add(colorScroll);
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);
        root.Children.Add(view);
        partList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<PartRow>((row, _) => row is null ? new StackPanel() : PartItem(row));   // null while a recycled row is cleared
        partList.SelectionChanged += (_, _) =>
        {
            preview.Highlight = -1;                                         // the selected part is being recoloured: show its real colours
            ShowColor();
        };
        foreach (var slider in new[] { hue, saturation, brightness, paintStrength })
        {
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty && !settingSliders)
                {
                    colorDelay.Stop();
                    colorDelay.Start();                                     // apply when the slider rests
                }
            };
        }

        pickColor.Click += (_, _) => SetSampling(!sampling);
        paintColor.ColorChanged += _ =>
        {
            if (!settingSliders)
            {
                paint.IsChecked = true;                                     // choosing a colour means painting with it
                colorDelay.Stop();
                colorDelay.Start();
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape && sampling)
            {
                SetSampling(false);
            }
        };
        onlyLike.IsCheckedChanged += (_, _) =>
        {
            if (!settingSliders)
            {
                ApplyRange();
            }
        };
        tolerance.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !settingSliders)
            {
                ApplyRange();
            }
        };
        paint.IsCheckedChanged += (_, _) =>
        {
            if (!settingSliders)
            {
                ApplyColor();
            }
        };
        colorDelay.Tick += (_, _) =>
        {
            colorDelay.Stop();
            ApplyColor();
        };
        buildRecolor.Click += (_, _) =>
        {
            if (model.SelectedItem is string modelPath && preview.PartColors.Count > 0 && compare.SelectedIndex <= (mod is null ? 0 : 1))
            {
                BuildRecolorRequested?.Invoke(compare.SelectedIndex == 0 ? mod : null, modelPath, new Dictionary<int, S2ModKit.Application.ColorAdjustment>(preview.PartColors));
            }
        };
        buildEdited.Click += (_, _) =>
        {
            if (model.SelectedItem is string modelPath && compare.SelectedIndex <= (mod is null ? 0 : 1))
            {
                BuildEditedRequested?.Invoke(compare.SelectedIndex == 0 ? mod : null, modelPath, preview.Hidden.ToHashSet());
            }
        };
        Content = root;
        ToolTip.SetTip(withLoaded, "Off: this mod alone on the unmodded game.\nOn: also load every mod the game currently loads (enabled pakNN_dir.vpk files)"
            + " beneath it, to see how this mod looks together with them, for example with a texture or animation mod for the same hero.");
        view.AddHandler(PointerPressedEvent, (_, e) =>
        {
            pressedAt = e.GetPosition(view);
            preview.BeginDrag(pressedAt, !e.GetCurrentPoint(view).Properties.IsLeftButtonPressed);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        view.AddHandler(PointerMovedEvent, (_, e) => preview.DragTo(e.GetPosition(view), view.Bounds.Height),
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        view.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            preview.EndDrag();
            var at = e.GetPosition(view);
            if (sampling && e.InitialPressMouseButton == Avalonia.Input.MouseButton.Left && Math.Abs(at.X - pressedAt.X) + Math.Abs(at.Y - pressedAt.Y) < 4)
            {
                SetSampling(false);
                preview.RequestSample(at, sampled =>
                {
                    if (sampled is not { } hit)
                    {
                        return;
                    }

                    ranges[hit.Part] = (hit.Color, (float)tolerance.Value);
                    partList.SelectedItem = parts.FirstOrDefault(part => part.Index == hit.Part);
                    settingSliders = true;
                    onlyLike.IsChecked = true;
                    settingSliders = false;
                    ApplyRange();
                });
            }
            else if (e.InitialPressMouseButton == Avalonia.Input.MouseButton.Left && Math.Abs(at.X - pressedAt.X) + Math.Abs(at.Y - pressedAt.Y) < 4)
            {
                preview.RequestPick(at, picked =>                          // a click, not a drag: select the part under it
                {
                    partList.SelectedItem = picked is { } index ? parts.FirstOrDefault(part => part.Index == index) : null;
                    if (partList.SelectedItem is { } item)
                    {
                        partList.ScrollIntoView(item);
                    }
                });
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
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
        parts = [.. session.Parts.Select((part, index) => new PartRow(index, part))];
        colorPanel.IsVisible = false;
        ranges.Clear();
        buildEdited.IsEnabled = false;
        partsTitle.Text = $"Parts ({parts.Count})";
        partList.ItemsSource = parts;
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

    /// <summary>The parts currently hidden in the preview (indices into the session's parts).</summary>
    public IReadOnlyCollection<int> HiddenParts => preview.Hidden;

    /// <summary>The parts of the open model, in draw order.</summary>
    public IReadOnlyList<S2ModKit.Application.PreviewPart> Parts => [.. parts.Select(row => row.Part)];

    private StackPanel PartItem(PartRow row)
    {
        var visible = new CheckBox { IsChecked = !preview.Hidden.Contains(row.Index), VerticalAlignment = VerticalAlignment.Center };
        visible.IsCheckedChanged += (_, _) => SetHidden([row.Index], visible.IsChecked != true);
        var material = row.Part.Material[(row.Part.Material.LastIndexOf('/') + 1)..];
        var item = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                visible,
                new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = $"{row.Index + 1}. {material}", FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 230 },
                        new TextBlock { Text = $"{row.Part.Mesh} - {row.Part.Indices / 3:N0} triangles", Foreground = Brushes.Gray, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 230 },
                    },
                },
            },
        };
        item.PointerEntered += (_, _) => preview.Highlight = ReferenceEquals(partList.SelectedItem, row) ? -1 : row.Index;   // never over the part being recoloured
        item.PointerExited += (_, _) => preview.Highlight = -1;            // only hovering highlights; selection never pulses
        return item;
    }

    private void SetHidden(IEnumerable<int> indices, bool hidden)
    {
        foreach (var index in indices.ToArray())
        {
            _ = hidden ? preview.Hidden.Add(index) : preview.Hidden.Remove(index);
        }

        partList.ItemsSource = null;                                        // redraw the check boxes
        partList.ItemsSource = parts;
        buildEdited.IsEnabled = preview.Hidden.Count > 0 && compare.SelectedIndex <= (mod is null ? 0 : 1);   // this mod, or the base game
    }

    private void ShowColor()
    {
        if (partList.SelectedItem is not PartRow row || preview.Session?.PartColorTexture(row.Index) is not { } texture)
        {
            colorPanel.IsVisible = false;
            return;
        }

        colorPanel.IsVisible = true;
        colorTitle.Text = $"Colour of part {row.Index + 1} ({texture[(texture.LastIndexOf('/') + 1)..]})";
        var change = preview.PartColors.GetValueOrDefault(row.Index) ?? new S2ModKit.Application.ColorAdjustment();
        settingSliders = true;
        hue.Value = change.HueShiftDegrees;
        saturation.Value = change.Saturation;
        brightness.Value = change.Brightness;
        paint.IsChecked = change.Colorize is not null;
        if (change.Colorize is { } painted)
        {
            paintColor.Show(Color.FromRgb((byte)(painted.R * 255), (byte)(painted.G * 255), (byte)(painted.B * 255)));
            paintStrength.Value = change.ColorizeStrength;
        }

        onlyLike.IsChecked = ranges.ContainsKey(row.Index);
        if (ranges.TryGetValue(row.Index, out var range))
        {
            tolerance.Value = range.Tolerance;
            sampleSwatch.Background = new SolidColorBrush(Color.FromRgb((byte)(range.Target.R * 255), (byte)(range.Target.G * 255), (byte)(range.Target.B * 255)));
        }

        settingSliders = false;

        buildRecolor.IsEnabled = preview.PartColors.Count > 0 && compare.SelectedIndex <= (mod is null ? 0 : 1);
    }

    private void ApplyColor()
    {
        if (partList.SelectedItem is not PartRow row)
        {
            return;
        }

        var color = paintColor.Color;
        var change = new S2ModKit.Application.ColorAdjustment((float)hue.Value, (float)saturation.Value, (float)brightness.Value,
            Colorize: paint.IsChecked == true ? new S2ModKit.Application.RgbColor(color.R / 255f, color.G / 255f, color.B / 255f) : null,
            ColorizeStrength: (float)paintStrength.Value,
            ColorizeLightness: paint.IsChecked == true);                  // painting black or white works too
        var unchanged = change == new S2ModKit.Application.ColorAdjustment();
        if (unchanged)
        {
            preview.PartColors.Remove(row.Index);
        }
        else
        {
            preview.PartColors[row.Index] = change;
        }

        preview.RequestPartColor(row.Index, unchanged ? null : change);
        buildRecolor.IsEnabled = preview.PartColors.Count > 0 && compare.SelectedIndex <= (mod is null ? 0 : 1);
    }

    private void ApplyRange()
    {
        if (partList.SelectedItem is not PartRow row || !ranges.TryGetValue(row.Index, out var range))
        {
            return;
        }

        ranges[row.Index] = (range.Target, (float)tolerance.Value);
        sampleSwatch.Background = new SolidColorBrush(Color.FromRgb((byte)(range.Target.R * 255), (byte)(range.Target.G * 255), (byte)(range.Target.B * 255)));
        preview.RequestColorRange(row.Index, onlyLike.IsChecked == true ? range.Target : null, (float)tolerance.Value);
    }

    private void ResetColor()
    {
        if (partList.SelectedItem is PartRow row)
        {
            preview.PartColors.Remove(row.Index);
            ranges.Remove(row.Index);
            preview.RequestColorRange(row.Index, null, 0);
            preview.RequestPartColor(row.Index, null);
            ShowColor();
        }
    }

    private void ToggleSelected()
    {
        if (partList.SelectedItem is PartRow row)
        {
            SetHidden([row.Index], !preview.Hidden.Contains(row.Index));
        }
    }

    private void SetSampling(bool on)
    {
        sampling = on;
        pickColor.Content = on ? "Cancel (Esc)" : "Pick on model";
    }

    /// <summary>Drops every recolor, colour range and hidden part since the model opened (nothing was built).</summary>
    private void CancelAll()
    {
        foreach (var part in preview.PartColors.Keys.ToArray())
        {
            preview.RequestPartColor(part, null);
        }

        foreach (var part in ranges.Keys.ToArray())
        {
            preview.RequestColorRange(part, null, 0);
        }

        preview.PartColors.Clear();
        ranges.Clear();
        SetSampling(false);
        SetHidden(parts.Select(part => part.Index), false);
        ShowColor();
    }

    /// <summary>A label and a slider on one line, to keep the colour column short.</summary>
    private static Grid Labelled(string label, Control slider)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("72,*"), Margin = new Thickness(0, -4) };
        row.Children.Add(new TextBlock { Text = label, Foreground = Brushes.Gray, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        return row;
    }

    private static WrapPanel Wrap(params Control[] children)
    {
        var row = new WrapPanel { ItemSpacing = 6, LineSpacing = 6, Margin = new Thickness(8, 4) };
        foreach (var child in children)
        {
            row.Children.Add(child);
        }

        return row;
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

/// <summary>A part card: its index in draw order and what the preview reports about it.</summary>
internal sealed record PartRow(int Index, S2ModKit.Application.PreviewPart Part);
