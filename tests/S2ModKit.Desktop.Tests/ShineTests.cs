using S2ModKit.Adapters.Preview;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using SteamDatabase.ValvePak;

namespace S2ModKit.Desktop.Tests;

public sealed class ShineTests
{
    [Fact]
    public void ShineLowersRoughnessInsideTheMaskOnly()
    {
        var rgba = new byte[] { 10, 20, 30, 255, 10, 20, 30, 255, 10, 20, 30, 255 };
        ShineMath.Apply(rgba, [1f, 0.5f, 0f], 1f);

        Assert.Equal((byte)Math.Round(ShineMath.Roughness * 255), rgba[3]);       // full: near mirror
        Assert.Equal(134, rgba[7]);                                                   // half way
        Assert.Equal(255, rgba[11]);                                               // outside the part: as it was
        Assert.Equal(new byte[] { 10, 20, 30 }, rgba[..3]);                        // the normal (colour channels) stays
    }

    [Fact]
    public void NoShineLeavesTheTextureAsItIs()
    {
        var rgba = new byte[] { 1, 2, 3, 200, 4, 5, 6, 240 };
        ShineMath.Apply(rgba, null, 0f);

        Assert.Equal(new byte[] { 1, 2, 3, 200, 4, 5, 6, 240 }, rgba);
    }

