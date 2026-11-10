using System.Text.Json;
using S2ModKit.Application;
using S2ModKit.Desktop;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;

namespace S2ModKit.Desktop.Tests;

/// <summary>Conflicts between enabled mods and moving a mod in the load order, on synthetic folders.</summary>
public sealed class ModConflictAndOrderTests
{
    private const string Shared = "sounds/vo/wraith/hello.vsnd_c";
    private const string WraithModel = "models/heroes_wip/wraith/wraith.vmdl_c";
    private const string BaseHud = "panorama/layout/base_hud.vxml_c";

    private static ModEntry Mod(IReadOnlyList<ModEntry> mods, string fileName) =>
        Assert.Single(mods, mod => mod.Files.Any(file => Path.GetFileName(file) == fileName));

    private static string[] Journals(ModTestRoot root) =>
        Directory.Exists(Path.Combine(root.Data, "journal")) ? [.. Directory.EnumerateFiles(Path.Combine(root.Data, "journal"), "*.json").Order(StringComparer.Ordinal)] : [];

    [Fact]
    public async Task EnablingADisabledModNamesTheEnabledModsItWouldConflictWith()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak05_dir.vpk"), Shared, WraithModel, "README.txt");
        await root.VpkAsync(root.In("off_pak07_dir.vpk"), Shared, WraithModel, "README.txt");
        await root.VpkAsync(root.In("off_pak08_dir.vpk"), "sounds/vo/haze/other.vsnd_c", "README.txt");
        var mods = await root.Catalog().LoadAsync(token);
        var clashing = Mod(mods, "off_pak07_dir.vpk");

        Assert.False(clashing.Loaded);
        Assert.Empty(clashing.ConflictInfo.With);                              // disabled mods are not compared in the list
        var lines = ModConflictAnalysis.WouldConflict(clashing, mods);
        Assert.Contains(lines, line => line.Contains("same 2 file(s)", StringComparison.Ordinal) && line.Contains("1 model", StringComparison.Ordinal) && line.Contains("1 sound", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("already has an enabled model mod", StringComparison.Ordinal));
        Assert.Empty(ModConflictAnalysis.WouldConflict(Mod(mods, "off_pak08_dir.vpk"), mods));   // README.txt alone is no conflict
    }

    [Fact]
    public async Task TheLowerSlotWinsASharedPathAndRootJunkIsIgnored()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak05_dir.vpk"), Shared, "README.txt", "/maps/cache.soc", "sounds/vo/wraith/only5.vsnd_c");
        await root.VpkAsync(root.In("pak06_dir.vpk"), Shared, "README.txt", "/maps/cache.soc", "models/props/crate.vmdl_c");
        var mods = await root.Catalog().LoadAsync(token);

        var first = Assert.Single(Mod(mods, "pak05_dir.vpk").ConflictInfo.With);
        var second = Assert.Single(Mod(mods, "pak06_dir.vpk").ConflictInfo.With);

