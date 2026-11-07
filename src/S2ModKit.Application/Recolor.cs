using System.Globalization;
using System.Text.Json;
using S2ModKit.Domain;

namespace S2ModKit.Application;

/// <summary>An sRGB colour, components 0-1. Parses <c>#rrggbb</c>.</summary>
public readonly record struct RgbColor(float R, float G, float B)
{
    public static RgbColor Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var hex = text.TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            throw Errors.Input("RECOLOR_COLOR_INVALID", $"'{text}' is not a #rrggbb colour.", "Write colours as #rrggbb, for example #8a1c2b.");
        }

        return new RgbColor(((value >> 16) & 255) / 255f, ((value >> 8) & 255) / 255f, (value & 255) / 255f);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"#{(int)MathF.Round(R * 255):x2}{(int)MathF.Round(G * 255):x2}{(int)MathF.Round(B * 255):x2}");
}

/// <summary>
/// Per-part colour change, applied in this order: hue shift (degrees), saturation and brightness (multipliers), tint
/// (multiply by a colour, by <see cref="TintStrength"/>), colorize (take a colour's hue and saturation, keep the
/// brightness, by <see cref="ColorizeStrength"/>). With <see cref="ColorizeLightness"/> the colorize also moves each texel's
/// brightness toward the colour's lightness (HSL), keeping the shading: a mid colour (pure red, lightness 0.5) keeps the
/// brightness, black darkens the texel and white lightens it, so black and white can be painted. Defaults change nothing.
/// </summary>
public sealed record ColorAdjustment(
    float HueShiftDegrees = 0,
    float Saturation = 1,
    float Brightness = 1,
    RgbColor? Tint = null,
    float TintStrength = 1,
    RgbColor? Colorize = null,
    float ColorizeStrength = 1,
    bool ColorizeLightness = false);

/// <summary>One part: a colour texture (path in the archives), an optional mask image in that texture's space (red
/// channel, 0 = untouched, 1 = full change; none = the whole texture) and the change.</summary>
public sealed record RecolorPart(string Name, string Texture, string? Mask, ColorAdjustment Change);

/// <summary>
/// A recolor: archives in search order (a mod first, then the game), the parts. Output is a texture-only mod: each
/// changed texture compiled at its original path; it must load before the source mod (a lower slot number wins).
/// </summary>
public sealed record RecolorRequest(IReadOnlyList<string> Archives, IReadOnlyList<RecolorPart> Parts, string OutputDirectory, string PackageName = "recolor_dir.vpk");

/// <summary>A decoded colour texture: RGBA8 rows top-down, the compiler definition that rebuilds it at the same path,
/// its format and mip count, the archive it came from and the hash of its compiled bytes.</summary>
public sealed record DecodedTexture(string Path, int Width, int Height, byte[] Rgba, string Definition, string Format, int MipLevels, string Archive, ContentHash CompiledHash);

/// <summary>Size, format and mips of a compiled texture (output verification).</summary>
public sealed record TextureFacts(int Width, int Height, string Format, int MipLevels);

/// <summary>Texture work outside the core: decoding compiled textures, reading masks, compiling with the Source 2 SDK.</summary>
public interface ITextureWorkshop
{
    Task<DecodedTexture> DecodeAsync(IReadOnlyList<string> archives, string texturePath, CancellationToken cancellationToken = default);

    /// <summary>A mask image resized to the texture's size: one weight 0-1 per texel (red channel), rows top-down.</summary>
    Task<float[]> ReadMaskAsync(string path, int width, int height, CancellationToken cancellationToken = default);

    /// <summary>Compiles RGBA8 pixels with the texture's own definition in <paramref name="workDirectory"/>; returns the compiled bytes.</summary>
    Task<byte[]> CompileAsync(DecodedTexture texture, byte[] rgba, string workDirectory, CancellationToken cancellationToken = default);

    TextureFacts Inspect(byte[] compiled);

    byte[] EncodePng(int width, int height, byte[] rgba);
}

public sealed record RecolorPartResult(string Name, string Texture, long Texels, string MeanBefore, string MeanAfter, ContentHash? MaskHash);

public sealed record RecolorResult(string OutputDirectory, string Package, ContentHash PackageHash, IReadOnlyList<RecolorPartResult> Parts, IReadOnlyList<string> Textures);

