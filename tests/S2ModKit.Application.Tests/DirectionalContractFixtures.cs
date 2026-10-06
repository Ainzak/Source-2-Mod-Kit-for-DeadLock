using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class ExperimentalVisualContractTests
{
    internal static MutationPlan DirectionalPlan(int members = 1, int lods = 1, bool bone = false)
    {
        var original = CoordinatedPlan(); var old = original.Operations[0].CoordinatedTransformTarget!;
        var template = old.Buffers[0];
        var memberIds = Enumerable.Range(0, members).Select(i => $"member-{i:D2}").ToArray();
        var maps = memberIds.Select((id, mi) => new CoordinatedMember(id,
            Enumerable.Range(0, lods).Select(l => new CoordinatedMemberLod(l, [$"dc_{l * members + mi:x24}"], 3)).ToArray())).ToArray();
        var buffers = new List<PlannedCoordinatedBuffer>(); var source = new List<PlannedTargetBlock>();
        var calls = new List<SelectedDrawCall>(); var context = new List<DirectionalContextBuffer>();
        var protectedSets = new List<DirectionalProtectedSet>(); var boxes = new List<PlannedExperimentalBoxTarget>(); var closures = new List<DirectionalBoxClosure>();
        uint[] matrix = [0x3f800000, 0, 0, 0, 0, 0x3f800000, 0, 0, 0, 0, 0x3f800000, 0];
        var matrixHash = ContentHash.Compute(matrix.SelectMany(BitConverter.GetBytes).ToArray());
        for (var l = 0; l < lods; l++)
        {
            var mesh = l;
            var baseBlock = l * (1 + 2 * (members + 1));
            source.Add(new(baseBlock, "MDAT", Hash));
            for (var mi = 0; mi <= members; mi++)
            {
                var selected = mi < members;
                var vb = baseBlock + 1 + mi * 2; var ib = vb + 1;
                source.Add(new(vb, "MVTX", Hash)); source.Add(new(ib, "MIDX", Hash));
                context.Add(new(l, mesh, mi, baseBlock, vb, ib, 3, "models/test.vmdl_c", selected, (ulong)(1 << mi), 1,
                    Hash, Hash, Hash, Hash, Hash, Hash));
                var indices = selected ? new[] { 2 } : bone ? new[] { 1 } : Array.Empty<int>();
                var wordHash = indices.Length == 0 ? ContentHash.Compute([]) : Hash;
                protectedSets.Add(new(l, mesh, mi, Hash, indices, DirectionalContractValidator.VertexSetHash(indices), indices.Length,
                    wordHash, wordHash, wordHash, wordHash));
                if (!selected) continue;
                buffers.Add(template with
                {
                    MemberId = memberIds[mi],
                    Lod = l,
                    MeshOrdinal = mesh,
                    ResourceBlockIndex = baseBlock,
                    VertexBufferOrdinal = mi,
                    IndexBufferOrdinal = mi,
                    VertexResourceBlockIndex = vb,
                    IndexResourceBlockIndex = ib,
                    ChangedFrameCount = 2
                });
                calls.Add(new(l, "models/test.vmdl_c", mesh, baseBlock, maps[mi].Lods[l].DrawCallIds[0], "materials/part.vmat", mi, mi * 3, 3));
            }
            boxes.Add(old.BoxTargets[0] with { ResourceBlockIndex = baseBlock, CoordinateMatrixHash = matrixHash, CoordinateMatrixWords = matrix, ContributorCount = (members + 1) * 3 });
            var contributions = context.Where(c => c.Lod == l).Select(c => new DirectionalBoxContributor(c.Lod, c.MeshOrdinal, c.VertexBufferOrdinal,
                3, Hash, c.PositionHash, c.Selected ? EllipsoidOutputHash : c.PositionHash)).ToArray();
            closures.Add(new(baseBlock, boxes[^1].FieldPath, l, mesh, contributions, MutationPlanJson.ComputeDirectionalFactsHash(contributions)));
        }
        var data = source.Max(b => b.Index) + 1; source.Add(new(data, "DATA", Hash));
        var boneLods = Enumerable.Range(0, lods).Select(l => new DirectionalBoneLod(l,
            DirectionalContractValidator.ContributorSetHash(protectedSets.Where(s => s.Lod == l)), members + 1)).ToArray();
        DirectionalProtectionAssertion assertion = bone ? new DirectionalBoneAssertion
        {
            Version = 1,
            AssertionId = "fixed",
            BoneIndex = 0,
            BoneName = "exact_source_bone",
            RootSkeletonHash = Hash,
            Lods = boneLods
        } : new DirectionalVertexAssertion
        {
            Version = 1,
            AssertionId = "fixed",
            Sets = maps.SelectMany(m => m.Lods.Select(l => new DirectionalVertexSet(m.MemberId, l.Lod, Hash, [2], DirectionalContractValidator.VertexSetHash([2]), 1))).ToArray()
        };
        var field = new DirectionalEllipsoidField("directional_ellipsoid", 1, "model", new() { Kind = "explicit_point", Point = new() },
            new() { X = 10, Y = 8, Z = 4 }, 0.4f, new() { X = 1.5f, Y = 1.5f, Z = 1 }, new("directional_ellipsoid_numeric", 1));
        var resolvedSets = protectedSets.Where(s => bone || s.VertexBufferOrdinal < members).ToArray();
        var explicitPivot = new AffineEvidenceResolver().ResolvePivot(new(field.Pivot, Enumerable.Range(0, lods).ToArray(), [], []));
        var protection = new PlannedDirectionalProtection([new("fixed", resolvedSets, DirectionalContractValidator.ContributorSetHash(resolvedSets))],
            protectedSets, MutationPlanJson.ComputeDirectionalFactsHash(protectedSets));
        var target = new PlannedDirectionalTransformTarget(Hash, Hash, "root_protected_directional_ellipsoid_visual", 1,
            "retain_expand_boxes_preserve_runtime_directional", 1, new("preserve_unverified", 1), new("reject", 1), new("reject", 1),
            new() { Kind = "draw_call_ids", DrawCallIds = maps.SelectMany(m => m.Lods).SelectMany(l => l.DrawCallIds).Order(StringComparer.Ordinal).ToArray() },
            new(maps, field, new("keep_fixed", 1, [assertion])), new("explicit_point", new(), "model", explicitPivot.SourceIdentity, explicitPivot.SourceHash, null), DirectionalContractValidator.Certificate(field),
            1, 64, buffers, buffers.Select(b => new DirectionalWordAudit(b.MemberId, b.Lod, [0, 1], DirectionalContractValidator.VertexSetHash([0, 1]),
                [0, 1], DirectionalContractValidator.VertexSetHash([0, 1]), [2], DirectionalContractValidator.VertexSetHash([2]), Hash, Hash, Hash, Hash, Hash, Hash)).ToArray(), protection, context,
            Enumerable.Range(0, lods).Select(l => new DirectionalCoincidenceLod(l, Hash, Hash, 3 * (members + 1), 0, members * 2, 0)).ToArray(),
            boxes, closures, [old.PreservationTargets[0] with { ResourceBlockIndex = data }], source.OrderBy(b => b.Index).ToArray());
        target = target with { TargetFingerprint = MutationPlanJson.ComputeDirectionalTargetFingerprint(target) };
        var changed = buffers.SelectMany(b => new[] { b.ResourceBlockIndex, b.VertexResourceBlockIndex }).Distinct().Order().ToHashSet();
        var plan = original with
        {
            SchemaVersion = 6,
            Operations = [new("scale", "transform_component", 9, calls,
            source.Where(s => changed.Contains(s.Index)).OrderBy(s => s.Index).ToArray()) { DirectionalTransformTarget = target }]
        };
        return plan with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(plan) };
    }

    private static RecipeDocument DirectionalRecipe(int members = 1, int lods = 1, bool bone = false)
    {
        var plan = DirectionalPlan(members, lods, bone);
        return new() { SchemaVersion = 10, RecipeId = plan.RecipeId, InputHash = plan.InputHash, Operations = [DirectionalContractValidator.Operation(plan.Operations[0])] };
    }

    private static EvidenceReport DirectionalReport(bool built, bool bone = false)
    {
        var plan = DirectionalPlan(2, 2, bone); var t = plan.Operations[0].DirectionalTransformTarget!;
        var observed = built ? new DirectionalObservation(t.Buffers.Select(b => new CoordinatedBufferObservation(b.MemberId, b.Lod, b.MeshOrdinal, b.VertexBufferOrdinal,
            b.ExpectedPositionHash, b.ExpectedPackedFrameHash, b.ExpectedDecodedVertexBufferHash, b.MaskHash, b.WeightHash, b.MaximumDisplacement)).ToArray(),
            t.WordAudits, t.Protection, t.ContextBuffers, t.Coincidences, t.BoxClosures, t.Certificate) : null;
        return ExperimentalReport(built) with
        {
            SchemaVersion = 11,
            PlanFingerprint = plan.Fingerprint,
            Input = new("models/test.vmdl_c", Hash, 12),
            Dependencies = [],
            Output = built ? new("models/test.vmdl_c", EllipsoidOutputHash, 12) : null,
            Boundaries = DirectionalContractValidator.PlannedBoundaries().Select(b => built && b.Status == "not_applicable" ? b with { Status = "passed" } : b).ToArray(),
            Operations = [new("scale", "transform_component", 9, t.Selector.DrawCallIds!, ["models/test.vmdl_c"])
            { DirectionalTransform = new(t, new(plan.RecipeId, plan.Inputs, plan.Operations[0].SelectedDrawCalls, plan.Operations[0].TargetBlocks), observed,
                t.BoxTargets.Select(b => new ExperimentalBoxEvidence(b, built ? b.ExpectedWords : null, built ? "passed" : "planned")).ToArray(),
                t.PreservationTargets.Select(p => new ExperimentalPreservationEvidence(p, built ? p.SourcePayloadHash : null, built ? p.OriginalWords : null, built ? "passed" : "planned")).ToArray()) }],
        };
    }

    private static MutationPlan RehashDirectional(MutationPlan plan, PlannedDirectionalTransformTarget target)
    {
        target = target with { TargetFingerprint = MutationPlanJson.ComputeDirectionalTargetFingerprint(target) };
        plan = plan with { Operations = [plan.Operations[0] with { DirectionalTransformTarget = target }] };
        return plan with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(plan) };
    }
}
