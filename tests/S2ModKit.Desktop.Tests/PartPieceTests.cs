using S2ModKit.Adapters.Preview;
using SteamDatabase.ValvePak;

namespace S2ModKit.Desktop.Tests;

public sealed class PartPieceTests
{
    [Fact]
    public void ExcludeTakesAPiecesTexelsOutOfItsPart()
    {
        var part = new[] { 1f, 1f, 1f, 0f };
        PartMasks.Exclude(part, [[0f, 1f, 0f, 1f]]);

        Assert.Equal([1f, 0f, 1f, 0f], part);
    }

    /// <summary>Live check (S2MODKIT_TEST_GAME): some hero parts are several separate meshes; their pieces are found in a fixed order,
    /// cover texels of their part only, and a part's mask leaves out a piece that has its own colour.</summary>
    [Fact]
    public void PartsMadeOfSeparateMeshesSplitIntoPieces()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this live check.");
            return;
        }

        using var package = new Package();
        package.Read(Path.Combine(game, "citadel", "pak01_dir.vpk"));
        var found = 0;
        foreach (var hero in Hero.Catalogue.Take(12))
        {
            var path = hero.Model + "_c";
            if (package.FindEntry(path) is not { } entry)
            {
                continue;
            }

            package.ReadEntry(entry, out var model);
            var pieces = PartMasks.Pieces(model, path);
            Assert.Equal(pieces, PartMasks.Pieces(model, path));                    // the same pieces each time: recipes keep their index
            if (pieces.Count == 0)
            {
                continue;
            }

            var piece = pieces[0];
            found++;
            Assert.True(pieces.Count(other => other.Part == piece.Part) >= 2);
            var whole = PartMasks.Mask(model, path, piece.Part, 256, 256);
            var own = PartMasks.Mask(model, path, piece.Index, 256, 256);
            Assert.Contains(own, value => value > 0);
            Assert.All(Enumerable.Range(0, own.Length).Where(i => own[i] > 0), i => Assert.True(whole[i] > 0, $"{hero.Name}: piece texel {i} outside its part"));
            var rest = PartMasks.Mask(model, path, piece.Part, 256, 256, recoloredPieces: [piece.Index]);
            Assert.All(Enumerable.Range(0, own.Length).Where(i => own[i] > 0), i => Assert.Equal(0f, rest[i]));
        }

        Assert.True(found > 0, "no hero part is made of separate meshes");
    }
}
