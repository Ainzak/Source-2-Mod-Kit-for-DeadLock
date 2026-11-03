using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using S2ModKit.Application;

namespace S2ModKit.Desktop;

/// <summary>
/// Mods: every addon in the game's addons folder as a mod (Mod Manager split groups as one), with a
/// preview for model mods and an information panel for the rest. The app installs, disables and re-enables only what it
/// installed; Mod Manager mods are read-only here.
/// </summary>
internal sealed class MainWindow : Window
{
    private static readonly IBrush Sidebar = new SolidColorBrush(Color.FromRgb(43, 45, 49));
    private static readonly IBrush Panel = new SolidColorBrush(Color.FromRgb(49, 51, 56));
    private static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(148, 155, 164));

    private AppSettings settings;
    private ModCatalog catalog;
    private PreviewPane pane;
    private readonly ListBox list = new() { Background = Brushes.Transparent };
    private readonly TextBox search = new() { PlaceholderText = "Search mods" };
    private readonly SelectableTextBlock listStatus = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };   // copyable errors
    private readonly ComboBox heroFilter = new() { MinWidth = 170 };
    private readonly ComboBox kindFilter = new() { MinWidth = 120, ItemsSource = new[] { AllKinds, "model", "sound", "ui", "particles", "other" }, SelectedIndex = 0 };
    private readonly TextBlock title = new() { FontSize = 20, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock details = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly ContentControl body = new();
    private const string AllHeroes = "All heroes";
    private const string AllKinds = "All kinds";
    private IReadOnlyList<ModEntry> mods = [];

    public MainWindow(AppOptions options)
    {
        Title = "S2ModKit";
        Width = 1700;
        Height = 1000;
        settings = AppSettings.Load(options.Game);
        catalog = new ModCatalog(settings);
        pane = new PreviewPane(settings);
        list.ItemTemplate = new FuncDataTemplate<ModEntry>((mod, _) => mod is null ? new TextBlock() : new StackPanel
        {
            Margin = new Thickness(2, 4),
            Children =
            {
                new TextBlock { Text = mod.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock
                {
                    Text = $"{(mod.HeroNames.Count > 0 ? string.Join(", ", mod.HeroNames) + " - " : string.Empty)}{mod.StatusText} - {(mod.Models.Count > 0 ? "model" : mod.KindText)}",
                    Foreground = Muted, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        });
        list.SelectionChanged += (_, _) => ShowSelected();
        search.TextChanged += (_, _) => ApplySearch();
        heroFilter.SelectionChanged += (_, _) => ApplySearch();
        kindFilter.SelectionChanged += (_, _) => ApplySearch();

        var side = new DockPanel { Width = 380, Background = Sidebar };
        var header = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
        header.Children.Add(new TextBlock { Text = "Mods", FontSize = 22, FontWeight = FontWeight.Bold });
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { Button("Install VPK...", InstallAsync), Button("Refresh", () => LoadAsync()), Button("Game folder...", PickGameAsync) },
        });
        header.Children.Add(search);
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { heroFilter, kindFilter } });
        header.Children.Add(listStatus);
        DockPanel.SetDock(header, Dock.Top);
        side.Children.Add(header);
        side.Children.Add(list);

        var info = new StackPanel { Spacing = 6, Margin = new Thickness(16, 12) };
        info.Children.Add(title);
        info.Children.Add(details);
        info.Children.Add(actions);
        var main = new DockPanel { Background = Panel };
        DockPanel.SetDock(info, Dock.Top);
        main.Children.Add(info);
        main.Children.Add(body);

        var root = new DockPanel();
        DockPanel.SetDock(side, Dock.Left);
        root.Children.Add(side);
        root.Children.Add(main);
        Content = root;

        if (options.SelfTest is { } prefix)                                  // measurements on a vanilla model, then exit
        {
            var test = new SelfTest(prefix, pane.Control, () => Dispatcher.UIThread.Post(Close));
            pane.Control.Opened += test.Opened;
            pane.Control.AfterFrame = test.Step;
            pane.Control.Outline = options.Outline ?? pane.Control.Outline;
            title.Text = "Self-test: " + options.Model;
            body.Content = pane;
            var entry = options.Addon is null ? null : new ModEntry("selftest", Path.GetFileName(options.Addon), ModSource.Other, true, [options.Addon], [null],
                new Dictionary<string, int>(), [], [options.Model], null);
            pane.Show(entry, entry is null ? [] : [entry], options.Model);
            return;
        }

        Opened += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync(string? select = null)
    {
        if (!settings.GameFound)
        {
            listStatus.Text = $"Deadlock not found at {settings.Game}. Choose the game folder (the one with 'citadel').";
            return;
        }

        listStatus.Text = "Reading the addons folder...";
        try
        {
            var current = catalog;
            mods = await Task.Run(() => current.LoadAsync());
            listStatus.Text = $"{mods.Count} mods, {mods.Count(mod => mod.Loaded)} loaded. Addons: {settings.AddonsRoot}";
            var hero = heroFilter.SelectedItem as string;
            heroFilter.ItemsSource = (string[])[AllHeroes, .. mods.SelectMany(mod => mod.HeroNames).Distinct().Order(StringComparer.OrdinalIgnoreCase)];
            heroFilter.SelectedItem = hero is not null && mods.Any(mod => mod.HeroNames.Contains(hero)) ? hero : AllHeroes;
            ApplySearch(select);
        }
#pragma warning disable CA1031 // shown in the window
        catch (Exception exception)
#pragma warning restore CA1031
        {
            listStatus.Text = "Could not read the addons folder: " + exception.Message;
        }
    }

    private void ApplySearch(string? select = null)
    {
        var text = search.Text ?? string.Empty;
        var current = select ?? (list.SelectedItem as ModEntry)?.Key;
        var hero = heroFilter.SelectedItem as string ?? AllHeroes;
        var kind = kindFilter.SelectedItem as string ?? AllKinds;
        var shown = mods.Where(mod => mod.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            && (hero == AllHeroes || mod.HeroNames.Contains(hero)) && (kind == AllKinds || mod.MainKind == kind)).ToArray();
        list.ItemsSource = shown;
        list.SelectedItem = shown.FirstOrDefault(mod => mod.Key == current);
    }

    private void ShowSelected()
    {
        actions.Children.Clear();
        if (list.SelectedItem is not ModEntry mod)
        {
            title.Text = string.Empty;
            details.Text = string.Empty;
            body.Content = null;
            return;
        }

        title.Text = mod.Name;
        details.Text = $"{mod.StatusText}. Files: {string.Join(", ", mod.Files.Select(Path.GetFileName))}. Contents: {mod.KindText}."
            + (mod.Models.Count > 0 ? $" Model: {string.Join(", ", mod.Models)}." : string.Empty)
            + mod.Source switch
            {
                ModSource.Manager => " Managed by Deadlock Mod Manager: switch it on or off there.",
                ModSource.Other => " Installed outside this app: read-only here.",
                _ => string.Empty,
            };
        if (mod.Install is { } install)
        {
            actions.Children.Add(mod.Loaded
                ? Button("Disable", () => RunAsync(() => catalog.DisableAsync(install), mod.Name,
                    $"Disabled '{mod.Name}': its file was taken out of the addons folder. It stays in this list; Enable puts it back."))
                : Button("Enable", () => RunAsync(() => catalog.EnableAsync(install), mod.Name, $"Enabled '{mod.Name}'.")));
        }

        if (mod.Models.Count > 0)
        {
            body.Content = pane;
            pane.Show(mod, mods);
            return;
        }

        body.Content = new ScrollViewer
        {
            Margin = new Thickness(16, 0),
            Content = new TextBlock
            {
                Text = "No model to preview (" + mod.KindText + ").\n\n" + string.Join('\n', mod.Entries.Take(300))
                    + (mod.Entries.Count > 300 ? $"\n... {mod.Entries.Count - 300} more" : string.Empty),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
            },
        };
    }

    private async Task InstallAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Install a mod (VPK)",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Deadlock mod") { Patterns = ["*.vpk"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        await RunAsync(() => catalog.InstallAsync(path, name), name, $"Installed '{name}'.");
    }

    /// <summary>Runs an install, disable or enable; selects <paramref name="selectName"/> afterwards and shows
    /// <paramref name="done"/> (the installer's own summary speaks of removing files, which read as deleting the mod).</summary>
    private async Task RunAsync(Func<Task<AddonFileResult>> action, string selectName, string done)
    {
        try
        {
            await pane.ReleaseAsync();                                     // the preview keeps the mod's archive open
            await Task.Run(action);
            await LoadAsync();
            list.SelectedItem = (list.ItemsSource as IEnumerable<ModEntry>)?.FirstOrDefault(mod => mod.Name == selectName) ?? list.SelectedItem;
            listStatus.Text = done;
        }
#pragma warning disable CA1031 // shown in the window (a refused install names the conflicting mods)
        catch (Exception exception)
#pragma warning restore CA1031
        {
            listStatus.Text = exception.Message;
        }
    }

    private async Task PickGameAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Deadlock 'game' folder (contains 'citadel')" });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        settings = settings with { Game = path };
        settings.Save();
        catalog = new ModCatalog(settings);
        pane = new PreviewPane(settings);
        body.Content = null;
        await LoadAsync();
    }

    private static Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) => await action();
        return button;
    }
}
