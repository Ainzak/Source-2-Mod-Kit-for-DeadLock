using S2ModKit.Domain;

namespace S2ModKit.Application;

public static partial class PairedContractValidator
{
    private static readonly string[] Checks = ["paired_reopen", "paired_numerical", "paired_dispatch", "paired_geometry", "paired_frames", "paired_triangles",
        "paired_protection", "paired_coincidences", "paired_closure", "paired_box_policy", "paired_procedural_inputs", "paired_consumer_inventory", "paired_metadata_preservation", "paired_unchanged_data"];
    private static readonly string[] Risks = ["source_face_harmlessness", "procedural_simulation", "procedural_pose", "culling", "sphere_containment", "proxy_coherence", "collision_correspondence", "self_intersection", "garment_pose_fit",
        "bodygroup_activation", "animated_lod_behavior", "runtime"];
    private static readonly string[] ExtraChecks = ["input_hash", "selection", "lod_coverage", "rewrite", "external_verifier", "source2_viewer",
        "paired_source_inventory", "paired_snapshot_postconditions", "all_draw_calls_preserved", "lod_inventory_preserved",
        "resource_block_inventory_preserved", "target_block_fingerprints", "selected_vertex_blocks_changed", "non_target_blocks_byte_identical"];

    public static IEnumerable<BoundaryEvidence> PlannedBoundaries() => Checks.Select(n => new BoundaryEvidence(n, "not_applicable", "Planning does not observe output bytes."))
        .Concat(Risks.Select(n => new BoundaryEvidence(n, "untested", "Static word checks do not qualify consumer, pose or live behavior.")));

