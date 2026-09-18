using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Source2.Tests;

public sealed class LocalPreciseComponentIntegrationTests
{
    [Fact]
    public async Task ConfiguredModelExposesExpectedCompleteMechanicalLineagesWithoutChangingInput()
    {
        var modelVariable = Environment.GetEnvironmentVariable("S2MODKIT_TEST_PRECISE_MODEL");
        var logicalPathVariable = Environment.GetEnvironmentVariable("S2MODKIT_TEST_PRECISE_LOGICAL_PATH");
        var hashVariable = Environment.GetEnvironmentVariable("S2MODKIT_TEST_PRECISE_SHA256");
        var expectedVariable = Environment.GetEnvironmentVariable("S2MODKIT_TEST_PRECISE_LINEAGES");
        if (string.IsNullOrWhiteSpace(modelVariable)
            || string.IsNullOrWhiteSpace(logicalPathVariable)
            || string.IsNullOrWhiteSpace(hashVariable)
            || string.IsNullOrWhiteSpace(expectedVariable))
        {
            Assert.Skip("Set the four S2MODKIT_TEST_PRECISE variables to enable the proprietary lineage integration check.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var modelPath = Path.GetFullPath(modelVariable);
        Assert.True(File.Exists(modelPath));
        var before = await File.ReadAllBytesAsync(modelPath, cancellationToken);
        var expectedHash = new ContentHash(hashVariable);
        Assert.Equal(expectedHash, ContentHash.Compute(before));
        var logicalPath = StableIdentity.NormalizePath(logicalPathVariable);
        var input = new ArtifactContent(logicalPath, expectedHash, before);
        var adapter = new Source2CompiledModelAdapter(Environment.GetEnvironmentVariable("S2MODKIT_MESHOPTIMIZER_PATH"));

        var snapshot = await adapter.InspectAsync(input, cancellationToken);
        var discovery = await new PreciseComponentDiscoveryService(adapter).DiscoverAsync(
            input,
            snapshot,
            cancellationToken);

        Assert.All(
            snapshot.Lods.SelectMany(lod => lod.Meshes),
            mesh => Assert.True(mesh.MechanicalLineage?.IsCanonical()));
        var expectedLineages = expectedVariable.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var expectedLineage in expectedLineages)
        {
            var candidate = Assert.Single(
                discovery.Candidates.OfType<MeshLineageComponentCandidateV2>(),
                item => item.LineageKey == expectedLineage);
            Assert.Equal(snapshot.Lods.Count, candidate.Lods.Count);
            Assert.Equal(snapshot.Lods.Select(lod => lod.Level).Order(), candidate.Lods.Select(lod => lod.Lod));
            Assert.All(candidate.Lods, lod =>
            {
                Assert.True(lod.DrawCallCount > 0);
                Assert.Equal(lod.DrawCallCount, lod.DrawCallIds.Count);
                Assert.False(string.IsNullOrWhiteSpace(lod.SourceName));
            });
        }

        var after = await File.ReadAllBytesAsync(modelPath, cancellationToken);
        Assert.Equal(expectedHash, ContentHash.Compute(after));
    }
}
