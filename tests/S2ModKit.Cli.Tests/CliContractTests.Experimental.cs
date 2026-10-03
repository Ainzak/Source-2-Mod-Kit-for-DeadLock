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

    [Fact]
    public async Task ExperimentalGuidedRegionParametersResumeWithoutRepeatingOptIn()
    {
        var hash = ContentHash.Compute("catalogue-directory"u8);
        var catalogue = await WriteCatalogueAsync(hash);
        var sessionPath = Path.Combine(Path.GetTempPath(), $"s2modkit-experimental-{Guid.NewGuid():N}.json");
        var recipePath = Path.Combine(Path.GetTempPath(), $"s2modkit-region-{Guid.NewGuid():N}.json");
        try
        {
            var app = new FakeApplication(writeScaffoldedRecipe: true) { ExperimentalAvailable = true };
            var cli = new S2ModKitCli(app, "test", "1", false, catalogueInventoryFactory: new FakeCatalogueInventoryFactory(hash));
            using var input = new StringReader($"1\n1\n1\n1\n2\n1.5\n64\n0\n0\n5\n3\n0\n4\n{recipePath}\ncancel\n");
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, await cli.RunAsync(["interactive", "--catalogue", catalogue, "--session", sessionPath, "--base-vpk", "pak01_dir.vpk", "--experimental"], input, output, error, TestContext.Current.CancellationToken));
            Assert.Empty(error.ToString());
            var session = JsonDefaults.Deserialize<GuidedWorkflowSession>(await File.ReadAllBytesAsync(sessionPath, TestContext.Current.CancellationToken), "session");
            Assert.Equal(GuidedWorkflowContract.OutputSelectionStep, session.Step);
            Assert.Equal(2, session.SchemaVersion);
            Assert.Equal(new RegionScaleSelection("axis_ramp", 1, "z", 0, 4), session.ExperimentalParameters!.Region);
            Assert.True(app.LastScaffoldRequest!.ExperimentalDiscovery);
            Assert.Equal(6, Assert.Single(app.LastRecipe!.Operations).Version);
            Assert.Contains("not guaranteed", output.ToString(), StringComparison.Ordinal);
            using var resumedInput = new StringReader("cancel\n");
            Assert.Equal(0, await cli.RunAsync(["interactive", "--session", sessionPath, "--resume"], resumedInput, output, error, TestContext.Current.CancellationToken));
            Assert.Empty(error.ToString());
        }
        finally { File.Delete(catalogue); File.Delete(sessionPath); File.Delete(recipePath); }
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

    [Fact]
    public async Task StrictGuidedSessionCannotBeSilentlyUpgradedOnResume()
    {
        var path = Path.Combine(Path.GetTempPath(), $"s2modkit-experimental-{Guid.NewGuid():N}.json");
        try
        {
            var session = GuidedWorkflow.CreateSession("catalogue.json", [new("source", GuidedWorkflowContract.CompiledModelSource, "Model", "model.vmdl_c")], false);
            await File.WriteAllTextAsync(path, JsonDefaults.Serialize(session), TestContext.Current.CancellationToken);
            var cli = new S2ModKitCli(new FakeApplication(), "test", "1", false);
            using var input = new StringReader("cancel\n");
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exit = await cli.RunAsync(["interactive", "--session", path, "--resume", "--experimental"], input, output, error, TestContext.Current.CancellationToken);
            Assert.Equal(10, exit);
            Assert.Contains("GUIDED_EXPERIMENTAL_RESUME_MISMATCH", error.ToString(), StringComparison.Ordinal);
            Assert.Equal(1, JsonDefaults.Deserialize<GuidedWorkflowSession>(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken), "session").SchemaVersion);
        }
        finally { File.Delete(path); }
    }
}
