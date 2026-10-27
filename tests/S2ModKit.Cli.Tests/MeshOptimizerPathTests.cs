namespace S2ModKit.Cli.Tests;

public sealed class MeshOptimizerPathTests
{
    [Fact]
    public void ExplicitPathWinsBundledCodecIsUsedOtherwiseAndAbsenceStaysUnconfigured()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2modkit-codec-path", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(S2ModKitCli.ResolveMeshOptimizerPath(null, root));
            var bundled = Path.Combine(root, "meshoptimizer.dll");
            File.WriteAllBytes(bundled, [0]);

            Assert.Equal(bundled, S2ModKitCli.ResolveMeshOptimizerPath(" ", root));
            Assert.Equal(@"D:\codec\meshoptimizer.dll", S2ModKitCli.ResolveMeshOptimizerPath(@"D:\codec\meshoptimizer.dll", root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
