using System.Numerics;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Desktop.Tests;

public sealed class MaterialColorTests
{
    private static readonly ColorAdjustment Yellow = new(Colorize: new RgbColor(0.95f, 0.8f, 0.2f));

    [Fact]
    public void OnlyColouredSettingsFollowARecolor()
    {
        Assert.True(MaterialColors.Follows("TextureNprTramsissiveColor1", new Vector4(0.31f, 0.18f, 0.33f, 0)));    // purple glow
        Assert.True(MaterialColors.Follows("g_vSolidOutlineTint", new Vector4(0.1f, 0.04f, 0.12f, 0)));
        Assert.False(MaterialColors.Follows("g_vColorTint1", new Vector4(1, 1, 1, 0)));                              // white: no colour
        Assert.False(MaterialColors.Follows("TextureTintMask1", new Vector4(1, 0, 0, 0)));                           // a mask, not a colour
        Assert.False(MaterialColors.Follows("g_vAlbedoContrastSaturationBrightness1", new Vector4(1, 1.3f, 1, 0)));
        var glow = MaterialColors.Apply(new Vector4(0.31f, 0.18f, 0.33f, 0.5f), Yellow);
        Assert.True(glow.X > glow.Z && glow.Y > glow.Z);                                                            // purple becomes yellow
        Assert.Equal(0.5f, glow.W);
    }

    /// <summary>Live check on Calico's cat material (S2MODKIT_TEST_GAME): the glow and outline colours follow a yellow recolor and read
    /// back as computed; white settings stay.</summary>
    [Fact]
    public async Task TheCatMaterialsGlowAndOutlineFollowARecolor()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this live check.");
            return;
        }

        const string Path = "models/heroes_staging/nano/cat_v2/materials/nano_cat.vmat_c";
        using var package = new Package();
        package.Read(System.IO.Path.Combine(game, "citadel", "pak01_dir.vpk"));
        package.ReadEntry(package.FindEntry(Path)!, out var original);
        Material Read(byte[] bytes)
        {
            var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            return (Material)resource.DataBlock!;
        }

        var before = Read(original);
        var (edited, changed, glowTexture) = CompiledMaterialEdit.Recolor(original, Path, Yellow);
        var after = Read(edited);

        Assert.Contains("TextureNprTramsissiveColor1", changed);
        Assert.Contains("g_vSolidOutlineTint", changed);
        Assert.DoesNotContain("g_vColorTint1", changed);
        Assert.EndsWith(".vtex", glowTexture, StringComparison.Ordinal);
        foreach (var (name, value) in before.VectorParams)
        {
            var expected = changed.Contains(name) ? MaterialColors.Apply(value, Yellow) : value;
            Assert.True(Vector4.Distance(expected, after.VectorParams[name]) < 1e-4f, $"{name}: {after.VectorParams[name]} instead of {expected}");
        }

        Assert.Equal(before.TextureParams, after.TextureParams);

        // its glow texture is one colour (1x1 in a 4x4 block): it decodes, recolors and writes back in place
        var archives = new[] { System.IO.Path.Combine(game, "citadel", "pak01_dir.vpk") };
        var workshop = new VrfTextureWorkshop();
        var glow = await workshop.DecodeAsync(archives, glowTexture!, TestContext.Current.CancellationToken);
        Assert.Equal(glow.Width * glow.Height * 4, glow.Rgba.Length);
        var rgba = (byte[])glow.Rgba.Clone();
        RecolorMath.Apply(rgba, null, Yellow);
        var staging = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "s2mk-glow-" + Guid.NewGuid().ToString("N"));
        try
        {
            var written = await workshop.CompileAsync(glow, rgba, staging, TestContext.Current.CancellationToken);
            using var resource = new Resource();
            resource.Read(new MemoryStream(written));
            using var bitmap = ((Texture)resource.DataBlock!).GenerateBitmap();
            var pixel = bitmap.GetPixel(0, 0);
            Assert.True(pixel.Red > pixel.Blue && pixel.Green > pixel.Blue, $"glow {pixel} is not yellow");
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
        }
    }

    /// <summary>Live check (S2MODKIT_TEST_GAME): Calico's cat form (ability 3 turns her into another model) takes the ability's colour in a
    /// build: its materials' colours and colour texture, in a test game folder (nothing is installed in the real game).</summary>
    [Fact]
    public async Task AnAbilitysModelTakesTheAbilitysColourInABuild()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this live check.");
            return;
        }

        var token = TestContext.Current.CancellationToken;
        var cache = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "s2mk-abilities-" + Guid.NewGuid().ToString("N") + ".json");
        var abilities = HeroAbilities.Of(HeroAbilities.Read(game, cache), "models/heroes_staging/nano/nano_v2/nano.vmdl");
        File.Delete(cache);
        const string CatForm = "models/abilities/nano_catform_model.vmdl";
        Assert.Contains(CatForm, Assert.Single(abilities, ability => ability.Slot == 3).ModelList);

        using var root = new ModTestRoot();
        var entries = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        using (var real = new Package())
        {
            real.Read(System.IO.Path.Combine(game, "citadel", "pak01_dir.vpk"));
            byte[] Copy(string path)
            {
                real.ReadEntry(real.FindEntry(path)!, out var bytes);
                entries[path] = bytes;
                return bytes;
            }

            var model = Copy(CatForm + "_c");
            foreach (var material in CompiledMaterialEdit.ModelMaterials(model, CatForm + "_c"))
            {
                var materialBytes = Copy(material + "_c");
                foreach (var texture in CompiledMaterialEdit.Textures(materialBytes, material + "_c").Values.Where(texture => real.FindEntry(texture + "_c") is not null))
                {
                    Copy(texture + "_c");
                }
            }
        }

        var pak = System.IO.Path.Combine(root.Game, "citadel", "pak01_dir.vpk");
        File.Delete(pak);
        await S2ModKit.Adapters.Vpk.CompactVpkArchiveIO.WriteAsync(pak, entries, token);
        var yellow = new EffectColorChange(new RgbColor(0.95f, 0.8f, 0.2f));

        await root.Catalog().BuildEditsAsync(null, CatForm, new HashSet<int>(), [], null, new Dictionary<string, EffectColorChange> { [CatForm] = yellow }, null, null, token);

        var built = Assert.Single(await root.Catalog().LoadAsync(token), mod => mod.Loaded);
        using var package = new Package();
        package.Read(built.Files[0]);
        const string Translucent = "materials/particle/abilities/nano/nano_cat_translucent.vmat_c";
        package.ReadEntry(package.FindEntry(Translucent)!, out var materialOut);
        var resource = new Resource();
        resource.Read(new MemoryStream(materialOut));
        var glow = ((Material)resource.DataBlock!).VectorParams["TextureNprTramsissiveColor1"];
        Assert.True(glow.X > glow.Z && glow.Y > glow.Z, $"the cat form's glow {glow} is not yellow");
        var colour = CompiledMaterialEdit.Textures(materialOut, Translucent)["g_tColor"];
        Assert.NotNull(package.FindEntry(colour + "_c"));                                      // its colour texture recolored too
    }
}
