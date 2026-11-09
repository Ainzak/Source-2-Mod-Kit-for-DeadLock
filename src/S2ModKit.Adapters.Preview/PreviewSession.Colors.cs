using System.Collections.Concurrent;
using OpenTK.Graphics.OpenGL;
using S2ModKit.Application;
using SkiaSharp;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Preview;

/// <summary>Live recolor of parts in the preview: the part's colour texture is decoded once, every changed part that uses it is
/// applied through its own UV mask (<see cref="RecolorMath"/>, the same math as <c>s2mod recolor</c>) and the result replaces the
/// texture on the GPU. The model and the archives are not changed.</summary>
public sealed partial class PreviewSession
{
    private const string ColorTexture = "g_tColor";
    private readonly Dictionary<int, ColorAdjustment> partColors = [];
    private readonly Dictionary<string, Recolored> recolored = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(int Part, int Width, int Height), float[]> masks = [];
    private readonly Dictionary<int, (RgbColor Target, float Tolerance)> ranges = [];
    private readonly ConcurrentDictionary<string, Task<Pixels?>> unpacking = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(int Part, int Width, int Height), float[]> uvMasks = new();   // computed beside the unpacking

    /// <summary>Whether part <paramref name="index"/>'s colour texture is unpacked, so a recolor applies at once (no colour texture: true).</summary>
    public bool ColorReady(int index) =>
        PartColorTexture(index) is not { } path || recolored.ContainsKey(path) || (unpacking.TryGetValue(path, out var task) && task.IsCompleted);

    /// <summary>
    /// Starts unpacking part <paramref name="index"/>'s colour texture on a worker thread (a large compressed texture takes seconds);
    /// the file is read here, the pixels are decoded in the background. Call again until <see cref="ColorReady"/> is true.
    /// </summary>
    public void PrepareColor(int index)
    {
        if (PartColorTexture(index) is not { } path || recolored.ContainsKey(path) || unpacking.ContainsKey(path))
        {
            return;
        }

        var resource = loader.LoadFileCompiled(path);
        unpacking[path] = resource is { DataBlock: Texture texture }
            ? Task.Run(() =>
            {
                var pixels = Unpack(texture);
                if (pixels is not null)
                {
                    uvMasks[(index, pixels.PreviewWidth, pixels.PreviewHeight)] = picker.Mask(index, pixels.PreviewWidth, pixels.PreviewHeight);   // reads only texture coordinates
                }

                return pixels;
            })
            : Task.FromResult<Pixels?>(null);
    }

    /// <summary>The part under a pixel of the last frame and the colour of its texture there (the original, before any recolor).</summary>
    public (int Part, RgbColor Color)? SamplePartColor(float x, float y, int width, int height)
    {
        if (PickWithUv(x, y, width, height) is not { } hit || PartColorTexture(hit.Part) is not { } path || Decode(path) is not { } source)
        {
            return null;
        }

        var tx = Math.Clamp((int)(hit.Uv.X * source.Width), 0, source.Width - 1);
        var ty = Math.Clamp((int)(hit.Uv.Y * source.Height), 0, source.Height - 1);
        var i = ((ty * source.Width) + tx) * 4;
        return (hit.Part, new RgbColor(source.Rgba[i] / 255f, source.Rgba[i + 1] / 255f, source.Rgba[i + 2] / 255f));
    }

    /// <summary>
    /// Limits part <paramref name="index"/>'s recolor to texels whose original colour is within <paramref name="tolerance"/> of
    /// <paramref name="target"/> (0-1, average RGB distance, with a soft edge), for clothes painted onto the same mesh as skin;
    /// null target: the whole part.
    /// </summary>
    public void SetPartColorRange(int index, RgbColor? target, float tolerance)
    {
        if (target is { } color)
        {
            ranges[index] = (color, tolerance);
        }
        else
        {
            ranges.Remove(index);
        }

        foreach (var key in masks.Keys.Where(key => key.Part == index).ToArray())
        {
            masks.Remove(key);
        }

        if (PartColorTexture(index) is { } path)
        {
            Refresh(path);
        }
    }

    /// <summary>The colour texture (path without <c>_c</c>) part <paramref name="index"/> draws with, if it has one.</summary>
    public string? PartColorTexture(int index)
    {
        var material = Draw(index).Material.Material;
        return material.TextureParams.TryGetValue(ColorTexture, out var path) ? path : null;
    }

