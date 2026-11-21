using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using S2ModKit.Adapters.Source2;

namespace S2ModKit.Desktop;

/// <summary>
/// The Glow controls of the Colour tab: one colour over the whole part, strength, an optional pulse and an optional brighter edge. They
/// belong to the picked part or piece; the build gives it its own material copy with the glow. See-through (the slider above) with a glow
/// makes a glowing see-through figure.
/// </summary>
internal sealed partial class PreviewPane
{
    private static readonly Color GlowDefaultColor = Color.FromRgb(150, 80, 255);

    private readonly CheckBox glowOn = new() { Content = "Glow" };
    private readonly ColorPicker glowColor = new();
    private readonly Slider glowStrength = new() { Minimum = 0.1, Maximum = 3, Value = 1 };
    private readonly Slider glowPulseSpeed = new() { Minimum = 0, Maximum = 6, Value = 0 };
    private readonly Slider glowPulseAmount = new() { Minimum = 0, Maximum = 100, Value = 40 };
    private readonly CheckBox glowEdges = new() { Content = "Brighter at the edges" };
    private readonly StackPanel glowControls = new() { Spacing = 2, IsVisible = false };
    private readonly Hint lookHint = new("Glow (work in progress)", string.Empty);
    private Grid? pulseAmountRow;
    private readonly ComboBox glowPreset = new() { PlaceholderText = "Saved glows", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox presetName = new() { PlaceholderText = "Preset name", MinWidth = 140 };
    private readonly StackPanel presetNameRow = new() { Orientation = Orientation.Horizontal, Spacing = 6, IsVisible = false };
    private readonly TextBlock presetNote = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private GlowPresets? presets;

    private GlowPresets Presets => presets ??= LoadPresets();

    /// <summary>The glow controls for the "Shine, see-through and glow" fold.</summary>
    private Control[] LookRows()
    {
        pulseAmountRow = Labelled("Pulse amount", glowPulseAmount, "0\\%");
        glowControls.Children.Add(PresetRows());
        glowControls.Children.Add(glowColor);
        glowControls.Children.Add(Labelled("Strength", glowStrength, "0.00"));
        glowControls.Children.Add(Labelled("Pulse speed", glowPulseSpeed, "0.0;;off"));
        glowControls.Children.Add(pulseAmountRow);
        glowControls.Children.Add(glowEdges);

        glowOn.IsCheckedChanged += (_, _) => ApplyLook();
        glowEdges.IsCheckedChanged += (_, _) => ApplyLook();
        glowColor.ColorChanged += _ => ApplyLook();
        foreach (var slider in new[] { glowStrength, glowPulseSpeed, glowPulseAmount })
        {
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty)
                {
                    ApplyLook();
                }
            };
        }

