using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;

namespace S2ModKit.Application.Tests;

public sealed class ModelPreviewTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"s2modkit-model-preview-{Guid.NewGuid():N}");
    private readonly string archive;

    public ModelPreviewTests()
    {
        Directory.CreateDirectory(root);
        archive = Path.Combine(root, "pak01_dir.vpk");
        File.WriteAllBytes(archive, [1, 2, 3]);
    }

    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void RendersAnimatedViewsSheetAndRecord()
    {
        var renderer = new FakeRenderer(frames: 6);
        var output = Path.Combine(root, "out");
        var result = new ModelPreviewApplication(renderer, new PngPreviewImageEncoder()).Render(Request(output) with { SheetRows = 3 }, TestContext.Current.CancellationToken);

        Assert.Equal(["front.png", "side.png", "sheet.png", "preview.json"], result.Files);
        Assert.Equal(6, result.Frames);
        var front = File.ReadAllBytes(Path.Combine(output, "front.png"));
        Assert.Equal((6, 16, 16), ReadPng(front).AnimatedFrames);
        var sheet = ReadPng(File.ReadAllBytes(Path.Combine(output, "sheet.png")));
        Assert.Equal((32, 48), (sheet.Width, sheet.Height));
        Assert.Equal(Pixel(1, 0, 2), sheet.Pixel(16, 16));            // second column = view 1, second row = frame 2 (of 6 in 3 rows)
        using var record = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "preview.json")));
        Assert.Equal("model_preview@1", record.RootElement.GetProperty("operation").GetString());
        Assert.Equal(ContentHash.Compute(File.ReadAllBytes(archive)).Value, record.RootElement.GetProperty("archives")[0].GetProperty("sha256").GetString());
        Assert.Empty(Directory.EnumerateDirectories(root, ".preview-*"));
    }

    [Fact]
    public void RefusesExistingOutputAndBadRequestsBeforeRendering()
    {
        var renderer = new FakeRenderer(frames: 2);
        var app = new ModelPreviewApplication(renderer, new PngPreviewImageEncoder());
        var existing = Path.Combine(root, "existing");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "keep.txt"), "owner");
        string Fail(ModelPreviewRequest request) => Assert.Throws<S2ModKitException>(() => app.Render(request, TestContext.Current.CancellationToken)).Error.Code;

        Assert.Equal("PREVIEW_OUTPUT_EXISTS", Fail(Request(existing)));
        Assert.Equal("PREVIEW_ARCHIVE_MISSING", Fail(Request(Path.Combine(root, "a")) with { Archives = [Path.Combine(root, "none.vpk")] }));
        Assert.Equal("PREVIEW_VIEWS_INVALID", Fail(Request(Path.Combine(root, "b")) with { Views = [new("sheet", 0)] }));
        Assert.Equal("PREVIEW_VIEWS_INVALID", Fail(Request(Path.Combine(root, "c")) with { Views = [new("a", 0), new("A", 90)] }));
        Assert.Equal("PREVIEW_VIEWS_INVALID", Fail(Request(Path.Combine(root, "d")) with { Views = [new("a", 0, Zoom: 50)] }));
        Assert.Equal("PREVIEW_SIZE_INVALID", Fail(Request(Path.Combine(root, "e")) with { Width = 4 }));
        Assert.Equal("PREVIEW_FRAMES_INVALID", Fail(Request(Path.Combine(root, "f")) with { Frames = 601 }));
        Assert.Equal("PREVIEW_OUTLINE_INVALID", Fail(Request(Path.Combine(root, "g")) with { OutlineThickness = -1 }));

        Assert.Equal(0, renderer.Calls);
        Assert.Equal(["keep.txt"], Directory.EnumerateFiles(existing).Select(Path.GetFileName));
    }

    [Fact]
    public void RefusesAMismatchedRenderWithoutLeavingOutput()
    {
        var output = Path.Combine(root, "out");
        var app = new ModelPreviewApplication(new FakeRenderer(frames: 3, dropLast: true), new PngPreviewImageEncoder());

        Assert.Equal("PREVIEW_FRAME_COUNT", Assert.Throws<S2ModKitException>(() => app.Render(Request(output), TestContext.Current.CancellationToken)).Error.Code);
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateDirectories(root, ".preview-*"));
    }

    [Fact]
    public void StillModelWritesPlainPng()
    {
        var output = Path.Combine(root, "still");
        new ModelPreviewApplication(new FakeRenderer(frames: 1), new PngPreviewImageEncoder()).Render(Request(output) with { ClipPath = null }, TestContext.Current.CancellationToken);

        var png = ReadPng(File.ReadAllBytes(Path.Combine(output, "front.png")));
        Assert.Null(png.AnimatedFrames);
        Assert.Equal(Pixel(0, 1, 0), png.Pixel(1, 1));
    }

    private ModelPreviewRequest Request(string output) => new(
        [archive], "models/test/test.vmdl", "models/test/clips/run.vnmclip", [new("front", 0), new("side", 90)],
        Frames: 0, FramesPerSecond: 2, Width: 16, Height: 16, EnvironmentMap: null, OutputDirectory: output);

    private static (byte R, byte G, byte B, byte A) Pixel(int view, int x, int frame) => ((byte)(view * 100), (byte)(frame * 30), (byte)(x * 10), 255);

    private sealed class FakeRenderer(int frames, bool dropLast = false) : IModelPreviewRenderer
    {
        public int Calls { get; private set; }

        public RenderedPreview Render(PreviewRenderSpec spec, CancellationToken cancellationToken = default)
        {
            Calls++;
            var images = spec.Views.Select((_, v) => (IReadOnlyList<PreviewImage>)Enumerable.Range(0, frames - (dropLast && v == 1 ? 1 : 0))
                .Select(f => Image(spec.Width, spec.Height, v, f)).ToArray()).ToArray();
            return new RenderedPreview(images, Enumerable.Range(0, frames).Select(f => f * 0.5f).ToArray(), frames * 0.5f, "fake", []);
        }

        private static PreviewImage Image(int width, int height, int view, int frame)
        {
            var rgba = new byte[width * height * 4];
            for (var i = 0; i < width * height; i++)
            {
                var p = Pixel(view, i % width, frame);
                (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = p;
            }

            return new PreviewImage(width, height, rgba);
        }
    }

    /// <summary>Minimal PNG reader for the encoder's output (8-bit RGBA, Up filter): checks CRCs and decodes the first frame.</summary>
    private sealed record DecodedPng(int Width, int Height, byte[] Rgba, (int Frames, int Width, int Height)? AnimatedFrames)
    {
        public (byte, byte, byte, byte) Pixel(int x, int y)
        {
            var i = (y * Width + x) * 4;
            return (Rgba[i], Rgba[i + 1], Rgba[i + 2], Rgba[i + 3]);
        }
    }

    private static DecodedPng ReadPng(byte[] png)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        int width = 0, height = 0, fctl = 0;
        int? actl = null;
        using var idat = new MemoryStream();
        for (var at = 8; at < png.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, length);
            Assert.Equal(Crc(png.AsSpan(at + 4, length + 4)), BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length)));
            switch (type)
            {
                case "IHDR": (width, height) = ((int)BinaryPrimitives.ReadUInt32BigEndian(data), (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..])); break;
                case "acTL": actl = (int)BinaryPrimitives.ReadUInt32BigEndian(data); break;
                case "fcTL": fctl++; break;
                case "IDAT": idat.Write(data); break;
            }

            at += 12 + length;
        }

        using var zlib = new ZLibStream(new MemoryStream(idat.ToArray()), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var bytes = raw.ToArray();
        var row = width * 4;
        var rgba = new byte[row * height];
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(2, bytes[y * (row + 1)]);
            for (var x = 0; x < row; x++)
            {
                rgba[y * row + x] = (byte)(bytes[y * (row + 1) + 1 + x] + (y == 0 ? 0 : rgba[(y - 1) * row + x]));
            }
        }

        if (actl is not null)
        {
            Assert.Equal(actl, fctl);
        }

        return new DecodedPng(width, height, rgba, actl is null ? null : (actl.Value, width, height));
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }
}
