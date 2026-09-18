using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;

namespace S2ModKit.Source2.Tests;

public sealed class LocalSource2ViewerIntegrationTests
{
    [Fact]
    public async Task ConfiguredSource2ViewerReopensImmutableModelFromScratchCopy()
    {
        var modelPath = Environment.GetEnvironmentVariable("S2MODKIT_TEST_MODEL");
        var resourceRoot = Environment.GetEnvironmentVariable("S2MODKIT_TEST_RESOURCE_ROOT");
        var viewerPath = Environment.GetEnvironmentVariable("S2MODKIT_SOURCE2_VIEWER_PATH");
        var scratchRoot = Environment.GetEnvironmentVariable("S2MODKIT_EXTERNAL_VERIFY_ROOT");
        if (string.IsNullOrWhiteSpace(modelPath)
            || string.IsNullOrWhiteSpace(resourceRoot)
            || string.IsNullOrWhiteSpace(viewerPath)
            || string.IsNullOrWhiteSpace(scratchRoot))
        {
            Assert.Skip("Set the Source 2 input and viewer environment variables to enable the external verifier check.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var fullModelPath = Path.GetFullPath(modelPath);
        var fullResourceRoot = Path.GetFullPath(resourceRoot);
        var originalBytes = await File.ReadAllBytesAsync(fullModelPath, cancellationToken);
        var originalHash = ContentHash.Compute(originalBytes);
        var logicalPath = StableIdentity.NormalizePath(Path.GetRelativePath(fullResourceRoot, fullModelPath));
        var artifact = new ArtifactContent(logicalPath, originalHash, originalBytes);
        var adapter = new Source2CompiledModelAdapter();
        var snapshot = await adapter.InspectAsync(artifact, cancellationToken);
        var candidate = new RewriteCandidate(logicalPath, originalBytes, snapshot);
        var verifier = new Source2ViewerExternalVerifier(viewerPath, scratchRoot);

        var boundary = await verifier.VerifyAsync(candidate, cancellationToken);
        var currentHash = ContentHash.Compute(await File.ReadAllBytesAsync(fullModelPath, cancellationToken));

        Assert.Equal("passed", boundary.Status);
        Assert.Equal(originalHash, currentHash);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.GetFullPath(scratchRoot)));
    }
}
