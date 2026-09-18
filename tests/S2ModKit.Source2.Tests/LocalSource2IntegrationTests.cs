using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Source2.Tests;

public sealed class LocalSource2IntegrationTests
{
    [Fact]
    public async Task ConfiguredModelCompletesInMemoryDrawCallRewriteWithoutChangingInputFile()
    {
        var modelVariable = Environment.GetEnvironmentVariable("S2MODKIT_TEST_MODEL");
        var rootVariable = Environment.GetEnvironmentVariable("S2MODKIT_TEST_RESOURCE_ROOT");
        if (string.IsNullOrWhiteSpace(modelVariable) || string.IsNullOrWhiteSpace(rootVariable))
        {
            Assert.Skip("Set S2MODKIT_TEST_MODEL and S2MODKIT_TEST_RESOURCE_ROOT to enable the proprietary local integration check.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var modelPath = Path.GetFullPath(modelVariable);
        var resourceRoot = Path.GetFullPath(rootVariable);
        var rootPrefix = resourceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Assert.StartsWith(rootPrefix, modelPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(modelPath));
        Assert.True(Directory.Exists(resourceRoot));
        var originalBytes = await File.ReadAllBytesAsync(modelPath, cancellationToken);
        var originalHash = ContentHash.Compute(originalBytes);
        var logicalPath = StableIdentity.NormalizePath(Path.GetRelativePath(resourceRoot, modelPath));
        var artifact = new ArtifactContent(logicalPath, originalHash, originalBytes);
        var adapter = new Source2CompiledModelAdapter();

        var dependencies = await adapter.ReadDependenciesAsync(artifact, cancellationToken);
        var snapshot = await adapter.InspectAsync(artifact, cancellationToken);
        var lods = snapshot.Lods.Select(lod => lod.Level).Order().ToArray();
        var candidateMaterial = snapshot.Lods
            .SelectMany(lod => lod.Meshes.SelectMany(mesh => mesh.DrawCalls.Select(drawCall => (lod.Level, DrawCall: drawCall))))
            .GroupBy(item => item.DrawCall.MaterialPath, StringComparer.Ordinal)
            .Where(group => lods.All(lod => group.Count(item => item.Level == lod) == 1))
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key)
            .FirstOrDefault();
        Assert.False(string.IsNullOrWhiteSpace(candidateMaterial));
        var expected = lods.ToDictionary(lod => lod.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => 1);
        var recipe = new RecipeDocument
        {
            SchemaVersion = 1,
            RecipeId = "local-integration-remove",
            InputHash = originalHash,
            Operations =
            [
                new RemoveComponentOperation
                {
                    OperationId = "remove-common-material",
                    Selector = new ComponentSelector { Kind = "material_exact", MaterialPath = candidateMaterial },
                    ExpectedMatchesByLod = expected,
                },
            ],
        };

        var plan = MutationPlanner.CreatePlan(snapshot, recipe);
        var candidate = await adapter.RewriteAsync(artifact, snapshot, plan, cancellationToken);
        var verification = ModelVerifier.Verify(snapshot, candidate.Snapshot, plan);
        var currentBytes = await File.ReadAllBytesAsync(modelPath, cancellationToken);

        Assert.True(verification.IsValid);
        Assert.NotEqual(originalHash, ContentHash.Compute(candidate.Content.Span));
        Assert.Equal(originalHash, ContentHash.Compute(currentBytes));
        Assert.Equal(12, dependencies.Count);
        Assert.Contains(dependencies, dependency => dependency.LogicalPath == "models/heroes_wip/necro/materials/bunnyoutfit.vmat_c");
        Assert.Equal(dependencies.Count, dependencies.Select(dependency => dependency.LogicalPath).Distinct(StringComparer.Ordinal).Count());
        Assert.All(plan.Operations.SelectMany(operation => operation.TargetBlocks), block => Assert.Equal("MDAT", block.Type));
        Assert.Contains(verification.Boundaries, boundary => boundary.Name == "non_target_blocks_byte_identical" && boundary.Status == "passed");
    }
}