        Assert.Equal((1, 0), (first.Wins, first.Loses));                       // README.txt and the .soc cache are not conflicts
        Assert.Equal((0, 1), (second.Wins, second.Loses));
        Assert.Equal(["sound"], first.ByKind.Select(item => item.Kind));
        Assert.Equal(["wins"], Mod(mods, "pak05_dir.vpk").ConflictInfo.Badges);
        Assert.Equal(["conflict"], Mod(mods, "pak06_dir.vpk").ConflictInfo.Badges);
    }

    [Fact]
    public async Task ADisabledModDoesNotConflict()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak05_dir.vpk"), Shared);
        await root.VpkAsync(root.In("off_pak06_dir.vpk"), Shared);

        var mods = await root.Catalog().LoadAsync(token);

        Assert.All(mods, mod => Assert.Empty(mod.ConflictInfo.With));
    }

    [Fact]
    public async Task ASplitGroupWinsSomePathsAndLosesOthers()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak10_dir.vpk"), "sounds/a.vsnd_c", "sounds/both.vsnd_c");
        await root.VpkAsync(root.In("pak30_dir.vpk"), "sounds/b.vsnd_c", "sounds/both.vsnd_c");
        await root.VpkAsync(root.In("pak20_dir.vpk"), "sounds/a.vsnd_c", "sounds/b.vsnd_c", "sounds/both.vsnd_c");
        await File.WriteAllTextAsync(root.In(".dmm.json"), """{"mods":{"777":{"enabled":true,"currentVpks":["pak10_dir.vpk","pak30_dir.vpk"]}}}""", token);
        var state = await root.ManagerStateAsync(("777", "Split pack", "installed", ["pak10_dir.vpk", "pak30_dir.vpk"]));

        var mods = await root.Catalog(state).LoadAsync(token);

        var group = Assert.Single(mods, mod => mod.Name == "Split pack");
        var with = Assert.Single(group.ConflictInfo.With);
        Assert.Equal("pak20_dir", with.OtherName);
        Assert.Equal(["sounds/a.vsnd_c", "sounds/both.vsnd_c"], with.Paths.Where(path => path.Wins).Select(path => path.Path));   // both: its pak10 archive wins
        Assert.Equal(["sounds/b.vsnd_c"], with.Paths.Where(path => !path.Wins).Select(path => path.Path));
        Assert.Equal(["conflict"], group.ConflictInfo.Badges);
        var other = Assert.Single(Mod(mods, "pak20_dir.vpk").ConflictInfo.With);
        Assert.Equal((1, 2), (other.Wins, other.Loses));
    }

    [Fact]
    public async Task ASharedHudLayoutIsNamedWithItsWinner()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak27_dir.vpk"), BaseHud, "panorama/scripts/a.vjs_c");
        await root.VpkAsync(root.In("pak28_dir.vpk"), BaseHud, "panorama/scripts/a.vjs_c", "panorama/layout/hud/other.vxml_c");

        var mods = await root.Catalog().LoadAsync(token);

        var loser = Assert.Single(Mod(mods, "pak28_dir.vpk").ConflictInfo.With);
        Assert.Contains("Both replace the HUD layout (base_hud): only pak27_dir works.", loser.Notes);
        Assert.Contains(loser.Notes, note => note.Contains("interface layout or script", StringComparison.Ordinal));
        Assert.Contains("Both replace the HUD layout (base_hud): only pak27_dir works.", Assert.Single(Mod(mods, "pak27_dir.vpk").ConflictInfo.With).Notes);
        Assert.Contains("ui", loser.ByKind.Select(item => item.Kind));
    }

    [Fact]
    public async Task TwoModelModsForOneHeroWarnBothAndNameTheLowerSlot()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak09_dir.vpk"), WraithModel, "models/heroes_wip/wraith/a.vmat_c");
        await root.VpkAsync(root.In("pak04_dir.vpk"), WraithModel);
        await root.VpkAsync(root.In("pak07_dir.vpk"), "sounds/vo/wraith/x.vsnd_c");

        var mods = await root.Catalog().LoadAsync(token);

        foreach (var file in new[] { "pak04_dir.vpk", "pak09_dir.vpk" })
        {
            var warning = Assert.Single(Mod(mods, file).ConflictInfo.ModelWarnings);
            Assert.StartsWith("Two model mods for ", warning, StringComparison.Ordinal);
            Assert.EndsWith("only 'pak04_dir' shows where they overlap.", warning, StringComparison.Ordinal);
            Assert.Contains("two models", Mod(mods, file).ConflictInfo.Badges);
        }

        Assert.Empty(Mod(mods, "pak07_dir.vpk").ConflictInfo.ModelWarnings);     // a sound mod is not a model mod
    }

    [Fact]
    public async Task MovingAModUpOrDownSwapsSlotsWithItsNeighbourOnly()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak03_dir.vpk"), "sounds/a.vsnd_c");
        await root.VpkAsync(root.In("pak10_dir.vpk"), "sounds/b.vsnd_c");        // a gap before it
        await root.VpkAsync(root.In("pak11_dir.vpk"), "sounds/c.vsnd_c");
        await root.VpkAsync(root.In("pak12_dir.vpk"), "sounds/d.vsnd_c");
        var catalog = root.Catalog();
        var mods = await catalog.LoadAsync(token);

        var change = await catalog.MoveAsync(Mod(mods, "pak10_dir.vpk"), -1, mods, token);       // across the gap
        Assert.Equal("file:pak03_dir.vpk", change.SelectKey);
        Assert.Equal(["b", "a", "c", "d"], await SlotOrderAsync(catalog, ["sounds/b.vsnd_c", "sounds/a.vsnd_c", "sounds/c.vsnd_c", "sounds/d.vsnd_c"], token));

        mods = await catalog.LoadAsync(token);
        await catalog.MoveAsync(Mod(mods, "pak12_dir.vpk"), -1, mods, token);                    // dense run: only pak11 and pak12 trade places

        Assert.Equal(["b", "a", "d", "c"], await SlotOrderAsync(catalog, ["sounds/b.vsnd_c", "sounds/a.vsnd_c", "sounds/d.vsnd_c", "sounds/c.vsnd_c"], token));
        Assert.Equal(["pak03_dir.vpk", "pak10_dir.vpk", "pak11_dir.vpk", "pak12_dir.vpk"], root.AddonNames());   // same slots, other contents
        mods = await catalog.LoadAsync(token);
        Assert.Equal(3, Mod(mods, "pak03_dir.vpk").Slots[0]);
        Assert.Contains("sounds/b.vsnd_c", Mod(mods, "pak03_dir.vpk").Entries);
        Assert.Contains("sounds/a.vsnd_c", Mod(mods, "pak10_dir.vpk").Entries);
        Assert.Contains("sounds/d.vsnd_c", Mod(mods, "pak11_dir.vpk").Entries);

        await catalog.MoveAsync(Mod(mods, "pak03_dir.vpk"), 1, mods, token);                      // later
        mods = await catalog.LoadAsync(token);
        Assert.Contains("sounds/a.vsnd_c", Mod(mods, "pak03_dir.vpk").Entries);
        Assert.Contains("sounds/b.vsnd_c", Mod(mods, "pak10_dir.vpk").Entries);
        Assert.Equal(3, Journals(root).Length);                                               // one journal per move (three moves)
        Assert.DoesNotContain(root.AddonNames(), name => name.StartsWith("s2mk_tmp_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadFirstAndLastMoveAModAcrossTheWholeOrderInOneOperation()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak03_dir.vpk"), "sounds/a.vsnd_c");
        await root.VpkAsync(root.In("pak10_dir.vpk"), "sounds/b.vsnd_c");
        await root.VpkAsync(root.In("pak11_dir.vpk"), "sounds/c.vsnd_c");
        await File.WriteAllBytesAsync(root.In("pak11_000.vpk"), [7], token);              // a chunk follows its archive
        await root.VpkAsync(root.In("pak98_dir.vpk"), "sounds/d.vsnd_c");                  // a new build at the end
        var catalog = root.Catalog();
        var mods = await catalog.LoadAsync(token);
        string[] markers = ["sounds/a.vsnd_c", "sounds/b.vsnd_c", "sounds/c.vsnd_c", "sounds/d.vsnd_c"];

        await catalog.MoveToEndAsync(Mod(mods, "pak98_dir.vpk"), first: true, mods, token);

        Assert.Equal(["d", "a", "b", "c"], await SlotOrderAsync(catalog, markers, token));
        string[] slots = ["pak03_dir.vpk", "pak10_dir.vpk", "pak11_dir.vpk", "pak98_000.vpk", "pak98_dir.vpk"];
        Assert.Equal(slots, root.AddonNames());   // the same slots, shared out again
        Assert.Single(Journals(root));                                                      // one operation
        mods = await catalog.LoadAsync(token);
        Assert.Equal("ADDON_REORDER_RANGE", (await Assert.ThrowsAsync<S2ModKitException>(() => catalog.MoveToEndAsync(Mod(mods, "pak03_dir.vpk"), first: true, mods, token))).Error.Code);   // already first

        mods = await catalog.LoadAsync(token);
        await catalog.MoveToEndAsync(Mod(mods, "pak03_dir.vpk"), first: false, mods, token);   // the first one (d) to the end again

        Assert.Equal(["a", "b", "c", "d"], await SlotOrderAsync(catalog, markers, token));
        Assert.DoesNotContain(root.AddonNames(), name => name.StartsWith("s2mk_tmp_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AModMovesToAFreeSlotOrSwapsWithTheModThere()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak03_dir.vpk"), "sounds/a.vsnd_c");
        await root.VpkAsync(root.In("pak10_dir.vpk"), "sounds/b.vsnd_c");
        await root.VpkAsync(root.In("pak97_dir.vpk"), "sounds/c.vsnd_c");
        await File.WriteAllBytesAsync(root.In("pak97_000.vpk"), [7], token);              // a chunk follows its archive
        var catalog = root.Catalog();
        var mods = await catalog.LoadAsync(token);

        await catalog.MoveToSlotAsync(Mod(mods, "pak97_dir.vpk"), 5, swap: false, mods, token);   // a free slot
        mods = await catalog.LoadAsync(token);
        Assert.Contains("sounds/c.vsnd_c", Mod(mods, "pak05_dir.vpk").Entries);
        Assert.Contains("pak05_000.vpk", root.AddonNames());

        var refused = await Assert.ThrowsAsync<S2ModKitException>(() => catalog.MoveToSlotAsync(Mod(mods, "pak05_dir.vpk"), 3, swap: false, mods, token));
        Assert.Equal("ADDON_SLOT_TAKEN", refused.Error.Code);
        Assert.Contains("pak03", refused.Error.Summary, StringComparison.Ordinal);

        await catalog.MoveToSlotAsync(Mod(mods, "pak05_dir.vpk"), 3, swap: true, mods, token);    // swapped with the mod in pak03
        mods = await catalog.LoadAsync(token);
        Assert.Contains("sounds/c.vsnd_c", Mod(mods, "pak03_dir.vpk").Entries);
        Assert.Contains("sounds/a.vsnd_c", Mod(mods, "pak05_dir.vpk").Entries);
        Assert.Contains("pak03_000.vpk", root.AddonNames());
        Assert.DoesNotContain(root.AddonNames(), name => name.StartsWith("s2mk_tmp_", StringComparison.Ordinal));
        Assert.Equal("ADDON_SLOT_RANGE", (await Assert.ThrowsAsync<S2ModKitException>(() => catalog.MoveToSlotAsync(Mod(mods, "pak03_dir.vpk"), 100, swap: false, mods, token))).Error.Code);
    }

    [Fact]
    public async Task DroppingAModOnAnotherGivesItThatPlace()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak03_dir.vpk"), "sounds/a.vsnd_c");
        await root.VpkAsync(root.In("pak10_dir.vpk"), "sounds/b.vsnd_c");
        await root.VpkAsync(root.In("pak11_dir.vpk"), "sounds/c.vsnd_c");
        await root.VpkAsync(root.In("pak98_dir.vpk"), "sounds/d.vsnd_c");
        var catalog = root.Catalog();
        var mods = await catalog.LoadAsync(token);
        string[] markers = ["sounds/a.vsnd_c", "sounds/b.vsnd_c", "sounds/c.vsnd_c", "sounds/d.vsnd_c"];

        await catalog.MoveToPlaceOfAsync(Mod(mods, "pak98_dir.vpk"), Mod(mods, "pak10_dir.vpk"), mods, token);   // d dropped on b

        Assert.Equal(["a", "d", "b", "c"], await SlotOrderAsync(catalog, markers, token));
        mods = await catalog.LoadAsync(token);
        await catalog.MoveToPlaceOfAsync(Mod(mods, "pak03_dir.vpk"), Mod(mods, "pak11_dir.vpk"), mods, token);   // a dropped on b (now pak11)
        Assert.Equal(["d", "b", "a", "c"], await SlotOrderAsync(catalog, markers, token));
        Assert.Equal(2, Journals(root).Length);
    }

    [Fact]
    public async Task AConflictMarkedFineIsHiddenUntilTheModsShareMore()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak03_dir.vpk"), "sounds/shared.vsnd_c", "sounds/a.vsnd_c");
        await root.VpkAsync(root.In("pak10_dir.vpk"), "sounds/shared.vsnd_c", "sounds/b.vsnd_c");
        var catalog = root.Catalog();
        var mods = await catalog.LoadAsync(token);
        var (first, second) = (Mod(mods, "pak03_dir.vpk"), Mod(mods, "pak10_dir.vpk"));
        Assert.Single(first.ConflictInfo.With);

        catalog.Dismissed.Dismiss(first, second, 1);
        mods = await catalog.LoadAsync(token);
        Assert.Empty(Mod(mods, "pak03_dir.vpk").ConflictInfo.With);                       // hidden on both sides
        Assert.Empty(Mod(mods, "pak10_dir.vpk").ConflictInfo.With);

        catalog.Dismissed.Dismiss(second, first, 0);                                        // marked when they shared fewer files
        mods = await catalog.LoadAsync(token);
        Assert.Single(Mod(mods, "pak03_dir.vpk").ConflictInfo.With);                      // more shared now: it shows again

        catalog.Dismissed.Dismiss(first, second, 1);
        catalog.Dismissed.Clear();
        Assert.Single(Mod(await catalog.LoadAsync(token), "pak03_dir.vpk").ConflictInfo.With);   // Show hidden conflicts
    }

    private static async Task<string[]> SlotOrderAsync(ModCatalog catalog, string[] markers, CancellationToken token)
    {
        var mods = await catalog.LoadAsync(token);
        return [.. mods.OrderBy(mod => mod.FirstSlot).Select(mod => markers.Single(marker => mod.Entries.Contains(marker))[^"x.vsnd_c".Length..][..1])];
    }

    [Fact]
    public async Task AMultiArchiveModAndChunksMoveTogether()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak10_dir.vpk"), "sounds/g1.vsnd_c");
        await root.VpkAsync(root.In("pak11_dir.vpk"), "sounds/g2.vsnd_c");
        await File.WriteAllBytesAsync(root.In("pak11_000.vpk"), [1, 2, 3], token);
        await root.VpkAsync(root.In("pak12_dir.vpk"), "sounds/s.vsnd_c");
        await File.WriteAllBytesAsync(root.In("pak12_000.vpk"), [4, 5], token);
        await File.WriteAllTextAsync(root.In(".dmm.json"), """{"mods":{"777":{"enabled":true,"currentVpks":["pak10_dir.vpk","pak11_dir.vpk"]}}}""", token);
        var state = await root.ManagerStateAsync(("777", "Split pack", "installed", ["pak10_dir.vpk", "pak11_dir.vpk"]));
        var catalog = root.Catalog(state);
        var mods = await catalog.LoadAsync(token);

        await catalog.MoveAsync(Mod(mods, "pak12_dir.vpk"), -1, mods, token);

        // Mod Manager's own record still names the old files (the app never writes it); read the folder without it.
        File.Delete(root.In(".dmm.json"));
        mods = await root.Catalog().LoadAsync(token);
        Assert.Equal([10], Assert.Single(mods, mod => mod.Entries.Contains("sounds/s.vsnd_c")).Slots);
        Assert.Equal([11], Assert.Single(mods, mod => mod.Entries.Contains("sounds/g1.vsnd_c")).Slots);     // the two archives stay in order, contiguous
        Assert.Equal([12], Assert.Single(mods, mod => mod.Entries.Contains("sounds/g2.vsnd_c")).Slots);
        Assert.Equal([4, 5], await File.ReadAllBytesAsync(root.In("pak10_000.vpk"), token));   // chunks follow their _dir
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(root.In("pak12_000.vpk"), token));
        Assert.False(File.Exists(root.In("pak11_000.vpk")));
    }

    [Fact]
    public async Task TheReorderJournalRecordsTheNetRenamesAndTheTemporarySteps()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak27_dir.vpk"), "sounds/a.vsnd_c");
        await root.VpkAsync(root.In("pak28_dir.vpk"), "sounds/b.vsnd_c");
        var catalog = root.Catalog();
        var mods = await catalog.LoadAsync(token);

        await catalog.MoveAsync(Mod(mods, "pak28_dir.vpk"), -1, mods, token);

        using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(Assert.Single(Journals(root)), token));
        var top = journal.RootElement;
        Assert.Equal("addon-reorder@1", top.GetProperty("operation").GetString());
        Assert.Equal("completed", top.GetProperty("result").GetString());
        Assert.True(top.GetProperty("verified").GetBoolean());
        Assert.Equal(["pak27_dir.vpk>pak28_dir.vpk", "pak28_dir.vpk>pak27_dir.vpk"], top.GetProperty("entries").EnumerateArray().Select(entry => $"{entry.GetProperty("from").GetString()}>{entry.GetProperty("to").GetString()}").Order());
        var steps = top.GetProperty("steps").EnumerateArray().Select(step => step.GetProperty("to").GetString()!).ToArray();
        Assert.Equal(4, steps.Length);                                         // both through temporary names, never an overwrite
        Assert.Equal(2, steps.Count(step => step.StartsWith("s2mk_tmp_", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AFailureMidWayPutsEveryFileBackAndJournalsRolledBack()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak27_dir.vpk"), "sounds/a.vsnd_c");
        await root.VpkAsync(root.In("pak28_dir.vpk"), "sounds/b.vsnd_c");
        var before = root.AddonNames().Select(name => (name, new FileInfo(root.In(name)).Length)).ToArray();
        var mods = await root.Catalog().LoadAsync(token);
        var enabled = ModCatalog.LoadOrderOf(mods);
        var operations = new AddonModOperations(new FailingRenames(new AddonFileOperations(), failOn: 3), root.GameProbe, root.Bin, new SystemClock());

        var failure = await Assert.ThrowsAsync<S2ModKitException>(() => operations.ReorderAsync(root.Addons, enabled, Mod(mods, "pak28_dir.vpk").Key, -1, Path.Combine(root.Data, "journal"), token));

        Assert.Equal("ADDON_RENAME_FAILED", failure.Error.Code);
        Assert.Equal(before, root.AddonNames().Select(name => (name, new FileInfo(root.In(name)).Length)).ToArray());
        using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(Assert.Single(Journals(root)), token));
        Assert.Equal("rolled_back", journal.RootElement.GetProperty("result").GetString());
        Assert.Equal("addon-reorder@1", journal.RootElement.GetProperty("operation").GetString());
    }

    [Fact]
    public async Task ReorderIsRefusedWhileTheGameRunsAndOutOfRange()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak01_dir.vpk"), "sounds/a.vsnd_c");
        await root.VpkAsync(root.In("pak99_dir.vpk"), "sounds/b.vsnd_c");
        await root.VpkAsync(root.In("off_pak50_dir.vpk"), "sounds/c.vsnd_c");
        var catalog = root.Catalog();
        var mods = await catalog.LoadAsync(token);

        var first = await Assert.ThrowsAsync<S2ModKitException>(() => catalog.MoveAsync(Mod(mods, "pak01_dir.vpk"), -1, mods, token));
        var last = await Assert.ThrowsAsync<S2ModKitException>(() => catalog.MoveAsync(Mod(mods, "pak99_dir.vpk"), 1, mods, token));
        var disabled = await Assert.ThrowsAsync<S2ModKitException>(() => catalog.MoveAsync(Mod(mods, "off_pak50_dir.vpk"), 1, mods, token));
        root.GameProbe.Running = true;
        var running = await Assert.ThrowsAsync<S2ModKitException>(() => catalog.MoveAsync(Mod(mods, "pak99_dir.vpk"), -1, mods, token));

        Assert.Equal(["ADDON_REORDER_RANGE", "ADDON_REORDER_RANGE", "ADDON_REORDER_NOT_ENABLED", "GAME_RUNNING"], new[] { first, last, disabled, running }.Select(error => error.Error.Code));
        Assert.Equal(["off_pak50_dir.vpk", "pak01_dir.vpk", "pak99_dir.vpk"], root.AddonNames());
        Assert.Empty(Journals(root));
    }

    /// <summary>Real file operations whose n-th rename call (1-based, forward or undo) throws.</summary>
    private sealed class FailingRenames(AddonFileOperations inner, int failOn) : IAddonFileOperations
    {
        private int renames;

        public Task<IReadOnlyList<AddonFileStat>> ListFilesAsync(string addonsRoot, CancellationToken cancellationToken = default) => inner.ListFilesAsync(addonsRoot, cancellationToken);

        public Task RenameAsync(string addonsRoot, string fromName, string toName, CancellationToken cancellationToken = default) =>
            ++renames == failOn ? throw new IOException("simulated failure") : inner.RenameAsync(addonsRoot, fromName, toName, cancellationToken);

        public Task CopyNewAsync(string sourcePath, string addonsRoot, string targetName, CancellationToken cancellationToken = default) => inner.CopyNewAsync(sourcePath, addonsRoot, targetName, cancellationToken);

        public Task DeleteCreatedAsync(string addonsRoot, string fileName, CancellationToken cancellationToken = default) => inner.DeleteCreatedAsync(addonsRoot, fileName, cancellationToken);

        public IReadOnlyList<string> FindChunksBeside(string packagePath) => inner.FindChunksBeside(packagePath);

        public Task WriteJournalAsync(string journalRoot, AddonOperationJournal journal, CancellationToken cancellationToken = default) => inner.WriteJournalAsync(journalRoot, journal, cancellationToken);
    }
}