    /// <summary>Live check (S2MODKIT_TEST_GAME): a shiny Wraith part's normal-roughness texture is built with lower roughness (the stored channel that holds it) in a
    /// test game folder, and the recipe keeps the shine for Re-apply edits.</summary>
    [Fact]
    public async Task AShinyPartIsBuiltWithLowerRoughness()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this live check.");
            return;
        }

        var token = TestContext.Current.CancellationToken;
        using var root = new ModTestRoot();
        var entries = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        string? normal = null;
        int part = -1;
        using (var real = new Package())
        {
            real.Read(Path.Combine(game, "citadel", "pak01_dir.vpk"));
            byte[] Copy(string path)
            {
                real.ReadEntry(real.FindEntry(path)!, out var bytes);
                entries[path] = bytes;
                return bytes;
            }

            var model = Copy(ModTestRoot.Model);
            var materials = CompiledMaterialEdit.ModelMaterials(model, ModTestRoot.Model);
            foreach (var material in materials)
            {
                if (real.FindEntry(material + "_c") is null)
                {
                    continue;
                }

                var textures = CompiledMaterialEdit.Textures(Copy(material + "_c"), material + "_c");
                if (textures.TryGetValue(PreviewSession.NormalRoughnessTexture, out var found) && real.FindEntry(found + "_c") is { } candidate
                    && (normal is null || candidate.TotalLength > real.FindEntry(normal + "_c")!.TotalLength))
                {
                    normal = found;                                                // the largest: a real map, not a one-value 4x4
                }
            }

            Assert.NotNull(normal);
            Copy(normal + "_c");
            for (var index = 0; index < 64 && part < 0; index++)                // the mask lands where the part's UVs are, in any texture
            {
                try
                {
                    var mask = PartMasks.Mask(model, ModTestRoot.Model, index, 64, 64);
                    if (mask.Any(value => value > 0))
                    {
                        part = index;
                    }
                }
                catch (S2ModKitException)
                {
                    break;
                }
            }
        }

        Assert.True(part >= 0, "no Wraith part has UVs");
        var pak = Path.Combine(root.Game, "citadel", "pak01_dir.vpk");
        File.Delete(pak);
        await S2ModKit.Adapters.Vpk.CompactVpkArchiveIO.WriteAsync(pak, entries, token);
        var before = InPlaceTexture.ReadStored(entries[normal + "_c"].ToArray(), normal + "_c");
        var channel = ShineMath.RoughnessChannel(before.Codec);

        await root.Catalog().BuildEditsAsync(null, ModTestRoot.Model, new HashSet<int>(), [], null, null, [new PartShine(part, normal!, 1f)], null, null, token);

        var built = Assert.Single(await root.Catalog().LoadAsync(token), mod => mod.Loaded);
        using var output = new Package();
        output.Read(built.Files[0]);
        output.ReadEntry(output.FindEntry(normal + "_c")!, out var shiny);
        var after = InPlaceTexture.ReadStored(shiny, normal + "_c");
        Assert.True(Mean(after.Rgba, channel) < Mean(before.Rgba, channel) - 1, $"roughness {Mean(before.Rgba, channel):0.0} -> {Mean(after.Rgba, channel):0.0} did not drop");
        var otherChannel = channel == 3 ? 0 : 3;
        Assert.InRange(Mean(after.Rgba, otherChannel), Mean(before.Rgba, otherChannel) - 2, Mean(before.Rgba, otherChannel) + 2);   // the rest stays
        var recipe = Assert.IsType<EditRecipe>(EditRecipe.Of(built));
        Assert.Equal(1f, Assert.Single(recipe.Shines!).Shine);
    }

    private static double Mean(byte[] rgba, int channel)
    {
        long sum = 0;
        for (var i = channel; i < rgba.Length; i += 4)
        {
            sum += rgba[i];
        }

        return sum / (double)(rgba.Length / 4);
    }

    [Fact]
    public void AMaterialTurnsSeeThroughWithTheGamesOwnTranslucencySettings()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this proprietary local check.");
            return;
        }

        const string Clothes = "models/heroes_wip/unicorn/materials/unicorn_clothes.vmat_c";
        using var package = new SteamDatabase.ValvePak.Package();
        package.Read(Path.Combine(game, "citadel", "pak01_dir.vpk"));
        package.ReadEntry(package.FindEntry(Clothes)!, out var original);
        Assert.Null(S2ModKit.Adapters.Source2.CompiledMaterialEdit.Opacity(original, Clothes));

        var edited = S2ModKit.Adapters.Source2.CompiledMaterialEdit.SetOpacity(original, Clothes, 0.4f);

        Assert.Equal(0.4f, S2ModKit.Adapters.Source2.CompiledMaterialEdit.Opacity(edited, Clothes)!.Value, 3);
        using var resource = new ValveResourceFormat.Resource { FileName = Clothes };
        resource.Read(new MemoryStream(edited));
        var material = (ValveResourceFormat.ResourceTypes.Material)resource.DataBlock!;
        Assert.Equal(1, material.IntParams["F_WRITE_DEPTH_BEFORE_ALPHA_BLENDING"]);
        Assert.Equal(1, material.IntParams["F_USE_NPR_LIGHTING"]);            // what it had stays
        Assert.Equal(S2ModKit.Adapters.Source2.CompiledMaterialEdit.Textures(original, Clothes), S2ModKit.Adapters.Source2.CompiledMaterialEdit.Textures(edited, Clothes));
        var again = S2ModKit.Adapters.Source2.CompiledMaterialEdit.SetOpacity(edited, Clothes, 0.9f);
        Assert.Equal(0.9f, S2ModKit.Adapters.Source2.CompiledMaterialEdit.Opacity(again, Clothes)!.Value, 3);
        using var second = new ValveResourceFormat.Resource { FileName = Clothes };
        second.Read(new MemoryStream(again));
        Assert.Equal(material.IntParams.Count, ((ValveResourceFormat.ResourceTypes.Material)second.DataBlock!).IntParams.Count);   // set, not added twice
    }

    [Fact]
    public void SeeThroughLowersTheAlphaInsideThePartOnly()
    {
        var rgba = new byte[] { 10, 20, 30, 0, 10, 20, 30, 0, 10, 20, 30, 0 };
        SeeThroughMath.Solid(rgba);                                            // an opaque material's unused alpha (0) would hide everything
        SeeThroughMath.Apply(rgba, [1f, 0.5f, 0f], 0.4f);

        Assert.Equal(102, rgba[3]);                                                 // the part: 40 %
        Assert.Equal(178, rgba[7]);                                                 // its soft edge: half way
        Assert.Equal(255, rgba[11]);                                                // the rest of the material: solid
        Assert.Equal(new byte[] { 10, 20, 30 }, rgba[..3]);                         // the colours stay
    }

    /// <summary>Live check (S2MODKIT_TEST_GAME): a see-through Wraith part is built as a translucent material (opacity 1) whose colour
    /// texture is solid except inside the part, and the recipe keeps it.</summary>
    [Fact]
    public async Task BuildEditedModMakesOnlyTheChosenPartSeeThroughAndKeepsItInTheRecipe()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this proprietary local check.");
            return;
        }

        var token = TestContext.Current.CancellationToken;
        using var root = new ModTestRoot();
        var entries = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        string? material = null, colour = null;
        var part = -1;
        using (var real = new Package())
        {
            real.Read(Path.Combine(game, "citadel", "pak01_dir.vpk"));
            byte[] Copy(string path)
            {
                real.ReadEntry(real.FindEntry(path)!, out var bytes);
                entries[path] = bytes;
                return bytes;
            }

            var model = Copy(ModTestRoot.Model);
            foreach (var candidate in CompiledMaterialEdit.ModelMaterials(model, ModTestRoot.Model))
            {
                if (real.FindEntry(candidate + "_c") is null)
                {
                    continue;
                }

                var textures = CompiledMaterialEdit.Textures(Copy(candidate + "_c"), candidate + "_c");
                if (textures.TryGetValue("g_tColor", out var found) && !found.StartsWith("materials/default/", StringComparison.Ordinal) && real.FindEntry(found + "_c") is { } entry
                    && (colour is null || entry.TotalLength > real.FindEntry(colour + "_c")!.TotalLength))
                {
                    (material, colour) = (candidate, found);                           // the largest colour texture
                }
            }

            Assert.NotNull(colour);
            Copy(colour + "_c");
            for (var index = 0; index < 64 && part < 0; index++)
            {
                try
                {
                    if (PartMasks.Mask(model, ModTestRoot.Model, index, 64, 64).Count(value => value > 0) > 40)
                    {
                        part = index;
                    }
                }
                catch (S2ModKitException)
                {
                    break;
                }
            }
        }

        Assert.True(part >= 0, "no Wraith part has UVs");
        var pak = Path.Combine(root.Game, "citadel", "pak01_dir.vpk");
        File.Delete(pak);
        await S2ModKit.Adapters.Vpk.CompactVpkArchiveIO.WriteAsync(pak, entries, token);

        await root.Catalog().BuildEditsAsync(null, ModTestRoot.Model, new HashSet<int>(), [], null, null, null, null, [new PartOpacity(part, material!, 0.3f)], token);

        var built = Assert.Single(await root.Catalog().LoadAsync(token), mod => mod.Loaded);
        using var output = new Package();
        output.Read(built.Files[0]);
        output.ReadEntry(output.FindEntry(material + "_c")!, out var translucent);
        Assert.Equal(1f, CompiledMaterialEdit.Opacity(translucent, material + "_c")!.Value, 3);   // translucent; the texture says how much
        output.ReadEntry(output.FindEntry(colour + "_c")!, out var texture);
        var (rgba, width, height, _) = InPlaceTexture.ReadStored(texture, colour + "_c");
        var mask = PartMasks.Mask(entries[ModTestRoot.Model].ToArray(), ModTestRoot.Model, part, width, height);
        double inside = 0, outside = 0;
        int insideCount = 0, outsideCount = 0;
        for (var t = 0; t < mask.Length; t++)
        {
            if (mask[t] >= 1)
            {
                inside += rgba[(t * 4) + 3];
                insideCount++;
            }
            else if (mask[t] <= 0)
            {
                outside += rgba[(t * 4) + 3];
                outsideCount++;
            }
        }

        Assert.InRange(inside / insideCount, 0.3 * 255 - 12, 0.3 * 255 + 12);
        Assert.True(outside / outsideCount > 245, $"the rest of the material is {outside / outsideCount:0} of 255");
        Assert.Equal(0.3f, Assert.Single(EditRecipe.Of(built)!.Opacities!).Opacity, 3);
    }
}
