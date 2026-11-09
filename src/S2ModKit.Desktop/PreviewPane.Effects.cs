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

    /// <summary>The effect recolors to build: every effect file of each changed group with its change.</summary>
    public IReadOnlyDictionary<string, EffectColorChange> EffectChanges =>
        effectGroups.Where(group => effectChanges.ContainsKey(group.Slot))
            .SelectMany(group => group.EffectList.Select(effect => (effect, change: effectChanges[group.Slot])))
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
        }

        if (effectsModel != modelPath)
        {
            effectChanges.Clear();
        }

        effectsModel = modelPath;
        effectSwatches.Clear();
        abilityData ??= Task.Run(() => HeroAbilities.Read(settings.Game, Path.Combine(settings.Data, "hero-abilities.json")));
        effectGroups = [.. HeroAbilities.Of(await abilityData.ConfigureAwait(true), modelPath).Where(group => group.EffectList.Count > 0)];
        BuildEffectsView();
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
        if (effectGroups.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "No effects are known for this model (only heroes have them).", Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Tick the abilities whose effects you want in a new colour, then pick the colour. The squares are each ability's main "
                    + "effect colours now, and after the arrow, how they will look. Colours keep their brightness, so glows stay glows.",
                Foreground = Muted,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
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
                Text = "The 3D preview does not play effects yet. Build edited mod makes the version with the new colours.",
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

    private void SetAllEffectChecks(bool on)
    {
        foreach (var (_, row) in effectRows)
        {
            row.Check.IsChecked = on;                                       // each box updates its own group
        }
    }
}
