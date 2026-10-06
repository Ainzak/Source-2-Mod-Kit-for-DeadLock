using S2ModKit.Application;
using S2ModKit.Cli;
using S2ModKit.Domain;

namespace S2ModKit.Cli.Tests;

public sealed partial class CliContractTests
{
    [Fact]
    public async Task DirectionalDiscoveryRequiresOptInAndRejectsAmbiguousRoutes()
    {
        var cli = new S2ModKitCli(new FakeApplication(), "test", "1", false); using var output = new StringWriter(); using var error = new StringWriter();
        var token = TestContext.Current.CancellationToken;
        Assert.Equal(0, await cli.RunAsync(["components", "list", "--project", "project", "--experimental", "--directional", "--format", "json"], output, error, token));
        Assert.Contains("\"schemaVersion\": 6", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, await cli.RunAsync(["components", "list", "--project", "project", "--directional", "--format", "json"], output, error, token));
        Assert.Contains("DIRECTIONAL_OPT_IN_REQUIRED", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, await cli.RunAsync(["components", "list", "--project", "project", "--directional", "--coordinated", "--experimental", "--format", "json"], output, error, token));
        Assert.Contains("DIRECTIONAL_ROUTE_AMBIGUOUS", output.ToString(), StringComparison.Ordinal); Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task DirectionalScaffoldCarriesTypedOptionsAndRejectsMissingOrUnrelatedCliOptions()
    {
        var token = TestContext.Current.CancellationToken;
        var field = new DirectionalEllipsoidField("directional_ellipsoid", 1, "model", new() { Kind = "explicit_point", Point = new() }, new() { X = 10, Y = 8, Z = 4 }, .4f,
            new() { X = 1.5f, Y = 1.5f, Z = 1 }, new("directional_ellipsoid_numeric", 1));
        var hash = ContentHash.Compute("source"u8);
        var bone = new DirectionalBoneAssertion { Version = 1, AssertionId = "fixed", BoneName = "source_bone", BoneIndex = 0, RootSkeletonHash = hash, Lods = [new(0, hash, 1)] };
        var options = new DirectionalScaffoldOptions(hash, new("preserve_unverified", 1), new("reject", 1), new("reject", 1), field, new("keep_fixed", 1, [bone]), 64);
        var path = Path.Combine(Path.GetTempPath(), "s2modkit-directional-options-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllBytesAsync(path, JsonDefaults.SerializeToUtf8(options), token);
            string[] args = ["recipe", "scaffold", "--project", "project", "--component", "cmp_0123456789abcdef01234567", "--intent", "directional-field", "--output", "unused.json", "--experimental", "--directional-options", path];
            var app = new FakeApplication(); using var output = new StringWriter(); using var error = new StringWriter();
            Assert.Equal(0, await new S2ModKitCli(app, "test", "1", false).RunAsync(args, output, error, token));
            Assert.Equal(6, app.LastScaffoldRequest!.ExperimentalDiscoverySchemaVersion);
            Assert.Equal(hash, app.LastScaffoldRequest.Directional!.InputHash); Assert.Null(app.LastScaffoldRequest.Coordinated);
            foreach (var flag in new[] { "--experimental", "--directional-options" })
            {
                var invalid = args.ToList(); invalid.RemoveRange(invalid.IndexOf(flag), flag == "--experimental" ? 1 : 2);
                var rejected = new FakeApplication(); Assert.Equal(2, await new S2ModKitCli(rejected, "test", "1", false).RunAsync(invalid.ToArray(), output, error, token)); Assert.Null(rejected.LastScaffoldRequest);
            }
            var unrelated = new FakeApplication(); Assert.Equal(2, await new S2ModKitCli(unrelated, "test", "1", false).RunAsync([.. args, "--scale-x", "2"], output, error, token)); Assert.Null(unrelated.LastScaffoldRequest);
            var missingReader = new S2ModKitCli(new FakeApplication(), "test", "1", false);
            Assert.Equal(2, await missingReader.RunAsync(["directional", "inspect", "--project", "project", "--component", "cmp_0123456789abcdef01234567", "--output-root", "unused"], output, error, token));
            Assert.Equal(20, await missingReader.RunAsync(["directional", "inspect", "--project", "project", "--component", "cmp_0123456789abcdef01234567", "--output-root", "unused", "--experimental"], output, error, token));
            Assert.Empty(error.ToString());
        }
        finally { File.Delete(path); }
    }
}
