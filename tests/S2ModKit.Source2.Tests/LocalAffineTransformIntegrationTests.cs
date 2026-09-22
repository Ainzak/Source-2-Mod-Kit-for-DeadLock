using System.Text;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Source2.Tests;

public sealed class LocalAffineTransformIntegrationTests
{
    [Fact]
    public async Task ConfiguredModelCompletesAffineRewriteAndInjectedFailuresReturnNoCandidate()
    {
        var modelVariable = Environment.GetEnvironmentVariable("S2MODKIT_TEST_AFFINE_MODEL");
        var logicalPathVariable = Environment.GetEnvironmentVariable("S2MODKIT_TEST_AFFINE_LOGICAL_PATH");
        var hashVariable = Environment.GetEnvironmentVariable("S2MODKIT_TEST_AFFINE_SHA256");
        var recipeVariable = Environment.GetEnvironmentVariable("S2MODKIT_TEST_AFFINE_RECIPE");
        var meshOptimizerPath = Environment.GetEnvironmentVariable("S2MODKIT_MESHOPTIMIZER_PATH");
        if (string.IsNullOrWhiteSpace(modelVariable)
            || string.IsNullOrWhiteSpace(logicalPathVariable)
            || string.IsNullOrWhiteSpace(hashVariable)
            || string.IsNullOrWhiteSpace(recipeVariable)
            || string.IsNullOrWhiteSpace(meshOptimizerPath))
        {
            Assert.Skip("Set the five S2MODKIT_TEST_AFFINE variables to enable the proprietary affine integration check.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var modelPath = Path.GetFullPath(modelVariable);
        var recipePath = Path.GetFullPath(recipeVariable);
        var originalBytes = await File.ReadAllBytesAsync(modelPath, cancellationToken);
        var originalHash = new ContentHash(hashVariable);
        Assert.Equal(originalHash, ContentHash.Compute(originalBytes));
        var input = new ArtifactContent(StableIdentity.NormalizePath(logicalPathVariable), originalHash, originalBytes);
        var recipe = JsonDefaults.Deserialize<RecipeDocument>(
            await File.ReadAllBytesAsync(recipePath, cancellationToken),
            "Affine integration recipe");
        var adapter = new Source2CompiledModelAdapter(meshOptimizerPath);
        var snapshot = await adapter.InspectAsync(input, cancellationToken);
        var plan = MutationPlanner.CreatePlan(snapshot, recipe, input, adapter);

        Assert.True(adapter.CanRewrite(snapshot, plan));
        var candidate = await adapter.RewriteAsync(input, snapshot, plan, cancellationToken);
        Assert.True(ModelVerifier.Verify(snapshot, candidate.Snapshot, plan).IsValid);

        foreach (var checkpoint in Enum.GetValues<AffineRewriteCheckpoint>())
        {
            var reached = false;
            var failingAdapter = new Source2CompiledModelAdapter(meshOptimizerPath, observed =>
            {
                if (observed == checkpoint)
                {
                    reached = true;
                    throw new InvalidOperationException($"Injected affine failure after {checkpoint}.");
                }
            });

            await Assert.ThrowsAsync<S2ModKitException>(() =>
                failingAdapter.RewriteAsync(input, snapshot, plan, cancellationToken));
            Assert.True(reached, $"The {checkpoint} failure injection was not reached.");
            Assert.Equal(originalHash, ContentHash.Compute(await File.ReadAllBytesAsync(modelPath, cancellationToken)));
        }

        Assert.Equal(originalHash, ContentHash.Compute(await File.ReadAllBytesAsync(modelPath, cancellationToken)));
    }
}
