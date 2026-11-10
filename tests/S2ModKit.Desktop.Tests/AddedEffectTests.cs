using System.Numerics;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Desktop.Tests;

/// <summary>
/// Local checks of always-on added effects (<c>add_model_effects@1</c>) on the game's own files (proprietary inputs, nothing
/// installed): S2MODKIT_TEST_GAME is the Deadlock game folder.
/// </summary>
public sealed class AddedEffectTests
{
    private const string Geist = "models/heroes_wip/geist/geist.vmdl";          // lists no effect of its own
    private const string Celeste = "models/heroes_wip/unicorn/unicorn.vmdl";    // lists her horn's effect
    private const string Flame = "particles/abilities/inferno/inferno_hand_ambient_flame.vpcf";

    private static Func<string, byte[]>? Game()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game))
        {
            return null;
        }

        return path =>
        {
            using var package = new Package();
            package.Read(Path.Combine(game, "citadel", "pak01_dir.vpk"));
            package.ReadEntry(package.FindEntry(path + "_c")!, out var bytes);
            return bytes;
        };
    }

    private static Resource Read(byte[] bytes, string path)
    {
        var resource = new Resource { FileName = path + "_c" };
        resource.Read(new MemoryStream(bytes));
        return resource;
    }

    [Fact]
    public void StoredReferenceIdsAreTheHashOfTheLowerCasePath()
    {
        if (Game() is not { } game)
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this proprietary local check.");
            return;
        }

        using var model = Read(game(Celeste), Celeste);
        Assert.All(model.ExternalReferences!.ResourceRefInfoList, reference => Assert.Equal(reference.Id, CompiledAddedEffects.ResourceId(reference.Name)));
    }

    [Theory]
    [InlineData(Geist, 0)]
    [InlineData(Celeste, 1)]
    public void AModelGetsTheAttachmentTheListedEffectAndItsReferenceAndNothingElseChanges(string path, int listedBefore)
    {
        if (Game() is not { } game)
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this proprietary local check.");
            return;
        }

        var original = game(path);
        const string Wrapper = "particles/s2modkit/test/added_1.vpcf";
        Assert.Equal(listedBefore, CompiledAddedEffects.ModelEffects(original, path + "_c").Count);

        var edited = CompiledAddedEffects.AddToModel(original, path + "_c", [new EffectAttachment("s2mk_fx_1", "head", new Vector3(1, 2, 3))], [Wrapper]);

        Assert.Equal(Wrapper, CompiledAddedEffects.ModelEffects(edited, path + "_c")[^1]);
        Assert.Equal(listedBefore + 1, CompiledAddedEffects.ModelEffects(edited, path + "_c").Count);
        Assert.Contains(("s2mk_fx_1", "head", new Vector3(1, 2, 3)), CompiledAddedEffects.Attachments(edited, path + "_c"));
        Assert.Equal(CompiledAddedEffects.Attachments(original, path + "_c").Count + 1, CompiledAddedEffects.Attachments(edited, path + "_c").Count);

        using var before = Read(original, path);
        using var after = Read(edited, path);
        Assert.Equal([.. before.ExternalReferences!.ResourceRefInfoList.Select(reference => reference.Name), Wrapper], after.ExternalReferences!.ResourceRefInfoList.Select(reference => reference.Name));
        Assert.Equal(CompiledAddedEffects.ResourceId(Wrapper), after.ExternalReferences.ResourceRefInfoList[^1].Id);
        Assert.Equal(before.Blocks.Count, after.Blocks.Count);
        for (var i = 0; i < before.Blocks.Count; i++)
        {
            if (before.Blocks[i] is Mesh or Model || before.Blocks[i].Type == BlockType.RERL)
            {
                continue;                                                   // attachments, the effect list, the references
            }

            Assert.True(original.AsSpan((int)before.Blocks[i].Offset, (int)before.Blocks[i].Size).SequenceEqual(edited.AsSpan((int)after.Blocks[i].Offset, (int)after.Blocks[i].Size)),
                $"block {i} ({before.Blocks[i].Type}) changed");
        }

        // every level of detail knows the point; the same edit again changes nothing more
        Assert.All(after.Blocks.OfType<Mesh>().Where(mesh => mesh.Data["m_attachments"].Count > 0), mesh =>
            Assert.Single(mesh.Data["m_attachments"].Values, entry => entry["key"].ToString(System.Globalization.CultureInfo.InvariantCulture) == "s2mk_fx_1"));
        var again = CompiledAddedEffects.AddToModel(edited, path + "_c", [new EffectAttachment("s2mk_fx_1", "head", new Vector3(1, 2, 3))], [Wrapper]);
        Assert.Equal(CompiledAddedEffects.ModelEffects(edited, path + "_c"), CompiledAddedEffects.ModelEffects(again, path + "_c"));
        Assert.Equal(CompiledAddedEffects.Attachments(edited, path + "_c").Count, CompiledAddedEffects.Attachments(again, path + "_c").Count);
    }

    [Fact]
    public void ABuildNumbersItsEffectsAfterTheOnesAnEarlierBuildLeft()
    {
        if (Game() is not { } game)
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this proprietary local check.");
            return;
        }

        var template = game(CompiledAddedEffects.TemplateEffect);
        var first = CompiledAddedEffects.Build(game(Geist), Geist, template, [new AddedEffect(Flame, "head", Vector3.Zero), new AddedEffect(Flame, null, Vector3.Zero)]);

        Assert.Equal(["models/heroes_wip/geist/geist.vmdl_c", "particles/s2modkit/geist/added_1.vpcf_c", "particles/s2modkit/geist/added_2.vpcf_c"], first.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["particles/s2modkit/geist/added_1.vpcf", "particles/s2modkit/geist/added_2.vpcf"], CompiledAddedEffects.ModelEffects(first[Geist + "_c"], Geist + "_c"));
        Assert.Single(CompiledAddedEffects.Attachments(first[Geist + "_c"], Geist + "_c"), attachment => attachment.Name.StartsWith("s2mk_fx_", StringComparison.Ordinal));   // the second follows the hero

        var second = CompiledAddedEffects.Build(first[Geist + "_c"], Geist, template, [new AddedEffect(Flame, "hand_R", new Vector3(0, 0, 4))]);
        Assert.Contains("particles/s2modkit/geist/added_3.vpcf_c", second.Keys);
        Assert.Contains(("s2mk_fx_3", "hand_R", new Vector3(0, 0, 4)), CompiledAddedEffects.Attachments(second[Geist + "_c"], Geist + "_c"));
    }

    [Fact]
    public async Task BuildEditedModAddsTheEffectsAndReapplyBuildsThemAgain()
    {
        if (Game() is not { } game)
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this proprietary local check.");
            return;
        }

        var token = TestContext.Current.CancellationToken;
        using var root = new ModTestRoot();
        var pak = Path.Combine(root.Game, "citadel", "pak01_dir.vpk");
        File.Delete(pak);
        await S2ModKit.Adapters.Vpk.CompactVpkArchiveIO.WriteAsync(pak, new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)
        {
            [Geist + "_c"] = game(Geist),
            [CompiledAddedEffects.TemplateEffect + "_c"] = game(CompiledAddedEffects.TemplateEffect),
            [Flame + "_c"] = game(Flame),
        }, token);
        AddedEffect[] added = [new(Flame, "head", new Vector3(0, 2, 0)), new(Flame, null, Vector3.Zero)];

        await root.Catalog().BuildEditsAsync(null, Geist, new HashSet<int>(), [], null, null, null, added, token);

        var built = Assert.Single(await root.Catalog().LoadAsync(token), mod => mod.Loaded);
        Assert.Equal(2, Shipped(built.Files[0]).Count(path => path.StartsWith("particles/s2modkit/geist/added_", StringComparison.Ordinal)));
        Assert.Equal(2, EditRecipe.Of(built)!.Added!.Length);
        Assert.Equal("head", EditRecipe.Of(built)!.Added![0].Bone);

        var missing = await Assert.ThrowsAsync<S2ModKitException>(() => root.Catalog().BuildEditsAsync(null, Geist, new HashSet<int>(), [], null, null, null, [new AddedEffect("particles/no/such.vpcf", null, Vector3.Zero)], token));
        Assert.Equal("ADDED_EFFECT_MISSING", missing.Error.Code);

        await root.Catalog().ReapplyAsync(built, await root.Catalog().LoadAsync(token), token);
        var again = Assert.Single(await root.Catalog().LoadAsync(token), mod => mod.Loaded);
        using var package = new Package();
        package.Read(again.Files[0]);
        package.ReadEntry(package.FindEntry(Geist + "_c")!, out var model);
        Assert.Equal(["particles/s2modkit/geist/added_1.vpcf", "particles/s2modkit/geist/added_2.vpcf"], CompiledAddedEffects.ModelEffects(model, Geist + "_c"));

        static string[] Shipped(string archive)
        {
            using var package = new Package();
            package.Read(archive);
            return [.. package.Entries!.SelectMany(group => group.Value).Select(entry => entry.GetFullPath())];
        }
    }

    [Fact]
    public void AnUnknownBoneIsRefused()
    {
        if (Game() is not { } game)
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this proprietary local check.");
            return;
        }

        var error = Assert.Throws<S2ModKitException>(() => CompiledAddedEffects.AddToModel(game(Geist), Geist + "_c", [new EffectAttachment("s2mk_fx_1", "no_such_bone", Vector3.Zero)], []));
        Assert.Equal("ADDED_EFFECT_BONE", error.Error.Code);
    }

    [Theory]
    [InlineData("s2mk_fx_1")]
    [InlineData(null)]
    public void TheWrapperPlaysTheChosenEffectAtTheAttachmentOrTheOrigin(string? attachment)
    {
        if (Game() is not { } game)
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this proprietary local check.");
            return;
        }

        var wrapper = CompiledAddedEffects.Wrapper(game(CompiledAddedEffects.TemplateEffect), Flame, attachment);

        using var resource = Read(wrapper, "particles/s2modkit/test/added_1.vpcf");
        var root = ((ParticleSystem)resource.DataBlock!).Data;
        Assert.Equal([Flame], resource.ExternalReferences!.ResourceRefInfoList.Select(reference => reference.Name));
        Assert.Equal(CompiledAddedEffects.ResourceId(Flame), resource.ExternalReferences.ResourceRefInfoList[0].Id);
        Assert.Equal(Flame, Assert.Single(root["m_Children"].Values)["m_ChildRef"].ToString(System.Globalization.CultureInfo.InvariantCulture));
        var configuration = Assert.Single(root["m_controlPointConfigurations"].Values);
        Assert.Equal(CompiledAddedEffects.Config, configuration["m_name"].ToString(System.Globalization.CultureInfo.InvariantCulture));
        var drivers = configuration["m_drivers"].Values.ToArray();
        if (attachment is null)
        {
            Assert.False(Assert.Single(drivers).ContainsKey("m_attachmentName"));
            Assert.Empty(root["m_PreEmissionOperators"].Values);
        }
        else
        {
            Assert.Equal(2, drivers.Length);
            Assert.All(drivers, driver => Assert.Equal(attachment, driver["m_attachmentName"].ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.All(drivers, driver => Assert.Equal("PATTACH_POINT_FOLLOW", driver["m_iAttachType"].ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.False(drivers[0].ContainsKey("m_iControlPoint"));         // control point 0
            Assert.Equal(1, drivers[1]["m_iControlPoint"].ToInt32(System.Globalization.CultureInfo.InvariantCulture));
            var operation = Assert.Single(root["m_PreEmissionOperators"].Values);
            Assert.Equal(0, operation["m_nCP1"].ToInt32(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(1, operation["m_transformInput"]["m_nControlPoint"].ToInt32(System.Globalization.CultureInfo.InvariantCulture));
            Assert.All(operation["m_vecCP1Pos"]["m_vLiteralValue"].Values, value => Assert.Equal(0f, value.ToSingle(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }
}
