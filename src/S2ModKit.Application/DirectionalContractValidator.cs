using System.Buffers.Binary;
using System.Globalization;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Application;

public static partial class DirectionalContractValidator
{
    public static void ValidateRecipe(RecipeDocument recipe)
    {
        RecipeValidator.Validate(recipe);
        if (recipe.SchemaVersion != 10 || recipe.Operations is not [TransformComponentOperation { Version: 9 } op]
            || !Hash(recipe.InputHash) || recipe.Extensions.Count != 0 || op.Extensions.Count != 0)
            throw Invalid("Require one schema-10 directional operation without semantic extensions.");
        _ = Certificate(op.DirectionalTransform!.Field);
        foreach (var vertex in op.DirectionalTransform.Protection.Assertions.OfType<DirectionalVertexAssertion>())
            foreach (var set in vertex.Sets)
                if (set.VertexSetHash != VertexSetHash(set.VertexIndices)) throw ProtectionDrift("Vertex assertion set hash drift.");
    }

    public static PlannedDirectionalCertificate Certificate(DirectionalEllipsoidField field)
    {
        try
        {
            var c = DirectionalEllipsoidCertificate.Create(field.CoreFraction, Point(field.Scale), Point(field.OuterRadii));
            static DirectionalRationalBound Bound(DirectionalFieldBound b) => new(b.Numerator.ToString(CultureInfo.InvariantCulture), b.Denominator.ToString(CultureInfo.InvariantCulture));
            return new(DirectionalEllipsoidCertificate.Algorithm, DirectionalEllipsoidCertificate.Version, DirectionalEllipsoidCertificate.SubdivisionDepth,
                c.AxisLowerBounds.Select(Bound).ToArray(), c.MinimumAxisFactors.Select(Bound).ToArray(), Bound(c.AlphaLowerBound), Bound(c.BetaLowerBound),
                Bound(c.RankOneNormUpperBound), Bound(c.InverseNormUpperBound), Bound(c.MinimumSingularValueLowerBound), Bound(c.DeterminantLowerBound), Bound(c.IdealDisplacementUpperBound));
        }
        catch (ArgumentException e) { throw Errors.InvalidRecipe("DIRECTIONAL_JACOBIAN_UNPROVEN", e.Message, "Choose a field that satisfies the exact global directional certificate."); }
    }

