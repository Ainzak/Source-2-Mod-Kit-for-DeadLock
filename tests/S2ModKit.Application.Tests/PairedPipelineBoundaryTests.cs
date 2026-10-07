using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class SyntheticPipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PairedContractAcceptanceCannotCallAnOlderPlannerWriterOrPublishAPlan(bool build)
    {
        var adapter = new PairedPlanningAdapter(1, 1) { Failure = "mixed" }; var workspace = new MemoryWorkspace(adapter.Input);
        var app = DirectionalApplication(workspace, adapter); var recipe = adapter.PairedRecipe;
        var exception = await Assert.ThrowsAsync<S2ModKitException>(async () =>
        {
            if (build) await app.BuildAsync("memory", recipe, TestContext.Current.CancellationToken);
            else await app.PlanAsync("memory", recipe, TestContext.Current.CancellationToken);
        });
        Assert.Equal(build ? "PAIRED_VERIFICATION_UNAVAILABLE" : "DIRECTIONAL_RESULT_DRIFT", exception.Error.Code); Assert.Equal(0, adapter.Rewrites); Assert.Empty(workspace.Builds); Assert.Empty(workspace.SavedPlans);
    }

    [Fact]
    public async Task GenericSnapshotVerificationCannotQualifyAPairedResource()
    {
        var adapter = new DirectionalPlanningAdapter(1, 1);
        var snapshot = await adapter.InspectAsync(adapter.Input, TestContext.Current.CancellationToken);
        var result = ModelVerifier.Verify(snapshot, snapshot, ExperimentalVisualContractTests.PairedPlan());
        Assert.False(result.IsValid); Assert.Contains(result.Boundaries, b => b.Status == "failed");
        Assert.DoesNotContain(result.Boundaries, b => b.Name == "paired_geometry");
    }
}
