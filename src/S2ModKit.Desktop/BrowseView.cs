using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace S2ModKit.Desktop;

/// <summary>
/// GameBanana browser: Deadlock mods by newest, recently updated, most liked or most viewed, by hero or category, or by search;
/// mods with content ratings stay hidden unless shown. A mod's page shows its screenshots, description, credits and files, each
/// with an Install button (the download is checked against GameBanana's size and checksum, then installed like a dropped file).
/// </summary>
internal sealed class BrowseView : UserControl
{
    private static readonly IBrush Sidebar = new SolidColorBrush(Color.FromRgb(43, 45, 49));
    private static readonly IBrush Panel = new SolidColorBrush(Color.FromRgb(49, 51, 56));
    private static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(148, 155, 164));
    private static readonly IBrush Warning = new SolidColorBrush(Color.FromRgb(240, 170, 70));
    private readonly GameBananaClient client;
    private readonly TextBox search = new() { PlaceholderText = "Search GameBanana (Enter)" };
    private readonly ComboBox sort = new() { ItemsSource = GameBananaClient.Sorts.Select(item => item.Label).ToArray(), SelectedIndex = 0, MinWidth = 150 };
    private readonly ComboBox category = new() { MinWidth = 170 };
    private readonly CheckBox mature = new() { Content = "Show mods with content ratings" };
    private readonly SelectableTextBlock status = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    private readonly ListBox list = new() { Background = Brushes.Transparent };
    private readonly ContentControl page = new();
    private readonly List<object> rows = [];
    private IReadOnlyList<(string Label, long? Id)> categories = [("All mods", null)];
    private int pageNumber;
    private int total;
    private string? searching;
    private int request;
    private bool opening;

    public BrowseView(GameBananaClient client, Control modeSwitch)
    {
        this.client = client;
        var header = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
        header.Children.Add(modeSwitch);
        header.Children.Add(search);
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { sort, category } });
        header.Children.Add(mature);
        header.Children.Add(status);
        var side = new DockPanel { Width = 380, Background = Sidebar };
        DockPanel.SetDock(header, Dock.Top);
        side.Children.Add(header);
        side.Children.Add(list);
        var root = new DockPanel();
        DockPanel.SetDock(side, Dock.Left);
        root.Children.Add(side);
        root.Children.Add(new Border { Background = Panel, Child = page });
        Content = root;

        list.ItemTemplate = new FuncDataTemplate<object>((row, _) => row switch
        {
            GameBananaMod mod => Card(mod),
            string text => new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center },
            _ => new TextBlock(),
        });
        list.SelectionChanged += async (_, _) =>
        {
            switch (list.SelectedItem)
            {
                case GameBananaMod mod:
                    await ShowPageAsync(mod);
                    break;
                case string:
                    list.SelectedItem = null;
                    await LoadAsync(more: true);
                    break;
            }
        };
        search.KeyDown += async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                searching = string.IsNullOrWhiteSpace(search.Text) ? null : search.Text.Trim();
                await LoadAsync(more: false);
            }
        };
        sort.SelectionChanged += async (_, _) => await LoadAsync(more: false, clearSearch: true);
        category.SelectionChanged += async (_, _) =>
        {
            if (!opening)
            {
                await LoadAsync(more: false, clearSearch: true);
            }
        };
        mature.IsCheckedChanged += (_, _) => ShowRows();
        SteadyWidth(sort);
        SteadyWidth(category);
        page.Content = new TextBlock { Text = "Pick a mod to see its page.", Foreground = Muted, Margin = new Thickness(20) };
    }

    /// <summary>Raised by a file's Install button; true when the file was installed.</summary>
    public event Func<GameBananaPage, GameBananaFile, Task<bool>>? InstallRequested;

    /// <summary>Whether a GameBanana file (by id) is installed now; its button then reads "Installed".</summary>
    public Func<long, bool> IsInstalled { get; set; } = _ => false;

    /// <summary>A drop-down list that keeps one width while it scrolls: every entry is laid out (no virtualizing), so the list is as
    /// wide as its widest entry instead of the widest one currently visible.</summary>
    internal static void SteadyWidth(ComboBox box) => box.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel());

    /// <summary>The status line under the filters (download progress, install results).</summary>
    public string Status
    {
        get => status.Text ?? string.Empty;
        set
        {
            status.Text = value;
            AppLog.Shown(value);
        }
    }

    /// <summary>For window pictures: opens the first mod of the list.</summary>
    public void OpenFirstForShot() => list.SelectedItem = (list.ItemsSource as IEnumerable<object>)?.OfType<GameBananaMod>().FirstOrDefault();

    /// <summary>Loads the categories and the first page the first time the view is shown.</summary>
    public async Task OpenAsync()
    {
        if (pageNumber > 0)
        {
            return;
        }

        try
        {
            var (top, heroes) = await client.CategoriesAsync();
            categories = [("All mods", null), .. heroes.Select(hero => ($"Hero: {hero.Name} ({hero.Count})", (long?)hero.Id)),
                .. top.Where(item => item.Id != GameBananaClient.SkinsCategory).Select(item => ($"{item.Name} ({item.Count})", (long?)item.Id))];
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Status = $"GameBanana could not be reached ({AppUpdates.Describe(exception)}).";
            AppLog.Error("GameBanana", exception);
        }

        opening = true;
        category.ItemsSource = categories.Select(item => item.Label).ToArray();
        category.SelectedIndex = 0;
        opening = false;
        await LoadAsync(more: false);
    }

    private async Task LoadAsync(bool more, bool clearSearch = false)
    {
        if (clearSearch && searching is not null)
        {
            searching = null;
            search.Text = string.Empty;
        }

        var ticket = ++request;
        var next = more ? pageNumber + 1 : 1;
        status.Text = "Loading...";
        try
        {
            var (mods, count) = searching is { } text
                ? await client.SearchAsync(text, next)
                : await client.ListAsync(GameBananaClient.Sorts[Math.Max(0, sort.SelectedIndex)].Key, categories.ElementAtOrDefault(Math.Max(0, category.SelectedIndex)).Id, next);
            if (ticket != request)
            {
                return;                                                     // a newer request replaced this one
            }

            if (!more)
            {
                rows.Clear();
            }

            rows.AddRange(mods);
            (pageNumber, total) = (next, count);
            ShowRows();
            status.Text = searching is { } shown ? $"{count} mods match '{shown}'." : $"{count} mods.";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Status = $"GameBanana could not be reached ({AppUpdates.Describe(exception)}).";
            AppLog.Error("GameBanana", exception);
        }
    }

    private void ShowRows()
    {
        var shown = rows.OfType<GameBananaMod>().Where(mod => mature.IsChecked == true || !mod.Mature).Cast<object>().ToList();
        var hidden = mature.IsChecked == true ? 0 : rows.OfType<GameBananaMod>().Count(mod => mod.Mature);
        if (rows.Count < total)
        {
            shown.Add("Load more");
        }

        list.ItemsSource = shown;
        if (hidden > 0)
        {
            status.Text = $"{status.Text?.Split(" (", 2)[0]} ({hidden} with content ratings hidden)";
        }
    }

    private Grid Card(GameBananaMod mod)
    {
        var image = new Image { Width = 110, Height = 62, Stretch = Stretch.UniformToFill };
        _ = LoadImageAsync(image, mod.Thumbnail);
        var text = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = mod.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 });
        text.Children.Add(new TextBlock { Text = $"{mod.Author} · ♥ {mod.Likes} · {mod.Updated.LocalDateTime:d}", Foreground = Muted, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 });
        text.Children.Add(new TextBlock { Text = mod.Hero is { } hero ? $"{mod.Category}: {hero}" : mod.Category, Foreground = Muted, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 });
        var card = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(2, 4) };
        card.Children.Add(new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = image });
        Grid.SetColumn(text, 1);
        card.Children.Add(text);
        return card;
    }

    private static void ShowInstalled(Button install, bool installed)
    {
        install.Content = installed ? "Installed \u2713" : "Install";
        install.IsEnabled = !installed;
        ToolTip.SetTip(install, installed ? "This file is in My mods. Delete it there to install it again." : null);
    }

    /// <summary>The screenshot strip: the mouse wheel scrolls it sideways (the page scrolls on once the strip is at its end), with
    /// arrow buttons and a scroll bar that stays visible.</summary>
    private static Grid Gallery(StackPanel strip)
    {
        var viewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            AllowAutoHide = false,
            Content = strip,
        };
        viewer.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            var room = Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width);
            var step = (e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X) * 120;
            var target = Math.Clamp(viewer.Offset.X - step, 0, room);
            if (room > 0 && Math.Abs(target - viewer.Offset.X) > 0.5)
            {
                viewer.Offset = viewer.Offset.WithX(target);
                e.Handled = true;                                           // otherwise the strip is at its end and the page scrolls
            }
        }, RoutingStrategies.Tunnel);
        Button Arrow(string text, int direction)
        {
            var button = new Button { Content = text, FontSize = 20, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 16), Opacity = 0.85 };
            button.Click += (_, _) =>
            {
                var room = Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width);
                viewer.Offset = viewer.Offset.WithX(Math.Clamp(viewer.Offset.X + direction * viewer.Viewport.Width * 0.8, 0, room));
            };
            return button;
        }

        var left = Arrow("\u2039", -1);
        var right = Arrow("\u203A", 1);
        void Update()
        {
            var room = viewer.Extent.Width - viewer.Viewport.Width;
            left.IsVisible = viewer.Offset.X > 0.5;
            right.IsVisible = room - viewer.Offset.X > 0.5;
        }

        viewer.ScrollChanged += (_, _) => Update();
        viewer.LayoutUpdated += (_, _) => Update();
        left.HorizontalAlignment = HorizontalAlignment.Left;
        right.HorizontalAlignment = HorizontalAlignment.Right;
        return new Grid { Children = { viewer, left, right } };
    }

    private async Task LoadImageAsync(Image image, string? url)
    {
        if (url is null || await client.ImageAsync(url) is not { } bytes)
        {
            return;
        }

        try
        {
            image.Source = new Bitmap(new MemoryStream(bytes));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // an image format Skia cannot decode: the card stays without a picture
        }
    }

    private async Task ShowPageAsync(GameBananaMod mod)
    {
        page.Content = new TextBlock { Text = $"Loading '{mod.Name}'...", Foreground = Muted, Margin = new Thickness(20) };
        GameBananaPage info;
        try
        {
            info = await client.PageAsync(mod.Id);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            page.Content = new SelectableTextBlock { Text = $"GameBanana could not be reached ({AppUpdates.Describe(exception)}).", Foreground = Warning, Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap };
            return;
        }

        if (!ReferenceEquals(list.SelectedItem, mod))
        {
            return;                                                         // another mod was picked meanwhile
        }

        var body = new StackPanel { Spacing = 10, Margin = new Thickness(20, 14) };
        body.Children.Add(new SelectableTextBlock { Text = info.Mod.Name, FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock
        {
            Text = $"by {info.Mod.Author} · {(info.Mod.Hero is { } hero ? $"{info.Mod.Category}: {hero}" : info.Mod.Category)} · ♥ {info.Mod.Likes} · {info.Mod.Views:N0} views · updated {info.Mod.Updated.LocalDateTime:d}"
                + (info.Mod.Version.Length > 0 ? $" · version {info.Mod.Version}" : string.Empty),
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap,
        });
        var open = new Button { Content = "Open on GameBanana" };
        open.Click += (_, _) => AppUpdates.OpenPage(info.Page);
        body.Children.Add(open);

        if (info.Images.Count > 0)
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var url in info.Images.Take(12))
            {
                var image = new Image { Height = 260, Stretch = Stretch.Uniform };
                _ = LoadImageAsync(image, url);
                strip.Children.Add(new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = image });
            }

            body.Children.Add(Gallery(strip));
        }

        body.Children.Add(new TextBlock { Text = info.Files.Count == 1 ? "File" : $"Files ({info.Files.Count}): pick the variant you want", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        foreach (var file in info.Files)
        {
            var install = new Button { Content = "Install", VerticalAlignment = VerticalAlignment.Center, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
            ShowInstalled(install, IsInstalled(file.Id));
            install.Click += async (_, _) =>
            {
                install.IsEnabled = false;
                var installed = InstallRequested is { } handler && await handler(info, file);
                ShowInstalled(install, installed || IsInstalled(file.Id));
            };
            var details = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            details.Children.Add(new SelectableTextBlock { Text = file.Name, FontWeight = FontWeight.SemiBold });
            details.Children.Add(new TextBlock
            {
                Text = $"{file.Size / 1048576.0:0.0} MB · {file.Added.LocalDateTime:d} · {file.Downloads:N0} downloads · virus scan: {(file.Scan.Length > 0 ? file.Scan : "not reported")}",
                Foreground = file.Scan is "clean" or "" ? Muted : Warning,
                FontSize = 12,
            });
            if (file.Description.Length > 0)
            {
                details.Children.Add(new TextBlock { Text = file.Description, Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            }

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 2) };
            row.Children.Add(install);
            Grid.SetColumn(details, 1);
            row.Children.Add(details);
            body.Children.Add(row);
        }

        if (info.Description.Length > 0)
        {
            body.Children.Add(new TextBlock { Text = "Description", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
            body.Children.Add(new SelectableTextBlock { Text = info.Description, TextWrapping = TextWrapping.Wrap });
        }

        if (info.Credits.Count > 0)
        {
            body.Children.Add(new TextBlock { Text = "Credits", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
            body.Children.Add(new SelectableTextBlock { Text = string.Join('\n', info.Credits), Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        }

        page.Content = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = body };
    }
}
