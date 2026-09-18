using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed class GuidedWorkflowTests
{
    [Fact]
    public async Task BaseVpkRejectsStaleSourceBeforeOfferingResources()
    {
        var catalogue = CreateCatalogue(ContentHash.Compute("expected"u8));
        var inventory = new FakeInventory(
            ContentHash.Compute("different"u8),
            [new ResourceCatalogEntry("models/heroes/haze/haze.vmdl_c", "source::haze", 10)]);

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() =>
            GuidedWorkflow.SelectFromVpkAsync(catalogue, inventory, requireBaseSourceIdentity: true, TestContext.Current.CancellationToken));

        Assert.Equal("CATALOGUE_SOURCE_STALE", exception.Error.Code);
    }

    [Fact]
    public async Task ModVpkOffersOnlyExactCatalogueLocatorsAndReportsAmbiguity()
    {
        var catalogue = CreateCatalogue(ContentHash.Compute("base"u8));
        var inventory = new FakeInventory(
            ContentHash.Compute("mod"u8),
            [
                new ResourceCatalogEntry("models/heroes/haze/haze.vmdl_c", "source::haze", 10),
                new ResourceCatalogEntry("mods/a/hat.vmdl_c", "source::hat-a", 20),
                new ResourceCatalogEntry("mods/b/hat.vmdl_c", "source::hat-b", 20),
            ]);

        var selection = await GuidedWorkflow.SelectFromVpkAsync(
            catalogue,
            inventory,
            requireBaseSourceIdentity: false,
            TestContext.Current.CancellationToken);

        var haze = selection.Heroes.Single();
        Assert.True(haze.Resources.Single(resource => resource.ResourceId == "haze.primary").Selectable);
        var hat = haze.Resources.Single(resource => resource.ResourceId == "haze.hat");
        Assert.False(hat.Selectable);
        Assert.Equal(HeroCatalogueVerificationContract.ResourceAmbiguous, hat.VerificationStatus);
        Assert.Equal(2, hat.CandidateLogicalPaths.Count);
    }

    [Fact]
    public void CompiledModelUsesCatalogueFilenameWithoutGuessingOtherResources()
    {
        var catalogue = CreateCatalogue(null);

        var selection = GuidedWorkflow.SelectFromCompiledModel(catalogue, "C:/mods/haze.vmdl_c");

        var resources = selection.Heroes.Single().Resources;
        Assert.True(resources.Single(resource => resource.ResourceId == "haze.primary").Selectable);
        Assert.False(resources.Single(resource => resource.ResourceId == "haze.hat").Selectable);
    }

    [Fact]
    public void SessionValidationRejectsSkippedStateTransition()
    {
        var session = GuidedWorkflow.CreateSession(
            "catalogue.json",
            [new GuidedSourceOption("source-1", GuidedWorkflowContract.BaseVpkSource, "Base", "pak01_dir.vpk")],
            expert: false) with
        {
            Step = GuidedWorkflowContract.ComponentSelectionStep,
        };

        var exception = Assert.Throws<S2ModKitException>(() => GuidedWorkflow.ValidateSession(session));

        Assert.Equal("GUIDED_SESSION_STATE_INVALID", exception.Error.Code);
    }

    [Fact]
    public void ComponentChoicesExposeOnlyAvailableScaffoldedActions()
    {
        var model = new ComponentModelIdentity("models/test.vmdl_c", ContentHash.Compute("model"u8), 10);
        var availableRemove = new ComponentCapability(
            "remove_component",
            1,
            ComponentDiscoveryContract.Available,
            [new ComponentCapabilityReason("AVAILABLE", "Ready.")],
            []);
        var blockedTransform = new ComponentCapability(
            "transform_component",
            1,
            ComponentDiscoveryContract.Blocked,
            [new ComponentCapabilityReason("CODEC_MISSING", "Codec missing.")],
            []);
        var discovery = new ComponentDiscoveryResultV2(
            ComponentDiscoveryV2Contract.SchemaVersion,
            model,
            ContentHash.Compute("discovery"u8),
            new ComponentCapabilityAnalyzerIdentity("test", "1", new Dictionary<string, string>()),
            [
                new MaterialGroupComponentCandidateV2(
                    "cmp_0123456789abcdef01234567",
                    model,
                    "materials/ready.vmat",
                    "Ready component",
                    [new ComponentCandidateLod(0, ["dc_0123456789abcdef01234567"], 1)],
                    [availableRemove, blockedTransform]),
                new MaterialGroupComponentCandidateV2(
                    "cmp_111111111111111111111111",
                    model,
                    "materials/blocked.vmat",
                    "Blocked component",
                    [new ComponentCandidateLod(0, ["dc_111111111111111111111111"], 1)],
                    [blockedTransform]),
            ],
            []);

        var choice = Assert.Single(GuidedWorkflow.CreateComponentChoices(discovery));

        Assert.Equal("Ready component", choice.DisplayLabel);
        var action = Assert.Single(choice.Actions);
        Assert.Equal(RecipeScaffoldContract.RemoveIntent, action.Intent);
    }

    [Fact]
    public void DryRunReviewSummarizesPlanWithoutClaimingBuild()
    {
        var hash = ContentHash.Compute("plan"u8);
        var selected = new SelectedDrawCall(0, "models/test.vmdl_c", 0, 1, "dc_test", "materials/test.vmat", 0, 0, 3);
        var operation = new PlannedOperation(
            "remove-test",
            "remove_component",
            1,
            [selected],
            [new PlannedTargetBlock(1, "MDAT", hash)]);
        var plan = new MutationPlan("recipe", hash, hash, [operation]);
        var evidence = new EvidenceReport
        {
            ReportId = "plan",
            CreatedUtc = DateTimeOffset.UnixEpoch,
            Command = "plan",
            Status = "passed",
            Input = new ArtifactEvidence("models/test.vmdl_c", hash, 10),
            PlanFingerprint = hash,
        };

        var review = GuidedWorkflow.CreateDryRunReview(new PlanRunResult(plan, evidence));

        Assert.Equal(1, review.OperationCount);
        Assert.Equal(1, review.SelectedDrawCallCount);
        Assert.Equal(1, review.LodCount);
        Assert.Equal(1, review.TargetBlockCount);
        Assert.False(review.IncludesCoupledCollision);
    }

    private static HeroCatalogueDocument CreateCatalogue(ContentHash? expectedHash) => new()
    {
        CatalogueId = "deadlock.heroes",
        Revision = "test.1",
        Source = new HeroCatalogueSourceProvenance { ExpectedDirectoryHash = expectedHash },
        Heroes =
        [
            new HeroCatalogueEntry
            {
                HeroId = "haze",
                DisplayName = "Haze",
                Resources =
                [
                    new HeroResourceLocator
                    {
                        ResourceId = "haze.primary",
                        DisplayName = "Primary model",
                        Role = HeroCatalogueContract.PrimaryModelRole,
                        LogicalPath = "models/heroes/haze/haze.vmdl_c",
                    },
                    new HeroResourceLocator
                    {
                        ResourceId = "haze.hat",
                        DisplayName = "Hat",
                        Role = HeroCatalogueContract.AccessoryModelRole,
                        LogicalPath = "models/heroes/haze/hat.vmdl_c",
                        Optional = true,
                    },
                ],
            },
        ],
    };

    private sealed class FakeInventory(
        ContentHash sourceContentHash,
        IReadOnlyList<ResourceCatalogEntry> entries) : IResourceCatalogInventory
    {
        public ResourceCatalogDescriptor Descriptor { get; } = new("vpk", "test", "runtime_provided", 100, "metadata_only");

        public ContentHash SourceContentHash { get; } = sourceContentHash;

        public Task<IReadOnlyList<ResourceCatalogEntry>> ListEntriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(entries);

        public Task<ResourceCatalogEntry?> FindEntryAsync(string logicalPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<ResourceCatalogEntry?>(entries.FirstOrDefault(entry => entry.LogicalPath == logicalPath));

        public Task<ResourceCatalogArtifact?> TryOpenAsync(string logicalPath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Guided selection must not open payloads.");
    }
}
