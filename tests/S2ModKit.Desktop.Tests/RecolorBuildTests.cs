using S2ModKit.Application;
using S2ModKit.Desktop;
using SteamDatabase.ValvePak;

namespace S2ModKit.Desktop.Tests;

public sealed class RecolorBuildTests
{
    [Fact]
    public async Task ABuildWithoutRecoloredPartsOrWhileTheGameRunsIsRefused()
    {
        using var root = new ModTestRoot();
        var catalog = root.Catalog();
        var change = new ColorAdjustment(HueShiftDegrees: 90);

        await Assert.ThrowsAnyAsync<Exception>(() => catalog.BuildRecoloredAsync(null, "models/x/x.vmdl", [], TestContext.Current.CancellationToken));
        root.GameProbe.Running = true;
        await Assert.ThrowsAnyAsync<Exception>(() => catalog.BuildRecoloredAsync(null, "models/x/x.vmdl", [new PartRecolor(0, "materials/x.vtex", [1f], 1, 1, change)], TestContext.Current.CancellationToken));
        Assert.Empty(root.AddonNames());
    }

    /// <summary>Local check with a real compiled texture (S2MODKIT_TEST_TEXTURE_VPK: a VPK; S2MODKIT_TEST_TEXTURE: its texture path without _c).</summary>
    [Fact]
    public async Task ARecoloredModIsInstalledAsAVersionWithTheTextureRewrittenInPlace()
    {
        var (vpk, texture) = (Environment.GetEnvironmentVariable("S2MODKIT_TEST_TEXTURE_VPK"), Environment.GetEnvironmentVariable("S2MODKIT_TEST_TEXTURE"));
        if (string.IsNullOrWhiteSpace(vpk) || string.IsNullOrWhiteSpace(texture))
        {
            Assert.Skip("Set S2MODKIT_TEST_TEXTURE_VPK and S2MODKIT_TEST_TEXTURE to enable this proprietary local check.");
        }

        using var root = new ModTestRoot();
        byte[] original;
        using (var source = new Package())
        {
            source.Read(vpk);
            source.ReadEntry(source.FindEntry(texture + "_c")!, out original);
        }

        await S2ModKit.Adapters.Vpk.CompactVpkArchiveIO.WriteAsync(root.In("pak05_dir.vpk"), new Dictionary<string, ReadOnlyMemory<byte>> { [texture + "_c"] = original, ["materials/other.txt"] = "x"u8.ToArray() }, CancellationToken.None);
        var catalog = root.Catalog();
        var mod = Assert.Single(await catalog.LoadAsync(TestContext.Current.CancellationToken));
        var decoded = await new S2ModKit.Adapters.Source2.VrfTextureWorkshop().DecodeAsync([mod.Files[0]], texture, TestContext.Current.CancellationToken);
        var mask = Enumerable.Repeat(1f, decoded.Width * decoded.Height).ToArray();

        await catalog.BuildRecoloredAsync(mod, "models/x/x.vmdl", [new PartRecolor(0, texture, mask, decoded.Width, decoded.Height, new ColorAdjustment(Colorize: new RgbColor(0.8f, 0.1f, 0.1f)))], TestContext.Current.CancellationToken);

        var mods = await catalog.LoadAsync(TestContext.Current.CancellationToken);
        var recolored = Assert.Single(mods, entry => entry.Loaded);
        Assert.Contains("materials/other.txt", recolored.Entries);           // the whole mod, not only the texture
        using var package = new Package();
        package.Read(recolored.Files[0]);
        package.ReadEntry(package.FindEntry(texture + "_c")!, out var written);
        Assert.Equal(original.Length, written.Length);                     // in place: same size and layout
        Assert.NotEqual(original, written);
        Assert.Equal(["original", "recolored: 1 part"], recolored.VersionList.Select(version => version.Label).Order(StringComparer.Ordinal));
    }
}