        return
        [
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { Hint.Beside(glowOn, lookHint) } },
            glowControls,
        ];
    }

    /// <summary>Saved glows: pick one to set the controls; Save as preset names the current glow; Delete removes the picked one.</summary>
    private StackPanel PresetRows()
    {
        glowPreset.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<GlowPreset>((preset, _) => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new Border
                {
                    Width = 12,
                    Height = 12,
                    CornerRadius = new CornerRadius(6),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = preset is null ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(Byte(preset.R), Byte(preset.G), Byte(preset.B))),
                },
                new TextBlock { Text = preset?.Name ?? string.Empty, VerticalAlignment = VerticalAlignment.Center },
            },
        });
        ShowPresets(null);
        glowPreset.SelectionChanged += (_, _) =>
        {
            if (!settingSliders && glowPreset.SelectedItem is GlowPreset preset)
            {
                UsePreset(preset);
            }
        };

        var save = new Button { Content = "Save as preset" };
        var delete = new Button { Content = "Delete" };
        var confirm = new Button { Content = "Save" };
        var cancel = new Button { Content = "Cancel" };
        save.Click += (_, _) =>
        {
            presetName.Text = (glowPreset.SelectedItem as GlowPreset)?.Name ?? string.Empty;
            presetNameRow.IsVisible = true;
            presetNote.IsVisible = false;
            presetName.Focus();
        };
        cancel.Click += (_, _) => presetNameRow.IsVisible = false;
        confirm.Click += (_, _) => SavePreset();
        presetName.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                SavePreset();
            }
        };
        delete.Click += (_, _) =>
        {
            if (glowPreset.SelectedItem is GlowPreset preset && Try(() => Presets.Delete(preset.Name)))
            {
                ShowPresets(null);
            }
        };
        presetNameRow.Children.Add(presetName);
        presetNameRow.Children.Add(confirm);
        presetNameRow.Children.Add(cancel);

        var picker = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetColumn(glowPreset, 1);
        picker.Children.Add(new TextBlock { Text = "Preset", Foreground = Muted, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        picker.Children.Add(glowPreset);
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                picker,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Children = { save, delete } },
                presetNameRow,
                presetNote,
            },
        };
    }

    private GlowPresets LoadPresets()
    {
        var loaded = GlowPresets.Load(settings.Data);
        if (loaded.Problem is { } problem)
        {
            ShowPresetNote(problem);
        }

        return loaded;
    }

    private void ShowPresetNote(string text)
    {
        presetNote.Text = text;
        presetNote.Foreground = Warn;
        presetNote.IsVisible = true;
        AppLog.Shown(text);
    }

    private void ShowPresets(GlowPreset? picked)
    {
        settingSliders = true;
        glowPreset.ItemsSource = Presets.Items.ToArray();
        glowPreset.SelectedItem = picked is null ? null : Presets.Items.FirstOrDefault(item => item.Name == picked.Name);
        settingSliders = false;
    }

    private void UsePreset(GlowPreset preset)
    {
        settingSliders = true;
        glowColor.Show(Color.FromRgb(Byte(preset.R), Byte(preset.G), Byte(preset.B)));
        glowStrength.Value = preset.Strength;
        glowPulseSpeed.Value = preset.PulseSpeed;
        glowPulseAmount.Value = preset.PulseAmount * 100;
        glowEdges.IsChecked = preset.Edges;
        settingSliders = false;
        ApplyLook();
    }

    private void SavePreset()
    {
        if (string.IsNullOrWhiteSpace(presetName.Text))
        {
            ShowPresetNote("Enter a name for the preset.");
            return;
        }

        GlowPreset? saved = null;
        if (Try(() => saved = Presets.Save(presetName.Text, CurrentLook())))
        {
            presetNameRow.IsVisible = false;
            presetNote.IsVisible = false;
            ShowPresets(saved);
        }
    }

    private bool Try(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowPresetNote(exception.Message);
            return false;
        }
    }

    private MaterialLook CurrentLook()
    {
        var color = glowColor.Color;
        return new MaterialLook(color.R / 255f, color.G / 255f, color.B / 255f, (float)glowStrength.Value,
            (float)glowPulseSpeed.Value, (float)(glowPulseAmount.Value / 100), glowEdges.IsChecked == true);
    }

    private static byte Byte(float value) => (byte)Math.Round(Math.Clamp(value, 0f, 1f) * 255);

    private void ApplyLook()
    {
        ShowLookRows();
        if (settingSliders || selected is not { } row)
        {
            return;
        }

        if (glowOn.IsChecked != true)
        {
            preview.PartLooks.Remove(row.Index);
        }
        else
        {
            preview.PartLooks[row.Index] = CurrentLook();
        }

        UpdateSummary();
    }

    private void ShowLookRows()
    {
        glowControls.IsVisible = glowOn.IsChecked == true;
        if (pulseAmountRow is not null)
        {
            pulseAmountRow.IsEnabled = glowPulseSpeed.Value > 0;               // no pulse: the amount does nothing
            pulseAmountRow.Opacity = pulseAmountRow.IsEnabled ? 1 : 0.45;
        }
    }

    /// <summary>Shows the picked part's glow (none: the defaults); the caller has <c>settingSliders</c> set.</summary>
    private void ShowLook(PartRow row)
    {
        var look = preview.PartLooks.GetValueOrDefault(row.Index);
        glowOn.IsChecked = look is not null;
        glowColor.Show(look is null ? GlowDefaultColor : Color.FromRgb(Byte(look.R), Byte(look.G), Byte(look.B)));
        glowStrength.Value = look?.Strength ?? 1;
        glowPulseSpeed.Value = look?.PulseSpeed ?? 0;
        glowPulseAmount.Value = (look?.PulseAmount ?? 0.4f) * 100;
        glowEdges.IsChecked = look?.Edges == true;
        glowPreset.SelectedItem = null;                                     // a part shows its own glow, not a preset
        presetNameRow.IsVisible = false;
        ShowLookRows();
        lookHint.Text = "Only this part or piece glows: it gets its own copy of the material. The glow covers the part with one colour; Strength is "
            + "how much of it shows. Pulse speed above 0 makes it rise and fall by Pulse amount. Brighter at the edges glows more at the outline "
            + "than in the middle and tints the part. For a glowing see-through figure, also move See-through above (for example 45 %). "
            + "The preview shows the glow, with its pulse, once it is built (select the built mod). "
            + "Save as preset keeps these settings under a name for any part of any mod; pick it under Preset to use it again.";
    }

    /// <summary>For the window's screenshot mode: the selected part gets a glow and the controls are scrolled into view.</summary>
    public void LookForShot()
    {
        glowOn.IsChecked = true;
        glowPulseSpeed.Value = 2;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => glowEdges.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background);
    }
}
