using S2ModKit.Domain;

namespace S2ModKit.Application;

public static partial class DirectionalContractValidator
{
    private static readonly string[] Checks = ["directional_reopen", "directional_numerical", "directional_geometry", "directional_frames", "directional_triangles",
        "directional_protection", "directional_coincidences", "directional_closure", "directional_box_policy", "directional_metadata_preservation", "directional_unchanged_data"];
    private static readonly string[] Risks = ["sphere_containment", "proxy_coherence", "collision_correspondence", "self_intersection", "garment_pose_fit",
        "bodygroup_activation", "animated_lod_behavior", "runtime"];

    public static IEnumerable<BoundaryEvidence> PlannedBoundaries() => Checks.Select(n => new BoundaryEvidence(n, "not_applicable", "Planning does not observe output bytes."))
        .Concat(Risks.Select(n => new BoundaryEvidence(n, "untested", "Static word checks do not qualify consumer, pose or live behavior.")));

    public static void ValidateEvidence(EvidenceReport report)
    {
        if (report.SchemaVersion != 11 || report.Operations is not [{ Kind: "transform_component", Version: 9, DirectionalTransform: not null } op]
            || op.CoordinatedTransform is not null || op.EllipsoidTransform is not null || op.ExperimentalTransform is not null || op.AffineTransform is not null || op.CoupledTransform is not null
            || op.GeometryChanges is not { Count: 0 } || report.ProofLevel != "offline_static" || report.Status != "passed" || report.Warnings is not { Count: > 0 }
            || report.Input is null || !Hash(report.PlanFingerprint) || report.Boundaries is null || report.Extensions is not { Count: 0 }) throw Invalid("Complete offline directional evidence with explicit risks is required.");
        var visual = op.DirectionalTransform;
        var t = visual.Target;
        ValidateTarget(t);
        var binding = visual.PlanBinding ?? throw Invalid("Missing evidence-to-plan binding.");
        var plan = new MutationPlan(binding.RecipeId, t.InputHash, report.PlanFingerprint,
            [new(op.OperationId, "transform_component", 9, binding.SelectedDrawCalls, binding.TargetBlocks) { DirectionalTransformTarget = t }])
        { SchemaVersion = 6, Inputs = binding.Inputs };
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
        foreach (var name in Checks.Concat(Risks))
        {
            var rows = report.Boundaries.Where(b => b is not null && b.Name == name).ToArray();
            if (rows.Length != 1 || rows[0].Status != (Risks.Contains(name) ? "untested" : built ? "passed" : "not_applicable"))
                throw Invalid($"Required boundary '{name}' is missing or incorrectly qualified.");
        }
        if (report.Boundaries.Any(b => b is null || b.Status == "failed" || (b.Status is not ("passed" or "untested" or "not_applicable")
                && !(b.Name == "external_verifier" && b.Status == "skipped"))
            || (b.Status == "untested" && !Risks.Contains(b.Name) && b.Name != "source2_viewer"))
            || report.Boundaries.Select(b => b.Name).Distinct(StringComparer.Ordinal).Count() != report.Boundaries.Count) throw Invalid("Unexpected failed, unknown or duplicate evidence boundary.");
        if (built)
        {
            var observed = visual.Observed ?? throw Invalid("Built evidence must contain independent observations.");
            if (!Same(observed.WordAudits, t.WordAudits) || !Same(observed.Protection, t.Protection) || !Same(observed.ContextBuffers, t.ContextBuffers) || !Same(observed.Coincidences, t.Coincidences)
                || !Same(observed.BoxClosures, t.BoxClosures) || !Same(observed.Certificate, t.Certificate)
                || observed.Buffers is null || observed.Buffers.Count != t.Buffers.Count) throw Invalid("Independent protection/context/certificate observation drift.");
            for (var i = 0; i < t.Buffers.Count; i++)
            {
                var b = t.Buffers[i]; var o = observed.Buffers[i];
                if (o is null || o.MemberId != b.MemberId || o.Lod != b.Lod || o.MeshOrdinal != b.MeshOrdinal || o.VertexBufferOrdinal != b.VertexBufferOrdinal
                    || o.PositionHash != b.ExpectedPositionHash || o.PackedFrameHash != b.ExpectedPackedFrameHash || o.DecodedVertexBufferHash != b.ExpectedDecodedVertexBufferHash
                    || o.MaskHash != b.MaskHash || o.WeightHash != b.WeightHash || o.MaximumDisplacement != b.MaximumDisplacement) throw Invalid("Observed directional buffer drift.");
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
