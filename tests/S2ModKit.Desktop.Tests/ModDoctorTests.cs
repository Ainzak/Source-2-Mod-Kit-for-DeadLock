using System.Text;
using S2ModKit.Adapters.Vpk;
using S2ModKit.Desktop;
using SteamDatabase.ValvePak;

namespace S2ModKit.Desktop.Tests;

public sealed class ModDoctorTests
{
    private static ModFacts Facts(string key, Dictionary<string, uint>? overrides = null, string[]? missing = null, Dictionary<string, IReadOnlyList<string>>? bones = null) =>
        new(key, key, overrides ?? [], missing ?? [], bones ?? []);

    [Fact]
    public void AFirstCheckOnlyRecordsTheBaseline()
    {
        var current = new DoctorSnapshot(100, DateTimeOffset.UnixEpoch, [Facts("a", missing: ["models/x/broken.vmat"])]);

        var finding = Assert.Single(DoctorRules.Compare(null, current));

        Assert.Equal(DoctorVerdict.Works, finding.Verdict);                  // a mod that works today is not flagged for what it always had
        Assert.Empty(finding.Reasons);
    }

    [Fact]
    public void AnOlderHeroModelAloneCanBeUpdatedAndIsNotAProblem()
    {
        var gaps = new Dictionary<string, ModelSettingsGap> { ["models/heroes_wip/ivy/ivy.vmdl_c"] = new(["TaggedSoundSettings_t"], ["healthbar"]) };
        var current = new DoctorSnapshot(100, DateTimeOffset.UnixEpoch, [Facts("a") with { ModelGaps = gaps }, Facts("b")]);
        var later = new DoctorSnapshot(101, DateTimeOffset.UnixEpoch, [Facts("a") with { ModelGaps = gaps }, Facts("b")]);

        foreach (var findings in new[] { DoctorRules.Compare(null, current), DoctorRules.Compare(current, later) })
        {
            var older = Assert.Single(findings, finding => finding.Key == "a");
            Assert.Equal(DoctorVerdict.Updatable, older.Verdict);
            Assert.Equal(["models/heroes_wip/ivy/ivy.vmdl_c"], older.RestoreModels);
            Assert.Empty(older.StaleOverrides);
            Assert.Contains("animation sounds", Assert.Single(older.Reasons), StringComparison.Ordinal);
            Assert.Equal(DoctorVerdict.Works, Assert.Single(findings, finding => finding.Key == "b").Verdict);
        }
    }

    [Fact]
    public void AfterAnUpdateEachKindOfChangeGetsItsVerdict()
    {
        var before = new DoctorSnapshot(100, DateTimeOffset.UnixEpoch,
        [
            Facts("data", new() { ["scripts/heroes.vdata_c"] = 1, ["models/a.vmdl_c"] = 7 }),
            Facts("model", new() { ["models/a.vmdl_c"] = 7 }),
            Facts("refs", missing: ["models/old/gone.vmat"]),
            Facts("bones", bones: new() { ["models/heroes_wip/x/x.vmdl_c"] = ["tail_0"] }),
            Facts("same", new() { ["scripts/abilities.vdata_c"] = 3 }),
        ]);
        var after = new DoctorSnapshot(101, DateTimeOffset.UnixEpoch,
        [
            Facts("data", new() { ["scripts/heroes.vdata_c"] = 2, ["models/a.vmdl_c"] = 7 }),
            Facts("model", new() { ["models/a.vmdl_c"] = 8 }),
            Facts("refs", missing: ["models/old/gone.vmat", "models/new/gone.vtex"]),
            Facts("bones", bones: new() { ["models/heroes_wip/x/x.vmdl_c"] = ["tail_0", "tail_1"] }),
            Facts("same", new() { ["scripts/abilities.vdata_c"] = 3 }),
            Facts("new mod"),
        ]);

        var findings = DoctorRules.Compare(before, after).ToDictionary(finding => finding.Key);

        Assert.Equal(DoctorVerdict.Repairable, findings["data"].Verdict);
        Assert.Equal(["scripts/heroes.vdata_c"], findings["data"].StaleOverrides);
        Assert.Equal(DoctorVerdict.Look, findings["model"].Verdict);
        Assert.Empty(findings["model"].StaleOverrides);                     // a model is the mod's own content: never dropped
        Assert.Equal(DoctorVerdict.Broken, findings["refs"].Verdict);
        Assert.Contains("models/new/gone.vtex", findings["refs"].Reasons[0], StringComparison.Ordinal);
        Assert.DoesNotContain("models/old/gone.vmat", findings["refs"].Reasons[0], StringComparison.Ordinal);
        Assert.Equal(DoctorVerdict.Look, findings["bones"].Verdict);
        Assert.Contains("tail_1", findings["bones"].Reasons[0], StringComparison.Ordinal);
        Assert.Equal(DoctorVerdict.Works, findings["same"].Verdict);
        Assert.Equal(DoctorVerdict.Works, findings["new mod"].Verdict);     // installed after the baseline: nothing to compare
    }

    [Theory]
    [InlineData("models/heroes_wip/x/materials/body.vmat", true)]
    [InlineData("models/heroes_wip/x/x.vanmgrph", false)]
    [InlineData("materials/default/default_mask_tga_2aa88af4.vtex", false)]
    [InlineData("eyes.vmat", false)]
    [InlineData("models/heroes_wip/x/x.vnmgraph", false)]
    public void OnlyReferencesThatShowInGameAreChecked(string reference, bool checkedReference) =>
        Assert.Equal(checkedReference, DoctorRules.IsCheckedReference(reference));

