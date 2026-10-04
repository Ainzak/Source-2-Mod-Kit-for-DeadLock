using System.Buffers.Binary;
using System.Globalization;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public static class CoordinatedContractValidator
{
    private static readonly string[] Checks = ["coordinated_reopen", "coordinated_geometry", "coordinated_frames", "coordinated_triangles",
        "coordinated_closure", "coordinated_seams", "coordinated_box_policy", "coordinated_metadata_preservation", "coordinated_unchanged_data"];
    private static readonly string[] Risks = ["sphere_containment", "zero_box_containment", "zero_box_consumer", "zero_render_sphere_consumer",
        "proxy_coherence", "collision_correspondence", "garment_pose_fit", "runtime"];

    public static void ValidateRecipe(RecipeDocument recipe)
    {
        RecipeValidator.Validate(recipe);
        if (recipe.SchemaVersion != 9 || !Hash(recipe.InputHash) || recipe.Operations is not [TransformComponentOperation { Version: 8 } operation])
            throw Invalid("Exactly one schema-9 coordinated operation is required.");
        _ = new CoordinatedFieldMath(operation.CoordinatedTransform!.Field, operation.Limits.MaximumVertexDisplacement);
    }

    public static TransformComponentOperation Operation(PlannedOperation operation)
    {
        var t = operation.CoordinatedTransformTarget ?? throw Invalid("Missing coordinated target.");
        return new()
        {
            OperationId = operation.OperationId,
            Version = 8,
            Granularity = "coordinated_buffer_vertices",
            Transform = null!,
            Selector = t.Selector,
            CoordinatedTransform = t.CoordinatedTransform,
            RuntimeMetadataPolicy = t.RuntimeMetadataPolicy,
            ZeroBoneBoxPolicy = t.ZeroBoneBoxPolicy,
            ZeroRenderSpherePolicy = t.ZeroRenderSpherePolicy,
            Limits = new() { MaximumVertexDisplacement = t.DisplacementLimit },
            ExpectedMatchesByLod = t.CoordinatedTransform.Members.SelectMany(m => m.Lods).GroupBy(l => l.Lod)
                .ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Sum(l => l.DrawCallIds.Count)),
            ExpectedVerticesByLod = t.Buffers.GroupBy(b => b.Lod)
                .ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => checked((int)g.Sum(b => (long)b.VertexCount))),
        };
    }

    public static void ValidatePlan(MutationPlan plan)
    {
        if (plan.SchemaVersion != 5 || plan.Operations is not [{ Kind: "transform_component", Version: 8, CoordinatedTransformTarget: not null } operation]
            || operation.ExperimentalTransformTarget is not null || operation.EllipsoidTransformTarget is not null
            || operation.AffineTransformTarget is not null || operation.CoupledTransformTarget is not null
            || operation.GeometryTargets is not { Count: 0 } || operation.DistanceFieldTargets is not { Count: 0 }
            || operation.SelectedDrawCalls is not { Count: > 0 } || operation.SelectedDrawCalls.Any(c => c is null || c.Lod < 0 || c.MeshOrdinal < 0
                || c.ResourceBlockIndex < 0 || c.DrawCallOrdinal < 0 || c.IndexStart < 0 || c.IndexCount <= 0 || c.IndexCount % 3 != 0 || !Portable(c.ResourcePath) || !Portable(c.MaterialPath))
            || operation.SelectedDrawCalls.Select(c => c.DrawCallId).Distinct(StringComparer.Ordinal).Count() != operation.SelectedDrawCalls.Count
            || operation.TargetBlocks is not { Count: > 0 } || operation.TargetBlocks.Any(b => b is null)
            || plan.Inputs is not { Count: > 0 } || plan.Inputs.Any(i => i is null || i.Size <= 0 || !Hash(i.ContentHash) || !Portable(i.LogicalPath))
            || !plan.Inputs.Any(i => i.ContentHash == plan.InputHash)
            || plan.Inputs.Select(i => i.LogicalPath).Distinct(StringComparer.Ordinal).Count() != plan.Inputs.Count)
            throw Invalid("Complete isolated coordinated plan facts are required.");
        var t = operation.CoordinatedTransformTarget;
        ValidateTarget(t);
        if (t.InputHash != plan.InputHash) throw Invalid("Input identity drift.");
        ValidateRecipe(new() { SchemaVersion = 9, RecipeId = plan.RecipeId, InputHash = plan.InputHash, Operations = [Operation(operation)] });
        if (!operation.SelectedDrawCalls.Select(c => c.DrawCallId).ToHashSet(StringComparer.Ordinal).SetEquals(t.Selector.DrawCallIds!))
            throw Invalid("Selection differs from the complete member union.");
        foreach (var member in t.CoordinatedTransform.Members)
            foreach (var map in member.Lods)
            {
                var b = t.Buffers.Single(buffer => buffer.MemberId == member.MemberId && buffer.Lod == map.Lod);
                var calls = operation.SelectedDrawCalls.Where(c => map.DrawCallIds.Contains(c.DrawCallId, StringComparer.Ordinal)).ToArray();
                if (calls.Length != map.DrawCallIds.Count || calls.Any(c => c.Lod != b.Lod || c.MeshOrdinal != b.MeshOrdinal
                    || c.ResourceBlockIndex != b.ResourceBlockIndex || c.ResourcePath != b.ResourcePath)) throw Invalid("Member/LOD mappings differ from selected source records.");
            }
        var blocks = t.SourceBlocks.ToDictionary(b => b.Index);
        var changed = t.Buffers.SelectMany(b => new[] { b.ResourceBlockIndex, b.VertexResourceBlockIndex }).Concat(t.BoxTargets.Select(b => b.ResourceBlockIndex)).Distinct().Order();
        if (!operation.TargetBlocks.Select(b => b.Index).SequenceEqual(changed)
            || operation.TargetBlocks.Any(b => !blocks.TryGetValue(b.Index, out var source) || source != b)) throw Invalid("The exact mutation block closure differs.");
    }

    public static void ValidateTarget(PlannedCoordinatedTransformTarget t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (!Hash(t.InputHash) || !Hash(t.TargetFingerprint) || t.StructuralProfileId != "root_coordinated_fields_visual" || t.StructuralProfileVersion != 1
            || t.BoundsPolicyId != "retain_expand_boxes_preserve_runtime_coordinated" || t.BoundsPolicyVersion != 1
            || t.RuntimeMetadataPolicy is not { Kind: "preserve_unverified", Version: 1 }
            || t.ZeroBoneBoxPolicy is not { Kind: "reject" or "preserve_all_zero_single_contributor_unverified", Version: 1 }
            || t.ZeroRenderSpherePolicy is not { Kind: "reject" or "preserve_zero_render_sphere_with_zero_box_unverified", Version: 1 }
            || (t.ZeroRenderSpherePolicy.Kind != "reject" && t.ZeroBoneBoxPolicy.Kind == "reject")
            || t.Buffers is not { Count: > 0 } || t.Buffers.Any(b => b is null) || t.BoxTargets is not { Count: > 0 } || t.BoxTargets.Any(b => b is null)
            || t.ZeroBoxTargets is null || t.ZeroBoxTargets.Any(b => b is null) || t.ZeroRenderSphereTargets is null || t.ZeroRenderSphereTargets.Any(b => b is null)
            || t.PreservationTargets is not { Count: > 0 } || t.PreservationTargets.Any(p => p is null)
            || t.SourceBlocks is not { Count: > 0 } || t.SourceBlocks.Any(b => b is null || b.Index < 0 || !Hash(b.InputHash) || string.IsNullOrWhiteSpace(b.Type))
            || !t.SourceBlocks.Select(b => b.Index).SequenceEqual(t.SourceBlocks.Select(b => b.Index).Distinct().Order())
            || t.SeamInventory is null || !Hash(t.SeamInventory.CompleteSourcePositionIdentity) || t.SeamInventory.ExcludedMovingMates != 0
            || t.SeamInventory.SelectedRecordCount != t.Buffers.Sum(b => (long)b.VertexCount)
            || t.SeamInventory.MovingCohortCount < 1 || t.SeamInventory.CoincidentSelectedRecordCount < 0
            || t.SeamInventory.CoincidentSelectedRecordCount > t.SeamInventory.SelectedRecordCount)
            throw Invalid("Malformed coordinated policies, closure or source inventory.");
        RecipeValidator.ValidateCoordinatedIntent(t.CoordinatedTransform, t.DisplacementLimit);
        RecipeValidator.ValidateSelector(t.Selector);
        var ids = t.CoordinatedTransform.Members.SelectMany(m => m.Lods).SelectMany(l => l.DrawCallIds).Order(StringComparer.Ordinal);
        if (t.Selector is not { Kind: "draw_call_ids", DrawCallIds: not null, MaterialPath: null }
            || !t.Selector.DrawCallIds.SequenceEqual(ids, StringComparer.Ordinal)) throw Invalid("Union selector drift.");
        if (!Same(t.FieldProof, new CoordinatedFieldMath(t.CoordinatedTransform.Field, t.DisplacementLimit).Proof)) throw Invalid("The common field certificate is stale or noncanonical.");
        if (!t.Buffers.SequenceEqual(t.Buffers.OrderBy(b => b.Lod).ThenBy(b => b.MeshOrdinal).ThenBy(b => b.VertexBufferOrdinal))
            || t.Buffers.Select(b => (b.MemberId, b.Lod)).Distinct().Count() != t.Buffers.Count
            || t.Buffers.Select(b => (b.MeshOrdinal, b.VertexBufferOrdinal)).Distinct().Count() != t.Buffers.Count
            || t.Buffers.Select(b => b.VertexResourceBlockIndex).Distinct().Count() != t.Buffers.Count
            || t.Buffers.Select(b => b.Codec).Distinct().Count() != 1) throw Invalid("Buffer mappings overlap or are noncanonical.");
        var sources = t.SourceBlocks.ToDictionary(b => b.Index);
        foreach (var b in t.Buffers)
        {
            var maps = t.CoordinatedTransform.Members.Where(m => m.MemberId == b.MemberId).SelectMany(m => m.Lods).Where(l => l.Lod == b.Lod).ToArray();
            if (maps.Length != 1 || maps[0].ExpectedVertices != b.VertexCount) throw Invalid("Buffer/member expectation drift.");
            ValidateBuffer(b, t.DisplacementLimit);
            if (!sources.TryGetValue(b.ResourceBlockIndex, out var mesh) || mesh.Type != "MDAT"
                || !sources.TryGetValue(b.VertexResourceBlockIndex, out var vertex) || vertex.Type != "MVTX" || vertex.InputHash != b.VertexBlockInputHash
                || !sources.TryGetValue(b.IndexResourceBlockIndex, out var index) || index.Type != "MIDX" || index.InputHash != b.IndexBlockInputHash)
                throw Invalid("Buffer/source block identities disagree.");
        }
        if (t.CoordinatedTransform.Members.Sum(m => m.Lods.Count) != t.Buffers.Count
            || !float.IsFinite(t.MaximumDisplacement) || t.MaximumDisplacement != t.Buffers.Max(b => b.MaximumDisplacement)) throw Invalid("Member coverage or displacement drift.");
        UniqueOrdered(t.BoxTargets.Select(b => (b.ResourceBlockIndex, b.FieldPath)));
        UniqueOrdered(t.ZeroBoxTargets.Select(b => (b.ResourceBlockIndex, b.FieldPath)));
        UniqueOrdered(t.ZeroRenderSphereTargets.Select(b => (b.ResourceBlockIndex, b.FieldPath)));
        UniqueOrdered(t.PreservationTargets.Select(b => (b.ResourceBlockIndex, b.FieldPath)));
        var boxIds = t.BoxTargets.Select(b => (b.ResourceBlockIndex, b.FieldPath)).ToHashSet();
        var preservedIds = t.PreservationTargets.Select(b => (b.ResourceBlockIndex, b.FieldPath)).ToHashSet();
        MutationPlanJson.ValidateMetadataFacts(t.BoxTargets, t.PreservationTargets, sources);
        if (t.BoxTargets.Any(b => b.Growth is not { Count: > 0 } || b.Growth.Select(g => g.Measure).Distinct(StringComparer.Ordinal).Count() != b.Growth.Count))
            throw Invalid("Every positive box needs complete growth measurements.");
        foreach (var z in t.ZeroBoxTargets)
        {
            if (t.ZeroBoneBoxPolicy.Kind == "reject" || z.Policy != t.ZeroBoneBoxPolicy || z.Disposition != "preserve_unverified"
                || z.Storage != "center_half_extent_f32" || z.CoordinateSpace != "render_inverse_bind" || z.Lod < 0 || z.MeshOrdinal < 0 || z.BoneIndex < 0
                || z.ContributorCount != 1 || z.ContributorMeshVertexIndex < 0 || !Hash(z.ContributorSetHash) || string.IsNullOrWhiteSpace(z.FieldPath)
                || z.OriginalWords is not { Count: 6 } || z.OriginalWords.Any(w => w != 0) || z.CoordinateMatrixWords is not { Count: 12 }
                || z.CoordinateMatrixWords.Any(w => !float.IsFinite(BitConverter.UInt32BitsToSingle(w))) || z.CoordinateMatrixHash != WordHash(z.CoordinateMatrixWords)
                || !sources.TryGetValue(z.ResourceBlockIndex, out var source) || source.Type != "MDAT" || source.InputHash != z.SourcePayloadHash
                || !t.Buffers.Any(b => b.Lod == z.Lod && b.MeshOrdinal == z.MeshOrdinal && b.ResourceBlockIndex == z.ResourceBlockIndex)
                || boxIds.Contains((z.ResourceBlockIndex, z.FieldPath))) throw Invalid("Invalid zero-box preservation family or contributor identity.");
        }
        foreach (var z in t.ZeroRenderSphereTargets)
        {
            var paired = t.ZeroBoxTargets.Where(b => b.ResourceBlockIndex == z.ResourceBlockIndex && b.FieldPath == z.PairedZeroBoxFieldPath).ToArray();
            if (t.ZeroRenderSpherePolicy.Kind == "reject" || z.Policy != t.ZeroRenderSpherePolicy || z.Disposition != "preserve_unverified"
                || z.Storage is not ("binary32" or "binary64") || z.OriginalWord != 0 || paired.Length != 1
                || z.FieldPath != $"m_skeleton.m_bones[{paired[0].BoneIndex}].m_flSphereRadius"
                || paired[0].ContributorSetHash != z.ContributorSetHash || paired[0].SourcePayloadHash != z.SourcePayloadHash
                || preservedIds.Contains((z.ResourceBlockIndex, z.FieldPath))) throw Invalid("Invalid paired zero-sphere field, storage or contributor identity.");
        }
        if (t.TargetFingerprint != MutationPlanJson.ComputeCoordinatedTargetFingerprint(t)) throw Invalid("Target fingerprint drift.");
    }

    private static void ValidateBuffer(PlannedCoordinatedBuffer b, float limit)
    {
        if (b.Lod < 0 || b.MeshOrdinal < 0 || b.ResourceBlockIndex < 0 || b.VertexBufferOrdinal < 0 || b.IndexBufferOrdinal < 0
            || b.VertexResourceBlockIndex < 0 || b.IndexResourceBlockIndex < 0 || b.VertexCount <= 0 || b.OwnershipPolicy != "exclusive" || !Portable(b.ResourcePath)
            || b.FullVertexCount < 0 || b.TransitionVertexCount < 0 || b.PinnedVertexCount < 0 || (long)b.FullVertexCount + b.TransitionVertexCount + b.PinnedVertexCount != b.VertexCount
            || b.ChangedPositionCount <= 0 || b.ChangedPositionCount > b.VertexCount - b.PinnedVertexCount || b.ChangedFrameCount < 0 || b.ChangedFrameCount > b.TransitionVertexCount
            || !float.IsFinite(b.MaximumDisplacement) || b.MaximumDisplacement <= 0 || b.MaximumDisplacement > limit || !Bounds(b.BeforeBounds) || !Bounds(b.ExpectedAfterBounds)
            || b.PositionLayout is not { Format: "R32G32B32_FLOAT" } p || p.Offset < 0 || (long)p.Offset + 12 > p.Stride
            || b.PackedFrameLayout is not { Format: "R32_UINT", EncodingProfile: "source2_normal_tangent_v2" } f || f.Offset < 0 || (long)f.Offset + 4 > f.Stride
            || f.Stride != p.Stride || (f.Offset < (long)p.Offset + 12 && p.Offset < (long)f.Offset + 4)
            || b.Codec is null || string.IsNullOrWhiteSpace(b.Codec.Name) || string.IsNullOrWhiteSpace(b.Codec.ApiProfile) || string.IsNullOrWhiteSpace(b.Codec.Platform)
            || string.IsNullOrWhiteSpace(b.Codec.Version) || !Hash(b.Codec.BinaryHash)
            || !new[] { b.VertexBlockInputHash, b.IndexBlockInputHash, b.InputDecodedVertexBufferHash, b.ExpectedDecodedVertexBufferHash, b.DecodedIndexBufferHash,
                b.VertexSetHash, b.MaskHash, b.WeightHash, b.InputPositionHash, b.ExpectedPositionHash, b.InputPackedFrameHash, b.ExpectedPackedFrameHash }.All(Hash)
            || (b.ChangedFrameCount == 0) != (b.InputPackedFrameHash == b.ExpectedPackedFrameHash)
            || b.InputPositionHash == b.ExpectedPositionHash || b.InputDecodedVertexBufferHash == b.ExpectedDecodedVertexBufferHash)
            throw Invalid("Malformed complete buffer, layouts, codec, masks, hashes or counts.");
    }

    public static void ValidateEvidence(EvidenceReport report)
    {
        if (report.SchemaVersion != 10 || report.Operations is not [{ Kind: "transform_component", Version: 8, CoordinatedTransform: not null } op]
            || op.ExperimentalTransform is not null || op.EllipsoidTransform is not null || op.AffineTransform is not null || op.CoupledTransform is not null
            || op.GeometryChanges is not { Count: 0 } || report.ProofLevel != "offline_static" || report.Status != "passed" || report.Warnings is not { Count: > 0 }
            || report.Input is null || !Hash(report.PlanFingerprint) || report.Boundaries is null) throw Invalid("Complete offline coordinated evidence with explicit consumer risks is required.");
        var visual = op.CoordinatedTransform;
        var t = visual.Target;
        ValidateTarget(t);
        if (report.Input.ContentHash != t.InputHash || !op.SelectedDrawCallIds.SequenceEqual(t.Selector.DrawCallIds!.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || !op.ChangedResources.Order(StringComparer.Ordinal).SequenceEqual(t.Buffers.Select(b => b.ResourcePath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            || t.Buffers.Any(b => b.ResourcePath != report.Input.LogicalPath)
            || (report.Output is not null && (!Hash(report.Output.ContentHash) || report.Output.ContentHash == report.Input.ContentHash || report.Output.Size <= 0 || report.Output.LogicalPath != report.Input.LogicalPath)))
            throw Invalid("Evidence input, selection or output identity drift.");
        foreach (var name in Checks.Concat(Risks))
        {
            var matches = report.Boundaries.Where(b => b.Name == name).ToArray();
            if (matches.Length != 1 || matches[0].Status != (Risks.Contains(name) ? "untested" : report.Output is null ? "not_applicable" : "passed"))
                throw Invalid($"Boundary '{name}' is missing or incorrectly qualified.");
        }
        if (report.Boundaries.Any(b => b.Status == "failed" || (b.Status == "untested" && !Risks.Contains(b.Name) && b.Name != "source2_viewer"))) throw Invalid("Unexpected failed or untested check.");
        var built = report.Output is not null;
        var phase = built ? "passed" : "planned";
        if (built ? visual.ObservedBuffers?.Count != t.Buffers.Count : visual.ObservedBuffers is not null) throw Invalid("Observed buffer inventory drift.");
        if (built)
            for (var i = 0; i < t.Buffers.Count; i++)
            {
                var b = t.Buffers[i]; var o = visual.ObservedBuffers![i];
                if (o is null || o.MemberId != b.MemberId || o.Lod != b.Lod || o.MeshOrdinal != b.MeshOrdinal || o.VertexBufferOrdinal != b.VertexBufferOrdinal
                    || o.PositionHash != b.ExpectedPositionHash || o.PackedFrameHash != b.ExpectedPackedFrameHash || o.DecodedVertexBufferHash != b.ExpectedDecodedVertexBufferHash
                    || o.MaskHash != b.MaskHash || o.WeightHash != b.WeightHash || o.MaximumDisplacement != b.MaximumDisplacement) throw Invalid("Observed buffer facts drifted.");
            }
        if (visual.Boxes is null || visual.ZeroBoxes is null || visual.ZeroRenderSpheres is null || visual.PreservedMetadata is null
            || visual.Boxes.Count != t.BoxTargets.Count || visual.ZeroBoxes.Count != t.ZeroBoxTargets.Count
            || visual.ZeroRenderSpheres.Count != t.ZeroRenderSphereTargets.Count || visual.PreservedMetadata.Count != t.PreservationTargets.Count) throw Invalid("Observed field inventories are incomplete.");
        for (var i = 0; i < t.BoxTargets.Count; i++)
        {
            var b = visual.Boxes[i]; var expected = t.BoxTargets[i];
            if (b is null || !Same(b.Target, expected) || b.Status != phase || (built ? b.ObservedWords is null || !b.ObservedWords.SequenceEqual(expected.ExpectedWords) : b.ObservedWords is not null)) throw Invalid("Box observations drifted.");
        }
        for (var i = 0; i < t.ZeroBoxTargets.Count; i++)
        {
            var z = visual.ZeroBoxes[i]; var expected = t.ZeroBoxTargets[i];
            if (z is null || !Same(z.Target, expected) || z.PreservationStatus != phase || z.ContainmentStatus != "untested" || z.ConsumerStatus != "untested"
                || (built ? z.ObservedWords is null || !z.ObservedWords.SequenceEqual(expected.OriginalWords) : z.ObservedWords is not null)) throw Invalid("Zero-box observations or risk labels drifted.");
        }
        for (var i = 0; i < t.ZeroRenderSphereTargets.Count; i++)
        {
            var z = visual.ZeroRenderSpheres[i]; var expected = t.ZeroRenderSphereTargets[i];
            if (z is null || !Same(z.Target, expected) || z.PreservationStatus != phase || z.ContainmentStatus != "untested" || z.ConsumerStatus != "untested"
                || (built ? z.ObservedStorage != expected.Storage || z.ObservedWord != expected.OriginalWord : z.ObservedStorage is not null || z.ObservedWord is not null)) throw Invalid("Paired zero-sphere observations drifted.");
        }
        for (var i = 0; i < t.PreservationTargets.Count; i++)
        {
            var p = visual.PreservedMetadata[i]; var expected = t.PreservationTargets[i];
            if (p is null || !Same(p.Target, expected) || p.Status != phase || (built ? p.ObservedPayloadHash is null || p.ObservedWords is null || !p.ObservedWords.SequenceEqual(expected.OriginalWords)
                || (expected.FieldPath == "$payload" && p.ObservedPayloadHash != expected.SourcePayloadHash) : p.ObservedPayloadHash is not null || p.ObservedWords is not null)) throw Invalid("Preservation observations drifted.");
        }
    }

    public static IEnumerable<BoundaryEvidence> PlannedBoundaries() => Checks.Select(n => new BoundaryEvidence(n, "not_applicable", "Planning does not observe output bytes."))
        .Concat(Risks.Select(n => new BoundaryEvidence(n, "untested", "Preservation does not qualify containment, consumer behavior or fit.")));
    private static bool Hash(ContentHash h) => h.Value is { Length: 64 };
    private static bool Portable(string? p) => !string.IsNullOrWhiteSpace(p) && !p.StartsWith('/') && !p.Contains(':') && !p.Any(char.IsControl)
        && p == StableIdentity.NormalizePath(p) && !p.Split('/').Any(s => s is "" or "." or "..");
    private static bool Bounds(GeometryBounds? b) => b?.Min is not null && b.Max is not null
        && new[] { b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z }.All(float.IsFinite) && b.Min.X < b.Max.X && b.Min.Y < b.Max.Y && b.Min.Z < b.Max.Z;
    private static void UniqueOrdered(IEnumerable<(int Block, string Path)> values)
    {
        var array = values.ToArray();
        if (array.Distinct().Count() != array.Length || !array.SequenceEqual(array.OrderBy(v => v.Block).ThenBy(v => v.Path, StringComparer.Ordinal))) throw Invalid("Metadata identities must be unique and canonically sorted.");
    }
    private static ContentHash WordHash(IReadOnlyList<uint> words)
    {
        var bytes = new byte[checked(words.Count * 4)];
        for (var i = 0; i < words.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        return ContentHash.Compute(bytes);
    }
    private static bool Same<T>(T a, T b) => JsonDefaults.Serialize(a) == JsonDefaults.Serialize(b);
    private static S2ModKitException Invalid(string message) => CoordinatedContractJson.Invalid(message);
}
