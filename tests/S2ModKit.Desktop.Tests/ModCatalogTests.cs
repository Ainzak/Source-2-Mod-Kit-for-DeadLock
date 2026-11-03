using S2ModKit.Adapters.Vpk;
using S2ModKit.Desktop;
using S2ModKit.Domain;

namespace S2ModKit.Desktop.Tests;

public sealed class ModCatalogTests
{
    private const string Model = "models/heroes_wip/wraith/wraith.vmdl_c";
    private const string Texture = "models/heroes_wip/wraith/materials/body_color_png_1.vtex_c";
    private const string Sound = "sounds/vo/wraith/hello.vsnd_c";

    [Fact]
    public async Task InstallDisableAndEnableTouchOnlyTheAppsOwnFile()
    {
        using var root = new TestRoot();
        var token = TestContext.Current.CancellationToken;
        var other = Path.Combine(root.Addons, "pak03_dir.vpk");
        await WriteVpkAsync(other, token, Sound);
        var before = await File.ReadAllBytesAsync(other, token);
        var package = Path.Combine(root.Library, "skin_dir.vpk");
        await WriteVpkAsync(package, token, Model, Texture);
        var catalog = root.Catalog();

        await catalog.InstallAsync(package, "Wraith skin", token);
        var installed = Assert.Single(await catalog.LoadAsync(token), mod => mod.Name == "Wraith skin");
        Assert.Equal(ModSource.App, installed.Source);
        Assert.True(installed.Loaded);
        Assert.Equal(["models/heroes_wip/wraith/wraith.vmdl"], installed.Models);
        var installedFile = Assert.Single(installed.Files);
        Assert.Matches(@"pak9\d_dir\.vpk$", installedFile);

        await catalog.DisableAsync(installed.Install!, token);
        Assert.False(File.Exists(installedFile));
        var disabled = Assert.Single(await catalog.LoadAsync(token), mod => mod.Name == "Wraith skin");
        Assert.False(disabled.Loaded);
        Assert.Equal([package], disabled.Files);                           // previewed from its package while disabled

        await catalog.EnableAsync(disabled.Install!, token);
        var mods = await catalog.LoadAsync(token);
        Assert.True(Assert.Single(mods, mod => mod.Name == "Wraith skin").Loaded);
        Assert.Equal(before, await File.ReadAllBytesAsync(other, token));   // another mod is never touched
        Assert.Contains(mods, mod => mod.Source == ModSource.Other && mod.KindText.Contains("sound", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TextureOnlyModShowsOnTheGamesHeroModel()
    {
        using var root = new TestRoot();
        var token = TestContext.Current.CancellationToken;
        await WriteVpkAsync(Path.Combine(root.Addons, "pak05_dir.vpk"), token, Texture);

        var mod = Assert.Single(await root.Catalog().LoadAsync(token));

        Assert.Equal(["models/heroes_wip/wraith/wraith.vmdl"], mod.Models);
        Assert.Equal(ModSource.Other, mod.Source);
    }

    [Fact]
    public async Task InstallIsRefusedWhenALoadedModShipsTheSameFile()
    {
        using var root = new TestRoot();
        var token = TestContext.Current.CancellationToken;
        await WriteVpkAsync(Path.Combine(root.Addons, "pak03_dir.vpk"), token, Model);
        var package = Path.Combine(root.Library, "skin_dir.vpk");
        await WriteVpkAsync(package, token, Model);

        await Assert.ThrowsAsync<S2ModKitException>(() => root.Catalog().InstallAsync(package, "Wraith skin", token));

        Assert.Equal(["pak03_dir.vpk"], Directory.EnumerateFiles(root.Addons, "*.vpk").Select(Path.GetFileName));
    }

    [Fact]
    public async Task AModWithAPropModelOpensOnItsHerosMainModel()
    {
        using var root = new TestRoot();
        var token = TestContext.Current.CancellationToken;
        await WriteVpkAsync(Path.Combine(root.Addons, "pak05_dir.vpk"), token, "models/heroes_wip/doorman/doorman_bell.vmdl_c", "models/abilities/doorman_key_fx_model.vmdl_c");
        await WriteVpkAsync(Path.Combine(root.Addons, "pak06_dir.vpk"), token, "models/heroes_staging/hornet_v3/materials/body_color.vtex_c");

        var mods = await root.Catalog().LoadAsync(token);

        var doorman = Assert.Single(mods, mod => mod.Files[0].EndsWith("pak05_dir.vpk", StringComparison.Ordinal));
        Assert.Equal(["Doorman"], doorman.HeroNames);
        Assert.Equal("models/heroes_wip/doorman_v2/doorman.vmdl", doorman.Models[0]);  // not the prop or the ability model
        Assert.Contains("models/heroes_wip/doorman/doorman_bell.vmdl", doorman.Models);
        var vindicta = Assert.Single(mods, mod => mod.Files[0].EndsWith("pak06_dir.vpk", StringComparison.Ordinal));
        Assert.Equal(["Vindicta"], vindicta.HeroNames);                     // found through her alias folder (hornet_v3)
        Assert.Equal("models/heroes_staging/hornet_v3/hornet.vmdl", vindicta.Models[0]);
    }

    [Theory]
    [InlineData(new[] { "run_fwd", "crouch_idle", "out_of_combat_stand_idle", "stand_idle_alt" }, "out_of_combat_stand_idle")]
    [InlineData(new[] { "run_fwd", "crouch_idle" }, "crouch_idle")]
    [InlineData(new[] { "run_fwd" }, "run_fwd")]
    public void DefaultClipPrefersAStandingIdle(string[] animations, string expected) =>
        Assert.Equal(expected, PreviewControl.DefaultClip(animations));

    private static async Task WriteVpkAsync(string path, CancellationToken token, params string[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await CompactVpkArchiveIO.WriteAsync(path, entries.ToDictionary(entry => entry, entry => (ReadOnlyMemory<byte>)System.Text.Encoding.UTF8.GetBytes(entry)), token);
    }

    /// <summary>A synthetic game folder (citadel/pak01_dir.vpk with Wraith's model, an addons folder) and a library.</summary>
    private sealed class TestRoot : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "s2modkit-desktop-tests", Guid.NewGuid().ToString("N"));

        public TestRoot()
        {
            Directory.CreateDirectory(Addons);
            Directory.CreateDirectory(Library);
            CompactVpkArchiveIO.WriteAsync(Path.Combine(Game, "citadel", "pak01_dir.vpk"),
                new Dictionary<string, ReadOnlyMemory<byte>> { [Model] = "model"u8.ToArray() }, CancellationToken.None).GetAwaiter().GetResult();
        }

        public string Game => Path.Combine(root, "game");

        public string Addons => Path.Combine(Game, "citadel", "addons");

        public string Library => Path.Combine(root, "library");

        public ModCatalog Catalog() => new(new AppSettings(Game, Path.Combine(root, "data")), Path.Combine(root, "no-manager-state.json"));

        public void Dispose() => Directory.Delete(root, true);
    }
}