    [Fact]
    public void ReferencesAreReadFromTheResourceReferenceBlock()
    {
        var name = Encoding.UTF8.GetBytes("materials/x.vmat\0");
        var bytes = new byte[56 + name.Length];
        BitConverter.TryWriteBytes(bytes.AsSpan(8), 8u);                    // block table right after the header (offset 16)
        BitConverter.TryWriteBytes(bytes.AsSpan(12), 1u);
        Encoding.ASCII.GetBytes("RERL").CopyTo(bytes, 16);
        BitConverter.TryWriteBytes(bytes.AsSpan(20), 12u);                  // block at 32, relative to 20
        BitConverter.TryWriteBytes(bytes.AsSpan(32), 8u);                   // reference array at 40
        BitConverter.TryWriteBytes(bytes.AsSpan(36), 1u);
        BitConverter.TryWriteBytes(bytes.AsSpan(48), 8u);                   // name at 56, relative to 48
        name.CopyTo(bytes, 56);

        Assert.Equal(["materials/x.vmat"], DoctorScanner.References(bytes));
        Assert.Empty(DoctorScanner.References(new byte[10]));
    }

    [Fact]
    public async Task TheScannerSeesWhichGameFilesAModReplacesAndWhenTheGameChangesThem()
    {
        using var root = new ModTestRoot();
        await WriteGameAsync(root, "version 1");
        var mod = await root.VpkAsync(root.In("pak05_dir.vpk"), "scripts/heroes.vdata_c", "models/own/thing.vmdl_c");

        ModFacts before, after;
        using (var scanner = new DoctorScanner(root.Game))
        {
            before = scanner.Read("file:pak05_dir.vpk", "mod", [mod]);
        }

        await WriteGameAsync(root, "version 2");                             // possible only because the scanner closed the game's archives
        using (var scanner = new DoctorScanner(root.Game))
        {
            after = scanner.Read("file:pak05_dir.vpk", "mod", [mod]);
        }

        Assert.Equal(["scripts/heroes.vdata_c"], before.Overrides.Keys);    // the mod's own model is not a game file
        Assert.NotEqual(before.Overrides["scripts/heroes.vdata_c"], after.Overrides["scripts/heroes.vdata_c"]);
        var finding = Assert.Single(DoctorRules.Compare(new DoctorSnapshot(1, DateTimeOffset.UnixEpoch, [before]), new DoctorSnapshot(2, DateTimeOffset.UnixEpoch, [after])));
        Assert.Equal(DoctorVerdict.Repairable, finding.Verdict);
    }

    [Fact]
    public async Task ARepairInstallsACopyWithoutTheStaleFilesAndKeepsTheOriginalAsTheOldVersion()
    {
        using var root = new ModTestRoot();
        await root.VpkAsync(root.In("pak05_dir.vpk"), "scripts/heroes.vdata_c", "models/own/thing.vmdl_c");
        var catalog = root.Catalog();
        var mod = Assert.Single(await catalog.LoadAsync(TestContext.Current.CancellationToken));

        var change = await catalog.RepairAsync(mod, ["scripts/heroes.vdata_c"], TestContext.Current.CancellationToken);

        var mods = await catalog.LoadAsync(TestContext.Current.CancellationToken);
        var repaired = Assert.Single(mods, entry => entry.Loaded);
        Assert.Equal(change.SelectKey, repaired.Key);
        Assert.Equal(["models/own/thing.vmdl_c"], repaired.Entries);
        var original = Assert.Single(mods, entry => !entry.Loaded);
        Assert.Equal(["models/own/thing.vmdl_c", "scripts/heroes.vdata_c"], original.Entries.Order(StringComparer.Ordinal));   // unchanged, only disabled
        Assert.Equal(["old version", $"repaired {DateTimeOffset.UtcNow:yyyy-MM-dd}"], repaired.VersionList.Select(version => version.Label).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ARepairIsRefusedWhileTheGameRuns()
    {
        using var root = new ModTestRoot();
        await root.VpkAsync(root.In("pak05_dir.vpk"), "scripts/heroes.vdata_c", "models/own/thing.vmdl_c");
        var catalog = root.Catalog();
        var mod = Assert.Single(await catalog.LoadAsync(TestContext.Current.CancellationToken));
        root.GameProbe.Running = true;

        await Assert.ThrowsAnyAsync<Exception>(() => catalog.RepairAsync(mod, ["scripts/heroes.vdata_c"], TestContext.Current.CancellationToken));
        Assert.Equal(["pak05_dir.vpk"], root.AddonNames());
    }

    private static async Task WriteGameAsync(ModTestRoot root, string content)
    {
        foreach (var folder in new[] { "citadel", "core" })
        {
            var path = Path.Combine(root.Game, folder, "pak01_dir.vpk");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Delete(path);
            await CompactVpkArchiveIO.WriteAsync(path, new Dictionary<string, ReadOnlyMemory<byte>>
            {
                [folder == "citadel" ? "scripts/heroes.vdata_c" : "core/x.txt"] = Encoding.UTF8.GetBytes(content),
            }, CancellationToken.None);
        }
    }
}
