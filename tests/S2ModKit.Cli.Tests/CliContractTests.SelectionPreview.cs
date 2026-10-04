using S2ModKit.Cli;

namespace S2ModKit.Cli.Tests;

public sealed partial class CliContractTests
{
    [Fact]
    public async Task SelectionPreviewRequiresAnExplicitOutputRoot()
    {
        var app = new FakeApplication();
        var cli = new S2ModKitCli(app, "test", "1", false);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await cli.RunAsync(["selection-preview", "--project", "project", "--recipe", "unused.json"], output, error, TestContext.Current.CancellationToken));
        Assert.Contains("--output-root", output.ToString() + error.ToString(), StringComparison.Ordinal);
        Assert.Null(app.LastRecipe);
    }

    [Fact]
    public async Task InvalidPreviewRecipeReturnsDiagnosticsWithoutPublishing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"s2modkit-preview-recipe-{Guid.NewGuid():N}.json");
        var root = Path.Combine(Path.GetTempPath(), $"s2modkit-preview-output-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(path, "{invalid-json", TestContext.Current.CancellationToken);
            var app = new FakeApplication();
            var cli = new S2ModKitCli(app, "test", "1", false);
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(2, await cli.RunAsync(["selection-preview", "--project", "project", "--recipe", path, "--output-root", root], output, error, TestContext.Current.CancellationToken));
            Assert.Contains("selection-preview", output.ToString(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(root));
            Assert.Null(app.LastRecipe);
        }
        finally { File.Delete(path); }
    }
}
