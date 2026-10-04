using S2ModKit.Application;
using S2ModKit.Cli;
using S2ModKit.Domain;

namespace S2ModKit.Cli.Tests;

public sealed partial class CliContractTests
{
    [Fact]
    public async Task ExplicitLocalFieldScaffoldPassesTypedIntentToApplication()
    {
        var app = new FakeApplication();
        var cli = new S2ModKitCli(app, "test", "1", false);
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, await cli.RunAsync(LocalFieldArgs(), output, error, TestContext.Current.CancellationToken));
        var request = app.LastScaffoldRequest!;
        Assert.True(request.ExperimentalDiscovery); Assert.Equal(4, request.ExperimentalDiscoverySchemaVersion);
        Assert.Null(request.UniformScale); Assert.Null(request.Experimental);
        Assert.Equal(new TransformVector3 { X = -3, Y = 0, Z = 106 }, request.Ellipsoid!.LocalTransform.Field.Center);
        Assert.Equal(2, request.Ellipsoid.LocalTransform.UniformScale);
        Assert.Equal(1f / 16, request.Ellipsoid.LocalTransform.Field.CoreFraction);
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("--experimental", null)]
    [InlineData("--field-center", null)]
    [InlineData("--field-radii", null)]
    [InlineData("--core-fraction", null)]
    [InlineData("--scale", null)]
    [InlineData("--max-displacement", null)]
    [InlineData("--field-center", "NaN,0,0")]
    [InlineData("--field-radii", "0,10,10")]
    [InlineData("--core-fraction", "1")]
    [InlineData("--scale", "1")]
    [InlineData("--intent", "remove")]
    public async Task LocalFieldScaffoldRejectsImplicitOrInvalidParametersBeforeApplication(string option, string? replacement)
    {
        var args = LocalFieldArgs().ToList(); var index = args.IndexOf(option);
        if (replacement is null) args.RemoveRange(index, option == "--experimental" ? 1 : 2);
        else args[index + 1] = replacement;
        var app = new FakeApplication(); var cli = new S2ModKitCli(app, "test", "1", false);
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2, await cli.RunAsync(args.ToArray(), output, error, TestContext.Current.CancellationToken));
        Assert.Null(app.LastScaffoldRequest);
    }

    [Theory]
    [InlineData("cancel\n", "action_selection")]
    [InlineData("back\ncancel\n", "action_selection")]
    [InlineData("1\n2\ncancel\n", "action_selection")]
    public async Task GuidedLocalParametersSupportInvalidInputBackCancelAndResume(string values, string expectedStep)
    {
        var hash = ContentHash.Compute("catalogue-directory"u8);
        var catalogue = await WriteCatalogueAsync(hash);
        var path = Path.Combine(Path.GetTempPath(), $"s2modkit-local-{Guid.NewGuid():N}.json");
        try
        {
            var app = new FakeApplication() { EllipsoidAvailable = true };
            var cli = new S2ModKitCli(app, "test", "1", false, catalogueInventoryFactory: new FakeCatalogueInventoryFactory(hash));
            using var input = new StringReader("1\n1\n1\n1\n2\n" + values);
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.Equal(0, await cli.RunAsync(["interactive", "--catalogue", catalogue, "--session", path, "--base-vpk", "pak01_dir.vpk", "--experimental"], input, output, error, TestContext.Current.CancellationToken));
            Assert.Empty(error.ToString());
            var session = JsonDefaults.Deserialize<GuidedWorkflowSession>(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken), "session");
            Assert.Equal(3, session.SchemaVersion); Assert.Equal(expectedStep, session.Step);
            Assert.Null(session.EllipsoidParameters); Assert.Null(session.EllipsoidPreview); Assert.Null(app.LastScaffoldRequest);
            Assert.Contains("Experimental local ellipsoid scale", output.ToString(), StringComparison.Ordinal);
            using var resumed = new StringReader("cancel\n");
            Assert.Equal(0, await cli.RunAsync(["interactive", "--session", path, "--resume"], resumed, output, error, TestContext.Current.CancellationToken));
            Assert.Empty(error.ToString());
        }
        finally { File.Delete(path); File.Delete(catalogue); }
    }

    private static string[] LocalFieldArgs() => ["recipe", "scaffold", "--project", "project", "--component", "cmp_0123456789abcdef01234567",
        "--intent", "ellipsoid-scale", "--output", "unused.json", "--experimental", "--field-center", "-3,0,106", "--field-radii", "10,10,10",
        "--core-fraction", "0.0625", "--scale", "2", "--max-displacement", "64"];
}