    public static ContentHash VertexSetHash(IReadOnlyList<int> indices)
    {
        var bytes = new byte[checked(indices.Count * 4)];
        for (var i = 0; i < indices.Count; i++) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), indices[i]);
        return ContentHash.Compute(bytes);
    }

    public static ContentHash ContributorSetHash(IEnumerable<DirectionalProtectedSet> sets) => MutationPlanJson.ComputeDirectionalFactsHash(
        sets.Select(s => new { s.Lod, s.MeshOrdinal, s.VertexBufferOrdinal, s.VertexIndices }).ToArray());

    public static TransformComponentOperation Operation(PlannedOperation operation)
    {
        var t = operation.DirectionalTransformTarget ?? throw Invalid("Missing directional target.");
        return new()
        {
            OperationId = operation.OperationId,
            Version = 9,
            Granularity = "directional_buffer_vertices",
            Transform = null!,
            Selector = t.Selector,
            DirectionalTransform = t.DirectionalTransform,
            RuntimeMetadataPolicy = t.RuntimeMetadataPolicy,
            ZeroBoneBoxPolicy = t.ZeroBoneBoxPolicy,
            ZeroRenderSpherePolicy = t.ZeroRenderSpherePolicy,
            Limits = new() { MaximumVertexDisplacement = t.DisplacementLimit },
            ExpectedMatchesByLod = t.DirectionalTransform.Members.SelectMany(m => m.Lods).GroupBy(l => l.Lod)
                .ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Sum(l => l.DrawCallIds.Count)),
            ExpectedVerticesByLod = t.DirectionalTransform.Members.SelectMany(m => m.Lods).GroupBy(l => l.Lod)
                .ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => checked((int)g.Sum(l => (long)l.ExpectedVertices))),
        };
    }

    public static void ValidatePlan(MutationPlan plan)
    {
        if (plan.SchemaVersion != 6 || plan.Operations is not [{ Kind: "transform_component", Version: 9, DirectionalTransformTarget: not null } op]
            || op.CoordinatedTransformTarget is not null || op.EllipsoidTransformTarget is not null || op.ExperimentalTransformTarget is not null
            || op.AffineTransformTarget is not null || op.CoupledTransformTarget is not null || op.GeometryTargets is not { Count: 0 } || op.DistanceFieldTargets is not { Count: 0 }
            || op.SelectedDrawCalls is not { Count: > 0 } || op.SelectedDrawCalls.Any(c => c is null || c.Lod < 0 || c.MeshOrdinal < 0 || c.ResourceBlockIndex < 0
                || c.DrawCallOrdinal < 0 || c.IndexStart < 0 || c.IndexCount <= 0 || c.IndexCount % 3 != 0 || !Portable(c.ResourcePath) || !Portable(c.MaterialPath))
            || !op.SelectedDrawCalls.SequenceEqual(op.SelectedDrawCalls.OrderBy(c => c.Lod).ThenBy(c => c.ResourcePath, StringComparer.Ordinal).ThenBy(c => c.MeshOrdinal).ThenBy(c => c.DrawCallOrdinal))
            || op.SelectedDrawCalls.Select(c => c.DrawCallId).Distinct(StringComparer.Ordinal).Count() != op.SelectedDrawCalls.Count
            || op.TargetBlocks is not { Count: > 0 } || op.TargetBlocks.Any(b => b is null)
            || plan.Inputs is not { Count: > 0 } || plan.Inputs.Any(i => i is null || !Hash(i.ContentHash) || i.Size <= 0 || !Portable(i.LogicalPath))
            || !plan.Inputs.Select(i => i.LogicalPath).SequenceEqual(plan.Inputs.Select(i => i.LogicalPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            || !plan.Inputs.Any(i => i.ContentHash == plan.InputHash)) throw Invalid("Malformed isolated directional plan, inputs or selection.");
        var t = op.DirectionalTransformTarget;
        ValidateTarget(t);
        ValidateRecipe(new() { SchemaVersion = 10, RecipeId = plan.RecipeId, InputHash = plan.InputHash, Operations = [Operation(op)] });
        if (t.InputHash != plan.InputHash || !op.SelectedDrawCalls.Select(c => c.DrawCallId).ToHashSet(StringComparer.Ordinal).SetEquals(t.Selector.DrawCallIds!))
            throw Invalid("Plan selection or input identity drift.");
        foreach (var member in t.DirectionalTransform.Members)
            foreach (var map in member.Lods)
            {
                var b = t.Buffers.Single(x => x.MemberId == member.MemberId && x.Lod == map.Lod);
                var calls = op.SelectedDrawCalls.Where(c => map.DrawCallIds.Contains(c.DrawCallId, StringComparer.Ordinal)).ToArray();
                if (calls.Length != map.DrawCallIds.Count || calls.Any(c => c.Lod != b.Lod || c.MeshOrdinal != b.MeshOrdinal
                    || c.ResourceBlockIndex != b.ResourceBlockIndex || c.ResourcePath != b.ResourcePath)) throw Invalid("Selected source records differ from member/LOD ownership.");
            }
        var sources = t.SourceBlocks.ToDictionary(b => b.Index);
        var changed = t.Buffers.SelectMany(b => new[] { b.ResourceBlockIndex, b.VertexResourceBlockIndex }).Concat(t.BoxTargets.Select(b => b.ResourceBlockIndex)).Distinct().Order();
        if (!op.TargetBlocks.Select(b => b.Index).SequenceEqual(changed) || op.TargetBlocks.Any(b => !sources.TryGetValue(b.Index, out var s) || s != b)
            || plan.Fingerprint != MutationPlanJson.ComputeExperimentalFingerprint(plan)) throw Invalid("Plan block closure or fingerprint drift.");
    }

    public static void ValidateTarget(PlannedDirectionalTransformTarget t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (!Hash(t.InputHash) || !Hash(t.TargetFingerprint) || t.StructuralProfileId != "root_protected_directional_ellipsoid_visual" || t.StructuralProfileVersion != 1
            || t.BoundsPolicyId != "retain_expand_boxes_preserve_runtime_directional" || t.BoundsPolicyVersion != 1
            || t.RuntimeMetadataPolicy is not { Kind: "preserve_unverified", Version: 1 } || t.ZeroBoneBoxPolicy is not { Kind: "reject", Version: 1 }
            || t.ZeroRenderSpherePolicy is not { Kind: "reject", Version: 1 }
            || t.Buffers is not { Count: > 0 } || t.Buffers.Any(b => b is null) || t.BoxTargets is not { Count: > 0 } || t.BoxTargets.Any(b => b is null)
            || t.PreservationTargets is not { Count: > 0 } || t.PreservationTargets.Any(p => p is null)
            || t.SourceBlocks is not { Count: > 0 } || t.SourceBlocks.Any(b => b is null || b.Index < 0 || !Hash(b.InputHash) || string.IsNullOrWhiteSpace(b.Type))
            || !t.SourceBlocks.Select(b => b.Index).SequenceEqual(t.SourceBlocks.Select(b => b.Index).Distinct().Order())) throw Invalid("Incomplete directional target policies or storage facts.");
        RecipeValidator.ValidateDirectionalIntent(t.DirectionalTransform, t.DisplacementLimit);
        var op = new PlannedOperation("validate", "transform_component", 9, [], []) { DirectionalTransformTarget = t };
        ValidateRecipe(new() { SchemaVersion = 10, RecipeId = "validate", InputHash = t.InputHash, Operations = [Operation(op)] });
        var f = t.DirectionalTransform.Field;
        if (t.Pivot is not { CoordinateSpace: "model" } pivot || pivot.Kind != f.Pivot.Kind || pivot.ReferenceLod != f.Pivot.ReferenceLod
            || pivot.Point is null || !new[] { pivot.Point.X, pivot.Point.Y, pivot.Point.Z }.All(float.IsFinite) || !Hash(pivot.SourceHash) || string.IsNullOrWhiteSpace(pivot.SourceIdentity)
            || (f.Pivot.Point is not null && !Same(pivot.Point, f.Pivot.Point)) || !Same(t.Certificate, Certificate(f))) throw Invalid("Resolved centered pivot or exact certificate drift.");
        if (f.Pivot.Kind == "explicit_point")
        {
            var explicitPivot = new AffineEvidenceResolver().ResolvePivot(new(f.Pivot, t.DirectionalTransform.Members[0].Lods.Select(l => l.Lod).ToArray(), [], []));
            if (pivot.SourceHash != explicitPivot.SourceHash || pivot.SourceIdentity != explicitPivot.SourceIdentity) throw Invalid("Explicit pivot source identity drift.");
        }
        if (!t.Buffers.SequenceEqual(t.Buffers.OrderBy(b => b.Lod).ThenBy(b => b.MeshOrdinal).ThenBy(b => b.VertexBufferOrdinal))
            || t.Buffers.Select(b => (b.MemberId, b.Lod)).Distinct().Count() != t.Buffers.Count
            || t.Buffers.Select(b => (b.MeshOrdinal, b.VertexBufferOrdinal)).Distinct().Count() != t.Buffers.Count
            || t.Buffers.Select(b => b.VertexResourceBlockIndex).Distinct().Count() != t.Buffers.Count
            || t.Buffers.Select(b => b.Codec).Distinct().Count() != 1) throw Invalid("Buffer ownership is overlapping or noncanonical.");
        var sources = t.SourceBlocks.ToDictionary(b => b.Index);
        var uniform = f.Scale.X == f.Scale.Y && f.Scale.Y == f.Scale.Z;
        foreach (var b in t.Buffers)
        {
            var maps = t.DirectionalTransform.Members.Where(m => m.MemberId == b.MemberId).SelectMany(m => m.Lods).Where(l => l.Lod == b.Lod).ToArray();
            if (maps.Length != 1 || maps[0].ExpectedVertices != b.VertexCount) throw Invalid("Complete member/buffer count drift.");
            ValidateBuffer(b, t.DisplacementLimit, uniform);
            if (!sources.TryGetValue(b.ResourceBlockIndex, out var mesh) || mesh.Type != "MDAT"
                || !sources.TryGetValue(b.VertexResourceBlockIndex, out var vertex) || vertex.Type != "MVTX" || vertex.InputHash != b.VertexBlockInputHash
                || !sources.TryGetValue(b.IndexResourceBlockIndex, out var index) || index.Type != "MIDX" || index.InputHash != b.IndexBlockInputHash) throw Invalid("Buffer/source identity drift.");
        }
        if (t.Buffers.Count != t.DirectionalTransform.Members.Sum(m => m.Lods.Count) || !float.IsFinite(t.MaximumDisplacement)
            || t.MaximumDisplacement != t.Buffers.Max(b => b.MaximumDisplacement)) throw Invalid("Member coverage or displacement drift.");
        ValidateStorage(ProtectedBufferStorage.From(t));
        if (t.TargetFingerprint != MutationPlanJson.ComputeDirectionalTargetFingerprint(t)) throw Invalid("Directional target fingerprint drift.");
    }

    internal static void ValidateBuffer(PlannedCoordinatedBuffer b, float limit, bool uniform)
    {
        if (b.Lod < 0 || b.MeshOrdinal < 0 || b.ResourceBlockIndex < 0 || b.VertexBufferOrdinal < 0 || b.IndexBufferOrdinal < 0
            || b.VertexResourceBlockIndex < 0 || b.IndexResourceBlockIndex < 0 || b.VertexCount <= 0 || b.OwnershipPolicy != "exclusive" || !Portable(b.ResourcePath)
            || b.FullVertexCount < 0 || b.TransitionVertexCount < 0 || b.PinnedVertexCount < 0 || (long)b.FullVertexCount + b.TransitionVertexCount + b.PinnedVertexCount != b.VertexCount
            || b.ChangedPositionCount <= 0 || b.ChangedPositionCount > b.VertexCount - b.PinnedVertexCount || b.ChangedFrameCount < 0
            || b.ChangedFrameCount > b.TransitionVertexCount + (uniform ? 0L : b.FullVertexCount)
            || !float.IsFinite(b.MaximumDisplacement) || b.MaximumDisplacement <= 0 || b.MaximumDisplacement > limit || !Bounds(b.BeforeBounds) || !Bounds(b.ExpectedAfterBounds)
            || b.PositionLayout is not { Format: "R32G32B32_FLOAT" } p || p.Offset < 0 || (long)p.Offset + 12 > p.Stride
            || b.PackedFrameLayout is not { Format: "R32_UINT", EncodingProfile: "source2_normal_tangent_v2" } frame || frame.Offset < 0 || (long)frame.Offset + 4 > frame.Stride
            || frame.Stride != p.Stride || (frame.Offset < (long)p.Offset + 12 && p.Offset < (long)frame.Offset + 4)
            || b.Codec is null || string.IsNullOrWhiteSpace(b.Codec.Name) || string.IsNullOrWhiteSpace(b.Codec.ApiProfile) || string.IsNullOrWhiteSpace(b.Codec.Platform)
            || string.IsNullOrWhiteSpace(b.Codec.Version) || !Hash(b.Codec.BinaryHash)
            || !new[] { b.VertexBlockInputHash, b.IndexBlockInputHash, b.InputDecodedVertexBufferHash, b.ExpectedDecodedVertexBufferHash, b.DecodedIndexBufferHash,
                b.VertexSetHash, b.MaskHash, b.WeightHash, b.InputPositionHash, b.ExpectedPositionHash, b.InputPackedFrameHash, b.ExpectedPackedFrameHash }.All(Hash)
            || (b.ChangedFrameCount == 0) != (b.InputPackedFrameHash == b.ExpectedPackedFrameHash)
            || b.InputPositionHash == b.ExpectedPositionHash || b.InputDecodedVertexBufferHash == b.ExpectedDecodedVertexBufferHash) throw Invalid("Invalid directional buffer layouts, codec, effect or hashes.");
    }

    internal static void ValidateStorage(ProtectedBufferStorage t)
    {
        var sources = t.SourceBlocks.ToDictionary(b => b.Index);
        ValidateWordAudits(t);
        ValidateContext(t, sources);
        ValidateProtection(t);
        ValidateBoxes(t, sources);
    }

    private static void ValidateWordAudits(ProtectedBufferStorage t)
    {
        if (t.WordAudits is null || t.WordAudits.Any(w => w is null) || t.WordAudits.Count != t.Buffers.Count) throw Invalid("Missing per-buffer word audits.");
        for (var i = 0; i < t.Buffers.Count; i++)
        {
            var b = t.Buffers[i]; var w = t.WordAudits[i];
            if (w.MemberId != b.MemberId || w.Lod != b.Lod || w.ChangedPositionIndices is null || w.ChangedFrameIndices is null || w.PinnedIndices is null
                || w.ChangedPositionIndices.Count != b.ChangedPositionCount || w.ChangedFrameIndices.Count != b.ChangedFrameCount || w.PinnedIndices.Count != b.PinnedVertexCount)
                throw Invalid("Word-audit member/LOD or changed/pinned counts drifted.");
            foreach (var indices in new[] { w.ChangedPositionIndices, w.ChangedFrameIndices, w.PinnedIndices })
                if (indices.Any(v => v < 0 || v >= b.VertexCount) || !indices.SequenceEqual(indices.Distinct().Order())) throw Invalid("Word-audit indices must be sorted, unique and in range.");
            if (w.ChangedPositionSetHash != VertexSetHash(w.ChangedPositionIndices) || w.ChangedFrameSetHash != VertexSetHash(w.ChangedFrameIndices) || w.PinnedSetHash != VertexSetHash(w.PinnedIndices)
                || w.PinnedIndices.Intersect(w.ChangedPositionIndices.Concat(w.ChangedFrameIndices)).Any()
                || !new[] { w.SourcePinnedPositionHash, w.ExpectedPinnedPositionHash, w.SourcePinnedFrameHash, w.ExpectedPinnedFrameHash,
                    w.SourceUnchangedAttributesHash, w.ExpectedUnchangedAttributesHash }.All(Hash)
                || w.SourcePinnedPositionHash != w.ExpectedPinnedPositionHash || w.SourcePinnedFrameHash != w.ExpectedPinnedFrameHash
                || w.SourceUnchangedAttributesHash != w.ExpectedUnchangedAttributesHash
                || (b.PinnedVertexCount == 0 && (w.SourcePinnedPositionHash != ContentHash.Compute([]) || w.SourcePinnedFrameHash != ContentHash.Compute([]))))
                throw Invalid("Pinned/unchanged words changed, or word-audit set identities drifted.");
        }
    }

    private static bool Hash(ContentHash h) => h.Value is { Length: 64 };
    private static bool Portable(string? p) => !string.IsNullOrWhiteSpace(p) && !p.StartsWith('/') && !p.Contains(':') && !p.Any(char.IsControl)
        && p == StableIdentity.NormalizePath(p) && !p.Split('/').Any(s => s is "" or "." or "..");
    private static bool Bounds(GeometryBounds? b) => b?.Min is not null && b.Max is not null
        && new[] { b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z }.All(float.IsFinite) && b.Min.X < b.Max.X && b.Min.Y < b.Max.Y && b.Min.Z < b.Max.Z;
    private static Point3 Point(TransformVector3 v) => new(v.X, v.Y, v.Z);
    private static bool Same<T>(T a, T b) => JsonDefaults.Serialize(a) == JsonDefaults.Serialize(b);
    private static S2ModKitException Invalid(string message) => DirectionalContractJson.Invalid(message);
    private static S2ModKitException ProtectionDrift(string message) => Errors.InvalidRecipe("DIRECTIONAL_PROTECTION_DRIFT", message,
        "Regenerate exact source-bound assertions and their deduplicated all-LOD union.");
}
