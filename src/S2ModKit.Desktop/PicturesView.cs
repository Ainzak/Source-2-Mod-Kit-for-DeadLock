using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using S2ModKit.Application;

namespace S2ModKit.Desktop;

/// <summary>A picture mod built earlier (<c>picture-mods/&lt;folder&gt;/recipe.json</c>), to open and build again.</summary>
internal sealed record SavedPictureMod(string Folder, string Name, string? PaintingId, string? HeroName, IReadOnlyList<string> HeroKinds,
    PictureFit Fit, float Glow, float Speed, IReadOnlyList<PictureSource> Sources);

internal sealed record PictureInstallResult(string Message, string? PackagePath = null);

/// <summary>
/// The Pictures tab: put pictures and GIFs on map paintings and on a hero's pictures. Left: the targets (measured paintings, every
/// hero, picture mods built before); middle: the result playing as it will in the game ("as in the room" approximates the room's
/// light and the glow); right: one or two pictures, fit, brightness, glow, speed and Build. The build is an ordinary mod: it is
/// installed into My mods under its name, with its first frame as the card picture.
/// </summary>
internal sealed class PicturesView : UserControl
{
    private const int PreviewHeight = 460;
    private static readonly IBrush Muted = AppTheme.Muted;
    private readonly AppSettings settings;
    private readonly PictureModBuilder builder;
    private readonly TextBox heroFilter = new() { PlaceholderText = "Find a hero" };
    private readonly ListBox targets = new() { Background = Brushes.Transparent };
    private readonly TextBlock title = new() { FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock info = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel preview = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly CheckBox roomLight = new() { Content = "As in the room (approximate)", IsChecked = true };
    private readonly StackPanel sources = new() { Spacing = 6 };
    private readonly ComboBox fit = new() { ItemsSource = new[] { "Whole picture", "Fill and crop" }, SelectedIndex = 0, MinWidth = 150 };
    private readonly Slider glow = new() { Minimum = 0, Maximum = 2, Value = 0.5 };
    private readonly Slider speed = new() { Minimum = 0.25, Maximum = 3, Value = 1 };
    private readonly StackPanel heroKinds = new() { Spacing = 2 };
    private readonly Grid glowRow;
    private readonly Grid speedRow;
    private readonly TextBox name = new() { PlaceholderText = "Name of the mod" };
    private readonly Button build = new() { Content = "Build pictures mod", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 8) };
    private readonly SelectableTextBlock status = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    private readonly Button showInstalled = new() { Content = "Show in My mods", IsVisible = false };
    private readonly DispatcherTimer player = new();
    private readonly Dictionary<string, IReadOnlyList<PictureFrame>> decoded = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string File, Slider Brightness)> files = [];
    private readonly List<Image> frames = [];
    private List<WriteableBitmap> playing = [];
    private object? target;
    private int frame;
    private int refresh;
    private int targetSelection;
    private bool loading;

    public PicturesView(AppSettings settings, Control modeSwitch)
    {
        this.settings = settings;
        builder = new PictureModBuilder(settings, PictureTargets.Shipped);
        glowRow = Row("Glow", glow, "0.00");
        speedRow = Row("Speed", speed, "0.00x");

        var header = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
        header.Children.Add(modeSwitch);
        header.Children.Add(new TextBlock
        {
            Text = "Picture replacement is still in development. Check the result in Deadlock after installing.",
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap,
        });
        header.Children.Add(heroFilter);
        var side = new DockPanel { Width = 300, Background = AppTheme.Side };
        DockPanel.SetDock(header, Dock.Top);
        side.Children.Add(header);
        side.Children.Add(targets);

        var drop = new Border
        {
            BorderBrush = AppTheme.Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10),
            Background = Brushes.Transparent,
            Child = new TextBlock { Text = "Drop pictures or GIFs here, or click to choose (one, or two stacked)", Foreground = Muted, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
        };
        DragDrop.SetAllowDrop(drop, true);
        drop.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            e.Handled = true;
            AddFiles(e.DataTransfer.TryGetFiles()?.Select(item => item.TryGetLocalPath()).OfType<string>() ?? []);
        });
        drop.PointerPressed += async (_, _) => await ChooseAsync();
        var right = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
        right.Children.Add(drop);
        right.Children.Add(sources);
        right.Children.Add(Row("Fit", fit, null));
        right.Children.Add(glowRow);
        right.Children.Add(speedRow);
        right.Children.Add(heroKinds);
        right.Children.Add(name);
        right.Children.Add(build);
        right.Children.Add(status);
        right.Children.Add(showInstalled);
        showInstalled.Click += async (_, _) =>
        {
            if (showInstalled.Tag is string package && ShowInstalled is { } show)
            {
                await show(package);
            }
        };
        var settingsPanel = new Border { Width = 320, Background = AppTheme.Side, Child = new ScrollViewer { Content = right } };

        var middle = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
        middle.Children.Add(title);
        middle.Children.Add(info);
        middle.Children.Add(roomLight);
        middle.Children.Add(preview);
        var root = new DockPanel();
        DockPanel.SetDock(side, Dock.Left);
        DockPanel.SetDock(settingsPanel, Dock.Right);
        root.Children.Add(side);
        root.Children.Add(settingsPanel);
        root.Children.Add(new Border { Background = AppTheme.Main, Child = new ScrollViewer { Content = middle } });
        Content = root;

        targets.ItemTemplate = new FuncDataTemplate<object>((row, _) => row switch
        {
            string heading => new TextBlock { Text = heading, Foreground = Muted, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0), IsHitTestVisible = false },
            PaintingTarget painting => Item(painting.Name, $"{painting.Map}, {painting.Place}"),
            Hero hero => Item(hero.Name, "Cards, icons, hero screen background"),
            SavedPictureMod saved => Item(saved.Name, "Built before: open to change and build again"),
            _ => new TextBlock(),
        });
        targets.SelectionChanged += (_, _) =>
        {
            if (targets.SelectedItem is string)
            {
                targets.SelectedItem = target;
                return;
            }

            Select(targets.SelectedItem);
        };
        heroFilter.TextChanged += (_, _) => ShowTargets();
        foreach (var control in new Control[] { fit, roomLight })
        {
            control.PropertyChanged += (_, e) =>
            {
                if (e.Property == SelectingItemsControl.SelectedIndexProperty || e.Property == ToggleButton.IsCheckedProperty)
                {
                    Refresh();
                }
            };
        }

        foreach (var slider in new[] { glow, speed })
        {
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty)
                {
                    Refresh();
                }
            };
        }

        player.Tick += (_, _) => Advance();
        build.Click += async (_, _) => await BuildAsync();
        ShowTargets();
    }

    /// <summary>Installs a built package under its name with its card picture.</summary>
    public Func<PictureBuild, string, Task<PictureInstallResult>>? InstallRequested { get; set; }

    public Func<string, Task>? ShowInstalled { get; set; }

    /// <summary>For the window's screenshot mode: the first painting with these pictures.</summary>
    public void ShowForShot(IReadOnlyList<string> pictures)
    {
        targets.SelectedItem = PictureTargets.Shipped.Paintings.Count > 0 ? PictureTargets.Shipped.Paintings[0] : null;
        AddFiles(pictures);
    }

    private PaintingTarget? Painting => target as PaintingTarget ?? (target is SavedPictureMod { PaintingId: { } id } ? PictureTargets.Shipped.Paintings.FirstOrDefault(p => p.Id == id) : null);

    private Hero? HeroTarget => target as Hero ?? (target is SavedPictureMod { HeroName: { } hero } ? Hero.Catalogue.FirstOrDefault(h => h.Name == hero) : null);

    private void ShowTargets()
    {
        var filter = heroFilter.Text?.Trim() ?? string.Empty;
        var rows = new List<object> { "Map paintings" };
        rows.AddRange(PictureTargets.Shipped.Paintings);
        rows.Add("Hero pictures");
        rows.AddRange(Hero.Catalogue.Where(hero => filter.Length == 0 || hero.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).OrderBy(hero => hero.Name, StringComparer.OrdinalIgnoreCase));
        var saved = Saved();
        if (saved.Count > 0)
        {
            rows.Add("Picture mods built before");
            rows.AddRange(saved);
        }

        targets.ItemsSource = rows;
        targets.SelectedItem = target is not null && rows.Contains(target) ? target : null;
        if (target is null)
        {
            Select(null);
        }
    }

    private void Select(object? row)
    {
        var selection = ++targetSelection;
        target = row;
        heroKinds.Children.Clear();
        if (row is SavedPictureMod saved)
        {
            Open(saved);
        }

        var painting = Painting;
        var hero = HeroTarget;
        glowRow.IsVisible = speedRow.IsVisible = painting is not null;
        title.Text = painting?.Name ?? hero?.Name ?? "Pick a painting or a hero";
        info.Text = painting is not null ? $"{painting.Map}, {painting.Place}. A picture or GIF fills the painting's frame; GIFs play in the game. The painting's own glow (the Patron's eyes) is off; Glow lights the picture by its own colours, against the room's dim light."
            : hero is not null ? "The first frame of the picture goes on each picture of this hero the game has, at its size. Fill and crop suits cards; Whole picture keeps everything in view."
            : "Paintings take pictures and GIFs; hero pictures take still pictures.";
        if (painting is not null && row is not SavedPictureMod)
        {
            glow.Value = painting.Glow;
        }

        if (hero is not null)
        {
            var chosen = (row as SavedPictureMod)?.HeroKinds ?? [];
            foreach (var kind in PictureTargets.Shipped.HeroPictures)
            {
                heroKinds.Children.Add(new CheckBox { Content = kind.Name, Tag = kind.Id, IsEnabled = false, IsChecked = chosen.Count == 0 || chosen.Contains(kind.Id) });
            }

            foreach (var box in heroKinds.Children.OfType<CheckBox>())
            {
                box.IsCheckedChanged += (_, _) => Refresh();
            }

            _ = CheckHeroKindsAsync(hero, selection);
        }

        if (row is not SavedPictureMod && (painting ?? (object?)hero) is not null && string.IsNullOrWhiteSpace(name.Text))
        {
            name.Text = $"{painting?.Name ?? hero!.Name} pictures";
        }

        Refresh();
    }

    private async Task CheckHeroKindsAsync(Hero hero, int selection)
    {
        try
        {
            var available = await Task.Run(() => builder.AvailableHeroKinds(hero));
            if (selection != targetSelection)
            {
                return;
            }

            foreach (var box in heroKinds.Children.OfType<CheckBox>())
            {
                box.IsEnabled = available.Contains((string)box.Tag!);
                if (!box.IsEnabled)
                {
                    box.IsChecked = false;
                    box.Content = $"{box.Content} (unavailable)";
                }
            }

            Refresh();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            if (selection == targetSelection)
            {
                Say("Could not read the hero pictures: " + exception.Message);
            }
        }
    }

    private string[] SelectedHeroKinds() =>
        [.. heroKinds.Children.OfType<CheckBox>().Where(box => box.IsEnabled && box.IsChecked == true).Select(box => (string)box.Tag!)];

    private void Open(SavedPictureMod saved)
    {
        loading = true;
        files.Clear();
        name.Text = saved.Name;
        fit.SelectedIndex = saved.Fit == PictureFit.Fill ? 1 : 0;
        glow.Value = saved.Glow;
        speed.Value = saved.Speed;
        foreach (var source in saved.Sources.Where(source => File.Exists(source.File)))
        {
            files.Add((source.File, BrightnessSlider(source.Brightness)));
        }

        loading = false;
        ShowSources();
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths.Take(2 - Math.Min(2, files.Count)))
        {
            try
            {
                decoded[path] = PictureFrames.Read(path);
                files.Add((path, BrightnessSlider(1)));
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Say(exception.Message);
            }
        }

        if (string.IsNullOrWhiteSpace(name.Text) || name.Text.EndsWith(" pictures", StringComparison.Ordinal))
        {
            name.Text = files.Count > 0 ? Path.GetFileNameWithoutExtension(files[0].File) : name.Text;
        }

        ShowSources();
    }

    private async Task ChooseAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Pictures or GIFs (one, or two stacked)",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Pictures and GIFs") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp"] }],
        });
        AddFiles(picked.Select(item => item.TryGetLocalPath()).OfType<string>());
    }

    private void ShowSources()
    {
        sources.Children.Clear();
        for (var i = 0; i < files.Count; i++)
        {
            var index = i;
            var (file, brightness) = files[i];
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) =>
            {
                files.RemoveAt(index);
                ShowSources();
            };
            var order = new Button { Content = "Swap", IsVisible = files.Count == 2 };
            order.Click += (_, _) =>
            {
                (files[0], files[1]) = (files[1], files[0]);
                ShowSources();
            };
            var frameCount = decoded.TryGetValue(file, out var read) && read.Count > 1 ? $", {read.Count} frames" : string.Empty;
            sources.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = $"{(files.Count == 2 ? index == 0 ? "Top: " : "Bottom: " : string.Empty)}{Path.GetFileName(file)}{frameCount}", TextTrimming = TextTrimming.CharacterEllipsis },
                    Row("Brightness", brightness, "0\\%"),
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { order, remove } },
                },
            });
        }

        Refresh();
    }

    private Slider BrightnessSlider(float value)
    {
        var slider = new Slider { Minimum = 20, Maximum = 200, Value = value * 100 };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                Refresh();
            }
        };
        return slider;
    }

    private PictureFit Fit => fit.SelectedIndex == 1 ? PictureFit.Fill : PictureFit.Whole;

    private IReadOnlyList<PictureLayer> Layers() =>
        [.. files.Select(item => new PictureLayer(decoded.TryGetValue(item.File, out var read) ? read : decoded[item.File] = PictureFrames.Read(item.File), (float)(item.Brightness.Value / 100)))];

    /// <summary>Composes the preview again a moment after the last change (sliders send many changes).</summary>
    private void Refresh()
    {
        if (loading)
        {
            return;
        }

        var ticket = ++refresh;
        build.IsEnabled = files.Count > 0 && (Painting is not null || SelectedHeroKinds().Length > 0);
        DispatcherTimer.RunOnce(async () =>
        {
            if (ticket == refresh)
            {
                await ComposeAsync(ticket);
            }
        }, TimeSpan.FromMilliseconds(250));
    }

    private async Task ComposeAsync(int ticket)
    {
        player.Stop();
        preview.Children.Clear();
        frames.Clear();
        if (files.Count == 0)
        {
            return;
        }

        var painting = Painting;
        var hero = HeroTarget;
        var layers = Layers();
        var fitting = Fit;
        var room = roomLight.IsChecked == true;
        var glowAmount = (float)glow.Value;
        var playback = (float)speed.Value;
        try
        {
            if (painting is not null)
            {
                var composed = await Task.Run(() => PictureModBuilder.PreviewPainting(painting, layers, fitting, playback, PreviewHeight));
                if (ticket != refresh)
                {
                    return;
                }

                var tint = room ? painting.RoomTint.Select(channel => channel + glowAmount).ToArray() : [1f, 1f, 1f];
                playing = [.. composed.Frames.Select(image => Bitmap(image, tint))];
                var shown = new Image { Width = composed.Frames[0].Width, Height = composed.Frames[0].Height, Source = playing[0] };
                frames.Add(shown);
                preview.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(138, 90, 43)), BorderThickness = new Thickness(12), Child = shown });
                frame = 0;
                if (playing.Count > 1)
                {
                    player.Interval = TimeSpan.FromSeconds(1 / Math.Max(1, composed.FramesPerSecond));
                    player.Start();
                }

                info.Text = (info.Text ?? string.Empty).Split(" Preview:")[0] + (playing.Count > 1 ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $" Preview: {playing.Count} frames, a {composed.LoopSeconds:0.##} s loop.") : " Preview: a still picture.");
            }
            else if (hero is not null)
            {
                var kinds = SelectedHeroKinds();
                var heroFiles = await Task.Run(() => builder.HeroFiles(hero));
                if (ticket != refresh)
                {
                    return;
                }

                var still = layers.Select(layer => layer with { Frames = [layer.Frames[0]] }).ToArray();
                foreach (var (kind, _, width, height) in heroFiles.Where(file => kinds.Contains(file.Kind.Id)).DistinctBy(file => file.Kind.Id))
                {
                    var scale = Math.Min(1.0, 220.0 / height);
                    var (w, h) = (Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
                    var image = await Task.Run(() => PictureComposer.Compose(still, w, h, fitting, 1).Frames[0]);
                    preview.Children.Add(new StackPanel
                    {
                        Margin = new Thickness(0, 0, 12, 12),
                        Children = { new Image { Width = w, Height = h, Source = Bitmap(image, [1f, 1f, 1f]) }, new TextBlock { Text = $"{kind.Name} ({width}x{height})", Foreground = Muted } },
                    });
                }
            }
        }
        catch (Exception exception) when (exception is S2ModKit.Domain.S2ModKitException or InvalidDataException or IOException)
        {
            Say(exception.Message);
        }
    }

    private void Advance()
    {
        if (playing.Count < 2 || frames.Count == 0)
        {
            player.Stop();
            return;
        }

        frame = (frame + 1) % playing.Count;
        frames[0].Source = playing[frame];
    }

    private async Task BuildAsync()
    {
        var painting = Painting;
        var hero = HeroTarget;
        var kinds = SelectedHeroKinds();
        if (files.Count == 0 || (painting is null && (hero is null || kinds.Length == 0)) || InstallRequested is null)
        {
            return;
        }

        var label = string.IsNullOrWhiteSpace(name.Text) ? "Pictures" : name.Text.Trim();
        var request = new PictureRequest(label, painting?.Id, painting is null ? hero?.Name : null, [.. files.Select(item => new PictureSource(item.File, (float)(item.Brightness.Value / 100)))],
            Fit, (float)glow.Value, (float)speed.Value, kinds);
        build.IsEnabled = false;
        build.Content = "Building...";
        showInstalled.IsVisible = false;
        showInstalled.Tag = null;
        try
        {
            var layers = Layers();
            var built = await Task.Run(() => builder.BuildAsync(request, layers));
            var installed = await InstallRequested(built, label);
            Say(installed.Message);
            showInstalled.Tag = installed.PackagePath;
            showInstalled.IsVisible = installed.PackagePath is not null && ShowInstalled is not null;
            ShowTargets();
        }
        catch (Exception exception) when (exception is S2ModKit.Domain.S2ModKitException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Say("The pictures mod was not built: " + exception.Message);
        }
        finally
        {
            build.Content = "Build pictures mod";
            build.IsEnabled = files.Count > 0 && (Painting is not null || SelectedHeroKinds().Length > 0);
        }
    }

    private List<SavedPictureMod> Saved()
    {
        var root = Path.Combine(settings.Data, "picture-mods");
        if (!Directory.Exists(root))
        {
            return [];
        }

        var list = new List<SavedPictureMod>();
        foreach (var folder in Directory.GetDirectories(root).Order(StringComparer.Ordinal).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "recipe.json")));
                var r = document.RootElement;
                if (r.GetProperty("schema").GetString() != PictureModBuilder.RecipeSchema)
                {
                    continue;
                }

                string? Text(string key) => r.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                list.Add(new SavedPictureMod(folder, Text("name") ?? Path.GetFileName(folder), Text("painting"), Text("hero"),
                    [.. r.GetProperty("heroKinds").EnumerateArray().Select(kind => kind.GetString()!)],
                    Text("fit") == nameof(PictureFit.Fill) ? PictureFit.Fill : PictureFit.Whole, r.GetProperty("glow").GetSingle(), r.GetProperty("speed").GetSingle(),
                    [.. r.GetProperty("sources").EnumerateArray().Select(source => new PictureSource(Path.Combine(folder, source.GetProperty("file").GetString()!), source.GetProperty("brightness").GetSingle()))]));
            }
            catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                AppLog.Info($"Picture mod '{folder}' was skipped: {exception.Message}");
            }
        }

        return list;
    }

    private void Say(string text)
    {
        status.Text = text;
        AppLog.Shown(text);
    }

    private static WriteableBitmap Bitmap(PreviewImage image, float[] tint)
    {
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var locked = bitmap.Lock();
        var pixels = new byte[image.Rgba.Length];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                pixels[i + c] = (byte)Math.Clamp(image.Rgba[i + c] * tint[c], 0, 255);
            }

            pixels[i + 3] = 255;
        }

        for (var y = 0; y < image.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(pixels, y * image.Width * 4, locked.Address + (y * locked.RowBytes), image.Width * 4);
        }

        return bitmap;
    }

    private static StackPanel Item(string heading, string detail) => new()
    {
        Margin = new Thickness(4, 2),
        Children =
        {
            new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
            new TextBlock { Text = detail, Foreground = Muted, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
        },
    };

    private static Grid Row(string label, Control control, string? format)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*,52"), Margin = new Thickness(0, 2) };
        var text = new TextBlock { Text = label, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        if (format is not null && control is Slider slider)
        {
            var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            void Show() => value.Text = slider.Value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty)
                {
                    Show();
                }
            };
            Show();
            Grid.SetColumn(value, 2);
            grid.Children.Add(value);
        }

        return grid;
    }
}
