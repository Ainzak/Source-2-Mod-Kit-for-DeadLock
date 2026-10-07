using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class ExperimentalVisualContractTests
{
    private static readonly double[] PairedLeftWeights = [1d, 0d, 0d];
    private static readonly double[] PairedRightWeights = [0d, 1d, 0d];
    internal static MutationPlan PairedPlan(int members = 1, int lods = 1, bool bone = false, bool mixed = true, bool physics = false)
    {
        var original = DirectionalPlan(members, lods, bone); var old = original.Operations[0].DirectionalTransformTarget!;
        var fields = new[] { new PairedDirectionalField("left", old.DirectionalTransform.Field with { Pivot = new() { Kind = "explicit_point", Point = new() { Y = -16 } } }),
            new PairedDirectionalField("right", old.DirectionalTransform.Field with { Pivot = new() { Kind = "explicit_point", Point = new() { Y = 16 } } }) };
        var dispatch = old.Buffers.Select(b =>
        {
            var weights = new[] { ContentHash.Compute(PairedLeftWeights.SelectMany(BitConverter.GetBytes).ToArray()),
                ContentHash.Compute(PairedRightWeights.SelectMany(BitConverter.GetBytes).ToArray()) };
            var rows = fields.Select((f, i) => new PairedFieldDispatch(f.FieldId, [i], [], [i], [i], default, weights[i])).ToArray();
            rows = rows.Select(f => f with { MembershipHash = PairedContractValidator.FieldMembershipHash(3, f) }).ToArray();
            return new PairedBufferDispatch(b.MemberId, b.Lod, rows, [2], PairedContractValidator.DispatchMembershipHash(3, rows), PairedContractValidator.DispatchWeightHash(rows));
        }).ToArray();
        var stride = mixed ? 28 : 24;
        var buffers = old.Buffers.Select((b, i) => b with
        {
            MaskHash = dispatch[i].MembershipHash,
            WeightHash = dispatch[i].WeightHash,
            FullVertexCount = 2,
            TransitionVertexCount = 0,
            PinnedVertexCount = 1,
            PositionLayout = b.PositionLayout with { Stride = stride, Offset = 0 },
            PackedFrameLayout = b.PackedFrameLayout with { Stride = stride, Offset = 12 }
        }).ToArray();
        var roots = new[] { new PairedRootBone(0, "exact_source_bone", -1, 4, true, Hash), new PairedRootBone(1, "ordinary_driver", 0, 0, false, Hash) };
        var proceduralSets = old.ContextBuffers.Select(c =>
        {
            int[] indices = c.Selected ? [2] : [1];
            return new DirectionalProtectedSet(c.Lod, c.MeshOrdinal, c.VertexBufferOrdinal, c.DecodedBufferHash,
                indices, DirectionalContractValidator.VertexSetHash(indices), 1, Hash, Hash, Hash, Hash);
        }).ToArray();
        var skins = old.ContextBuffers.Select(c =>
        {
            var contributors = new[] { new PairedRootContributorSet(0, c.Selected ? [2] : [1], DirectionalContractValidator.VertexSetHash(c.Selected ? [2] : [1])),
                new PairedRootContributorSet(1, c.Selected ? [0, 1] : [0, 2], DirectionalContractValidator.VertexSetHash(c.Selected ? [0, 1] : [0, 2])) };
            return new PairedSkinningBufferFacts(c.Lod, c.MeshOrdinal, c.VertexBufferOrdinal, stride, 0, 12,
                mixed ? 14u : 30u, 16, mixed ? 8 : 4, 28, mixed ? 24 : 20, 4, 1,
                c.SkinningHash, c.SkinningHash, c.RootRenderRemapHash, contributors, MutationPlanJson.ComputeDirectionalFactsHash(contributors));
        }).ToArray();
        var sources = old.SourceBlocks.ToList(); var preserved = old.PreservationTargets.ToList();
        var data = sources.Single(b => b.Type == "DATA"); var physicsIndex = data.Index + 1;
        if (physics)
        {
            sources.Add(new(physicsIndex, "PHYS", Hash));
            preserved.Add(new(physicsIndex, "$payload", "collision_payload", "resource", "preserve_unverified", Hash, []));
        }
        var rootReferences = roots.Select(r => new PairedConsumerReference("root_bone", "root", r.Index, r.Name, r.Index,
            r.Name, MutationPlanJson.ComputeDirectionalFactsHash(r), MutationPlanJson.ComputeDirectionalFactsHash(r), "preserve_unverified")).ToArray();
        PairedConsumerRecord Record(string id, int block, string path, IReadOnlyList<PairedConsumerReference> references) => new(id, block, path,
            Hash, Hash, "preserve_unverified", references, MutationPlanJson.ComputeDirectionalFactsHash(references));
        var families = PairedContractValidator.ConsumerCategories.Select(category =>
        {
            PairedConsumerRecord[] rows = category switch
            {
                "root_skeleton" => [Record("root", data.Index, "$payload", rootReferences)],
                "render_skeleton" => old.ContextBuffers.Select(c => c.ResourceBlockIndex).Distinct().Order().Select(block => Record($"render-{block:D6}", block, "m_modelSkeleton.m_boneName", rootReferences)).ToArray(),
                "skin_remap_weights" => old.ContextBuffers.SelectMany(c => new[] { c.ResourceBlockIndex, c.VertexResourceBlockIndex }).Distinct().Order()
                    .Select(block => Record($"skin-{block:D6}", block, sources.Single(s => s.Index == block).Type == "MDAT" ? "m_boneRemap" : "BLENDINDICES_BLENDEWEIGHT", rootReferences)).ToArray(),
                "physics" when physics => [Record("physics", physicsIndex, "$payload", [])],
                "fe_controls" when physics => [Record("controls", physicsIndex, "fe.controls", [new("fe_control", "fe", 0, "unmapped_control", -1, "", ContentHash.Compute([]), Hash, "preserve_unverified")])],
                _ => []
            };
            return new PairedConsumerFamily(category, rows.Length != 0, rows);
        }).ToArray();
        var procedural = new PlannedPairedProceduralInputs(data.Index, Hash, Hash, roots, MutationPlanJson.ComputeDirectionalFactsHash(roots), skins,
            [new(0, roots[0].Name, proceduralSets, DirectionalContractValidator.ContributorSetHash(proceduralSets))],
            proceduralSets, MutationPlanJson.ComputeDirectionalFactsHash(proceduralSets), families, MutationPlanJson.ComputeDirectionalFactsHash(families));
        var frozenFields = fields.Select(f =>
        {
            var pivot = new AffineEvidenceResolver().ResolvePivot(new(f.Field.Pivot, old.DirectionalTransform.Members[0].Lods.Select(l => l.Lod).ToArray(), [], []));
            return new PlannedPairedField(f.FieldId, new("explicit_point", f.Field.Pivot.Point!, "model", pivot.SourceIdentity, pivot.SourceHash, null), DirectionalContractValidator.Certificate(f.Field));
        }).ToArray();
        var target = new PlannedPairedTransformTarget(Hash, Hash, "root_paired_directional_preservation_visual", 1, "retain_expand_boxes_preserve_runtime_paired", 1,
            new("preserve_unverified", 1), new("reject", 1), new("reject", 1), new("preserve_source_coincidence", 1), new("preserve_serialized_inputs_unverified", 1),
            old.Selector, new(old.DirectionalTransform.Members, fields, old.DirectionalTransform.Protection), frozenFields, PairedContractValidator.Separation(fields),
            old.MaximumDisplacement, old.DisplacementLimit, buffers, dispatch, old.WordAudits, old.Protection, old.ContextBuffers, old.Coincidences,
            buffers.Select(b => new PairedSourceTriangleFacts(b.MemberId, b.Lod, b.DecodedIndexBufferHash, [0, 1, 2], DirectionalContractValidator.VertexSetHash([0, 1, 2]),
                [0], ContentHash.Compute(new byte[] { 0 }), ContentHash.Compute(new byte[] { 0 }), 1, 1, 0, 0)).ToArray(),
            procedural, old.BoxTargets, old.BoxClosures, preserved.OrderBy(p => p.ResourceBlockIndex).ThenBy(p => p.FieldPath, StringComparer.Ordinal).ToArray(), sources);
        var operation = original.Operations[0] with { Version = 10, DirectionalTransformTarget = null, PairedTransformTarget = target };
        return RehashPaired(original with { SchemaVersion = 7, Operations = [operation] }, target);
    }

    private static MutationPlan RehashPaired(MutationPlan plan, PlannedPairedTransformTarget target)
    {
        target = target with { TargetFingerprint = MutationPlanJson.ComputePairedTargetFingerprint(target) };
        plan = plan with { Operations = [plan.Operations[0] with { PairedTransformTarget = target }] };
        return plan with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(plan) };
    }
    private static RecipeDocument PairedRecipe(int members = 1, int lods = 1, bool bone = false)
    {
        var plan = PairedPlan(members, lods, bone);
        return new() { SchemaVersion = 11, RecipeId = plan.RecipeId, InputHash = plan.InputHash, Operations = [PairedContractValidator.Operation(plan.Operations[0])] };
    }
    private static PairedFieldOptions PairedOptions()
    {
        var recipe = PairedRecipe(); var op = (TransformComponentOperation)recipe.Operations[0];
        return new(1, "paired_directional_field_options", 1, recipe.InputHash, op.RuntimeMetadataPolicy!, op.ZeroBoneBoxPolicy!, op.ZeroRenderSpherePolicy!,
            op.SourceTrianglePolicy!, op.ProceduralInputPolicy!, op.PairedTransform!, op.Limits.MaximumVertexDisplacement);
    }
    private static EvidenceReport PairedReport(bool built, bool bone = false, bool physics = false)
    {
        var plan = PairedPlan(2, 2, bone, physics: physics); var t = plan.Operations[0].PairedTransformTarget!;
        var observed = built ? new PairedDirectionalObservation(t.Buffers.Select(b => new CoordinatedBufferObservation(b.MemberId, b.Lod, b.MeshOrdinal, b.VertexBufferOrdinal,
            b.ExpectedPositionHash, b.ExpectedPackedFrameHash, b.ExpectedDecodedVertexBufferHash, b.MaskHash, b.WeightHash, b.MaximumDisplacement)).ToArray(),
            t.Dispatch, t.WordAudits, t.Protection, t.ContextBuffers, t.Coincidences, t.BoxClosures, t.Fields, t.Separation, t.SourceTriangles, t.ProceduralInputs) : null;
        return DirectionalReport(built, bone) with
        {
            SchemaVersion = 12,
            PlanFingerprint = plan.Fingerprint,
            Blocks = t.SourceBlocks.Select(b => new ResourceBlockEvidence(b.Index, b.Type, b.InputHash,
                built ? plan.Operations[0].TargetBlocks.Any(target => target.Index == b.Index) ? EllipsoidOutputHash : b.InputHash : null,
                built ? plan.Operations[0].TargetBlocks.Any(target => target.Index == b.Index) ? "changed" : "unchanged"
                    : plan.Operations[0].TargetBlocks.Any(target => target.Index == b.Index) ? "planned_change" : "planned_unchanged")).ToArray(),
            Boundaries = PairedContractValidator.PlannedBoundaries().Select(b => built && b.Status == "not_applicable" ? b with { Status = "passed" } : b).ToArray(),
            Operations = [new("scale", "transform_component", 10, t.Selector.DrawCallIds!, ["models/test.vmdl_c"])
            { PairedTransform = new(t, new(plan.RecipeId, plan.Inputs, plan.Operations[0].SelectedDrawCalls, plan.Operations[0].TargetBlocks), observed,
                t.BoxTargets.Select(b => new ExperimentalBoxEvidence(b, built ? b.ExpectedWords : null, built ? "passed" : "planned")).ToArray(),
                t.PreservationTargets.Select(p => new ExperimentalPreservationEvidence(p, built ? p.SourcePayloadHash : null, built ? p.OriginalWords : null, built ? "passed" : "planned")).ToArray()) }]
        };
    }
}
