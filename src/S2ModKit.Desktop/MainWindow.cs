using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Desktop;

/// <summary>
/// Mods: every addon in the game's addons folder as a mod (Mod Manager split groups as one), with a
/// preview for model mods and an information panel for the rest. Any mod can be enabled, disabled (renamed in place) or deleted
/// (Recycle Bin); install takes a .vpk, .zip, .7z or .rar by button or by dropping it on the window.
/// </summary>
internal sealed class MainWindow : Window
{
    private static readonly IBrush Sidebar = new SolidColorBrush(Color.FromRgb(43, 45, 49));
    private static readonly IBrush Panel = new SolidColorBrush(Color.FromRgb(49, 51, 56));
    private static readonly IBrush Warning = new SolidColorBrush(Color.FromRgb(240, 170, 70));
    private static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(148, 155, 164));

    private AppSettings settings;
    private ModCatalog catalog;
    private PreviewPane pane;
    private SoundsPane? sounds;
    private readonly ListBox list = new() { Background = Brushes.Transparent };
    private readonly TextBox search = new() { PlaceholderText = "Search mods" };
    private readonly SelectableTextBlock listStatus = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };   // copyable errors
    private readonly ToggleButton flatView = new() { Content = "All mods", IsChecked = true };
    private readonly ToggleButton heroView = new() { Content = "By hero" };
    private readonly ComboBox sort = new() { ItemsSource = new[] { "Name", "Date added", "Load order" }, SelectedIndex = 0 };
    private readonly ComboBox heroFilter = new() { MinWidth = 170 };
    private readonly ComboBox kindFilter = new() { MinWidth = 120, ItemsSource = new[] { AllKinds, "model", "sound", "ui", "particles", "other", NeedsAttention }, SelectedIndex = 0 };
    private readonly SelectableTextBlock title = new() { FontSize = 20, FontWeight = FontWeight.SemiBold };
    private readonly SelectableTextBlock details = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel conflictPanel = new() { Spacing = 4 };
    private readonly ContentControl body = new();
    private const string AllHeroes = "All heroes";
    private const string AllKinds = "All kinds";

    /// <summary>Kind filter entry: only the mods the last "Check mods" marked (broken, repairable or worth a look).</summary>
    private const string NeedsAttention = "Flagged by Check mods";
    private IReadOnlyList<ModEntry> mods = [];
    private IReadOnlyDictionary<string, DoctorFinding> doctor = new Dictionary<string, DoctorFinding>();
    private readonly SelectableTextBlock updateBanner = new() { Foreground = Warning, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly StackPanel appUpdate = new() { Spacing = 4, IsVisible = false };
    private List<ListRow> rows = [];
    private bool byHero;

    public MainWindow(AppOptions options)
    {
        Title = "S2ModKit";
        Width = 1700;
        Height = 1000;
        settings = AppSettings.Load(options.Game);
        catalog = new ModCatalog(settings);
        pane = new PreviewPane(settings);
        pane.BuildEditedRequested += (mod, model, hidden) => _ = RunAsync(() => catalog.BuildEditedAsync(mod, model, hidden));
        pane.BuildRecolorRequested += (mod, model, colors) => _ = RunAsync(() => BuildRecolorAsync(mod, model, colors));
        list.ItemTemplate = new FuncDataTemplate<ListRow>((row, _) => row switch
        {
            HeaderRow header => new TextBlock { Text = header.Title, FontWeight = FontWeight.Bold, FontSize = 14, Margin = new Thickness(2, 10, 2, 2) },
            ModRow { Mod: var mod } item => ModItem(mod, item.Tags, item.LoadOrder, doctor.GetValueOrDefault(mod.Key)),
            BaseRow { Hero: var hero } => new StackPanel
            {
                Margin = new Thickness(2, 4),
                Children =
                {
                    new TextBlock { Text = $"{hero.Name} (base game)", FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "The game's own model, without mods", Foreground = Muted, FontSize = 12 },
                },
            },
            _ => new TextBlock(),
        });
        list.ContainerPrepared += (_, e) =>                                  // group headers are not selectable (containers are recycled)
        {
            var header = e.Index >= 0 && e.Index < rows.Count && rows[e.Index] is HeaderRow;
            e.Container.IsHitTestVisible = !header;
            e.Container.Focusable = !header;
        };
        list.SelectionChanged += (_, _) => ShowSelected();
        search.TextChanged += (_, _) => ApplySearch();
        heroFilter.SelectionChanged += (_, _) => ApplySearch();
        sort.SelectionChanged += (_, _) => ApplySearch();
        flatView.Click += (_, _) => SetView(false);
        heroView.Click += (_, _) => SetView(true);
        kindFilter.SelectionChanged += (_, _) => ApplySearch();

        var side = new DockPanel { Width = 380, Background = Sidebar };
        var header = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
        header.Children.Add(new TextBlock { Text = "Mods", FontSize = 22, FontWeight = FontWeight.Bold });
        header.Children.Add(new WrapPanel                                 // wraps to a second line in the narrow side panel
        {
            ItemSpacing = 6,
            LineSpacing = 6,
            Children =
            {
                Button("Install mod...", InstallAsync), Button("Refresh", () => LoadAsync()), SettingsButton(),
                Button("Open addons folder", () => ShowInExplorer(settings.AddonsRoot, select: false)),
                Button("Check mods", CheckModsAsync),
            },
        });
        header.Children.Add(search);
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { flatView, heroView, sort } });
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { heroFilter, kindFilter } });
        header.Children.Add(appUpdate);
        header.Children.Add(updateBanner);
        header.Children.Add(listStatus);
        DockPanel.SetDock(header, Dock.Top);
        side.Children.Add(header);
        side.Children.Add(list);

        var info = new StackPanel { Spacing = 6, Margin = new Thickness(16, 12) };
        info.Children.Add(title);
        info.Children.Add(details);
        info.Children.Add(actions);
        info.Children.Add(conflictPanel);
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

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            var dropped = (e.DataTransfer.TryGetFiles() ?? []).Select(item => item.TryGetLocalPath()).OfType<string>().ToArray();
            foreach (var path in dropped)
            {
                await InstallPathAsync(path);
            }
        });
        Opened += async (_, _) =>
        {
            await LoadAsync();
            if (settings.CheckUpdates)
            {
                await CheckForUpdateAsync(quiet: true);
            }
        };
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
            doctor = (current.LastReport()?.Findings ?? []).ToDictionary(finding => finding.Key, StringComparer.Ordinal);
            var build = await current.GameBuildAsync();
            updateBanner.IsVisible = await current.GameUpdatedSinceCheckAsync();
            updateBanner.Text = $"Deadlock was updated (build {build}). Press 'Check mods' to see which mods the update affects.";
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

    private ModEntry? SelectedMod => (list.SelectedItem as ModRow)?.Mod;

    private void SetView(bool heroes)
    {
        byHero = heroes;
        flatView.IsChecked = !heroes;
        heroView.IsChecked = heroes;
        sort.IsVisible = !heroes;
        ApplySearch();
    }

    /// <summary>The list for the current view, search and filters: flat (by name or date added, newest first), or one group per
    /// hero (a mod under each hero it changes, with that hero's tags) followed by Objects, World, Interface and Other.</summary>
    private void ApplySearch(string? select = null)
    {
        var text = search.Text ?? string.Empty;
        var current = select ?? SelectedMod?.Key;
        var hero = heroFilter.SelectedItem as string ?? AllHeroes;
        var kind = kindFilter.SelectedItem as string ?? AllKinds;
        var flaggedOnly = kind == NeedsAttention;
        var matches = mods.Where(mod => mod.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            && (!flaggedOnly || doctor.GetValueOrDefault(mod.Key) is { Verdict: not DoctorVerdict.Works })).ToArray();
        kind = flaggedOnly ? AllKinds : kind;
        var shown = new List<ListRow>();
        if (byHero)
        {
            // every hero, with or without mods, starts with its base-game row (so a hero without mods can be previewed too)
            var everyHero = text.Length == 0 && kind == AllKinds && !flaggedOnly ? Hero.Catalogue.Select(entry => entry.Name) : [];
            foreach (var name in matches.SelectMany(mod => mod.HeroNames).Concat(everyHero).Distinct().Order(StringComparer.OrdinalIgnoreCase).Where(name => hero is AllHeroes || name == hero))
            {
                var group = matches.Where(mod => mod.HeroNames.Contains(name))
                    .Select(mod => (mod, tags: mod.HeroKinds?.GetValueOrDefault(name) ?? [])).Where(item => kind == AllKinds
                    || (item.tags.Count > 0 ? item.tags.Any(tag => tag.Equals(kind, StringComparison.OrdinalIgnoreCase)) : item.mod.MainKind == kind)).ToArray();
                AddGroup(shown, name, group.Select(item => new ModRow(item.mod, item.tags)), Hero.FindByName(name) is { } found ? new BaseRow(found) : null);
            }

            foreach (var name in hero is AllHeroes ? ModAttribution.Groups : [])
            {
                AddGroup(shown, name, matches.Where(mod => mod.HeroNames.Count == 0 && mod.Group == name && (kind == AllKinds || mod.MainKind == kind)).Select(mod => new ModRow(mod)));
            }
        }
        else
        {
            var flat = matches.Where(mod => (hero == AllHeroes || mod.HeroNames.Contains(hero)) && (kind == AllKinds || mod.MainKind == kind));
            var loadOrder = sort.SelectedIndex == 2;
            shown.AddRange((sort.SelectedIndex switch
            {
                1 => flat.OrderByDescending(mod => mod.Added).ThenBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase),
                2 => flat.OrderBy(mod => mod.Loaded ? 0 : 1).ThenBy(mod => mod.FirstSlot).ThenBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase),   // enabled first, lowest pak number (wins) first
                _ => flat.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase),
            }).Select(mod => new ModRow(mod, null, loadOrder)));
        }

        if (select is not null && !shown.OfType<ModRow>().Any(row => row.Mod.Key == select) && mods.Any(mod => mod.Key == select)
            && (text.Length > 0 || hero != AllHeroes || flaggedOnly || kind != AllKinds))
        {
            search.Text = string.Empty;                                     // a mod just installed or changed is always shown:
            heroFilter.SelectedItem = AllHeroes;                            // clear the filters that hide it, then list again
            kindFilter.SelectedItem = AllKinds;
            ApplySearch(select);
            return;
        }

        rows = shown;
        list.ItemsSource = shown;
        list.SelectedItem = shown.OfType<ModRow>().FirstOrDefault(row => row.Mod.Key == current);
    }

    /// <summary>" It shares files with 'A' and 2 other enabled mods: see the conflicts beside it." for a just installed or enabled
    /// mod that loses or shares files; empty otherwise.</summary>
    private string ConflictNote(string key)
    {
        if (mods.FirstOrDefault(mod => mod.Key == key) is not { Loaded: true } mod || mod.ConflictInfo.With.Count + mod.ConflictInfo.ModelWarnings.Count == 0)
        {
            return string.Empty;
        }

        var names = mod.ConflictInfo.With.Select(other => $"'{other.OtherName}'").ToArray();
        var who = names.Length switch
        {
            0 => "another model mod for the same hero",
            1 => names[0],
            _ => $"{names[0]} and {names.Length - 1} other enabled mod(s)",
        };
        return $" It shares files with {who}: see the conflicts beside it.";
    }

    private static void AddGroup(List<ListRow> shown, string title, IEnumerable<ModRow> group, BaseRow? baseGame = null)
    {
        var items = group.OrderBy(row => row.Mod.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (items.Length > 0 || baseGame is not null)
        {
            shown.Add(new HeaderRow($"{title} ({items.Length})"));
            if (baseGame is not null)
            {
                shown.Add(baseGame);
            }

            shown.AddRange(items);
        }
    }

    /// <summary>A list item: name, status line (flat view: heroes, status, kind), and in the hero view the tags of what the mod changes.</summary>
    private static StackPanel ModItem(ModEntry mod, IReadOnlyList<string>? tags, bool loadOrder = false, DoctorFinding? finding = null)
    {
        var item = new StackPanel
        {
            Margin = new Thickness(2, 4),
            Children =
            {
                new TextBlock { Text = mod.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock
                {
                    Text = tags is null
                        ? $"{(loadOrder && mod.Loaded ? $"pak{mod.FirstSlot:00} - " : string.Empty)}{(mod.HeroNames.Count > 0 ? string.Join(", ", mod.HeroNames) + " - " : string.Empty)}{mod.StatusText} - {(mod.MainKind == "model" ? "model" : mod.KindText)}"
                        : mod.StatusText,
                    Foreground = Muted, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };
        var badges = mod.ConflictInfo.Badges;
        var health = finding?.Verdict switch
        {
            DoctorVerdict.Broken => "\u26A0 broken after update",
            DoctorVerdict.Repairable => "\u26A0 repairable",
            DoctorVerdict.Look => "check after update",
            _ => null,
        };
        if (tags is { Count: > 0 } || badges.Count > 0 || health is not null)
        {
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 3, 0, 0) };
            chips.Children.AddRange((tags ?? []).Select(tag => Chip(tag, Muted)));
            chips.Children.AddRange(badges.Select(badge => Chip(badge is "wins" ? "wins" : "\u26A0 " + badge, badge is "wins" ? Muted : Warning)));
            if (health is not null)
            {
                chips.Children.Add(Chip(health, finding!.Verdict == DoctorVerdict.Look ? Muted : Warning));
            }

            item.Children.Add(chips);
        }

        return item;
    }

    private static Border Chip(string text, IBrush foreground) => new()
    {
        Background = Panel,
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(7, 1),
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = foreground },
    };

    private void ShowSounds(ModEntry mod)
    {
        sounds?.Release();
        sounds = new SoundsPane(settings);
        body.Content = sounds;
        _ = sounds.ShowAsync(mod, settings);
    }

    private void ShowSelected()
    {
        sounds?.Release();                                                 // stops playback and closes the game archives
        sounds = null;
        actions.Children.Clear();
        conflictPanel.Children.Clear();
        if (list.SelectedItem is BaseRow { Hero: var baseHero })
        {
            title.Text = $"{baseHero.Name} (base game)";
            details.Text = $"The game's own model, without mods: {baseHero.Model}.";
            body.Content = pane;
            pane.Show(null, mods, baseHero.Model);
            return;
        }

        if (SelectedMod is not { } mod)
        {
            title.Text = string.Empty;
            details.Text = string.Empty;
            body.Content = null;
            return;
        }

        title.Text = mod.Name;
        details.Text = $"{mod.StatusText}. Files: {string.Join(", ", mod.Files.Select(Path.GetFileName))}. Contents: {mod.KindText}."
            + (mod.Models.Count > 0 ? $" Model: {string.Join(", ", mod.Models)}." : string.Empty)
            + (mod.StrayHeroes is { Count: > 0 } stray ? $" Also changes a few files of {string.Join(", ", stray)} (not listed under that hero)." : string.Empty)
            + (mod.Source == ModSource.Manager ? " Known to Deadlock Mod Manager: switching it here renames its files, so the manager's own list can show an old state." : string.Empty);
        actions.Children.Add(mod.Loaded
            ? Button("Disable", () => RunAsync(() => catalog.DisableAsync(mod)))
            : Button("Enable", () => EnableAsync(mod)));
        actions.Children.Add(Button("Delete", () => DeleteAsync(mod)));
        actions.Children.Add(Button("Show in folder", () => ShowInExplorer(mod.Files[0], select: true)));
        if (!byHero && sort.SelectedIndex == 2 && mod.Loaded && !mod.Key.StartsWith("app:", StringComparison.Ordinal))
        {
            var order = ModCatalog.LoadOrderOf(mods).Select(item => item.Key).ToList();
            order = [.. mods.Where(other => order.Contains(other.Key)).OrderBy(other => other.FirstSlot).Select(other => other.Key)];
            var at = order.IndexOf(mod.Key);
            var up = Button("Load earlier (lower pak number)", () => RunAsync(() => catalog.MoveAsync(mod, -1, mods)));
            var down = Button("Load later", () => RunAsync(() => catalog.MoveAsync(mod, 1, mods)));
            up.IsEnabled = at > 0;
            down.IsEnabled = at >= 0 && at < order.Count - 1;
            actions.Children.Add(new TextBlock { Text = $"pak{mod.FirstSlot:00}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) });
            actions.Children.Add(up);
            actions.Children.Add(down);
        }

        ShowConflicts(mod);
        if (doctor.GetValueOrDefault(mod.Key) is { Reasons.Count: > 0 } finding)
        {
            foreach (var reason in finding.Reasons)
            {
                conflictPanel.Children.Add(new SelectableTextBlock { Text = "\u26A0 " + reason, Foreground = Warning, TextWrapping = TextWrapping.Wrap });
            }

            if (finding.Verdict == DoctorVerdict.Repairable)
            {
                actions.Children.Add(Button("Repair", () => RunAsync(() => catalog.RepairAsync(mod, finding.StaleOverrides))));
            }
        }
        if (mod.VersionList.Count > 1)
        {
            var versions = new ComboBox
            {
                ItemsSource = mod.VersionList,
                SelectedItem = mod.VersionList.FirstOrDefault(version => version.Key == mod.Key),
                MinWidth = 260,
                ItemTemplate = new FuncDataTemplate<ModVersion>((version, _) => new TextBlock { Text = version is null ? string.Empty : version.Label + (version.Active ? " (enabled)" : string.Empty) }),
            };
            actions.Children.Add(new TextBlock { Text = "Versions:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) });
            actions.Children.Add(versions);
            actions.Children.Add(Button("Use this version", () => versions.SelectedItem is ModVersion picked && mods.FirstOrDefault(other => other.Key == picked.Key) is { } chosen
                ? RunAsync(() => catalog.ActivateVersionAsync(chosen, mods))
                : Task.CompletedTask));
        }

        var hasSounds = mod.Entries.Any(entry => entry.EndsWith(".vsnd_c", StringComparison.OrdinalIgnoreCase) || entry.EndsWith(".vsndevts_c", StringComparison.OrdinalIgnoreCase));
        if (hasSounds && mod.Models.Count > 0)
        {
            actions.Children.Add(Button("Sounds", () =>
            {
                ShowSounds(mod);
                return Task.CompletedTask;
            }));
        }

        if (mod.Models.Count > 0)
        {
            body.Content = pane;
            pane.Show(mod, mods);
            return;
        }

        if (hasSounds)
        {
            ShowSounds(mod);
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

    /// <summary>Warnings (two model mods, shared HUD files) always shown; the shared files per other mod and kind behind an expander.</summary>
    private void ShowConflicts(ModEntry mod)
    {
        var info = mod.ConflictInfo;
        foreach (var warning in info.ModelWarnings.Concat(info.With.SelectMany(item => item.Notes.Select(note => $"{note} (with '{item.OtherName}')"))))
        {
            conflictPanel.Children.Add(new SelectableTextBlock { Text = "\u26A0 " + warning, Foreground = Warning, TextWrapping = TextWrapping.Wrap });
        }

        if (info.With.Count == 0)
        {
            return;
        }

        const int Preview = 4;
        var content = new StackPanel { Spacing = 8, Margin = new Thickness(12, 4) };
        foreach (var with in info.With)
        {
            content.Children.Add(new SelectableTextBlock { Text = $"{with.OtherName}: this mod wins {with.Wins}, loses {with.Loses}", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            foreach (var (kind, wins, loses) in with.ByKind)
            {
                var paths = with.Paths.Where(path => path.Kind == kind).ToArray();
                string Line(SharedPath path) => $"{(path.Wins ? "wins " : "loses to " + with.OtherName)}  {path.Path}";
                content.Children.Add(new SelectableTextBlock
                {
                    Text = $"{kind} {paths.Length} (wins {wins}, loses {loses})\n" + string.Join('\n', paths.Take(Preview).Select(Line)),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Foreground = Muted,
                });
                if (paths.Length > Preview)
                {
                    content.Children.Add(new Expander
                    {
                        Header = $"{paths.Length - Preview} more {kind} file(s)",
                        Content = new SelectableTextBlock
                        {
                            Text = string.Join('\n', paths.Skip(Preview).Take(500).Select(Line)) + (paths.Length - Preview > 500 ? $"\n... {paths.Length - Preview - 500} more" : string.Empty),
                            FontFamily = new FontFamily("Consolas"),
                            FontSize = 12,
                            Foreground = Muted,
                        },
                    });
                }
            }
        }

        conflictPanel.Children.Add(new Expander
        {
            Header = $"Conflicts with {info.With.Count} mod(s): wins {info.Wins}, loses {info.Loses} shared file(s)",
            Content = new ScrollViewer { MaxHeight = 360, Content = content },
        });
    }

    private async Task InstallAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Install a mod (.vpk, .zip, .7z or .rar)",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Deadlock mod or archive") { Patterns = ["*.vpk", "*.zip", "*.7z", "*.rar"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            await InstallPathAsync(path);
        }
    }

    /// <summary>Installs a .vpk, or the .vpk variants of a .zip, .7z or .rar chosen by the user when the archive holds several.</summary>
    private async Task InstallPathAsync(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".vpk":
                await RunAsync(async () =>
                {
                    var result = await catalog.InstallAsync(path, name);
                    return new CatalogChange(result.Receipt is { } receipt ? AddonNaming.FileKey(receipt.TargetFileName) : string.Empty, $"Installed '{name}'.");
                });
                break;
            case ".zip" or ".7z" or ".rar":
                IReadOnlyList<ArchiveVariant> variants;
                try
                {
                    variants = await Task.Run(() => catalog.ReadArchiveAsync(path));
                }
#pragma warning disable CA1031 // shown in the window
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    listStatus.Text = exception.Message;
                    return;
                }

                var chosen = variants.Count == 1 ? variants : await new VariantChooser(name, variants).ShowDialog<IReadOnlyList<ArchiveVariant>?>(this);
                if (chosen is { Count: > 0 })
                {
                    await RunAsync(() => catalog.InstallArchiveAsync(path, chosen));
                }

                break;
            default:
                listStatus.Text = $"'{Path.GetFileName(path)}' is not a .vpk, .zip, .7z or .rar file.";
                break;
        }
    }

    /// <summary>Enables the mod; when it would replace the same files as an enabled mod (or add a second model for a hero), says so
    /// first and enables only on "Enable anyway".</summary>
    private async Task EnableAsync(ModEntry mod)
    {
        var clashes = ModConflictAnalysis.WouldConflict(mod, mods);
        if (clashes.Count > 0 && !await new ConfirmDialog($"Enabling '{mod.Name}' would conflict", string.Join(Environment.NewLine, clashes)
            + Environment.NewLine + Environment.NewLine + "The mod with the lower pak number wins; you can change the order with the Load order sort.", "Enable anyway").ShowDialog<bool>(this))
        {
            return;
        }

        await RunAsync(() => catalog.EnableAsync(mod));
    }

    private async Task DeleteAsync(ModEntry mod)
    {
        var shown = mod.Files.Select(file => Path.GetFileName(file)).Take(6);
        if (await new ConfirmDialog($"Delete '{mod.Name}'?", $"Its files go to the Recycle Bin, where you can restore them: {string.Join(", ", shown)}{(mod.Files.Count > 6 ? ", ..." : string.Empty)}.", "Delete").ShowDialog<bool>(this))
        {
            await RunAsync(() => catalog.DeleteAsync(mod));
        }
    }

    /// <summary>Collects the masks of the recolored parts from the open preview, then builds the recolored copy.</summary>
    private async Task<CatalogChange> BuildRecolorAsync(ModEntry? mod, string model, IReadOnlyDictionary<int, S2ModKit.Application.ColorAdjustment> colors)
    {
        var session = pane.Control.Session ?? throw Errors.Input("RECOLOR_NO_PREVIEW", "The preview is not open.", "Open the mod's preview first.");
        var parts = new List<PartRecolor>();
        foreach (var (part, change) in colors)
        {
            if (session.PartColorTexture(part) is { } texture && session.PartColorMask(part) is { } mask)
            {
                parts.Add(new PartRecolor(part, texture, mask.Mask, mask.Width, mask.Height, change));
            }
        }

        return await catalog.BuildRecoloredAsync(mod, model, parts);
    }

    /// <summary>Runs the mod doctor over every mod (read-only) and shows what the last game update affected.</summary>
    private async Task CheckModsAsync()
    {
        try
        {
            await pane.ReleaseAsync();
            var progress = new Progress<string>(text => listStatus.Text = text);
            var report = await catalog.CheckModsAsync(mods, progress);
            doctor = report.Findings.ToDictionary(finding => finding.Key, StringComparer.Ordinal);
            updateBanner.IsVisible = false;
            int Count(DoctorVerdict verdict) => report.Findings.Count(finding => finding.Verdict == verdict);
            var flagged = report.Findings.Where(finding => finding.Verdict != DoctorVerdict.Works)
                .Select(finding => mods.FirstOrDefault(mod => mod.Key == finding.Key)?.Name).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToArray();
            listStatus.Text = report.ComparedWith is null
                ? $"Recorded what your {report.Findings.Count} mods depend on in game build {report.Build}. After the next game update, 'Check mods' shows what changed for each mod."
                : $"Compared with game build {report.ComparedWith}: {Count(DoctorVerdict.Broken)} broken, {Count(DoctorVerdict.Repairable)} repairable, {Count(DoctorVerdict.Look)} worth a look, {Count(DoctorVerdict.Works)} unaffected."
                    + (flagged.Length > 0 ? $" Showing only those: {string.Join(", ", flagged)}. Click one to see why; pick 'All kinds' to see every mod." : string.Empty);
            if (flagged.Length > 0)
            {
                kindFilter.SelectedItem = NeedsAttention;                     // the flagged mods are what the user wants to see now
            }

            ApplySearch();
        }
#pragma warning disable CA1031 // shown in the window
        catch (Exception exception)
#pragma warning restore CA1031
        {
            listStatus.Text = "Could not check the mods: " + (exception is S2ModKitException { Error: var error } ? $"{error.Summary} {error.Remediation}" : exception.Message);
        }
    }

    /// <summary>Runs an install, enable, disable, delete or version switch (refused with a message while the game runs), reloads the
    /// list and selects the mod under the key the operation reports.</summary>
    private async Task RunAsync(Func<Task<CatalogChange>> action)
    {
        try
        {
            await pane.ReleaseAsync();                                     // the preview keeps the mod's archive open
            var change = await Task.Run(action);
            await LoadAsync(change.SelectKey.Length > 0 ? change.SelectKey : null);
            listStatus.Text = change.Message + ConflictNote(change.SelectKey);
        }
#pragma warning disable CA1031 // shown in the window (a refused install names the conflicting mods)
        catch (Exception exception)
#pragma warning restore CA1031
        {
            listStatus.Text = exception is S2ModKitException { Error: var error } ? $"{error.Summary} {error.Remediation}" : exception.Message;
            await LoadAsync();                                             // the preview was released: show the current state again
            listStatus.Text = exception is S2ModKitException { Error: var shown } ? $"{shown.Summary} {shown.Remediation}" : exception.Message;
        }
    }

    /// <summary>"Settings" with the rarely used choices in one menu: game folder, data folder, updates.</summary>
    private Button SettingsButton()
    {
        var button = new Button { Content = "Settings ▾" };
        var updates = new MenuItem { Header = "Look for updates on start", ToggleType = MenuItemToggleType.CheckBox, IsChecked = settings.CheckUpdates };
        updates.Click += (_, _) =>
        {
            settings = settings with { CheckUpdates = !settings.CheckUpdates };
            settings.Save();
            updates.IsChecked = settings.CheckUpdates;
        };
        var game = new MenuItem { Header = "Game folder..." };
        game.Click += async (_, _) => await PickGameAsync();
        var data = new MenuItem { Header = "Data folder..." };
        data.Click += async (_, _) => await PickDataAsync();
        var now = new MenuItem { Header = "Look for updates now" };
        now.Click += async (_, _) => await CheckForUpdateAsync(quiet: false);
        var flyout = new MenuFlyout();
        foreach (var item in new[] { game, data, now, updates })
        {
            flyout.Items.Add(item);
        }

        button.Flyout = flyout;
        return button;
    }

    /// <summary>Moves the app's data (journals, install records, caches) to a new empty folder; the old one is left as it was.</summary>
    private async Task PickDataAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = $"Data folder for S2ModKit (now {settings.Data})" });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            var old = settings.Data;
            settings = settings.MoveData(path);
            settings.Save();
            await ApplySettingsAsync();
            listStatus.Text = $"The app's data is now in {settings.Data}. The old folder {old} was left as it was; you can delete it.";
        }
        catch (IOException exception)
        {
            listStatus.Text = exception.Message;
        }
    }

    /// <summary>Asks GitHub for a newer release (once, on request or on start) and shows "Update" when there is one.</summary>
    private async Task CheckForUpdateAsync(bool quiet)
    {
        var update = await AppUpdates.CheckAsync();
        appUpdate.Children.Clear();
        appUpdate.IsVisible = update is not null;
        if (update is null)
        {
            if (!quiet)
            {
                listStatus.Text = $"S2ModKit {AppUpdates.Current.ToString(3)} is the latest version (or GitHub could not be reached).";
            }

            return;
        }

        appUpdate.Children.Add(new TextBlock { Text = $"S2ModKit {update.Version.ToString(3)} is available (you have {AppUpdates.Current.ToString(3)}).", Foreground = Warning, TextWrapping = TextWrapping.Wrap });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (AppSettings.Installed && update.Setup is not null)
        {
            row.Children.Add(Button("Update now", async () =>
            {
                listStatus.Text = "Downloading the update...";
                try
                {
                    await AppUpdates.InstallAsync(update, Path.Combine(settings.Data, "updates"));
                    Close();                                                // the setup replaces the files and starts the app again
                }
#pragma warning disable CA1031 // shown in the window
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    listStatus.Text = "Could not update: " + exception.Message;
                }
            }));
        }

        row.Children.Add(Button("What's new", () =>
        {
            AppUpdates.OpenPage(update.Page);
            return Task.CompletedTask;
        }));
        appUpdate.Children.Add(row);
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
        await ApplySettingsAsync();
    }

    /// <summary>Recreates what depends on the settings (game and data folders) and lists the mods again.</summary>
    private async Task ApplySettingsAsync()
    {
        catalog = new ModCatalog(settings);
        pane = new PreviewPane(settings);
        pane.BuildEditedRequested += (mod, model, hidden) => _ = RunAsync(() => catalog.BuildEditedAsync(mod, model, hidden));
        pane.BuildRecolorRequested += (mod, model, colors) => _ = RunAsync(() => BuildRecolorAsync(mod, model, colors));
        body.Content = null;
        await LoadAsync();
    }

    private static Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) => await action();
        return button;
    }

    /// <summary>Opens Explorer on a folder, or on a file's folder with the file selected (a disabled app install's package too).</summary>
    private Task ShowInExplorer(string path, bool select)
    {
        var arguments = select && File.Exists(path) ? $"/select,\"{path}\"" : $"\"{(Directory.Exists(path) ? path : Path.GetDirectoryName(path))}\"";
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            listStatus.Text = "Could not open Explorer: " + exception.Message;
        }

        return Task.CompletedTask;
    }
}

