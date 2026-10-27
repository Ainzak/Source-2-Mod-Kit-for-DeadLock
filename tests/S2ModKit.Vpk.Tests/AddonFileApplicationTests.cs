using S2ModKit.Adapters.Vpk;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;

namespace S2ModKit.Vpk.Tests;

public sealed class AddonFileApplicationTests
{
    private const string Model = "models/heroes_wip/wraith/wraith.vmdl_c";
    private const string Texture = "models/heroes_wip/wraith/materials/body_color_png_1.vtex_c";
    private const string Sound = "sounds/vo/wraith/hello.vsnd_c";

    [Fact]
    public async Task ScanNamesManagerModsClassifiesKindsAndReusesTheCache()
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(directory, token);
        await WriteVpkAsync(Path.Combine(fixture.Addons, "pak03_dir.vpk"), token, Model, Texture);
        await WriteVpkAsync(Path.Combine(fixture.Addons, "4711_pak05_dir.vpk"), token, Sound);
        await File.WriteAllTextAsync(Path.Combine(fixture.Addons, ".dmm.json"),
            """{"version":3,"mods":{"777":{"enabled":true,"currentVpks":["pak03_dir.vpk"],"disabledVpks":[],"originalVpkNames":["pak03_dir.vpk"]},"4711":{"enabled":false,"currentVpks":[],"disabledVpks":["4711_pak05_dir.vpk"],"originalVpkNames":["pak05_dir.vpk"]}}}""", token);
        await File.WriteAllTextAsync(fixture.ManagerState,
            """{"local-config":"{\"state\":{\"localMods\":[{\"remoteId\":\"777\",\"name\":\"Wraith Skin\",\"status\":\"installed\",\"installedVpks\":[\"pak03_dir.vpk\"]},{\"remoteId\":\"4711\",\"name\":\"Voice Pack\",\"status\":\"downloaded\",\"installedVpks\":[]}]}}"}""", token);

        var first = await fixture.Application.ScanAsync(fixture.Addons, fixture.ManagerState, fixture.Cache, token);
        var second = await fixture.Application.ScanAsync(fixture.Addons, fixture.ManagerState, fixture.Cache, token);

