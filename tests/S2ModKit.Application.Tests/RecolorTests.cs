using System.Text.Json;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed class RecolorTests
{
    private const string Texture = "models/heroes_wip/wraith/materials/body_color_png_1.vtex";

    [Fact]
    public void ColorizeTakesTheTargetsHueAndKeepsBrightness()
    {
        var (r, g, b) = RecolorMath.Change(0.4f, 0.4f, 0.4f, new ColorAdjustment(Colorize: RgbColor.Parse("#ff0000")));
        Assert.Equal(0.4f, MathF.Max(r, MathF.Max(g, b)), 3);                // brightness (value) kept
        Assert.True(r > g && g == b);                                        // now red
    }

    [Fact]
    public void HueShiftOfHalfATurnInvertsTheHue()
    {
        var (r, g, b) = RecolorMath.Change(1f, 0f, 0f, new ColorAdjustment(HueShiftDegrees: 180));
        Assert.Equal((0f, 1f, 1f), (MathF.Round(r, 3), MathF.Round(g, 3), MathF.Round(b, 3)));
    }

    [Fact]
    public void MaskWeightsBlendAndAlphaIsUntouched()
    {
        byte[] rgba = [200, 100, 50, 7, 200, 100, 50, 9];
        var changed = RecolorMath.Apply(rgba, [0f, 1f], new ColorAdjustment(Brightness: 0));

        Assert.Equal(1, changed);
        Assert.Equal([200, 100, 50, 7, 0, 0, 0, 9], rgba);
    }

    [Fact]
    public async Task RecolorPackagesTheCompiledTextureAtItsPathWithEvidence()
    {
        using var root = new TempRoot();
        var workshop = new FakeWorkshop(2, 1, mips: 2);
        var writer = new MemoryWriter();
        var mask = root.File("mask.png", [1]);
        var application = new RecolorApplication(workshop, writer, new FixedClock());

        var result = await application.RunAsync(new RecolorRequest(["mod_dir.vpk", "pak01_dir.vpk"],
            [new RecolorPart("suit", Texture, mask, new ColorAdjustment(Colorize: RgbColor.Parse("#0000ff")))], root.Path("out")), TestContext.Current.CancellationToken);

        Assert.Equal([Texture + "_c"], result.Textures);
        Assert.Equal([Texture + "_c"], writer.Entries!.Keys);
        Assert.Equal(1, Assert.Single(result.Parts).Texels);                  // the mask covers the first texel only
        Assert.Equal([0, 0, 255, 255, 255, 0, 0, 255], workshop.Compiled);   // red -> blue there, the second texel untouched
        using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.OutputDirectory, "recolor.json")));
        Assert.Equal("recolor@1", record.RootElement.GetProperty("operation").GetString());
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory, "body_color_png_1.vtex.png")));
    }

    [Fact]
    public async Task ACompiledTextureOfAnotherSizeOrFormatIsRefused()
    {
        using var root = new TempRoot();
        var workshop = new FakeWorkshop(2, 1, mips: 2) { CompiledFacts = new TextureFacts(2, 1, "DXT1", 2) };
        var application = new RecolorApplication(workshop, new MemoryWriter(), new FixedClock());

        var error = await Assert.ThrowsAsync<S2ModKitException>(() => application.RunAsync(new RecolorRequest(["pak01_dir.vpk"],
            [new RecolorPart("all", Texture, null, new ColorAdjustment(Saturation: 0))], root.Path("out")), TestContext.Current.CancellationToken));

        Assert.Equal("RECOLOR_TEXTURE_CHANGED_LAYOUT", error.Error.Code);
    }

    [Theory]
    [InlineData("models/x/body.vmat", 1f, "RECOLOR_TEXTURE_PATH")]
    [InlineData(Texture, 5f, "RECOLOR_CHANGE_RANGE")]
    public async Task InvalidPartsAreRefusedBeforeAnyWork(string texture, float saturation, string code)
    {
        using var root = new TempRoot();
        var workshop = new FakeWorkshop(1, 1, mips: 1);
        var application = new RecolorApplication(workshop, new MemoryWriter(), new FixedClock());

        var error = await Assert.ThrowsAsync<S2ModKitException>(() => application.RunAsync(new RecolorRequest(["pak01_dir.vpk"],
            [new RecolorPart("x", texture, null, new ColorAdjustment(Saturation: saturation))], root.Path("out")), TestContext.Current.CancellationToken));

        Assert.Equal(code, error.Error.Code);
        Assert.False(Directory.Exists(root.Path("out")));
    }

    [Fact]
    public void ColoursParseAndPrintAsHex()
    {
        Assert.Equal("#8a1c2b", RgbColor.Parse("#8A1C2B").ToString());
        Assert.Equal("RECOLOR_COLOR_INVALID", Assert.Throws<S2ModKitException>(() => RgbColor.Parse("red")).Error.Code);
    }

    private sealed class FakeWorkshop(int width, int height, int mips) : ITextureWorkshop
    {
        public byte[]? Compiled { get; private set; }

        public TextureFacts? CompiledFacts { get; init; }

        public Task<DecodedTexture> DecodeAsync(IReadOnlyList<string> archives, string texturePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DecodedTexture(texturePath, width, height, [.. Enumerable.Repeat<byte[]>([255, 0, 0, 255], width * height).SelectMany(p => p)],
                "definition", "BC7", mips, archives[0], ContentHash.Compute([1, 2, 3])));

        public Task<float[]> ReadMaskAsync(string path, int w, int h, CancellationToken cancellationToken = default) =>
            Task.FromResult(Enumerable.Range(0, w * h).Select(i => i == 0 ? 1f : 0f).ToArray());

        public Task<byte[]> CompileAsync(DecodedTexture texture, byte[] rgba, string workDirectory, CancellationToken cancellationToken = default)
        {
            Compiled = rgba;
            return Task.FromResult<byte[]>([9, 9, 9]);
        }

        public TextureFacts Inspect(byte[] compiled) => CompiledFacts ?? new TextureFacts(width, height, "BC7", mips);

        public byte[] EncodePng(int w, int h, byte[] rgba) => [0x89];
    }

    private sealed class MemoryWriter : IAddonArchiveWriter
    {
        public IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? Entries { get; private set; }

        public Task WriteNewAsync(string outputPath, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> entries, CancellationToken cancellationToken = default)
        {
            Entries = entries;
            File.WriteAllBytes(outputPath, [1]);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class TempRoot : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "s2modkit-recolor-tests", Guid.NewGuid().ToString("N"));

        public TempRoot() => Directory.CreateDirectory(root);

        public string Path(string name) => System.IO.Path.Combine(root, name);

        public string File(string name, byte[] bytes)
        {
            var path = Path(name);
            System.IO.File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose() => Directory.Delete(root, true);
    }
}
