using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;
using ValveResourceFormat;

namespace S2ModKit.Source2.Tests;

public sealed partial class Source2ComponentCapabilityAnalyzerTests
{
    private const string ResourcePath = "models/test/hero.vmdl_c";
    private const string MaterialPath = "materials/part.vmat_c";

    private static readonly ContentHash EmptyHash = ContentHash.Compute(ReadOnlySpan<byte>.Empty);

    [Fact]
    public void AnalyzerIdentityIsDeterministicAndOrdered()
    {
        IComponentCapabilityAnalyzer first = new Source2CompiledModelAdapter();
        IComponentCapabilityAnalyzer second = new Source2CompiledModelAdapter();

        Assert.Equal("source2_transform_profile", first.AnalyzerName);
        Assert.Equal("2", first.AnalyzerVersion);
        Assert.Equal(first.ComponentVersions, second.ComponentVersions);
        Assert.Equal(
            first.ComponentVersions.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray(),
            first.ComponentVersions.Keys.ToArray());
        Assert.Equal("2", first.ComponentVersions["s2modkit.source2.component_capability"]);
        Assert.Subset(
            first.ComponentVersions.Keys.ToHashSet(),
            ((IModelInspector)new Source2CompiledModelAdapter()).ComponentVersions.Keys.ToHashSet());
    }

    [Fact]
    public void CanAnalyzeIsCheapAndConsistentWithCanInspect()
    {
        var adapter = (IComponentCapabilityAnalyzer)new Source2CompiledModelAdapter();
        var model = Snapshot(ResourcePath, EmptyHash, 32);

        Assert.True(adapter.CanAnalyze(new ArtifactContent(ResourcePath, EmptyHash, new byte[32]), model));
        Assert.False(adapter.CanAnalyze(new ArtifactContent("models/test/hero.vtx", EmptyHash, new byte[32]), model));
        Assert.False(adapter.CanAnalyze(new ArtifactContent(ResourcePath, EmptyHash, new byte[8]), model));
        Assert.False(adapter.CanAnalyze(null!, model));
        Assert.False(adapter.CanAnalyze(new ArtifactContent(ResourcePath, EmptyHash, new byte[32]), null!));
    }

    [Fact]
    public async Task AnalyzeAsyncHonorsCancellationBeforeAnyWork()
    {
        var adapter = (IComponentCapabilityAnalyzer)new Source2CompiledModelAdapter();
        var bytes = new byte[32];
        var input = new ArtifactContent(ResourcePath, ContentHash.Compute(bytes), bytes);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.AnalyzeAsync(
                new ComponentCapabilityAnalysisRequest(input, Snapshot(ResourcePath, input.ContentHash, bytes.Length), []),
                new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task AnalyzeAsyncRejectsIncompleteRequestsWithoutMutatingCallerData()
    {
        var adapter = (IComponentCapabilityAnalyzer)new Source2CompiledModelAdapter();
        var bytes = new byte[32];
        var input = new ArtifactContent(ResourcePath, ContentHash.Compute(bytes), bytes);
        var model = Snapshot(ResourcePath, input.ContentHash, bytes.Length);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => adapter.AnalyzeAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => adapter.AnalyzeAsync(new ComponentCapabilityAnalysisRequest(null!, model, []), CancellationToken.None));

        var hashDrift = await Assert.ThrowsAsync<S2ModKitException>(
            () => adapter.AnalyzeAsync(
                new ComponentCapabilityAnalysisRequest(input, Snapshot(ResourcePath, EmptyHash, bytes.Length), []),
                CancellationToken.None));
        Assert.Equal("INPUT_HASH_DRIFT", hashDrift.Error.Code);

        var sizeDrift = await Assert.ThrowsAsync<S2ModKitException>(
            () => adapter.AnalyzeAsync(
                new ComponentCapabilityAnalysisRequest(input, Snapshot(ResourcePath, input.ContentHash, 31), []),
                CancellationToken.None));
        Assert.Equal("INPUT_HASH_DRIFT", sizeDrift.Error.Code);

        var pathDrift = await Assert.ThrowsAsync<S2ModKitException>(
            () => adapter.AnalyzeAsync(
                new ComponentCapabilityAnalysisRequest(input, Snapshot("models/test/other.vmdl_c", input.ContentHash, bytes.Length), []),
                CancellationToken.None));
        Assert.Equal("INPUT_HASH_DRIFT", pathDrift.Error.Code);

        Assert.Equal(bytes, input.Bytes.ToArray());
    }

    [Fact]
    public async Task AnalyzeAsyncFailsClosedOnEmptyOrDuplicateSelectionIdentities()
    {
        var adapter = (IComponentCapabilityAnalyzer)new Source2CompiledModelAdapter();
        var bytes = new byte[32];
        var input = new ArtifactContent(ResourcePath, ContentHash.Compute(bytes), bytes);
        var model = Snapshot(ResourcePath, input.ContentHash, bytes.Length);
        var mesh = AvailableMesh(0, 0, 10, 2);
        var selection = Selection("cmp_one", Sel(0, 0, mesh.DrawCalls[0].Id, blockIndex: 10));

        var duplicate = await Assert.ThrowsAsync<S2ModKitException>(
            () => adapter.AnalyzeAsync(
                new ComponentCapabilityAnalysisRequest(input, model, [selection, selection]),
                CancellationToken.None));
        Assert.Equal("TRANSFORM_SELECTION_INVALID", duplicate.Error.Code);

        var empty = await Assert.ThrowsAsync<S2ModKitException>(
            () => adapter.AnalyzeAsync(
                new ComponentCapabilityAnalysisRequest(input, model, [Selection(string.Empty, Sel(0, 0, mesh.DrawCalls[0].Id, blockIndex: 10))]),
                CancellationToken.None));
        Assert.Equal("TRANSFORM_SELECTION_INVALID", empty.Error.Code);
    }

    [Fact]
    public async Task AnalyzeAsyncReturnsEmptyWithoutParsingWhenNoSelectionsAreRequested()
    {
        var adapter = (IComponentCapabilityAnalyzer)new Source2CompiledModelAdapter();
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        var input = new ArtifactContent(ResourcePath, ContentHash.Compute(bytes), bytes);

        var results = await adapter.AnalyzeAsync(
            new ComponentCapabilityAnalysisRequest(input, Snapshot(ResourcePath, input.ContentHash, bytes.Length), []),
            CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal(bytes, input.Bytes.ToArray());
    }

    [Fact]
    public async Task AnalyzeAsyncPropagatesStructuredParseFailureForUnusableInput()
    {
        var adapter = (IComponentCapabilityAnalyzer)new Source2CompiledModelAdapter();
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        var input = new ArtifactContent(ResourcePath, ContentHash.Compute(bytes), bytes);
        var selection = Selection("cmp_one", Sel(0, 0, Call(0, 0, 0, 0, 3).Id));

        var failure = await Assert.ThrowsAsync<S2ModKitException>(
            () => adapter.AnalyzeAsync(
                new ComponentCapabilityAnalysisRequest(input, Snapshot(ResourcePath, input.ContentHash, bytes.Length), [selection]),
                CancellationToken.None));
        Assert.NotEqual("INPUT_HASH_DRIFT", failure.Error.Code);
        Assert.Equal(bytes, input.Bytes.ToArray());
    }

}
