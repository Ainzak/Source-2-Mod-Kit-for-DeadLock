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
    public async Task AFileRemovedOutsideTheAppAndASlotReusedByALaterInstallStillList()
    {
        using var root = new TestRoot();
        var token = TestContext.Current.CancellationToken;
        var first = Path.Combine(root.Library, "first_dir.vpk");
        var second = Path.Combine(root.Library, "second_dir.vpk");
        await WriteVpkAsync(first, token, Model);
        await WriteVpkAsync(second, token, Texture);
        var catalog = root.Catalog();
        var installed = await catalog.InstallAsync(first, "Old mod", token);
        File.Delete(Path.Combine(root.Addons, installed.Receipt!.TargetFileName));   // removed by hand: its receipt still says installed

        var again = await catalog.InstallAsync(second, "New mod", token);
        Assert.Equal(installed.Receipt.TargetFileName, again.Receipt!.TargetFileName);   // the free slot is taken again

        var mods = await catalog.LoadAsync(token);
        var listed = Assert.Single(mods, mod => mod.Files.Any(file => Path.GetFileName(file) == again.Receipt.TargetFileName));
        Assert.Equal("New mod", listed.Name);
    }

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

        var change = await catalog.DisableAsync(installed, token);              // a rename in place: the package copy is not needed
        Assert.False(File.Exists(installedFile));
        var disabledFile = Path.Combine(root.Addons, "off_" + Path.GetFileName(installedFile));
        Assert.True(File.Exists(disabledFile));
        Assert.Equal("file:" + Path.GetFileName(disabledFile), change.SelectKey);
        var disabled = Assert.Single(await catalog.LoadAsync(token), mod => mod.Name == "Wraith skin");
        Assert.False(disabled.Loaded);
        Assert.Equal(ModSource.App, disabled.Source);
        Assert.Equal([disabledFile], disabled.Files);

        await catalog.EnableAsync(disabled, token);
        var mods = await catalog.LoadAsync(token);
        Assert.True(Assert.Single(mods, mod => mod.Name == "Wraith skin").Loaded);
        Assert.Equal([installedFile], Assert.Single(mods, mod => mod.Name == "Wraith skin").Files);
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
    [InlineData("models/heroes_wip/ivy_v2/materials/body.vmat_c", "Ivy", "model")]
    [InlineData("sounds/abilities/bebop/a4_hyper_beam/beam.vsnd_c", "Bebop", "sound")]
    [InlineData("sounds/vo/bookworm/ping.vsnd_c", "Paige", "sound")]
    [InlineData("sounds/heroes/punkgoat/hit.vsnd_c", "Billy", "sound")]
    [InlineData("soundevents/hero/chrono.vsndevts_c", "Paradox", "sound")]
    [InlineData("soundevents/hero/chrono_ult.vsndevts_c", "Paradox", "sound")]
    [InlineData("maps/ui/hero_prefabs/necro.vpk", "Graves", "UI")]
    [InlineData("panorama/images/heroes/bebop_card_psd.vtex_c", "Bebop", "UI")]
    [InlineData("panorama/images/heroes/wraith/portrait.vtex_c", "Wraith", "UI")]
    [InlineData("particles/abilities/tengu/lightning.vpcf_c", "Ivy", "particles")]
    [InlineData("materials/particle/abilities/nano/glow.vmat_c", "Calico", "particles")]
    [InlineData("Sounds\\Abilities\\BEBOP\\Beam.vsnd_c", "Bebop", "sound")]
    public void EachPathRuleNamesTheHeroAndTheKind(string path, string hero, string tag)
    {
        var found = Assert.Single(ModAttribution.HeroKinds([path]));

        Assert.Equal(hero, found.Key.Name);
        Assert.Equal([tag], found.Value);
    }

    [Theory]
    [InlineData("sounds/abilities/bebopish/beam.vsnd_c")]               // not a folder of any hero
    [InlineData("sounds/abilities/bebop_extra/beam.vsnd_c")]
    [InlineData("models/heroes_wip/unknown_hero/x.vmdl_c")]
    [InlineData("models/heroes_wip/bebop.vmdl_c")]                       // a file, not a hero folder
    [InlineData("panorama/images/heroes/bebopish_card.vtex_c")]
    [InlineData("soundevents/hero/bebopish.vsndevts_c")]
    [InlineData("particles/abilities/generic/spark.vpcf_c")]
    [InlineData("maps/ui/hero_prefabs/nobody.vpk")]
    [InlineData("sounds/weapons/bebop/shot.vsnd_c")]                    // no rule for this folder
    public void ABroadOrUnknownNameAttributesNoHero(string path) => Assert.Empty(ModAttribution.HeroKinds([path]));

    [Fact]
    public void AModForSeveralHeroesListsEachWithItsOwnTagsInTagOrder()
    {
        var found = ModAttribution.HeroKinds([
            "particles/abilities/tengu/a.vpcf_c", "sounds/vo/tengu/a.vsnd_c", "models/heroes_wip/ivy/ivy.vmdl_c", "maps/ui/hero_prefabs/tengu.vpk",
            "sounds/abilities/bebop/a.vsnd_c", "sounds/abilities/bebop/b.vsnd_c",
        ]).ToDictionary(pair => pair.Key.Name, pair => pair.Value);

        Assert.Equal(["model", "sound", "UI", "particles"], found["Ivy"]);
        Assert.Equal(["sound"], found["Bebop"]);
        Assert.Equal(2, found.Count);
    }

    [Theory]
    [InlineData("Objects", "models/props_gameplay/soul_container/a.vmdl_c", "models/props_gameplay/soul_container/materials/a.vmat_c")]
    [InlineData("Objects", "models/items/item_crate/crate.vmdl_c", "sounds/physics/wood/hit.vsnd_c")]
    [InlineData("World", "maps/street_test.vpk", "maps/street_test/world.vwrld_c")]
    [InlineData("World", "sounds/ambient/wind.vsnd_c", "soundevents/world_ambience.vsndevts_c")]
    [InlineData("Interface", "panorama/layout/hud.vxml_c", "panorama/styles/hud.vcss_c")]
    [InlineData("Interface", "maps/ui/other.vpk", "resource/localization/citadel_gc_hero_names/x.txt")]
    [InlineData("Other", "scripts/misc.vdata_c", "models/heroes_wip/unknown_hero/x.vmdl_c")]
    public void AModWithoutAHeroGoesToTheGroupMostOfItsEntriesBelongTo(string group, params string[] entries) =>
        Assert.Equal(group, ModAttribution.GroupOf(entries));

    [Fact]
    public async Task TheCatalogListsEachHeroModWithTagsAndOtherModsInGroups()
    {
        using var root = new TestRoot();
        var token = TestContext.Current.CancellationToken;
        await WriteVpkAsync(Path.Combine(root.Addons, "pak05_dir.vpk"), token, "sounds/abilities/bebop/a.vsnd_c", "maps/ui/hero_prefabs/necro.vpk");
        await WriteVpkAsync(Path.Combine(root.Addons, "pak06_dir.vpk"), token, "models/props_gameplay/soul_container/a.vmdl_c");
        await WriteVpkAsync(Path.Combine(root.Addons, "pak07_dir.vpk"), token, "scripts/odd.vdata_c");

        var mods = await root.Catalog().LoadAsync(token);

        var both = Assert.Single(mods, mod => mod.Files[0].EndsWith("pak05_dir.vpk", StringComparison.Ordinal));
        Assert.Equal(["Bebop", "Graves"], both.HeroNames.Order());
        Assert.Equal(["sound"], both.HeroKinds!["Bebop"]);
        Assert.Equal(["UI"], both.HeroKinds["Graves"]);
        Assert.Empty(both.Models);                                           // a sound or UI mod does not open on the hero's model
        Assert.Equal("Objects", Assert.Single(mods, mod => mod.Files[0].EndsWith("pak06_dir.vpk", StringComparison.Ordinal)).Group);
        var other = Assert.Single(mods, mod => mod.Files[0].EndsWith("pak07_dir.vpk", StringComparison.Ordinal));
        Assert.Equal("Other", other.Group);
        Assert.Empty(other.HeroNames);
    }

    [Fact]
    public void WithoutReadableGameDataTheShippedHeroListStays()
    {
        using var root = new TestRoot();

        Assert.Null(GameHeroes.Read(root.Game, Path.Combine(root.Root, "heroes-cache.json"), Hero.Shipped));   // no hero data, no localization
        Hero.UseGame(root.Game, root.Root);
        Assert.Same(Hero.Shipped, Hero.Catalogue);
        Assert.Equal("Bebop", Hero.Find("bebop")?.Name);
    }

    [Fact]
    public void AHeroTouchedByAFewFilesOfAMuchLargerHeroModIsNotListed()
    {
        var mina = Enumerable.Range(0, 40).Select(i => $"models/heroes_wip/vampirebat/m{i}.vmat_c").ToArray();

        Assert.Equal(["Nurse Harrow"], ModAttribution.StrayHeroes([.. mina, "models/heroes_wip/nurse/clock.vmdl_c"]).Select(hero => hero.Name));
        Assert.Empty(ModAttribution.StrayHeroes([.. mina[..10], .. mina[..10].Select(path => path.Replace("vampirebat", "hornet", StringComparison.Ordinal))]));   // two heroes alike
        Assert.Empty(ModAttribution.StrayHeroes(["models/heroes_wip/nurse/clock.vmdl_c", "models/heroes_wip/vampirebat/a.vmat_c"]));   // a small mod for two heroes
    }

    [Fact]
    public async Task AModKeepsItsNameFromTheAppJournalAndHellcatNamesItsOwnFiles()
    {
        using var root = new TestRoot();
        var token = TestContext.Current.CancellationToken;
        await WriteVpkAsync(Path.Combine(root.Addons, "pak28_dir.vpk"), token, "panorama/layout/base_hud.vxml_c");          // Mod Manager forgot it
        await WriteVpkAsync(Path.Combine(root.Addons, "gh-abc_pak08_dir.vpk"), token, "models/heroes_wip/vampirebat/a.vmdl_c");
        await WriteVpkAsync(Path.Combine(root.Addons, "pak15_dir.vpk"), token, "README.txt", "sounds/vo/gigawatt/ult.vsnd_c");
        Directory.CreateDirectory(Path.Combine(root.Root, "data", "journal"));
        await File.WriteAllTextAsync(Path.Combine(root.Root, "data", "journal", "addon-rename-1.json"), """
            { "operation": "addon-rename@1", "id": "1", "label": "Anita-UI", "startedUtc": "2026-11-04T06:47:40Z", "result": "completed",
              "entries": [ { "from": "pak28_dir.vpk", "to": "648899_pak28_dir.vpk" } ] }
            """, token);
        await File.WriteAllTextAsync(Path.Combine(root.Root, "data", "journal", "addon-rename-2.json"), """
            { "operation": "addon-rename@1", "id": "2", "label": "pak28_dir", "startedUtc": "2026-11-04T06:48:17Z", "result": "completed",
              "entries": [ { "from": "648899_pak28_dir.vpk", "to": "pak28_dir.vpk" } ] }
            """, token);
        await File.WriteAllTextAsync(Path.Combine(root.Addons, ".godhand-manifest.json"), """
            { "version": 1, "mods": { "gh-abc": { "name": "Vivian (model part)", "enabled": false, "originalNames": ["pak08_dir.vpk"], "installedVpks": [] } } }
            """, token);

        var mods = await root.Catalog().LoadAsync(token);

        var anita = Assert.Single(mods, mod => mod.Name == "Anita-UI");               // the first rename's label, carried through the second
        Assert.Equal("648899", anita.ModId);
        Assert.True(anita.Loaded);
        Assert.Equal(["Mina"], Assert.Single(mods, mod => mod.Name == "Vivian (model part)").HeroNames);
        Assert.Equal("sound", Assert.Single(mods, mod => mod.Files[0].EndsWith("pak15_dir.vpk", StringComparison.Ordinal)).MainKind);   // a README is not the kind
    }

    [Fact]
    public async Task ModManagerRecordsNameDisabledFilesAndFillTheHeroOnlyWhenPathsNameNone()
    {
        using var root = new TestRoot();
        var token = TestContext.Current.CancellationToken;
        await WriteVpkAsync(Path.Combine(root.Addons, "625513_pak32_dir.vpk"), token, "sounds/other/x.vsnd_c");           // disabled: <mod id>_<name>, no .dmm.json
        await WriteVpkAsync(Path.Combine(root.Addons, "local-9c5101cc_graves_in_ui.vpk"), token, "maps/ui/hero_prefabs/necro.vpk");
        await WriteVpkAsync(Path.Combine(root.Addons, "pak05_dir.vpk"), token, "models/props_gameplay/crate/a.vmdl_c");   // enabled, listed in installedVpks
        await WriteVpkAsync(Path.Combine(root.Addons, "snd-1_pak09_dir.vpk"), token, "sounds/abilities/bookworm/a.vsnd_c");
        var local = new
        {
            state = new
            {
                localMods = new object[]
                {
                    new { remoteId = "625513", name = "Miku Bebop", hero = "Bebop", status = "downloaded", downloadedAt = "2026-04-07T14:56:57.249Z", createdAt = "2025-11-01T11:00:34.256Z", installedVpks = Array.Empty<string>() },
                    new { remoteId = "local-9c5101cc", name = "graves nude in ui", hero = "Lady Geist", status = "downloaded", downloadedAt = (string?)null, createdAt = "2026-08-03T18:30:57.717Z", installedVpks = Array.Empty<string>() },
                    new { remoteId = "659224", name = "Soul container", hero = (string?)null, status = "installed", downloadedAt = "2026-11-03T21:18:22.634Z", createdAt = (string?)null, installedVpks = new[] { "pak05_dir.vpk" } },
                    new { remoteId = "snd-1", name = "Paige sound", hero = "Bebop", status = "downloaded", downloadedAt = "2026-01-02T03:04:05.000Z", createdAt = (string?)null, installedVpks = Array.Empty<string>() },
                },
            },
        };
        var state = Path.Combine(root.Root, "state.json");
        await File.WriteAllTextAsync(state, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["local-config"] = System.Text.Json.JsonSerializer.Serialize(local) }), token);

        var mods = await root.Catalog(state).LoadAsync(token);

        var miku = Assert.Single(mods, mod => mod.Name == "Miku Bebop");
        Assert.False(miku.Loaded);
        Assert.Equal(ModSource.Manager, miku.Source);
        Assert.Equal(["Bebop"], miku.HeroNames);                              // from Mod Manager: the paths name no hero
        Assert.Equal(new DateTimeOffset(2026, 4, 7, 14, 56, 57, 249, TimeSpan.Zero), miku.Added);   // downloadedAt wins over createdAt
        var graves = Assert.Single(mods, mod => mod.Name == "graves nude in ui");
        Assert.Equal(["Graves"], graves.HeroNames);                           // the file path wins over Mod Manager's hero
        Assert.Equal(new DateTimeOffset(2026, 8, 3, 18, 30, 57, 717, TimeSpan.Zero), graves.Added);   // createdAt when there is no downloadedAt
        var crate = Assert.Single(mods, mod => mod.Name == "Soul container");
        Assert.True(crate.Loaded);
        Assert.Equal("Objects", crate.Group);
        Assert.Equal(["Paige"], Assert.Single(mods, mod => mod.Name == "Paige sound").HeroNames);   // the path names Paige, not Mod Manager's Bebop
    }

    [Fact]
    public async Task DateAddedFallsBackToTheAppInstallThenTheFileCreationTimeAndSurvivesADisable()
    {
        using var root = new TestRoot();
        var token = TestContext.Current.CancellationToken;
        var plain = Path.Combine(root.Addons, "pak03_dir.vpk");
        await WriteVpkAsync(plain, token, Sound);
        File.SetCreationTimeUtc(plain, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var package = Path.Combine(root.Library, "skin_dir.vpk");
        await WriteVpkAsync(package, token, Model);
        var catalog = root.Catalog();

        await catalog.InstallAsync(package, "Wraith skin", token);
        var installed = Assert.Single(await catalog.LoadAsync(token), mod => mod.Name == "Wraith skin");
        await catalog.DisableAsync(installed, token);
        var disabled = Assert.Single(await catalog.LoadAsync(token), mod => mod.Name == "Wraith skin");
        await catalog.EnableAsync(disabled, token);
        var enabled = Assert.Single(await catalog.LoadAsync(token), mod => mod.Name == "Wraith skin");

        Assert.Equal(installed.Install!.FirstCreatedUtc, installed.Added);
        Assert.Equal(installed.Added, disabled.Added);                        // a mod keeps its date (and its place) when switched
        Assert.Equal(installed.Added, enabled.Added);
        Assert.Equal(new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero), Assert.Single(await catalog.LoadAsync(token), mod => mod.Source == ModSource.Other).Added);
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

        public string Root => root;

        public ModCatalog Catalog(string? managerState = null) => new(new AppSettings(Game, Path.Combine(root, "data")), managerState ?? Path.Combine(root, "no-manager-state.json"), new FakeGame(), new FakeBin());

        public void Dispose() => Directory.Delete(root, true);
    }
}
