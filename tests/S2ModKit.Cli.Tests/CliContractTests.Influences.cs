using System.Text.Json;
using S2ModKit.Cli;

namespace S2ModKit.Cli.Tests;

public sealed partial class CliContractTests
{
    [Fact]
    public async Task InfluenceDiagnosticMissingInputReturnsFailureBeforeCreatingOutput()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"s2mod-influence-test-{Guid.NewGuid():N}");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var cli = new S2ModKitCli(new FakeApplication(), "test", "1", false);
        var result = await cli.RunAsync(["influences", "diagnose", "--input", Path.Combine(destination, "absent.vmdl_c"),
            "--resource-path", "models/synthetic.vmdl_c", "--output-root", destination, "--format", "json"],
            output, error, TestContext.Current.CancellationToken);
        Assert.NotEqual(0, result);
        Assert.False(Directory.Exists(destination));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("error", json.RootElement.GetProperty("status").GetString());
        Assert.Contains("INFLUENCE_INPUT_NOT_FOUND", output.ToString(), StringComparison.Ordinal);
    }
}