    /// <summary>The part's UV mask in its colour texture's size (1 = texels the part covers); null without a colour texture.</summary>
    public (float[] Mask, int Width, int Height)? PartColorMask(int index)
    {
        if (PartColorTexture(index) is not { } path || Decode(path) is not { } source)
        {
            return null;
        }

        return (Mask(index, source.Width, source.Height), source.Width, source.Height);
    }

    /// <summary>Recolors part <paramref name="index"/> in the preview (null: back to the original colours).</summary>
    public void SetPartColor(int index, ColorAdjustment? change)
    {
        if (PartColorTexture(index) is not { } path)
        {
            return;
        }

        if (change is null)
        {
            partColors.Remove(index);
        }
        else
        {
            partColors[index] = change;
        }

        Refresh(path);
    }

    private void Refresh(string path)
    {
        var users = Enumerable.Range(0, Parts.Count).Where(part => string.Equals(PartColorTexture(part), path, StringComparison.OrdinalIgnoreCase)).ToArray();
        var changed = users.Where(partColors.ContainsKey).ToArray();
        if (Decode(path) is not { } source)
        {
            return;
        }

        if (changed.Length == 0)
        {
            foreach (var part in users)
            {
                Draw(part).Material.Textures[ColorTexture] = source.Original;
            }

            source.Replacement?.Delete();
            recolored[path] = source with { Replacement = null };

            return;
        }

        var rgba = (byte[])source.PreviewRgba.Clone();                      // the live preview works at most 2048 wide: big textures stay responsive
        foreach (var part in changed)
        {
            RecolorMath.Apply(rgba, Mask(part, source.PreviewWidth, source.PreviewHeight), partColors[part]);
        }

        source.Replacement?.Delete();
        using var resource = new ValveResourceFormat.Resource { FileName = path + "_c" };
        resource.Read(new MemoryStream(RgbaTexture(rgba, source.PreviewWidth, source.PreviewHeight)));
        var replacement = context.MaterialLoader.LoadTexture(resource, srgbRead: source.Srgb, isViewerRequest: false);   // the renderer's own upload
        recolored[path] = source with { Replacement = replacement };
        foreach (var part in users)
        {
            Draw(part).Material.Textures[ColorTexture] = replacement;
        }
    }

    private float[] Mask(int part, int width, int height)
    {
        if (!masks.TryGetValue((part, width, height), out var mask))
        {
            mask = uvMasks.TryRemove((part, width, height), out var ready) ? ready : picker.Mask(part, width, height);
            if (ranges.TryGetValue(part, out var range) && PartColorTexture(part) is { } path && Decode(path) is { } source && source.Colours(width, height) is { } colours)
            {
                const float Soft = 0.05f;
                for (var t = 0; t < mask.Length; t++)
                {
                    if (mask[t] <= 0)
                    {
                        continue;
                    }

                    var (dr, dg, db) = ((colours[t * 4] / 255f) - range.Target.R, (colours[(t * 4) + 1] / 255f) - range.Target.G, (colours[(t * 4) + 2] / 255f) - range.Target.B);
                    var distance = MathF.Sqrt(((dr * dr) + (dg * dg) + (db * db)) / 3);
                    mask[t] *= Math.Clamp(1 - ((distance - range.Tolerance) / Soft), 0, 1);
                }
            }

            masks[(part, width, height)] = mask;
        }

        return mask;
    }

    private Recolored? Decode(string path)
    {
        if (recolored.TryGetValue(path, out var known))
        {
            return known;
        }

        if (Draw(Enumerable.Range(0, Parts.Count).First(part => string.Equals(PartColorTexture(part), path, StringComparison.OrdinalIgnoreCase))).Material.Textures.GetValueOrDefault(ColorTexture) is not { } original)
        {
            return null;
        }

        if (!unpacking.TryGetValue(path, out var task))
        {
            task = Task.FromResult(loader.LoadFileCompiled(path) is { DataBlock: Texture texture } ? Unpack(texture) : null);   // not prepared: unpack now
            unpacking[path] = task;
        }

        if (task.GetAwaiter().GetResult() is not { } pixels)                // callers check ColorReady first; a build waits here
        {
            return null;
        }

        GL.GetTextureLevelParameter(original.Handle, 0, GetTextureParameter.TextureInternalFormat, out int format);
        var srgb = format is 0x8E8D or 0x8C43 or 0x8C41 or 0x8C4C or 0x8C4D or 0x8C4E or 0x8C4F;   // sRGB BC7, RGBA8, RGB8 and S3TC
        var decoded = new Recolored(pixels.Rgba, pixels.Width, pixels.Height, original, null, srgb, pixels.Preview, pixels.PreviewWidth, pixels.PreviewHeight);
        recolored[path] = decoded;
        return decoded;
    }

