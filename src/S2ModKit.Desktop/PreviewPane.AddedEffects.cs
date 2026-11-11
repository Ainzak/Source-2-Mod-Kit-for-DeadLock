using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using S2ModKit.Adapters.Preview;
using S2ModKit.Adapters.Source2;

namespace S2ModKit.Desktop;

/// <summary>
/// The Effects tab's "Added effects" part: any effect of the game (searchable, grouped by hero and ability) can be played alone,
/// then placed on the model by a click (it sits on the bone under the click and follows it) or at the hero's feet ("follows the
/// hero"). Placed effects play with the model; the selected one has a marker and three sliders that move it along its bone's
/// axes. Build edited mod writes them as always-on effects (<see cref="CompiledAddedEffects"/>).
/// </summary>
internal sealed partial class PreviewPane
{
    private const int ShownChoices = 400;

    private sealed class AddedRow(string effect, string label, string? bone, Vector3 offset, bool existing = false, string? source = null, string? parent = null)
    {
        /// <summary>The ability effect this one plays with (null: always on, placed on the model).</summary>
        public string? Parent { get; } = parent;

        /// <summary>The mod archive or loose file the effect is copied from (null: the game's or the opened mod's own).</summary>
        public string? Source { get; } = source;

        /// <summary>Already in the opened mod (built earlier): shown and played, not built again.</summary>
        public bool Existing { get; } = existing;

        public string Effect { get; } = effect;

        public string Label { get; } = label;

        public string? Bone { get; } = bone;

        public Vector3 Start { get; } = offset;

        public Vector3 Nudge { get; set; }

        public string? Problem { get; set; }

        public Vector3 Offset => Start + Nudge;

        public override string ToString() =>
            $"{System.IO.Path.GetFileNameWithoutExtension(Effect)}  ({(Parent is not null ? "with " + System.IO.Path.GetFileNameWithoutExtension(Parent) : Bone is null ? "follows the hero" : "on " + Bone)}{(Existing ? ", already in this mod" : string.Empty)}){(Problem is null ? string.Empty : "  - not shown here: " + Problem)}";
    }

    private sealed record EffectOffer(string Label, string Path, string? Source = null)
    {
        public override string ToString() => Label;
    }

