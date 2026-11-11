using System.Text;
using S2ModKit.Adapters.Vpk;
using S2ModKit.Domain;

namespace S2ModKit.Desktop.Tests;

public sealed class ModMergeTests
{
    [Fact]
    public void ASharedFileIsKeptFromTheModThatLoadsFirst()
    {
        var conflicts = ModMerge.Conflicts([("First", ["a.txt", "shared.txt"]), ("Second", ["b.txt", "shared.txt"]), ("Third", ["shared.txt"])]);

        var conflict = Assert.Single(conflicts);
        Assert.Equal("shared.txt", conflict.Path);
        Assert.Equal("First", conflict.Winner);
        Assert.Equal(["Second", "Third"], conflict.Losers);
    }

    [Fact]
    public async Task MergeKeepsTheLoadOrderWinnerDisablesTheOriginalsAndSplitTurnsThemBackOn()
    {
        var token = TestContext.Current.CancellationToken;
        using var root = new ModTestRoot();
        await WriteAsync(root.In("pak10_dir.vpk"), ("sounds/a.vsnd_c", "a"), ("shared/file.txt", "from pak10"));
        await WriteAsync(root.In("pak20_dir.vpk"), ("sounds/b.vsnd_c", "b"), ("shared/file.txt", "from pak20"));
        var catalog = root.Catalog();
        var mods = await catalog.LoadAsync(token);
        Assert.Equal(2, mods.Count(mod => mod.Loaded));

        var change = await catalog.MergeAsync([.. mods], "Merged test", token);

        var after = await catalog.LoadAsync(token);
        var merged = Assert.Single(after, mod => mod.Loaded);
        Assert.Equal(change.SelectKey, merged.Key);
        Assert.Equal(2, after.Count(mod => !mod.Loaded));                            // the originals: disabled, not deleted
        using (var package = new SteamDatabase.ValvePak.Package())
        {
            package.Read(merged.Files[0]);
            Assert.Equal(3, package.Entries!.Values.Sum(group => group.Count));
            package.ReadEntry(package.FindEntry("shared/file.txt")!, out var kept);
            Assert.Equal("from pak10", Encoding.UTF8.GetString(kept));                // pak10 loads first and wins
        }

        var record = Assert.IsType<MergeRecord>(MergeRecord.Of(merged));
        Assert.Equal("merge_addons@1", record.Operation);
        Assert.Equal(2, record.Parts.Count);
        Assert.Equal("shared/file.txt", Assert.Single(record.Conflicts).Path);
        Assert.All(record.Parts, part => Assert.Equal(64, Assert.Single(part.Files).Sha256.Length));

        await catalog.SplitAsync(merged, token);

        var split = await catalog.LoadAsync(token);
        Assert.Equal(2, split.Count(mod => mod.Loaded));
        Assert.Contains(split, mod => !mod.Loaded && mod.Name == merged.Name);      // the merged mod is kept, disabled
        Assert.Contains("pak10_dir.vpk", root.AddonNames());
        Assert.Contains("pak20_dir.vpk", root.AddonNames());
    }

    [Fact]
    public async Task MergingOneModIsRefused()
    {
        var token = TestContext.Current.CancellationToken;
        using var root = new ModTestRoot();
        await WriteAsync(root.In("pak10_dir.vpk"), ("sounds/a.vsnd_c", "a"));
        var catalog = root.Catalog();
        var mods = await catalog.LoadAsync(token);

        var error = await Assert.ThrowsAsync<S2ModKitException>(() => catalog.MergeAsync([.. mods], "Alone", token));

        Assert.Equal("MERGE_TOO_FEW", error.Error.Code);
        Assert.Equal(["pak10_dir.vpk"], root.AddonNames());
    }

    private static Task WriteAsync(string path, params (string Path, string Text)[] entries) =>
        CompactVpkArchiveIO.WriteAsync(path, entries.ToDictionary(entry => entry.Path, entry => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(entry.Text)), CancellationToken.None);
}
