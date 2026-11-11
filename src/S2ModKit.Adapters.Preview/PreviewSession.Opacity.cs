namespace S2ModKit.Adapters.Preview;

/// <summary>What the window needs to know to make a part see-through in a build: the part's material and who shares it. The
/// preview itself does not draw the part see-through yet (the renderer's blended pass draws a material swapped in after loading
/// black; tried 2026-11-11).</summary>
public sealed partial class PreviewSession
{
    /// <summary>The compiled material path of part <paramref name="index"/> (<c>.vmat</c>, without <c>_c</c>).</summary>
    public string PartMaterialPath(int index) => Draw(index).Material.Material.Name;

    /// <summary>How many other parts are drawn with part <paramref name="index"/>'s material (they turn see-through with it).</summary>
    public int PartMaterialSharers(int index)
    {
        var material = Draw(index).Material;
        return drawOrder.Count(item => ReferenceEquals(item.Draw.Material, material)) - 1;
    }
}

/// <summary>
/// The alpha of a colour texture that makes parts see-through. The hero shader's translucency is the colour texture's alpha
/// times the material's opacity, and an opaque material's colour alpha is unused (often 0 everywhere, which would hide the whole
/// material), so the build sets it solid first and then lowers it only where the chosen parts are.
/// </summary>
public static class SeeThroughMath
{
    /// <summary>Every texel's alpha to 255 (solid).</summary>
    public static void Solid(byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        for (var i = 3; i < rgba.Length; i += 4)
        {
            rgba[i] = 255;
        }
    }

    /// <summary>Takes the alpha of <paramref name="from"/> (same size) and keeps the colours of <paramref name="rgba"/>.</summary>
    public static void CopyAlpha(byte[] rgba, byte[] from)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentOutOfRangeException.ThrowIfNotEqual(from.Length, rgba.Length);
        for (var i = 3; i < rgba.Length; i += 4)
        {
            rgba[i] = from[i];
        }
    }

    /// <summary>Lowers the alpha inside the mask to <paramref name="opacity"/> of what it was (1 solid, 0 invisible), weighted by the mask.</summary>
    public static void Apply(byte[] rgba, float[] mask, float opacity)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentNullException.ThrowIfNull(mask);
        var keep = Math.Clamp(opacity, 0f, 1f);
        for (int i = 3, t = 0; i < rgba.Length && t < mask.Length; i += 4, t++)
        {
            var weight = Math.Clamp(mask[t], 0f, 1f);
            if (weight > 0)
            {
                rgba[i] = (byte)Math.Round(rgba[i] * (1 - (weight * (1 - keep))));
            }
        }
    }
}
