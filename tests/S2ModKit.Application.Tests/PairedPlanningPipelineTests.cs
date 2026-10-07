using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class SyntheticPipelineTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    public async Task PairedSourcePlansEmitBoundSchemaTwelveEvidenceWithoutPublishingCandidates(int members, int lods)
    {
        var adapter = new PairedPlanningAdapter(members, lods); var workspace = new MemoryWorkspace(adapter.Input);
        var app = DirectionalApplication(workspace, adapter); var token = TestContext.Current.CancellationToken;
        var result = await app.PlanAsync("memory", adapter.PairedRecipe, token);
        Assert.Equal(7, result.Plan.SchemaVersion); Assert.Equal(12, result.Evidence.SchemaVersion);
        Assert.Null(result.Evidence.Output); Assert.Null(result.Evidence.Operations[0].PairedTransform!.Observed);
        PairedContractValidator.ValidatePlan(result.Plan); PairedContractValidator.ValidateEvidence(result.Evidence);
        Assert.Equal(members * lods, result.Plan.Operations[0].PairedTransformTarget!.Buffers.Count);
        Assert.Empty(workspace.Builds); Assert.Equal(0, adapter.Rewrites);
        Assert.Equal("PAIRED_VERIFICATION_UNAVAILABLE", (await Assert.ThrowsAsync<S2ModKitException>(() => app.BuildAsync("memory", adapter.PairedRecipe, token))).Error.Code);
        Assert.Empty(workspace.Builds); Assert.Equal(0, adapter.Rewrites);
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("source")]
    [InlineData("mixed")]
    [InlineData("closure")]
    public async Task PairedPlanningRejectsMissingOrForgedAdapterFactsBeforeSavingAPlan(string failure)
    {
        var adapter = new PairedPlanningAdapter(2, 2) { Failure = failure }; var workspace = new MemoryWorkspace(adapter.Input);
        await Assert.ThrowsAsync<S2ModKitException>(() => DirectionalApplication(workspace, adapter).PlanAsync("memory", adapter.PairedRecipe, TestContext.Current.CancellationToken));
        Assert.Empty(workspace.Builds); Assert.Empty(workspace.SavedPlans); Assert.Equal(0, adapter.Rewrites);
    }

    private class PairedPlanningAdapter : DirectionalPlanningAdapter
    {
        protected readonly PlannedPairedTransformTarget paired;
        private readonly IReadOnlyList<PlannedTargetBlock> pairedBlocks;
        public PairedPlanningAdapter(int members, int lods) : base(members, lods)
        {
            var template = ExperimentalVisualContractTests.PairedPlan(members, lods);
            var original = template.Operations[0].PairedTransformTarget!;
            paired = original with
            {
                InputHash = Input.ContentHash,
                Selector = target.Selector,
                PairedTransform = original.PairedTransform with { Members = target.DirectionalTransform.Members }
            };
            paired = paired with { TargetFingerprint = MutationPlanJson.ComputePairedTargetFingerprint(paired) };
            pairedBlocks = template.Operations[0].TargetBlocks;
            PairedRecipe = new()
            {
                SchemaVersion = 11,
                RecipeId = "synthetic-pair",
                InputHash = Input.ContentHash,
                Operations = [PairedContractValidator.Operation(template.Operations[0] with { PairedTransformTarget = paired })]
            };
        }
        public RecipeDocument PairedRecipe { get; }
        public override TransformPlanningResult PlanTransform(TransformPlanningRequest request)
        {
            var facts = Failure switch { "intent" => paired with { DisplacementLimit = 32 }, "source" => paired with { InputHash = ContentHash.Compute("forged"u8) }, _ => paired };
            facts = facts with { TargetFingerprint = MutationPlanJson.ComputePairedTargetFingerprint(facts) };
            return new([], [], Failure == "closure" ? pairedBlocks.Skip(1).ToArray() : pairedBlocks)
            { PairedTransformTarget = facts, DirectionalTransformTarget = Failure == "mixed" ? target : null };
        }
    }
}
