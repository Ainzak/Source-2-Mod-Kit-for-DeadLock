using System.Globalization;
using System.Numerics;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Application;

public static class EllipsoidContractValidator
{
    private static readonly string[] Checks = ["ellipsoid_reopen", "ellipsoid_geometry", "ellipsoid_frames", "ellipsoid_triangles", "ellipsoid_box_policy", "ellipsoid_metadata_preservation", "ellipsoid_unchanged_data"];
    private static readonly string[] Risks = ["sphere_containment", "proxy_coherence", "collision_correspondence", "runtime"];

    public static void ValidateRecipe(RecipeDocument recipe)
    {
        RecipeValidator.Validate(recipe);
        if (recipe.SchemaVersion != 8 || !Hash(recipe.InputHash) || recipe.Operations is not [TransformComponentOperation { Version: 7 } operation]) throw Invalid("Recipe version or input identity mismatch.");
        _ = Field(operation.LocalTransform!, operation.Limits.MaximumVertexDisplacement);
    }

    public static void ValidatePlan(MutationPlan plan)
    {
        if (plan.SchemaVersion != 4 || plan.Operations is not [{ Kind: "transform_component", Version: 7, EllipsoidTransformTarget: not null } operation]
            || operation.ExperimentalTransformTarget is not null || operation.AffineTransformTarget is not null || operation.CoupledTransformTarget is not null
            || operation.GeometryTargets is not { Count: 0 } || operation.DistanceFieldTargets is not { Count: 0 }
            || operation.SelectedDrawCalls is not { Count: > 0 } || operation.SelectedDrawCalls.Any(c => c is null || c.Lod < 0 || c.MeshOrdinal < 0 || c.ResourceBlockIndex < 0
                || c.DrawCallOrdinal < 0 || c.IndexStart < 0 || c.IndexCount <= 0 || c.IndexCount % 3 != 0 || !DrawCallId(c.DrawCallId) || !Portable(c.MaterialPath))
            || operation.TargetBlocks is not { Count: > 0 } || operation.TargetBlocks.Any(b => b is null)
            || plan.Inputs is not { Count: > 0 } || plan.Inputs.Any(i => i is null)
            || plan.Inputs.Select(i => i.LogicalPath).Distinct(StringComparer.Ordinal).Count() != plan.Inputs.Count) throw Invalid("Complete isolated version-7 plan facts are required.");
        var target = operation.EllipsoidTransformTarget;
        ValidateTarget(target);
        if (target.InputHash != plan.InputHash) throw Invalid("Input identity drift.");
        RecipeValidator.Validate(new RecipeDocument
        {
            SchemaVersion = 8,
            RecipeId = plan.RecipeId,
            InputHash = plan.InputHash,
            Operations = [new TransformComponentOperation
            {
                OperationId = operation.OperationId, Version = 7, Granularity = "ellipsoid_vertices", Selector = target.Selector,
                Transform = null!, LocalTransform = target.LocalTransform, RuntimeMetadataPolicy = target.RuntimeMetadataPolicy,
                Limits = new() { MaximumVertexDisplacement = target.DisplacementLimit },
                ExpectedMatchesByLod = operation.SelectedDrawCalls.GroupBy(c => c.Lod).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Count()),
                ExpectedVerticesByLod = target.Buffers.ToDictionary(b => b.Lod.ToString(CultureInfo.InvariantCulture), b => b.VertexCount),
            }],
        });
        foreach (var buffer in target.Buffers)
        {
            var calls = operation.SelectedDrawCalls.Where(c => c.Lod == buffer.Lod).ToArray();
            if (calls.Any(c => c.MeshOrdinal != buffer.MeshOrdinal || c.ResourceBlockIndex != buffer.ResourceBlockIndex || c.ResourcePath != buffer.ResourcePath))
                throw Invalid("The selected draw calls disagree with the enclosing buffer.");
        }
        var changedBlocks = target.Buffers.SelectMany(b => new[] { b.ResourceBlockIndex, b.VertexResourceBlockIndex })
            .Concat(target.BoxTargets.Select(b => b.ResourceBlockIndex)).Distinct().Order();
        if (!operation.TargetBlocks.Select(b => b.Index).Order().SequenceEqual(changedBlocks)) throw Invalid("The mutation block inventory must exactly cover positions, frames and declared boxes.");
        MutationPlanJson.ValidateStorageFacts(plan, operation, target.Buffers.Select(b => StorageGeometry(b, target.LocalTransform)).ToArray(),
            target.BoxTargets, target.PreservationTargets, target.SourceBlocks);
    }

    public static void ValidateTarget(PlannedEllipsoidTransformTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!Hash(target.InputHash) || !Hash(target.TargetFingerprint)
            || target.StructuralProfileId != "root_complete_buffer_ellipsoid_visual" || target.StructuralProfileVersion != 1
            || target.BoundsPolicyId != "retain_expand_boxes_preserve_runtime" || target.BoundsPolicyVersion != 1
            || target.RuntimeMetadataPolicy is not { Kind: "preserve_unverified", Version: 1 } || target.Selector is null
            || target.Buffers is not { Count: > 0 } || target.Buffers.Any(b => b is null)
            || target.BoxTargets is not { Count: > 0 } || target.BoxTargets.Any(b => b is null)
            || target.PreservationTargets is not { Count: > 0 } || target.PreservationTargets.Any(b => b is null)
            || target.SourceBlocks is not { Count: > 0 } || target.SourceBlocks.Any(b => b is null)
            || !float.IsFinite(target.MaximumDisplacement) || target.MaximumDisplacement <= 0 || target.MaximumDisplacement > target.DisplacementLimit
            || target.Buffers.Select(b => b.Lod).Distinct().Count() != target.Buffers.Count
            || !target.Buffers.Select(b => b.Lod).SequenceEqual(target.Buffers.Select(b => b.Lod).Order())) throw Invalid("Malformed ellipsoid profile, policies or target inventory.");
        var field = Field(target.LocalTransform, target.DisplacementLimit);
        RecipeValidator.ValidateSelector(target.Selector);
        var expected = Certificate(field.Certificate);
        if (target.Certificate is null || !CanonicalInteger(target.Certificate.Numerator, out var numerator) || !CanonicalInteger(target.Certificate.Denominator, out var denominator)
            || numerator <= 0 || denominator <= 0 || BigInteger.GreatestCommonDivisor(numerator, denominator) != BigInteger.One
            || target.Certificate != expected) throw Invalid("The exact certificate is noncanonical, unreduced or stale.");
        foreach (var b in target.Buffers)
        {
            if (b.Lod < 0 || b.MeshOrdinal < 0 || b.ResourceBlockIndex < 0 || b.VertexBufferOrdinal < 0 || b.IndexBufferOrdinal < 0
                || b.VertexResourceBlockIndex < 0 || b.IndexResourceBlockIndex < 0 || b.VertexCount <= 0 || b.OwnershipPolicy != "exclusive"
                || !Portable(b.ResourcePath)
                || b.CoreVertexCount < 0 || b.TransitionVertexCount < 0 || b.PinnedVertexCount < 0
                || (long)b.CoreVertexCount + b.TransitionVertexCount + b.PinnedVertexCount != b.VertexCount
                || b.ChangedPositionCount <= 0 || b.ChangedPositionCount > b.VertexCount - b.PinnedVertexCount
                || b.ChangedFrameCount < 0 || b.ChangedFrameCount > b.TransitionVertexCount
                || !float.IsFinite(b.MaximumDisplacement) || b.MaximumDisplacement <= 0 || b.MaximumDisplacement > target.DisplacementLimit
                || !Bounds(b.BeforeBounds) || !Bounds(b.ExpectedAfterBounds)
                || b.PositionLayout is not { Format: "R32G32B32_FLOAT" } position || position.Offset < 0 || (long)position.Offset + 12 > position.Stride
                || b.PackedFrameLayout is not { Format: "R32_UINT", EncodingProfile: "source2_normal_tangent_v2" } frame
                || frame.Stride != position.Stride || frame.Offset < 0 || (long)frame.Offset + 4 > frame.Stride
                || (frame.Offset < (long)position.Offset + 12 && position.Offset < (long)frame.Offset + 4)
                || b.Codec is null || string.IsNullOrWhiteSpace(b.Codec.Name) || string.IsNullOrWhiteSpace(b.Codec.ApiProfile)
                || string.IsNullOrWhiteSpace(b.Codec.Platform) || string.IsNullOrWhiteSpace(b.Codec.Version) || !Hash(b.Codec.BinaryHash)
                || !Hash(b.VertexBlockInputHash) || !Hash(b.IndexBlockInputHash) || !Hash(b.VertexSetHash) || !Hash(b.DecodedIndexBufferHash)
                || !Hash(b.InputDecodedVertexBufferHash) || !Hash(b.ExpectedDecodedVertexBufferHash) || !Hash(b.MaskHash) || !Hash(b.WeightHash)
                || !Hash(b.InputPositionHash) || !Hash(b.ExpectedPositionHash) || !Hash(b.InputPackedFrameHash) || !Hash(b.ExpectedPackedFrameHash))
                throw Invalid("Buffer ownership, masks, layouts, codec, counts, hashes or limits are invalid.");
            if ((b.ChangedFrameCount == 0) != (b.InputPackedFrameHash == b.ExpectedPackedFrameHash)
                || b.InputPositionHash == b.ExpectedPositionHash || b.InputDecodedVertexBufferHash == b.ExpectedDecodedVertexBufferHash)
                throw Invalid("Changed-word counts disagree with input and expected identities.");
        }
        if (target.MaximumDisplacement != target.Buffers.Max(b => b.MaximumDisplacement)) throw Invalid("Maximum displacement drift.");
        if (target.SourceBlocks.Any(b => b.Index < 0 || !Hash(b.InputHash) || string.IsNullOrWhiteSpace(b.Type))
            || target.SourceBlocks.Select(b => b.Index).Distinct().Count() != target.SourceBlocks.Count
            || target.BoxTargets.Select(b => (b.ResourceBlockIndex, b.FieldPath)).Distinct().Count() != target.BoxTargets.Count
            || target.PreservationTargets.Select(b => (b.ResourceBlockIndex, b.FieldPath)).Distinct().Count() != target.PreservationTargets.Count)
            throw Invalid("Duplicated or invalid source/metadata identities.");
        if (!target.SourceBlocks.Select(b => b.Index).SequenceEqual(target.SourceBlocks.Select(b => b.Index).Order())
            || !OrderedMetadata(target.BoxTargets.Select(b => (b.ResourceBlockIndex, b.FieldPath)))
            || !OrderedMetadata(target.PreservationTargets.Select(b => (b.ResourceBlockIndex, b.FieldPath)))
            || target.Buffers.Select(b => b.Codec).Distinct().Count() != 1)
            throw Invalid("Source blocks and metadata must have canonical ordering and one frozen codec identity.");
        var sources = target.SourceBlocks.ToDictionary(b => b.Index);
        foreach (var b in target.Buffers)
            if (!sources.TryGetValue(b.ResourceBlockIndex, out var mesh) || mesh.Type != "MDAT"
                || !sources.TryGetValue(b.VertexResourceBlockIndex, out var vertices) || vertices.Type != "MVTX" || vertices.InputHash != b.VertexBlockInputHash
                || !sources.TryGetValue(b.IndexResourceBlockIndex, out var indices) || indices.Type != "MIDX" || indices.InputHash != b.IndexBlockInputHash)
                throw Invalid("Buffer identities disagree with the source-block inventory.");
        if (target.BoxTargets.Any(b => b.Growth is not { Count: > 0 } || b.Growth.Any(g => g is null)
            || b.Growth.Select(g => g.Measure).Distinct(StringComparer.Ordinal).Count() != b.Growth.Count)) throw Invalid("Every box requires explicit unique growth measurements.");
        MutationPlanJson.ValidateMetadataFacts(target.BoxTargets, target.PreservationTargets, sources);
        if (target.TargetFingerprint != MutationPlanJson.ComputeEllipsoidTargetFingerprint(target)) throw Invalid("Target fingerprint drift.");
    }

    public static PlannedEllipsoidCertificate Certificate(EllipsoidJacobianCertificate c) => new(
        EllipsoidJacobianCertificate.Algorithm, EllipsoidJacobianCertificate.Version, EllipsoidJacobianCertificate.SubdivisionDepth,
        c.Numerator.ToString(CultureInfo.InvariantCulture), c.Denominator.ToString(CultureInfo.InvariantCulture), c.LowerBound, c.UpperBound, c.MinimumSingularValueLowerBound);

    public static void ValidateEvidence(EvidenceReport report)
    {
        if (report.SchemaVersion != 9 || report.Operations is not [{ Kind: "transform_component", Version: 7, EllipsoidTransform: not null } operation]
            || operation.ExperimentalTransform is not null || operation.AffineTransform is not null || operation.CoupledTransform is not null
            || operation.GeometryChanges is not { Count: 0 } || report.ProofLevel != "offline_static" || report.Status != "passed"
            || report.Input is null || !Hash(report.PlanFingerprint) || report.Warnings is not { Count: > 0 } || report.Boundaries is null)
            throw Invalid("Evidence requires one explicit passed offline ellipsoid contract with consumer warnings.");
        var visual = operation.EllipsoidTransform;
        ValidateTarget(visual.Target);
        if (report.Input.ContentHash != visual.Target.InputHash) throw Invalid("Evidence input drift.");
        if (operation.SelectedDrawCallIds is not { Count: > 0 } || operation.SelectedDrawCallIds.Any(id => !DrawCallId(id))
            || operation.SelectedDrawCallIds.Distinct(StringComparer.Ordinal).Count() != operation.SelectedDrawCallIds.Count
            || operation.ChangedResources is null || !operation.ChangedResources.Order(StringComparer.Ordinal)
                .SequenceEqual(visual.Target.Buffers.Select(b => b.ResourcePath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            || visual.Target.Buffers.Any(b => b.ResourcePath != report.Input.LogicalPath)
            || (report.Output is not null && (report.Output.ContentHash == report.Input.ContentHash || !Hash(report.Output.ContentHash)
                || report.Output.Size <= 0 || report.Output.LogicalPath != report.Input.LogicalPath))) throw Invalid("Evidence selection, resource or output identity drift.");
        foreach (var name in Checks.Concat(Risks))
        {
            var matches = report.Boundaries.Where(b => b.Name == name).ToArray();
            var status = Risks.Contains(name) ? "untested" : report.Output is null ? "not_applicable" : "passed";
            if (matches.Length != 1 || matches[0].Status != status) throw Invalid($"Boundary '{name}' is missing, duplicated or incorrectly qualified.");
        }
        if (report.Boundaries.Any(b => b.Status == "failed" || (b.Status == "untested" && !Risks.Contains(b.Name) && b.Name != "source2_viewer"))) throw Invalid("Unexpected failed or untested check.");
        if (report.Output is null)
        {
            if (visual.ObservedBuffers is not null) throw Invalid("A dry run cannot claim buffer observations.");
        }
        else
        {
            if (visual.ObservedBuffers is null || visual.ObservedBuffers.Count != visual.Target.Buffers.Count) throw Invalid("Reopened buffer observations are incomplete.");
            for (var i = 0; i < visual.Target.Buffers.Count; i++)
            {
                var b = visual.Target.Buffers[i]; var observed = visual.ObservedBuffers[i];
                if (observed is null || observed.Lod != b.Lod || observed.MeshOrdinal != b.MeshOrdinal || observed.VertexBufferOrdinal != b.VertexBufferOrdinal
                    || observed.PositionHash != b.ExpectedPositionHash || observed.PackedFrameHash != b.ExpectedPackedFrameHash
                    || observed.DecodedVertexBufferHash != b.ExpectedDecodedVertexBufferHash || observed.MaskHash != b.MaskHash || observed.WeightHash != b.WeightHash
                    || observed.MaximumDisplacement != b.MaximumDisplacement) throw Invalid("Observed buffer facts disagree with frozen expectations.");
            }
        }
        var phase = report.Output is null ? "planned" : "passed";
        if (visual.Boxes is null || visual.PreservedMetadata is null || visual.Boxes.Count != visual.Target.BoxTargets.Count || visual.PreservedMetadata.Count != visual.Target.PreservationTargets.Count)
            throw Invalid("Measured boxes or preservation inventory is incomplete.");
        for (var i = 0; i < visual.Boxes.Count; i++)
        {
            var b = visual.Boxes[i]; var expected = visual.Target.BoxTargets[i];
            if (b is null || !Same(b.Target, expected) || b.Status != phase || (report.Output is null ? b.ObservedWords is not null : b.ObservedWords is null || !b.ObservedWords.SequenceEqual(expected.ExpectedWords))) throw Invalid("Box observations disagree with the target.");
        }
        for (var i = 0; i < visual.PreservedMetadata.Count; i++)
        {
            var p = visual.PreservedMetadata[i]; var expected = visual.Target.PreservationTargets[i];
            if (p is null || !Same(p.Target, expected) || p.Status != phase || (report.Output is null ? p.ObservedWords is not null || p.ObservedPayloadHash is not null
                : p.ObservedWords is null || !p.ObservedWords.SequenceEqual(expected.OriginalWords) || p.ObservedPayloadHash is null || (expected.FieldPath == "$payload" && p.ObservedPayloadHash != expected.SourcePayloadHash))) throw Invalid("Preservation observations disagree with the target.");
        }
    }

    public static IEnumerable<BoundaryEvidence> PlannedBoundaries() => Checks.Select(n => new BoundaryEvidence(n, "not_applicable", "Planned facts do not observe output bytes."))
        .Concat(Risks.Select(n => new BoundaryEvidence(n, "untested", "Preservation does not qualify the consumer.")));

    private static EllipsoidScale Field(EllipsoidVisualTransform intent, float limit)
    {
        RecipeValidator.ValidateEllipsoidIntent(intent, limit);
        try { return new(Point(intent.Field.Center), Point(intent.Field.OuterRadii), intent.Field.CoreFraction, intent.UniformScale, limit); }
        catch (ArgumentException ex) { throw Errors.Unsupported("ELLIPSOID_JACOBIAN_UNPROVEN", $"Jacobian admission rejected: {ex.Message}", "Choose an admitted field and regenerate the plan."); }
    }

    // This projection supplies storage identities to the existing common checker;
    // it is never persisted or used to evaluate a legacy uniform transform.
    private static PlannedGeometryTarget StorageGeometry(PlannedEllipsoidBuffer b, EllipsoidVisualTransform intent) => new(
        b.Lod, b.ResourcePath, b.MeshOrdinal, b.ResourceBlockIndex, b.VertexBufferOrdinal, b.IndexBufferOrdinal, b.VertexResourceBlockIndex, b.IndexResourceBlockIndex,
        b.VertexBlockInputHash, b.IndexBlockInputHash, b.InputDecodedVertexBufferHash, b.ExpectedDecodedVertexBufferHash, b.DecodedIndexBufferHash, b.VertexSetHash,
        b.VertexCount, b.PositionLayout, b.BeforeBounds, b.ExpectedAfterBounds, intent.Field.Center, intent.UniformScale, new(), b.MaximumDisplacement, ["normal_tangent", "position"], b.Codec);

    private static bool CanonicalInteger(string? value, out BigInteger integer)
    {
        integer = default;
        return value is { Length: > 0 and <= 2048 } && (value == "0" || value[0] is >= '1' and <= '9')
            && value.All(c => c is >= '0' and <= '9') && BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out integer);
    }
    private static bool Hash(ContentHash hash) => hash.Value is { Length: 64 };
    private static bool DrawCallId(string? id) => id is { Length: 27 } && id.StartsWith("dc_", StringComparison.Ordinal) && id.Skip(3).All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Portable(string? path) => !string.IsNullOrWhiteSpace(path) && !path.StartsWith('/') && !path.Contains(':') && !path.Any(char.IsControl)
        && path == StableIdentity.NormalizePath(path) && !path.Split('/').Any(p => p is "" or "." or "..");
    private static bool OrderedMetadata(IEnumerable<(int Block, string Path)> values)
    {
        var array = values.ToArray();
        return array.SequenceEqual(array.OrderBy(v => v.Block).ThenBy(v => v.Path, StringComparer.Ordinal));
    }
    private static Point3 Point(TransformVector3 p) => new(p.X, p.Y, p.Z);
    private static bool Bounds(GeometryBounds? b) => b?.Min is not null && b.Max is not null && Finite(b.Min) && Finite(b.Max) && b.Min.X < b.Max.X && b.Min.Y < b.Max.Y && b.Min.Z < b.Max.Z;
    private static bool Finite(TransformVector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static bool Same<T>(T a, T b) => JsonDefaults.Serialize(a) == JsonDefaults.Serialize(b);
    private static S2ModKitException Invalid(string message) => EllipsoidContractJson.Invalid(message);
}
