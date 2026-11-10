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

        await root.Catalog().BuildEditsAsync(null, ModTestRoot.Model, new HashSet<int>(), [], null, null, [new PartShine(part, normal!, 1f)], token);

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
}
