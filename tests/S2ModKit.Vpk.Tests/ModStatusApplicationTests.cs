using System.Text;
using System.Text.Json;
using S2ModKit.Adapters.Vpk;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;

namespace S2ModKit.Vpk.Tests;

public sealed class ModStatusApplicationTests
{
    private const string Model = "models/heroes_wip/wraith/wraith.vmdl_c";
    private const string SmallData = "scripts/heroes/wraith.vdata_c";
    private const string BigData = "scripts/heroes/big.vdata_c";
    private const string Localization = "resource/localization/citadel_gc_hero_names/hero_names_english.txt";
    private const string Sound = "sounds/vo/wraith/hello.vsnd_c";
    private const string Untouched = "models/heroes_wip/other/other.vmdl_c";
    private static readonly DateTime BeforeUpdate = new(2026, 10, 25, 23, 59, 59, DateTimeKind.Utc);
    private static readonly DateTime OnUpdateDay = new(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ANewBuildIsRecordedOnceWithOnlyTheOverriddenBaseFiles()
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var fixture = await GameFixture.CreateAsync(directory, token);
        await fixture.AddAddonAsync("pak03_dir.vpk", BeforeUpdate, token, (Model, "addon model"), (SmallData, "addon data"), (BigData, "addon big"), (Localization, "addon text"), (Sound, "addon sound"), ("models/new/only_in_addon.vmdl_c", "new"));
        await fixture.AddAddonAsync("4711_pak05_dir.vpk", BeforeUpdate, token, (Untouched, "inactive copy"));

        var first = await GameFixture.App().StatusAsync(fixture.Request(), token);
        var buildDirectory = Path.Combine(fixture.State, "builds", "6821");
        var gameFiles = Path.Combine(buildDirectory, "game-files.json");
        var gameFilesBytes = await File.ReadAllBytesAsync(gameFiles, token);
        var buildJsonTime = File.GetLastWriteTimeUtc(Path.Combine(buildDirectory, "build.json"));
        var second = await GameFixture.App().StatusAsync(fixture.Request(), token);

        Assert.True(first.NewBuildRecorded);
        Assert.False(second.NewBuildRecorded);
        Assert.Equal(6821, first.Build);
        Assert.Equal("Oct 26 2026", first.VersionDate);
        using var build = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(buildDirectory, "build.json"), token));
        Assert.Equal(6821, build.RootElement.GetProperty("build").GetInt32());
        Assert.Equal("Oct 26 2026", build.RootElement.GetProperty("versionDate").GetString());
        Assert.True(build.RootElement.TryGetProperty("firstSeenUtc", out _));

        var archive = await CompactVpkArchiveIO.ReadAndVerifyAsync(fixture.BaseVpk, token);
        using var document = JsonDocument.Parse(gameFilesBytes);
        var files = document.RootElement.GetProperty("files").EnumerateArray().ToDictionary(item => item.GetProperty("path").GetString()!, StringComparer.Ordinal);
        Assert.Equal(new[] { BigData, Localization, Model, SmallData, Sound }.Order(StringComparer.Ordinal).ToArray(), files.Keys.Order(StringComparer.Ordinal).ToArray());
        foreach (var (path, item) in files)
        {
            var expected = archive.Entries.Single(entry => entry.LogicalPath == path);
            Assert.Equal(expected.Crc32.ToString("x8", System.Globalization.CultureInfo.InvariantCulture), item.GetProperty("crc32").GetString());
            Assert.Equal(expected.Length, item.GetProperty("size").GetInt64());
        }

        Assert.Equal("base data", await File.ReadAllTextAsync(Path.Combine(buildDirectory, "files", SmallData), token));
        Assert.Equal("base text", await File.ReadAllTextAsync(Path.Combine(buildDirectory, "files", Localization), token));
        Assert.False(File.Exists(Path.Combine(buildDirectory, "files", BigData)));
        Assert.False(File.Exists(Path.Combine(buildDirectory, "files", Model)));
        Assert.False(File.Exists(Path.Combine(buildDirectory, "files", Sound)));
        Assert.Equal(gameFilesBytes, await File.ReadAllBytesAsync(gameFiles, token));
        Assert.Equal(buildJsonTime, File.GetLastWriteTimeUtc(Path.Combine(buildDirectory, "build.json")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(fixture.State, "builds"), ".tmp-*"));
    }

    [Fact]
    public async Task ThirdPartyModsAreComparedWithTheDayOfTheGameUpdate()
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var fixture = await GameFixture.CreateAsync(directory, token);
        await fixture.AddAddonAsync("pak03_dir.vpk", BeforeUpdate, token, (Model, "older"));
        await fixture.AddAddonAsync("pak04_dir.vpk", OnUpdateDay, token, (Sound, "newer"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Addons, ".dmm.json"),
            """{"mods":{"777":{"enabled":true,"currentVpks":["pak03_dir.vpk"],"originalVpkNames":["pak03_dir.vpk"]}}}""", token);
        await File.WriteAllTextAsync(fixture.ManagerState,
            """{"local-config":{"state":{"localMods":[{"remoteId":"777","name":"Wraith Skin","status":"installed","installedVpks":["pak03_dir.vpk"]}]}}}""", token);

        var report = await GameFixture.App().StatusAsync(fixture.Request(), token);

        var older = Assert.Single(report.Mods, item => item.FileName == "pak03_dir.vpk");
        Assert.Equal("Wraith Skin", older.ModName);
        Assert.Equal(1, older.ResourceKinds["model"]);
        Assert.Equal("third_party", older.Source);
        Assert.Equal(ModStatusStates.MayBeOutdated, older.State);
        Assert.True(older.Notice);
        Assert.Equal("game updated on Oct 26 2026 after this mod was installed; it may be outdated", older.Message);
        var newer = Assert.Single(report.Mods, item => item.FileName == "pak04_dir.vpk");
        Assert.Equal(ModStatusStates.InstalledAfterUpdate, newer.State);
        Assert.False(newer.Notice);
        Assert.Equal(1, report.NoticeCount);
        Assert.Equal(0, report.AcknowledgedCount);
    }

    [Fact]
    public async Task ModsInstalledByTheToolReportTheBuildTheyWereMadeFor()
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var fixture = await GameFixture.CreateAsync(directory, token, build: 6820);
        var package = await fixture.PackageAsync("package_dir.vpk", token, (Model, "ours"));
        var installer = GameFixture.Installer();
        var receipts = Path.Combine(fixture.Work, "receipts");
        _ = await installer.InstallAsync(new AddonFileInstallRequest(fixture.Addons, package, "96", receipts, "Wraith body", fixture.ManagerState, null), token);
        var secondPackage = await fixture.PackageAsync("second_dir.vpk", token, (Sound, "no build recorded"));
        var unknownReceipts = Path.Combine(fixture.Work, "receipts-unknown");
        _ = await new AddonFileApplication(new AddonFileStore(), new ValvePakAddonTreeReader(), new ModManagerStateReader(), new FixedClock())
            .InstallAsync(new AddonFileInstallRequest(fixture.Addons, secondPackage, "95", unknownReceipts, "Voice", fixture.ManagerState, null), token);
        var missing = Path.Combine(fixture.Work, "no-such-receipts");

        var sameBuild = await GameFixture.App().StatusAsync(fixture.Request(receipts, unknownReceipts, missing), token);
        fixture.SetBuild(6821, "Oct 26 2026");
        var nextBuild = await GameFixture.App().StatusAsync(fixture.Request(receipts, unknownReceipts, missing), token);
        var withoutReceipts = await GameFixture.App().StatusAsync(fixture.Request(), token);

        var current = Assert.Single(sameBuild.Mods, item => item.FileName == "pak96_dir.vpk");
        Assert.Equal(ModStatusStates.Current, current.State);
        Assert.Equal(6820, current.InstalledOnBuild);
        Assert.Equal("Wraith body", current.ModName);
        Assert.False(current.Notice);
        var outdated = Assert.Single(nextBuild.Mods, item => item.FileName == "pak96_dir.vpk");
        Assert.Equal(ModStatusStates.OutdatedBuild, outdated.State);
        Assert.Equal("s2modkit", outdated.Source);
        Assert.Equal("installed on build 6820; game is now build 6821", outdated.Message);
        Assert.True(outdated.Notice);
        var unknown = Assert.Single(nextBuild.Mods, item => item.FileName == "pak95_dir.vpk");
        Assert.Equal(ModStatusStates.UnknownBuild, unknown.State);
        Assert.True(unknown.Notice);
        Assert.All(withoutReceipts.Mods, item => Assert.Equal("third_party", item.Source));
    }

    [Fact]
    public async Task AFileThatNoLongerMatchesItsReceiptIsTreatedAsThirdParty()
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var fixture = await GameFixture.CreateAsync(directory, token);
        var package = await fixture.PackageAsync("package_dir.vpk", token, (Model, "ours"));
        var receipts = Path.Combine(fixture.Work, "receipts");
        _ = await GameFixture.Installer().InstallAsync(new AddonFileInstallRequest(fixture.Addons, package, "96", receipts, "Wraith body", fixture.ManagerState, null), token);
        await File.AppendAllTextAsync(Path.Combine(fixture.Addons, "pak96_dir.vpk"), "edited elsewhere", token);

        var report = await GameFixture.App().StatusAsync(fixture.Request(receipts), token);

        Assert.Equal("third_party", Assert.Single(report.Mods).Source);
    }

    [Fact]
    public async Task AcknowledgingHidesOnlyThatNoticeForThatFileVersionAndBuild()
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var fixture = await GameFixture.CreateAsync(directory, token);
        await fixture.AddAddonAsync("pak03_dir.vpk", BeforeUpdate, token, (Model, "one"));
        await fixture.AddAddonAsync("pak04_dir.vpk", BeforeUpdate, token, (Sound, "two"));
        var app = GameFixture.App();

        var ack = await app.AcknowledgeAsync(new ModAckRequest(fixture.Game, fixture.Addons, fixture.State, "pak03_dir.vpk", false), token);
        var afterOne = await app.StatusAsync(fixture.Request(), token);
        await fixture.AddAddonAsync("pak03_dir.vpk", BeforeUpdate, token, (Model, "one"), (Sound, "now with a second file"));
        var replaced = await app.StatusAsync(fixture.Request(), token);
        File.SetLastWriteTimeUtc(Path.Combine(fixture.Addons, "pak04_dir.vpk"), BeforeUpdate.AddHours(-1));
        _ = await app.AcknowledgeAsync(new ModAckRequest(fixture.Game, fixture.Addons, fixture.State, null, true), token);
        var afterAll = await app.StatusAsync(fixture.Request(), token);
        File.SetLastWriteTimeUtc(Path.Combine(fixture.Addons, "pak04_dir.vpk"), BeforeUpdate.AddHours(-2));
        var touched = await app.StatusAsync(fixture.Request(), token);
        fixture.SetBuild(6822, "Oct 28 2026");
        var nextBuild = await app.StatusAsync(fixture.Request(), token);

        Assert.Equal(["pak03_dir.vpk"], ack.Acknowledged);
        Assert.True(afterOne.Mods.Single(item => item.FileName == "pak03_dir.vpk").Acknowledged);
        Assert.False(afterOne.Mods.Single(item => item.FileName == "pak04_dir.vpk").Acknowledged);
        Assert.Equal(1, afterOne.NoticeCount);
        Assert.Equal(1, afterOne.AcknowledgedCount);
        Assert.False(replaced.Mods.Single(item => item.FileName == "pak03_dir.vpk").Acknowledged);
        Assert.Equal(2, afterAll.AcknowledgedCount);
        Assert.Equal(0, afterAll.NoticeCount);
        Assert.False(touched.Mods.Single(item => item.FileName == "pak04_dir.vpk").Acknowledged);
        Assert.True(touched.Mods.Single(item => item.FileName == "pak03_dir.vpk").Acknowledged);
        Assert.Equal(0, nextBuild.AcknowledgedCount);
        Assert.Equal(2, nextBuild.NoticeCount);
        Assert.True(File.Exists(Path.Combine(fixture.State, "acks", "6821.json")));
    }

    [Fact]
    public async Task AcknowledgeRejectsUnknownFilesAndAmbiguousSelectors()
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var fixture = await GameFixture.CreateAsync(directory, token);
        await fixture.AddAddonAsync("pak03_dir.vpk", BeforeUpdate, token, (Model, "one"));
        await fixture.AddAddonAsync("4711_pak05_dir.vpk", BeforeUpdate, token, (Sound, "not loaded"));
        var app = GameFixture.App();

        var unknown = await Assert.ThrowsAsync<S2ModKitException>(() => app.AcknowledgeAsync(new ModAckRequest(fixture.Game, fixture.Addons, fixture.State, "pak09_dir.vpk", false), token));
        var notLoaded = await Assert.ThrowsAsync<S2ModKitException>(() => app.AcknowledgeAsync(new ModAckRequest(fixture.Game, fixture.Addons, fixture.State, "4711_pak05_dir.vpk", false), token));
        var both = await Assert.ThrowsAsync<S2ModKitException>(() => app.AcknowledgeAsync(new ModAckRequest(fixture.Game, fixture.Addons, fixture.State, "pak03_dir.vpk", true), token));
        var neither = await Assert.ThrowsAsync<S2ModKitException>(() => app.AcknowledgeAsync(new ModAckRequest(fixture.Game, fixture.Addons, fixture.State, null, false), token));

        Assert.Equal("MODS_ACK_FILE_UNKNOWN", unknown.Error.Code);
        Assert.Equal("MODS_ACK_FILE_UNKNOWN", notLoaded.Error.Code);
        Assert.Equal("MODS_ACK_SELECTOR_INVALID", both.Error.Code);
        Assert.Equal("MODS_ACK_SELECTOR_INVALID", neither.Error.Code);
    }

    [Theory]
    [InlineData("ProductName=citadel\nVersionDate=Oct 26 2026\n")]
    [InlineData("ClientVersion=abc\nVersionDate=Oct 26 2026\n")]
    [InlineData("ClientVersion=0\nVersionDate=Oct 26 2026\n")]
    [InlineData("ClientVersion=6821\n")]
    [InlineData("ClientVersion=6821\nVersionDate=26th of October\n")]
    [InlineData("ClientVersion=6821\nClientVersion=6822\nVersionDate=Oct 26 2026\n")]
    public async Task AMalformedSteamInfFailsClosedAndWritesNoState(string steamInf)
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var fixture = await GameFixture.CreateAsync(directory, token);
        await File.WriteAllTextAsync(Path.Combine(fixture.Citadel, "steam.inf"), steamInf, token);

        var error = await Assert.ThrowsAsync<S2ModKitException>(() => GameFixture.App().StatusAsync(fixture.Request(), token));

        Assert.Equal("GAME_STEAM_INF_INVALID", error.Error.Code);
        Assert.Contains("steam.inf", error.Error.Summary, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.State));
    }

    [Fact]
    public async Task AMissingSteamInfIsReportedAsSuch()
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var fixture = await GameFixture.CreateAsync(directory, token);
        File.Delete(Path.Combine(fixture.Citadel, "steam.inf"));

        var error = await Assert.ThrowsAsync<S2ModKitException>(() => GameFixture.App().StatusAsync(fixture.Request(), token));

        Assert.Equal("GAME_STEAM_INF_NOT_FOUND", error.Error.Code);
    }

    [Fact]
    public void SteamInfParsingAcceptsPaddedDaysAndWindowsLineEndings()
    {
        var info = SteamInf.Parse("ClientVersion=6821\r\nServerVersion=6821\r\nVersionDate=Oct  6 2026\r\nVersionTime=11:14:53\r\n", "test");

        Assert.Equal(6821, info.Build);
        Assert.Equal(new DateOnly(2026, 10, 6), info.VersionDay);
        Assert.Equal("Oct  6 2026", info.VersionDate);
    }

    [Fact]
    public async Task InstallRecordsTheGameBuildOnlyInsideAGameFolder()
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var fixture = await GameFixture.CreateAsync(directory, token, build: 6820);
        var package = await fixture.PackageAsync("package_dir.vpk", token, (Model, "ours"));
        var outside = directory.CreateSubdirectory("elsewhere/addons");

        var inside = await GameFixture.Installer().InstallAsync(new AddonFileInstallRequest(fixture.Addons, package, "96", Path.Combine(fixture.Work, "r1"), "Wraith body", fixture.ManagerState, null), token);
        var notInGame = await GameFixture.Installer().InstallAsync(new AddonFileInstallRequest(outside, package, "96", Path.Combine(fixture.Work, "r2"), "Wraith body", fixture.ManagerState, null), token);

        Assert.Equal(6820, inside.Receipt!.GameBuild);
        Assert.Null(notInGame.Receipt!.GameBuild);
        var receipts = await new AddonFileStore().ListReceiptsAsync(Path.Combine(fixture.Work, "r1"), token);
        Assert.Equal(6820, Assert.Single(receipts).GameBuild);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class GameFixture
    {
        private GameFixture(string root)
        {
            Game = Path.Combine(root, "game");
            Citadel = Path.Combine(Game, "citadel");
            Addons = Path.Combine(Citadel, "addons");
            Work = Path.Combine(root, "work");
            State = Path.Combine(root, "state");
            ManagerState = Path.Combine(Work, "state.json");
            BaseVpk = Path.Combine(Citadel, "pak01_dir.vpk");
            Directory.CreateDirectory(Addons);
            Directory.CreateDirectory(Work);
        }

        public string Game { get; }

        public string Citadel { get; }

        public string Addons { get; }

        public string Work { get; }

        public string State { get; }

        public string ManagerState { get; }

        public string BaseVpk { get; }

        public static async Task<GameFixture> CreateAsync(TestDirectory directory, CancellationToken token, int build = 6821)
        {
            var fixture = new GameFixture(directory.CreateSubdirectory("root"));
            fixture.SetBuild(build, "Oct 26 2026");
            var entries = new Dictionary<string, ReadOnlyMemory<byte>>
            {
                [Model] = Encoding.UTF8.GetBytes("base model"),
                [SmallData] = Encoding.UTF8.GetBytes("base data"),
                [BigData] = new byte[(1024 * 1024) + 1],
                [Localization] = Encoding.UTF8.GetBytes("base text"),
                [Sound] = Encoding.UTF8.GetBytes("base sound"),
                [Untouched] = Encoding.UTF8.GetBytes("base other"),
            };
            await CompactVpkArchiveIO.WriteAsync(fixture.BaseVpk, entries, token);
            return fixture;
        }

        public void SetBuild(int build, string date) =>
            File.WriteAllText(Path.Combine(Citadel, "steam.inf"), $"ClientVersion={build}\nServerVersion={build}\nProductName=citadel\nVersionDate={date}\nVersionTime=11:14:53\n");

        public async Task AddAddonAsync(string fileName, DateTime lastWriteUtc, CancellationToken token, params (string Path, string Text)[] entries)
        {
            var path = Path.Combine(Addons, fileName);
            await WriteVpkAsync(path, entries, token);
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
        }

        public async Task<string> PackageAsync(string fileName, CancellationToken token, params (string Path, string Text)[] entries)
        {
            var path = Path.Combine(Work, "package", fileName);
            await WriteVpkAsync(path, entries, token);
            return path;
        }

        public static ModStatusApplication App() => new(
            new AddonFileStore(),
            new ValvePakAddonTreeReader(),
            new ModManagerStateReader(),
            new GameBuildReader(),
            new ValvePakBaseGameArchiveFactory(),
            new ModStatusStore(),
            new FixedClock());

        public static AddonFileApplication Installer() =>
            new(new AddonFileStore(), new ValvePakAddonTreeReader(), new ModManagerStateReader(), new FixedClock(), new GameBuildReader());

        public ModStatusRequest Request(params string[] receipts) => new(Game, Addons, State, receipts, ManagerState);

        private static async Task WriteVpkAsync(string path, (string Path, string Text)[] entries, CancellationToken token)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.Delete(path);
            await CompactVpkArchiveIO.WriteAsync(
                path,
                entries.ToDictionary(entry => entry.Path, entry => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(entry.Text), StringComparer.Ordinal),
                token);
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "s2modkit-mod-status-tests", Guid.NewGuid().ToString("N"));

        public TestDirectory() => Directory.CreateDirectory(root);

        public string CreateSubdirectory(string path)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(full);
            return full;
        }

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