/// <summary>A row of the mod list: a group header (hero view) or a mod, with the tags of what it changes for the hero it is listed under.</summary>
internal abstract record ListRow;

internal sealed record HeaderRow(string Title) : ListRow;

/// <summary>A hero's base-game model at the top of its group in the hero view.</summary>
internal sealed record BaseRow(Hero Hero) : ListRow;

internal sealed record ModRow(ModEntry Mod, IReadOnlyList<string>? Tags = null, bool LoadOrder = false) : ListRow;

/// <summary>A yes/no question in a small window; the result of <c>ShowDialog&lt;bool&gt;</c> is true for the confirm button.</summary>
internal sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirm)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var yes = new Button { Content = confirm };
        var no = new Button { Content = "Cancel" };
        yes.Click += (_, _) => Close(true);
        no.Click += (_, _) => Close(false);
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } },
            },
        };
    }
}

/// <summary>Lists the variants (a _dir.vpk with its chunks) in an archive with a check box each; the result of <c>ShowDialog</c> is the
/// chosen variants, or null when cancelled.</summary>
internal sealed class VariantChooser : Window
{
    public VariantChooser(string archive, IReadOnlyList<ArchiveVariant> variants)
    {
        Title = "Choose what to install";
        Width = 760;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var boxes = variants.Select((variant, index) => new CheckBox
        {
            IsChecked = index == 0,
            Content = $"{variant.FileName}   {FormatSize(variant.TotalSize)}   {(variant.Folder.Length > 0 ? "in " + variant.Folder : "(archive root)")}"
                + (variant.Files.Count > 1 ? $"   +{variant.Files.Count - 1} data file(s)" : string.Empty),
        }).ToArray();
        var install = new Button { Content = "Install" };
        var cancel = new Button { Content = "Cancel" };
        install.Click += (_, _) => Close((IReadOnlyList<ArchiveVariant>?)[.. variants.Where((_, index) => boxes[index].IsChecked == true)]);
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { install, cancel } };
        var list = new StackPanel { Spacing = 6 };
        list.Children.AddRange(boxes);
        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);
        var heading = new TextBlock { Text = $"'{archive}' holds {variants.Count} packages. Pick one or more to install (a package that shares files with an enabled mod is refused).", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);
        layout.Children.Add(new ScrollViewer { Content = list });
        Content = layout;
    }

    private static string FormatSize(long bytes) => bytes >= 1 << 20 ? $"{bytes / (1024.0 * 1024):0.#} MB" : $"{bytes / 1024.0:0.#} KB";
}
