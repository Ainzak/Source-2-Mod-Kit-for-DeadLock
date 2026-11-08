using S2ModKit.Domain;

namespace S2ModKit.Desktop.Tests;

/// <summary>Profiles: named sets of enabled mods, switched by renaming in place, on synthetic folders.</summary>
public sealed class ModProfileTests
{
    private static ModEntry ModWith(IReadOnlyList<ModEntry> mods, string fileName) =>
        Assert.Single(mods, mod => mod.Files.Any(file => Path.GetFileName(file) == fileName));

    private static ModEntry Named(IReadOnlyList<ModEntry> mods, string sound) =>
        Assert.Single(mods, mod => mod.Entries.Any(entry => entry.Contains(sound, StringComparison.Ordinal)));

    private static int JournalCount(ModTestRoot root) =>
        Directory.Exists(Path.Combine(root.Data, "journal")) ? Directory.EnumerateFiles(Path.Combine(root.Data, "journal"), "*.json").Count() : 0;

    [Fact]
    public async Task AProfileFollowsRenamesAndSwitchingBackRestoresItsSetInOneOperation()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak05_dir.vpk"), "sounds/a/alpha.vsnd_c");
        await root.VpkAsync(root.In("pak06_dir.vpk"), "sounds/b/bravo.vsnd_c");
        await root.VpkAsync(root.In("off_pak07_dir.vpk"), "sounds/c/charlie.vsnd_c");
        var catalog = root.Catalog();
        var mods = await catalog.LoadAsync(token);
        var saved = catalog.Profiles.Save("Two", mods, DateTimeOffset.UnixEpoch);
        Assert.Equal(["file:pak05_dir.vpk", "file:pak06_dir.vpk"], saved.Mods.Select(mod => mod.Id));   // the enabled ones, in load order

        await catalog.DisableAsync(ModWith(mods, "pak06_dir.vpk"), token);                // bravo off: the profile follows its new name
        mods = await catalog.LoadAsync(token);
        await catalog.EnableAsync(Named(mods, "charlie"), token);                          // charlie on (outside the profile)
        mods = await catalog.LoadAsync(token);
        Assert.Contains("file:off_pak06_dir.vpk", Assert.Single(catalog.Profiles.Load()).Mods.Select(mod => mod.Id));
        var plan = ModProfiles.Plan(Assert.Single(catalog.Profiles.Load()), mods);
        Assert.Equal(["charlie"], plan.Disable.Select(mod => Path.GetFileNameWithoutExtension(mod.Entries.Single())));
        Assert.Equal(["bravo"], plan.Enable.Select(mod => Path.GetFileNameWithoutExtension(mod.Entries.Single())));
        var journals = JournalCount(root);

        var change = await catalog.ApplyProfileAsync(Assert.Single(catalog.Profiles.Load()), mods, token);

        Assert.Equal(journals + 1, JournalCount(root));                                    // one journaled rename for the whole switch
        Assert.Contains("1 mod disabled, 1 enabled", change.Message, StringComparison.Ordinal);
        mods = await catalog.LoadAsync(token);
        Assert.True(Named(mods, "alpha").Loaded);
        Assert.True(Named(mods, "bravo").Loaded);
        Assert.False(Named(mods, "charlie").Loaded);
        var after = ModProfiles.Plan(Assert.Single(catalog.Profiles.Load()), mods);
        Assert.True(after.IsEmpty);
        Assert.Empty(after.Missing);
    }

    [Fact]
    public async Task MissingModsAreNamedAndTheSwitchIsRefusedWhileTheGameRuns()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak05_dir.vpk"), "sounds/a/alpha.vsnd_c");
        await root.VpkAsync(root.In("pak06_dir.vpk"), "sounds/b/bravo.vsnd_c");
        await root.VpkAsync(root.In("off_pak07_dir.vpk"), "sounds/c/charlie.vsnd_c");
        var catalog = root.Catalog();
        var mods = await catalog.LoadAsync(token);
        var profile = catalog.Profiles.Save("Both", mods, DateTimeOffset.UnixEpoch);
        File.Delete(root.In("pak05_dir.vpk"));                                             // removed outside the app
        await catalog.EnableAsync(Named(mods, "charlie"), token);
        mods = await catalog.LoadAsync(token);

        root.GameProbe.Running = true;
        var before = root.AddonNames();
        Assert.Equal("GAME_RUNNING", (await Assert.ThrowsAsync<S2ModKitException>(() => catalog.ApplyProfileAsync(profile, mods, token))).Error.Code);
        Assert.Equal(before, root.AddonNames());

        root.GameProbe.Running = false;
        var change = await catalog.ApplyProfileAsync(profile, mods, token);
        Assert.Contains("Not in the addons folder any more: 'pak05_dir'", change.Message, StringComparison.Ordinal);
        mods = await catalog.LoadAsync(token);
        Assert.False(Named(mods, "charlie").Loaded);
        Assert.True(Named(mods, "bravo").Loaded);
    }

    [Fact]
    public void ProfilesAreReplacedByNameDeletedAndAnUnreadableFileIsEmpty()
    {
        using var root = new ModTestRoot();
        var profiles = new ModProfiles(Path.Combine(root.Data, "profiles.json"));
        profiles.Save("Ranked", [], DateTimeOffset.UnixEpoch);
        profiles.Save("Fun", [], DateTimeOffset.UnixEpoch);
        profiles.Save("ranked", [], DateTimeOffset.UnixEpoch.AddDays(1));                  // same name, other case: replaced
        Assert.Equal(["Fun", "ranked"], profiles.Load().Select(profile => profile.Name));
        profiles.Delete("Fun");
        Assert.Equal(["ranked"], profiles.Load().Select(profile => profile.Name));
        File.WriteAllText(Path.Combine(root.Data, "profiles.json"), "not json");
        Assert.Empty(profiles.Load());
    }
}
