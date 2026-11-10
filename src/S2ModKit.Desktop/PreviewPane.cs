using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using S2ModKit.Adapters.Preview;
using S2ModKit.Application;

namespace S2ModKit.Desktop;

/// <summary>
/// Preview of one mod on its model: compare this mod / vanilla / another mod on the same model (camera, clip and time
/// kept), alone or with the other enabled mods; hero-select lighting; animation picker with a filter, play/pause, timeline;
/// camera presets over the view. Beside it the part tools: the picked part above three tabs (Parts: hide and show; Colour:
/// recolor; Shape: size, move and rotate where the CLI's checked operations allow), and one "Build edited mod" for all changes.
/// </summary>
internal sealed partial class PreviewPane : UserControl
{
    private const string Vanilla = "Vanilla";
    private static readonly IBrush Muted = Brushes.Gray;
    private static readonly IBrush Good = new SolidColorBrush(Color.FromRgb(90, 190, 100));
    private static readonly IBrush Warn = new SolidColorBrush(Color.FromRgb(240, 170, 70));
    private readonly AppSettings settings;
    private readonly PreviewControl preview = new();
    private readonly TextBlock overlay = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 16 };
    private readonly Button loadPreview = new() { Content = "Show 3D preview", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 16, Padding = new Thickness(18, 10), IsVisible = false };
    private bool previewWanted;

    /// <summary>Opens the 3D preview as soon as a mod is shown (false: only after "Show 3D preview", so switching mods stays quick and
    /// the mod's file is not held open).</summary>
    public bool OpenAutomatically { get; set; }
    private readonly ComboBox compare = new() { MinWidth = 220 };
    private readonly ComboBox model = new() { MinWidth = 220 };
    private readonly CheckBox withLoaded = new() { Content = "Include my enabled mods" };
    private readonly TextBox filter = new() { PlaceholderText = "Filter animations", Width = 160 };
    private readonly ComboBox animations = new() { MinWidth = 280 };
    private readonly ToggleButton play = new() { Content = "Pause", IsChecked = true, Width = 70 };
    private readonly Slider timeline = new() { Minimum = 0, Maximum = 1, MinWidth = 200 };
    private readonly TextBlock timeText = new() { VerticalAlignment = VerticalAlignment.Center, Width = 100 };
    private readonly TextBlock stats = new() { Foreground = Muted, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8) };

    // part tools
    private readonly ComboBox partPicker = new() { MinWidth = 250 };
    private readonly ListBox partList = new() { SelectionMode = SelectionMode.Single };
    private readonly ToggleButton partsTab = new() { Content = "Parts" };
    private readonly ToggleButton colourTab = new() { Content = "Colour" };
    private readonly ToggleButton shapeTab = new() { Content = "Shape" };
    private readonly ContentControl toolHost = new();
    private readonly TextBlock changeSummary = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private readonly Button buildEdited = new() { Content = "Build edited mod", IsEnabled = false };
    private IReadOnlyList<PartRow> parts = [];
    private PartRow? selected;
    private bool selecting;
    private Point pressedAt;

    // colour
    private readonly StackPanel colorPanel = new() { Spacing = 2, Margin = new Thickness(8, 6), IsVisible = false };
    private readonly TextBlock colorTitle = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock colorHint = new() { Text = "Pick a part to recolor it.", Foreground = Muted, Margin = new Thickness(8, 10), TextWrapping = TextWrapping.Wrap };
    private readonly Slider hue = new() { Minimum = -180, Maximum = 180, Value = 0 };
    private readonly Slider saturation = new() { Minimum = 0, Maximum = 2, Value = 1 };
    private readonly Slider brightness = new() { Minimum = 0, Maximum = 3, Value = 1 };
    private readonly Slider shine = new() { Minimum = 0, Maximum = 1, Value = 0 };
    private readonly TextBlock shineNote = new() { Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox paint = new() { Content = "Paint a colour" };
    private readonly ColorPicker paintColor = new();
    private readonly Slider paintStrength = new() { Minimum = 0, Maximum = 1, Value = 1 };
    private readonly Avalonia.Threading.DispatcherTimer colorDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool settingSliders;
    private bool sampling;
    private readonly CheckBox onlyLike = new() { Content = "Only colours like" };
    private readonly Border sampleSwatch = new() { Width = 40, Height = 16, CornerRadius = new CornerRadius(3), Background = Brushes.Gray };
    private readonly Button pickColor = new() { Content = "Pick on model" };
    private readonly Slider tolerance = new() { Minimum = 0.02, Maximum = 0.5, Value = 0.15 };
    private readonly Dictionary<int, (RgbColor Target, float Tolerance)> ranges = [];

    // shape
    private readonly TextBlock shapeStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private readonly CheckBox keepProportions = new() { Content = "Keep proportions", IsChecked = true };
    private readonly Slider[] size = [Shape(0.25, 4, 1), Shape(0.25, 4, 1), Shape(0.25, 4, 1)];        // width (Y), height (Z), depth (X)
    private readonly Slider[] move = [Shape(-40, 40, 0), Shape(-40, 40, 0), Shape(-40, 40, 0)];        // left/right (Y), up/down (Z), fwd/back (X)
    private readonly Slider[] turn = [Shape(-180, 180, 0), Shape(-180, 180, 0), Shape(-180, 180, 0)];  // tilt (about Y), turn (about Z), roll (about X)
    private readonly ComboBox pivot = new() { ItemsSource = new[] { "the part's centre", "the part's base", "the part's top" }, SelectedIndex = 0, MinWidth = 170 };
    private readonly StackPanel shapeControls = new() { Spacing = 2 };
    private readonly Avalonia.Threading.DispatcherTimer shapeDelay = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private IReadOnlyDictionary<int, PartShapeCapability> refusedShapes = new Dictionary<int, PartShapeCapability>();   // parts the direct reshape refuses
    private string? modelRefusal = "Checking which parts can be reshaped...";
    private readonly Dictionary<int, string> previewRefusals = [];
    private int capabilityRequest;

    /// <summary>Raised by "Build edited mod": the shown mod (null for the base game), its model, the hidden parts, the recolored
    /// parts and the reshaped parts.</summary>
    public event Action<ModEntry?, string, IReadOnlySet<int>, IReadOnlyDictionary<int, ColorAdjustment>, IReadOnlyDictionary<int, PartShape>, IReadOnlyDictionary<string, EffectColorChange>, IReadOnlyDictionary<int, float>, IReadOnlyList<S2ModKit.Adapters.Source2.AddedEffect>>? BuildRequested;

    private IReadOnlyList<string> allAnimations = [];
    private readonly StackPanel abilityBar = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
    private Task<IReadOnlyDictionary<string, IReadOnlyList<HeroAbility>>?>? abilityData;

    /// <summary>The ability buttons of the open model: slot, name and the clip each plays (null: no matching clip, no button).</summary>
    public IReadOnlyList<(int Slot, string Name, IReadOnlyList<string> Clips)> AbilityClips { get; private set; } = [];
    private ModEntry? mod;
    private IReadOnlyList<ModEntry> others = [];
    private bool updating;

    public PreviewPane(AppSettings settings)
    {
        this.settings = settings;
        preview.MaxFps = settings.PreviewFps;
        var top = Row(new TextBlock { Text = "Show", VerticalAlignment = VerticalAlignment.Center }, compare, model, withLoaded);
        var anim = Row(filter, animations, play, timeline, timeText);
        var cameras = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        foreach (var preset in PreviewControl.Presets)
        {
            cameras.Children.Add(Button(preset.Name, () => preview.SetCamera(preset)));
        }

        var view = new Grid { Background = new SolidColorBrush(Color.FromRgb(30, 31, 34)) };
        view.Children.Add(preview);
        view.Children.Add(overlay);
        view.Children.Add(loadPreview);
        loadPreview.Click += (_, _) =>
        {
            previewWanted = true;
            loadPreview.IsVisible = false;
            Reopen();
        };
        view.Children.Add(cameras);
        view.Children.Add(abilityBar);
        view.Children.Add(stats);
        var root = new DockPanel();
        foreach (var bar in new[] { top, anim })
        {
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
        }

        var tools = BuildTools();
        DockPanel.SetDock(tools, Dock.Right);
        root.Children.Add(tools);
        root.Children.Add(view);
        Content = root;

        partList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<PartRow>((row, _) => row is null ? new StackPanel() : PartItem(row));   // null while a recycled row is cleared
        partPicker.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<PartRow>((row, _) =>
        {
            var text = new TextBlock { Text = row is null ? string.Empty : $"{row.Label}. {row.Name}", Background = Brushes.Transparent };
            if (row is not null)
            {
                text.PointerEntered += (_, _) => preview.Highlight = partPicker.IsDropDownOpen && !ReferenceEquals(selected, row) ? row.Index : -1;   // hovering a dropdown item shows that part
                text.PointerExited += (_, _) => preview.Highlight = -1;
            }

            return text;
        });
        partPicker.DropDownClosed += (_, _) => preview.Highlight = -1;
        partList.SelectionChanged += (_, _) => SelectPart(partList.SelectedItem as PartRow);
        partPicker.SelectionChanged += (_, _) => SelectPart(partPicker.SelectedItem as PartRow);
        shine.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !settingSliders && selected is { } row)
            {
                if (shine.Value > 0.005)
                {
                    preview.PartShines[row.Index] = (float)shine.Value;
                }
                else
                {
                    preview.PartShines.Remove(row.Index);
                }

                preview.RequestPartShine(row.Index, shine.Value > 0.005 ? (float)shine.Value : null);
                UpdateSummary();
            }
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
        for (var axis = 0; axis < 3; axis++)
        {
            var index = axis;
            size[axis].PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty && !settingSliders)
                {
                    if (keepProportions.IsChecked == true)
                    {
                        settingSliders = true;                              // one size drives the other two
                        foreach (var other in size.Where((_, i) => i != index))
                        {
                            other.Value = size[index].Value;
                        }

                        settingSliders = false;
                    }

                    QueueShape();
                }
            };
            move[axis].PropertyChanged += (_, e) => ShapeSliderChanged(e);
            turn[axis].PropertyChanged += (_, e) => ShapeSliderChanged(e);
        }

        pivot.SelectionChanged += (_, _) => QueueShape();
        shapeDelay.Tick += (_, _) =>
        {
            shapeDelay.Stop();
            ApplyShape();
        };
        buildEdited.Click += (_, _) =>
        {
            if (model.SelectedItem is string modelPath && CanBuild)
            {
                BuildRequested?.Invoke(compare.SelectedIndex == 0 ? mod : null, modelPath, preview.Hidden.ToHashSet(),
                    new Dictionary<int, ColorAdjustment>(preview.PartColors), new Dictionary<int, PartShape>(preview.PartShapes), EffectChanges, new Dictionary<int, float>(preview.PartShines), AddedEffects);
            }
        };
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
            var click = e.InitialPressMouseButton == Avalonia.Input.MouseButton.Left && Math.Abs(at.X - pressedAt.X) + Math.Abs(at.Y - pressedAt.Y) < 4;
            if (click && sampling)
            {
                SetSampling(false);
                preview.RequestSample(at, sampled =>
                {
                    if (sampled is not { } hit)
                    {
                        return;
                    }

                    ranges[hit.Part] = (hit.Color, (float)tolerance.Value);
                    SelectPart(parts.FirstOrDefault(part => part.Index == hit.Part));
                    settingSliders = true;
                    onlyLike.IsChecked = true;
                    settingSliders = false;
                    ApplyRange();
                });
            }
            else if (click && effectsTab.IsChecked == true && PlaceAt(at))
            {
                // an added effect was waiting for its place
            }
            else if (click)
            {
                preview.RequestPick(at, picked => SelectPart(picked is { } index ? parts.FirstOrDefault(part => part.Index == index) : null));   // a click, not a drag
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
            if (!ReferenceEquals(animations.SelectedItem, preview.ClipName) && (animations.SelectedItem as string) != preview.ClipName)
            {
                ApplyFilter();                                             // an ability played once and the clip before it is back
            }

            updating = true;
            timeline.Maximum = Math.Max(length, 0.001);
            timeline.Value = t;
            timeText.Text = $"{t:F2} / {length:F2} s";
            updating = false;
        };
        preview.FrameStats += (fps, w, h) => stats.Text = $"{fps:F0} fps, {w} x {h}, {preview.GlInfo}";
        ShowTab(partsTab);
    }

    public PreviewControl Control => preview;

    /// <summary>For window pictures: opens tool <paramref name="tab"/> (Parts, Colour or Shape) with part number <paramref name="part"/> picked.</summary>
    public void ShowForShot(string? tab, int? part)
    {
        if (part is { } number)
        {
            SelectPart(number == 0 ? parts.FirstOrDefault(row => row.Piece is not null) : parts.FirstOrDefault(row => row.Index == number - 1));   // 0: the first separate mesh
        }

        ShowTab(tab?.ToUpperInvariant() switch { "COLOUR" or "COLOR" => colourTab, "SHAPE" => shapeTab, "EFFECTS" => effectsTab, _ => partsTab });
        if (string.Equals(tab, "effects", StringComparison.OrdinalIgnoreCase))
        {
            SetAllEffectChecks(true);                                        // shows the before and after colours
        }
    }

    /// <summary>The parts currently hidden in the preview (indices into the session's parts).</summary>
    public IReadOnlyCollection<int> HiddenParts => preview.Hidden;

    /// <summary>The parts of the open model, in draw order.</summary>
    public IReadOnlyList<PreviewPart> Parts => [.. parts.Select(row => row.Part)];

    private bool CanBuild => compare.SelectedIndex <= (mod is null ? 0 : 1)                                // this mod, or the base game
        && (preview.Hidden.Count > 0 || preview.PartColors.Count > 0 || preview.PartShines.Count > 0 || preview.PartShapes.Count > 0 || effectChanges.Count > 0 || NewAddedCount > 0);

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
        previewWanted = OpenAutomatically;
        if (previewWanted)
        {
            Reopen();
            return;
        }

        _ = preview.CloseAsync();                                           // the previous model: its archives are let go at once
        effectGroups = [];
        effectsView = null;
        if (effectsTab.IsChecked == true)
        {
            toolHost.Content = EffectsView();
        }

        overlay.IsVisible = false;
        loadPreview.IsVisible = true;
    }

    private Grid BuildTools()
    {
        var tools = new Grid { Width = 380, RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Background = new SolidColorBrush(Color.FromRgb(43, 45, 49)) };
        var picked = new StackPanel { Margin = new Thickness(10, 8), Spacing = 4 };
        picked.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Part", VerticalAlignment = VerticalAlignment.Center, Foreground = Muted }, partPicker } });
        picked.Children.Add(new TextBlock { Text = "Hover a part in the list to highlight it; click the model to pick one.", Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        tools.Children.Add(picked);

        var tabs = new UniformGrid { Columns = 4, Margin = new Thickness(10, 0, 10, 6) };
        foreach (var tab in new[] { partsTab, colourTab, shapeTab, effectsTab })
        {
            tab.HorizontalAlignment = HorizontalAlignment.Stretch;
            tab.HorizontalContentAlignment = HorizontalAlignment.Center;
            tab.Click += (_, _) => ShowTab(tab);
            tabs.Children.Add(tab);
        }

        Grid.SetRow(tabs, 1);
        tools.Children.Add(tabs);
        Grid.SetRow(toolHost, 2);
        tools.Children.Add(toolHost);

        var footer = new StackPanel { Margin = new Thickness(10, 8) };
        footer.Children.Add(changeSummary);
        footer.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Button("Cancel all", CancelAll), buildEdited } });
        Grid.SetRow(footer, 3);
        tools.Children.Add(footer);

        // colour
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
        colorPanel.Children.Add(Labelled("Shine", shine, "0%"));
        colorPanel.Children.Add(shineNote);
        colorPanel.Children.Add(Wrap(Button("Reset colour", ResetColor)));

        // shape
        shapeControls.Children.Add(Section("Size", keepProportions));
        foreach (var (label, slider) in new[] { ("Width", size[0]), ("Height", size[1]), ("Depth", size[2]) })
        {
            shapeControls.Children.Add(Labelled(label, slider, "0.00x"));
        }

        shapeControls.Children.Add(Section("Position"));
        foreach (var (label, slider) in new[] { ("Left/right", move[0]), ("Up/down", move[1]), ("Fwd/back", move[2]) })
        {
            shapeControls.Children.Add(Labelled(label, slider, "+0;-0;0"));
        }

        shapeControls.Children.Add(Section("Rotation"));
        foreach (var (label, slider) in new[] { ("Tilt", turn[0]), ("Turn", turn[1]), ("Roll", turn[2]) })
        {
            shapeControls.Children.Add(Labelled(label, slider, "+0°;-0°;0°"));
        }

        shapeControls.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0), Children = { new TextBlock { Text = "Around", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center }, pivot } });
        shapeControls.Children.Add(Wrap(Button("Reset shape", ResetShape)));
        return tools;
    }

    private void ShowTab(ToggleButton tab)
    {
        foreach (var other in new[] { partsTab, colourTab, shapeTab, effectsTab })
        {
            other.IsChecked = other == tab;
        }

        if (tab == effectsTab)
        {
            toolHost.Content = EffectsView();
            return;
        }

        StopEffectPreview();                                                // the other tools work on the model

        if (tab == partsTab)
        {
            var view = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            view.Children.Add(Wrap(Button("Show all", () => SetHidden(parts.Where(part => part.Piece is null).Select(part => part.Index), false)), Button("Hide picked part", () => ToggleSelected())));
            Grid.SetRow(partList, 1);
            partList.Margin = new Thickness(4, 0);
            Detach(partList);
            view.Children.Add(partList);
            toolHost.Content = view;
        }
        else if (tab == colourTab)
        {
            Detach(colorHint);
            Detach(colorPanel);
            toolHost.Content = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = new StackPanel { Children = { colorHint, colorPanel } } };
            ShowColor();
        }
        else
        {
            Detach(shapeStatus);
            Detach(shapeControls);
            toolHost.Content = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new StackPanel { Margin = new Thickness(10, 4), Children = { shapeStatus, shapeControls } },
            };
            ShowShape();
        }
    }

    private static void Detach(Control control)
    {
        switch (control.Parent)
        {
            case Panel panel:
                panel.Children.Remove(control);
                break;
            case ContentControl host:
                host.Content = null;
                break;
            case Decorator decorator:
                decorator.Child = null;
                break;
        }
    }

    private void Reopen()
    {
        if (updating || model.SelectedItem is not string modelPath || !previewWanted)
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
        shownArchives = archives;
        _ = LoadEffectsAsync(modelPath);
        _ = ReadCapabilitiesAsync(archives, modelPath);
    }

    /// <summary>Asks the direct reshape, in the background, which parts of the opened model can be reshaped.</summary>
    private async Task ReadCapabilitiesAsync(IReadOnlyList<string> archives, string modelPath)
    {
        var request = ++capabilityRequest;
        refusedShapes = new Dictionary<int, PartShapeCapability>();
        modelRefusal = "Checking which parts can be reshaped...";
        ShowShape();
        try
        {
            var (refused, refusal) = await Task.Run(() => ShapeCapabilities.ReadAsync(archives, modelPath)).ConfigureAwait(true);
            if (request != capabilityRequest)
            {
                return;                                                     // another model opened meanwhile
            }

            (refusedShapes, modelRefusal) = (refused, refusal);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            if (request == capabilityRequest)
            {
                modelRefusal = "Could not check this model for reshaping: " + exception.Message;
            }
        }

        if (request == capabilityRequest)
        {
            ShowShape();
        }
    }

    private void OnOpened(PreviewSession? session, TimeSpan elapsed, string? error)
    {
        if (session is null)
        {
            overlay.Text = $"Could not open the preview: {error}";
            AppLog.Shown(overlay.Text);
            return;
        }

        overlay.IsVisible = false;
        ShowListedEffects(session);                                         // a mod built here with added effects plays them
        stats.Text = $"opened in {elapsed.TotalSeconds:F1} s";
        parts = [.. session.Parts.SelectMany((part, index) => session.Pieces.Where(piece => piece.Part == index).Select(piece => new PartRow(piece.Index, part, piece))
            .Prepend(new PartRow(index, part)))];                           // a part made of separate meshes lists them under it, for their own colours
        ranges.Clear();
        previewRefusals.Clear();
        selected = null;
        partList.ItemsSource = parts;
        partPicker.ItemsSource = parts;
        allAnimations = [.. session.Animations.Order(StringComparer.OrdinalIgnoreCase)];
        ApplyFilter();
        _ = ShowAbilitiesAsync(session.Animations);
        SelectPart(null);
    }

    /// <summary>Buttons 1-4 over the preview for the hero's abilities whose animation the model has (the game's own data names them).</summary>
    private async Task ShowAbilitiesAsync(IReadOnlyList<string> clips)
    {
        abilityBar.Children.Clear();
        AbilityClips = [];
        if (model.SelectedItem is not string modelPath)
        {
            return;
        }

        abilityData ??= Task.Run(() => HeroAbilities.Read(settings.Game, Path.Combine(settings.Data, "hero-abilities.json")));
        var abilities = HeroAbilities.Of(await abilityData.ConfigureAwait(true), modelPath);
        if (model.SelectedItem as string != modelPath)
        {
            return;                                                         // another model was opened meanwhile
        }

        AbilityClips = [.. abilities.Where(ability => ability.Slot is >= 1 and <= 4).Select(ability => (ability.Slot, ability.Name, ability.PlayList is { Count: > 0 } list ? list : HeroAbilities.PhasesFor(ability, clips)))];
        foreach (var (slot, name, phases) in AbilityClips)
        {
            if (phases.Count == 0)
            {
                continue;
            }

            var button = new Button { Content = $"{slot}  {name}", Opacity = 0.9 };
            ToolTip.SetTip(button, $"Plays the {name} animation once: {string.Join(", then ", phases.Select(Path.GetFileNameWithoutExtension))}.");
            button.Click += (_, _) =>
            {
                preview.PlaySequence(phases);
                play.IsChecked = true;
            };
            abilityBar.Children.Add(button);
        }
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

    /// <summary>Makes <paramref name="row"/> the picked part everywhere (picker, list, colour and shape tools).</summary>
    private void SelectPart(PartRow? row)
    {
        if (selecting)
        {
            return;
        }

        selecting = true;
        selected = row;
        partPicker.SelectedItem = row;
        partList.SelectedItem = row;
        if (row is not null)
        {
            partList.ScrollIntoView(row);
        }

        selecting = false;
        preview.Highlight = -1;                                             // the picked part shows its real colours and shape
        ShowColor();
        ShowShape();
        UpdateSummary();
    }

    private StackPanel PartItem(PartRow row)
    {
        var visible = new CheckBox { IsChecked = !preview.Hidden.Contains(row.Index), VerticalAlignment = VerticalAlignment.Center, IsVisible = row.Piece is null };
        visible.IsCheckedChanged += (_, _) => SetHidden([row.Index], visible.IsChecked != true);
        var item = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(row.Piece is null ? 0 : 28, 0, 0, 0),
            Children =
            {
                visible,
                new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = $"{row.Label}. {row.Name}", FontWeight = row.Piece is null ? FontWeight.SemiBold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 },
                        new TextBlock { Text = row.Piece is { } piece ? $"separate mesh - {piece.Triangles:N0} triangles, own colour" : $"{row.Part.Mesh} - {row.Part.Indices / 3:N0} triangles", Foreground = Muted, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 },
                    },
                },
            },
        };
        item.PointerEntered += (_, _) => preview.Highlight = ReferenceEquals(selected, row) ? -1 : row.Index;   // never over the picked part
        item.PointerExited += (_, _) => preview.Highlight = -1;
        return item;
    }

    private void SetHidden(IEnumerable<int> indices, bool hidden)
    {
        foreach (var index in indices.Select(index => parts.FirstOrDefault(row => row.Index == index)?.PartIndex ?? index).Distinct().ToArray())   // a piece hides with its part
        {
            _ = hidden ? preview.Hidden.Add(index) : preview.Hidden.Remove(index);
        }

        selecting = true;
        partList.ItemsSource = null;                                        // redraw the check boxes
        partList.ItemsSource = parts;
        partList.SelectedItem = selected;
        selecting = false;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var changes = new List<string>();
        if (preview.Hidden.Count > 0)
        {
            changes.Add($"{preview.Hidden.Count} hidden");
        }

        if (preview.PartColors.Count > 0)
        {
            changes.Add($"{preview.PartColors.Count} recolored");
        }

        if (preview.PartShapes.Count > 0)
        {
            changes.Add($"{preview.PartShapes.Count} reshaped");
        }

        if (preview.PartShines.Count > 0)
        {
            changes.Add($"{preview.PartShines.Count} shiny");
        }

        if (NewAddedCount > 0)
        {
            changes.Add($"{NewAddedCount} effect{(NewAddedCount == 1 ? string.Empty : "s")} added");
        }

        if (effectChanges.Count > 0)
        {
            changes.Add($"effects of {effectChanges.Count} group{(effectChanges.Count == 1 ? string.Empty : "s")} recolored");
        }

        changeSummary.Text = changes.Count == 0 ? "No changes yet." : "Changes: " + string.Join(" · ", changes);
        buildEdited.IsEnabled = CanBuild;
        ToolTip.SetTip(buildEdited, "Builds one edited version with every change (reshaped parts through the checked model operations); the original stays as a disabled version.");
    }

    private void ShowColor()
    {
        if (selected is not { } row || preview.Session?.PartColorTexture(row.Index) is not { } texture)
        {
            colorPanel.IsVisible = false;
            colorHint.IsVisible = true;
            colorHint.Text = selected is null ? "Pick a part to recolor it." : "This part has no colour texture to recolor.";
            return;
        }

        colorPanel.IsVisible = true;
        colorHint.IsVisible = false;
        preview.PrepareColor(row.Index);                                    // unpack its texture now, not on the first colour change
        colorTitle.Text = $"Colour of part {row.Label} ({texture[(texture.LastIndexOf('/') + 1)..]})";
        var change = preview.PartColors.GetValueOrDefault(row.Index) ?? new ColorAdjustment();
        settingSliders = true;
        hue.Value = change.HueShiftDegrees;
        saturation.Value = change.Saturation;
        brightness.Value = change.Brightness;
        shine.Value = preview.PartShines.GetValueOrDefault(row.Index);
        shine.IsEnabled = preview.Session?.PartNormalTexture(row.Index) is not null;
        shineNote.Text = shine.IsEnabled ? "Shine lowers the part's roughness: it reflects like latex or metal in game."
            : "This part's material has no roughness texture, so shine cannot change it.";
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
    }

    private void ApplyColor()
    {
        if (selected is not { } row)
        {
            return;
        }

        var color = paintColor.Color;
        var change = new ColorAdjustment((float)hue.Value, (float)saturation.Value, (float)brightness.Value,
            Colorize: paint.IsChecked == true ? new RgbColor(color.R / 255f, color.G / 255f, color.B / 255f) : null,
            ColorizeStrength: (float)paintStrength.Value,
            ColorizeLightness: paint.IsChecked == true);                  // painting black or white works too
        var unchanged = change == new ColorAdjustment();
        if (unchanged)
        {
            preview.PartColors.Remove(row.Index);
        }
        else
        {
            preview.PartColors[row.Index] = change;
        }

        preview.RequestPartColor(row.Index, unchanged ? null : change);
        UpdateSummary();
    }

    private void ApplyRange()
    {
        if (selected is not { } row || !ranges.TryGetValue(row.Index, out var range))
        {
            return;
        }

        ranges[row.Index] = (range.Target, (float)tolerance.Value);
        sampleSwatch.Background = new SolidColorBrush(Color.FromRgb((byte)(range.Target.R * 255), (byte)(range.Target.G * 255), (byte)(range.Target.B * 255)));
        preview.RequestColorRange(row.Index, onlyLike.IsChecked == true ? range.Target : null, (float)tolerance.Value);
    }

    private void ResetColor()
    {
        if (selected is { } row)
        {
            preview.PartShines.Remove(row.Index);
            preview.RequestPartShine(row.Index, null);
            preview.PartColors.Remove(row.Index);
            ranges.Remove(row.Index);
            preview.RequestColorRange(row.Index, null, 0);
            preview.RequestPartColor(row.Index, null);
            ShowColor();
            UpdateSummary();
        }
    }

    /// <summary>Shows what the picked part allows (from the CLI's checked operations and the preview) and its current shape.</summary>
    private void ShowShape()
    {
        var capability = selected is { } row ? CapabilityOf(row) : null;
        var previewRefusal = selected is { Piece: not null } piece ? $"A separate mesh takes its own colour; reshape or hide its whole part {piece.PartIndex + 1}."
            : selected is { } picked ? previewRefusals.GetValueOrDefault(picked.Index) ?? preview.Session?.PartShapeRefusal(picked.Index) : null;
        var level = previewRefusal is null ? capability?.Level ?? ShapeLevel.None : ShapeLevel.None;
        (shapeStatus.Text, shapeStatus.Foreground) = selected is null ? ("Pick a part to reshape it.", Muted)
            : modelRefusal is { } refused && capability is null ? (refused, Warn)
            : previewRefusal is not null ? (previewRefusal, Warn)
            : level == ShapeLevel.Full ? ("✓ " + capability!.Reason, Good)
            : (capability?.Reason ?? "This part cannot be reshaped.", Warn);
        shapeControls.IsEnabled = level != ShapeLevel.None;
        keepProportions.IsEnabled = level == ShapeLevel.Full;
        if (level is ShapeLevel.EvenSize or ShapeLevel.EvenSizeAndMove)
        {
            keepProportions.IsChecked = true;                               // the checked operation allows an even size only
        }

        foreach (var slider in turn)
        {
            slider.IsEnabled = level == ShapeLevel.Full;
        }

        foreach (var slider in move)
        {
            slider.IsEnabled = level is ShapeLevel.Full or ShapeLevel.EvenSizeAndMove;
        }

        pivot.IsEnabled = level == ShapeLevel.Full;
        var shape = selected is { } current ? preview.PartShapes.GetValueOrDefault(current.Index) ?? PartShape.Identity : PartShape.Identity;
        settingSliders = true;
        (size[0].Value, size[1].Value, size[2].Value) = (shape.Scale.Y, shape.Scale.Z, shape.Scale.X);
        (move[0].Value, move[1].Value, move[2].Value) = (shape.Move.Y, shape.Move.Z, shape.Move.X);
        (turn[0].Value, turn[1].Value, turn[2].Value) = (shape.RotationDegrees.Y, shape.RotationDegrees.Z, shape.RotationDegrees.X);
        pivot.SelectedIndex = (int)shape.Pivot;
        settingSliders = false;
    }

    /// <summary>What the checked operations allow for part <paramref name="index"/> (the build uses the same level).</summary>
    public ShapeLevel ShapeLevelOf(int index) =>
        parts.FirstOrDefault(row => row.Index == index) is { } row ? CapabilityOf(row)?.Level ?? ShapeLevel.None : ShapeLevel.None;

    private PartShapeCapability? CapabilityOf(PartRow row) =>
        modelRefusal is not null ? null : refusedShapes.GetValueOrDefault(row.PartIndex) ?? ShapeCapabilities.Allowed;

    private void ShapeSliderChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == RangeBase.ValueProperty && !settingSliders)
        {
            QueueShape();
        }
    }

    private void QueueShape()
    {
        if (!settingSliders)
        {
            shapeDelay.Stop();
            shapeDelay.Start();
        }
    }

    private void ApplyShape()
    {
        if (selected is not { } row)
        {
            return;
        }

        var shape = new PartShape(
            new System.Numerics.Vector3((float)size[2].Value, (float)size[0].Value, (float)size[1].Value),
            new System.Numerics.Vector3((float)turn[2].Value, (float)turn[0].Value, (float)turn[1].Value),
            new System.Numerics.Vector3((float)move[2].Value, (float)move[0].Value, (float)move[1].Value),
            (ShapePivot)Math.Max(0, pivot.SelectedIndex));
        var index = row.Index;
        if (shape.IsIdentity)
        {
            preview.PartShapes.Remove(index);
        }
        else
        {
            preview.PartShapes[index] = shape;
        }

        preview.RequestPartShape(index, shape.IsIdentity ? null : shape, refusal => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (refusal is not null)
            {
                previewRefusals[index] = refusal;
                preview.PartShapes.Remove(index);
                ShowShape();
            }

            UpdateSummary();
        }));
        UpdateSummary();
    }

    private void ResetShape()
    {
        if (selected is { } row)
        {
            preview.PartShapes.Remove(row.Index);
            preview.RequestPartShape(row.Index, null);
            ShowShape();
            UpdateSummary();
        }
    }

    private void ToggleSelected()
    {
        if (selected is { } row)
        {
            SetHidden([row.Index], !preview.Hidden.Contains(row.PartIndex));
        }
    }

    private void SetSampling(bool on)
    {
        sampling = on;
        pickColor.Content = on ? "Cancel (Esc)" : "Pick on model";
    }

    /// <summary>Drops every recolor, colour range, shape and hidden part since the model opened (nothing was built).</summary>
    private void CancelAll()
    {
        SetAllEffectChecks(false);                                           // unticked groups go back to their colours
        effectChanges.Clear();
        ClearAddedEffects(keepExisting: true);
        RefreshAddedList(null);
        _ = ApplyPlacedAsync(files: false);
        foreach (var part in preview.PartColors.Keys.ToArray())
        {
            preview.RequestPartColor(part, null);
        }

        foreach (var part in preview.PartShines.Keys.ToArray())
        {
            preview.RequestPartShine(part, null);
        }

        preview.PartShines.Clear();

        foreach (var part in ranges.Keys.ToArray())
        {
            preview.RequestColorRange(part, null, 0);
        }

        foreach (var part in preview.PartShapes.Keys.ToArray())
        {
            preview.RequestPartShape(part, null);
        }

        preview.PartColors.Clear();
        preview.PartShapes.Clear();
        ranges.Clear();
        SetSampling(false);
        SetHidden(parts.Where(part => part.Piece is null).Select(part => part.Index), false);
        ShowColor();
        ShowShape();
    }

    private static Slider Shape(double minimum, double maximum, double value) => new() { Minimum = minimum, Maximum = maximum, Value = value, Height = 36 };

    private static Grid Section(string title, Control? extra = null)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        if (extra is not null)
        {
            Grid.SetColumn(extra, 1);
            row.Children.Add(extra);
        }

        return row;
    }

    /// <summary>A label and a slider on one line (and its value, when a format is given), to keep the tool column short.</summary>
    private static Grid Labelled(string label, Slider slider, string? format = null)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("72,*,52"), Margin = new Thickness(0, -4) };
        row.Children.Add(new TextBlock { Text = label, Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        Detach(slider);
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        if (format is not null)
        {
            var value = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            void Show() => value.Text = slider.Value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            Show();
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty)
                {
                    Show();
                }
            };
            Grid.SetColumn(value, 2);
            row.Children.Add(value);
        }

        return row;
    }

    private static WrapPanel Wrap(params Control[] children)
    {
        var row = new WrapPanel { ItemSpacing = 6, LineSpacing = 6, Margin = new Thickness(8, 4) };
        foreach (var child in children)
        {
            Detach(child);
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
/// <summary>A row of the parts list: a part, or a separate mesh (<paramref name="Piece"/>) of the part above it.</summary>
internal sealed record PartRow(int Index, PreviewPart Part, S2ModKit.Adapters.Preview.PartPiece? Piece = null)
{
    public int PartIndex => Piece?.Part ?? Index;

    /// <summary>"3" for part 3, "3.2" for its second-largest separate mesh.</summary>
    public string Label => Piece is { } piece ? $"{piece.Part + 1}.{piece.Number}" : $"{Index + 1}";

    public string Name => Part.Material[(Part.Material.LastIndexOf('/') + 1)..];
}
