using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using S2ModKit.Domain;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

/// <summary>
/// Writes new pixels into a compiled texture without the Source 2 compiler. The header and every block stay as
/// they are; only the pixel data after them is replaced: each mip level is re-encoded in the texture's own format from
/// box-downsampled RGBA (BCnEncoder.NET generates the levels) and stored smallest level first, as the game reads it. Every
/// level must have exactly its original size, so the file keeps its size and layout. Layouts this cannot rebuild exactly are
/// refused: compressed mip data, cube maps and volumes, padded non-power-of-two canvases, HDR and uncommon formats.
/// </summary>
public static class InPlaceTexture
{
    /// <param name="logicalPath">The texture's path in the game (<c>..._c</c>), which tells VRF the resource type.</param>
    public static byte[] Write(byte[] compiled, string logicalPath, int width, int height, byte[] rgba) => Write(compiled, logicalPath, width, height, rgba, fastCopy: false);

    /// <param name="fastCopy">For a BC7 texture written as a new file of the mod: encode it as BC3 (DXT5) instead, whose blocks have the same
    /// size, and mark the format so: about twenty times faster (2 s instead of 50 s for 4096 x 4096) at a slightly lower colour quality.</param>
    public static byte[] Write(byte[] compiled, string logicalPath, int width, int height, byte[] rgba, bool fastCopy)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(rgba);
        using var resource = new Resource { FileName = logicalPath };
        resource.Read(new MemoryStream(compiled));
        var texture = resource.DataBlock as Texture ?? throw Errors.Verification("RECOLOR_OUTPUT_NOT_TEXTURE", "The file is not a texture.", "Report the texture.");
        var asBc3 = fastCopy && texture.Format == VTexFormat.BC7;
        var format = asBc3 ? CompressionFormat.Bc3 : Format(texture.Format);
        if (texture.Width != width || texture.Height != height || rgba.Length != width * height * 4)
        {
            throw Errors.Verification("RECOLOR_PIXELS_SIZE", $"The pixels are {width}x{height}; the texture is {texture.Width}x{texture.Height}.", "Report the texture.");
        }

        // a picture smaller than its storage is only rewritten as one level (a 1x1 colour in one 4x4 block): the encoder fills the same block
        var paddedSingle = texture.NumMipLevels == 1 && texture.ActualWidth <= texture.Width && texture.ActualHeight <= texture.Height;
        if (texture.Depth != 1 || (texture.Flags & VTexFlags.CUBE_TEXTURE) != 0 || texture.ExtraData.ContainsKey(VTexExtraData.COMPRESSED_MIP_SIZE)
            || ((texture.ActualWidth != texture.Width || texture.ActualHeight != texture.Height) && !paddedSingle))
        {
            throw Errors.Unsupported("RECOLOR_TEXTURE_LAYOUT", $"'{texture.Format}' {texture.Width}x{texture.Height} with this layout (cube, volume, compressed or padded mips) cannot be rewritten in place.",
                "Recolor it with the SDK compiler (--resourcecompiler and --sdk-game).");
        }

        var levels = (int)texture.NumMipLevels;
        var sizes = Enumerable.Range(0, levels).Select(level => texture.CalculateBufferSizeForMipLevel((uint)level)).ToArray();   // bytes per level, largest first
        var pixelBytes = sizes.Sum();
        var start = compiled.Length - pixelBytes;
        if (start < texture.Offset + texture.Size)
        {
            throw Errors.Unsupported("RECOLOR_TEXTURE_LAYOUT", "The pixel data does not follow the texture's blocks.", "Recolor it with the SDK compiler.");
        }

        var encoder = new BcEncoder(format);
        encoder.OutputOptions.GenerateMipMaps = levels > 1;
        encoder.OutputOptions.MaxMipMapLevel = levels;
        encoder.OutputOptions.Quality = CompressionQuality.Balanced;
        encoder.Options.IsParallel = true;
        var encoded = encoder.EncodeToRawBytes(rgba, width, height, PixelFormat.Rgba32);
        if (encoded.Length < levels || Enumerable.Range(0, levels).Any(level => encoded[level].Length != sizes[level]))
        {
            throw Errors.Verification("RECOLOR_MIP_SIZE", $"The re-encoded levels ({string.Join(", ", encoded.Select(level => level.Length))}) do not match the texture's ({string.Join(", ", sizes)}).",
                "Report the texture; recolor it with the SDK compiler meanwhile.");
        }

        var output = (byte[])compiled.Clone();
        if (asBc3)
        {
            // the texture header's format byte (version, flags, reflectivity, width, height, depth come first: 26 bytes)
            var formatAt = (int)texture.Offset + 26;
            if (output[formatAt] != (byte)VTexFormat.BC7)
            {
                throw Errors.Verification("RECOLOR_TEXTURE_LAYOUT", "The texture header has an unknown layout.", "Report the texture.");
            }

            output[formatAt] = (byte)VTexFormat.DXT5;
        }

        var at = start;
        for (var level = levels - 1; level >= 0; level--)                   // smallest level first
        {
            encoded[level].CopyTo(output, at);
            at += sizes[level];
        }

        return output;
    }

    /// <summary>
    /// The stored pixels of the top level (RGBA, without VRF's normal or colour decoding: what <see cref="Write"/> stores back) and the
    /// texture's codec flags, which say how its channels are used (<c>HemiOctRB</c>: normal in red and green, roughness in blue). A
    /// picture smaller than its storage (1x1 in a 4x4 block) is repeated to the storage size.
    /// </summary>
    public static (byte[] Rgba, int Width, int Height, ValveResourceFormat.TextureDecoders.TextureCodec Codec) ReadStored(byte[] compiled, string logicalPath)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        using var resource = new Resource { FileName = logicalPath };
        resource.Read(new MemoryStream(compiled));
        var texture = resource.DataBlock as Texture ?? throw Errors.Input("RECOLOR_NOT_A_TEXTURE", $"'{logicalPath}' is not a texture.", "Pass a texture (.vtex).");
        using var bitmap = texture.GenerateBitmap(0, Texture.CubemapFace.PositiveX, 0, ValveResourceFormat.TextureDecoders.TextureCodec.None);
        var rgba = new byte[texture.Width * texture.Height * 4];
        for (var y = 0; y < texture.Height; y++)
        {
            for (var x = 0; x < texture.Width; x++)
            {
                var colour = bitmap.GetPixel(x * bitmap.Width / texture.Width, y * bitmap.Height / texture.Height);
                var at = ((y * texture.Width) + x) * 4;
                (rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3]) = (colour.Red, colour.Green, colour.Blue, colour.Alpha);
            }
        }

        return (rgba, texture.Width, texture.Height, texture.RetrieveCodecFromResourceEditInfo());
    }

    private static CompressionFormat Format(VTexFormat format) => format switch
    {
        VTexFormat.BC7 => CompressionFormat.Bc7,
        VTexFormat.DXT1 => CompressionFormat.Bc1,
        VTexFormat.DXT5 => CompressionFormat.Bc3,
        VTexFormat.ATI1N => CompressionFormat.Bc4,
        VTexFormat.ATI2N => CompressionFormat.Bc5,
        VTexFormat.RGBA8888 => CompressionFormat.Rgba,
        VTexFormat.BGRA8888 => CompressionFormat.Bgra,
        _ => throw Errors.Unsupported("RECOLOR_TEXTURE_FORMAT", $"'{format}' textures cannot be rewritten in place.", "Recolor it with the SDK compiler (--resourcecompiler and --sdk-game)."),
    };
}
