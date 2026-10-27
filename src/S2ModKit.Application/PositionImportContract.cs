using System.Globalization;
using System.Text.Json;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public static class PositionImportContract
{
    private static readonly string[] RequiredObservedBoundaries = ["import_reopen", "import_frames", "import_boxes", "import_preservation"];
    public static ContentHash FactsHash<T>(T value) => ContentHash.Compute(PositionAuthoringJson.Canonical(value));
    public static ContentHash TargetHash(PlannedPositionImportTarget t) => MutationPlanJson.ComputePositionImportTargetFingerprint(t);
    public static PositionImportBinding Binding(PositionImportAssets a)
    {
        var s = PositionReturnValidator.ReadSelection(a.Selection);
        var delta = ContentHash.Compute(a.Delta.Span);
        return new(ContentHash.Compute(a.Bundle.Span), delta, PolicyHash(s), ContentHash.Compute(a.Selection.Span), new($"authoring/{delta}.json", delta));
    }
    public static ContentHash PolicyHash(PositionAuthoringSelection s) => FactsHash(new { s.RuntimeMetadataPolicy, s.ProceduralInputPolicy, s.SourceTrianglePolicy, s.ZeroBoneBoxPolicy, s.ZeroRenderSpherePolicy, s.BoundaryFramePolicy });
    public static void ValidateAssets(PositionImportBinding binding, PositionImportAssets a)
    {
        if (Binding(a) != binding) throw PositionAuthoringJson.Invalid("IMPORT_BINDING_DRIFT", "Trusted selection/policy/bundle/delta binding differs.");
    }
    public static PositionAuthoringSelection Selection(ImportPositionsOperation op, PositionImportAssets a)
    {
        ValidateAssets(op.PositionImport.Binding, a);
        var s = PositionReturnValidator.ReadSelection(a.Selection);
        var intent = Intent(s, op.PositionImport.Binding);
        if (!PositionAuthoringJson.Canonical(intent).AsSpan().SequenceEqual(PositionAuthoringJson.Canonical(op.PositionImport))
            || s.MaximumDisplacement != op.Limits.MaximumVertexDisplacement)
            throw PositionAuthoringJson.Invalid("IMPORT_INTENT_DRIFT", "Frozen member/protection/policy/limit differs from trusted selection.");
        return s;
    }
    public static PositionImportIntent Intent(PositionAuthoringSelection s, PositionImportBinding b) => new(b, s.Members, new("incident_source_differential", 1),
        new(s.BoundaryFramePolicy.Kind, s.BoundaryFramePolicy.Version, s.BoundaryFramePolicy.AllowedBoundary?.Select(Map).ToArray()), s.Protection.Select(Map).ToArray());
    private static PositionImportRecordMap Map(PositionRecordMap m) => new(m.MemberId, m.Lod, new(m.SourceDecodedHash), m.VertexIds);
    public static RecipeDocument Recipe(PositionAuthoringSelection s, PositionImportBinding b) => new()
    {
        SchemaVersion = 12,
        RecipeId = "returned-positions",
        InputHash = new(s.InputHash),
        Operations = [new ImportPositionsOperation{
        OperationId="import-positions",Selector=new(){Kind="draw_call_ids",DrawCallIds=s.Members.SelectMany(m=>m.Lods).SelectMany(l=>l.DrawCallIds).Order(StringComparer.Ordinal).ToArray()},
        OwnershipPolicy="exclusive",RuntimeMetadataPolicy=new("preserve_unverified",1),SourceTrianglePolicy=new("preserve_source_coincidence",1),ProceduralInputPolicy=new("preserve_serialized_inputs_unverified",1),ZeroBoneBoxPolicy=new("reject",1),ZeroRenderSpherePolicy=new("reject",1),
        Limits=new(){MaximumVertexDisplacement=s.MaximumDisplacement},PositionImport=Intent(s,b),
        ExpectedMatchesByLod=s.Members.SelectMany(m=>m.Lods).GroupBy(l=>l.Lod).ToDictionary(g=>g.Key.ToString(CultureInfo.InvariantCulture),g=>g.Sum(l=>l.DrawCallIds.Count)),
        ExpectedVerticesByLod=s.Members.SelectMany(m=>m.Lods).GroupBy(l=>l.Lod).ToDictionary(g=>g.Key.ToString(CultureInfo.InvariantCulture),g=>g.Sum(l=>l.ExpectedVertices))}]
    };
    public static void ValidateRecipe(RecipeDocument recipe)
    {
        RecipeValidator.Validate(recipe); var op = (ImportPositionsOperation)recipe.Operations.Single();
        var i = op.PositionImport;
        var s = new PositionAuthoringSelection(1, recipe.InputHash.Value, i.Members, op.Limits.MaximumVertexDisplacement, i.Protection.Select(m => new PositionRecordMap(m.MemberId, m.Lod, m.SourceDecodedHash.Value, m.VertexIds)).ToArray(),
            new(i.BoundaryFramePolicy.Kind, i.BoundaryFramePolicy.Version, i.BoundaryFramePolicy.AllowedBoundary?.Select(m => new PositionRecordMap(m.MemberId, m.Lod, m.SourceDecodedHash.Value, m.VertexIds)).ToArray()),
            new("preserve_unverified", 1), new("preserve_serialized_inputs_unverified", 1), new("preserve_source_coincidence", 1), new("reject", 1), new("reject", 1));
        _ = PositionReturnValidator.ReadSelection(PositionAuthoringJson.Canonical(s));
        foreach (var h in new[] { i.Binding.BundleSha256, i.Binding.DeltaSha256, i.Binding.PolicySha256, i.Binding.SelectionSha256, i.Binding.DeltaArtifact.Sha256 }) PositionAuthoringJson.Hash(h.Value);
        var expected = (ImportPositionsOperation)Recipe(s, i.Binding).Operations[0];
        if (i.Binding.DeltaArtifact.Path != $"authoring/{i.Binding.DeltaSha256}.json" || i.Binding.DeltaArtifact.Sha256 != i.Binding.DeltaSha256
            || FactsHash(op.Selector) != FactsHash(expected.Selector) || FactsHash(op.ExpectedMatchesByLod) != FactsHash(expected.ExpectedMatchesByLod)
            || FactsHash(op.ExpectedVerticesByLod) != FactsHash(expected.ExpectedVerticesByLod))
            throw PositionAuthoringJson.Invalid("IMPORT_INTENT_DRIFT", "Exact selector/cardinality/artifact identity differs from member union.");
    }
    public static void ValidatePlan(MutationPlan p)
    {
        if (p.SchemaVersion != 8 || p.PositionImportComposition is not null || p.Operations is not [{ Kind: "import_positions", Version: 1, PositionImportTarget: not null } op]
            || op.PairedTransformTarget is not null || op.DirectionalTransformTarget is not null || op.CoordinatedTransformTarget is not null || op.EllipsoidTransformTarget is not null
            || op.ExperimentalTransformTarget is not null || op.AffineTransformTarget is not null || op.CoupledTransformTarget is not null || op.GeometryTargets.Count != 0 || op.DistanceFieldTargets.Count != 0)
            throw PositionAuthoringJson.Invalid("IMPORT_PLAN_INVALID", "Require one unmixed source-bound position import target.");
        var t = op.PositionImportTarget;
        if (t.PositionImport.Binding.DeltaArtifact.Path != $"authoring/{t.PositionImport.Binding.DeltaSha256}.json"
            || t.PositionImport.Binding.DeltaArtifact.Sha256 != t.PositionImport.Binding.DeltaSha256)
            throw PositionAuthoringJson.Invalid("IMPORT_PLAN_INVALID", "Artifact locator must match its immutable hash-derived identity.");
        var memberships = t.PositionImport.Members.SelectMany(m => m.Lods.Select(l => (m.MemberId, l.Lod, l.ExpectedVertices))).ToArray();
        if (t.InputHash != p.InputHash || t.TargetFingerprint != TargetHash(t) || p.Fingerprint != MutationPlanJson.ComputeExperimentalFingerprint(p)
            || t.StructuralProfileId != "root_returned_positions_preservation_visual" || t.StructuralProfileVersion != 1
            || t.BoundsPolicyId != "retain_expand_boxes_preserve_runtime_import" || t.BoundsPolicyVersion != 1 || t.Buffers.Count is < 1 or > 128
            || t.ContextInventoryHash != FactsHash(t.ContextBuffers) || t.SourceBlocksHash != FactsHash(t.SourceBlocks) || t.SourceCoincidenceHash != FactsHash(t.Coincidences)
            || t.Buffers.Select(b => (b.MemberId, b.Lod)).Distinct().Count() != t.Buffers.Count
            || !t.Buffers.Select(b => (b.MemberId, b.Lod, b.VertexCount)).Order().SequenceEqual(memberships.Order())
            || t.SourceTriangles.Count != t.Buffers.Count || t.SourceBlocks.Count == 0 || t.SourceBlocks.Select(b => b.Index).Distinct().Count() != t.SourceBlocks.Count
            || t.Buffers.Any(b => b.Lod < 0 || b.MeshOrdinal < 0 || b.BufferOrdinal < 0 || b.VertexCount < 1 || b.PositionChangeCount < 0 || b.PositionChangeCount > b.VertexCount
                || !ValidIds(b.FrameClosureIds, b.VertexCount) || !ValidIds(b.BoundaryChangedFrameIds, b.VertexCount)
                || !b.FrameClosureIds.SequenceEqual(b.FrameWords.Select(w => w.VertexId))
                || b.FrameWords.Any(w => !HexWord(w.SourceWord) || !HexWord(w.ExpectedWord))
                || !b.BoundaryChangedFrameIds.All(b.FrameClosureIds.Contains))
            || !op.TargetBlocks.All(b => t.SourceBlocks.Contains(b)) || op.TargetBlocks.Select(b => b.Index).Distinct().Count() != op.TargetBlocks.Count
            || !op.SelectedDrawCalls.Select(c => c.DrawCallId).Order(StringComparer.Ordinal).SequenceEqual(t.PositionImport.Members.SelectMany(m => m.Lods).SelectMany(l => l.DrawCallIds).Order(StringComparer.Ordinal)))
            throw PositionAuthoringJson.Invalid("IMPORT_PLAN_INVALID", "Plan fingerprint, profile, inventory or selected union differs.");
    }
    private static bool ValidIds(IReadOnlyList<int> ids, int count) => ids.SequenceEqual(ids.Distinct().Order()) && ids.All(i => i >= 0 && i < count);
    private static bool HexWord(string word) => word is { Length: 8 } && word.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static void ValidateEvidence(EvidenceReport e)
    {
        if (e.SchemaVersion != 13 || e.PositionImportComposition is not null || e.Operations is not [{ Kind: "import_positions", Version: 1, PositionImport: not null } op])
            throw PositionAuthoringJson.Invalid("IMPORT_EVIDENCE_INVALID", "Require one source-bound position import observation.");
        var v = op.PositionImport; var binding = v.PlanBinding;
        var p = new MutationPlan(binding.RecipeId, e.Input.ContentHash, e.PlanFingerprint, [new(op.OperationId, op.Kind, op.Version, binding.SelectedDrawCalls, binding.TargetBlocks) { PositionImportTarget = v.Target }]) { SchemaVersion = 8, Inputs = binding.Inputs };
        ValidatePlan(p);
        if (op.GeometryChanges.Count != 0 || op.CoupledTransform is not null || op.AffineTransform is not null || op.ExperimentalTransform is not null || op.EllipsoidTransform is not null || op.CoordinatedTransform is not null || op.DirectionalTransform is not null || op.PairedTransform is not null
            || FactsHash(v.Boxes.Select(b => b.Target).ToArray()) != FactsHash(v.Target.BoxTargets) || FactsHash(v.PreservedMetadata.Select(b => b.Target).ToArray()) != FactsHash(v.Target.PreservationTargets)
            || e.ProofLevel != "offline_static" || e.Status != "passed" || e.Boundaries.Any(b => b.Status == "failed")
            || !op.SelectedDrawCallIds.SequenceEqual(binding.SelectedDrawCalls.Select(c => c.DrawCallId))
            || !op.ChangedResources.SequenceEqual(binding.SelectedDrawCalls.Select(c => c.ResourcePath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
            throw PositionAuthoringJson.Invalid("IMPORT_EVIDENCE_INVALID", "Evidence inventory or proof level differs from frozen plan.");
        if (e.Output is null)
        {
            if (v.Observed is not null || v.Boxes.Any(b => b.Status != "planned" || b.ObservedWords is not null) || v.PreservedMetadata.Any(b => b.Status != "planned" || b.ObservedPayloadHash is not null || b.ObservedWords is not null)) throw PositionAuthoringJson.Invalid("IMPORT_EVIDENCE_INVALID", "Dry run cannot claim observed values.");
        }
        else if (v.Observed is not { } o || o.InputHash != e.Input.ContentHash || o.OutputHash != e.Output.ContentHash || o.PlanFingerprint != p.Fingerprint || o.TargetFingerprint != v.Target.TargetFingerprint
            || o.Binding != v.Target.PositionImport.Binding || o.StructuralStatus != "passed" || o.BoxStatus != "passed" || o.PreservationStatus != "passed"
            || o.ConsumerStatus != "untested" || o.PoseStatus != "untested" || o.RuntimeStatus != "untested" || o.Buffers.Count != v.Target.Buffers.Count
            || o.Buffers.Any(b => b.Status != "passed") || v.Boxes.Any(b => b.Status != "passed" || b.ObservedWords is null || !b.ObservedWords.SequenceEqual(b.Target.ExpectedWords))
            || v.PreservedMetadata.Any(b => b.Status != "passed" || b.ObservedPayloadHash is null || b.Target.FieldPath == "$payload" && b.ObservedPayloadHash != b.Target.SourcePayloadHash || b.ObservedWords is null || !b.ObservedWords.SequenceEqual(b.Target.OriginalWords))
            || RequiredObservedBoundaries.Any(name => e.Boundaries.Count(b => b.Name == name && b.Status == "passed") != 1)) throw PositionAuthoringJson.Invalid("IMPORT_EVIDENCE_INVALID", "Passed evidence needs complete source/output-linked independent observations.");
        if (v.Observed is { } observed)
            for (var index = 0; index < v.Target.Buffers.Count; index++)
            {
                var b = v.Target.Buffers[index]; var actual = observed.Buffers[index];
                if (actual.MemberId != b.MemberId || actual.Lod != b.Lod || actual.ObservedDecodedHash != b.ExpectedDecodedHash || actual.ObservedPositionHash != b.ExpectedPositionHash || actual.ObservedFrameHash != b.ExpectedFrameHash)
                    throw PositionAuthoringJson.Invalid("IMPORT_EVIDENCE_INVALID", "Observed buffer hashes/identity differ from prescribed words.");
                PositionAuthoringJson.Hash(actual.UnchangedAttributesHash.Value);
            }
    }
}
