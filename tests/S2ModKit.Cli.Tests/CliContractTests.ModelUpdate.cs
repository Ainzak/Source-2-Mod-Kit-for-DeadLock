using System.Text.Json;
using S2ModKit.Adapters.Vpk;
using S2ModKit.Cli;

namespace S2ModKit.Cli.Tests;

public sealed partial class CliContractTests
{
    [Fact]
    public async Task ModelsUpdateWritesNothingWhenNoHeroModelLacksAnything()
    {
        var folder = Path.Combine(Path.GetTempPath(), "s2mk-models-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var package = Path.Combine(folder, "mod_dir.vpk");
            await CompactVpkArchiveIO.WriteAsync(package, new Dictionary<string, ReadOnlyMemory<byte>> { ["sounds/a.vsnd_c"] = new byte[] { 1, 2, 3 } }, TestContext.Current.CancellationToken);
            var target = Path.Combine(folder, "out", "mod_dir.vpk");
            var cli = new S2ModKitCli(new FakeApplication(), new FakePackagingApplication(), new FakeAddonManagementApplication(), "test-adapter", "1", externalVerifierAvailable: false);
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await cli.RunAsync(["models", "update", "--package", package, "--game", folder, "--output", target, "--format", "json"], output, error, TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(output.ToString());

            Assert.Equal(0, exitCode);
            var result = document.RootElement.GetProperty("result");
            Assert.True(!result.TryGetProperty("outputSha256", out var hash) || hash.ValueKind == JsonValueKind.Null);   // no archive written
            Assert.Empty(result.GetProperty("models").EnumerateArray());
            Assert.False(File.Exists(target));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task ModelsUpdateRefusesAnExistingOutput()
    {
        var folder = Path.Combine(Path.GetTempPath(), "s2mk-models-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var target = Path.Combine(folder, "exists_dir.vpk");
            await File.WriteAllBytesAsync(target, [1], TestContext.Current.CancellationToken);
            var cli = new S2ModKitCli(new FakeApplication(), new FakePackagingApplication(), new FakeAddonManagementApplication(), "test-adapter", "1", externalVerifierAvailable: false);
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await cli.RunAsync(["models", "update", "--package", target, "--game", folder, "--output", target, "--format", "json"], output, error, TestContext.Current.CancellationToken);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("MODEL_UPDATE_OUTPUT_EXISTS", output.ToString() + error.ToString(), StringComparison.Ordinal);
            Assert.Equal([1], await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
