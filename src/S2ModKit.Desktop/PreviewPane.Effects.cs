using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using S2ModKit.Application;

namespace S2ModKit.Desktop;

/// <summary>
/// The Effects tab: the hero's effects grouped by ability 1-4, Weapon and Other (from the game's own data), each with its main
/// colours. Ticked groups follow the picked colour at once (no Apply step): every colour keeps its brightness and strength and
/// takes the picked hue (glows stay glows); "Exactly this colour" replaces them. Unticking puts a group back. The change goes
/// into the same Build as part edits. The tab is built once per model and then only updated, so the colour picker is never
/// moved while it is used.
/// </summary>
internal sealed partial class PreviewPane
{
    private readonly ToggleButton effectsTab = new() { Content = "Effects" };
    private readonly ColorPicker effectColor = new();
    private readonly CheckBox effectExact = new() { Content = "Exactly this colour (flat, no shading)" };
    private readonly Dictionary<int, EffectColorChange> effectChanges = [];
    private readonly Dictionary<int, IReadOnlyList<(int R, int G, int B)>> effectSwatches = [];
    private readonly Dictionary<int, (CheckBox Check, WrapPanel Before, WrapPanel After)> effectRows = [];
    private IReadOnlyList<HeroAbility> effectGroups = [];
    private IReadOnlyList<string> shownArchives = [];
    private string? effectsModel;
    private ScrollViewer? effectsView;
    private bool effectsWired;
    private readonly ComboBox effectPicker = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 420 };
    private readonly TextBlock effectStatus = new() { Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private bool effectPlaying;

    /// <summary>An effect in the picker: its group and file.</summary>
    private sealed record EffectChoice(HeroAbility Group, string Path)
    {
        public override string ToString() => $"{(Group.Slot is >= 1 and <= 4 ? Group.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " : string.Empty)}{Group.Name}: {System.IO.Path.GetFileName(Path)}";
    }

    /// <summary>The effect recolors to build: every effect file of each changed group with its change.</summary>
    public IReadOnlyDictionary<string, EffectColorChange> EffectChanges =>
        effectGroups.Where(group => effectChanges.ContainsKey(group.Slot))
            .SelectMany(group => group.EffectList.Concat(group.ModelList).Select(effect => (effect, change: effectChanges[group.Slot])))   // and the models the ability turns into
            .GroupBy(item => item.effect, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().change, StringComparer.Ordinal);

    private EffectColorChange PickedEffectColor =>
        new(new RgbColor(effectColor.Color.R / 255f, effectColor.Color.G / 255f, effectColor.Color.B / 255f), effectExact.IsChecked == true);

    /// <summary>Loads the effect groups of the opened model and their colours (in the background).</summary>
    private async Task LoadEffectsAsync(string modelPath)
    {
        if (!effectsWired)
        {
            effectsWired = true;
            effectColor.ColorChanged += _ => FollowPickedColor();
            effectExact.IsCheckedChanged += (_, _) => FollowPickedColor();
            preview.EffectStopped += reason =>
            {
                effectPlaying = false;
                effectStatus.Text = reason + " The colour squares still show the change.";
                AppLog.Shown(effectStatus.Text);
            };
        }

        StopEffectPreview();

        if (effectsModel != modelPath)
        {
            effectChanges.Clear();
            ClearAddedEffects();
        }

        effectsModel = modelPath;
        effectSwatches.Clear();
        abilityData ??= Task.Run(() => HeroAbilities.Read(settings.Game, Path.Combine(settings.Data, "hero-abilities.json")));
        effectGroups = [.. HeroAbilities.Of(await abilityData.ConfigureAwait(true), modelPath).Where(group => group.EffectList.Count > 0)];
        BuildEffectsView();
        _ = ApplyPlacedAsync(files: true);                                   // a reopened view shows the added effects again
        var archives = shownArchives;
        var groups = effectGroups;
        var swatches = await Task.Run(() =>
        {
            var files = EffectFiles.Read(archives, groups.SelectMany(group => group.EffectList));
            return groups.ToDictionary(group => group.Slot, group => EffectFiles.Swatches(group.EffectList.Where(files.ContainsKey).Select(effect => (effect, files[effect])), 8));
        }).ConfigureAwait(true);
        if (effectsModel != modelPath)
        {
            return;                                                         // another model was opened meanwhile
        }

        foreach (var (slot, colours) in swatches)
        {
            effectSwatches[slot] = colours;
            UpdateEffectRow(slot);
        }
    }

    /// <summary>The tab's content for the current model (built once per model).</summary>
    private ScrollViewer EffectsView() => effectsView ?? BuildEffectsView();

    private ScrollViewer BuildEffectsView()
    {
        Detach(effectColor);                                                 // from the previous model's view
        Detach(effectExact);
        effectRows.Clear();
        var panel = new StackPanel { Margin = new Thickness(10, 4), Spacing = 4 };
        if (previewWanted)
        {
            panel.Children.Add(AddedEffectsSection());
        }

        if (effectGroups.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = previewWanted ? "No effects are known for this model (only heroes have them)." : "Press Show 3D preview first: the effects are read with the model.",
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            panel.Children.Add(new TextBlock { Text = "Effect colours", FontWeight = FontWeight.SemiBold });
            panel.Children.Add(new TextBlock
            {
                Text = "Tick the abilities whose effects you want in a new colour, then pick the colour. The squares are each ability's main "
                    + "effect colours now, and after the arrow, how they will look. Colours keep their brightness, so glows stay glows.",
                Foreground = Muted,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
            effectPicker.ItemsSource = effectGroups.SelectMany(group => group.EffectList.Select(path => new EffectChoice(group, path))).ToArray();
            effectPicker.SelectedIndex = 0;
            panel.Children.Add(new TextBlock { Text = "Preview an effect", Foreground = Muted });
            Detach(effectPicker);
            Detach(effectStatus);
            panel.Children.Add(effectPicker);
            panel.Children.Add(Wrap(Button("Play", () =>
            {
                if (effectPicker.SelectedItem is EffectChoice choice)
                {
                    _ = PlayEffectAsync(choice.Path);
                }
            }), Button("Stop", StopEffectPreview)));
            panel.Children.Add(effectStatus);
            panel.Children.Add(Wrap(Button("Tick all", () => SetAllEffectChecks(true)), Button("Untick all", () => SetAllEffectChecks(false))));
            foreach (var group in effectGroups)
            {
                var check = new CheckBox { IsChecked = effectChanges.ContainsKey(group.Slot), Content = group.Slot is >= 1 and <= 4 ? $"{group.Slot}  {group.Name}" : group.Name };
                ToolTip.SetTip(check, $"{group.EffectList.Count} effect files");
                check.IsCheckedChanged += (_, _) =>
                {
                    if (check.IsChecked == true)
                    {
                        effectChanges[group.Slot] = PickedEffectColor;
                    }
                    else
                    {
                        effectChanges.Remove(group.Slot);
                    }

                    UpdateEffectRow(group.Slot);
                    UpdateSummary();
                };
                var before = new WrapPanel();
                var after = new WrapPanel();
                panel.Children.Add(check);
                panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(26, 0, 0, 6), Children = { before, after } });
                effectRows[group.Slot] = (check, before, after);
                UpdateEffectRow(group.Slot);
            }

            panel.Children.Add(new TextBlock { Text = "Colour", Foreground = Muted, Margin = new Thickness(0, 6, 0, 0) });
            panel.Children.Add(effectColor);
            panel.Children.Add(effectExact);
            panel.Children.Add(new TextBlock
            {
                Text = "Play shows one effect alone, in your colours when its ability is ticked. Build edited mod makes the version with the new colours.",
                Foreground = Muted,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        effectsView = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = panel };
        if (effectsTab.IsChecked == true)
        {
            toolHost.Content = effectsView;
        }

        UpdateSummary();
        return effectsView;
    }

    private void UpdateEffectRow(int slot)
    {
        if (!effectRows.TryGetValue(slot, out var row))
        {
            return;
        }

        row.Before.Children.Clear();
        row.After.Children.Clear();
        var colours = effectSwatches.GetValueOrDefault(slot);
        if (colours is null || colours.Count == 0)
        {
            row.Before.Children.Add(new TextBlock { Text = colours is null ? "reading colours..." : "no strong colours", Foreground = Muted, FontSize = 11 });
            return;
        }

        foreach (var colour in colours)
        {
            row.Before.Children.Add(Swatch(colour));
        }

        if (effectChanges.GetValueOrDefault(slot) is { } change)
        {
            row.After.Children.Add(new TextBlock { Text = " → ", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
            foreach (var colour in colours)
            {
                row.After.Children.Add(Swatch(EffectColors.Apply(colour, change)));
            }
        }
    }

    private static Border Swatch((int R, int G, int B) colour) => new()
    {
        Width = 16,
        Height = 16,
        Margin = new Thickness(0, 0, 3, 3),
        CornerRadius = new CornerRadius(3),
        BorderBrush = Brushes.Black,
        BorderThickness = new Thickness(1),
        Background = new SolidColorBrush(Color.FromRgb((byte)colour.R, (byte)colour.G, (byte)colour.B)),
    };

    /// <summary>Ticked groups take the picked colour (and the "exactly" choice) at once.</summary>
    private void FollowPickedColor()
    {
        var change = PickedEffectColor;
        foreach (var slot in effectChanges.Keys.ToArray())
        {
            effectChanges[slot] = change;
            UpdateEffectRow(slot);
        }

        UpdateSummary();
    }

    /// <summary>Plays the picked effect alone in the 3D view; when its ability has a new colour, with the recolored files of every changed
    /// ability in front (so child effects change too).</summary>
    private async Task PlayEffectAsync(string effect, string? source = null)
    {
        placing = null;
        effectStatus.Text = "Preparing the effect...";
        byte[]? overlay = null;
        var changes = EffectChanges;
        var archives = shownArchives;
        try
        {
            var added = addedEffects.Select(row => (Effect: row.Effect, Source: row.Source)).Append((Effect: effect, Source: source)).ToArray();
            var children = addedEffects.Where(row => row.Parent is not null).ToLookup(row => row.Parent!, row => row.Effect, StringComparer.OrdinalIgnoreCase);   // the added effects' files stay prepared while one effect plays alone
            overlay = await Task.Run(() => EffectOverlay(archives, added.Where(item => item.Source is null).Select(item => item.Effect), changes, settings.StagingRoot,
                added.Where(item => item.Source is not null).Select(item => (item.Effect, item.Source!)), children)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or S2ModKit.Domain.S2ModKitException)
        {
            AppLog.Error("Effect preview overlay", exception);
            effectStatus.Text = "Could not prepare the effect for the preview: " + exception.Message;
        }

        preview.RequestEffect(effect, overlay, refusal =>
        {
            effectPlaying = refusal is null;
            effectStatus.Text = refusal is null
                ? $"Playing {System.IO.Path.GetFileName(effect)}{(changes.ContainsKey(effect) ? " in the new colours" : " in its current colours")}; it repeats every few seconds."
                : refusal + " The colour squares still show the change.";
            if (refusal is not null)
            {
                AppLog.Shown(effectStatus.Text);
            }
        });
    }

    private void StopEffectPreview()
    {
        if (effectPlaying)
        {
            preview.RequestEffect(null, null);
        }

        effectPlaying = false;
        effectStatus.Text = string.Empty;
    }

    /// <summary>
    /// A VPK (bytes) for the preview only (written to the staging folder and deleted): the effect and its child effects, recolored where
    /// their ability has a new colour, and simplified so the preview's renderer can read them (<see cref="S2ModKit.Adapters.Source2.CompiledParticleEdit.ForPreview"/>).
    /// </summary>
    internal byte[]? EffectOverlayFor(string effect) => EffectOverlay(shownArchives, [effect], EffectChanges, settings.StagingRoot);

    /// <param name="children">Effects added to an ability effect (parent path to child paths): the parent's preview copy gets them.</param>
    /// <param name="sourced">Effects from another mod or a loose file (effect, source): every file the source ships for them goes in
    /// under its own path, so the preview shows them without the source among the shown archives.</param>
    private static byte[]? EffectOverlay(IReadOnlyList<string> archives, IEnumerable<string> effects, IReadOnlyDictionary<string, EffectColorChange> changes, string staging,
        IEnumerable<(string Effect, string Source)>? sourced = null, ILookup<string, string>? children = null)
    {
        var entries = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        var plain = effects.ToList();
        foreach (var (effect, source) in (sourced ?? []).Distinct())
        {
            var closure = EffectClosure.Read(source, effect);
            foreach (var (file, bytes) in closure.Effects)
            {
                entries[file + "_c"] = S2ModKit.Adapters.Source2.CompiledParticleEdit.ForPreview(bytes, file + "_c").Bytes;
                plain.AddRange(S2ModKit.Adapters.Source2.CompiledAddedEffects.References(bytes, file + "_c")
                    .Where(name => name.EndsWith(".vpcf", StringComparison.OrdinalIgnoreCase) && !closure.Effects.ContainsKey(name)));   // children the game has
            }

            foreach (var (file, bytes) in closure.Extras)
            {
                entries[file + "_c"] = bytes;
            }
        }

        effects = plain;
        foreach (var (file, original) in effects.Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(effect => EffectFiles.ReadTree(archives, effect)).DistinctBy(pair => pair.Key))
        {
            var bytes = changes.TryGetValue(file, out var change) ? S2ModKit.Adapters.Source2.CompiledParticleEdit.Recolor(original, file + "_c", change).Bytes : original;
            foreach (var child in children?[file] ?? [])                    // effects added to this ability effect play with it
            {
                bytes = S2ModKit.Adapters.Source2.CompiledAddedEffects.AddChild(bytes, file + "_c", child);
            }

            var (simple, simplified) = S2ModKit.Adapters.Source2.CompiledParticleEdit.ForPreview(bytes, file + "_c");
            if ((simplified > 0 || !ReferenceEquals(bytes, original)) && !entries.ContainsKey(file + "_c"))
            {
                entries[file + "_c"] = simple;
            }
        }

        if (entries.Count == 0)
        {
            return null;
        }

        Directory.CreateDirectory(staging);
        var path = System.IO.Path.Combine(staging, $"effect-preview-{Guid.NewGuid():N}_dir.vpk");
        try
        {
            S2ModKit.Adapters.Vpk.CompactVpkArchiveIO.WriteAsync(path, entries).GetAwaiter().GetResult();
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private void SetAllEffectChecks(bool on)
    {
        foreach (var (_, row) in effectRows)
        {
            row.Check.IsChecked = on;                                       // each box updates its own group
        }
    }
}