    /// <summary>Decodes a compressed texture to RGBA8 and its preview copy (at most 2048 wide); no OpenGL, safe on a worker thread.</summary>
    private static Pixels? Unpack(Texture texture)
    {
        using var bitmap = texture.GenerateBitmap();
        var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var rgba = new byte[info.BytesSize];
        unsafe
        {
            fixed (byte* target = rgba)
            {
                if (!bitmap.PeekPixels().ReadPixels(info, (IntPtr)target, info.RowBytes, 0, 0))
                {
                    return null;
                }
            }
        }

        var (preview, previewWidth, previewHeight) = (rgba, bitmap.Width, bitmap.Height);
        while (previewWidth > 2048 || previewHeight > 2048)
        {
            (preview, previewWidth, previewHeight) = Half(preview, previewWidth, previewHeight);
        }

        return new Pixels(rgba, bitmap.Width, bitmap.Height, preview, previewWidth, previewHeight);
    }

    private sealed record Pixels(byte[] Rgba, int Width, int Height, byte[] Preview, int PreviewWidth, int PreviewHeight);

    private DrawCall Draw(int index) => node.RenderableMeshes.SelectMany(mesh => mesh.DrawCalls).ElementAt(index);

    private void DeleteRecolored()
    {
        foreach (var texture in recolored.Values)
        {
            texture.Replacement?.Delete();
        }

        recolored.Clear();
        unpacking.Clear();
        uvMasks.Clear();
    }

    /// <summary>
    /// A compiled texture in memory (VTEX v1, RGBA8888) with a full chain of box-downsampled mip levels, smallest first, so the
    /// renderer uploads recolored pixels exactly as it uploads any texture.
    /// </summary>
    private static byte[] RgbaTexture(byte[] rgba, int width, int height)
    {
        var levels = new List<byte[]> { rgba };
        var (w, h) = (width, height);
        while (w > 1 || h > 1)
        {
            var (next, nw, nh) = Half(levels[^1], w, h);
            levels.Add(next);
            (w, h) = (nw, nh);
        }

        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        const int Header = 16 + 12 + 4;                                    // resource header, one block entry, padding to 16
        const int Vtex = 40;
        writer.Write((uint)(Header + Vtex));                                // a texture's size field covers the resource part only
        writer.Write((ushort)12);
        writer.Write((ushort)1);
        writer.Write(8u);
        writer.Write(1u);
        writer.Write("DATA"u8);
        writer.Write(12u);                                                  // block at 32, relative to this field
        writer.Write((uint)Vtex);
        writer.Write(0u);
        writer.Write((ushort)1);                                            // VTEX version
        writer.Write((ushort)0);
        writer.Write(new byte[16]);                                         // reflectivity
        writer.Write((ushort)width);
        writer.Write((ushort)height);
        writer.Write((ushort)1);
        writer.Write((byte)ValveResourceFormat.VTexFormat.RGBA8888);
        writer.Write((byte)levels.Count);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        for (var level = levels.Count - 1; level >= 0; level--)
        {
            writer.Write(levels[level]);
        }

        return stream.ToArray();
    }

    /// <summary>A 2x2 box-filtered half-size copy (one mip level).</summary>
    private static (byte[] Rgba, int Width, int Height) Half(byte[] rgba, int width, int height)
    {
        var (w, h) = (Math.Max(1, width / 2), Math.Max(1, height / 2));
        var output = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            var row0 = Math.Min(height - 1, y * 2) * width * 4;
            var row1 = Math.Min(height - 1, (y * 2) + 1) * width * 4;
            var target = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                var c0 = Math.Min(width - 1, x * 2) * 4;
                var c1 = Math.Min(width - 1, (x * 2) + 1) * 4;
                for (var c = 0; c < 4; c++)
                {
                    output[target + (x * 4) + c] = (byte)((rgba[row0 + c0 + c] + rgba[row0 + c1 + c] + rgba[row1 + c0 + c] + rgba[row1 + c1 + c] + 2) >> 2);
                }
            }
        }

        return (output, w, h);
    }

    private sealed record Recolored(byte[] Rgba, int Width, int Height, RenderTexture Original, RenderTexture? Replacement, bool Srgb,
        byte[] PreviewRgba, int PreviewWidth, int PreviewHeight)
    {
        /// <summary>The original colours at the full or the preview size; null for another size.</summary>
        public byte[]? Colours(int width, int height) =>
            width == Width && height == Height ? Rgba : width == PreviewWidth && height == PreviewHeight ? PreviewRgba : null;
    }
}
