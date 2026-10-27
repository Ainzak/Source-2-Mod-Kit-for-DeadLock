using S2ModKit.Application;
using S2ModKit.Cli;
using S2ModKit.Domain;

namespace S2ModKit.Cli.Tests;

public sealed partial class CliContractTests
{
    [Fact]
    public async Task CoordinatedScaffoldPassesVersionedCommonFieldAndRejectsMissingOptIn()
    {
        var optionsPath = await WriteCoordinatedOptionsAsync(); var token = TestContext.Current.CancellationToken;
        try
        {
            string[] args = ["recipe", "scaffold", "--project", "project", "--component", "cmp_0123456789abcdef01234567", "--component", "cmp_111111111111111111111111",
                "--intent", "coordinated-field", "--output", "unused.json", "--experimental", "--coordinated-options", optionsPath];
            var app = new FakeApplication(); using var output = new StringWriter(); using var error = new StringWriter();
            var cli = new S2ModKitCli(app, "test", "1", false);
            Assert.Equal(0, await cli.RunAsync(args, output, error, token));
            Assert.Equal(5, app.LastScaffoldRequest!.ExperimentalDiscoverySchemaVersion);
            Assert.Equal(2, app.LastScaffoldRequest.ComponentIds.Count); Assert.Null(app.LastScaffoldRequest.MaximumVertexDisplacement);
            Assert.IsType<CoordinatedTiltedRampField>(app.LastScaffoldRequest.Coordinated!.Field);
            foreach (var option in new[] { "--experimental", "--coordinated-options" })
            {
                var invalid = args.ToList(); invalid.RemoveRange(invalid.IndexOf(option), option == "--experimental" ? 1 : 2);
                var rejected = new FakeApplication();
                Assert.Equal(2, await new S2ModKitCli(rejected, "test", "1", false).RunAsync(invalid.ToArray(), output, error, token));
                Assert.Null(rejected.LastScaffoldRequest);
            }
            Assert.Empty(error.ToString());
        }
        finally { File.Delete(optionsPath); }
    }

    [Fact]
    public async Task CoordinatedDiscoveryRequiresExplicitExperimentalAcknowledgement()
    {
        var cli = new S2ModKitCli(new FakeApplication(), "test", "1", false); using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2, await cli.RunAsync(["components", "list", "--project", "project", "--coordinated", "--format", "json"], output, error, TestContext.Current.CancellationToken));
        Assert.Contains("COORDINATED_OPT_IN_REQUIRED", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    private static async Task<string> WriteCoordinatedOptionsAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "s2modkit-common-options-" + Guid.NewGuid().ToString("N") + ".json");
        var options = new CoordinatedScaffoldOptions(new("preserve_unverified", 1), new("reject", 1), new("reject", 1),
            new CoordinatedTiltedRampField
            {
                Version = 1,
                CoordinateSpace = "model",
                FirstAxis = "x",
                FirstSign = 1,
                SecondAxis = "z",
                SecondSign = 1,
                PinnedThrough = 0,
                FullFrom = 8,
                Pivot = new(),
                UniformScale = 1.5f,
                NumericalPolicy = new("tilted_ramp_numeric", 1)
            }, 64);
        await File.WriteAllBytesAsync(path, JsonDefaults.SerializeToUtf8(options), TestContext.Current.CancellationToken);
        return path;
    }
}
