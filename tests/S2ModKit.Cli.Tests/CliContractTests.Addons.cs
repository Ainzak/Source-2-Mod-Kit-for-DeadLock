using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using S2ModKit.Application;
using S2ModKit.Cli;
using S2ModKit.Domain;

namespace S2ModKit.Cli.Tests;

public sealed partial class CliContractTests
{
    [Fact]
    public async Task AddonsInventoryWritesJsonOnlyToStandardOutput()
    {
        var addons = new FakeAddonManagementApplication();
        var cli = new S2ModKitCli(new FakeApplication(), new FakePackagingApplication(), addons, "test-adapter", "1", externalVerifierAvailable: false);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(
            ["addons", "inventory", "--addons-root", "addons", "--project", "project", "--package", "vpk-0123456789abcdef0123"],
            output,
            error,
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(output.ToString());

        Assert.Equal(0, exitCode);
        Assert.Equal("addons.inventory", document.RootElement.GetProperty("command").GetString());
        Assert.Equal("models/hero.vmdl_c", document.RootElement.GetProperty("result").GetProperty("logicalPath").GetString());
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("install", "addons.install")]
    [InlineData("verify-active", "addons.verify-active")]
    [InlineData("rollback", "addons.rollback")]
    public async Task AddonLifecycleCommandsWriteJsonOnlyToStandardOutput(string action, string expectedCommand)
    {
        var addons = new FakeAddonManagementApplication();
        var cli = new S2ModKitCli(new FakeApplication(), new FakePackagingApplication(), addons, "test-adapter", "1", externalVerifierAvailable: false);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var arguments = action == "install"
            ? new[] { "addons", action, "--addons-root", "addons", "--project", "project", "--package", "vpk-0123456789abcdef0123", "--slot", "99" }
            : new[] { "addons", action, "--addons-root", "addons", "--project", "project", "--installation", "install-test" };

        var exitCode = await cli.RunAsync(arguments, output, error, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(output.ToString());

        Assert.Equal(0, exitCode);
        Assert.Equal(expectedCommand, document.RootElement.GetProperty("command").GetString());
        Assert.Equal("install-test", document.RootElement.GetProperty("result").GetProperty("receipt").GetProperty("installationId").GetString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task RuntimeRecordWritesJsonOnlyToStandardOutput()
    {
        var observationPath = Path.Combine(Path.GetTempPath(), $"s2modkit-observation-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            observationPath,
            """{"schemaVersion":1,"status":"passed","checks":{"targetComponent":"passed","preservedMaterialsAndParts":"passed","animations":"passed","lodTransitions":"passed","menuPreview":"not_checked","deathAndRespawn":"not_checked"},"notes":[],"extensions":{}}""",
            TestContext.Current.CancellationToken);
        try
        {
            var cli = new S2ModKitCli(new FakeApplication(), new FakePackagingApplication(), new FakeAddonManagementApplication(), "test-adapter", "1", externalVerifierAvailable: false);
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await cli.RunAsync(
                ["runtime", "record", "--project", "project", "--installation", "install-test", "--observation", observationPath],
                output,
                error,
                TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(output.ToString());

            Assert.Equal(0, exitCode);
            Assert.Equal("runtime.record", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("player_observed", document.RootElement.GetProperty("result").GetProperty("observation").GetProperty("proofLevel").GetString());
            Assert.Empty(error.ToString());
        }
        finally
        {
            File.Delete(observationPath);
        }
    }

}
