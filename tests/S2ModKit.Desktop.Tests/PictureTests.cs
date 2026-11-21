using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using SteamDatabase.ValvePak;

namespace S2ModKit.Desktop.Tests;

public sealed class PictureTests : IDisposable
{
    /// <summary>A 4x2 GIF of two frames: red for 50 ms, then blue for 150 ms.</summary>
    private const string TwoFrameGif = "R0lGODlhBAACAIEAAP8AAAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQABQAAACwAAAAABAACAAAIBwABCBwoMCAAIfkEAQ8AAQAsAAAAAAQAAgCBAAD/AAAAAAAAAAAACAcAAQgcKDAgADs=";
    private readonly string folder = Path.Combine(Path.GetTempPath(), "s2mk-pictures-" + Guid.NewGuid().ToString("N"));

    public PictureTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public void AGifIsReadFrameByFrameWithItsDelays()
    {
        var path = Path.Combine(folder, "two.gif");
        File.WriteAllBytes(path, Convert.FromBase64String(TwoFrameGif));

        var frames = PictureFrames.Read(path);

        Assert.Equal(2, frames.Count);
        Assert.Equal([50, 150], frames.Select(frame => frame.DelayMs));
        Assert.Equal((4, 2), (frames[0].Image.Width, frames[0].Image.Height));
        Assert.Equal([255, 0, 0, 255], frames[0].Image.Rgba[..4]);
        Assert.Equal([0, 0, 255, 255], frames[1].Image.Rgba[..4]);
        Assert.Throws<InvalidDataException>(() => PictureFrames.Read(WriteText("not-a-picture.gif", "hello")));
    }

    /// <summary>The longest animation sets the loop and the frame rate; a shorter one plays a whole number of times in it.</summary>
    [Fact]
    public void AnimationsShareOneLoop()
    {
        var main = Animation(50, 50, 255);                                  // 2.5 s at 20 frames a second
        var short9 = Animation(9, 78, 10);                                  // 0.7 s

        var composed = PictureComposer.Compose([new PictureLayer(main), new PictureLayer(short9)], 64, 96, PictureFit.Whole, 256);

        Assert.Equal(50, composed.Frames.Count);
        Assert.Equal(20f, composed.FramesPerSecond, 3);
        Assert.Equal(2.5f, composed.LoopSeconds, 3);
        Assert.Equal(1, PictureComposer.FrameCount([new PictureLayer([Still(255)])], 256));
        Assert.Equal(16, PictureComposer.FrameCount([new PictureLayer(main)], 16));                 // thinned out
        Assert.Equal(5f, PictureComposer.Compose([new PictureLayer(main)], 8, 8, PictureFit.Whole, 256, speed: 2f).FramesPerSecond * 0.125f, 3);
    }

    /// <summary>Whole: two landscape pictures stacked across the canvas's width, centred, dark around them; Fill: no dark left.</summary>
    [Fact]
    public void LayersAreStackedOrCovered()
    {
        var wide = new PictureLayer([new PictureFrame(Solid(40, 20, 200), 0)]);
        var whole = PictureComposer.Compose([wide, wide], 40, 80, PictureFit.Whole, 1).Frames[0];
        var fill = PictureComposer.Compose([wide], 40, 80, PictureFit.Fill, 1).Frames[0];

        Assert.Equal(PictureComposer.Background[..3], Pixel(whole, 20, 2)[..3]);                   // the bar above
        Assert.Equal(200, Pixel(whole, 20, 30)[0]);                                                 // the first picture
        Assert.Equal(200, Pixel(whole, 20, 50)[0]);                                                 // the second one
        foreach (var y in (int[])[0, 40, 79])
        {
            Assert.Equal(200, Pixel(fill, 20, y)[0]);
        }

        var darker = PictureComposer.Compose([wide with { Brightness = 0.5f }], 40, 80, PictureFit.Fill, 1).Frames[0];
        Assert.Equal(100, Pixel(darker, 20, 40)[0]);
    }

