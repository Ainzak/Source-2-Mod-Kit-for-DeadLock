using S2ModKit.Application;
using S2ModKit.Cli;
using S2ModKit.Domain;

namespace S2ModKit.Cli.Tests;

public sealed partial class CliContractTests
{
    [Fact]
    public async Task ExperimentalDiscoveryContextDoesNotChangeStrictRemoveOperation()
    {
        var app = new FakeApplication();
        var cli = new S2ModKitCli(app, "test", "1", false);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await cli.RunAsync(["recipe", "scaffold", "--project", "project", "--component", "cmp_0123456789abcdef01234567",
            "--intent", "remove", "--output", "unused.json", "--experimental"], output, error, TestContext.Current.CancellationToken));
        Assert.True(app.LastScaffoldRequest!.ExperimentalDiscovery);
        Assert.Null(app.LastScaffoldRequest.Experimental);
    }

    [Theory]
    [InlineData(false, "region-scale", "z", "0", "1", "0,0,0")]
    [InlineData(true, "region-scale", "bad", "0", "1", "0,0,0")]
    [InlineData(true, "region-scale", "z", "0", "1", null)]
    [InlineData(true, "uniform-scale", "z", "0", "1", "0,0,0")]
    [InlineData(true, "remove", "z", "0", "1", "0,0,0")]
    public async Task ExperimentalScaffoldRejectsMissingOptInAndIgnoredOptions(bool experimental, string intent, string axis, string pinned, string full, string? pivot)
    {
        var app = new FakeApplication();
        var cli = new S2ModKitCli(app, "test", "1", false);
        var args = new List<string> { "recipe", "scaffold", "--project", "project", "--component", "cmp_0123456789abcdef01234567", "--intent", intent,
            "--output", "unused.json", "--scale", "1.5", "--max-displacement", "64", "--region-axis", axis, "--pinned-through", pinned, "--full-from", full };
        if (experimental) args.Add("--experimental");
        if (pivot is not null) args.AddRange(["--pivot-point", pivot]);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await cli.RunAsync(args.ToArray(), output, error, TestContext.Current.CancellationToken));
        Assert.Null(app.LastScaffoldRequest);
        var diagnostic = output.ToString() + error.ToString();
        Assert.True(diagnostic.Contains("SCAFFOLD_", StringComparison.Ordinal) || diagnostic.Contains("REGION_", StringComparison.Ordinal), diagnostic);
    }
}