/// <summary>
/// Recolors parts of colour textures and packages the changed textures as a new texture-only mod (operation
/// <c>recolor@1</c>). Inputs are never changed; the output folder must not exist. Each texture is decoded once, its parts
/// applied in order, compiled with its own definition (same path, size and format, at least its mips; verified) and
/// written to the package.
/// Evidence: <c>recolor.json</c> (input and output hashes, per-part texel counts and mean colours) and the recolored
/// images as PNG.
/// </summary>
public sealed class RecolorApplication(ITextureWorkshop workshop, IAddonArchiveWriter writer, IClock clock)
{
    public async Task<RecolorResult> RunAsync(RecolorRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var output = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(output);
        var work = Path.Combine(output, "work");
        var entries = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        var parts = new List<RecolorPartResult>();
        var textures = new List<object>();
        foreach (var group in request.Parts.GroupBy(part => TexturePath(part.Texture), StringComparer.OrdinalIgnoreCase))
        {
            var texture = await workshop.DecodeAsync(request.Archives, group.Key, cancellationToken).ConfigureAwait(false);
            var rgba = (byte[])texture.Rgba.Clone();
            foreach (var part in group)
            {
                float[]? mask = null;
                ContentHash? maskHash = null;
                if (part.Mask is { } maskPath)
                {
                    maskHash = ContentHash.Compute(await File.ReadAllBytesAsync(maskPath, cancellationToken).ConfigureAwait(false));
                    mask = await workshop.ReadMaskAsync(maskPath, texture.Width, texture.Height, cancellationToken).ConfigureAwait(false);
                }

                var before = (byte[])rgba.Clone();
                var texels = RecolorMath.Apply(rgba, mask, part.Change);
                parts.Add(new RecolorPartResult(part.Name, group.Key, texels, RecolorMath.Mean(before, mask).ToString(), RecolorMath.Mean(rgba, mask).ToString(), maskHash));
            }

            var compiled = await workshop.CompileAsync(texture, rgba, Path.Combine(work, "t" + textures.Count.ToString(CultureInfo.InvariantCulture)), cancellationToken).ConfigureAwait(false);
            var facts = workshop.Inspect(compiled);
            if (facts.Width != texture.Width || facts.Height != texture.Height || facts.Format != texture.Format || facts.MipLevels < texture.MipLevels)   // a fuller mip chain is allowed:
                                                                                                                                                           // some mods ship a single level (shimmers at a distance)
            {
                throw Errors.Verification("RECOLOR_TEXTURE_CHANGED_LAYOUT",
                    $"'{group.Key}' compiled as {facts.Width}x{facts.Height} {facts.Format} {facts.MipLevels} mips; the source is {texture.Width}x{texture.Height} {texture.Format} {texture.MipLevels} mips.",
                    "Report the texture; the recolor keeps the source's size, format and mips.");
            }

            var logical = group.Key + "_c";
            entries[logical] = compiled;
            var png = Path.Combine(output, Path.GetFileName(group.Key) + ".png");
            await File.WriteAllBytesAsync(png, workshop.EncodePng(texture.Width, texture.Height, rgba), cancellationToken).ConfigureAwait(false);
            textures.Add(new
            {
                path = logical,
                sourceArchive = texture.Archive,
                sourceHash = texture.CompiledHash.Value,
                outputHash = ContentHash.Compute(compiled).Value,
                texture.Width,
                texture.Height,
                texture.Format,
                texture.MipLevels,
                outputMipLevels = facts.MipLevels,
                image = Path.GetFileName(png),
            });
        }

        var package = Path.Combine(output, request.PackageName);
        await writer.WriteNewAsync(package, entries, cancellationToken).ConfigureAwait(false);
        var packageHash = ContentHash.Compute(await File.ReadAllBytesAsync(package, cancellationToken).ConfigureAwait(false));
        var record = new
        {
            operation = "recolor@1",
            createdUtc = clock.UtcNow.ToUniversalTime(),
            archives = request.Archives,
            parts = request.Parts.Select(part => new { part.Name, part.Texture, part.Mask, change = Describe(part.Change) }),
            results = parts,
            textures,
            package = Path.GetFileName(package),
            packageHash = packageHash.Value,
            note = "Texture-only mod: load it before the source mod (a lower pak number wins). Offline evidence, not a game test.",
        };
        await File.WriteAllTextAsync(Path.Combine(output, "recolor.json"), JsonDefaults.Serialize(record), cancellationToken).ConfigureAwait(false);
        return new RecolorResult(output, package, packageHash, parts, [.. entries.Keys]);
    }

    private static void Validate(RecolorRequest request)
    {
        if (request.Archives.Count == 0 || request.Parts.Count == 0)
        {
            throw Errors.Input("RECOLOR_EMPTY", "A recolor needs archives and at least one part.", "List the source archives and the parts.");
        }

        if (Directory.Exists(request.OutputDirectory) || File.Exists(request.OutputDirectory))
        {
            throw Errors.Input("RECOLOR_OUTPUT_EXISTS", $"'{request.OutputDirectory}' already exists.", "Choose a new output folder.");
        }

        foreach (var part in request.Parts)
        {
            var c = part.Change;
            if (!TexturePath(part.Texture).EndsWith(".vtex", StringComparison.OrdinalIgnoreCase))
            {
                throw Errors.Input("RECOLOR_TEXTURE_PATH", $"Part '{part.Name}': '{part.Texture}' is not a .vtex path.", "Use the colour texture path that `recolor textures` lists.");
            }

            if (c.Saturation is < 0 or > 4 || c.Brightness is < 0 or > 4 || c.TintStrength is < 0 or > 1 || c.ColorizeStrength is < 0 or > 1 || !float.IsFinite(c.HueShiftDegrees))
            {
                throw Errors.Input("RECOLOR_CHANGE_RANGE", $"Part '{part.Name}': saturation and brightness must be 0-4, strengths 0-1.", "Correct the part's values.");
            }

            if (part.Mask is { } mask && !File.Exists(mask))
            {
                throw Errors.Input("RECOLOR_MASK_MISSING", $"Part '{part.Name}': mask '{mask}' does not exist.", "Pass a mask image in the texture's space, or none for the whole texture.");
            }
        }
    }

