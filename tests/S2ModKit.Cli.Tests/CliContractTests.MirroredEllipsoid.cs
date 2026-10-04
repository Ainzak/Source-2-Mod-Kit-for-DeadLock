using S2ModKit.Application;
using S2ModKit.Cli;

namespace S2ModKit.Cli.Tests;

public sealed partial class CliContractTests
{
    [Fact]
    public async Task MirroredScaffoldingPassesExactTypedPlaneAndRejectsOverlap()
    {
        var args = LocalFieldArgs().ToList(); args[args.IndexOf("--intent") + 1] = "mirrored-ellipsoid-scale";
        args[args.IndexOf("--field-center") + 1] = "4,0,0"; args[args.IndexOf("--field-radii") + 1] = "4,4,4";
        args.AddRange(["--mirror-axis", "x", "--mirror-coordinate", "0"]);
        var app = new FakeApplication(); var cli = new S2ModKitCli(app, "test", "1", false);
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, await cli.RunAsync(args.ToArray(), output, error, TestContext.Current.CancellationToken));
        Assert.Equal(RecipeScaffoldContract.MirroredEllipsoidScaleIntent, app.LastScaffoldRequest!.Intent);
        var field = app.LastScaffoldRequest.Ellipsoid!.LocalTransform.Field;
        Assert.Equal("mirrored_ellipsoids", field.Kind); Assert.Equal("x", field.MirrorPlane!.Axis); Assert.Equal(0, field.MirrorPlane.Coordinate);
        foreach (var option in new[] { "--mirror-axis", "--mirror-coordinate" })
        {
            var incomplete = args.ToList(); incomplete.RemoveRange(incomplete.IndexOf(option), 2);
            var rejected = new FakeApplication();
            Assert.Equal(2, await new S2ModKitCli(rejected, "test", "1", false).RunAsync(incomplete.ToArray(), output, error, TestContext.Current.CancellationToken));
            Assert.Null(rejected.LastScaffoldRequest);
        }
        args[args.IndexOf("--field-center") + 1] = "3,0,0";
        var overlapping = new FakeApplication();
        Assert.NotEqual(0, await new S2ModKitCli(overlapping, "test", "1", false).RunAsync(args.ToArray(), output, error, TestContext.Current.CancellationToken));
        Assert.Null(overlapping.LastScaffoldRequest);
        Assert.Contains("ELLIPSOID_MIRROR_OVERLAP", output.ToString() + error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuidedMirroredPlaneCanCancelWithoutScaffoldingOrInventingDefaults()
    {
        var hash = S2ModKit.Domain.ContentHash.Compute("catalogue-directory"u8); var catalogue = await WriteCatalogueAsync(hash);
        var path = Path.Combine(Path.GetTempPath(), $"s2modkit-mirror-{Guid.NewGuid():N}.json");
        try
        {
            var app = new FakeApplication { EllipsoidAvailable = true };
            var cli = new S2ModKitCli(app, "test", "1", false, catalogueInventoryFactory: new FakeCatalogueInventoryFactory(hash));
            using var input = new StringReader("1\n1\n1\n1\n5\n1.25\n64\n4\n0\n0\n4\n4\n4\n0.0625\ncancel\n");
            using var output = new StringWriter(); using var error = new StringWriter();
            var exit = await cli.RunAsync(["interactive", "--catalogue", catalogue, "--session", path, "--base-vpk", "pak01_dir.vpk", "--experimental"], input, output, error, TestContext.Current.CancellationToken);
            Assert.True(exit == 0, output.ToString() + error.ToString());
            Assert.Null(app.LastScaffoldRequest); Assert.Empty(error.ToString());
            Assert.Contains("Explicit model-axis plane", output.ToString(), StringComparison.Ordinal);
        }
        finally { File.Delete(path); File.Delete(catalogue); }
    }
}
