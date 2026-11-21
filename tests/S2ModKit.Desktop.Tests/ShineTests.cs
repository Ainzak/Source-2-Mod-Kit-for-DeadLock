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

        await root.Catalog().BuildEditsAsync(null, ModTestRoot.Model, new HashSet<int>(), [], null, null, [new PartShine(part, normal!, 1f)], null, null, null, token);

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
    public void ThePulseIsTheMaterialCompilersOwnProgram()
    {
        // a = 0.4, b = 2, c = 1 as the Source 2 SDK compiler writes a * sin(b * time()) + c
        byte[] compiler = [0x07, 0xCD, 0xCC, 0xCC, 0x3E, 0x07, 0x00, 0x00, 0x00, 0x40, 0x06, 0x1B, 0x00, 0x15, 0x06, 0x00, 0x00, 0x15, 0x07, 0x00, 0x00, 0x80, 0x3F, 0x13, 0x00];

        Assert.Equal(compiler, CompiledMaterialLook.PulseExpression(0.4f, 2f, 1f));
    }

    [Fact]
    public void ALookWithValuesOutsideTheirRangeIsRefusedBeforeAnythingIsRead()
    {
        var glow = new MaterialLook(0.2f, 0.6f, 1f, 0.6f, 2.5f, 0.6f, false);

        Assert.Equal("MATERIAL_LOOK_VALUE", Assert.Throws<S2ModKitException>(() => CompiledMaterialLook.Apply([], "a.vmat_c", glow with { R = 1.5f }, "a.vtex")).Error.Code);
        Assert.Equal("MATERIAL_LOOK_VALUE", Assert.Throws<S2ModKitException>(() => CompiledMaterialLook.Apply([], "a.vmat_c", glow with { Strength = float.NaN }, "a.vtex")).Error.Code);
        Assert.Equal("MATERIAL_LOOK_MASK", Assert.Throws<S2ModKitException>(() => CompiledMaterialLook.Apply([], "a.vmat_c", glow, "a.png")).Error.Code);
    }

    /// <summary>Live check (S2MODKIT_TEST_GAME): a hero material glows in the chosen colour with the game's own self-illumination settings, its own
    /// mask texture and a pulse, and its outline takes another colour, thicker and brighter; a second look replaces the first (set, not added
    /// twice) and no pulse removes the dynamic setting.</summary>
    [Fact]
    public void AMaterialGlowsWithItsOwnMaskAndAPulse()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this proprietary local check.");
            return;
        }

        const string Arms = "models/heroes_wip/wraith/materials/wraith_arms.vmat_c";
        const string Mask = "models/heroes_wip/wraith/materials/wraith_arms_s2mk_own1_glowmask.vtex";
        var original = ReadGame(game, Arms);
        var glow = new MaterialLook(0.2f, 0.6f, 1f, 0.8f, 2.5f, 0.5f, false);

        var edited = CompiledMaterialLook.Apply(original, Arms, glow, Mask);

        var (material, references) = Read(edited, Arms);
        Assert.Equal("pbr.vfx", material.ShaderName);
        Assert.Equal(1, material.IntParams["F_SELF_ILLUM"]);
        Assert.Equal(1, material.IntParams["F_SOLID_COLOR_OUTLINE"]);
        Assert.Equal(0f, material.FloatParams["g_flSelfIllumAlbedoFactor1"]);           // the chosen colour itself
        Assert.Equal(0.8f, material.FloatParams["g_flSelfIllumScale1"], 3);
        Assert.Equal(new System.Numerics.Vector4(0.2f, 0.6f, 1f, 0f), material.VectorParams["g_vSelfIllumTint1"]);
        Assert.Equal(Mask, material.TextureParams["g_tSelfIllumMask"]);
        Assert.Contains(Mask, references);
        var others = CompiledMaterialEdit.Textures(original, Arms).Where(texture => texture.Key != "g_tSelfIllumMask");
        Assert.All(others, texture => Assert.Equal(texture.Value, material.TextureParams[texture.Key]));   // the other textures stay
        Assert.Equal(PulseOf(edited, Arms), CompiledMaterialLook.PulseExpression(0.8f * 0.5f, 2.5f, 0.8f));

        // a second look on the same material: settings are set, not added again; no pulse removes the dynamic setting
        var steady = CompiledMaterialLook.Apply(edited, Arms, glow with { PulseSpeed = 0f, Strength = 1.5f, Edges = true, R = 1f }, Mask);
        var (again, _) = Read(steady, Arms);
        Assert.Equal(material.IntParams.Count, again.IntParams.Count);
        Assert.Null(PulseOf(steady, Arms));
        Assert.Equal(1.5f, again.FloatParams["g_flSelfIllumScale1"], 3);
        Assert.Equal(1f, again.VectorParams["g_vSelfIllumTint1"].X);
        Assert.Equal(10f, again.FloatParams["g_flSelfIllumFresnelMaskExponent"]);       // brighter at the edges
        Assert.Equal(0.85f, again.VectorParams["g_vSelfIllumFresnelMaskTint1"].X, 3);
        Assert.Equal(1f, again.VectorParams["g_vColorTint1"].X, 3);                    // 0.4 + 0.6 x colour
        Assert.Equal(-1.19f, again.FloatParams["g_flAlphaAnglePower1"], 3);
    }

    /// <summary>Live check (S2MODKIT_TEST_GAME, S2MODKIT_MESHOPTIMIZER_PATH): a piece with a look gets its own draw call and material copy with a
    /// white copy of the material's glow mask; the shared material stays; the recipe keeps the look and a re-apply builds it again.</summary>
    [Fact]
    public async Task BuildEditedModGivesTheChosenPieceALookAndKeepsItInTheRecipe()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game) || !File.Exists(Environment.GetEnvironmentVariable("S2MODKIT_MESHOPTIMIZER_PATH")))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME and S2MODKIT_MESHOPTIMIZER_PATH to enable this proprietary local check.");
            return;
        }

        var token = TestContext.Current.CancellationToken;
        using var root = new ModTestRoot();
        var entries = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        byte[] model;
        using (var citadel = new Package())
        using (var core = new Package())
        {
            citadel.Read(Path.Combine(game, "citadel", "pak01_dir.vpk"));
            core.Read(Path.Combine(game, "core", "pak01_dir.vpk"));
            byte[]? Copy(string path)
            {
                var package = citadel.FindEntry(path) is not null ? citadel : core;
                if (package.FindEntry(path) is not { } entry)
                {
                    return null;
                }

                package.ReadEntry(entry, out var bytes);
                entries[path] = bytes;
                return bytes;
            }

            model = Copy(ModTestRoot.Model)!;
            foreach (var material in CompiledMaterialEdit.ModelMaterials(model, ModTestRoot.Model))
            {
                if (Copy(material + "_c") is { } bytes)
                {
                    foreach (var texture in CompiledMaterialEdit.Textures(bytes, material + "_c").Where(item => item.Key is "g_tColor" or "g_tSelfIllumMask"))
                    {
                        Copy(texture.Value + "_c");
                    }
                }
            }
        }

        var piece = PartMasks.Pieces(model, ModTestRoot.Model).First(item => (Draws(model)[item.Part].Material is var material) && entries.ContainsKey(material + "_c"));
        var before = Draws(model);
        var partMaterial = before[piece.Part].Material;
        var pak = Path.Combine(root.Game, "citadel", "pak01_dir.vpk");
        File.Delete(pak);
        await S2ModKit.Adapters.Vpk.CompactVpkArchiveIO.WriteAsync(pak, entries, token);
        var look = new MaterialLook(0.2f, 0.6f, 1f, 0.9f, 2.5f, 0.6f, true);

        var catalog = root.Catalog();
        await catalog.BuildEditsAsync(null, ModTestRoot.Model, new HashSet<int>(), [], null, null, null, null, null, [new PartLook(piece.Index, partMaterial, look)], token);

        var built = Assert.Single(await catalog.LoadAsync(token), mod => mod.Loaded);
        using var output = new Package();
        output.Read(built.Files[0]);
        output.ReadEntry(output.FindEntry(ModTestRoot.Model)!, out var builtModel);
        var after = Draws(builtModel);
        Assert.Equal(before.Count + 1, after.Count);
        var split = after[piece.Part + 1];
        Assert.EndsWith("_s2mk_own1.vmat", split.Material, StringComparison.Ordinal);
        Assert.Equal(partMaterial, after[piece.Part].Material);                 // the rest of the part keeps its material
        Assert.Equal(before[piece.Part].Triangles.Order(), after[piece.Part].Triangles.Concat(split.Triangles).Order());   // every triangle is still drawn

        output.ReadEntry(output.FindEntry(split.Material + "_c")!, out var newMaterial);
        var (written, references) = Read(newMaterial, split.Material + "_c");
        Assert.Equal(1, written.IntParams["F_SELF_ILLUM"]);
        Assert.Equal(0.9f, written.FloatParams["g_flSelfIllumScale1"], 3);
        Assert.NotNull(PulseOf(newMaterial, split.Material + "_c"));
        var mask = written.TextureParams["g_tSelfIllumMask"];
        Assert.EndsWith("_s2mk_own1_glowmask.vtex", mask, StringComparison.Ordinal);
        Assert.Contains(mask, references);
        output.ReadEntry(output.FindEntry(mask + "_c")!, out var maskBytes);
        var (rgba, _, _, _) = InPlaceTexture.ReadStored(maskBytes, mask + "_c");
        Assert.All(rgba.Chunk(4), texel => Assert.Equal(new byte[] { 255, 255, 255 }, texel[..3]));   // white: the glow covers the whole part
        Assert.Null(output.FindEntry(partMaterial + "_c"));                     // the shared material is not shipped: unchanged

        var recipe = EditRecipe.Of(built)!;
        var recipeLook = Assert.Single(recipe.Looks!);
        Assert.Equal((piece.Index, partMaterial, true, 0.9f), (recipeLook.Part, recipeLook.Material, recipeLook.Edges, recipeLook.Strength));
        Assert.True(recipe.HasEdits);

        output.Dispose();                                                       // the re-apply renames the first build's files
        await catalog.ReapplyAsync(built, await catalog.LoadAsync(token), token);
        var again = Assert.Single(await catalog.LoadAsync(token), mod => mod.Loaded);
        Assert.Equal(recipeLook, Assert.Single(EditRecipe.Of(again)!.Looks!));

        // an edit of this edited mod: another piece of the same material gets its own copy under a new name, the first copy stays
        byte[] againModel;
        using (var first = new Package())
        {
            first.Read(again.Files[0]);
            first.ReadEntry(first.FindEntry(ModTestRoot.Model)!, out againModel);
        }

        var other = PartMasks.Pieces(againModel, ModTestRoot.Model).First(item => Draws(againModel)[item.Part].Material == partMaterial);
        await catalog.BuildEditsAsync(again, ModTestRoot.Model, new HashSet<int>(), [], null, null, null, null, null,
            [new PartLook(other.Index, partMaterial, look with { R = 1f, G = 0f, B = 0f })], token);
        var second = Assert.Single(await catalog.LoadAsync(token), mod => mod.Loaded);
        using var secondOutput = new Package();
        secondOutput.Read(second.Files[0]);
        secondOutput.ReadEntry(secondOutput.FindEntry(ModTestRoot.Model)!, out var secondModel);
        var materials = Draws(secondModel).Select(draw => draw.Material).Where(name => name.Contains("_s2mk_own", StringComparison.Ordinal)).ToArray();
        Assert.Equal(materials.Length, materials.Distinct().Count());           // no two pieces share a copy
        Assert.Contains(materials, name => name.EndsWith("_s2mk_own2.vmat", StringComparison.Ordinal));
        secondOutput.ReadEntry(secondOutput.FindEntry(split.Material + "_c")!, out var firstCopy);
        Assert.Equal(0.2f, Read(firstCopy, split.Material + "_c").Item1.VectorParams["g_vSelfIllumTint1"].X, 3);   // the first piece keeps its own look
    }

    /// <summary>Live check (S2MODKIT_TEST_GAME, S2MODKIT_MESHOPTIMIZER_PATH): a see-through piece of a Wraith part gets its own draw call
    /// with its own translucent material and a BC3 copy of the colour texture at the chosen alpha; the part's other triangles keep the
    /// original material, the triangles are all still there, and the recipe keeps the piece.</summary>
    [Fact]
    public async Task BuildEditedModMakesOnlyTheChosenPieceSeeThroughAndKeepsItInTheRecipe()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game) || !File.Exists(Environment.GetEnvironmentVariable("S2MODKIT_MESHOPTIMIZER_PATH")))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME and S2MODKIT_MESHOPTIMIZER_PATH to enable this proprietary local check.");
            return;
        }

        var token = TestContext.Current.CancellationToken;
        using var root = new ModTestRoot();
        var entries = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        byte[] model;
        using (var real = new Package())
        {
            real.Read(Path.Combine(game, "citadel", "pak01_dir.vpk"));
            byte[] Copy(string path)
            {
                real.ReadEntry(real.FindEntry(path)!, out var bytes);
                entries[path] = bytes;
                return bytes;
            }

            model = Copy(ModTestRoot.Model);
            foreach (var material in CompiledMaterialEdit.ModelMaterials(model, ModTestRoot.Model).Where(material => real.FindEntry(material + "_c") is not null))
            {
                if (CompiledMaterialEdit.Textures(Copy(material + "_c"), material + "_c").GetValueOrDefault("g_tColor") is { } colour && real.FindEntry(colour + "_c") is not null)
                {
                    Copy(colour + "_c");
                }
            }
        }

        var piece = PartMasks.Pieces(model, ModTestRoot.Model).First(item => (Draws(model)[item.Part].Material is var material) && entries.ContainsKey(material + "_c"));
        var before = Draws(model);
        var partMaterial = before[piece.Part].Material;
        var pak = Path.Combine(root.Game, "citadel", "pak01_dir.vpk");
        File.Delete(pak);
        await S2ModKit.Adapters.Vpk.CompactVpkArchiveIO.WriteAsync(pak, entries, token);

        await root.Catalog().BuildEditsAsync(null, ModTestRoot.Model, new HashSet<int>(), [], null, null, null, null, [new PartOpacity(piece.Index, partMaterial, 0.3f)], null, token);

        var built = Assert.Single(await root.Catalog().LoadAsync(token), mod => mod.Loaded);
        using var output = new Package();
        output.Read(built.Files[0]);
        output.ReadEntry(output.FindEntry(ModTestRoot.Model)!, out var builtModel);
        var after = Draws(builtModel);
        Assert.Equal(before.Count + 1, after.Count);
        var split = after[piece.Part + 1];
        Assert.EndsWith("_s2mk_own1.vmat", split.Material, StringComparison.Ordinal);
        Assert.Equal(partMaterial, after[piece.Part].Material);                 // the rest of the part keeps its material
        Assert.Equal(piece.Triangles * 3, split.Count);
        Assert.Equal(before[piece.Part].Count, after[piece.Part].Count + split.Count);
        Assert.Equal(before[piece.Part].Triangles.Order(), after[piece.Part].Triangles.Concat(split.Triangles).Order());   // every triangle is still drawn

        output.ReadEntry(output.FindEntry(split.Material + "_c")!, out var newMaterial);
        Assert.Equal(1f, CompiledMaterialEdit.Opacity(newMaterial, split.Material + "_c")!.Value, 3);
        var newTexture = CompiledMaterialEdit.Textures(newMaterial, split.Material + "_c")["g_tColor"];
        Assert.EndsWith("_s2mk_own1.vtex", newTexture, StringComparison.Ordinal);
        output.ReadEntry(output.FindEntry(newTexture + "_c")!, out var textureBytes);
        using (var texture = new ValveResourceFormat.Resource { FileName = newTexture + "_c" })
        {
            texture.Read(new MemoryStream(textureBytes));
            Assert.Equal(ValveResourceFormat.VTexFormat.DXT5, ((ValveResourceFormat.ResourceTypes.Texture)texture.DataBlock!).Format);
        }

        var (rgba, _, _, _) = InPlaceTexture.ReadStored(textureBytes, newTexture + "_c");
        Assert.InRange(Mean(rgba, 3), 0.3 * 255 - 3, 0.3 * 255 + 3);
        Assert.Null(output.FindEntry(partMaterial + "_c"));                     // the shared material is not shipped: unchanged
        Assert.Equal(piece.Index, Assert.Single(EditRecipe.Of(built)!.Opacities!).Part);
    }

    /// <summary>Live check: a recolored piece gets its own material and colour texture copy and its own draw call; the shared colour texture is
    /// not written, so the part's other pieces keep their colours.</summary>
    [Fact]
    public async Task ARecoloredPieceGetsItsOwnCopiesAndTheSharedTextureStays()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game) || !File.Exists(Environment.GetEnvironmentVariable("S2MODKIT_MESHOPTIMIZER_PATH")))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME and S2MODKIT_MESHOPTIMIZER_PATH to enable this proprietary local check.");
            return;
        }

        var token = TestContext.Current.CancellationToken;
        using var root = new ModTestRoot();
        var entries = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        byte[] model;
        using (var real = new Package())
        {
            real.Read(Path.Combine(game, "citadel", "pak01_dir.vpk"));
            byte[] Copy(string path)
            {
                real.ReadEntry(real.FindEntry(path)!, out var bytes);
                entries[path] = bytes;
                return bytes;
            }

            model = Copy(ModTestRoot.Model);
            foreach (var material in CompiledMaterialEdit.ModelMaterials(model, ModTestRoot.Model).Where(material => real.FindEntry(material + "_c") is not null))
            {
                if (CompiledMaterialEdit.Textures(Copy(material + "_c"), material + "_c").GetValueOrDefault("g_tColor") is { } colour && real.FindEntry(colour + "_c") is not null)
                {
                    Copy(colour + "_c");
                }
            }
        }

        var piece = PartMasks.Pieces(model, ModTestRoot.Model).First(item => entries.ContainsKey(Draws(model)[item.Part].Material + "_c"));
        var partMaterial = Draws(model)[piece.Part].Material;
        var colourTexture = CompiledMaterialEdit.Textures(entries[partMaterial + "_c"].ToArray(), partMaterial + "_c")["g_tColor"];
        var (_, width, height, _) = InPlaceTexture.ReadStored(entries[colourTexture + "_c"].ToArray(), colourTexture + "_c");
        var mask = PartMasks.Mask(model, ModTestRoot.Model, piece.Index, width, height);
        var pak = Path.Combine(root.Game, "citadel", "pak01_dir.vpk");
        File.Delete(pak);
        await S2ModKit.Adapters.Vpk.CompactVpkArchiveIO.WriteAsync(pak, entries, token);

        await root.Catalog().BuildEditsAsync(null, ModTestRoot.Model, new HashSet<int>(),
            [new PartRecolor(piece.Index, colourTexture, mask, width, height, new S2ModKit.Application.ColorAdjustment(HueShiftDegrees: 120), partMaterial)], null, null, null, null, null, null, token);

        var built = Assert.Single(await root.Catalog().LoadAsync(token), mod => mod.Loaded);
        using var output = new Package();
        output.Read(built.Files[0]);
        output.ReadEntry(output.FindEntry(ModTestRoot.Model)!, out var builtModel);
        var split = Draws(builtModel)[piece.Part + 1];
        Assert.EndsWith("_s2mk_own1.vmat", split.Material, StringComparison.Ordinal);
        Assert.Null(output.FindEntry(colourTexture + "_c"));                    // the shared texture is not written
        output.ReadEntry(output.FindEntry(split.Material + "_c")!, out var newMaterial);
        Assert.Null(CompiledMaterialEdit.Opacity(newMaterial, split.Material + "_c"));   // recolored, not see-through
        Assert.NotNull(output.FindEntry(CompiledMaterialEdit.Textures(newMaterial, split.Material + "_c")["g_tColor"] + "_c"));
    }

    /// <summary>Live check: two pieces of one part each get their own draw call and material (one recolored, one see-through), and every triangle is
    /// still drawn once.</summary>
    [Fact]
    public async Task TwoPiecesOfOnePartGetTheirOwnDrawCalls()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game) || !File.Exists(Environment.GetEnvironmentVariable("S2MODKIT_MESHOPTIMIZER_PATH")))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME and S2MODKIT_MESHOPTIMIZER_PATH to enable this proprietary local check.");
            return;
        }

        var token = TestContext.Current.CancellationToken;
        using var root = new ModTestRoot();
        var entries = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        byte[] model;
        using (var real = new Package())
        {
            real.Read(Path.Combine(game, "citadel", "pak01_dir.vpk"));
            byte[] Copy(string path)
            {
                real.ReadEntry(real.FindEntry(path)!, out var bytes);
                entries[path] = bytes;
                return bytes;
            }

            model = Copy(ModTestRoot.Model);
            foreach (var material in CompiledMaterialEdit.ModelMaterials(model, ModTestRoot.Model).Where(material => real.FindEntry(material + "_c") is not null))
            {
                if (CompiledMaterialEdit.Textures(Copy(material + "_c"), material + "_c").GetValueOrDefault("g_tColor") is { } colour && real.FindEntry(colour + "_c") is not null)
                {
                    Copy(colour + "_c");
                }
            }
        }

        var before = Draws(model);
        var pieces = PartMasks.Pieces(model, ModTestRoot.Model).GroupBy(item => item.Part).First(group => group.Count() >= 2 && entries.ContainsKey(before[group.Key].Material + "_c")).Take(2).ToArray();
        var partMaterial = before[pieces[0].Part].Material;
        var colourTexture = CompiledMaterialEdit.Textures(entries[partMaterial + "_c"].ToArray(), partMaterial + "_c")["g_tColor"];
        var (_, width, height, _) = InPlaceTexture.ReadStored(entries[colourTexture + "_c"].ToArray(), colourTexture + "_c");
        var pak = Path.Combine(root.Game, "citadel", "pak01_dir.vpk");
        File.Delete(pak);
        await S2ModKit.Adapters.Vpk.CompactVpkArchiveIO.WriteAsync(pak, entries, token);

        await root.Catalog().BuildEditsAsync(null, ModTestRoot.Model, new HashSet<int>(),
            [new PartRecolor(pieces[0].Index, colourTexture, PartMasks.Mask(model, ModTestRoot.Model, pieces[0].Index, width, height), width, height, new S2ModKit.Application.ColorAdjustment(HueShiftDegrees: 90), partMaterial)],
            null, null, null, null, [new PartOpacity(pieces[1].Index, partMaterial, 0.4f)], null, token);

        var built = Assert.Single(await root.Catalog().LoadAsync(token), mod => mod.Loaded);
        using var output = new Package();
        output.Read(built.Files[0]);
        output.ReadEntry(output.FindEntry(ModTestRoot.Model)!, out var builtModel);
        var after = Draws(builtModel);
        Assert.Equal(before.Count + 2, after.Count);
        var part = pieces[0].Part;
        Assert.Equal(partMaterial, after[part].Material);
        Assert.Equal([pieces[0].Triangles * 3, pieces[1].Triangles * 3], new[] { after[part + 1].Count, after[part + 2].Count });
        Assert.All(new[] { after[part + 1].Material, after[part + 2].Material }, material => Assert.Contains("_s2mk_own", material, StringComparison.Ordinal));
        Assert.NotEqual(after[part + 1].Material, after[part + 2].Material);
        Assert.Equal(before[part].Triangles.Order(), after[part].Triangles.Concat(after[part + 1].Triangles).Concat(after[part + 2].Triangles).Order());
    }

    private static byte[] ReadGame(string game, string path)
    {
        foreach (var archive in new[] { "citadel", "core" })
        {
            using var package = new Package();
            package.Read(Path.Combine(game, archive, "pak01_dir.vpk"));
            if (package.FindEntry(path) is { } entry)
            {
                package.ReadEntry(entry, out var bytes);
                return bytes;
            }
        }

        throw new FileNotFoundException(path);
    }

    private static (ValveResourceFormat.ResourceTypes.Material Material, List<string> References) Read(byte[] compiled, string path)
    {
        using var resource = new ValveResourceFormat.Resource { FileName = path };
        resource.Read(new MemoryStream(compiled));
        return ((ValveResourceFormat.ResourceTypes.Material)resource.DataBlock!, [.. resource.ExternalReferences!.ResourceRefInfoList.Select(info => info.Name)]);
    }

    /// <summary>The bytes of the material's dynamic glow scale (null: none).</summary>
    private static byte[]? PulseOf(byte[] compiled, string path)
    {
        var (material, _) = Read(compiled, path);
        var entry = material.Data["m_dynamicParams"].Values.FirstOrDefault(item => item["m_name"].ToString(System.Globalization.CultureInfo.InvariantCulture) == "g_flSelfIllumScale1");
        return entry is null ? null : entry["m_value"].AsBlob();
    }

    /// <summary>The level-0 draw calls in order: material, index count and their triangles (sorted vertex triples).</summary>
    private static List<(string Material, int Count, List<(int, int, int)> Triangles)> Draws(byte[] compiled)
    {
        using var resource = new ValveResourceFormat.Resource { FileName = ModTestRoot.Model };
        resource.Read(new MemoryStream(compiled));
        var list = new List<(string, int, List<(int, int, int)>)>();
        foreach (var (mesh, _, _, _) in ((ValveResourceFormat.ResourceTypes.Model)resource.DataBlock!).GetEmbeddedMeshesForLod(0))
        {
            foreach (var sceneObject in mesh.Data["m_sceneObjects"].Values)
            {
                foreach (var draw in sceneObject["m_drawCalls"].Values)
                {
                    var buffer = mesh.VBIB.IndexBuffers[(int)draw["m_indexBuffer"]["m_hBuffer"].ToInt64(System.Globalization.CultureInfo.InvariantCulture)];
                    var (start, count) = ((int)draw["m_nStartIndex"].ToInt64(System.Globalization.CultureInfo.InvariantCulture), (int)draw["m_nIndexCount"].ToInt64(System.Globalization.CultureInfo.InvariantCulture));
                    var size = (int)buffer.ElementSizeInBytes;
                    int At(int i) => size == 2 ? BitConverter.ToUInt16(buffer.Data, (start + i) * 2) : BitConverter.ToInt32(buffer.Data, (start + i) * 4);
                    var triangles = Enumerable.Range(0, count / 3).Select(t => new[] { At(t * 3), At((t * 3) + 1), At((t * 3) + 2) }.Order().ToArray()).Select(v => (v[0], v[1], v[2])).ToList();
                    list.Add((draw["m_material"].ToString(System.Globalization.CultureInfo.InvariantCulture), count, triangles));
                }
            }
        }

        return list;
    }
}
