using S2ModKit.Desktop;

namespace S2ModKit.Desktop.Tests;

public sealed class GameBananaTests
{
    [Fact]
    public void DescriptionsBecomeReadableText()
    {
        var text = GameBananaClient.PlainText("<p>Hello&nbsp;<b>world</b></p><br><ul><li>One</li><li>Two &amp; three</li></ul><p></p><p></p><p>End</p>");
        Assert.Equal("Hello world\n\nOne\nTwo & three\n\nEnd", text);
    }

    [Fact]
    public void SourcesAreKeptPerInstallReceiptAndReplacedOnReinstall()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2mk-gb-" + Guid.NewGuid().ToString("N"));
        try
        {
            var records = new GameBananaRecords(Path.Combine(root, "gamebanana.json"));
            Assert.Empty(records.Load());
            records.Add(new GameBananaSource("r1", 10, 100, "a.zip", "md5a", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "Mod A"));
            records.Add(new GameBananaSource("r2", 20, 200, "b.zip", "md5b", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "Mod B"));
            records.Add(new GameBananaSource("r1", 10, 101, "a2.zip", "md5c", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "Mod A"));

            var loaded = records.Load();
            Assert.Equal(2, loaded.Count);
            Assert.Equal(101, Assert.Single(loaded, source => source.ReceiptId == "r1").FileId);   // a newer install of the same receipt replaces it

            File.WriteAllText(Path.Combine(root, "gamebanana.json"), "not json");
            Assert.Empty(records.Load());                                    // an unreadable record only loses update badges
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static GameBananaFile Upload(long id, DateTimeOffset added) => new(id, $"f{id}.zip", 1, "", new Uri("https://example.invalid/f"), added, "", 0, "clean");

    private static ModEntry Mod(string key, DateTimeOffset added, string? receipt = null, string? modId = null) => new(key, key, ModSource.Other, true, [key + ".vpk"], [null],
        new Dictionary<string, int>(), [], [], receipt is null ? null : new AppInstall("root", new S2ModKit.Application.AddonFileInstallReceipt { Id = receipt }, added), Added: added, ModId: modId);

    [Fact]
    public void AnAppInstallUpdatesWhenANewerFileIsUploadedAndAManagerModWhenAFileIsNewerThanItsDownload()
    {
        var day = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var files = new Dictionary<long, IReadOnlyList<GameBananaFile>>
        {
            [10] = [Upload(100, day), Upload(101, day.AddDays(3))],               // a newer file after the installed one
            [20] = [Upload(200, day)],                                           // only the installed file
            [30] = [Upload(300, day.AddDays(5))],                                // uploaded after the manager's download
            [40] = [Upload(400, day.AddMinutes(30))],                            // within the clock slack of the download
        };
        var sources = new[]
        {
            new GameBananaSource("r10", 10, 100, "f100.zip", "", day, day, "A"),
            new GameBananaSource("r20", 20, 200, "f200.zip", "", day, day, "B"),
        };
        var mods = new[] { Mod("a", day, "r10"), Mod("b", day, "r20"), Mod("c", day, modId: "30"), Mod("d", day, modId: "40"), Mod("e", day) };
        long? IdOf(ModEntry mod) => mod.Install?.Receipt.Id is { } receipt ? sources.FirstOrDefault(source => source.ReceiptId == receipt)?.ModId
            : long.TryParse(mod.ModId, out var id) ? id : null;

        var updates = GameBananaUpdates.Compare(mods, sources, IdOf, files, new Dictionary<long, string>(), day.AddDays(6));

        Assert.Equal(["a", "c"], updates.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(101, Assert.Single(updates["a"].Files).Id);
        Assert.Equal(300, Assert.Single(updates["c"].Files).Id);
    }

    /// <summary>Live check against GameBanana (S2MODKIT_TEST_NETWORK=1): a listing, a page and a small download whose size and MD5
    /// match the listing.</summary>
    [Fact]
    public async Task ALiveListingPageAndDownloadMatchGameBanana()
    {
        if (Environment.GetEnvironmentVariable("S2MODKIT_TEST_NETWORK") != "1")
        {
            Assert.Skip("Set S2MODKIT_TEST_NETWORK=1 to enable this live check against GameBanana.");
            return;
        }

        var cache = Path.Combine(Path.GetTempPath(), "s2mk-gb-live-" + Guid.NewGuid().ToString("N"));
        try
        {
            var client = new GameBananaClient(cache);
            var (mods, total) = await client.ListAsync("Generic_MostLiked", GameBananaClient.SkinsCategory, 1, TestContext.Current.CancellationToken);
            Assert.True(total > 100);
            Assert.NotEmpty(mods);
            var (_, heroes) = await client.CategoriesAsync(TestContext.Current.CancellationToken);
            Assert.Contains(heroes, hero => hero.Name == "Abrams");
            var page = await client.PageAsync(653045, TestContext.Current.CancellationToken);           // a small HUD mod
            var file = page.Files.OrderBy(item => item.Size).First();
            var path = await GameBananaClient.DownloadAsync(file, cache, null, TestContext.Current.CancellationToken);
            Assert.Equal(file.Size, new FileInfo(path).Length);
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, true);
            }
        }
    }

    /// <summary>Live check on the owner's own addons (S2MODKIT_TEST_GAME and S2MODKIT_TEST_NETWORK=1): the mod list is read (nothing
    /// changes) and every Mod Manager mod is compared with GameBanana; S2MODKIT_TEST_UPDATES_REPORT names a file for the result.</summary>
    [Fact]
    public async Task InstalledModsAreComparedWithGameBanana()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (Environment.GetEnvironmentVariable("S2MODKIT_TEST_NETWORK") != "1" || string.IsNullOrWhiteSpace(game))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME and S2MODKIT_TEST_NETWORK=1 to enable this live check.");
            return;
        }

        using var root = new ModTestRoot();
        var mods = await new ModCatalog(new AppSettings(game, root.Data)).LoadAsync(TestContext.Current.CancellationToken);
        long? IdOf(ModEntry mod) => long.TryParse(mod.ModId, out var id) && id > 0 ? id : null;
        var updates = await new GameBananaUpdates(Path.Combine(root.Data, "updates.json"))
            .CheckAsync(new GameBananaClient(Path.Combine(root.Data, "cache")), mods, [], IdOf, null, TestContext.Current.CancellationToken);
        Assert.All(updates, pair => Assert.All(pair.Value.Files, file => Assert.True(file.Added > mods.First(mod => mod.Key == pair.Key).Added)));
        if (Environment.GetEnvironmentVariable("S2MODKIT_TEST_UPDATES_REPORT") is { Length: > 0 } report)
        {
            System.IO.File.WriteAllLines(report, [$"{mods.Count(mod => IdOf(mod) is not null)} of {mods.Count} mods have a GameBanana id; {updates.Count} have a newer file:",
                .. updates.Select(pair => $"{mods.First(mod => mod.Key == pair.Key).Name}: installed {mods.First(mod => mod.Key == pair.Key).Added.LocalDateTime:d}, newest {pair.Value.Files[0].Name} {pair.Value.Files[0].Added.LocalDateTime:d}")]);
        }
    }
}
