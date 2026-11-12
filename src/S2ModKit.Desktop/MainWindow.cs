using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
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
    private static readonly IBrush Sidebar = AppTheme.Side;
    private static readonly IBrush Panel = AppTheme.Main;
    private static readonly IBrush Warning = AppTheme.Warning;
    private static readonly IBrush Muted = AppTheme.Muted;

    private AppSettings settings;
    private ModCatalog catalog;
    private PreviewPane pane;
    private SoundsPane? sounds;
    private Control? myMods;
    private BrowseView? browse;
    private GameBananaClient? gameBanana;
    private IReadOnlyDictionary<string, ModUpdate> updates = new Dictionary<string, ModUpdate>();
    private readonly ListBox list = new() { Background = Brushes.Transparent };
    private readonly TextBox search = new() { PlaceholderText = "Search mods" };
    private readonly SelectableTextBlock listStatus = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };   // copyable errors
    private readonly ToggleButton flatView = new() { Content = "All mods", IsChecked = true };
    private readonly ToggleButton heroView = new() { Content = "By hero" };
    private readonly ComboBox sort = new() { ItemsSource = new[] { "Name", "Date added", "Load order" }, SelectedIndex = 0 };
    private readonly ComboBox heroFilter = new() { MinWidth = 170 };
    private readonly ComboBox kindFilter = new() { MinWidth = 120, ItemsSource = new[] { AllKinds, "model", "sound", "ui", "particles", "other", NeedsAttention, WithUpdates }, SelectedIndex = 0 };
    private readonly SelectableTextBlock title = new() { FontSize = 20, FontWeight = FontWeight.SemiBold };
    private readonly SelectableTextBlock details = new() { Foreground = Muted };
    private readonly StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel conflictPanel = new() { Spacing = 4, IsVisible = false };
    private readonly StackPanel notices = new() { Spacing = 4 };
    private readonly SelectableTextBlock moreDetails = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button detailsToggle = new() { Content = "Details", Padding = new Thickness(6, 1), FontSize = 12, IsVisible = false };
    private readonly Button conflictChip = new() { Padding = new Thickness(8, 1), FontSize = 12, Foreground = Warning, IsVisible = false };
    private readonly StackPanel mainTabs = new() { Orientation = Orientation.Horizontal, Spacing = 4, IsVisible = false };
    private readonly Grid body = new();
    private readonly ContentControl paneHost = new();                  // the preview stays in the window: removing its OpenGL view stops it
    private readonly ContentControl bodyLayer = new() { IsVisible = false };   // Files and Sounds, drawn over the hidden preview
    private static readonly int[] PreviewSpeeds = [60, 30];
    private const string AllHeroes = "All heroes";
    private const string AllKinds = "All kinds";

    /// <summary>Kind filter entry: only the mods the last "Check mods" marked (broken, repairable or worth a look).</summary>
    private const string NeedsAttention = "Flagged by Check mods";

    /// <summary>Kind filter entry: mods with a newer file on GameBanana.</summary>
    private const string WithUpdates = "New files on GameBanana";
    private IReadOnlyList<ModEntry> mods = [];
    private IReadOnlyDictionary<string, DoctorFinding> doctor = new Dictionary<string, DoctorFinding>();
    private readonly SelectableTextBlock updateBanner = new() { Foreground = Warning, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button repairAll = new() { IsVisible = false };
    private readonly Button updateAll = new() { IsVisible = false };
    private readonly SelectableTextBlock managerBanner = new() { Foreground = Warning, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly SelectableTextBlock crashBanner = new()
    {
        Text = "S2ModKit closed unexpectedly last time; the report is in the log. Settings > Copy log copies it for a bug report.",
        Foreground = Warning,
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false,
    };
    private readonly StackPanel reapplyBanner = new() { Spacing = 4, IsVisible = false };
    private readonly StackPanel appUpdate = new() { Spacing = 4, IsVisible = false };
    private List<ListRow> rows = [];
    private bool byHero;
    private readonly HashSet<string> openGroups = new(StringComparer.OrdinalIgnoreCase);   // hero view: groups the user unfolded
    private string? lastKey;
    private bool listing;                                                // the list's rows are being replaced
    private bool folding;                                                // a fold toggle: no reveal, no reload of the shown mod

    public MainWindow(AppOptions options)
    {
        Title = "S2ModKit";
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://S2ModKit/Assets/S2ModKit.ico")));
        Width = 1700;
        Height = 1000;
        settings = AppSettings.Load(options.Game);
        AppLog.Start(settings.Data, $"S2ModKit {AppUpdates.Text(AppUpdates.Current)} ({(AppSettings.Installed ? "installed" : "portable")}) on {Environment.OSVersion.VersionString}; "
            + $"game {settings.Game}; data {settings.Data}");
        AppLog.CaptureCrashes();
        crashBanner.IsVisible = AppLog.PreviousCrash;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Error("Unhandled error", e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Error("Unobserved background error", e.Exception);
        Dispatcher.UIThread.UnhandledException += (_, e) => AppLog.Error("Unhandled error in the window", e.Exception);
        listStatus.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty)
            {
                AppLog.Shown(listStatus.Text);                              // every message the window shows
            }
        };
        Closed += (_, _) =>
        {
            OneClickLink.StopListening();
            sounds?.Release();
            AppLog.Close();
        };
        catalog = new ModCatalog(settings);
        pane = new PreviewPane(settings) { OpenAutomatically = settings.PreviewAuto || options.SelfTest is not null || options.UiShot is not null };
        pane.BuildRequested += (mod, model, hidden, colors, shapes, effects, shines, added, seeThrough) => _ = BuildAsync(mod, model, hidden, colors, shapes, effects, shines, added, seeThrough);
        list.ItemTemplate = new FuncDataTemplate<ListRow>((row, _) => row switch
        {
            HeaderRow header => new TextBlock
            {
                Text = header.Foldable ? $"{(header.Open ? "\u25BE" : "\u25B8")} {header.Title}" : header.Title,
                FontWeight = FontWeight.Bold,
                FontSize = 14,
                Margin = new Thickness(2, header.Foldable ? 4 : 10, 2, 2),
            },
            ModRow { Mod: var mod } item => ModItem(mod, item.Tags, item.LoadOrder, doctor.GetValueOrDefault(mod.Key), updates.ContainsKey(mod.Key)),
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
        list.ContainerPrepared += (_, e) =>                                  // fixed headers are not selectable; foldable ones toggle (containers are recycled)
        {
            var header = e.Index >= 0 && e.Index < rows.Count && rows[e.Index] is HeaderRow { Foldable: false };
            e.Container.IsHitTestVisible = !header;
            e.Container.Focusable = !header;
        };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is HeaderRow { Foldable: true } folded)
            {
                if (listing)
                {
                    return;                                                 // the new rows put the selection on a header: not a click
                }

                if (!openGroups.Add(folded.Group))
                {
                    openGroups.Remove(folded.Group);
                }

                var viewer = list.Scroll as ScrollViewer;                   // folding keeps the list where it is scrolled to
                var offset = viewer?.Offset ?? default;
                folding = true;
                ApplySearch(lastKey);                                       // the shown mod stays shown (selected when visible)
                folding = false;
                if (viewer is not null)
                {
                    viewer.Offset = offset;
                    Dispatcher.UIThread.Post(() => viewer.Offset = offset, DispatcherPriority.Loaded);   // again once the new rows are laid out
                }

                return;
            }

            if (folding)
            {
                return;
            }

            lastKey = SelectedMod?.Key ?? (list.SelectedItem is BaseRow ? lastKey : null);
            ShowSelected();
        };
        search.TextChanged += (_, _) => ApplySearch();
        heroFilter.SelectionChanged += (_, _) => ApplySearch();
        sort.SelectionChanged += (_, _) => ApplySearch();
        flatView.Click += (_, _) => SetView(false);
        heroView.Click += (_, _) => SetView(true);
        kindFilter.SelectionChanged += (_, _) => ApplySearch();
        BrowseView.SteadyWidth(heroFilter);
        BrowseView.SteadyWidth(kindFilter);

        var side = new DockPanel { Width = 380, Background = Sidebar };
        var header = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
        header.Children.Add(ModeSwitch(browse: false));
        header.Children.Add(new WrapPanel                                 // wraps to a second line in the narrow side panel
        {
            ItemSpacing = 6,
            LineSpacing = 6,
            Children =
            {
                Button("Install mod...", InstallAsync), Button("Check mods", CheckModsAsync), ProfilesButton(), SettingsButton(),
            },
        });
        header.Children.Add(search);
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { flatView, heroView, sort } });
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { heroFilter, kindFilter } });
        header.Children.Add(appUpdate);
        header.Children.Add(updateBanner);
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { repairAll, updateAll } });
        repairAll.Click += async (_, _) => await RepairAllAsync(DoctorVerdict.Repairable);
        updateAll.Click += async (_, _) => await RepairAllAsync(DoctorVerdict.Updatable);
        header.Children.Add(managerBanner);
        header.Children.Add(crashBanner);
        header.Children.Add(reapplyBanner);
        header.Children.Add(listStatus);
        DockPanel.SetDock(header, Dock.Top);
        side.Children.Add(header);
        side.Children.Add(list);

        var info = new StackPanel { Spacing = 6, Margin = new Thickness(16, 12) };
        info.Children.Add(title);
        info.Children.Add(new WrapPanel { ItemSpacing = 8, LineSpacing = 4, Children = { details, detailsToggle, conflictChip } });
        info.Children.Add(moreDetails);
        info.Children.Add(notices);
        info.Children.Add(actions);
        info.Children.Add(conflictPanel);
        info.Children.Add(mainTabs);
        details.VerticalAlignment = VerticalAlignment.Center;
        detailsToggle.Click += (_, _) => moreDetails.IsVisible = !moreDetails.IsVisible;
        conflictChip.Click += (_, _) => conflictPanel.IsVisible = !conflictPanel.IsVisible;
        var main = new DockPanel { Background = Panel };
        DockPanel.SetDock(info, Dock.Top);
        main.Children.Add(info);
        bodyLayer.Background = Panel;
        body.Children.Add(paneHost);
        body.Children.Add(bodyLayer);
        paneHost.Content = pane;
        pane.IsVisible = false;
        main.Children.Add(body);

        var root = new DockPanel();
        DockPanel.SetDock(side, Dock.Left);
        root.Children.Add(side);
        root.Children.Add(main);
        myMods = root;
        Content = new Grid { Children = { myMods } };

        if (options.SelfTest is { } prefix)                                  // measurements on a vanilla model, then exit
        {
            var test = new SelfTest(prefix, pane.Control, () => Dispatcher.UIThread.Post(Close));
            pane.Control.Opened += test.Opened;
            test.Abilities = () => pane.AbilityClips;
            test.Overlay = pane.EffectOverlayFor;
            pane.Control.AfterFrame = test.Step;
            pane.Control.Outline = options.Outline ?? pane.Control.Outline;
            title.Text = "Self-test: " + options.Model;
            ShowBody(pane);
            var entry = options.Addon is null ? null : new ModEntry("selftest", Path.GetFileName(options.Addon), ModSource.Other, true, [options.Addon], [null],
                new Dictionary<string, int>(), [], [options.Model], null);
            pane.Show(entry, entry is null ? [] : [entry], options.Model);
            return;
        }

        // drag and drop in the load order: press on an enabled mod, release on another (Load order sorting)
        ModEntry? dragFrom = null;
        var dragStart = default(Point);
        var dragging = false;
        ModEntry? RowAt(Point at) => (list.InputHitTest(at) as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is ModRow { Mod: var row } ? row : null;
        list.AddHandler(PointerPressedEvent, (_, e) =>
        {
            dragging = false;
            dragFrom = sort.SelectedItem as string == "Load order" && e.GetCurrentPoint(list).Properties.IsLeftButtonPressed && RowAt(e.GetPosition(list)) is { Loaded: true } row ? row : null;
            dragStart = e.GetPosition(list);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (dragFrom is not null && !dragging && Point.Distance(e.GetPosition(list), dragStart) > 10)
            {
                dragging = true;
                listStatus.Text = $"Moving '{dragFrom.Name}': release it on the mod whose place it should take.";
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(PointerReleasedEvent, async (_, e) =>
        {
            var (from, moved) = (dragFrom, dragging);
            (dragFrom, dragging) = (null, false);
            if (from is null || !moved)
            {
                return;
            }

            if (RowAt(e.GetPosition(list)) is { Loaded: true } target && target.Key != from.Key)
            {
                await RunAsync(() => catalog.MoveToPlaceOfAsync(from, target, mods));
            }
            else
            {
                listStatus.Text = "Not moved: release on another enabled mod.";
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

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
            if (options.ByHero)
            {
                SetView(true);
            }

            await LoadAsync();
            if (options.CrashTest == "overflow")
            {
                Overflow(1);
            }

            if (options.UiShot is { } shot)
            {
                await UiShotAsync(shot, options);
                return;
            }

            OneClickLink.StartListening(link => Dispatcher.UIThread.Post(() => _ = InstallLinkAsync(link)));
            if (options.Link is { } started)
            {
                await InstallLinkAsync(started);
            }

            if (settings.CheckUpdates)
            {
                if (UpdateStore.LastCheck is not { } last || DateTimeOffset.UtcNow - last > TimeSpan.FromHours(20))
                {
                    _ = CheckModUpdatesAsync(quiet: true);                  // once a day: one GameBanana request per mod that came from it
                }

                await CheckForUpdateAsync(quiet: true);
            }
        };
    }

    /// <summary>Shows the requested mod, tool tab and part, waits for the preview and the shape check, saves the window as a PNG
    /// and closes (the 3D view itself may come out empty: it is drawn by OpenGL outside the window's own rendering).</summary>
    private async Task UiShotAsync(string path, AppOptions options)
    {
        var placeEffect = string.Equals(options.Tab, "addeffect", StringComparison.OrdinalIgnoreCase);
        var seeThroughShot = string.Equals(options.Tab, "seethrough", StringComparison.OrdinalIgnoreCase);   // --part N see-through, with the 3D view
        var effectsShot = placeEffect || seeThroughShot || string.Equals(options.Tab, "effects", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(options.Tab, "browse", StringComparison.OrdinalIgnoreCase))     // the GameBanana browser with its first mod open
        {
            await SetBrowseAsync(true);
            await Task.Delay(TimeSpan.FromSeconds(4));
            browse?.OpenFirstForShot();
            await Task.Delay(TimeSpan.FromSeconds(6));
        }

        if (string.Equals(options.Tab, "fold", StringComparison.OrdinalIgnoreCase) && list.Scroll is ScrollViewer scroller)   // does folding a hero keep the scroll position?
        {
            scroller.Offset = new Vector(0, 600);
            await Task.Delay(TimeSpan.FromSeconds(1));
            var before = scroller.Offset.Y;
            var header = rows.OfType<HeaderRow>().Where(row => row.Foldable).ElementAt(14);
            list.SelectedItem = header;
            await Task.Delay(TimeSpan.FromSeconds(1));
            var unfolded = scroller.Offset.Y;
            list.SelectedItem = rows.OfType<HeaderRow>().First(row => row.Group == header.Group);
            await Task.Delay(TimeSpan.FromSeconds(1));
            await File.WriteAllTextAsync(path + ".txt", $"scroll before {before}; after unfolding '{header.Title}' {unfolded}; after folding it again {scroller.Offset.Y}");
        }

        BaseRow? baseGame = null;
        if (options.Select is { } baseName && baseName.EndsWith(" (base game)", StringComparison.OrdinalIgnoreCase))   // --by-hero --select "Celeste (base game)"
        {
            heroFilter.SelectedItem = baseName[..^" (base game)".Length];
            await Task.Delay(TimeSpan.FromSeconds(1));
            baseGame = rows.OfType<BaseRow>().FirstOrDefault(row => string.Equals(row.Hero.Name + " (base game)", baseName, StringComparison.OrdinalIgnoreCase));
        }

        var wanted = options.Select is { } name ? mods.FirstOrDefault(mod => mod.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) : null;
        if (baseGame is not null || wanted is not null)
        {
            if (baseGame is not null)
            {
                list.SelectedItem = baseGame;
            }
            else
            {
                ApplySearch(wanted!.Key);
            }

            await Task.Delay(TimeSpan.FromSeconds(8));                      // preview session and shape capabilities
            var hintShot = string.Equals(options.Tab, "hint", StringComparison.OrdinalIgnoreCase);   // the colour panel with the Shine hint open
            pane.ShowForShot(seeThroughShot || hintShot ? "colour" : options.Tab, options.Part);
            if (hintShot)
            {
                pane.OpenHintForShot();
            }

            if (double.TryParse(Environment.GetEnvironmentVariable("S2MODKIT_SHOT_HUE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hueShift))
            {
                pane.HueForShot(hueShift);                                  // checks of a recolor in screenshot mode
            }

            if (Environment.GetEnvironmentVariable("S2MODKIT_SHOT_CLIP") is { Length: > 0 } clipShot)
            {
                pane.ClipForShot(clipShot);                                 // checks of animation mods in screenshot mode
                await Task.Delay(TimeSpan.FromSeconds(3));
                var clipSaved = false;
                pane.Control.AfterFrame = (session, view, width, height, _) =>
                {
                    if (!clipSaved)
                    {
                        clipSaved = true;
                        File.WriteAllBytes(path + "-3d.png", new S2ModKit.Infrastructure.PngPreviewImageEncoder().EncodePng(session.Capture(view, width, height)));
                    }
                };
                await Task.Delay(TimeSpan.FromSeconds(2));
                pane.Control.AfterFrame = null;
            }

            if (seeThroughShot)
            {
                pane.SeeThroughForShot(double.TryParse(Environment.GetEnvironmentVariable("S2MODKIT_SHOT_SEE_THROUGH"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var amount) ? amount : 0.6);
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
            if (effectsShot)                                                 // the 3D view itself as <path>-3d.png; "addeffect": after an effect is placed by a click on the chest
            {
                if (placeEffect)
                {
                    await pane.PlaceForShotAsync(options.Effect ?? "inferno_hand_ambient_flame", 0.5, 0.33);
                }

                await Task.Delay(TimeSpan.FromSeconds(4));
                var saved = false;
                pane.Control.AfterFrame = (session, view, width, height, _) =>
                {
                    if (!saved)
                    {
                        saved = true;
                        File.WriteAllBytes(path + "-3d.png", new S2ModKit.Infrastructure.PngPreviewImageEncoder().EncodePng(session.Capture(view, width, height)));
                    }
                };
                await Task.Delay(TimeSpan.FromSeconds(2));
                pane.Control.AfterFrame = null;
            }

            if (string.Equals(options.Tab, "cycle", StringComparison.OrdinalIgnoreCase))   // Files, then back to Preview: does it draw again?
            {
                var tabs = mainTabs.Children.OfType<ToggleButton>().ToArray();
                var before = pane.Control.FramesDrawn;
                tabs.Last().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                await Task.Delay(TimeSpan.FromSeconds(2));
                var hidden = pane.Control.FramesDrawn;
                tabs.First().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                await Task.Delay(TimeSpan.FromSeconds(3));
                var shown = pane.Control.FramesDrawn;
                await File.WriteAllTextAsync(path + ".txt", $"frames before Files {before}; while Files was shown {hidden - before}; in 3 s after Preview again {shown - hidden}; max fps {pane.Control.MaxFps}");
            }
        }

        var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
        bitmap.Render(this);
#pragma warning disable CS0618 // PNG with default options is what a layout check needs
        bitmap.Save(path);
#pragma warning restore CS0618
        Close();
    }

#pragma warning disable CA1857, S2190 // deliberate endless recursion: the crash capture check
    private static int Overflow(int depth) => Overflow(depth + 1) + 1;
#pragma warning restore CA1857, S2190

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
            ShowRepairAll();                                               // Repair all / Update all from the last check
            updates = UpdateStore.Known(mods, SourceRecords.Load(), GameBananaIdOf);
            var build = await current.GameBuildAsync();
            updateBanner.IsVisible = await current.GameUpdatedSinceCheckAsync();
            updateBanner.Text = $"Deadlock was updated (build {build}). Press 'Check mods' to see which mods the update affects.";
            ShowReapplyBanner(EditRecipe.Outdated(mods, build));
            var stale = ManagerCompatibility.StaleWarning(ManagerCompatibility.Read(settings.Game), mods);
            managerBanner.Text = stale;
            managerBanner.IsVisible = stale is not null;
            if (stale is not null)
            {
                AppLog.Shown(stale);
            }
            var hero = heroFilter.SelectedItem as string;
            heroFilter.ItemsSource = (string[])[AllHeroes, .. mods.SelectMany(mod => mod.HeroNames).Distinct().Order(StringComparer.OrdinalIgnoreCase)];
            heroFilter.SelectedItem = hero is not null && mods.Any(mod => mod.HeroNames.Contains(hero)) ? hero : AllHeroes;
            ApplySearch(select);
        }
#pragma warning disable CA1031 // shown in the window
        catch (Exception exception)
#pragma warning restore CA1031
        {
            AppLog.Error("Shown in the window", exception);
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
        var updatesOnly = kind == WithUpdates;
        var flaggedOnly = kind == NeedsAttention || updatesOnly;
        var matches = mods.Where(mod => mod.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            && (kind != NeedsAttention || doctor.GetValueOrDefault(mod.Key) is { Verdict: not (DoctorVerdict.Works or DoctorVerdict.Updatable) })
            && (!updatesOnly || updates.ContainsKey(mod.Key))).ToArray();
        kind = flaggedOnly ? AllKinds : kind;
        var shown = new List<ListRow>();
        if (byHero)
        {
            // groups are folded unless opened, searched or filtered to one hero; the group of the shown mod opens
            bool unfold(string group) => openGroups.Contains(group) || text.Length > 0 || hero != AllHeroes || flaggedOnly;
            // every hero, with or without mods, starts with its base-game row (so a hero without mods can be previewed too)
            var everyHero = text.Length == 0 && kind == AllKinds && !flaggedOnly ? Hero.Catalogue.Select(entry => entry.Name) : [];
            foreach (var name in matches.SelectMany(mod => mod.HeroNames).Concat(everyHero).Distinct().Order(StringComparer.OrdinalIgnoreCase).Where(name => hero is AllHeroes || name == hero))
            {
                var group = matches.Where(mod => mod.HeroNames.Contains(name))
                    .Select(mod => (mod, tags: mod.HeroKinds?.GetValueOrDefault(name) ?? [])).Where(item => kind == AllKinds
                    || (item.tags.Count > 0 ? item.tags.Any(tag => tag.Equals(kind, StringComparison.OrdinalIgnoreCase)) : item.mod.MainKind == kind)).ToArray();
                AddGroup(shown, name, group.Select(item => new ModRow(item.mod, item.tags)), Hero.FindByName(name) is { } found ? new BaseRow(found) : null, unfold, current);
            }

            foreach (var name in hero is AllHeroes ? ModAttribution.Groups : [])
            {
                AddGroup(shown, name, matches.Where(mod => mod.HeroNames.Count == 0 && mod.Group == name && (kind == AllKinds || mod.MainKind == kind)).Select(mod => new ModRow(mod)), null, unfold, current);
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
        listing = true;                                                     // replacing the rows can leave the selection on a header row at the
        try                                                                 // same place; taken as a click it folded the group and listed again,
        {                                                                   // without end (the stack overflow of 2026-11-09/10)
            list.ItemsSource = shown;
        }
        finally
        {
            listing = false;
        }

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

    private void AddGroup(List<ListRow> shown, string title, IEnumerable<ModRow> group, BaseRow? baseGame, Func<string, bool> unfold, string? current)
    {
        var items = group.OrderBy(row => row.Mod.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (items.Length > 0 || baseGame is not null)
        {
            if (!folding && current is not null && items.Any(row => row.Mod.Key == current))
            {
                openGroups.Add(title);                                      // the shown mod's group opens
            }

            var open = unfold(title);
            shown.Add(new HeaderRow($"{title} ({items.Length})", title, Foldable: true, Open: open));
            if (!open)
            {
                return;
            }

            if (baseGame is not null)
            {
                shown.Add(baseGame);
            }

            shown.AddRange(items);
        }
    }

    /// <summary>A list item: name, status line (flat view: heroes, status, kind), and in the hero view the tags of what the mod changes.</summary>
    private static StackPanel ModItem(ModEntry mod, IReadOnlyList<string>? tags, bool loadOrder = false, DoctorFinding? finding = null, bool update = false)
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
            DoctorVerdict.Updatable => "\u2191 can be updated",
            _ => null,
        };
        if (tags is { Count: > 0 } || badges.Count > 0 || health is not null || update)
        {
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 3, 0, 0) };
            chips.Children.AddRange((tags ?? []).Select(tag => Chip(tag, Muted)));
            chips.Children.AddRange(badges.Select(badge => Chip(badge is "wins" ? "wins" : "\u26A0 " + badge, badge is "wins" ? Muted : Warning)));
            if (health is not null)
            {
                chips.Children.Add(Chip(health, finding!.Verdict is DoctorVerdict.Look or DoctorVerdict.Updatable ? Muted : Warning));
            }

            if (update)
            {
                chips.Children.Add(Chip("\u2B06 new file", Warning));
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
        ShowBody(sounds);
        _ = sounds.ShowAsync(mod, settings);
    }

    /// <summary>The main-area tabs for the shown mod: Preview (a model), Sounds (sound events or files), Files (the archive's
    /// contents); tabs without content are hidden and the first available one opens.</summary>
    private void ShowTabs(ModEntry? mod, bool preview, bool hasSounds)
    {
        mainTabs.Children.Clear();
        var choices = new List<(string Name, Action Open)>();
        if (preview)
        {
            choices.Add(("Preview", () =>
            {
                sounds?.Release();
                sounds = null;
                ShowBody(pane);
            }
            ));
        }

        if (mod is not null && hasSounds)
        {
            var count = mod.Entries.Count(entry => entry.EndsWith(".vsnd_c", StringComparison.OrdinalIgnoreCase));
            choices.Add(($"Sounds ({count})", () => ShowSounds(mod)));
        }

        if (mod is not null)
        {
            choices.Add(($"Files ({mod.Entries.Count})", () =>
            {
                sounds?.Release();
                sounds = null;
                ShowBody(new ScrollViewer
                {
                    Margin = new Thickness(16, 8),
                    Content = new SelectableTextBlock
                    {
                        Text = $"{mod.KindText}\n\n" + string.Join('\n', mod.Entries.Take(2000)) + (mod.Entries.Count > 2000 ? $"\n... {mod.Entries.Count - 2000} more" : string.Empty),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12,
                    },
                });
            }
            ));
        }

        var buttons = new List<ToggleButton>();
        foreach (var (name, open) in choices)
        {
            var tab = new ToggleButton { Content = name, Padding = new Thickness(14, 5) };
            tab.Click += (_, _) =>
            {
                foreach (var other in buttons)
                {
                    other.IsChecked = other == tab;
                }

                open();
            };
            buttons.Add(tab);
            mainTabs.Children.Add(tab);
        }

        mainTabs.IsVisible = choices.Count > 1;
        if (buttons.Count > 0)
        {
            buttons[0].IsChecked = true;
        }
    }

    private void ShowSelected()
    {
        sounds?.Release();                                                 // stops playback and closes the game archives
        sounds = null;
        actions.Children.Clear();
        conflictPanel.Children.Clear();
        notices.Children.Clear();
        conflictPanel.IsVisible = false;
        moreDetails.IsVisible = false;
        if (list.SelectedItem is BaseRow { Hero: var baseHero })
        {
            title.Text = $"{baseHero.Name} (base game)";
            details.Text = "The game's own model, without mods";
            moreDetails.Text = $"Model: {baseHero.Model}";
            detailsToggle.IsVisible = true;
            conflictChip.IsVisible = false;
            ShowTabs(null, preview: true, hasSounds: false);
            ShowBody(pane);
            pane.Show(null, mods, baseHero.Model);
            return;
        }

        if (SelectedMod is not { } mod)
        {
            title.Text = string.Empty;
            details.Text = string.Empty;
            detailsToggle.IsVisible = false;
            conflictChip.IsVisible = false;
            mainTabs.Children.Clear();
            mainTabs.IsVisible = false;
            ShowBody(null);
            return;
        }

        title.Text = mod.Name;
        var summary = new List<string>();
        if (mod.HeroNames.Count > 0)
        {
            summary.Add(string.Join(", ", mod.HeroNames));
        }

        summary.Add(mod.MainKind == "model" ? "model" : mod.KindText);
        summary.Add(mod.Loaded ? $"enabled, pak{mod.FirstSlot:00}" : "disabled");
        summary.Add(mod.Source switch { ModSource.Manager => "from Deadlock Mod Manager", ModSource.App => "installed by S2ModKit", _ => "added by hand" });
        details.Text = string.Join(" · ", summary);
        moreDetails.Text = $"{mod.StatusText}. Files: {string.Join(", ", mod.Files.Select(Path.GetFileName))}. Contents: {mod.KindText}."
            + (mod.Models.Count > 0 ? $" Model: {string.Join(", ", mod.Models)}." : string.Empty)
            + (mod.StrayHeroes is { Count: > 0 } stray ? $" Also changes a few files of {string.Join(", ", stray)} (not listed under that hero)." : string.Empty)
            + (mod.Source == ModSource.Manager ? " Known to Deadlock Mod Manager: switching it here renames its files, so the manager's own list can show an old state." : string.Empty);
        detailsToggle.IsVisible = true;

        actions.Children.Add(mod.Loaded
            ? Button("Disable", () => RunAsync(() => catalog.DisableAsync(mod)))
            : Button("Enable", () => EnableAsync(mod)));
        actions.Children.Add(Button("Show in folder", () => ShowInExplorer(mod.Files[0], select: true)));
        if (mod.VersionList.Count > 1)
        {
            var versions = new ComboBox
            {
                ItemsSource = mod.VersionList,
                SelectedItem = mod.VersionList.FirstOrDefault(version => version.Key == mod.Key),
                MinWidth = 220,
                ItemTemplate = new FuncDataTemplate<ModVersion>((version, _) => new TextBlock { Text = version is null ? string.Empty : "Version: " + version.Label + (version.Active ? " (enabled)" : string.Empty) }),
            };
            versions.SelectionChanged += (_, _) =>
            {
                if (versions.SelectedItem is ModVersion picked && picked.Key != mod.Key && mods.FirstOrDefault(other => other.Key == picked.Key) is { } chosen)
                {
                    _ = RunAsync(() => catalog.ActivateVersionAsync(chosen, mods));   // picking a version switches to it
                }
            };
            actions.Children.Add(versions);
        }

        var more = new MenuFlyout();
        void Add(string header, Func<Task> action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += async (_, _) => await action();
            more.Items.Add(item);
        }

        if (updates.GetValueOrDefault(mod.Key) is { } available)
        {
            actions.Children.Add(Button($"Update... (new file {available.Files[0].Added.LocalDateTime:d})", () => UpdateModAsync(mod, available)));
        }

        if (doctor.GetValueOrDefault(mod.Key) is { Verdict: DoctorVerdict.Repairable or DoctorVerdict.Updatable } repairable)
        {
            actions.Children.Add(Button(repairable.Verdict == DoctorVerdict.Updatable ? "Update" : "Repair", () => RunAsync(() => catalog.RepairFindingAsync(mod, repairable))));
        }

        if (mod.Loaded && !mod.Key.StartsWith("app:", StringComparison.Ordinal))
        {
            var order = ModCatalog.LoadOrderOf(mods).Select(item => item.Key).ToList();
            order = [.. mods.Where(other => order.Contains(other.Key)).OrderBy(other => other.FirstSlot).Select(other => other.Key)];
            var at = order.IndexOf(mod.Key);
            Add("Move to slot...", () => MoveToSlotAsync(mod), at >= 0);
            Add("Load earlier (wins over one more mod)", () => RunAsync(() => catalog.MoveAsync(mod, -1, mods)), at > 0);
            Add("Load later", () => RunAsync(() => catalog.MoveAsync(mod, 1, mods)), at >= 0 && at < order.Count - 1);
        }

        if (GameBananaIdOf(mod) is { } gameBananaId)
        {
            Add("Open on GameBanana", () =>
            {
                AppUpdates.OpenPage(new Uri($"https://gamebanana.com/mods/{gameBananaId}"));
                return Task.CompletedTask;
            });
        }

        if (EditRecipe.Of(mod) is { HasEdits: true } recipe)
        {
            Add("Re-apply edits (build them again on the current game)", () => ReapplyAsync([mod]), mod.Loaded && recipe is not null);
        }

        Add("Merge with other mods...", () => MergeAsync(mod), mod.Files.Count > 0);
        if (MergeRecord.Of(mod) is not null)
        {
            Add("Split (turn the merged mods back on)", () => RunAsync(() => catalog.SplitAsync(mod)));
        }

        Add("Delete...", () => DeleteAsync(mod));
        actions.Children.Add(new Button { Content = "More ▾", Flyout = more });

        ShowConflicts(mod);
        var conflicts = mod.ConflictInfo;
        var shared = conflicts.With.Count;
        conflictChip.IsVisible = shared > 0 || conflicts.ModelWarnings.Count > 0;
        conflictChip.Content = shared > 0
            ? $"⚠ shares files with {shared} mod{(shared == 1 ? string.Empty : "s")}: {(conflicts.Loses > 0 ? $"loses {conflicts.Loses}" : "wins all")} ▾"
            : "⚠ another model mod for this hero ▾";
        if (doctor.GetValueOrDefault(mod.Key) is { Reasons.Count: > 0 } finding)
        {
            foreach (var reason in finding.Reasons)
            {
                notices.Children.Add(new SelectableTextBlock { Text = "⚠ " + reason, Foreground = Warning, TextWrapping = TextWrapping.Wrap });
            }
        }

        var hasSounds = mod.Entries.Any(entry => entry.EndsWith(".vsnd_c", StringComparison.OrdinalIgnoreCase) || entry.EndsWith(".vsndevts_c", StringComparison.OrdinalIgnoreCase));
        if (mod.Models.Count == 0 && EditRecipe.Of(mod) is { HasEdits: true, Model: { Length: > 0 } edited })
        {
            // an edit built here without the model itself (effect colours, an effect added to an ability): shown on the hero it was
            // made for, the game's model with this mod's files, so more can be added to it
            mod = mod with { Models = [edited.EndsWith("_c", StringComparison.Ordinal) ? edited[..^2] : edited] };
        }

        ShowTabs(mod, preview: mod.Models.Count > 0, hasSounds);
        if (mod.Models.Count > 0)
        {
            ShowBody(pane);
            pane.Show(mod, mods);
            return;
        }

        if (hasSounds)
        {
            ShowSounds(mod);
            return;
        }

        if (mainTabs.Children.OfType<ToggleButton>().FirstOrDefault() is { } files)
        {
            files.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        }
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
            if (mods.FirstOrDefault(other => other.Key == with.OtherKey) is { } otherMod)
            {
                var pair = with;
                var fine = Button("Fine, stop warning", async () =>
                {
                    catalog.Dismissed.Dismiss(mod, otherMod, pair.Paths.Count);
                    await LoadAsync(mod.Key);
                    listStatus.Text = $"The conflict between '{mod.Name}' and '{otherMod.Name}' is hidden until they share more files. Settings > Show hidden conflicts brings it back.";
                });
                ToolTip.SetTip(fine, "Hides this conflict in the list and here. It comes back if the two mods start sharing more files.");
                content.Children.Add(fine);
            }

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
    private async Task<IReadOnlyList<AddonFileInstallReceipt>> InstallPathAsync(string path, string? label = null)
    {
        var name = label ?? Path.GetFileNameWithoutExtension(path);
        var receipts = new List<AddonFileInstallReceipt>();
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".vpk":
                await RunAsync(async () =>
                {
                    var result = await catalog.InstallAsync(path, name);
                    if (result.Receipt is { } installed)
                    {
                        receipts.Add(installed);
                    }

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
                    AppLog.Error("Shown in the window", exception);
                    listStatus.Text = exception.Message;
                    return receipts;
                }

                var chosen = variants.Count == 1 ? variants : await new VariantChooser(name, variants).ShowDialog<IReadOnlyList<ArchiveVariant>?>(this);
                if (chosen is { Count: > 0 })
                {
                    await RunAsync(async () =>
                    {
                        var change = await catalog.InstallArchiveAsync(path, chosen, label);
                        receipts.AddRange(change.Receipts);
                        return change;
                    });
                }

                break;
            default:
                listStatus.Text = $"'{Path.GetFileName(path)}' is not a .vpk, .zip, .7z or .rar file.";
                break;
        }

        return receipts;
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

    private async Task MergeAsync(ModEntry mod)
    {
        if (await new MergeDialog(mod, mods).ShowDialog<(IReadOnlyList<ModEntry> Mods, string Name)?>(this) is { } choice)
        {
            listStatus.Text = $"Merging {choice.Mods.Count} mods...";
            await RunAsync(() => catalog.MergeAsync(choice.Mods, choice.Name));
        }
    }

    private async Task DeleteAsync(ModEntry mod)
    {
        var shown = mod.Files.Select(file => Path.GetFileName(file)).Take(6);
        if (await new ConfirmDialog($"Delete '{mod.Name}'?", $"Its files go to the Recycle Bin, where you can restore them: {string.Join(", ", shown)}{(mod.Files.Count > 6 ? ", ..." : string.Empty)}.", "Delete").ShowDialog<bool>(this))
        {
            await RunAsync(() => catalog.DeleteAsync(mod));
        }
    }

    /// <summary>Builds one edited version with the hidden, recolored and reshaped parts (masks, boxes and movements taken from the open preview).</summary>
    private async Task BuildAsync(ModEntry? mod, string model, IReadOnlySet<int> hidden, IReadOnlyDictionary<int, ColorAdjustment> colors, IReadOnlyDictionary<int, PartShape> shapes,
        IReadOnlyDictionary<string, EffectColorChange> effects, IReadOnlyDictionary<int, float> shines, IReadOnlyList<S2ModKit.Adapters.Source2.AddedEffect> added, IReadOnlyDictionary<int, float> seeThrough)
    {
        var session = pane.Control.Session;
        var shiny = shines.Select(pair => session?.PartNormalTexture(pair.Key) is { } texture ? new PartShine(pair.Key, texture, pair.Value) : null).OfType<PartShine>().ToArray();
        var recolors = new List<PartRecolor>();
        foreach (var (part, change) in colors)
        {
            if (session?.PartColorTexture(part) is { } texture && session.PartColorMask(part) is { } mask)
            {
                var range = session.PartColorRange(part);
                recolors.Add(new PartRecolor(part, texture, mask.Mask, mask.Width, mask.Height, change, session.PartMaterial(part), range?.Target, range?.Tolerance ?? 0));
            }
        }

        var shapeEdits = new List<PartShapeEdit>();
        foreach (var (part, shape) in shapes)
        {
            if (session?.PartBounds(part) is { } bounds)
            {
                shapeEdits.Add(new PartShapeEdit(part, session.Parts[part].Material, shape, bounds.Min, bounds.Max, session.PartShapeDisplacement(part, shape), pane.ShapeLevelOf(part)));
            }
        }

        var slow = shapeEdits.Count > 0 ? "Building: the reshaped parts go through the checked model operations; this can take a minute..."
            : seeThrough.Count > 0 || shiny.Length > 0 ? "Building: see-through and shine write large textures again; this can take a minute..." : null;
        listStatus.Text = slow ?? "Building...";
        pane.ShowBuilding(slow ?? "Building the edited mod...");
        try
        {
            await RunAsync(() => catalog.BuildEditsAsync(mod, model, hidden, recolors, shapeEdits, effects, shiny, added,
                [.. seeThrough.Where(_ => session is not null).Select(pair => new PartOpacity(pair.Key, session!.PartMaterialPath(pair.Key), 1 - pair.Value))]));
        }
        finally
        {
            pane.ShowBuilding(null);
        }
    }

    /// <summary>Asks for a pak number (1-99, lower wins) and puts the mod there; when another mod has that slot, offers to swap them.</summary>
    private async Task MoveToSlotAsync(ModEntry mod)
    {
        var current = mod.FirstSlot;
        var answer = await new TextPromptDialog($"Move '{mod.Name}'", $"It is pak{current:00} now. Type a slot from 1 to 99: a lower number loads earlier and wins shared files.",
            "Slot number", "Move").ShowDialog<string?>(this);
        if (answer is null)
        {
            return;
        }

        if (!int.TryParse(answer.Trim().Replace("pak", string.Empty, StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var slot) || slot is < 1 or > 99)
        {
            listStatus.Text = $"'{answer}' is not a slot number from 1 to 99.";
            return;
        }

        var holder = mods.FirstOrDefault(other => other.Key != mod.Key && other.Loaded && other.Slots.Contains(slot));
        var swap = false;
        if (holder is not null)
        {
            if (!await new ConfirmDialog($"pak{slot:00} is taken", $"'{holder.Name}' is pak{slot:00}. Swap them? '{mod.Name}' becomes pak{slot:00} and '{holder.Name}' pak{current:00}.", "Swap").ShowDialog<bool>(this))
            {
                return;
            }

            swap = true;
        }

        await RunAsync(() => catalog.MoveToSlotAsync(mod, slot, swap, mods));
    }

    /// <summary>The banner after a game update: mods built here for an earlier game build, with one button for all of them.</summary>
    private void ShowReapplyBanner(IReadOnlyList<(ModEntry Mod, EditRecipe Recipe)> outdated)
    {
        reapplyBanner.Children.Clear();
        reapplyBanner.IsVisible = outdated.Count > 0;
        if (outdated.Count == 0)
        {
            return;
        }

        var text = $"{outdated.Count} edited mod{(outdated.Count == 1 ? " was" : "s were")} made for the previous game version: {string.Join(", ", outdated.Select(item => item.Mod.Name).Take(5))}"
            + (outdated.Count > 5 ? $" and {outdated.Count - 5} more" : string.Empty) + ".";
        reapplyBanner.Children.Add(new TextBlock { Text = text, Foreground = Warning, TextWrapping = TextWrapping.Wrap });
        reapplyBanner.Children.Add(Button("Re-apply edits", () => ReapplyAsync([.. outdated.Select(item => item.Mod)])));
        AppLog.Shown(text);
    }

    /// <summary>Builds each mod's saved edits again on the current game files and installs them as new versions (one after another).</summary>
    private async Task ReapplyAsync(IReadOnlyList<ModEntry> edited)
    {
        var messages = new List<string>();
        foreach (var (mod, index) in edited.Select((mod, index) => (mod, index)))
        {
            listStatus.Text = $"Re-applying the edits of '{mod.Name}' ({index + 1} of {edited.Count})...";
            await RunAsync(() => catalog.ReapplyAsync(mods.FirstOrDefault(current => current.Key == mod.Key) ?? mod, mods));
            messages.Add(listStatus.Text ?? string.Empty);
        }

        listStatus.Text = string.Join(" ", messages);
    }

    /// <summary>Runs the mod doctor over every mod (read-only) and shows what the last game update affected.</summary>
    /// <summary>The findings of <paramref name="verdict"/> (repairable or updatable) for enabled mods that are still in the list.</summary>
    private DoctorFinding[] Findings(DoctorVerdict verdict) => [.. doctor.Values.Where(finding => finding.Verdict == verdict && mods.Any(mod => mod.Key == finding.Key && mod.Loaded))];

    private void ShowRepairAll()
    {
        var repairable = Findings(DoctorVerdict.Repairable).Length;
        repairAll.IsVisible = repairable > 1;
        repairAll.Content = $"Repair all ({repairable})";
        ToolTip.SetTip(repairAll, "Repairs every mod the last check found repairable, one after another. Each original is kept disabled as the old version.");
        var updatable = Findings(DoctorVerdict.Updatable).Length;
        updateAll.IsVisible = updatable > 0;
        updateAll.Content = $"Update all ({updatable})";
        ToolTip.SetTip(updateAll, "Gives every mod whose hero model is older than the game's the settings later updates added (animation sounds, cameras, health bar position). They work without them; each original is kept disabled as the old version.");
    }

    /// <summary>Repairs or updates every such mod, one at a time (each a journaled operation); stops at the first failure and says which.</summary>
    private async Task RepairAllAsync(DoctorVerdict verdict)
    {
        var done = new List<string>();
        var all = Findings(verdict);
        var doing = verdict == DoctorVerdict.Updatable ? "Updating" : "Repairing";
        foreach (var finding in all)
        {
            if (mods.FirstOrDefault(mod => mod.Key == finding.Key) is not { } mod)
            {
                continue;
            }

            listStatus.Text = $"{doing} {done.Count + 1}/{all.Length}: {mod.Name}...";
            if (!await RunAsync(() => catalog.RepairFindingAsync(mod, finding)))
            {
                break;
            }

            done.Add(mod.Name);
            doctor = doctor.Where(pair => pair.Key != finding.Key).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }

        ShowRepairAll();
        listStatus.Text = done.Count == 0 ? listStatus.Text
            : $"{(verdict == DoctorVerdict.Updatable ? "Updated" : "Repaired")} {done.Count} mod{(done.Count == 1 ? string.Empty : "s")}: {string.Join(", ", done)}. " + listStatus.Text;
    }

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
            var flagged = report.Findings.Where(finding => finding.Verdict is not (DoctorVerdict.Works or DoctorVerdict.Updatable))
                .Select(finding => mods.FirstOrDefault(mod => mod.Key == finding.Key)?.Name).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToArray();
            listStatus.Text = report.ComparedWith is null
                ? $"Recorded what your {report.Findings.Count} mods depend on in game build {report.Build}. After the next game update, 'Check mods' shows what changed for each mod."
                    + (Count(DoctorVerdict.Updatable) > 0 ? $" {Count(DoctorVerdict.Updatable)} can be updated (models older than the game's; they work as they are): 'Update all' above the list." : string.Empty)
                : $"Compared with game build {report.ComparedWith}: {Count(DoctorVerdict.Broken)} broken, {Count(DoctorVerdict.Repairable)} repairable, {Count(DoctorVerdict.Look)} worth a look, {Count(DoctorVerdict.Works) + Count(DoctorVerdict.Updatable)} unaffected{(Count(DoctorVerdict.Updatable) > 0 ? $" ({Count(DoctorVerdict.Updatable)} of them can be updated)" : string.Empty)}."
                    + (flagged.Length > 0 ? $" Showing only those: {string.Join(", ", flagged)}. Click one to see why; pick 'All kinds' to see every mod." : string.Empty);
            if (flagged.Length > 0)
            {
                kindFilter.SelectedItem = NeedsAttention;                     // the flagged mods are what the user wants to see now
            }

            ShowRepairAll();
            ApplySearch();
        }
#pragma warning disable CA1031 // shown in the window
        catch (Exception exception)
#pragma warning restore CA1031
        {
            AppLog.Error("Shown in the window", exception);
            listStatus.Text = "Could not check the mods: " + (exception is S2ModKitException { Error: var error } ? $"{error.Summary} {error.Remediation}" : exception.Message);
        }
    }

    /// <summary>Runs an install, enable, disable, delete or version switch (refused with a message while the game runs), reloads the
    /// list and selects the mod under the key the operation reports.</summary>
    private async Task<bool> RunAsync(Func<Task<CatalogChange>> action)
    {
        try
        {
            await pane.ReleaseAsync();                                     // the preview keeps the mod's archive open
            sounds?.Release();                                             // and so does the Sounds tab
            var change = await Task.Run(action);
            await LoadAsync(change.SelectKey.Length > 0 ? change.SelectKey : null);
            listStatus.Text = change.Message + ConflictNote(change.SelectKey);
            return true;
        }
#pragma warning disable CA1031 // shown in the window (a refused install names the conflicting mods)
        catch (Exception exception)
#pragma warning restore CA1031
        {
            AppLog.Error("Shown in the window", exception);
            listStatus.Text = exception is S2ModKitException { Error: var error } ? $"{error.Summary} {error.Remediation}" : exception.Message;
            await LoadAsync();                                             // the preview was released: show the current state again
            listStatus.Text = exception is S2ModKitException { Error: var shown } ? $"{shown.Summary} {shown.Remediation}" : exception.Message;
            return false;
        }
    }

    /// <summary>"Profiles": switch to a saved set of enabled mods, save the enabled mods as one, delete one. The menu is built when it opens.</summary>
    private Button ProfilesButton()
    {
        var button = new Button { Content = "Profiles \u25BE" };
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            var profiles = catalog.Profiles.Load();
            foreach (var profile in profiles)
            {
                var plan = ModProfiles.Plan(profile, mods);
                var item = new MenuItem
                {
                    Header = $"{profile.Name} ({profile.Mods.Count} mod{(profile.Mods.Count == 1 ? string.Empty : "s")})",
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = plan.IsEmpty && plan.Missing.Count == 0,
                };
                item.Click += async (_, _) => await SwitchProfileAsync(profile);
                flyout.Items.Add(item);
            }

            if (profiles.Count == 0)
            {
                flyout.Items.Add(new MenuItem { Header = "No profiles yet", IsEnabled = false });
            }

            flyout.Items.Add(new Separator());
            var save = new MenuItem { Header = "Save enabled mods as a profile..." };
            save.Click += async (_, _) => await SaveProfileAsync();
            flyout.Items.Add(save);
            if (profiles.Count > 0)
            {
                var delete = new MenuItem { Header = "Delete a profile" };
                foreach (var profile in profiles)
                {
                    var item = new MenuItem { Header = profile.Name };
                    item.Click += async (_, _) =>
                    {
                        if (await new ConfirmDialog($"Delete profile '{profile.Name}'", "Only the profile is deleted; no mod changes.", "Delete").ShowDialog<bool>(this))
                        {
                            catalog.Profiles.Delete(profile.Name);
                            listStatus.Text = $"Deleted profile '{profile.Name}'.";
                        }
                    };
                    delete.Items.Add(item);
                }

                flyout.Items.Add(delete);
            }
        };
        flyout.Items.Add(new MenuItem { Header = "..." });                 // a flyout with no items does not open
        button.Flyout = flyout;
        return button;
    }

    private async Task SaveProfileAsync()
    {
        var enabled = mods.Count(mod => mod.Loaded);
        var name = await new TextPromptDialog("Save a profile", $"The {enabled} enabled mod{(enabled == 1 ? string.Empty : "s")} and their load order are saved under this name. A profile with the same name is replaced.",
            "Profile name", "Save").ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var profile = catalog.Profiles.Save(name, mods, DateTimeOffset.UtcNow);
            listStatus.Text = $"Saved profile '{profile.Name}' ({profile.Mods.Count} mods). Switch to it from Profiles.";
        }
        catch (IOException exception)
        {
            listStatus.Text = "Could not save the profile: " + exception.Message;
        }
    }

    private async Task SwitchProfileAsync(ModProfile profile)
    {
        var plan = ModProfiles.Plan(profile, mods);
        if (plan.IsEmpty)
        {
            listStatus.Text = $"The mods already match '{profile.Name}'." + (plan.Missing.Count > 0 ? $" Not in the addons folder any more: {string.Join(", ", plan.Missing.Select(mod => $"'{mod.Name}'"))}." : string.Empty);
            return;
        }

        static string Names(IEnumerable<string> names, int count) => string.Join(", ", names.Take(12)) + (count > 12 ? $" and {count - 12} more" : string.Empty);
        var message = (plan.Disable.Count > 0 ? $"Disable {plan.Disable.Count}: {Names(plan.Disable.Select(mod => mod.Name), plan.Disable.Count)}.\n\n" : string.Empty)
            + (plan.Enable.Count > 0 ? $"Enable {plan.Enable.Count}: {Names(plan.Enable.Select(mod => mod.Name), plan.Enable.Count)}.\n\n" : string.Empty)
            + (plan.Missing.Count > 0 ? $"Not in the addons folder any more (skipped): {Names(plan.Missing.Select(mod => mod.Name), plan.Missing.Count)}.\n\n" : string.Empty)
            + "Mods are renamed in place, as with Enable and Disable; nothing is deleted.";
        if (await new ConfirmDialog($"Switch to '{profile.Name}'", message, "Switch").ShowDialog<bool>(this))
        {
            await RunAsync(() => catalog.ApplyProfileAsync(profile, mods));
        }
    }

    /// <summary>"Settings": the rarely used choices and actions in their own window (<see cref="SettingsWindow"/>).</summary>
    private Button SettingsButton()
    {
        var button = new Button { Content = "Settings" };
        button.Click += async (_, _) => await new SettingsWindow(new SettingsActions(
            () => settings,
            changed =>
            {
                settings = changed;
                settings.Save();
                pane.OpenAutomatically = settings.PreviewAuto;
                pane.Control.MaxFps = settings.PreviewFps;
            },
            () => Report(PickGameAsync),
            () => Report(PickDataAsync),
            () => Report(() => ShowInExplorer(settings.AddonsRoot, select: false)),
            () => Report(() => LoadAsync(SelectedMod?.Key)),
            () => Report(() => CheckModUpdatesAsync(quiet: false)),
            () => Report(async () =>
            {
                catalog.Dismissed.Clear();
                await LoadAsync(SelectedMod?.Key);
                listStatus.Text = "Every conflict shows again.";
            }),
            () => Report(() => CheckForUpdateAsync(quiet: false)),
            () => Report(async () =>
            {
                if (AppLog.CurrentPath is { } log)
                {
                    await ShowInExplorer(log, select: true);
                }
            }),
            () => Report(async () =>
            {
                if (Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(AppLog.Read());
                    listStatus.Text = "The log is copied: paste it into your report. It holds the app's messages and errors, your folders and mod names.";
                }
            }),
            PreviewSpeeds)).ShowDialog(this);
        return button;

        // runs a main-window action and hands its new status line (if it set one) to the Settings window
        async Task<string?> Report(Func<Task> run)
        {
            var before = listStatus.Text;
            await run();
            return listStatus.Text != before ? listStatus.Text : null;
        }
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
        var (update, failure) = await AppUpdates.CheckWithReasonAsync();
        appUpdate.Children.Clear();
        appUpdate.IsVisible = update is not null;
        if (update is null)
        {
            if (!quiet)
            {
                listStatus.Text = failure is null
                    ? $"S2ModKit {AppUpdates.Text(AppUpdates.Current)} is the latest version."
                    : $"Could not check for updates: GitHub could not be reached ({failure}).";
            }

            return;
        }

        appUpdate.Children.Add(new TextBlock { Text = $"S2ModKit {AppUpdates.Text(update.Version)} is available (you have {AppUpdates.Text(AppUpdates.Current)}).", Foreground = Warning, TextWrapping = TextWrapping.Wrap });
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
                    AppLog.Error("Shown in the window", exception);
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

    private GameBananaRecords SourceRecords => new(Path.Combine(settings.Data, "gamebanana.json"));

    private GameBananaUpdates UpdateStore => new(Path.Combine(settings.Data, "gamebanana-updates.json"));

    /// <summary>Asks GameBanana for newer files of every installed mod that came from it and shows the badges.</summary>
    private async Task CheckModUpdatesAsync(bool quiet)
    {
        gameBanana ??= new GameBananaClient(Path.Combine(settings.Data, "gamebanana-cache"));
        try
        {
            var progress = new Progress<string>(text =>
            {
                if (!quiet)
                {
                    listStatus.Text = text;
                }
            });
            updates = await UpdateStore.CheckAsync(gameBanana, mods, SourceRecords.Load(), GameBananaIdOf, progress);
            if (!quiet || updates.Count > 0)
            {
                listStatus.Text = updates.Count == 0
                    ? "Every mod from GameBanana is up to date."
                    : $"{updates.Count} mod{(updates.Count == 1 ? " has" : "s have")} a newer file on GameBanana: {string.Join(", ", updates.Keys.Select(key => mods.FirstOrDefault(mod => mod.Key == key)?.Name).OfType<string>().Take(6))}. Pick 'New files on GameBanana' in the kind filter to see them.";
            }

            ApplySearch(SelectedMod?.Key);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException)
        {
            if (!quiet)
            {
                listStatus.Text = $"Could not check GameBanana for updates ({AppUpdates.Describe(exception)}).";
                AppLog.Error("GameBanana updates", exception);
            }
        }
    }

    /// <summary>Downloads the newest (or the chosen) newer file of <paramref name="mod"/>, installs it as a new version of the same mod
    /// and records its GameBanana source.</summary>
    private async Task UpdateModAsync(ModEntry mod, ModUpdate update)
    {
        var file = update.Files[0];
        var others = update.Files.Count > 1 ? $"\n\nOther new files: {string.Join(", ", update.Files.Skip(1).Select(other => other.Name))} (install them from Browse GameBanana)." : string.Empty;
        var confirmed = await new ConfirmDialog($"Update '{mod.Name}'",
            $"New file on GameBanana: {file.Name}, uploaded {file.Added.LocalDateTime:d}{(file.Description.Length > 0 ? $": \"{file.Description}\"" : string.Empty)}."
            + "\n\nA new file can be an update or an optional extra; check the description. It installs as a new version of this mod; the current files stay as a disabled version."
            + others, "Download and install").ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        try
        {
            var progress = new Progress<double>(done => listStatus.Text = $"Downloading {file.Name}: {done:P0}");
            var path = await GameBananaClient.DownloadAsync(file, Path.Combine(settings.Data, "downloads"), progress);
            ArchiveVariant? variant = null;
            if (!Path.GetExtension(path).Equals(".vpk", StringComparison.OrdinalIgnoreCase))
            {
                var variants = await Task.Run(() => catalog.ReadArchiveAsync(path));
                var chosen = variants.Count == 1 ? variants : await new VariantChooser(file.Name, variants).ShowDialog<IReadOnlyList<ArchiveVariant>?>(this);
                if (chosen is not { Count: > 0 })
                {
                    return;
                }

                variant = chosen[0];
            }

            var receipts = new List<AddonFileInstallReceipt>();
            await RunAsync(async () =>
            {
                var change = await catalog.UpdateAsync(mod, path, variant, $"update {file.Added.LocalDateTime:d}");
                receipts.AddRange(change.Receipts);
                return change;
            });
            foreach (var receipt in receipts)
            {
                SourceRecords.Add(new GameBananaSource(receipt.Id, update.GameBananaId, file.Id, file.Name, file.Md5, file.Added, DateTimeOffset.UtcNow, update.Name));
            }

            updates = UpdateStore.Known(mods, SourceRecords.Load(), GameBananaIdOf);
            ApplySearch(SelectedMod?.Key);
            TryDelete(path);
        }
#pragma warning disable CA1031 // shown in the window
        catch (Exception exception)
#pragma warning restore CA1031
        {
            AppLog.Error("Shown in the window", exception);
            listStatus.Text = "Could not update: " + (exception is S2ModKitException { Error: var error } ? $"{error.Summary} {error.Remediation}" : exception.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The GameBanana mod a mod came from: the app's own record of a browser install, else Deadlock Mod Manager's id (its
    /// mod ids are GameBanana mod ids; locally added mods have none).</summary>
    private long? GameBananaIdOf(ModEntry mod)
    {
        if (mod.Install?.Receipt.Id is { } receipt
            && new GameBananaRecords(Path.Combine(settings.Data, "gamebanana.json")).Load().FirstOrDefault(source => source.ReceiptId == receipt) is { } source)
        {
            return source.ModId;
        }

        return long.TryParse(mod.ModId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    /// <summary>"My mods | Browse" at the top of the left panel; both views carry one.</summary>
    private StackPanel ModeSwitch(bool browse)
    {
        var mine = new ToggleButton { Content = "My mods", IsChecked = !browse, FontSize = 16, FontWeight = FontWeight.SemiBold };
        var online = new ToggleButton { Content = "Browse GameBanana", IsChecked = browse, FontSize = 16, FontWeight = FontWeight.SemiBold };
        void Pick(bool toBrowse)
        {
            (mine.IsChecked, online.IsChecked) = (!browse, browse);       // each view has its own switch: its state never changes
            if (toBrowse != browse)
            {
                _ = SetBrowseAsync(toBrowse);
            }
        }

        mine.Click += (_, _) => Pick(false);
        online.Click += (_, _) => Pick(true);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { mine, online } };
    }

    /// <summary>A GameBanana 1-click link: the window comes forward, Browse GameBanana shows the mod and installs the file.</summary>
    private async Task InstallLinkAsync(OneClickLink link)
    {
        AppLog.Info($"1-click link: mod {link.ModId}, file {link.FileId}");
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        await SetBrowseAsync(true);
        if (browse is not null)
        {
            await browse.InstallLinkAsync(link);
        }
    }

    /// <summary>Switches between the installed mods and the GameBanana browser (created and loaded the first time it is shown).</summary>
    private async Task SetBrowseAsync(bool on)
    {
        if (Content is not Grid host || myMods is null)
        {
            return;
        }

        if (on && browse is null)
        {
            gameBanana = new GameBananaClient(Path.Combine(settings.Data, "gamebanana-cache"));
            browse = new BrowseView(gameBanana, ModeSwitch(browse: true));
            browse.InstallRequested += InstallFromGameBananaAsync;
            browse.IsInstalled = InstalledFromGameBanana;
            host.Children.Add(browse);
        }

        myMods.IsVisible = !on;
        if (browse is not null)
        {
            browse.IsVisible = on;
        }

        if (on)
        {
            await browse!.OpenAsync();                                      // the hidden preview stops drawing by itself
        }
        else
        {
            await LoadAsync(SelectedMod?.Key);                              // installs made while browsing show up
        }
    }

    /// <summary>Downloads a GameBanana file (checked against its size and checksum), installs it like a dropped file under the mod's
    /// name, and records where it came from for update checks.</summary>
    private async Task<bool> InstallFromGameBananaAsync(GameBananaPage page, GameBananaFile file)
    {
        if (browse is null || gameBanana is null)
        {
            return false;
        }

        try
        {
            var progress = new Progress<double>(done => browse.Status = $"Downloading {file.Name}: {done:P0}");
            var path = await GameBananaClient.DownloadAsync(file, Path.Combine(settings.Data, "downloads"), progress);
            browse.Status = $"Installing {page.Mod.Name}...";
            var receipts = await InstallPathAsync(path, page.Mod.Name);
            var records = SourceRecords;
            foreach (var receipt in receipts)
            {
                records.Add(new GameBananaSource(receipt.Id, page.Mod.Id, file.Id, file.Name, file.Md5, file.Added, DateTimeOffset.UtcNow, page.Mod.Name));
            }

            browse.Status = receipts.Count > 0
                ? $"{listStatus.Text} It is in My mods."                   // the install's own message: "Installed '...'." and conflicts
                : listStatus.Text ?? "Nothing was installed.";
            try
            {
                File.Delete(path);                                          // the installed copy is in the addons folder and the app's records
            }
            catch (IOException)
            {
            }

            return receipts.Count > 0;
        }
#pragma warning disable CA1031 // shown in the window
        catch (Exception exception)
#pragma warning restore CA1031
        {
            AppLog.Error("Shown in the window", exception);
            browse.Status = "Could not install: " + (exception is S2ModKitException { Error: var error } ? $"{error.Summary} {error.Remediation}" : exception.Message);
            return false;
        }
    }

    /// <summary>Whether a GameBanana file is installed now: a recorded install whose receipt still belongs to a mod in the list.</summary>
    private bool InstalledFromGameBanana(long fileId)
    {
        var receipts = mods.Select(mod => mod.Install?.Receipt.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return SourceRecords.Load().Any(source => source.FileId == fileId && receipts.Contains(source.ReceiptId));
    }

    /// <summary>Shows <paramref name="content"/> in the main area: the preview itself is only hidden, never removed from the window
    /// (an OpenGL view taken out of the window does not render again); anything else is drawn on the layer above it.</summary>
    private void ShowBody(Control? content)
    {
        if (ReferenceEquals(content, pane))
        {
            bodyLayer.Content = null;
            bodyLayer.IsVisible = false;
            pane.IsVisible = true;
            pane.Control.Resume();                                          // frames stopped while it was hidden
            return;
        }

        pane.IsVisible = false;
        bodyLayer.Content = content;
        bodyLayer.IsVisible = content is not null;
    }

    /// <summary>Recreates what depends on the settings (game and data folders) and lists the mods again.</summary>
    private async Task ApplySettingsAsync()
    {
        catalog = new ModCatalog(settings);
        pane = new PreviewPane(settings) { OpenAutomatically = settings.PreviewAuto };
        pane.BuildRequested += (mod, model, hidden, colors, shapes, effects, shines, added, seeThrough) => _ = BuildAsync(mod, model, hidden, colors, shapes, effects, shines, added, seeThrough);
        paneHost.Content = pane;
        ShowBody(null);
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

internal sealed record HeaderRow(string Title, string Group = "", bool Foldable = false, bool Open = true) : ListRow;

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

/// <summary>Asks for one line of text; the result of <c>ShowDialog</c> is the text, or null when cancelled.</summary>
internal sealed class TextPromptDialog : Window
{
    public TextPromptDialog(string title, string message, string placeholder, string confirm)
    {
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var text = new TextBox { PlaceholderText = placeholder };
        var yes = new Button { Content = confirm, IsDefault = true };
        var no = new Button { Content = "Cancel", IsCancel = true };
        yes.Click += (_, _) => Close(string.IsNullOrWhiteSpace(text.Text) ? null : text.Text.Trim());
        no.Click += (_, _) => Close(null);
        Opened += (_, _) => text.Focus();
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                text,
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
