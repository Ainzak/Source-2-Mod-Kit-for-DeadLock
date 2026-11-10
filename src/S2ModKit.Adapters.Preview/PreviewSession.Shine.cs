using System.Collections.Concurrent;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.TextureDecoders;

namespace S2ModKit.Adapters.Preview;

/// <summary>
/// Live shine: a part's roughness in its material's <c>g_tNormalRoughness</c> is lowered inside the part's UV mask, so the part reflects
/// its surroundings (latex, metal) as in the game. 0 leaves the part as it is, 1 is near mirror (<see cref="ShineMath.Roughness"/>).
/// The texture is unpacked raw (as stored, without VRF's normal decoding) in the background, at most 1024 texels wide, because the
/// renderer's shaders read the stored layout: Deadlock's <c>HemiOctRB</c> textures keep the normal in red and green, roughness in blue.
/// </summary>
public sealed partial class PreviewSession
{
    public const string NormalRoughnessTexture = "g_tNormalRoughness";
    private const int ShineSize = 1024;
    private readonly Dictionary<int, float> partShine = [];
    private readonly ConcurrentDictionary<string, Task<RawPixels?>> shineUnpacking = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (RenderTexture Original, RenderTexture? Replacement)> shineTextures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The normal-roughness texture part <paramref name="index"/> draws with (path without <c>_c</c>), if any.</summary>
    public string? PartNormalTexture(int index) => Draw(index).Material.Material.TextureParams.TryGetValue(NormalRoughnessTexture, out var path) ? path : null;

    /// <summary>Whether part <paramref name="index"/>'s normal-roughness texture is unpacked (no texture: true).</summary>
    public bool ShineReady(int index) => PartNormalTexture(index) is not { } path || (shineUnpacking.TryGetValue(path, out var task) && task.IsCompleted);

    /// <summary>Starts unpacking part <paramref name="index"/>'s normal-roughness texture in the background.</summary>
    public void PrepareShine(int index)
    {
        if (PartNormalTexture(index) is not { } path || shineUnpacking.ContainsKey(path))
        {
            return;
        }

        var resource = loader.LoadFileCompiled(path);
        shineUnpacking[path] = resource is { DataBlock: Texture texture } ? Task.Run(() => UnpackRaw(texture)) : Task.FromResult<RawPixels?>(null);
    }

    /// <summary>Sets part <paramref name="index"/>'s shine (0-1; null or 0: as the game has it).</summary>
    public void SetPartShine(int index, float? shine)
    {
        if (PartNormalTexture(index) is not { } path)
        {
            return;
        }

        if (shine is > 0)
        {
            partShine[index] = Math.Clamp(shine.Value, 0, 1);
        }
        else
        {
            partShine.Remove(index);
        }

        var users = Enumerable.Range(0, Parts.Count + Pieces.Count).Where(part => string.Equals(PartNormalTexture(part), path, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!shineTextures.TryGetValue(path, out var state))
        {
            if (Draw(users[0]).Material.Textures.GetValueOrDefault(NormalRoughnessTexture) is not { } original)
            {
                return;
            }

            state = (original, null);
        }

        state.Replacement?.Delete();
        state.Replacement = null;
        var changed = users.Where(partShine.ContainsKey).ToArray();
        if (changed.Length > 0 && shineUnpacking.TryGetValue(path, out var task) && task.GetAwaiter().GetResult() is { } pixels)
        {
            var rgba = (byte[])pixels.Rgba.Clone();
            var whole = pixels.Width * pixels.Height <= 16;                        // a one-value texture (4x4): the whole material
            foreach (var part in changed)
            {
                ShineMath.Apply(rgba, whole ? null : picker.Mask(part, pixels.Width, pixels.Height), partShine[part], pixels.Channel);
            }

            using var resource = new ValveResourceFormat.Resource { FileName = path + "_c" };
            resource.Read(new MemoryStream(RgbaTexture(rgba, pixels.Width, pixels.Height)));
            state.Replacement = context.MaterialLoader.LoadTexture(resource, srgbRead: false, isViewerRequest: false);   // normals are linear
        }

        shineTextures[path] = state;
        foreach (var part in users)
        {
            Draw(part).Material.Textures[NormalRoughnessTexture] = state.Replacement ?? state.Original;
        }
    }

    /// <summary>The stored pixels of the first level at most <see cref="ShineSize"/> wide, and the channel that holds roughness.</summary>
    private static RawPixels? UnpackRaw(Texture texture)
    {
        uint level = 0;
        while (level + 1 < texture.NumMipLevels && Math.Max(texture.Width >> (int)level, texture.Height >> (int)level) > ShineSize)
        {
            level++;
        }

        using var bitmap = texture.GenerateBitmap(0, Texture.CubemapFace.PositiveX, level, TextureCodec.None);
        var rgba = new byte[bitmap.Width * bitmap.Height * 4];
        var info = new SkiaSharp.SKImageInfo(bitmap.Width, bitmap.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
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

        return new RawPixels(rgba, bitmap.Width, bitmap.Height, ShineMath.RoughnessChannel(texture.RetrieveCodecFromResourceEditInfo()));
    }

    private sealed record RawPixels(byte[] Rgba, int Width, int Height, int Channel);
}

/// <summary>The roughness change of a shine edit, on a normal-roughness texture's stored pixels (255 = matte).</summary>
public static class ShineMath
{
    /// <summary>The roughness full shine reaches (near mirror, as the owner's in-game latex test).</summary>
    public const float Roughness = 0.05f;

    /// <summary>The stored channel that holds roughness: blue for <c>HemiOctRB</c> (Deadlock's normal-roughness textures), else alpha.</summary>
    public static int RoughnessChannel(TextureCodec codec) => (codec & TextureCodec.HemiOctRB) != 0 ? 2 : 3;

    /// <summary>Lowers <paramref name="channel"/> towards <see cref="Roughness"/> by <paramref name="shine"/>, weighted by the mask (null:
    /// every texel).</summary>
    public static void Apply(byte[] rgba, float[]? mask, float shine, int channel = 3)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentOutOfRangeException.ThrowIfLessThan(channel, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, 3);
        var target = Roughness * 255f;
        for (int i = channel, t = 0; i < rgba.Length; i += 4, t++)
        {
            var weight = (mask is null ? 1f : Math.Clamp(mask[t], 0f, 1f)) * shine;
            if (weight > 0)
            {
                rgba[i] = (byte)Math.Round(rgba[i] + ((target - rgba[i]) * weight));
            }
        }
    }
}