    /// <summary>As few cells as hold the frames, powers of two, the cell shape nearest the canvas; frames row by row.</summary>
    [Fact]
    public void FramesGoInTheSmallestGrid()
    {
        Assert.Equal(new PictureGrid(16, 4, 256, 512), PictureComposer.Grid(50, 4096, 2048, 0.67f));
        Assert.Equal(new PictureGrid(2, 1, 2048, 2048), PictureComposer.Grid(2, 4096, 2048, 0.67f));
        Assert.Throws<S2ModKit.Domain.S2ModKitException>(() => PictureComposer.Grid(5000, 64, 64, 1f));

        var atlas = PictureComposer.Atlas([Solid(8, 8, 10), Solid(8, 8, 20), Solid(8, 8, 30)], 32, 16, new PictureGrid(2, 2, 16, 8));
        Assert.Equal(10, Pixel(atlas, 4, 4)[0]);
        Assert.Equal(20, Pixel(atlas, 20, 4)[0]);
        Assert.Equal(30, Pixel(atlas, 4, 12)[0]);
        Assert.Equal(PictureComposer.Background[0], Pixel(atlas, 20, 12)[0]);                       // an unused cell
    }

    [Fact]
    public void TheShippedTargetsAreComplete()
    {
        var targets = PictureTargets.Load(Path.Combine(AppContext.BaseDirectory, "catalogues", "pictures.json"));

        var portrait = Assert.Single(targets.Paintings, painting => painting.Id == "hideout-portrait");
        Assert.Equal(1.03125f, portrait.UvRange[2]);
        Assert.Equal(117.8f / 175.9f, portrait.Aspect, 3);
        Assert.Equal(4096, targets.TemplateFor(50)!.Width);
        Assert.Equal(2048, targets.TemplateFor(50)!.Height);
        Assert.Equal(4096, targets.TemplateFor(200)!.Height);
        Assert.Null(targets.TemplateFor(5000));
        var files = targets.HeroFiles(["panorama/images/heroes/astro_card_psd.vtex_c", "panorama/images/heroes/astro_sm_psd_e2407af5.vtex_c",
            "panorama/images/heroes/astro2_card_psd.vtex_c", "panorama/images/heroes/backgrounds/astro_bg_psd.vtex_c"], ["astro"]);
        Assert.Equal(["card", "small", "background"], files.Select(file => file.Kind.Id));
    }

    [Fact]
    public void AbramsPicturesUseBothTheLegacyAndCurrentPrefixes()
    {
        var abrams = Assert.Single(Hero.Shipped, hero => hero.Name == "Abrams");
        var files = PictureTargets.Shipped.HeroFiles([
            "panorama/images/heroes/bull_card_psd.vtex_c",
            "panorama/images/heroes/bull_vertical_psd.vtex_c",
            "panorama/images/heroes/backgrounds/abrams_bg_psd.vtex_c",
            "panorama/images/heroes/bullfrog_card_psd.vtex_c",
        ], abrams.Folders);

        Assert.Equal(["card", "vertical", "background"], files.Select(file => file.Kind.Id));
    }

