using S2ModKit.Cli;

namespace S2ModKit.Cli.Tests;

public sealed class CataloguePathResolverTests
{
    [Fact]
    public void FindsPackagedCatalogueRelativeToApplicationDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2mod-catalogue-" + Guid.NewGuid().ToString("N"));
        var catalogue = Path.Combine(root, "catalogues", "deadlock-current.json");
        Directory.CreateDirectory(Path.GetDirectoryName(catalogue)!);
        File.WriteAllText(catalogue, "{}");

        try
        {
            Assert.Equal(Path.GetFullPath(catalogue), CataloguePathResolver.TryFindPackagedCatalogue(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingPackagedCatalogueReturnsNull()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2mod-catalogue-missing-" + Guid.NewGuid().ToString("N"));

        Assert.Null(CataloguePathResolver.TryFindPackagedCatalogue(root));
    }
}
