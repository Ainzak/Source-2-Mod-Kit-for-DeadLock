using S2ModKit.Cli;
namespace S2ModKit.Cli.Tests;

public sealed partial class CliContractTests
{
    [Theory]
    [InlineData("discover")]
    [InlineData("inspect")]
    [InlineData("scaffold")]
    [InlineData("review")]
    public async Task PairedRouteRequiresOptInBeforeApplicationOrFileAccess(string command)
    {
        var args = new List<string> { "paired", command, "--project", "unused" };
        if (command is "inspect" or "scaffold") args.AddRange(["--component", "cmp_unused"]);
        if (command is "inspect" or "review") args.AddRange(["--output-root", "unused"]);
        if (command == "scaffold") args.AddRange(["--options", "absent.json", "--output", "unused"]);
        if (command == "review") args.AddRange(["--recipe", "absent.json"]);
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2, await new S2ModKitCli(new FakeApplication(), "test", "1", false).RunAsync(args.ToArray(), output, error, TestContext.Current.CancellationToken));
        Assert.Contains("PAIRED_OPT_IN_REQUIRED", output.ToString(), StringComparison.Ordinal); Assert.Empty(error.ToString());
    }
}
