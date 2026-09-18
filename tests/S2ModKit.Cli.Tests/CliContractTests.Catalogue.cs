using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using S2ModKit.Application;
using S2ModKit.Cli;
using S2ModKit.Domain;

namespace S2ModKit.Cli.Tests;

public sealed partial class CliContractTests
{
    [Fact]
    public async Task CatalogueListJsonRetainsStableIdsAndExactLocators()
    {
        var directoryHash = ContentHash.Compute("catalogue-directory"u8);
        var factory = new FakeCatalogueInventoryFactory(directoryHash);
        var cataloguePath = await WriteCatalogueAsync(directoryHash);
        try
        {
            var cli = new S2ModKitCli(
                new FakeApplication(),
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: factory);
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await cli.RunAsync(
                ["catalog", "heroes", "list", "--catalogue", cataloguePath, "--source-vpk", "pak01_dir.vpk", "--format", "json"],
                output,
                error,
                TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(output.ToString());
            var hero = document.RootElement.GetProperty("result").GetProperty("heroes")[0];
            var resource = hero.GetProperty("resources")[0];

            Assert.Equal(0, exitCode);
            Assert.Equal("catalog.heroes.list", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("haze", hero.GetProperty("heroId").GetString());
            Assert.Equal("haze.primary", resource.GetProperty("resourceId").GetString());
            Assert.Equal("models/heroes_staging/haze/haze.vmdl_c", resource.GetProperty("logicalPath").GetString());
            Assert.Empty(error.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
        }
    }

    [Fact]
    public async Task CatalogueResolveTextAcceptsAliasAndFavorsNames()
    {
        var directoryHash = ContentHash.Compute("catalogue-directory"u8);
        var cataloguePath = await WriteCatalogueAsync(directoryHash);
        try
        {
            var cli = new S2ModKitCli(
                new FakeApplication(),
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: new FakeCatalogueInventoryFactory(directoryHash));
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await cli.RunAsync(
                ["catalog", "resolve", "--catalogue", cataloguePath, "--source-vpk", "pak01_dir.vpk", "--hero", "mist"],
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Contains("Hero: Haze [haze]", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Primary model [haze.primary]", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Locator: models/heroes_staging/haze/haze.vmdl_c", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("SHA-256", output.ToString(), StringComparison.Ordinal);
            Assert.Empty(error.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
        }
    }

    [Fact]
    public async Task CatalogueSourceDriftAndUnknownHeroUseResolutionExitCategory()
    {
        var expectedHash = ContentHash.Compute("catalogue-directory"u8);
        var cataloguePath = await WriteCatalogueAsync(expectedHash);
        try
        {
            var staleCli = new S2ModKitCli(
                new FakeApplication(),
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: new FakeCatalogueInventoryFactory(ContentHash.Compute("changed"u8)));
            using var staleOutput = new StringWriter();
            using var staleError = new StringWriter();
            var staleExit = await staleCli.RunAsync(
                ["catalog", "heroes", "list", "--catalogue", cataloguePath, "--source-vpk", "pak01_dir.vpk", "--format", "json"],
                staleOutput,
                staleError,
                TestContext.Current.CancellationToken);
            using var staleDocument = JsonDocument.Parse(staleOutput.ToString());

            Assert.Equal((int)ErrorCategory.InputOrResolution, staleExit);
            Assert.Equal("CATALOGUE_SOURCE_STALE", staleDocument.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Empty(staleError.ToString());

            var currentCli = new S2ModKitCli(
                new FakeApplication(),
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: new FakeCatalogueInventoryFactory(expectedHash));
            using var missingOutput = new StringWriter();
            using var missingError = new StringWriter();
            var missingExit = await currentCli.RunAsync(
                ["catalog", "resolve", "--catalogue", cataloguePath, "--source-vpk", "pak01_dir.vpk", "--hero", "unknown", "--format", "json"],
                missingOutput,
                missingError,
                TestContext.Current.CancellationToken);
            using var missingDocument = JsonDocument.Parse(missingOutput.ToString());

            Assert.Equal((int)ErrorCategory.InputOrResolution, missingExit);
            Assert.Equal("CATALOGUE_HERO_NOT_FOUND", missingDocument.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Empty(missingError.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
        }
    }

    [Fact]
    public async Task CompatibilityScanTextIsConciseAndPublishesBothReports()
    {
        var directoryHash = ContentHash.Compute("catalogue-directory"u8);
        var cataloguePath = await WriteCatalogueAsync(directoryHash);
        var publisher = new FakeCompatibilityReportPublisher();
        try
        {
            var cli = new S2ModKitCli(
                new FakeApplication(),
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: new FakeCatalogueInventoryFactory(directoryHash),
                compatibilityScanner: new FakeCompatibilityScanner(directoryHash),
                compatibilityReportPublisher: publisher);
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await cli.RunAsync(
                ["compatibility", "scan", "--catalogue", cataloguePath, "--source-vpk", "pak01_dir.vpk", "--output-root", "reports"],
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Contains("Resources: 1; supported: 1", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"JSON report: {Path.Combine("reports", "result.json")}", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(directoryHash.Value, output.ToString(), StringComparison.Ordinal);
            Assert.NotNull(publisher.PublishedReport);
            Assert.Empty(error.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
        }
    }

}
