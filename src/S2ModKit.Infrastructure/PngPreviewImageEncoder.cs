using System.Buffers.Binary;
using System.IO.Compression;
using S2ModKit.Application;

namespace S2ModKit.Infrastructure;

/// <summary>PNG and animated PNG (APNG, looping) from RGBA8 frames; no external encoder.</summary>
public sealed class PngPreviewImageEncoder : IPreviewImageEncoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public byte[] EncodePng(PreviewImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var stream = new MemoryStream();
        stream.Write(Signature);
        Chunk(stream, "IHDR", Header(image));
        Chunk(stream, "IDAT", Compress(image));
        Chunk(stream, "IEND", []);
        return stream.ToArray();
    }

    public byte[] EncodeAnimatedPng(IReadOnlyList<PreviewImage> frames, int framesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0 || frames.Any(f => f.Width != frames[0].Width || f.Height != frames[0].Height))
        {
            throw new ArgumentException("Frames must be non-empty and the same size.", nameof(frames));
        }

        using var stream = new MemoryStream();
        stream.Write(Signature);
        Chunk(stream, "IHDR", Header(frames[0]));
        var actl = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(actl, (uint)frames.Count);
        Chunk(stream, "acTL", actl);
        uint sequence = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            var fctl = new byte[26];
            BinaryPrimitives.WriteUInt32BigEndian(fctl, sequence++);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(4), (uint)frames[i].Width);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(8), (uint)frames[i].Height);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(22), (ushort)framesPerSecond);
            Chunk(stream, "fcTL", fctl);
            var data = Compress(frames[i]);
            if (i == 0)
            {
                Chunk(stream, "IDAT", data);
                continue;
            }

            var fdat = new byte[data.Length + 4];
            BinaryPrimitives.WriteUInt32BigEndian(fdat, sequence++);
            data.CopyTo(fdat, 4);
            Chunk(stream, "fdAT", fdat);
        }

        Chunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static byte[] Header(PreviewImage image)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)image.Height);
        header[8] = 8;
        header[9] = 6;
        return header;
    }

    /// <summary>Scanlines with the Up filter (good for smooth renders), zlib-compressed.</summary>
    private static byte[] Compress(PreviewImage image)
    {
        var row = image.Width * 4;
        var raw = new byte[(row + 1) * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            var target = y * (row + 1);
            raw[target] = 2;
            for (var x = 0; x < row; x++)
            {
                var above = y == 0 ? 0 : image.Rgba[(y - 1) * row + x];
                raw[target + 1 + x] = (byte)(image.Rgba[y * row + x] - above);
            }
        }

        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return output.ToArray();
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }

        return c;
    }).ToArray();

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static void Chunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)data.Length);
        stream.Write(buffer);
        var name = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(name);
        stream.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, ~Crc(Crc(uint.MaxValue, name), data));
        stream.Write(buffer);
    }
}