    private static string TexturePath(string path) => (path.EndsWith("_c", StringComparison.Ordinal) ? path[..^2] : path).Replace('\\', '/');

    private static object Describe(ColorAdjustment c) => new
    {
        c.HueShiftDegrees,
        c.Saturation,
        c.Brightness,
        tint = c.Tint?.ToString(),
        c.TintStrength,
        colorize = c.Colorize?.ToString(),
        c.ColorizeStrength,
        c.ColorizeLightness,
    };
}

/// <summary>The colour change on RGBA8 pixels (sRGB values; alpha untouched).</summary>
public static class RecolorMath
{
    /// <summary>Changes the pixels in place, blended by the mask weight per texel (null: all texels); returns the texels changed.</summary>
    public static long Apply(byte[] rgba, float[]? mask, ColorAdjustment change)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentNullException.ThrowIfNull(change);
        var changed = 0L;
        for (int i = 0, t = 0; i + 3 < rgba.Length; i += 4, t++)
        {
            var weight = mask is null ? 1f : Math.Clamp(mask[t], 0f, 1f);
            if (weight <= 0f)
            {
                continue;
            }

            var (r, g, b) = (rgba[i] / 255f, rgba[i + 1] / 255f, rgba[i + 2] / 255f);
            var (nr, ng, nb) = Change(r, g, b, change);
            rgba[i] = ToByte(r + (nr - r) * weight);
            rgba[i + 1] = ToByte(g + (ng - g) * weight);
            rgba[i + 2] = ToByte(b + (nb - b) * weight);
            changed++;
        }

        return changed;
    }

    public static (float R, float G, float B) Change(float r, float g, float b, ColorAdjustment change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var (h, s, v) = ToHsv(r, g, b);
        h = ((h + change.HueShiftDegrees / 360f) % 1f + 1f) % 1f;
        s = Math.Clamp(s * change.Saturation, 0f, 1f);
        v = Math.Clamp(v * change.Brightness, 0f, 1f);
        (r, g, b) = FromHsv(h, s, v);
        if (change.Tint is { } tint)
        {
            r += (r * tint.R - r) * change.TintStrength;
            g += (g * tint.G - g) * change.TintStrength;
            b += (b * tint.B - b) * change.TintStrength;
        }

        if (change.Colorize is { } target)
        {
            var (th, ts, _) = ToHsv(target.R, target.G, target.B);
            var (_, _, cv) = ToHsv(r, g, b);
            if (change.ColorizeLightness)
            {
                var lightness = (MathF.Max(target.R, MathF.Max(target.G, target.B)) + MathF.Min(target.R, MathF.Min(target.G, target.B))) / 2f;
                cv = lightness < 0.5f ? cv * lightness * 2f : cv + ((1f - cv) * ((lightness * 2f) - 1f));
            }

            var (cr, cg, cb) = FromHsv(th, ts, cv);
            r += (cr - r) * change.ColorizeStrength;
            g += (cg - g) * change.ColorizeStrength;
            b += (cb - b) * change.ColorizeStrength;
        }

        return (Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f));
    }

    /// <summary>Mean colour of the texels weighted by the mask (evidence).</summary>
    public static RgbColor Mean(byte[] rgba, float[]? mask)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        double r = 0, g = 0, b = 0, w = 0;
        for (int i = 0, t = 0; i + 3 < rgba.Length; i += 4, t++)
        {
            var weight = mask is null ? 1f : Math.Clamp(mask[t], 0f, 1f);
            r += rgba[i] * weight;
            g += rgba[i + 1] * weight;
            b += rgba[i + 2] * weight;
            w += weight;
        }

        return w <= 0 ? default : new RgbColor((float)(r / w / 255), (float)(g / w / 255), (float)(b / w / 255));
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    private static (float H, float S, float V) ToHsv(float r, float g, float b)
    {
        var max = MathF.Max(r, MathF.Max(g, b));
        var min = MathF.Min(r, MathF.Min(g, b));
        var d = max - min;
        var h = d <= 0 ? 0 : max == r ? (g - b) / d / 6f : max == g ? ((b - r) / d + 2f) / 6f : ((r - g) / d + 4f) / 6f;
        return ((h % 1f + 1f) % 1f, max <= 0 ? 0 : d / max, max);
    }

    private static (float R, float G, float B) FromHsv(float h, float s, float v)
    {
        var i = (int)MathF.Floor(h * 6f) % 6;
        var f = h * 6f - MathF.Floor(h * 6f);
        var (p, q, t) = (v * (1 - s), v * (1 - f * s), v * (1 - (1 - f) * s));
        return i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
    }
}
