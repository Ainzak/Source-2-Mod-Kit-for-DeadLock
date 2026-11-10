using System.Numerics;

namespace S2ModKit.Application;

/// <summary>
/// The colour settings of a hero material that follow a part recolor: tints, the outline colours and the see-through glow colour
/// (Deadlock's cartoon shading: <c>TextureNprTramsissiveColor1</c> with the game's spelling, <c>g_vSolidOutlineTint</c>,
/// <c>g_vSolidOutlineAdditive</c>, <c>g_vColorTint1</c>, <c>g_vSelfIllumTint1</c>...). White and grey values stay as they are (they
/// carry no colour, and a white tint must not start tinting the texture); coloured ones get the part's change, so a purple glow
/// becomes yellow with a yellow body. Masks, strengths and scales are not colours.
/// </summary>
public static class MaterialColors
{
    /// <summary>The see-through glow's colour texture, recolored as a whole with the part's change.</summary>
    public const string GlowTexture = "g_tNprTransmissiveColor";

    private const float GreyLimit = 0.04f;

    /// <summary>Whether a material vector setting is a colour that follows a recolor.</summary>
    public static bool Follows(string name, Vector4 value)
    {
        ArgumentNullException.ThrowIfNull(name);
        var colourName = (name.Contains("Tint", StringComparison.OrdinalIgnoreCase) || name.Contains("Color", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Additive", StringComparison.OrdinalIgnoreCase))
            && !name.Contains("Mask", StringComparison.OrdinalIgnoreCase) && !name.Contains("Strength", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("Scale", StringComparison.OrdinalIgnoreCase) && !name.Contains("Brightness", StringComparison.OrdinalIgnoreCase);
        var max = MathF.Max(value.X, MathF.Max(value.Y, value.Z));
        var min = MathF.Min(value.X, MathF.Min(value.Y, value.Z));
        return colourName && max - min > GreyLimit && max <= 1.0001f && min >= 0;
    }

    /// <summary>The value with the part's change (alpha kept).</summary>
    public static Vector4 Apply(Vector4 value, ColorAdjustment change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var (r, g, b) = RecolorMath.Change(value.X, value.Y, value.Z, change);
        return new Vector4(Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1), value.W);
    }
}
