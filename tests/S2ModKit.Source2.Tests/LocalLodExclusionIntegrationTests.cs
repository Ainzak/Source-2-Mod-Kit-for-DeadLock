using System.Globalization;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Source2.Tests;

public sealed class LocalLodExclusionIntegrationTests
{
    [Fact]
    public async Task ConfiguredZeroMaskModelInspectsReadonlyAndStaysImmutable()
    {
        var modelVariable = Environment.GetEnvironmentVariable("S2MODKIT_TEST_ZERO_MASK_MODEL");
        var logicalPathVariable = Environment.GetEnvironmentVariable("S2MODKIT_TEST_ZERO_MASK_LOGICAL_PATH");
        var hashVariable = Environment.GetEnvironmentVariable("S2MODKIT_TEST_ZERO_MASK_SHA256");
        var lodsVariable = Environment.GetEnvironmentVariable("S2MODKIT_TEST_ZERO_MASK_LODS");
        var representedVariable = Environment.GetEnvironmentVariable("S2MODKIT_TEST_ZERO_MASK_REPRESENTED");
        var inventoriedVariable = Environment.GetEnvironmentVariable("S2MODKIT_TEST_ZERO_MASK_INVENTORIED");
        if (string.IsNullOrWhiteSpace(modelVariable)
            || string.IsNullOrWhiteSpace(logicalPathVariable)
            || string.IsNullOrWhiteSpace(hashVariable)
            || string.IsNullOrWhiteSpace(lodsVariable)
            || string.IsNullOrWhiteSpace(representedVariable)
            || string.IsNullOrWhiteSpace(inventoriedVariable))
        {
            Assert.Skip("Set the six S2MODKIT_TEST_ZERO_MASK variables to enable the proprietary LOD-exclusion integration check.");
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
        var expectedLods = int.Parse(lodsVariable, CultureInfo.InvariantCulture);
        var expectedRepresented = int.Parse(representedVariable, CultureInfo.InvariantCulture);
        var expectedInventoried = int.Parse(inventoriedVariable, CultureInfo.InvariantCulture);

        Assert.Equal(Enumerable.Range(0, expectedLods), snapshot.Lods.Select(lod => lod.Level));
        var mdatBlocks = snapshot.Artifact.Blocks
            .Where(block => string.Equals(block.Type, "MDAT", StringComparison.Ordinal))
            .Select(block => block.Index)
            .ToHashSet();
        Assert.Equal(expectedInventoried, mdatBlocks.Count);
        var representedBlocks = snapshot.Lods
            .SelectMany(lod => lod.Meshes)
            .Select(mesh => mesh.ResourceBlockIndex)
            .ToHashSet();
        Assert.Equal(expectedRepresented, representedBlocks.Count);
        Assert.ProperSubset(mdatBlocks, representedBlocks);
        Assert.Equal(expectedHash, snapshot.Artifact.ContentHash);

        var plan = RemovalPlan(logicalPath, expectedHash, snapshot.Artifact, mdatBlocks);
        Assert.False(adapter.CanRewrite(snapshot, plan));

        // A forged snapshot that fabricates full MDAT coverage cannot waive the strict reparse:
        // the actual zero-mask input still rejects mutation of the whole resource.
        var forged = ForgedCompleteSnapshot(snapshot.Artifact, logicalPath, mdatBlocks);
        var strict = await Assert.ThrowsAsync<S2ModKitException>(
            () => adapter.RewriteAsync(input, forged, plan, cancellationToken));

        Assert.Equal("ZERO_LOD_MESH_READ_ONLY", strict.Error.Code);

        var after = await File.ReadAllBytesAsync(modelPath, cancellationToken);
        Assert.Equal(expectedHash, ContentHash.Compute(after));
    }

    private static ModelSnapshot ForgedCompleteSnapshot(
        ArtifactSnapshot artifact,
        string logicalPath,
        IReadOnlySet<int> mdatBlocks)
    {
        var ordinal = 0;
        var meshes = mdatBlocks.Order()
            .Select(blockIndex => new MeshSnapshot(logicalPath, ordinal++, blockIndex, ContentHash.Compute([(byte)blockIndex]), []))
            .ToArray();
        return new ModelSnapshot(artifact, [new LodSnapshot(0, meshes)]);
    }

    private static MutationPlan RemovalPlan(
        string logicalPath,
        ContentHash inputHash,
        ArtifactSnapshot artifact,
        IReadOnlySet<int> mdatBlocks)
    {
        var target = mdatBlocks.Min();
        var targetBlock = artifact.Blocks.Single(block => block.Index == target);
        var selected = new SelectedDrawCall(
            0,
            logicalPath,
            0,
            target,
            "forged-zero-mask-probe",
            "materials/synthetic/probe.vmat",
            0,
            0,
            3);
        var operation = new PlannedOperation(
            "zero-mask-probe",
            "remove_component",
            1,
            [selected],
            [new PlannedTargetBlock(target, "MDAT", targetBlock.ContentHash)]);
        return new MutationPlan("zero-mask-probe", inputHash, ContentHash.Compute("zero-mask-probe"u8), [operation]);
    }
}
