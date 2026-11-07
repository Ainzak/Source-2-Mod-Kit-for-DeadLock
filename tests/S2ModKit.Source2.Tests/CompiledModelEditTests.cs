using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Source2.Tests;

public sealed class CompiledModelEditTests
{
    [Fact]
    public void OnlyAModelCanHaveItsPartsHidden()
    {
        var texture = InPlaceTextureTests.Texture(VTexFormat.RGBA8888, 4, 4, 1, _ => new byte[4 * 4 * 4]);

        var error = Assert.Throws<S2ModKitException>(() => CompiledModelEdit.HideDrawCalls(texture, "materials/t.vtex_c", new HashSet<int> { 0 })).Error;

        Assert.Equal("MODEL_EDIT_NOT_A_MODEL", error.Code);
    }

    [Fact]
    public void BlocksCannotBeReplacedWhenDataFollowsThem()
    {
        var texture = InPlaceTextureTests.Texture(VTexFormat.RGBA8888, 4, 4, 1, _ => new byte[4 * 4 * 4]);
        using var resource = new Resource { FileName = "materials/t.vtex_c" };
        resource.Read(new MemoryStream(texture));

        var error = Assert.Throws<S2ModKitException>(() => CompiledModelEdit.ReplaceBlocks(texture, resource, new Dictionary<int, byte[]>())).Error;

        Assert.Equal("MODEL_EDIT_LAYOUT", error.Code);                     // a texture's pixels follow its blocks
    }

    [Fact]
    public void HidingAPartEmptiesOnlyItsDrawCallAndKeepsTheOtherBlocks()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to the Deadlock game folder to enable this proprietary local check.");
        }

        const string Model = "models/heroes_wip/wraith/wraith.vmdl_c";
        using var package = new Package();
        package.Read(Path.Combine(game, "citadel", "pak01_dir.vpk"));
        package.ReadEntry(package.FindEntry(Model)!, out var original);

        var edited = CompiledModelEdit.HideDrawCalls(original, Model, new HashSet<int> { 4 });

        using var before = new Resource { FileName = Model };
        before.Read(new MemoryStream(original));
        using var after = new Resource { FileName = Model };
        after.Read(new MemoryStream(edited));
        var counts = (Resource resource) => ((Model)resource.DataBlock!).GetEmbeddedMeshesForLod(0)
            .SelectMany(mesh => mesh.Mesh.Data["m_sceneObjects"].Values.SelectMany(sceneObject => sceneObject["m_drawCalls"].Values))
            .Select(draw => draw["m_nIndexCount"].ToInt64(null)).ToArray();
        var expected = counts(before);
        expected[4] = 0;
        Assert.Equal(expected, counts(after));
        var changedMesh = ((Model)before.DataBlock!).GetEmbeddedMeshesForLod(0).ElementAt(1).Mesh;   // part 4 is the arms mesh
        for (var i = 0; i < before.Blocks.Count; i++)
        {
            if (!ReferenceEquals(before.Blocks[i], changedMesh))
            {
                Assert.Equal(original.AsSpan((int)before.Blocks[i].Offset, (int)before.Blocks[i].Size).ToArray(),
                    edited.AsSpan((int)after.Blocks[i].Offset, (int)after.Blocks[i].Size).ToArray());   // every other block byte for byte
            }
        }
    }
}