    private readonly List<AddedRow> addedEffects = [];
    private readonly TextBox addedSearch = new() { PlaceholderText = "Search effects: hero, ability or file name" };
    private readonly ListBox addedChoices = new() { Height = 150 };
    private readonly ListBox addedList = new() { MaxHeight = 120 };
    private readonly ComboBox abilityParent = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 420 };
    private readonly TextBlock addedStatus = new() { Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly Slider[] addedNudge = [Shape(-40, 40, 0), Shape(-40, 40, 0), Shape(-40, 40, 0)];
    private readonly StackPanel addedSelected = new() { Spacing = 2, IsVisible = false };
    private List<EffectOffer> effectOffers = [];                        // the game's
    private readonly List<EffectOffer> looseOffers = [];                 // files chosen with Browse
    private IReadOnlyList<ModEntry> installed = [];

    /// <summary>Every effect on offer: chosen files, the game's by hero and ability, then the installed mods' own effect files.</summary>
    private IEnumerable<EffectOffer> Offers => looseOffers.Concat(effectOffers).Concat(installed
        .Where(other => other.Files.Count == 1 && other != mod)
        .SelectMany(other => other.Entries.Where(entry => entry.EndsWith(".vpcf_c", StringComparison.OrdinalIgnoreCase))
            .Select(entry => new EffectOffer($"Mod '{other.Name}': {System.IO.Path.GetFileNameWithoutExtension(entry)}", entry[..^2], other.Files[0]))));
    private EffectOffer? placing;
    private bool addedWired;
    private bool settingNudge;

    /// <summary>The effects to add in the next build.</summary>
    public IReadOnlyList<AddedEffect> AddedEffects => [.. addedEffects.Where(row => !row.Existing).Select(row => new AddedEffect(row.Effect, row.Bone, row.Offset, row.Source, row.Parent))];

    /// <summary>The rows shown on the model (always on); rows that play with an ability are heard of only by the build and by Play.</summary>
    private AddedRow[] PlacedRows => [.. addedEffects.Where(row => row.Parent is null)];

    private int NewAddedCount => addedEffects.Count(row => !row.Existing);

    /// <summary>Takes over the added effects the opened model already has (a mod built here earlier), before the new ones.</summary>
    private void ShowListedEffects(S2ModKit.Adapters.Preview.PreviewSession session)
    {
        addedEffects.RemoveAll(row => row.Existing);
        addedEffects.InsertRange(0, session.ListedEffects.Select(effect => new AddedRow(effect.Effect, effect.Effect, effect.Bone, effect.Offset, existing: true)));
        RefreshAddedList(addedList.SelectedItem as AddedRow);
        _ = ApplyPlacedAsync(files: true);
    }

    /// <summary>The "Added effects" part of the Effects tab (the same controls for every model).</summary>
    private StackPanel AddedEffectsSection()
    {
        if (!addedWired)
        {
            addedWired = true;
            addedSearch.TextChanged += (_, _) => ShowOffers();
            addedList.SelectionChanged += (_, _) => ShowAddedSelection();
            for (var axis = 0; axis < 3; axis++)
            {
                addedNudge[axis].ValueChanged += (_, _) => NudgeSelected();
            }

            preview.PlacedStopped += reason =>
            {
                addedStatus.Text = "The preview stopped an added effect it cannot draw (" + reason + "). The build still adds it.";
                AppLog.Shown(addedStatus.Text);
            };
            addedSelected.Children.Add(new TextBlock { Text = "Move the selected effect along its bone's axes", Foreground = Muted, FontSize = 11 });
            foreach (var slider in addedNudge)
            {
                addedSelected.Children.Add(slider);
            }

            addedSelected.Children.Add(Wrap(Button("Back to the clicked point", () => SetNudge(Vector3.Zero)), Button("Remove", RemoveSelectedAdded)));
        }

        foreach (var control in new Control[] { addedSearch, addedChoices, addedList, addedStatus, addedSelected })
        {
            Detach(control);
        }

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = "Added effects", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Pick any effect of the game or of an installed mod (or Browse... for an effect file), play it, then place it: click the hero where it should sit (it follows that body part), "
                + "or add it at the feet for effects around the whole hero, like snow. Effects that loop suit best: one that plays once shows only when the hero appears.",
            Foreground = Muted,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(addedSearch);
        panel.Children.Add(addedChoices);
        panel.Children.Add(Wrap(
            Button("Play", () =>
            {
                if (addedChoices.SelectedItem is EffectOffer offer)
                {
                    _ = PlayEffectAsync(offer.Path, offer.Source);
                }
            }),
            Button("Stop", StopEffectPreview),
            Button("Place on the hero", StartPlacing),
            Button("Add at the feet", () => AddEffect(null, Vector3.Zero)),
            Button("Browse...", () => _ = BrowseEffectAsync())));
        panel.Children.Add(new TextBlock
        {
            Text = "Or let it play during an ability: pick one of this hero's ability effects; the chosen effect starts with it, at the same place.",
            Foreground = Muted,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });
        Detach(abilityParent);
        abilityParent.ItemsSource = effectGroups.SelectMany(group => group.EffectList.Select(path => new EffectChoice(group, path))).ToArray();
        abilityParent.SelectedIndex = abilityParent.ItemCount > 0 ? 0 : -1;
        panel.Children.Add(abilityParent);
        panel.Children.Add(Wrap(Button("Add to this ability effect", AddToAbility), Button("Play the ability effect", () =>
        {
            if (abilityParent.SelectedItem is EffectChoice choice)
            {
                _ = PlayEffectAsync(choice.Path);
            }
        }), Button("Stop", StopEffectPreview)));
        panel.Children.Add(addedStatus);
        panel.Children.Add(addedList);
        panel.Children.Add(addedSelected);
        panel.Children.Add(new Border { Height = 1, Background = Muted, Opacity = 0.4, Margin = new Thickness(0, 6) });
        _ = LoadOffersAsync();
        RefreshAddedList(null);
        return panel;
    }

    private async Task LoadOffersAsync()
    {
        if (effectOffers.Count > 0)
        {
            ShowOffers();
            return;
        }

        abilityData ??= Task.Run(() => HeroAbilities.Read(settings.Game, Path.Combine(settings.Data, "hero-abilities.json")));
        var data = await abilityData.ConfigureAwait(true);
        effectOffers = await Task.Run(() =>
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var offers = new List<EffectOffer>();
            foreach (var hero in Hero.Catalogue.OrderBy(hero => hero.Name, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var group in HeroAbilities.Of(data, hero.Model))
                {
                    foreach (var path in group.EffectList.Where(seen.Add))
                    {
                        offers.Add(new EffectOffer($"{hero.Name} · {group.Name}: {System.IO.Path.GetFileNameWithoutExtension(path)}", path));
                    }
                }
            }

            return offers;
        }).ConfigureAwait(true);
        ShowOffers();
    }

    private void ShowOffers()
    {
        var words = (addedSearch.Text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = Offers.Where(offer => words.All(word => offer.Label.Contains(word, StringComparison.OrdinalIgnoreCase))).Take(ShownChoices + 1).ToArray();
        addedChoices.ItemsSource = matches.Take(ShownChoices).ToArray();
        if (effectOffers.Count == 0)
        {
            addedStatus.Text = "Reading the game's effects...";
        }
        else if (matches.Length > ShownChoices)
        {
            addedStatus.Text = $"The first {ShownChoices} effects; type a hero, ability or mod name to narrow the list.";
        }
        else if (placing is null)
        {
            addedStatus.Text = string.Empty;
        }
    }

    /// <summary>Offers a loose compiled effect file (and selects it): its child effects and textures are looked for beside it.</summary>
    private async Task BrowseEffectAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "A compiled effect file (.vpcf_c)",
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Compiled effect") { Patterns = ["*.vpcf_c"] }],
        }).ConfigureAwait(true);
        if (files.Count == 0 || Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(files[0]) is not { } path)
        {
            return;
        }

        try
        {
            using var source = EffectSource.Open(path);
            var offer = new EffectOffer($"File: {System.IO.Path.GetFileNameWithoutExtension(path)}", source.Top!, path);
            looseOffers.RemoveAll(other => other.Source == path);
            looseOffers.Insert(0, offer);
            addedSearch.Text = string.Empty;
            ShowOffers();
            addedChoices.SelectedItem = offer;
            addedStatus.Text = "The file is at the top of the list: Play it, then place it.";
        }
        catch (Exception exception) when (exception is IOException or S2ModKit.Domain.S2ModKitException)
        {
            addedStatus.Text = exception is S2ModKit.Domain.S2ModKitException { Error: var error } ? $"{error.Summary} {error.Remediation}" : exception.Message;
        }
    }

    private void StartPlacing()
    {
        if (addedChoices.SelectedItem is not EffectOffer offer)
        {
            addedStatus.Text = "Choose an effect in the list first.";
            return;
        }

        StopEffectPreview();                                                 // the model comes back to be clicked
        placing = offer;
        addedStatus.Text = $"Click the hero where '{System.IO.Path.GetFileNameWithoutExtension(offer.Path)}' should sit. Turn the view first if you need to (drag).";
    }

    /// <summary>A click on the view while an effect waits to be placed. Returns false when no effect waits.</summary>
    private bool PlaceAt(Point at)
    {
        if (placing is null)
        {
            return false;
        }

        preview.RequestPoint(at, point =>
        {
            if (point is not { } hit)
            {
                addedStatus.Text = "That click missed the hero. Click on the body.";
                return;
            }

            AddEffect(hit.Bone, hit.Offset);
        });
        return true;
    }

    private void AddEffect(string? bone, Vector3 offset)
    {
        var offer = placing ?? addedChoices.SelectedItem as EffectOffer;
        placing = null;
        if (offer is null)
        {
            addedStatus.Text = "Choose an effect in the list first.";
            return;
        }

        StopEffectPreview();
        var row = new AddedRow(offer.Path, offer.Label, bone, offset, source: offer.Source);
        addedEffects.Add(row);
        addedStatus.Text = bone is null
            ? $"Added at the hero's feet. Build edited mod puts it into the game."
            : $"Placed on '{bone}' (the marker). An effect made for another body can sit off the marker: move it with the sliders below until the effect itself is where you want it. Build edited mod puts it into the game.";
        RefreshAddedList(row);
        _ = ApplyPlacedAsync(files: true);
        UpdateSummary();
    }

    private void AddToAbility()
    {
        placing = null;
        if (addedChoices.SelectedItem is not EffectOffer offer || abilityParent.SelectedItem is not EffectChoice parent)
        {
            addedStatus.Text = "Choose an effect in the list and an ability effect first.";
            return;
        }

        var row = new AddedRow(offer.Path, offer.Label, null, Vector3.Zero, source: offer.Source, parent: parent.Path);
        addedEffects.Add(row);
        addedStatus.Text = $"It will play with {parent}. 'Play the ability effect' shows the two together; Build edited mod puts it into the game.";
        RefreshAddedList(row);
        UpdateSummary();
    }

    private void RemoveSelectedAdded()
    {
        if (addedList.SelectedItem is AddedRow { Existing: false } row)
        {
            addedEffects.Remove(row);
            RefreshAddedList(null);
            _ = ApplyPlacedAsync(files: true);
            UpdateSummary();
        }
    }

    private void RefreshAddedList(AddedRow? select)
    {
        addedList.ItemsSource = addedEffects.ToArray();
        addedList.IsVisible = addedEffects.Count > 0;
        addedList.SelectedItem = select is not null && addedEffects.Contains(select) ? select : addedEffects.LastOrDefault();
        ShowAddedSelection();
    }

    private void ShowAddedSelection()
    {
        var row = addedList.SelectedItem as AddedRow;
        addedSelected.IsVisible = row is { Existing: false };
        foreach (var slider in addedNudge)
        {
            slider.IsVisible = row is { Parent: null };                      // an ability's effect decides the place
        }

        preview.MarkedPlaced = row is null ? -1 : Array.IndexOf(PlacedRows, row);
        settingNudge = true;
        for (var axis = 0; axis < 3; axis++)
        {
            addedNudge[axis].Value = row is null ? 0 : axis switch { 0 => row.Nudge.X, 1 => row.Nudge.Y, _ => row.Nudge.Z };
        }

        settingNudge = false;
    }

    private void SetNudge(Vector3 nudge)
    {
        if (addedList.SelectedItem is AddedRow row)
        {
            row.Nudge = nudge;
            ShowAddedSelection();
            _ = ApplyPlacedAsync(files: false);
        }
    }

    private void NudgeSelected()
    {
        if (!settingNudge && addedList.SelectedItem is AddedRow row)
        {
            row.Nudge = new Vector3((float)addedNudge[0].Value, (float)addedNudge[1].Value, (float)addedNudge[2].Value);
            _ = ApplyPlacedAsync(files: false);
        }
    }

    /// <summary>Shows the added effects in the 3D view; <paramref name="files"/>: the set of effects changed, so their files are
    /// prepared for the preview again (not needed when only a point moved).</summary>
    private async Task ApplyPlacedAsync(bool files)
    {
        var rows = PlacedRows;
        var list = rows.Select(row => new PlacedEffect(row.Effect, row.Bone, row.Offset)).ToArray();
        byte[]? prepared = null;
        if (files && list.Length > 0)
        {
            var archives = shownArchives;
            var changes = EffectChanges;
            try
            {
                prepared = await Task.Run(() => EffectOverlay(archives, rows.Where(row => row.Source is null).Select(row => row.Effect), changes, settings.StagingRoot,
                    rows.Where(row => row.Source is not null).Select(row => (row.Effect, row.Source!)))).ConfigureAwait(true);
            }
#pragma warning disable CA1031 // the effects still play from their own files; the reason goes to the log
            catch (Exception exception)
#pragma warning restore CA1031
            {
                AppLog.Error("Added effect preview overlay", exception);
            }
        }

        AppLog.Info($"Added effects in the preview: {list.Length} ({string.Join(", ", list.Select(effect => System.IO.Path.GetFileNameWithoutExtension(effect.Effect) + "@" + (effect.Bone ?? "origin")))}); prepared files: {prepared?.Length ?? 0} bytes");

        preview.RequestPlacedEffects(list, prepared, refusals =>
        {
            var changed = false;
            if (refusals.Any(refusal => refusal is not null))
            {
                AppLog.Info("Added effects the preview cannot show: " + string.Join(" | ", refusals.Where(refusal => refusal is not null)));
            }

            for (var i = 0; i < rows.Length && i < refusals.Count; i++)
            {
                changed |= rows[i].Problem != refusals[i];
                rows[i].Problem = refusals[i];
            }

            if (changed)
            {
                var selected = addedList.SelectedItem as AddedRow;
                addedList.ItemsSource = addedEffects.ToArray();
                addedList.SelectedItem = selected;
            }
        });
    }

    /// <summary>For the window's screenshot mode: finds the first effect matching <paramref name="search"/> and places it where a
    /// click at the given part of the view (0-1 across and down) lands.</summary>
    public async Task PlaceForShotAsync(string search, double across, double down)
    {
        ShowTab(effectsTab);
        await LoadOffersAsync().ConfigureAwait(true);
        addedSearch.Text = search;
        ShowOffers();
        addedChoices.SelectedIndex = 0;
        StartPlacing();
        PlaceAt(new Point(preview.Bounds.Width * across, preview.Bounds.Height * down));
    }

    private void ClearAddedEffects(bool keepExisting = false)
    {
        placing = null;
        addedEffects.RemoveAll(row => !keepExisting || !row.Existing);
        preview.MarkedPlaced = -1;
    }
}