    /// <summary>Live check (S2MODKIT_TEST_GAME): an animation on the hideout portrait goes into a copy of the template texture as a grid of
    /// frames, the material gets the fitted flipbook and the glow from the picture; a still picture goes into the portrait's own texture;
    /// a hero's chosen pictures are written at their sizes. Every build leaves its package, recipe and record.</summary>
    [Fact]
    public async Task PicturesAreBuiltForAPaintingAndAHero()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this proprietary local check.");
            return;
        }

        var builder = new PictureModBuilder(new AppSettings(game, folder), PictureTargets.Shipped);
        var png = Path.Combine(folder, "still.png");
        File.WriteAllBytes(png, new S2ModKit.Infrastructure.PngPreviewImageEncoder().EncodePng(Solid(64, 48, 180)));
        var request = new PictureRequest("Test painting", "hideout-portrait", null, [new PictureSource(png)], PictureFit.Whole, 0.5f, 1f, []);

        var animated = await builder.BuildAsync(request, [new PictureLayer(Animation(12, 100, 200))], TestContext.Current.CancellationToken);
        var still = await builder.BuildAsync(request with { Name = "Test still" }, TestContext.Current.CancellationToken);
        var hero = await builder.BuildAsync(request with { Name = "Test hero", PaintingId = null, HeroName = "Holliday", HeroKinds = ["card", "small"] }, TestContext.Current.CancellationToken);
        var abrams = await builder.BuildAsync(request with
        {
            Name = "Test Abrams",
            PaintingId = null,
            HeroName = "Abrams",
            HeroKinds = [.. PictureTargets.Shipped.HeroPictures.Select(kind => kind.Id)],
        }, TestContext.Current.CancellationToken);

        var portrait = PictureTargets.Shipped.Paintings[0];
        Assert.Contains(portrait.FramesTexture, animated.Written);
        var material = ReadMaterial(animated.Package, portrait.Material);
        Assert.Equal(portrait.FramesTexture[..^2], material.TextureParams["g_tColor"]);
        Assert.Equal(PictureTargets.Shipped.WhiteMask, material.TextureParams["g_tSelfIllumMask"]);
        Assert.Equal(0.5f, material.FloatParams["g_flSelfIllumScale1"]);
        Assert.Contains(material.Data["m_dynamicParams"].Values, item => item["m_name"].ToString(System.Globalization.CultureInfo.InvariantCulture) == "g_vAlbedoTexcoordOffset1");
        var frames = InPlaceTexture.ReadStored(ReadEntry(animated.Package, portrait.FramesTexture), portrait.FramesTexture);
        Assert.Equal((4096, 2048), (frames.Width, frames.Height));

        Assert.Contains(portrait.ColorTexture, still.Written);
        Assert.DoesNotContain(portrait.FramesTexture, still.Written);
        Assert.DoesNotContain(ReadMaterial(still.Package, portrait.Material).Data["m_dynamicParams"].Values, item => item["m_name"].ToString(System.Globalization.CultureInfo.InvariantCulture) == "g_vAlbedoTexcoordOffset1");

        Assert.Equal(["panorama/images/heroes/astro_card_psd.vtex_c", "panorama/images/heroes/astro_sm_psd.vtex_c", "panorama/images/heroes/astro_sm_psd_e2407af5.vtex_c"],
            hero.Written);                                                                          // both small icons the game has
        Assert.Equal(8, abrams.Written.Count);
        Assert.Contains("panorama/images/heroes/backgrounds/abrams_bg_psd.vtex_c", abrams.Written);
        Assert.Contains("panorama/images/heroes/bull_vertical_psd.vtex_c", abrams.Written);
        Assert.Contains("panorama/images/heroes/bull_card_psd.vtex_c", abrams.Written);
        Assert.All(new[] { animated, still, hero, abrams }, build =>
        {
            var record = Path.GetDirectoryName(build.Package)!;
            Assert.True(File.Exists(Path.Combine(record, "recipe.json")) && File.Exists(Path.Combine(record, "edit.json")) && File.Exists(build.Picture));
        });
    }

    private string WriteText(string name, string text)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static PictureFrame[] Animation(int count, int delay, byte red) => [.. Enumerable.Range(0, count).Select(_ => new PictureFrame(Solid(8, 6, red), delay))];

    private static PictureFrame Still(byte red) => new(Solid(8, 6, red), 0);

    private static PreviewImage Solid(int width, int height, byte red)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (red, 30, 60, 255);
        }

        return new PreviewImage(width, height, pixels);
    }

    private static byte[] Pixel(PreviewImage image, int x, int y) => image.Rgba[(((y * image.Width) + x) * 4)..((((y * image.Width) + x) * 4) + 4)];

    private static byte[] ReadEntry(string vpk, string path)
    {
        using var package = new Package();
        package.Read(vpk);
        package.ReadEntry(package.FindEntry(path)!, out var bytes);
        return bytes;
    }

    private static ValveResourceFormat.ResourceTypes.Material ReadMaterial(string vpk, string path)
    {
        using var resource = new ValveResourceFormat.Resource { FileName = path };
        resource.Read(new MemoryStream(ReadEntry(vpk, path)));
        return (ValveResourceFormat.ResourceTypes.Material)resource.DataBlock!;
    }
}
