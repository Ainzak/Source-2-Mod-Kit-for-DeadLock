using System.Text;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;

namespace S2ModKit.Application.Tests;

public sealed class CompatibilityReportingTests
{
    [Fact]
    public async Task ReportsAreDeterministicClusterGapsCompareRoundTripAndPublishAtomically()
    {
        var scan = Scan();
        var first = CompatibilityReports.Create(scan);
        var repeated = CompatibilityReports.Create(scan);

        Assert.Equal(first.ReportId, repeated.ReportId);
        Assert.Equal(CompatibilityReports.RenderJson(first), CompatibilityReports.RenderJson(repeated));
        var cluster = Assert.Single(first.Clusters);
        Assert.Equal(["alpha.primary", "beta.primary"], cluster.ResourceIds);
        Assert.Contains("COMPONENT_CAPABILITY_ANALYZER_UNAVAILABLE", cluster.ReasonCodes);
        Assert.DoesNotContain("sha-256", CompatibilityReports.RenderText(first), StringComparison.OrdinalIgnoreCase);

        var parsed = JsonDefaults.Deserialize<CompatibilityReport>(
            Encoding.UTF8.GetBytes(CompatibilityReports.RenderJson(first)),
            "Compatibility report");
        var changedResource = scan.Resources[0] with { ContentHash = ContentHash.Compute("changed"u8) };
        var changedScan = scan with { Resources = [changedResource, scan.Resources[1]] };
        var compared = CompatibilityReports.Create(changedScan, parsed);
        Assert.Equal(["alpha.primary"], compared.Comparison?.ChangedResourceIds);
        Assert.Equal(["beta.primary"], compared.Comparison?.UnchangedResourceIds);

        var schema = await JsonSchema.FromFileAsync(
            GetSchemaPath(),
            TestContext.Current.CancellationToken);
        Assert.Empty(schema.Validate(CompatibilityReports.RenderJson(compared)));

        var root = Path.Combine(Path.GetTempPath(), "s2modkit-compatibility-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var publisher = new FileSystemCompatibilityReportPublisher();
            var publication = await publisher.PublishAsync(
                root,
                compared,
                CompatibilityReports.RenderJson(compared),
                CompatibilityReports.RenderMarkdown(compared),
                TestContext.Current.CancellationToken);

            Assert.True(File.Exists(publication.JsonPath));
            Assert.True(File.Exists(publication.MarkdownPath));
            var exception = await Assert.ThrowsAsync<S2ModKitException>(() => publisher.PublishAsync(
                root,
                compared,
                CompatibilityReports.RenderJson(compared),
                CompatibilityReports.RenderMarkdown(compared),
                TestContext.Current.CancellationToken));
            Assert.Equal("COMPATIBILITY_REPORT_EXISTS", exception.Error.Code);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static CompatibilityScanResult Scan()
    {
        var signature = StructuralSignature.Create(
            "component-discovery-v2",
            [new StructuralFact("lod.count", "1")]);
        var reason = new StructuralCompatibilityReason(
            "COMPONENT_CAPABILITY_ANALYZER_UNAVAILABLE",
            "No compatible component capability analyzer is available for this model.");
        var capability = new CompatibilityCapabilityResult(
            "transform_component",
            1,
            CapabilityAvailability.Blocked,
            [reason]);
        var candidate = new CompatibilityCandidateResult(
            "cmp_0123456789abcdef01234567",
            ComponentDiscoveryV2Contract.MaterialGroupKind,
            "Test material",
            ["materials/test.vmat_c"],
            [0],
            [capability]);
        var alpha = Resource("alpha", signature, candidate, reason);
        var beta = Resource("beta", signature, candidate with { CandidateId = "cmp_1123456789abcdef01234567" }, reason);
        return new CompatibilityScanResult(
            CompatibilityScanContract.SchemaVersion,
            "deadlock.heroes",
            "test.1",
            ContentHash.Compute("directory"u8),
            new CompatibilityScanAnalyzer(
                "synthetic",
                "1",
                "not_configured",
                "not_configured",
                new SortedDictionary<string, string>(StringComparer.Ordinal)),
            [alpha, beta]);
    }

    private static CompatibilityResourceResult Resource(
        string id,
        StructuralSignature signature,
        CompatibilityCandidateResult candidate,
        StructuralCompatibilityReason reason) => new(
            id,
            char.ToUpperInvariant(id[0]) + id[1..],
            $"{id}.primary",
            "Primary model",
            HeroCatalogueContract.PrimaryModelRole,
            $"models/heroes/{id}/{id}.vmdl_c",
            ContentHash.Compute(Encoding.UTF8.GetBytes(id)),
            id.Length,
            CompatibilityScanContract.Supported,
            signature,
            [candidate],
            [reason]);

    private static string GetSchemaPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "schemas", "compatibility-scan.schema.json");
    }
}
