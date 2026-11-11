using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using SteamDatabase.ValvePak;

namespace S2ModKit.Desktop.Tests;

/// <summary>Re-apply edits: the recipe a build saves, which mods are outdated after a game update, and a build made again.</summary>
public sealed class ReapplyTests
{
    private static ModEntry Mod(string key, string name, string? package, bool loaded = true, string? modId = null) =>
        new(key, name, ModSource.App, loaded, [key + ".vpk"], [loaded ? 5 : null], new Dictionary<string, int>(), [], [],
            package is null ? null : new AppInstall("root", new AddonFileInstallReceipt { Id = key, PackagePath = package, Status = "installed" }, DateTimeOffset.UnixEpoch), ModId: modId);

    [Fact]
    public void ARecipeIsFoundBesideItsPackageAndOutdatedAfterAGameUpdate()
    {
        var folder = Path.Combine(Path.GetTempPath(), "s2mk-recipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var recipe = new EditRecipe(EditRecipe.CurrentSchema, 6860, "models/x/x.vmdl", "Gojo", "673069", "file:pak03_dir.vpk", [2],
                [new RecipeRecolor(1, "models/x/c.vtex", "models/x/m.vmat", new ColorAdjustment(Colorize: new RgbColor(1, 0, 0)), new RgbColor(0.2f, 0.2f, 0.2f), 0.1f)], [],
                new Dictionary<string, EffectColorChange> { ["particles/a.vpcf"] = new(new RgbColor(0, 0, 1)) }, DateTimeOffset.UnixEpoch);
            recipe.Write(folder);
            var edited = Mod("file:pak09_dir.vpk", "Gojo (edited)", Path.Combine(folder, "x_dir.vpk"));
            var read = EditRecipe.Of(edited);

            Assert.NotNull(read);
            Assert.Equal(recipe.Recolors[0], read.Recolors[0]);
            Assert.Equal(recipe.Effects, read.Effects);
            Assert.Single(EditRecipe.Outdated([edited], 6900));                             // made for 6860
            Assert.Empty(EditRecipe.Outdated([edited], 6860));
            Assert.Empty(EditRecipe.Outdated([edited with { Loaded = false }], 6900));       // a disabled version is not offered
            Assert.Empty(EditRecipe.Outdated([edited], null));
            Assert.Null(EditRecipe.Of(Mod("file:pak10_dir.vpk", "Other", Path.Combine(Path.GetTempPath(), "nothing", "y_dir.vpk"))));

            var original = Mod("file:673069_pak03_dir.vpk", "Gojo", null, loaded: false, modId: "673069");
            Assert.Same(original, read.BaseIn([edited, original, Mod("file:pak11_dir.vpk", "Gojo", null)], edited));   // by mod id first
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>Live check (S2MODKIT_TEST_GAME): a test game folder with the real Doorman model and two of his effects; an edit (one part
    /// hidden, effects to blue) is built, then re-applied: a new version with the same edits, the first one kept disabled.</summary>
    [Fact]
    public async Task AnEditIsBuiltAgainFromItsRecipe()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this live check.");
            return;
        }

        const string Model = "models/heroes_wip/doorman_v2/doorman.vmdl";
        string[] effects = ["particles/abilities/doorman/doorman_bell_projectile.vpcf", "particles/abilities/doorman/doorman_bell_explode.vpcf"];
        var token = TestContext.Current.CancellationToken;
        using var root = new ModTestRoot();
        using (var real = new Package())
        {
            real.Read(Path.Combine(game, "citadel", "pak01_dir.vpk"));
            var entries = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
            foreach (var path in effects.Select(effect => effect + "_c").Append(Model + "_c"))
            {
                real.ReadEntry(real.FindEntry(path)!, out var bytes);
                entries[path] = bytes;
            }

            var pak = Path.Combine(root.Game, "citadel", "pak01_dir.vpk");
            File.Delete(pak);
            await S2ModKit.Adapters.Vpk.CompactVpkArchiveIO.WriteAsync(pak, entries, token);
        }

        var catalog = root.Catalog();
        var blue = new EffectColorChange(new RgbColor(0.1f, 0.3f, 1f));
        await catalog.BuildEditsAsync(null, Model, new HashSet<int> { 0 }, [], null, effects.ToDictionary(effect => effect, _ => blue), null, null, null, token);
        var mods = await catalog.LoadAsync(token);
        var edited = Assert.Single(mods, mod => mod.Loaded);
        var recipe = EditRecipe.Of(edited);
        Assert.NotNull(recipe);
        Assert.Equal([0], recipe.Hidden);
        Assert.Equal(effects.Order(), recipe.Effects.Keys.Order());

        var change = await catalog.ReapplyAsync(edited, mods, token);

        mods = await catalog.LoadAsync(token);
        var again = Assert.Single(mods, mod => mod.Loaded);
        Assert.NotEqual(edited.Install!.Receipt.PackagePath, again.Install!.Receipt.PackagePath);   // a new build
        Assert.Contains(mods, mod => !mod.Loaded && mod.Install?.Receipt.PackagePath == edited.Install.Receipt.PackagePath);   // the first one kept, disabled
        Assert.Contains("re-applied", again.Name, StringComparison.Ordinal);
        Assert.DoesNotContain("original mod", change.Message, StringComparison.Ordinal);       // made from the game: no third-party note
        using var built = new Package();
        built.Read(again.Files[0]);
        built.ReadEntry(built.FindEntry(effects[0] + "_c")!, out var effect);
        Assert.All(CompiledParticleEdit.ReadColors(effect, effects[0] + "_c").Where(value => EffectColors.ToHsv(value.R / 255f, value.G / 255f, value.B / 255f).S >= EffectColors.GreyLimit),
            value => Assert.InRange(EffectColors.ToHsv(value.R / 255f, value.G / 255f, value.B / 255f).H, 215, 235));   // blue again
        Assert.NotNull(built.FindEntry(Model + "_c"));                                         // the hidden part's model
    }
}
