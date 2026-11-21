using System.Numerics;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using SteamDatabase.ValvePak;

namespace S2ModKit.Desktop.Tests;

public sealed class MaterialSettingsTests
{
    private const string Gun = "models/heroes_staging/astro/materials/astro_gun.vmat_c";
    private const string White = "materials/default/default_mask_tga_344101f8.vtex";
    private const string Sheen = "models/heroes_staging/atlas_detective/materials/body_vmat_g_tsheen_e083ae60.vtex";

    /// <summary>Live check (S2MODKIT_TEST_GAME): a glass finish (new feature, numbers, colour and textures the gun material lacks) and a
    /// pearl finish read back with every value, the new textures join the list of used files, and a second write replaces a value
    /// instead of adding it twice.</summary>
    [Fact]
    public void FinishesAreAddedOnceAndReadBack()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this proprietary local check.");
            return;
        }

        var original = ReadGame(game, Gun);
        var glass = new MaterialSettings(
            Ints: new Dictionary<string, int> { ["F_GLASS"] = 1 },
            Floats: new Dictionary<string, float> { ["g_flCloakRefractAmount"] = 0.06f },
            Vectors: new Dictionary<string, Vector4> { ["g_vColorTint1"] = new(0.75f, 0.9f, 1f, 1f) },
            Textures: new Dictionary<string, string> { ["g_tGlass"] = White });

        var once = CompiledMaterialSettings.Apply(original, Gun, glass);
        var twice = CompiledMaterialSettings.Apply(once, Gun, glass with { Floats = new Dictionary<string, float> { ["g_flCloakRefractAmount"] = 0.1f } });
        var pearl = CompiledMaterialSettings.Apply(original, Gun, new MaterialSettings(
            Ints: new Dictionary<string, int> { ["F_SHEEN"] = 1 }, Textures: new Dictionary<string, string> { ["g_tSheen"] = Sheen }));

        var (material, references) = Read(twice);
        Assert.Equal("pbr.vfx", material.ShaderName);
        Assert.Equal(1, material.IntParams["F_GLASS"]);
        Assert.Equal(0.1f, material.FloatParams["g_flCloakRefractAmount"], 4);
        Assert.Equal(new Vector4(0.75f, 0.9f, 1f, 1f), material.VectorParams["g_vColorTint1"]);
        Assert.Equal(White, material.TextureParams["g_tGlass"]);
        Assert.Single(material.Data["m_floatParams"].Values, item => item["m_name"].ToString(System.Globalization.CultureInfo.InvariantCulture) == "g_flCloakRefractAmount");
        Assert.Contains(White, references);
        var (pearly, pearlReferences) = Read(pearl);
        Assert.Equal(1, pearly.IntParams["F_SHEEN"]);
        Assert.Contains(Sheen, pearlReferences);
    }

    /// <summary>Live check (S2MODKIT_TEST_GAME): a number setting the material keeps as a whole number (SDK-compiled materials do so for
    /// values written without a decimal point, and the game reads that copy) is moved to the number list, for finishes and glows.</summary>
    [Fact]
    public void ANumberKeptAsAWholeNumberIsMovedNotDuplicated()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this proprietary local check.");
            return;
        }

        var sdkLike = CompiledMaterialSettings.Apply(ReadGame(game, Gun), Gun, new MaterialSettings(
            Ints: new Dictionary<string, int> { ["g_flSelfIllumAlbedoFactor1"] = 1, ["g_flSelfIllumFresnelMaskExponent"] = 5 }));
        Assert.Equal(1, Read(sdkLike).Material.IntParams["g_flSelfIllumAlbedoFactor1"]);

        var finished = CompiledMaterialSettings.Apply(sdkLike, Gun, new MaterialSettings(Floats: new Dictionary<string, float> { ["g_flSelfIllumAlbedoFactor1"] = 0f }));
        var glowing = CompiledMaterialLook.Apply(sdkLike, Gun, new MaterialLook(1f, 0.5f, 0.9f, 0.6f, 0f, 0f, true), White);

        foreach (var bytes in new[] { finished, glowing })
        {
            var material = Read(bytes).Material;
            Assert.False(material.IntParams.ContainsKey("g_flSelfIllumAlbedoFactor1"));
            Assert.Equal(0f, material.FloatParams["g_flSelfIllumAlbedoFactor1"]);
        }

        Assert.False(Read(glowing).Material.IntParams.ContainsKey("g_flSelfIllumFresnelMaskExponent"));   // the glow's edges set it too
        Assert.Equal(5, Read(finished).Material.IntParams["g_flSelfIllumFresnelMaskExponent"]);           // a setting not written stays
    }

    /// <summary>The flipbook expression decompiles (with ValveResourceFormat's reader of the game's expression code) to the frame
    /// formula: whole frames from the clock, each frame's middle measured from the texture's middle.</summary>
    [Fact]
    public void TheFlipbookExpressionReadsBackAsTheFrameFormula()
    {
        var program = CompiledMaterialSettings.FlipbookOffsetExpression(8, 4f);

        var text = new ValveResourceFormat.Serialization.VfxEval.VfxEval(program, false, [], null!).DynamicExpressionResult;
        var compact = string.Concat(text.Where(character => !char.IsWhiteSpace(character)));

        Assert.Equal("returnfloat2((floor(frac(time()*.5)*8)+.5)/8-.5,0);", compact);
        var grid = new ValveResourceFormat.Serialization.VfxEval.VfxEval(CompiledMaterialSettings.FlipbookOffsetExpression(50, 20f, 16, 4), false, [], null!).DynamicExpressionResult;
        Assert.Equal("returnfloat2((frac(floor(frac(time()*.4)*50)/16)*16+.5)/16-.5,(floor(floor(frac(time()*.4)*50)/16)+.5)/4-.5);",
            string.Concat(grid.Where(character => !char.IsWhiteSpace(character))));
        // a surface whose u runs 0 to 1.03125 (the hideout portrait): cells fitted to that span, the scale to match
        var fitted = new ValveResourceFormat.Serialization.VfxEval.VfxEval(CompiledMaterialSettings.FlipbookOffsetExpression(8, 4f, 8, 1, new Vector4(0f, 0f, 1.03125f, 1f)), false, [], null!).DynamicExpressionResult;
        Assert.Contains("+.4848", string.Concat(fitted.Where(character => !char.IsWhiteSpace(character))), StringComparison.Ordinal);
        Assert.Equal(1f / (8 * 1.03125f), CompiledMaterialSettings.FlipbookScale(8, 1, new Vector4(0f, 0f, 1.03125f, 1f)).X, 6);
        Assert.Throws<S2ModKitException>(() => CompiledMaterialSettings.FlipbookScale(8, 1, new Vector4(0f, 0f, 0f, 1f)));
        Assert.Throws<S2ModKitException>(() => CompiledMaterialSettings.FlipbookOffsetExpression(1, 4f));
        Assert.Throws<S2ModKitException>(() => CompiledMaterialSettings.FlipbookOffsetExpression(8, 0f));
        Assert.Throws<S2ModKitException>(() => CompiledMaterialSettings.FlipbookOffsetExpression(50, 20f, 16, 3));   // 48 cells for 50 frames
    }

    /// <summary>Live check (S2MODKIT_TEST_GAME): the hideout portrait (baked map geometry, shader pbr.vfx) takes a flipbook: texture
    /// transforms, an eighth of the texture's width and the frame expression on the offset, read back exactly.</summary>
    [Fact]
    public void APaintingTakesAFlipbook()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this proprietary local check.");
            return;
        }

        const string Portrait = "models/hideout/materials/hideout_portrait_large.vmat_c";
        var program = CompiledMaterialSettings.FlipbookOffsetExpression(8, 1f);
        var edited = CompiledMaterialSettings.Apply(ReadGame(game, Portrait), Portrait, new MaterialSettings(
            Ints: new Dictionary<string, int> { ["F_ENABLE_TEXTURE_TRANSFORMS"] = 1 },
            Vectors: new Dictionary<string, Vector4> { ["g_vAlbedoTexcoordScale1"] = new(0.125f, 1f, 0f, 0f) },
            Expressions: new Dictionary<string, byte[]> { ["g_vAlbedoTexcoordOffset1"] = program }));

        using var resource = new ValveResourceFormat.Resource { FileName = Portrait };
        resource.Read(new MemoryStream(edited));
        var material = (ValveResourceFormat.ResourceTypes.Material)resource.DataBlock!;
        Assert.Equal(1, material.IntParams["F_ENABLE_TEXTURE_TRANSFORMS"]);
        var entry = Assert.Single(material.Data["m_dynamicParams"].Values, item => item["m_name"].ToString(System.Globalization.CultureInfo.InvariantCulture) == "g_vAlbedoTexcoordOffset1");
        Assert.Equal(program, entry["m_value"].AsBlob());
    }

    [Fact]
    public void AnythingButAPbrMaterialOrABadValueIsRefused()
    {
        Assert.ThrowsAny<Exception>(() => CompiledMaterialSettings.Apply([1, 2, 3], Gun, new MaterialSettings()));

        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game))
        {
            return;
        }

        var original = ReadGame(game, Gun);
        Assert.Throws<S2ModKitException>(() => CompiledMaterialSettings.Apply(original, Gun, new MaterialSettings(Floats: new Dictionary<string, float> { ["g_flCloakRefractAmount"] = float.NaN })));
        Assert.Throws<S2ModKitException>(() => CompiledMaterialSettings.Apply(original, Gun, new MaterialSettings(Textures: new Dictionary<string, string> { ["g_tGlass"] = "materials/x.png" })));
    }

    private static byte[] ReadGame(string game, string path)
    {
        using var package = new Package();
        package.Read(Path.Combine(game, "citadel", "pak01_dir.vpk"));
        package.ReadEntry(package.FindEntry(path)!, out var bytes);
        return bytes;
    }

    private static (ValveResourceFormat.ResourceTypes.Material Material, List<string> References) Read(byte[] compiled)
    {
        using var resource = new ValveResourceFormat.Resource { FileName = Gun };
        resource.Read(new MemoryStream(compiled));
        return ((ValveResourceFormat.ResourceTypes.Material)resource.DataBlock!, [.. resource.ExternalReferences!.ResourceRefInfoList.Select(info => info.Name)]);
    }
}
