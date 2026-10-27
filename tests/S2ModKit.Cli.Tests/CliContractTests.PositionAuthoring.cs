using S2ModKit.Cli;

namespace S2ModKit.Cli.Tests;

public sealed partial class CliContractTests
{
    [Theory]
    [InlineData("export")]
    [InlineData("validate")]
    [InlineData("scene-create")]
    [InlineData("scene-extract")]
    [InlineData("import-recipe")]
    [InlineData("compose-recipe")]
    public async Task PositionAuthoringRoutesRequireExplicitInputsAndDocumentThem(string command)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var cli = new S2ModKitCli(new FakeApplication(), "test", "1", false);
        Assert.Equal(0, await cli.RunAsync(["authoring", command, "--help"], output, error, TestContext.Current.CancellationToken));
        Assert.Contains(command == "compose-recipe" ? "--import-recipe" : command == "export" ? "--selection" : "--bundle-sha256", output.ToString(), StringComparison.Ordinal);
        output.GetStringBuilder().Clear(); error.GetStringBuilder().Clear();
        Assert.NotEqual(0, await cli.RunAsync(["authoring", command], output, error, TestContext.Current.CancellationToken));
    }
}