        var skin = Assert.Single(first.Archives, item => item.FileName == "pak03_dir.vpk");
        Assert.True(skin.Loaded);
        Assert.Equal("Wraith Skin", skin.ModName);
        Assert.Equal(1, skin.ResourceKinds["model"]);
        Assert.Equal(1, skin.ResourceKinds["material"]);
        var voice = Assert.Single(first.Archives, item => item.FileName == "4711_pak05_dir.vpk");
        Assert.False(voice.Loaded);
        Assert.Equal("Voice Pack", voice.ModName);
        Assert.Equal(1, voice.ResourceKinds["sound"]);
        Assert.Equal(0, first.CachedArchives);
        Assert.Equal(2, second.CachedArchives);
    }

    [Fact]
    public async Task InstallAndRollbackTouchOnlyTheOwnedFile()
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(directory, token);
        var foreign = Path.Combine(fixture.Addons, "pak10_dir.vpk");
        await WriteVpkAsync(foreign, token, Sound);
        var foreignBefore = await HashAsync(foreign, token);
        var package = await fixture.PackageAsync(token, Model);

        var installed = await fixture.Application.InstallAsync(fixture.Request(package, "96"), token);
        var target = Path.Combine(fixture.Addons, "pak96_dir.vpk");
        var installedHash = await HashAsync(target, token);
        var again = await fixture.Application.InstallAsync(fixture.Request(package, "96"), token);
        var rolledBack = await fixture.Application.RollbackAsync(fixture.Addons, fixture.Receipts, token);
        var repeated = await fixture.Application.RollbackAsync(fixture.Addons, fixture.Receipts, token);

        Assert.Equal("installed", installed.Status);
        Assert.Equal(await HashAsync(package, token), installedHash);
        Assert.Equal("unchanged", again.Status);
        Assert.Equal("rolled_back", rolledBack.Status);
        Assert.Equal("unchanged", repeated.Status);
        Assert.False(File.Exists(target));
        Assert.True(File.Exists(package));
        Assert.Equal(foreignBefore, await HashAsync(foreign, token));
        Assert.Empty(Directory.EnumerateFiles(fixture.Addons, "*staging*"));
    }

    [Fact]
    public async Task InstallRefusesFilesChangedByAnEnabledModAndNamesIt()
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(directory, token);
        await WriteVpkAsync(Path.Combine(fixture.Addons, "pak03_dir.vpk"), token, Model);
        await File.WriteAllTextAsync(Path.Combine(fixture.Addons, ".dmm.json"),
            """{"mods":{"777":{"enabled":true,"currentVpks":["pak03_dir.vpk"],"originalVpkNames":["pak03_dir.vpk"]}}}""", token);
        await File.WriteAllTextAsync(fixture.ManagerState,
            """{"local-config":{"state":{"localMods":[{"remoteId":"777","name":"Wraith Skin","status":"installed","installedVpks":["pak03_dir.vpk"]}]}}}""", token);
        var package = await fixture.PackageAsync(token, Model, Texture);

        var error = await Assert.ThrowsAsync<S2ModKitException>(() => fixture.Application.InstallAsync(fixture.Request(package, "auto"), token));

        Assert.Equal("ADDON_FILE_COLLISION", error.Error.Code);
        Assert.Contains("Wraith Skin", error.Error.Summary, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.Receipts));
        Assert.Single(Directory.EnumerateFiles(fixture.Addons, "*.vpk"));
    }

    [Fact]
    public async Task InactiveManagerCopiesAndOtherResourceKindsDoNotBlockInstall()
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(directory, token);
        await WriteVpkAsync(Path.Combine(fixture.Addons, "4711_pak03_dir.vpk"), token, Model);
        await WriteVpkAsync(Path.Combine(fixture.Addons, "pak04_dir.vpk"), token, Sound);
        var package = await fixture.PackageAsync(token, Model);

        var installed = await fixture.Application.InstallAsync(fixture.Request(package, "auto"), token);

        Assert.Equal("pak99_dir.vpk", installed.Receipt!.TargetFileName);
    }

    [Fact]
    public async Task SlotsUsedByManagerRecordsAreNeverTaken()
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(directory, token);
        await File.WriteAllTextAsync(Path.Combine(fixture.Addons, ".dmm.json"),
            """{"mods":{"9":{"enabled":false,"currentVpks":[],"disabledVpks":["9_pak99_dir.vpk"],"originalVpkNames":["pak99_dir.vpk"]}}}""", token);
        var package = await fixture.PackageAsync(token, Model);

        var error = await Assert.ThrowsAsync<S2ModKitException>(() => fixture.Application.InstallAsync(fixture.Request(package, "99"), token));
        var automatic = await fixture.Application.InstallAsync(fixture.Request(package, "auto"), token);

        Assert.Equal("ADDON_SLOT_RESERVED", error.Error.Code);
        Assert.Equal("pak98_dir.vpk", automatic.Receipt!.TargetFileName);
    }

    [Fact]
    public async Task AnotherPackageNeedsRollbackFirstAndAChangedFileIsLeftInPlace()
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(directory, token);
        var first = await fixture.PackageAsync(token, Model);
        _ = await fixture.Application.InstallAsync(fixture.Request(first, "96"), token);
        var second = Path.Combine(fixture.Work, "second_dir.vpk");
        await WriteVpkAsync(second, token, Texture);

        var other = await Assert.ThrowsAsync<S2ModKitException>(() => fixture.Application.InstallAsync(fixture.Request(second, "95"), token));
        var target = Path.Combine(fixture.Addons, "pak96_dir.vpk");
        await File.AppendAllTextAsync(target, "changed by someone else", token);
        var drift = await Assert.ThrowsAsync<S2ModKitException>(() => fixture.Application.RollbackAsync(fixture.Addons, fixture.Receipts, token));

        Assert.Equal("ADDON_FILE_OTHER_PACKAGE_INSTALLED", other.Error.Code);
        Assert.Equal("ADDON_FILE_HASH_DRIFT", drift.Error.Code);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task AnIdenticalCopyInstalledOutsideTheToolIsReportedAsSuch()
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(directory, token);
        var package = await fixture.PackageAsync(token, Model);
        File.Copy(package, Path.Combine(fixture.Addons, "pak20_dir.vpk"));

        var error = await Assert.ThrowsAsync<S2ModKitException>(() => fixture.Application.InstallAsync(fixture.Request(package, "auto"), token));

        Assert.Contains("identical copy", error.Error.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("models/a/b.vmdl_c", "model")]
    [InlineData("materials/a/b.vtex_c", "material")]
    [InlineData("panorama/images/heroes/a.vtex_c", "ui")]
    [InlineData("particles/abilities/ghost/a.vpcf_c", "particles")]
    [InlineData("sounds/vo/a.vsnd_c", "sound")]
    [InlineData("resource/localization/citadel_gc_hero_names/a.txt", "localization")]
    [InlineData("README.txt", "other")]
    public void ResourceKindsSeparateIndependentChanges(string path, string kind) =>
        Assert.Equal(kind, AddonResourceKinds.Classify(path));

    private static async Task WriteVpkAsync(string path, CancellationToken token, params string[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await CompactVpkArchiveIO.WriteAsync(path, entries.ToDictionary(entry => entry, entry => (ReadOnlyMemory<byte>)System.Text.Encoding.UTF8.GetBytes(entry)), token);
    }

    private static async Task<ContentHash> HashAsync(string path, CancellationToken token) =>
        ContentHash.Compute(await File.ReadAllBytesAsync(path, token));

    private sealed record Fixture(string Addons, string Work, string Receipts, string ManagerState, string Cache, AddonFileApplication Application)
    {
        public static Task<Fixture> CreateAsync(TestDirectory directory, CancellationToken token)
        {
            _ = token;
            var addons = directory.CreateSubdirectory("addons");
            var work = directory.CreateSubdirectory("work");
            return Task.FromResult(new Fixture(
                addons,
                work,
                Path.Combine(work, "receipts"),
                Path.Combine(work, "state.json"),
                Path.Combine(work, "tree-cache.json"),
                new AddonFileApplication(new AddonFileStore(), new ValvePakAddonTreeReader(), new ModManagerStateReader(), new FixedClock())));
        }

        public async Task<string> PackageAsync(CancellationToken token, params string[] entries)
        {
            var path = Path.Combine(Work, "package", "diagnostic_dir.vpk");
            await WriteVpkAsync(path, token, entries);
            return path;
        }

        public AddonFileInstallRequest Request(string package, string slot) =>
            new(Addons, package, slot, Receipts, "Wraith body", ManagerState, Cache);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class TestDirectory : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "s2modkit-addon-file-tests", Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(root);
        public string CreateSubdirectory(string path) { var full = Path.Combine(root, path); Directory.CreateDirectory(full); return full; }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
