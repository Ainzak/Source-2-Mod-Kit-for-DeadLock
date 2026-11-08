using System.Numerics;
using S2ModKit.Application;
using S2ModKit.Desktop;
using ValveResourceFormat;

namespace S2ModKit.Desktop.Tests;

/// <summary>
/// Local checks of reshaped-part builds through the CLI's checked operations (proprietary inputs, nothing installed):
/// S2MODKIT_TEST_GAME is the Deadlock game folder; S2MODKIT_TEST_SHAPE_MOD optionally names a mod as
/// "&lt;vpk&gt;|&lt;model.vmdl&gt;|&lt;material.vmat&gt;" for the archive import route.
/// </summary>
public sealed class ShapeBuildTests
{
    private static string? Game()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        return string.IsNullOrWhiteSpace(game) || !File.Exists(Path.Combine(game, "citadel", "pak01_dir.vpk")) ? null : game;
    }

    private static PartShapeEdit Edit(string material) => new(0, material,
        new PartShape(new Vector3(1.5f, 1.5f, 1.5f), new Vector3(0, 0, 20), new Vector3(0, 0, 3)), new Vector3(-5, -5, 30), new Vector3(5, 5, 50), 60f, ShapeLevel.Full);

    private static void AssertReopens(byte[] model, string logical)
    {
        using var resource = new Resource { FileName = logical };
        resource.Read(new MemoryStream(model));
        Assert.NotNull(resource.DataBlock);
    }

    [Fact]
    public async Task TwoBaseHeroPartsAreReshapedInOneRecipeThroughTheCheckedOperations()
    {
        var game = Game();
        if (game is null)
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to the Deadlock game folder to enable this proprietary local check.");
            return;
        }

        using var root = new ModTestRoot();
        var settings = new AppSettings(game, root.Data);
        const string Model = "models/heroes_wip/vampirebat/vampirebat.vmdl";
        var gun = Edit("models/heroes_wip/vampirebat/materials/mina_gun.vmat");                       // per-axis, rotation and move
        var umbrella = new PartShapeEdit(1, "models/heroes_wip/vampirebat/materials/mina_umbrella.vmat",   // only an even size and a move
            new PartShape(new Vector3(1.3f, 1.3f, 1.3f), Vector3.Zero, new Vector3(0, 0, 2)), Vector3.Zero, Vector3.One, 80f, ShapeLevel.EvenSizeAndMove);
        var (bytes, project) = await ShapeModelBuilder.BuildAsync(settings, null, Model, [gun, umbrella], TestContext.Current.CancellationToken);

        Assert.Equal(2, Directory.EnumerateDirectories(project, "round-*").Count());               // one checked build per part, chained
        AssertReopens(bytes, Model + "_c");
    }

    [Fact]
    public async Task AModPartIsReshapedFromACopyOfItsArchive()
    {
        var spec = Environment.GetEnvironmentVariable("S2MODKIT_TEST_SHAPE_MOD")?.Split('|');
        var game = Game();
        if (game is null || spec is not { Length: 3 } || !File.Exists(spec[0]))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME and S2MODKIT_TEST_SHAPE_MOD (vpk|model|material) to enable this proprietary local check.");
            return;
        }

        using var root = new ModTestRoot();
        var settings = new AppSettings(game, root.Data);
        var mod = new ModEntry("test", "Test mod", ModSource.Other, true, [spec[0]], [null], new Dictionary<string, int>(), [], [spec[1]], null);
        var (bytes, _) = await ShapeModelBuilder.BuildAsync(settings, mod, spec[1], [Edit(spec[2])], TestContext.Current.CancellationToken);

        AssertReopens(bytes, spec[1] + "_c");
    }
}
