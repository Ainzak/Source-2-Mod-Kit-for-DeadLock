using System.IO.Compression;
using System.Text.Json;
using S2ModKit.Application;
using S2ModKit.Desktop;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;

namespace S2ModKit.Desktop.Tests;

/// <summary>Enable, disable, delete, install from archives and versions of any mod, on synthetic folders.</summary>
public sealed class ModManagementTests
{
    private const string Sound = "sounds/vo/wraith/hello.vsnd_c";
    private const string Model = "models/heroes_wip/wraith/wraith.vmdl_c";

    private static ModEntry ModWith(IReadOnlyList<ModEntry> mods, string fileName) =>
        Assert.Single(mods, mod => mod.Files.Any(file => Path.GetFileName(file) == fileName));

    private static string[] Journals(ModTestRoot root) =>
        Directory.Exists(Path.Combine(root.Data, "journal")) ? [.. Directory.EnumerateFiles(Path.Combine(root.Data, "journal"), "*.json").Order(StringComparer.Ordinal)] : [];

    [Fact]
    public async Task AModManagerModIsRenamedToItsModIdAndBackWithoutTouchingTheManagerState()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        var file = await root.VpkAsync(root.In("pak05_dir.vpk"), Sound);
        var stamp = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, stamp);
        var size = new FileInfo(file).Length;
        var state = await root.ManagerStateAsync(("659224", "Soul container", "installed", ["pak05_dir.vpk"]));
        var stateBefore = await File.ReadAllBytesAsync(state, token);
        var catalog = root.Catalog(state);

        var change = await catalog.DisableAsync(ModWith(await catalog.LoadAsync(token), "pak05_dir.vpk"), token);

        Assert.Equal(["659224_pak05_dir.vpk"], root.AddonNames());
        Assert.Equal("659224", change.SelectKey);                            // a Manager mod keeps its key
        var renamed = new FileInfo(root.In("659224_pak05_dir.vpk"));
        Assert.Equal(size, renamed.Length);
        Assert.Equal(stamp, renamed.LastWriteTimeUtc);                       // a rename: nothing rewritten
        var disabled = Assert.Single(await catalog.LoadAsync(token));
        Assert.False(disabled.Loaded);
        Assert.Equal(("Soul container", ModSource.Manager), (disabled.Name, disabled.Source));

        await catalog.EnableAsync(disabled, token);

        Assert.Equal(["pak05_dir.vpk"], root.AddonNames());
        Assert.Equal(stateBefore, await File.ReadAllBytesAsync(state, token));   // Mod Manager's state is never written
        Assert.Equal(stamp, new FileInfo(root.In("pak05_dir.vpk")).LastWriteTimeUtc);
    }

    [Fact]
    public async Task AModManagerUnknownModGetsAnOffPrefixWithADeterministicDisambiguator()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak03_dir.vpk"), Sound);
        await root.VpkAsync(root.In("off_pak03_dir.vpk"), "sounds/other/y.vsnd_c");       // an earlier disabled mod already owns off_pak03
        var catalog = root.Catalog();

        await catalog.DisableAsync(ModWith(await catalog.LoadAsync(token), "pak03_dir.vpk"), token);

        Assert.Equal(["off2_pak03_dir.vpk", "off_pak03_dir.vpk"], root.AddonNames());
        var mods = await catalog.LoadAsync(token);
        Assert.Contains(mods, mod => mod.Name == "pak03_dir" && !mod.Loaded);       // the display name drops the off prefix

        await catalog.EnableAsync(ModWith(mods, "off2_pak03_dir.vpk"), token);        // its slot is free: it takes it back
        Assert.Equal(["off_pak03_dir.vpk", "pak03_dir.vpk"], root.AddonNames());
        await catalog.EnableAsync(ModWith(await catalog.LoadAsync(token), "off_pak03_dir.vpk"), token);   // slot taken now: the next free slot, from 99
        Assert.Equal(["pak03_dir.vpk", "pak99_dir.vpk"], root.AddonNames());
    }

    [Fact]
    public async Task ASplitModManagerGroupMovesAllItsArchivesTogether()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak20_dir.vpk"), Sound);
        await root.VpkAsync(root.In("pak21_dir.vpk"), "sounds/other/b.vsnd_c");
        await File.WriteAllTextAsync(root.In(".dmm.json"), """{"mods":{"777":{"enabled":true,"currentVpks":["pak20_dir.vpk","pak21_dir.vpk"]}}}""", token);
        var state = await root.ManagerStateAsync(("777", "Split pack", "installed", ["pak20_dir.vpk", "pak21_dir.vpk"]));
        var catalog = root.Catalog(state);
        var group = Assert.Single(await catalog.LoadAsync(token));
        Assert.Equal(2, group.Files.Count);

        await catalog.DisableAsync(group, token);

        Assert.Equal(["777_pak20_dir.vpk", "777_pak21_dir.vpk"], root.AddonNames().Where(name => name.EndsWith(".vpk", StringComparison.Ordinal)));
        await catalog.EnableAsync(Assert.Single(await catalog.LoadAsync(token)), token);
        Assert.Equal(["pak20_dir.vpk", "pak21_dir.vpk"], root.AddonNames().Where(name => name.EndsWith(".vpk", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SplitVpkDataChunksFollowTheirDirFileAndKeepTheirNumbers()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak30_dir.vpk"), Sound);
        await File.WriteAllBytesAsync(root.In("pak30_000.vpk"), [1, 2, 3], token);
        await File.WriteAllBytesAsync(root.In("pak30_001.vpk"), [4, 5], token);
        var catalog = root.Catalog();

        await catalog.DisableAsync(ModWith(await catalog.LoadAsync(token), "pak30_dir.vpk"), token);

        Assert.Equal(["off_pak30_000.vpk", "off_pak30_001.vpk", "off_pak30_dir.vpk"], root.AddonNames());
        await root.VpkAsync(root.In("pak30_dir.vpk"), "sounds/other/new.vsnd_c");       // another mod takes the slot meanwhile

        await catalog.EnableAsync(ModWith(await catalog.LoadAsync(token), "off_pak30_dir.vpk"), token);

        Assert.Equal(["pak30_dir.vpk", "pak99_000.vpk", "pak99_001.vpk", "pak99_dir.vpk"], root.AddonNames());
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(root.In("pak99_000.vpk"), token));
        Assert.Equal([4, 5], await File.ReadAllBytesAsync(root.In("pak99_001.vpk"), token));
    }

    [Fact]
    public async Task DisabledModManagerFilesWithoutAPakNameTakeFreeSlotsWhenEnabled()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("local-abc_graves_in_ui.vpk"), "maps/ui/hero_prefabs/necro.vpk");
        await root.VpkAsync(root.In("561445_pak04-armstrong-type-a-84fa0_dir.vpk"), Sound);
        await File.WriteAllBytesAsync(root.In("561445_pak04-armstrong-type-a-84fa0_000.vpk"), [9], token);
        var state = await root.ManagerStateAsync(("local-abc", "graves in ui", "downloaded", []), ("561445", "Armstrong", "downloaded", []));
        var catalog = root.Catalog(state);

        await catalog.EnableAsync(Assert.Single(await catalog.LoadAsync(token), mod => mod.Name == "graves in ui"), token);
        await catalog.EnableAsync(Assert.Single(await catalog.LoadAsync(token), mod => mod.Name == "Armstrong"), token);

        Assert.Equal(["pak98_000.vpk", "pak98_dir.vpk", "pak99_dir.vpk"], root.AddonNames());
    }

    [Fact]
    public async Task EnableNeverTakesASlotAManagerRecordReservesForAnUnmanagedMod()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("off_pak05_dir.vpk"), Sound);
        await File.WriteAllTextAsync(root.In(".dmm.json"), """{"mods":{"888":{"enabled":false,"currentVpks":[],"originalVpkNames":["pak05_dir.vpk"]}}}""", token);
        var catalog = root.Catalog();

        await catalog.EnableAsync(Assert.Single(await catalog.LoadAsync(token)), token);

        Assert.Equal(["pak99_dir.vpk"], root.AddonNames().Where(name => name.EndsWith(".vpk", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task EveryRenameWritesAVerifiedVersionedJournal()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        var file = await root.VpkAsync(root.In("pak07_dir.vpk"), Sound);
        var catalog = root.Catalog();

        await catalog.DisableAsync(ModWith(await catalog.LoadAsync(token), "pak07_dir.vpk"), token);

        using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(Assert.Single(Journals(root)), token));
        var top = journal.RootElement;
        Assert.Equal("addon-rename@1", top.GetProperty("operation").GetString());
        Assert.Equal("completed", top.GetProperty("result").GetString());
        Assert.True(top.GetProperty("verified").GetBoolean());
        Assert.Equal(32, top.GetProperty("id").GetString()!.Length);
        Assert.Equal(Path.GetFullPath(root.Addons), top.GetProperty("addonsRoot").GetString());
        var entry = Assert.Single(top.GetProperty("entries").EnumerateArray());
        Assert.Equal(("pak07_dir.vpk", "off_pak07_dir.vpk"), (entry.GetProperty("from").GetString(), entry.GetProperty("to").GetString()));
        Assert.Equal(new FileInfo(root.In("off_pak07_dir.vpk")).Length, entry.GetProperty("size").GetInt64());
        Assert.True(top.TryGetProperty("startedUtc", out _) && top.TryGetProperty("completedUtc", out _) && entry.TryGetProperty("lastWriteUtc", out _));
        Assert.NotNull(file);
    }

    [Fact]
    public async Task ALockedFileUndoesTheRenamesAlreadyDone()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak41_dir.vpk"), Sound);
        await File.WriteAllBytesAsync(root.In("pak41_000.vpk"), [1], token);
        var catalog = root.Catalog();
        var mod = ModWith(await catalog.LoadAsync(token), "pak41_dir.vpk");

        using (new FileStream(root.In("pak41_000.vpk"), FileMode.Open, FileAccess.Read, FileShare.None))   // the chunk is in use
        {
            var failure = await Assert.ThrowsAsync<S2ModKitException>(() => catalog.DisableAsync(mod, token));
            Assert.Equal("ADDON_RENAME_FAILED", failure.Error.Code);
        }

        Assert.Equal(["pak41_000.vpk", "pak41_dir.vpk"], root.AddonNames());
        using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(Assert.Single(Journals(root)), token));
        Assert.Equal("rolled_back", journal.RootElement.GetProperty("result").GetString());
    }

    [Fact]
    public async Task FailedRenamesAreUndoneAndJournaledAndAFailedUndoIsReported()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak20_dir.vpk"), Sound);
        await root.VpkAsync(root.In("pak21_dir.vpk"), "sounds/other/b.vsnd_c");
        await File.WriteAllBytesAsync(root.In("pak21_000.vpk"), [1], token);
        var target = new AddonModTarget(root.Addons, "Split", null, ["pak20_dir.vpk", "pak21_dir.vpk"], new HashSet<string>());
        var journals = Path.Combine(root.Data, "journal");

        var undone = await Assert.ThrowsAsync<S2ModKitException>(() => Operations(root, new FlakyFiles(new AddonFileOperations(), failFrom: 3, failUntil: 3)).DisableAsync(target, journals, token));
        Assert.Equal("ADDON_RENAME_FAILED", undone.Error.Code);
        Assert.Equal(["pak20_dir.vpk", "pak21_000.vpk", "pak21_dir.vpk"], root.AddonNames());

        var stuck = await Assert.ThrowsAsync<S2ModKitException>(() => Operations(root, new FlakyFiles(new AddonFileOperations(), failFrom: 3, failUntil: 99)).DisableAsync(target, journals, token));
        Assert.Equal("ADDON_RENAME_ROLLBACK_INCOMPLETE", stuck.Error.Code);
        Assert.Contains("off_pak20_dir.vpk", stuck.Error.Summary, StringComparison.Ordinal);   // the files left under new names are listed
        var results = new List<string>();
        foreach (var path in Directory.EnumerateFiles(journals, "*.json"))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
            results.Add(document.RootElement.GetProperty("result").GetString()!);
        }

        Assert.Equal(["rollback_incomplete", "rolled_back"], results.Order());
    }

    [Fact]
    public async Task ARenameThatChangesTheFileIsCaughtByVerificationAndUndone()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak20_dir.vpk"), Sound);
        var target = new AddonModTarget(root.Addons, "One", null, ["pak20_dir.vpk"], new HashSet<string>());

        var failure = await Assert.ThrowsAsync<S2ModKitException>(() =>
            Operations(root, new FlakyFiles(new AddonFileOperations(), touchAfterRename: true)).DisableAsync(target, Path.Combine(root.Data, "journal"), token));

        Assert.Equal("ADDON_RENAME_FAILED", failure.Error.Code);
        Assert.Contains("differs in size or time", failure.Error.Summary, StringComparison.Ordinal);
        Assert.Equal(["pak20_dir.vpk"], root.AddonNames());
    }

    [Fact]
    public async Task EveryChangeIsRefusedWhileTheGameRuns()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak05_dir.vpk"), Sound);
        await root.VpkAsync(root.In("off_pak06_dir.vpk"), "sounds/other/b.vsnd_c");
        var package = await root.VpkAsync(Path.Combine(root.Library, "skin_dir.vpk"), Model);
        var catalog = root.Catalog();
        var mods = await catalog.LoadAsync(token);
        root.GameProbe.Running = true;
        var before = root.AddonNames();

        async Task Refused(Func<Task> action) => Assert.Equal("GAME_RUNNING", (await Assert.ThrowsAsync<S2ModKitException>(action)).Error.Code);
        await Refused(() => catalog.DisableAsync(ModWith(mods, "pak05_dir.vpk"), token));
        await Refused(() => catalog.EnableAsync(ModWith(mods, "off_pak06_dir.vpk"), token));
        await Refused(() => catalog.DeleteAsync(ModWith(mods, "pak05_dir.vpk"), token));
        await Refused(() => catalog.InstallAsync(package, "skin", token));
        await Refused(() => catalog.ActivateVersionAsync(ModWith(mods, "off_pak06_dir.vpk"), mods, token));

        Assert.Equal(before, root.AddonNames());
        Assert.Empty(Journals(root));
    }

    [Fact]
    public async Task DeleteSendsEveryFileOfTheModToTheBinAndJournalsIt()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak30_dir.vpk"), Sound);
        await File.WriteAllBytesAsync(root.In("pak30_000.vpk"), [1], token);
        await root.VpkAsync(root.In("pak31_dir.vpk"), "sounds/other/b.vsnd_c");
        var catalog = root.Catalog();

        await catalog.DeleteAsync(ModWith(await catalog.LoadAsync(token), "pak30_dir.vpk"), token);

        Assert.Equal(["pak31_dir.vpk"], root.AddonNames());
        Assert.Equal(["pak30_000.vpk", "pak30_dir.vpk"], root.Bin.Recycled.Order(StringComparer.Ordinal));
        using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(Assert.Single(Journals(root)), token));
        Assert.Equal("addon-delete@1", journal.RootElement.GetProperty("operation").GetString());
        Assert.Equal("completed", journal.RootElement.GetProperty("result").GetString());
        Assert.Equal(2, journal.RootElement.GetProperty("entries").GetArrayLength());
        Assert.Single(await catalog.LoadAsync(token));
    }

    [Fact]
    public async Task ADeleteThatLeavesFilesBehindIsReportedAsFailed()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak30_dir.vpk"), Sound);
        var catalog = new ModCatalog(root.Settings, Path.Combine(root.Root, "none.json"), root.GameProbe, new NoOpBin());

        var failure = await Assert.ThrowsAsync<S2ModKitException>(async () => await catalog.DeleteAsync(ModWith(await catalog.LoadAsync(token), "pak30_dir.vpk"), token));

        Assert.Equal("ADDON_DELETE_UNVERIFIED", failure.Error.Code);
        Assert.Equal(["pak30_dir.vpk"], root.AddonNames());
    }

    [Fact]
    public async Task DeletingAnAppInstallClosesItsReceiptAndKeepsThePackage()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        var package = await root.VpkAsync(Path.Combine(root.Library, "skin_dir.vpk"), Model);
        var catalog = root.Catalog();
        await catalog.InstallAsync(package, "Wraith skin", token);

        await catalog.DeleteAsync(Assert.Single(await catalog.LoadAsync(token)), token);

        Assert.Empty(await catalog.LoadAsync(token));
        Assert.Empty(root.AddonNames());
        Assert.True(File.Exists(package));
    }

    [Fact]
    public async Task AnAppInstallTheOldWayDisabledStaysListedCanBeEnabledAndDeleted()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        var package = await root.VpkAsync(Path.Combine(root.Library, "skin_dir.vpk"), Model);
        var catalog = root.Catalog();
        await catalog.InstallAsync(package, "Wraith skin", token);
        var receipts = Assert.Single(Directory.EnumerateDirectories(root.Settings.InstallsRoot));
        var application = new AddonFileApplication(new AddonFileStore(), new S2ModKit.Adapters.Vpk.ValvePakAddonTreeReader(), new ModManagerStateReader(), new SystemClock());
        await application.RollbackAsync(root.Addons, receipts, token);       // what the earlier app version did for Disable
        Assert.Empty(root.AddonNames());

        var legacy = Assert.Single(await catalog.LoadAsync(token));
        Assert.Equal((false, ModSource.App), (legacy.Loaded, legacy.Source));
        Assert.Equal([package], legacy.Files);

        await catalog.EnableAsync(legacy, token);
        Assert.True(Assert.Single(await catalog.LoadAsync(token)).Loaded);
        await catalog.DisableAsync(Assert.Single(await catalog.LoadAsync(token)), token);   // and from now on it is renamed like any mod
        Assert.Matches(@"^off_pak9\d_dir\.vpk$", Assert.Single(root.AddonNames()));
    }

    [Fact]
    public async Task ZipVariantsAreChosenExtractedAndInstalledThroughTheSafeInstallerWithTheirChunks()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        var first = await root.VpkAsync(Path.Combine(root.Library, "build", "a", "skin_dir.vpk"), Model);
        var second = await root.VpkAsync(Path.Combine(root.Library, "build", "b", "skin_dir.vpk"), Sound);
        var archive = Path.Combine(root.Library, "pack.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(first, "variants/a/skin_dir.vpk");
            zip.CreateEntryFromFile(second, "variants/b/skin_dir.vpk");
            AddZipEntry(zip, "variants/b/skin_000.vpk", [7, 8, 9]);
            AddZipEntry(zip, "readme.txt", "not a package"u8.ToArray());
        }

        var catalog = root.Catalog();
        var variants = await catalog.ReadArchiveAsync(archive, token);

        Assert.Equal(["variants/a", "variants/b"], variants.Select(variant => variant.Folder));
        Assert.Equal(2, variants[1].Files.Count);                            // the _dir file and its chunk stay together
        var change = await catalog.InstallArchiveAsync(archive, [variants[1]], token);

        Assert.Equal(["pak99_000.vpk", "pak99_dir.vpk"], root.AddonNames());
        Assert.Equal([7, 8, 9], await File.ReadAllBytesAsync(root.In("pak99_000.vpk"), token));
        Assert.Equal("file:pak99_dir.vpk", change.SelectKey);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Settings.StagingRoot));   // the staging copy is removed
        Assert.Equal("pack", Assert.Single(await catalog.LoadAsync(token)).Name);
    }

    [Fact]
    public async Task SeveralChosenVariantsInstallAndOneThatSharesFilesWithAnEnabledModIsRefusedByName()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        var archive = Path.Combine(root.Library, "pack.zip");
        var a = await root.VpkAsync(Path.Combine(root.Library, "build", "a_dir.vpk"), Model);
        var b = await root.VpkAsync(Path.Combine(root.Library, "build", "b_dir.vpk"), Model);        // the same file: variants of one skin
        var c = await root.VpkAsync(Path.Combine(root.Library, "build", "c_dir.vpk"), Sound);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(a, "a_dir.vpk");
            zip.CreateEntryFromFile(b, "b_dir.vpk");
            zip.CreateEntryFromFile(c, "c_dir.vpk");
        }

        var catalog = root.Catalog();
        var change = await catalog.InstallArchiveAsync(archive, await catalog.ReadArchiveAsync(archive, token), token);

        Assert.Equal(["pak98_dir.vpk", "pak99_dir.vpk"], root.AddonNames());   // a and c; b shares a's model
        Assert.Contains("Refused: b_dir.vpk", change.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASevenZipArchiveInstallsLikeAZip()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        var package = await root.VpkAsync(Path.Combine(root.Library, "build", "skin_dir.vpk"), Model);
        var archive = Path.Combine(root.Library, "pack.7z");
        SevenZipFixture.Write(archive, ("mods/skin_dir.vpk", await File.ReadAllBytesAsync(package, token)), ("skin_000.vpk", [1, 2]), ("notes.txt", [0x20]));
        var catalog = root.Catalog();

        var variants = await catalog.ReadArchiveAsync(archive, token);
        await catalog.InstallArchiveAsync(archive, [Assert.Single(variants)], token);

        Assert.Equal(["pak99_dir.vpk"], root.AddonNames());                  // skin_000 is in another folder: not this package's chunk
        Assert.Equal(await File.ReadAllBytesAsync(package, token), await File.ReadAllBytesAsync(root.In("pak99_dir.vpk"), token));
        Assert.Equal("pack", Assert.Single(await catalog.LoadAsync(token)).Name);
    }

    [Fact]
    public async Task AnArchiveWithATraversalEntryIsRefusedAsAWholeAndWritesNothing()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        var archive = Path.Combine(root.Library, "evil.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            AddZipEntry(zip, "../../evil_dir.vpk", "x"u8.ToArray());
            AddZipEntry(zip, "fine_dir.vpk", "y"u8.ToArray());
        }

        var catalog = root.Catalog();
        var failure = await Assert.ThrowsAsync<S2ModKitException>(() => catalog.ReadArchiveAsync(archive, token));

        Assert.Equal("ARCHIVE_PATH_ESCAPE", failure.Error.Code);
        Assert.Empty(root.AddonNames());
        Assert.False(File.Exists(Path.Combine(root.Root, "evil_dir.vpk")));
        Assert.False(Directory.Exists(root.Settings.StagingRoot));
    }

    [Fact]
    public async Task ExtractionRefusesATargetOutsideTheStagingFolderAndAnArchiveWithoutPackagesOrWithGarbage()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        var archive = Path.Combine(root.Library, "ok.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            AddZipEntry(zip, "a_dir.vpk", "x"u8.ToArray());
        }

        var staging = Path.Combine(root.Data, "staging", "one");
        var reader = new ArchivePackageReader();
        var escape = await Assert.ThrowsAsync<S2ModKitException>(() => reader.ExtractAsync(archive, [new ArchiveExtraction("a_dir.vpk", Path.Combine(staging, "..", "out.vpk"))], staging, token));
        Assert.Equal("ARCHIVE_TARGET_ESCAPE", escape.Error.Code);
        var missing = await Assert.ThrowsAsync<S2ModKitException>(() => reader.ExtractAsync(archive, [new ArchiveExtraction("nope_dir.vpk", Path.Combine(staging, "n.vpk"))], staging, token));
        Assert.Equal("ARCHIVE_ENTRY_MISSING", missing.Error.Code);

        var textOnly = Path.Combine(root.Library, "text.zip");
        using (var zip = ZipFile.Open(textOnly, ZipArchiveMode.Create))
        {
            zip.CreateEntry("readme.txt");
        }

        Assert.Equal("ARCHIVE_NO_VPK", (await Assert.ThrowsAsync<S2ModKitException>(() => root.Catalog().ReadArchiveAsync(textOnly, token))).Error.Code);
        var garbage = Path.Combine(root.Library, "garbage.rar");
        await File.WriteAllBytesAsync(garbage, new byte[200], token);
        Assert.Equal("ARCHIVE_UNREADABLE", (await Assert.ThrowsAsync<S2ModKitException>(() => root.Catalog().ReadArchiveAsync(garbage, token))).Error.Code);
    }

    [Fact]
    public void VariantsGroupADirFileWithItsChunksInTheSameFolderOnly()
    {
        var variants = ArchiveVariants.Group([
            new ArchiveVpkEntry("b/x_dir.vpk", 10), new ArchiveVpkEntry("b/x_000.vpk", 100), new ArchiveVpkEntry("b/x_001.vpk", 100),
            new ArchiveVpkEntry("a/x_dir.vpk", 5), new ArchiveVpkEntry("a/x_000.vpk", 50), new ArchiveVpkEntry("a/other_000.vpk", 1),
            new ArchiveVpkEntry("single.vpk", 7), new ArchiveVpkEntry("orphan_003.vpk", 1),
        ]);

        Assert.Equal(["single.vpk", "a/x_dir.vpk", "b/x_dir.vpk"], variants.Select(variant => variant.DirEntryPath));   // the archive root first, then by folder
        Assert.Equal(55, variants.Single(variant => variant.Folder == "a").TotalSize);
        Assert.Equal(["b/x_dir.vpk", "b/x_000.vpk", "b/x_001.vpk"], variants.Single(variant => variant.Folder == "b").Files.Select(file => file.EntryPath));
    }

    [Fact]
    public void StagingLayoutKeepsLeafNamesInsideOneFolderPerVariantAndRefusesBadNames()
    {
        var variants = ArchiveVariants.Group([new ArchiveVpkEntry("a/x_dir.vpk", 1), new ArchiveVpkEntry("a/x_000.vpk", 1), new ArchiveVpkEntry("b/x_dir.vpk", 1)]);
        var layout = ArchiveVariants.Layout(variants, "stage");

        Assert.Equal([Path.Combine("stage", "v1", "x_dir.vpk"), Path.Combine("stage", "v2", "x_dir.vpk")], layout.Select(item => item.DirPath));
        Assert.Equal([Path.Combine("stage", "v1", "x_dir.vpk"), Path.Combine("stage", "v1", "x_000.vpk")], layout[0].Extractions.Select(item => item.TargetPath));
        Assert.Equal("ARCHIVE_NAME_INVALID", Assert.Throws<S2ModKitException>(() => ArchiveVariants.Layout([new ArchiveVariant("a/b:c_dir.vpk", "b:c_dir.vpk", "a", 1, [new ArchiveVpkEntry("a/b:c_dir.vpk", 1)])], "stage")).Error.Code);
    }

    [Fact]
    public async Task EnablingOneVersionDisablesItsSiblingsInOneOperationAndTheLibraryFollowsTheRenames()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak05_dir.vpk"), Model);                                    // the active version
        await root.VpkAsync(root.In("off_pak05_dir.vpk"), Model);                                // an older version that once sat in the same slot
        await root.VpkAsync(root.In("pak07_dir.vpk"), Sound);                                    // another mod, not a version
        var catalog = root.Catalog();
        await catalog.GroupVersionsAsync("Wraith skin", [("file:pak05_dir.vpk", "edited: no jacket"), ("file:off_pak05_dir.vpk", "original")], token);
        var mods = await catalog.LoadAsync(token);
        var original = ModWith(mods, "off_pak05_dir.vpk");
        Assert.Equal([("file:off_pak05_dir.vpk", "original", false), ("file:pak05_dir.vpk", "edited: no jacket", true)], original.VersionList.OrderBy(version => version.Key).Select(version => (version.Key, version.Label, version.Active)));
        Assert.Empty(ModWith(mods, "pak07_dir.vpk").VersionList);

        var change = await catalog.ActivateVersionAsync(original, mods, token);

        Assert.Equal(["off2_pak05_dir.vpk", "pak05_dir.vpk", "pak07_dir.vpk"], root.AddonNames());   // the sibling is disabled, the chosen one takes the freed slot
        Assert.Equal("file:pak05_dir.vpk", change.SelectKey);
        var after = await catalog.LoadAsync(token);
        Assert.Equal([("original", true), ("edited: no jacket", false)], ModWith(after, "pak05_dir.vpk").VersionList.Select(version => (version.Label, version.Active)).OrderByDescending(item => item.Item2));
        Assert.Single(Journals(root));                                                           // one operation for both renames
        var again = await catalog.ActivateVersionAsync(ModWith(after, "pak05_dir.vpk"), after, token);   // already active: nothing to do
        Assert.Contains("already the active version", again.Message, StringComparison.Ordinal);
        Assert.Single(Journals(root));
    }

    [Fact]
    public void TheLibraryRefusesRepeatedKeysAndAnUnknownSchemaAndDropsSmallGroups()
    {
        var now = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        var library = new ModLibraryDocument().WithVersions("Skin", [new ModLibraryVersion { Key = "k1", Label = "original" }, new ModLibraryVersion { Key = "k2", Label = "repaired 2026-11-10" }], now);

        Assert.Equal("mod-library@1", library.Schema);
        Assert.All(library.Mods[0].Versions, version => Assert.Equal(now, version.CreatedUtc));
        Assert.Throws<S2ModKitException>(() => library.WithVersions("Other", [new ModLibraryVersion { Key = "k1", Label = "x" }], now));
        Assert.Throws<S2ModKitException>(() => library.WithVersions("Skin", [new ModLibraryVersion { Key = "k2", Label = "x" }], now));
        Assert.Throws<S2ModKitException>(() => new ModLibraryDocument().WithVersions("Skin", [new ModLibraryVersion { Key = "a" }, new ModLibraryVersion { Key = "a" }], now));
        Assert.Equal(["k3"], library.WithVersions("Skin", [new ModLibraryVersion { Key = "k3", Label = "x" }], now).Mods[0].Versions.Skip(2).Select(version => version.Key));
        Assert.Empty(library.Without("k1").Mods);                            // one version left is no group
        Assert.Same(library, library.WithRenames([new AddonRename("zzz_dir.vpk", "yyy_dir.vpk")]));
    }

    [Fact]
    public async Task TheLibraryFileIsVersionedAndRejectsAnotherSchema()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(root.Data, "library.json");
        var store = new ModLibraryStore();
        await store.SaveAsync(path, new ModLibraryDocument().WithVersions("Skin", [new ModLibraryVersion { Key = "a", Label = "original" }], DateTimeOffset.UnixEpoch), token);

        Assert.Equal("original", (await store.LoadAsync(path, token)).Mods[0].Versions[0].Label);
        Assert.Contains("\"schema\": \"mod-library@1\"", await File.ReadAllTextAsync(path, token), StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, """{"schema":"mod-library@2","mods":[]}""", token);
        Assert.Equal("MOD_LIBRARY_SCHEMA", (await Assert.ThrowsAsync<S2ModKitException>(() => store.LoadAsync(path, token))).Error.Code);
    }

    [Theory]
    [InlineData("625513_pak32_dir.vpk", "625513", 32)]
    [InlineData("snd-82100_pak09_dir.vpk", "snd-82100", 9)]
    [InlineData("off_pak05_dir.vpk", null, 5)]
    [InlineData("off12_pak05_dir.vpk", null, 5)]
    [InlineData("local-9c5101cc_graves_in_ui.vpk", "local-9c5101cc", null)]
    [InlineData("561445_pak04-armstrong-type-a-84fa0_dir.vpk", "561445", null)]
    [InlineData("pak05_dir.vpk", null, 5)]
    public void TheSlotInADisabledNameIsReadOnlyFromTheKnownPrefixes(string name, string? modId, int? slot) =>
        Assert.Equal(slot, AddonNaming.OriginalSlot(name, modId));

    [Theory]
    [InlineData("625513", true)]
    [InlineData("snd-82100", true)]
    [InlineData("local-9c5101cc", true)]
    [InlineData("?", false)]
    [InlineData("../x", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyFileNameSafeModIdsBecomePrefixes(string? modId, bool safe) => Assert.Equal(safe, AddonNaming.IsSafeModId(modId));

    [Fact]
    public async Task ListingTheRealStyleFolderPlansTheNamesWithoutChangingIt()
    {
        using var root = new ModTestRoot();
        var token = TestContext.Current.CancellationToken;
        await root.VpkAsync(root.In("pak05_dir.vpk"), Sound);
        await root.VpkAsync(root.In("off_pak06_dir.vpk"), Model);
        var catalog = root.Catalog();
        var before = root.AddonNames();

        var mods = await catalog.LoadAsync(token);

        Assert.Equal(["pak05_dir.vpk -> off_pak05_dir.vpk"], (await catalog.PlanToggleAsync(ModWith(mods, "pak05_dir.vpk"), token)).Select(item => $"{item.From} -> {item.To}"));
        Assert.Equal(["off_pak06_dir.vpk -> pak06_dir.vpk"], (await catalog.PlanToggleAsync(ModWith(mods, "off_pak06_dir.vpk"), token)).Select(item => $"{item.From} -> {item.To}"));
        Assert.Equal(before, root.AddonNames());
        Assert.Empty(Journals(root));
    }

    private static void AddZipEntry(ZipArchive zip, string name, byte[] data)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(data);
    }

    private static AddonModOperations Operations(ModTestRoot root, IAddonFileOperations files) => new(files, root.GameProbe, root.Bin, new SystemClock());

    private sealed class NoOpBin : IRecycleBin
    {
        public void Recycle(IReadOnlyList<string> paths)
        {
        }
    }

    /// <summary>Real file operations that fail the n-th rename calls (1-based, forward and undo alike) or change a file after renaming it.</summary>
    private sealed class FlakyFiles(AddonFileOperations inner, int failFrom = int.MaxValue, int failUntil = int.MaxValue, bool touchAfterRename = false) : IAddonFileOperations
    {
        private int renames;

        public Task<IReadOnlyList<AddonFileStat>> ListFilesAsync(string addonsRoot, CancellationToken cancellationToken = default) => inner.ListFilesAsync(addonsRoot, cancellationToken);

        public async Task RenameAsync(string addonsRoot, string fromName, string toName, CancellationToken cancellationToken = default)
        {
            renames++;
            if (renames >= failFrom && renames <= failUntil)
            {
                throw new IOException($"simulated failure on rename {renames}");
            }

            await inner.RenameAsync(addonsRoot, fromName, toName, cancellationToken);
            if (touchAfterRename)
            {
                File.SetLastWriteTimeUtc(Path.Combine(addonsRoot, toName), DateTime.UtcNow.AddYears(-3));
            }
        }

        public Task CopyNewAsync(string sourcePath, string addonsRoot, string targetName, CancellationToken cancellationToken = default) => inner.CopyNewAsync(sourcePath, addonsRoot, targetName, cancellationToken);

        public Task DeleteCreatedAsync(string addonsRoot, string fileName, CancellationToken cancellationToken = default) => inner.DeleteCreatedAsync(addonsRoot, fileName, cancellationToken);

        public IReadOnlyList<string> FindChunksBeside(string packagePath) => inner.FindChunksBeside(packagePath);

        public Task WriteJournalAsync(string journalRoot, AddonOperationJournal journal, CancellationToken cancellationToken = default) => inner.WriteJournalAsync(journalRoot, journal, cancellationToken);
    }
}
