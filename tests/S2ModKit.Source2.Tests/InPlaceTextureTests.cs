using System.Text;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using ValveResourceFormat;

namespace S2ModKit.Source2.Tests;

public sealed class InPlaceTextureTests
{
    /// <summary>A minimal compiled texture: resource header, one DATA block (VTEX v1) and the pixel data, smallest level first.</summary>
    internal static byte[] Texture(VTexFormat format, int width, int height, int levels, Func<int, byte[]> level)
    {
        var data = new MemoryStream();
        var writer = new BinaryWriter(data);
        writer.Write((ushort)1);                                            // VTEX version
        writer.Write((ushort)0);                                            // flags
        writer.Write(new byte[16]);                                         // reflectivity
        writer.Write((ushort)width);
        writer.Write((ushort)height);
        writer.Write((ushort)1);                                            // depth
        writer.Write((byte)format);
        writer.Write((byte)levels);
        writer.Write(0u);                                                   // picmip resolution
        writer.Write(0u);                                                   // extra data offset and count
        writer.Write(0u);
        var block = data.ToArray();

        var file = new MemoryStream();
        var header = new BinaryWriter(file);
        header.Write((uint)(32 + block.Length));                            // a texture's size field covers the resource part, not the pixels
        header.Write((ushort)12);
        header.Write((ushort)1);
        header.Write(8u);                                                   // block table right after this field pair
        header.Write(1u);
        header.Write(Encoding.ASCII.GetBytes("DATA"));
        header.Write(12u);                                                  // block at 32, relative to this field (20)
        header.Write((uint)block.Length);
        header.Write(new byte[4]);
        header.Write(block);
        for (var i = levels - 1; i >= 0; i--)
        {
            header.Write(level(i));
        }

        return file.ToArray();
    }

    [Fact]
    public void NewPixelsReplaceEveryLevelSmallestFirstAndTheRestStays()
    {
        var original = Texture(VTexFormat.RGBA8888, 8, 8, 4, level => new byte[(8 >> level) * (8 >> level) * 4]);
        var red = Enumerable.Range(0, 8 * 8).SelectMany(_ => new byte[] { 200, 10, 20, 255 }).ToArray();

        var written = InPlaceTexture.Write(original, "materials/test/red.vtex_c", 8, 8, red);

        Assert.Equal(original.Length, written.Length);
        var pixels = (1 * 1 * 4) + (2 * 2 * 4) + (4 * 4 * 4) + (8 * 8 * 4);   // 1x1, 2x2, 4x4, 8x8 levels
        Assert.Equal(original[..^pixels], written[..^pixels]);             // header and block untouched
        Assert.Equal(red, written[^(8 * 8 * 4)..]);                       // the largest level is stored last, exactly as given (uncompressed)
        Assert.Equal(new byte[] { 200, 10, 20, 255 }, written[^pixels..^(pixels - 4)]);   // the 1x1 level first: the average of a flat colour
    }

    [Fact]
    public void ACompressedFormatKeepsEachLevelsSize()
    {
        var original = Texture(VTexFormat.BC7, 16, 16, 3, level => new byte[Math.Max(1, (16 >> level) / 4) * Math.Max(1, (16 >> level) / 4) * 16]);
        var pixels = Enumerable.Range(0, 16 * 16).SelectMany(i => new byte[] { (byte)i, 100, 50, 255 }).ToArray();

        var written = InPlaceTexture.Write(original, "materials/test/bc7.vtex_c", 16, 16, pixels);

        Assert.Equal(original.Length, written.Length);
        using var resource = new Resource { FileName = "materials/test/bc7.vtex_c" };
        resource.Read(new MemoryStream(written));
        using var bitmap = ((ValveResourceFormat.ResourceTypes.Texture)resource.DataBlock!).GenerateBitmap();
        Assert.InRange(bitmap.GetPixel(5, 0).Red, 0, 15);                  // decodes back close to the input gradient
        Assert.InRange(bitmap.GetPixel(5, 0).Green, 90, 110);
    }

    [Fact]
    public void AnUnsupportedFormatOrAWrongSizeIsRefused()
    {
        var hdr = Texture(VTexFormat.RGBA16161616F, 4, 4, 1, _ => new byte[4 * 4 * 8]);
        var plain = Texture(VTexFormat.RGBA8888, 4, 4, 1, _ => new byte[4 * 4 * 4]);

        Assert.Equal("RECOLOR_TEXTURE_FORMAT", Assert.Throws<S2ModKitException>(() => InPlaceTexture.Write(hdr, "t.vtex_c", 4, 4, new byte[64])).Error.Code);
        Assert.Equal("RECOLOR_PIXELS_SIZE", Assert.Throws<S2ModKitException>(() => InPlaceTexture.Write(plain, "t.vtex_c", 8, 8, new byte[256])).Error.Code);
    }
}