    public static void ValidateEvidence(EvidenceReport report)
    {
        if (report.SchemaVersion != 12 || report.Operations is not [{ Kind: "transform_component", Version: 10, PairedTransform: not null } op]
            || op.DirectionalTransform is not null || op.CoordinatedTransform is not null || op.EllipsoidTransform is not null || op.ExperimentalTransform is not null || op.AffineTransform is not null || op.CoupledTransform is not null
            || op.GeometryChanges is not { Count: 0 } || report.ProofLevel != "offline_static" || report.Status != "passed" || report.Warnings is not { Count: > 0 }
            || report.Input is null || !Hash(report.PlanFingerprint) || report.Boundaries is null || report.Extensions is not { Count: 0 }) throw Invalid("Complete offline paired evidence with explicit risks is required.");
        var visual = op.PairedTransform;
        var t = visual.Target;
        ValidateTarget(t);
        var binding = visual.PlanBinding ?? throw Invalid("Missing evidence-to-plan binding.");
        var plan = new MutationPlan(binding.RecipeId, t.InputHash, report.PlanFingerprint,
            [new(op.OperationId, "transform_component", 10, binding.SelectedDrawCalls, binding.TargetBlocks) { PairedTransformTarget = t }])
        { SchemaVersion = 7, Inputs = binding.Inputs };
        ValidatePlan(plan);
        if (report.Input.ContentHash != t.InputHash || report.Input.Size <= 0 || !Portable(report.Input.LogicalPath)
            || report.Dependencies is null || report.Dependencies.Any(d => d is null || !Hash(d.ContentHash) || d.Size <= 0 || !Portable(d.LogicalPath))
            || !Same(binding.Inputs, report.Dependencies.Select(d => new PlannedInput(d.LogicalPath, d.ContentHash, d.Size))
                .Append(new(report.Input.LogicalPath, report.Input.ContentHash, report.Input.Size)).OrderBy(i => i.LogicalPath, StringComparer.Ordinal).ToArray())
            || !op.SelectedDrawCallIds.SequenceEqual(t.Selector.DrawCallIds!, StringComparer.Ordinal)
            || !op.ChangedResources.SequenceEqual(t.Buffers.Select(b => b.ResourcePath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            || t.Buffers.Any(b => b.ResourcePath != report.Input.LogicalPath)
            || (report.Output is not null && (!Hash(report.Output.ContentHash) || report.Output.ContentHash == report.Input.ContentHash || report.Output.Size <= 0 || report.Output.LogicalPath != report.Input.LogicalPath)))
            throw Invalid("Evidence input, output, dependencies or selection drift.");
        var built = report.Output is not null;
        if (string.IsNullOrWhiteSpace(report.ReportId) || (built ? report.Command is not ("build" or "verify") : report.Command != "plan"))
            throw Invalid("Evidence phase and command disagree.");
        var phase = built ? "passed" : "planned";
        if (report.Blocks is null || report.Blocks.Any(b => b is null) || report.Blocks.Count != t.SourceBlocks.Count)
            throw Invalid("Evidence requires the complete source/output block inventory.");
        var targetIndices = binding.TargetBlocks.Select(b => b.Index).ToHashSet();
        for (var i = 0; i < report.Blocks.Count; i++)
        {
            var row = report.Blocks[i]; var original = t.SourceBlocks[i]; var changed = targetIndices.Contains(original.Index);
            if (row.Index != original.Index || row.Type != original.Type || row.InputHash != original.InputHash
                || row.Disposition != (built ? changed ? "changed" : "unchanged" : changed ? "planned_change" : "planned_unchanged")
                || (built ? row.OutputHash is not { } outputHash || !Hash(outputHash) || (!changed && outputHash != original.InputHash)
                    || (changed && row.Type == "MVTX" && outputHash == original.InputHash) : row.OutputHash is not null))
                throw Invalid("Observed source/output block identity or unchanged data drift.");
        }
        foreach (var name in Checks.Concat(Risks))
        {
            var rows = report.Boundaries.Where(b => b is not null && b.Name == name).ToArray();
            if (rows.Length != 1 || rows[0].Status != (Risks.Contains(name) ? "untested" : built ? "passed" : "not_applicable"))
                throw Invalid($"Required boundary '{name}' is missing or incorrectly qualified.");
        }
        if (report.Boundaries.Any(b => b is null || (!Checks.Contains(b.Name) && !Risks.Contains(b.Name) && !ExtraChecks.Contains(b.Name))
                || b.Status == "failed" || (b.Status is not ("passed" or "untested" or "not_applicable")
                && !(b.Name == "external_verifier" && b.Status == "skipped"))
            || (b.Status == "untested" && !Risks.Contains(b.Name) && b.Name != "source2_viewer"))
            || report.Boundaries.Select(b => b.Name).Distinct(StringComparer.Ordinal).Count() != report.Boundaries.Count) throw Invalid("Unexpected failed, unknown or duplicate evidence boundary.");
        if (built)
        {
            var observed = visual.Observed ?? throw Invalid("Built evidence must contain independent observations.");
            if (!Same(observed.WordAudits, t.WordAudits) || !Same(observed.Protection, t.Protection) || !Same(observed.ContextBuffers, t.ContextBuffers) || !Same(observed.Coincidences, t.Coincidences)
                || !Same(observed.BoxClosures, t.BoxClosures) || !Same(observed.Fields, t.Fields) || !Same(observed.Separation, t.Separation) || !Same(observed.Dispatch, t.Dispatch)
                || !Same(observed.SourceTriangles, t.SourceTriangles) || !Same(observed.ProceduralInputs, t.ProceduralInputs)
                || observed.Buffers is null || observed.Buffers.Count != t.Buffers.Count) throw Invalid("Independent protection/context/certificate observation drift.");
            for (var i = 0; i < t.Buffers.Count; i++)
            {
                var b = t.Buffers[i]; var o = observed.Buffers[i];
                if (o is null || o.MemberId != b.MemberId || o.Lod != b.Lod || o.MeshOrdinal != b.MeshOrdinal || o.VertexBufferOrdinal != b.VertexBufferOrdinal
                    || o.PositionHash != b.ExpectedPositionHash || o.PackedFrameHash != b.ExpectedPackedFrameHash || o.DecodedVertexBufferHash != b.ExpectedDecodedVertexBufferHash
                    || o.MaskHash != b.MaskHash || o.WeightHash != b.WeightHash || o.MaximumDisplacement != b.MaximumDisplacement) throw Invalid("Observed paired buffer drift.");
            }
        }
        else if (visual.Observed is not null) throw Invalid("Planning cannot invent observed output facts.");
        if (visual.Boxes is null || visual.PreservedMetadata is null || visual.Boxes.Count != t.BoxTargets.Count || visual.PreservedMetadata.Count != t.PreservationTargets.Count)
            throw Invalid("Incomplete observed metadata inventory.");
        for (var i = 0; i < t.BoxTargets.Count; i++)
        {
            var b = visual.Boxes[i]; var target = t.BoxTargets[i];
            if (b is null || !Same(b.Target, target) || b.Status != phase || (built ? b.ObservedWords is null || !b.ObservedWords.SequenceEqual(target.ExpectedWords) : b.ObservedWords is not null))
                throw Invalid("Prescribed box observations drifted.");
        }
        for (var i = 0; i < t.PreservationTargets.Count; i++)
        {
            var p = visual.PreservedMetadata[i]; var target = t.PreservationTargets[i];
            if (p is null || !Same(p.Target, target) || p.Status != phase || (built ? p.ObservedPayloadHash is null || p.ObservedWords is null
                || !p.ObservedWords.SequenceEqual(target.OriginalWords) || (target.FieldPath == "$payload" && p.ObservedPayloadHash != target.SourcePayloadHash)
                : p.ObservedPayloadHash is not null || p.ObservedWords is not null)) throw Invalid("Preserved metadata observations drifted.");
        }
    }
}
