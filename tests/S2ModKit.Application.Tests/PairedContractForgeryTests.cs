using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class ExperimentalVisualContractTests
{
    [Theory]
    [InlineData("simulation_independent")]
    [InlineData("invented_consumer_clearance")]
    [InlineData("morph_fit")]
    public void PairedEvidenceCannotAddAnUnknownPassedQualification(string name)
    {
        var report = PairedReport(true);
        Assert.Throws<S2ModKitException>(() => ReadEvidence(Raw(report with { Boundaries = [.. report.Boundaries, new(name, "passed", "Invented qualification.")] })));
    }
    [Fact]
    public void PairedPlansRejectRehashedSeparationDispatchFacesProtectionAndOwnershipForgeries()
    {
        var plan = PairedPlan(); var t = plan.Operations[0].PairedTransformTarget!; var d = t.Dispatch[0];
        var left = d.Fields[0]; var face = t.SourceTriangles[0];
        var invalid = new[]
        {
            t with { Fields = [t.Fields[0] with { Certificate = t.Fields[0].Certificate with { BetaLowerBound = new("1", "1") } }, t.Fields[1]] },
            t with { Fields = [t.Fields[0] with { Pivot = t.Fields[0].Pivot with { SourceHash = Hash } }, t.Fields[1]] },
            t with { Separation = t.Separation with { Axis = "x" } },
            t with { Separation = t.Separation with { Gap = new("016", "1") } },
            t with { Dispatch = [] },
            t with { Dispatch = [d with { Fields = d.Fields.Reverse().ToArray() }] },
            t with { Dispatch = [d with { Fields = [left with { CoreIndices = [0, 1] }, d.Fields[1]] }] },
            t with { Dispatch = [d with { Fields = [left with { ChangedFrameIndices = [2] }, d.Fields[1]] }] },
            t with { Dispatch = [d with { Fields = [left with { MembershipHash = Hash }, d.Fields[1]] }] },
            t with { Dispatch = [d with { PinnedIndices = [] }] },
            t with { SourceTriangles = [] },
            t with { SourceTriangles = [face with { IndexHash = EllipsoidOutputHash }] },
            t with { SourceTriangles = [face with { ExpectedPartitionHash = EllipsoidOutputHash }] },
            t with { SourceTriangles = [face with { ValidTriangleCount = 0 }] },
            t with { SourceTriangles = [face with { TouchedCollapsedTriangleCount = 1 }] },
            t with { SourceTriangles = [face with { TriangleIndices = [0, 0, 2], TriangleSetHash = DirectionalContractValidator.VertexSetHash([0, 0, 2]) }] },
            t with { Protection = t.Protection with { Union = [t.Protection.Union[0] with { ExpectedPackedFrameHash = EllipsoidOutputHash }, t.Protection.Union[1]] } },
            t with { BoxTargets = [t.BoxTargets[0], t.BoxTargets[0]] },
            t with { ContextBuffers = [t.ContextBuffers[0]] },
            t with { Coincidences = [t.Coincidences[0] with { ExcludedMovingMates = 1 }] },
            t with { Buffers = [t.Buffers[0] with { ChangedPositionCount = 0 }] }
        };
        foreach (var target in invalid) Assert.Throws<S2ModKitException>(() => ReadPlan(Raw(RehashPaired(plan, target))));
        var emptyRight = d with { Fields = [left, d.Fields[1] with { ChangedPositionIndices = [] }] };
        var noPartnerEffect = t with
        {
            Dispatch = [emptyRight],
            Buffers = [t.Buffers[0] with { ChangedPositionCount = 1 }],
            WordAudits = [t.WordAudits[0] with { ChangedPositionIndices = [0], ChangedPositionSetHash = DirectionalContractValidator.VertexSetHash([0]) }],
            Coincidences = [t.Coincidences[0] with { MovingSelectedRecordCount = 1 }]
        };
        Assert.Throws<S2ModKitException>(() => ReadPlan(Raw(RehashPaired(plan, noPartnerEffect))));
        var changed = t with { Dispatch = [d with { Fields = [left with { WeightHash = Hash }, d.Fields[1]] }] };
        Assert.NotEqual(t.TargetFingerprint, MutationPlanJson.ComputePairedTargetFingerprint(changed));
        Assert.Throws<S2ModKitException>(() => ReadPlan(Raw(plan with { Operations = [plan.Operations[0] with { PairedTransformTarget = changed }] })));
    }

    [Fact]
    public void PairedProceduralContractsRejectOmittedRowsTinyContributorsChangedWordsAndUnclassifiedConsumers()
    {
        var plan = PairedPlan(bone: true, physics: true); var t = plan.Operations[0].PairedTransformTarget!; var p = t.ProceduralInputs;
        var skin = p.SkinningBuffers[0]; var bone = p.BoneContributors[0]; var family = p.ConsumerFamilies[0];
        var wrongContributors = new[] { skin.RootContributors[0], skin.RootContributors[1] with { VertexIndices = [0], VertexSetHash = DirectionalContractValidator.VertexSetHash([0]) } };
        var wrongRoots = new[] { p.RootBones[0] with { ParentIndex = 1 }, p.RootBones[1] };
        var invalid = new[]
        {
            p with { RootPayloadHash = EllipsoidOutputHash },
            p with { RootSkeletonHash = EllipsoidOutputHash },
            p with { RootBones = wrongRoots, RootFactsHash = MutationPlanJson.ComputeDirectionalFactsHash(wrongRoots) },
            p with { BoneContributors = [] },
            p with { BoneContributors = [bone with { Sets = [bone.Sets[0]], ContributorSetHash = DirectionalContractValidator.ContributorSetHash([bone.Sets[0]]) }] },
            p with { Union = [p.Union[0] with { ExpectedPositionHash = EllipsoidOutputHash }, p.Union[1]] },
            p with { Union = [] },
            p with { SkinningBuffers = [skin] },
            p with { SkinningBuffers = [skin with { RootContributors = wrongContributors, ContributorHash = MutationPlanJson.ComputeDirectionalFactsHash(wrongContributors) }, p.SkinningBuffers[1]] },
            p with { SkinningBuffers = [skin with { WeightFormat = 11, WeightWidth = 8 }, p.SkinningBuffers[1]] },
            p with { SkinningBuffers = [skin with { IndexOffset = 12 }, p.SkinningBuffers[1]] },
            p with { ConsumerFamilies = p.ConsumerFamilies.Skip(1).ToArray() },
            p with { ConsumerFamilies = [family with { Category = "unknown_solver" }, .. p.ConsumerFamilies.Skip(1)] },
            p with { ConsumerFamilies = p.ConsumerFamilies.Select(f => f.Category == "render_skeleton" ? f with { Present = false, Records = [] } : f).ToArray() },
            p with { ConsumerFamilies = p.ConsumerFamilies.Select(f => f.Category == "physics" ? f with { Present = false, Records = [] } : f).ToArray() },
            p with { ConsumerFamilies = p.ConsumerFamilies.Select(f => f.Category == "root_skeleton" ? f with { Records = [f.Records[0] with { ExpectedPayloadHash = EllipsoidOutputHash }] } : f).ToArray() },
            p with { ConsumerFamilies = p.ConsumerFamilies.Select(f => f.Category == "fe_controls" ? f with { Records = [f.Records[0] with { References = [f.Records[0].References[0] with { Kind = "unknown_driver" }] }] } : f).ToArray() }
        };
        foreach (var facts in invalid)
        {
            var rehashed = facts with { UnionHash = MutationPlanJson.ComputeDirectionalFactsHash(facts.Union), ConsumerInventoryHash = MutationPlanJson.ComputeDirectionalFactsHash(facts.ConsumerFamilies) };
            Assert.Throws<S2ModKitException>(() => ReadPlan(Raw(RehashPaired(plan, t with { ProceduralInputs = rehashed }))));
        }
        Assert.Single(p.SkinningBuffers[0].RootContributors[0].VertexIndices);
        Assert.Contains(p.ConsumerFamilies.Single(f => f.Category == "fe_controls").Records[0].References, r => r.RootBoneIndex == -1 && r.Disposition == "preserve_unverified");
    }

    [Fact]
    public void PairedEvidenceRejectsForgedObservationsMissingBlocksAndUpgradedRuntimeClaims()
    {
        var report = PairedReport(true, true, true); var v = report.Operations[0].PairedTransform!;
        var invalid = new[] { v with { Observed = null }, v with { Observed = v.Observed! with { Dispatch = [] } },
            v with { Observed = v.Observed! with { SourceTriangles = [] } },
            v with { Observed = v.Observed! with { ProceduralInputs = v.Observed!.ProceduralInputs with { ConsumerInventoryHash = EllipsoidOutputHash } } },
            v with { PlanBinding = v.PlanBinding with { RecipeId = "forged" } },
            v with { Boxes = [v.Boxes[0] with { ObservedWords = [0, 0, 0, 0, 0, 0] }, .. v.Boxes.Skip(1)] } };
        foreach (var visual in invalid) Assert.Throws<S2ModKitException>(() => ReadEvidence(Raw(report with { Operations = [report.Operations[0] with { PairedTransform = visual }] })));
        Assert.Throws<S2ModKitException>(() => ReadEvidence(Raw(report with { Blocks = report.Blocks.Skip(1).ToArray() })));
        Assert.Throws<S2ModKitException>(() => ReadEvidence(Raw(report with { Blocks = report.Blocks.Select(b => b.Type == "PHYS" ? b with { OutputHash = EllipsoidOutputHash } : b).ToArray() })));
        Assert.Throws<S2ModKitException>(() => ReadEvidence(Raw(report with { ProofLevel = "live" })));
        var planned = PairedReport(false);
        Assert.Throws<S2ModKitException>(() => ReadEvidence(Raw(planned with { Operations = [planned.Operations[0] with { PairedTransform = planned.Operations[0].PairedTransform! with { Observed = v.Observed } }] })));
    }
}
