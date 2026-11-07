using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace S2ModKit.Desktop;

/// <summary>
/// A colour picker: a saturation/brightness square for the current hue, a hue strip beside it, a row of quick swatches and a
/// hex field. Raises <see cref="ColorChanged"/> while the user drags and on every edit.
/// </summary>
internal sealed class ColorPicker : UserControl
{
    private static readonly Color[] Swatches =
    [
        Color.FromRgb(20, 20, 22), Color.FromRgb(240, 240, 240), Color.FromRgb(200, 30, 40), Color.FromRgb(225, 120, 30),
        Color.FromRgb(235, 210, 40), Color.FromRgb(70, 170, 60), Color.FromRgb(40, 110, 200), Color.FromRgb(130, 50, 190),
    ];

    private readonly SaturationValueSquare square = new() { Height = 120 };
    private readonly HueStrip strip = new() { Width = 18, Height = 120 };
    private readonly TextBox hex = new() { Width = 90, FontFamily = new FontFamily("Consolas, monospace") };
    private readonly Border current = new() { Width = 36, Height = 22, CornerRadius = new CornerRadius(4), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
    private bool updating;

    public ColorPicker()
    {
        var swatches = new WrapPanel { ItemSpacing = 4, LineSpacing = 4 };
        foreach (var swatch in Swatches)
        {
            var button = new Button { Width = 22, Height = 22, Padding = default, Background = new SolidColorBrush(swatch), CornerRadius = new CornerRadius(4) };
            button.Click += (_, _) => Color = swatch;
            swatches.Children.Add(button);
        }

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto") };
        top.Children.Add(square);
        Grid.SetColumn(strip, 2);
        top.Children.Add(strip);
        Content = new StackPanel
        {
            Spacing = 6,
            Children = { top, swatches, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { current, hex } } },
        };
        square.Changed += () => Update(fromHex: false);
        strip.Changed += hue =>
        {
            square.Hue = hue;
            Update(fromHex: false);
        };
        hex.LostFocus += (_, _) => ReadHex();
        hex.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ReadHex();
            }
        };
        Update(fromHex: false);
    }

    public event Action<Color>? ColorChanged;

    public Color Color
    {
        get => square.Color;
        set
        {
            var (h, s, v) = Hsv(value);
            strip.Hue = h;
            square.Hue = h;
            square.Saturation = s;
            square.Value = v;
            Update(fromHex: false);
        }
    }

    /// <summary>Sets the colour without raising <see cref="ColorChanged"/> (when another part is selected).</summary>
    public void Show(Color color)
    {
        updating = true;
        Color = color;
        updating = false;
    }

    private void ReadHex()
    {
        if (Avalonia.Media.Color.TryParse((hex.Text ?? string.Empty).Trim().StartsWith('#') ? hex.Text!.Trim() : "#" + hex.Text?.Trim(), out var parsed))
        {
            Color = parsed;
        }
        else
        {
            Update(fromHex: false);                                         // put the valid value back
        }
    }

    private void Update(bool fromHex)
    {
        var color = square.Color;
        current.Background = new SolidColorBrush(color);
        if (!fromHex)
        {
            hex.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        square.InvalidateVisual();
        strip.InvalidateVisual();
        if (!updating)
        {
            ColorChanged?.Invoke(color);
        }
    }

    internal static Color FromHsv(float h, float s, float v)
    {
        var i = (int)(h * 6) % 6;
        var f = (h * 6) - MathF.Floor(h * 6);
        var (p, q, t) = (v * (1 - s), v * (1 - (f * s)), v * (1 - ((1 - f) * s)));
        var (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        return Avalonia.Media.Color.FromRgb((byte)MathF.Round(r * 255), (byte)MathF.Round(g * 255), (byte)MathF.Round(b * 255));
    }

    internal static (float H, float S, float V) Hsv(Color color)
    {
        var (r, g, b) = (color.R / 255f, color.G / 255f, color.B / 255f);
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        var h = d == 0 ? 0 : max == r ? ((g - b) / d % 6) : max == g ? ((b - r) / d) + 2 : ((r - g) / d) + 4;
        return (((h / 6) + 1) % 1, max == 0 ? 0 : d / max, max);
    }

    /// <summary>Saturation left to right, brightness top to bottom, for one hue; drag to choose.</summary>
    private sealed class SaturationValueSquare : Control
    {
        public float Hue { get; set; }

        public float Saturation { get; set; } = 0.8f;

        public float Value { get; set; } = 0.9f;

        public Color Color => FromHsv(Hue, Saturation, Value);

        public event Action? Changed;

        public override void Render(DrawingContext context)
        {
            var rect = new Rect(Bounds.Size);
            context.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(FromHsv(Hue, 1, 1), 1) },
            }, rect, 4);
            context.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Colors.Black, 1) },
            }, rect, 4);
            var at = new Point(Saturation * rect.Width, (1 - Value) * rect.Height);
            context.DrawEllipse(null, new Pen(Brushes.White, 2), at, 7, 7);
            context.DrawEllipse(null, new Pen(Brushes.Black, 1), at, 8.5, 8.5);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            e.Pointer.Capture(this);
            Move(e.GetPosition(this));
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (e.Pointer.Captured == this)
            {
                Move(e.GetPosition(this));
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e) => e.Pointer.Capture(null);

        private void Move(Point at)
        {
            Saturation = (float)Math.Clamp(at.X / Math.Max(1, Bounds.Width), 0, 1);
            Value = 1 - (float)Math.Clamp(at.Y / Math.Max(1, Bounds.Height), 0, 1);
            Changed?.Invoke();
        }
    }

    /// <summary>The hue strip, red at the top through the spectrum back to red; drag to choose.</summary>
    private sealed class HueStrip : Control
    {
        public float Hue { get; set; }

        public event Action<float>? Changed;

        public override void Render(DrawingContext context)
        {
            var brush = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative) };
            for (var i = 0; i <= 6; i++)
            {
                brush.GradientStops.Add(new GradientStop(FromHsv((i % 6) / 6f, 1, 1), i / 6.0));
            }

            context.FillRectangle(brush, new Rect(Bounds.Size), 4);
            var y = Hue * Bounds.Height;
            context.DrawRectangle(null, new Pen(Brushes.White, 2), new Rect(-1, y - 3, Bounds.Width + 2, 6), 2);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            e.Pointer.Capture(this);
            Move(e.GetPosition(this));
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (e.Pointer.Captured == this)
            {
                Move(e.GetPosition(this));
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e) => e.Pointer.Capture(null);

        private void Move(Point at)
        {
            Hue = (float)Math.Clamp(at.Y / Math.Max(1, Bounds.Height), 0, 0.999);
            Changed?.Invoke(Hue);
        }
    }
}
