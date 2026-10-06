using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class SyntheticPipelineTests
{
    [Fact]
    public async Task DirectionalPreviewRequiresCompleteGeometryPortAndNeverWritesCandidates()
    {
        var adapter = new DirectionalPlanningAdapter(2, 2); var workspace = new MemoryWorkspace(adapter.Input);
        Assert.Equal("DIRECTIONAL_PREVIEW_READER_REQUIRED", (await Assert.ThrowsAsync<S2ModKitException>(() =>
            DirectionalApplication(workspace, adapter).PreviewDirectionalSelectionAsync("memory", adapter.Recipe, TestContext.Current.CancellationToken))).Error.Code);
        Assert.Empty(workspace.Builds); Assert.Equal(0, adapter.Rewrites);
    }
}
