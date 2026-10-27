using System.Text.Json;
using S2ModKit.Application;

namespace S2ModKit.Cli.Tests;

public sealed partial class CliContractTests
{
    [Fact]
    public async Task ModsStatusPassesEveryReceiptsFolderAndPrintsOneLinePerMod()
    {
        var status = new RecordingModStatusApplication();
        var cli = new S2ModKitCli(new FakeApplication(), "test-adapter", "1", externalVerifierAvailable: false) { ModStatusApplication = status };
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(
            ["mods", "status", "--game", "game", "--addons-root", "addons", "--state", "state", "--receipts", "r1", "--receipts", "r2", "--manager-state", "manager.json"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.Equal(["r1", "r2"], status.Status!.ReceiptsRoots);
        Assert.Equal("manager.json", status.Status.ManagerStatePath);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Game build 6821 (Oct 26 2026); new build recorded", lines[0]);
        Assert.Equal(4, lines.Length);
        Assert.Contains("pak03_dir.vpk", lines[2], StringComparison.Ordinal);
        Assert.Equal("1 acknowledged (hidden).", lines[3]);
    }

    [Fact]
    public async Task ModsStatusJsonUsesTheCliEnvelopeAndKeepsAcknowledgedNotices()
    {
        var cli = new S2ModKitCli(new FakeApplication(), "test-adapter", "1", externalVerifierAvailable: false) { ModStatusApplication = new RecordingModStatusApplication() };
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(
            ["mods", "status", "--game", "game", "--addons-root", "addons", "--state", "state", "--format", "json"],
            output,
            error,
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(output.ToString());

        Assert.Equal(0, exitCode);
        Assert.Equal("mods.status", document.RootElement.GetProperty("command").GetString());
        var mods = document.RootElement.GetProperty("result").GetProperty("mods");
        Assert.Equal(2, mods.GetArrayLength());
        Assert.True(mods[1].GetProperty("acknowledged").GetBoolean());
    }

    [Fact]
    public async Task ModsAckDefaultsTheAddonsFolderBelowTheGameFolder()
    {
        var status = new RecordingModStatusApplication();
        var cli = new S2ModKitCli(new FakeApplication(), "test-adapter", "1", externalVerifierAvailable: false) { ModStatusApplication = status };
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(["mods", "ack", "--game", "game", "--state", "state", "--all"], output, error, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(Path.Combine("game", "citadel", "addons"), status.Ack!.AddonsRoot);
        Assert.True(status.Ack.All);
        Assert.Null(status.Ack.FileName);
        Assert.Equal("Acknowledged 1 mod(s) for build 6821.", output.ToString().Trim());
    }

    private sealed class RecordingModStatusApplication : IModStatusApplication
    {
        public ModStatusRequest? Status { get; private set; }

        public ModAckRequest? Ack { get; private set; }

        public Task<ModStatusReport> StatusAsync(ModStatusRequest request, CancellationToken cancellationToken = default)
        {
            Status = request;
            var kinds = new Dictionary<string, int> { ["model"] = 1 };
            return Task.FromResult(new ModStatusReport(
                6821,
                "Oct 26 2026",
                NewBuildRecorded: true,
                "state",
                NoticeCount: 1,
                AcknowledgedCount: 1,
                [
                    new ModStatusEntry("pak03_dir.vpk", "Wraith Skin", kinds, "third_party", ModStatusStates.MayBeOutdated, "it may be outdated", Notice: true, Acknowledged: false, null),
                    new ModStatusEntry("pak04_dir.vpk", null, kinds, "third_party", ModStatusStates.MayBeOutdated, "it may be outdated", Notice: true, Acknowledged: true, null),
                ]));
        }

        public Task<ModAckResult> AcknowledgeAsync(ModAckRequest request, CancellationToken cancellationToken = default)
        {
            Ack = request;
            return Task.FromResult(new ModAckResult(6821, ["pak03_dir.vpk"]));
        }
    }
}
