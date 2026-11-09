namespace S2ModKit.Application;

/// <summary>
/// A colour change for effects. <see cref="Exact"/> false: every colour takes the target's hue and keeps its own brightness and
/// strength (glows stay glows, dark smoke stays dark, white and grey stay as they are); true: every colour becomes the target.
/// </summary>
public sealed record EffectColorChange(RgbColor Target, bool Exact = false);

/// <summary>Colour math for effect files (0-255 channels as the game stores them).</summary>
public static class EffectColors
{
    /// <summary>Below this strength (HSV saturation) a colour counts as white or grey and keeps its hue-less look.</summary>
    public const float GreyLimit = 0.08f;

    public static (int R, int G, int B) Apply((int R, int G, int B) colour, EffectColorChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Exact)
        {
            return (Byte(change.Target.R), Byte(change.Target.G), Byte(change.Target.B));
        }

        var (_, saturation, value) = ToHsv(colour.R / 255f, colour.G / 255f, colour.B / 255f);
        if (saturation < GreyLimit)
        {
            return colour;
        }

        var (hue, _, _) = ToHsv(change.Target.R, change.Target.G, change.Target.B);
        var (r, g, b) = FromHsv(hue, saturation, value);
        return (Byte(r), Byte(g), Byte(b));
    }

    public static (float H, float S, float V) ToHsv(float r, float g, float b)
    {
        var max = MathF.Max(r, MathF.Max(g, b));
        var min = MathF.Min(r, MathF.Min(g, b));
        var delta = max - min;
        var hue = delta <= 0 ? 0
            : max == r ? 60 * (((g - b) / delta) % 6)
            : max == g ? 60 * (((b - r) / delta) + 2)
            : 60 * (((r - g) / delta) + 4);
        return (hue < 0 ? hue + 360 : hue, max <= 0 ? 0 : delta / max, max);
    }

    public static (float R, float G, float B) FromHsv(float hue, float saturation, float value)
    {
        var c = value * saturation;
        var x = c * (1 - MathF.Abs(((hue / 60) % 2) - 1));
        var m = value - c;
        var (r, g, b) = (hue % 360) switch
        {
            < 60 => (c, x, 0f),
            < 120 => (x, c, 0f),
            < 180 => (0f, c, x),
            < 240 => (0f, x, c),
            < 300 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return (r + m, g + m, b + m);
    }

    private static int Byte(float channel) => (int)MathF.Round(Math.Clamp(channel, 0, 1) * 255);
}
