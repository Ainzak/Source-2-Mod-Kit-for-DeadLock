using System.Globalization;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Application;

public static partial class PairedContractValidator
{
    private static readonly string[] Axes = ["x", "y", "z"];
    public static void ValidateRecipe(RecipeDocument recipe)
    {
        RecipeValidator.Validate(recipe);
        if (recipe.SchemaVersion != 11 || recipe.Operations is not [TransformComponentOperation { Version: 10 } op]
            || !Hash(recipe.InputHash) || recipe.Extensions.Count != 0 || op.Extensions.Count != 0)
            throw Invalid("Require one schema-11 pair without semantic extensions.");
        foreach (var field in op.PairedTransform!.Fields) _ = DirectionalContractValidator.Certificate(field.Field);
        _ = Separation(op.PairedTransform.Fields);
        foreach (var assertion in op.PairedTransform.Protection.Assertions.OfType<DirectionalVertexAssertion>())
            foreach (var set in assertion.Sets)
                if (set.VertexSetHash != DirectionalContractValidator.VertexSetHash(set.VertexIndices)) throw Invalid("Protected vertex identity drift.");
    }

    public static PlannedPairSeparation Separation(IReadOnlyList<PairedDirectionalField> fields)
    {
        if (fields is not { Count: 2 }) throw Invalid("Exactly two fields are required for separation.");
        try
        {
            var proof = DirectionalPairSeparation.Prove(Point(fields[0].Field.Pivot.Point!), Point(fields[0].Field.OuterRadii),
                Point(fields[1].Field.Pivot.Point!), Point(fields[1].Field.OuterRadii));
            static DirectionalRationalBound Bound(System.Numerics.BigInteger n, System.Numerics.BigInteger d) => new(n.ToString(CultureInfo.InvariantCulture), d.ToString(CultureInfo.InvariantCulture));
            return new("axis_projection_exact", 1, Axes[proof.Axis],
                Bound(proof.CenterDistanceNumerator, proof.CenterDistanceDenominator), Bound(proof.RadiusSumNumerator, proof.RadiusSumDenominator), Bound(proof.GapNumerator, proof.GapDenominator));
        }
        catch (ArgumentException e) { throw Invalid($"Exact pair separation is unproved: {e.Message}"); }
    }

    public static TransformComponentOperation Operation(PlannedOperation operation)
    {
        var t = operation.PairedTransformTarget ?? throw Invalid("Missing paired target.");
        return new()
        {
            OperationId = operation.OperationId,
            Version = 10,
            Granularity = "paired_directional_buffer_vertices",
            Transform = null!,
            Selector = t.Selector,
            PairedTransform = t.PairedTransform,
            RuntimeMetadataPolicy = t.RuntimeMetadataPolicy,
            SourceTrianglePolicy = t.SourceTrianglePolicy,
            ProceduralInputPolicy = t.ProceduralInputPolicy,
            ZeroBoneBoxPolicy = t.ZeroBoneBoxPolicy,
            ZeroRenderSpherePolicy = t.ZeroRenderSpherePolicy,
            Limits = new() { MaximumVertexDisplacement = t.DisplacementLimit },
            ExpectedMatchesByLod = t.PairedTransform.Members.SelectMany(m => m.Lods).GroupBy(l => l.Lod)
                .ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Sum(l => l.DrawCallIds.Count)),
            ExpectedVerticesByLod = t.PairedTransform.Members.SelectMany(m => m.Lods).GroupBy(l => l.Lod)
                .ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => checked((int)g.Sum(l => (long)l.ExpectedVertices)))
        };
    }

    public static void ValidatePlan(MutationPlan plan)
    {
        if (plan.SchemaVersion != 7 || plan.Operations is not [{ Kind: "transform_component", Version: 10, PairedTransformTarget: not null } op]
            || op.DirectionalTransformTarget is not null || op.CoordinatedTransformTarget is not null || op.EllipsoidTransformTarget is not null
            || op.ExperimentalTransformTarget is not null || op.AffineTransformTarget is not null || op.CoupledTransformTarget is not null
            || op.GeometryTargets is not { Count: 0 } || op.DistanceFieldTargets is not { Count: 0 }
            || op.SelectedDrawCalls is not { Count: > 0 } || op.SelectedDrawCalls.Any(c => c is null || c.Lod < 0 || c.MeshOrdinal < 0
                || c.ResourceBlockIndex < 0 || c.DrawCallOrdinal < 0 || c.IndexStart < 0 || c.IndexCount <= 0 || c.IndexCount > 3_000_000
                || c.IndexStart > int.MaxValue - c.IndexCount || c.IndexCount % 3 != 0 || !Portable(c.ResourcePath) || !Portable(c.MaterialPath))
            || !op.SelectedDrawCalls.SequenceEqual(op.SelectedDrawCalls.OrderBy(c => c.Lod).ThenBy(c => c.ResourcePath, StringComparer.Ordinal).ThenBy(c => c.MeshOrdinal).ThenBy(c => c.DrawCallOrdinal))
            || op.SelectedDrawCalls.Select(c => c.DrawCallId).Distinct(StringComparer.Ordinal).Count() != op.SelectedDrawCalls.Count
            || op.TargetBlocks is not { Count: > 0 } || op.TargetBlocks.Any(b => b is null)
            || plan.Inputs is not { Count: > 0 } || plan.Inputs.Any(i => i is null || !Hash(i.ContentHash) || i.Size <= 0 || !Portable(i.LogicalPath))
            || !plan.Inputs.Select(i => i.LogicalPath).SequenceEqual(plan.Inputs.Select(i => i.LogicalPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            || !plan.Inputs.Any(i => i.ContentHash == plan.InputHash)) throw Invalid("Malformed isolated paired plan, inputs or selection.");
        var t = op.PairedTransformTarget;
        ValidateTarget(t);
        ValidateRecipe(new() { SchemaVersion = 11, RecipeId = plan.RecipeId, InputHash = plan.InputHash, Operations = [Operation(op)] });
        if (t.InputHash != plan.InputHash || !op.SelectedDrawCalls.Select(c => c.DrawCallId).ToHashSet(StringComparer.Ordinal).SetEquals(t.Selector.DrawCallIds!))
            throw Invalid("Plan selection or source identity drift.");
        foreach (var member in t.PairedTransform.Members)
            foreach (var map in member.Lods)
            {
                var b = t.Buffers.Single(x => x.MemberId == member.MemberId && x.Lod == map.Lod);
                var calls = op.SelectedDrawCalls.Where(c => map.DrawCallIds.Contains(c.DrawCallId, StringComparer.Ordinal)).ToArray();
                if (calls.Length != map.DrawCallIds.Count || calls.Any(c => c.Lod != b.Lod || c.MeshOrdinal != b.MeshOrdinal
                    || c.ResourceBlockIndex != b.ResourceBlockIndex || c.ResourcePath != b.ResourcePath)) throw Invalid("Selected calls differ from complete member ownership.");
                var ranges = calls.Select(c => (c.IndexStart, c.IndexCount)).Distinct().OrderBy(r => r.IndexStart).ToArray();
                if (ranges.Zip(ranges.Skip(1)).Any(pair => pair.First.IndexStart + pair.First.IndexCount > pair.Second.IndexStart)
                    || ranges.Sum(r => r.IndexCount) != t.SourceTriangles.Single(f => f.MemberId == member.MemberId && f.Lod == map.Lod).TriangleIndices.Count)
                    throw Invalid("Complete face inventory differs from uniquely covered selected index ranges.");
            }
        var sources = t.SourceBlocks.ToDictionary(b => b.Index);
        var changed = t.Buffers.SelectMany(b => new[] { b.ResourceBlockIndex, b.VertexResourceBlockIndex }).Concat(t.BoxTargets.Select(b => b.ResourceBlockIndex)).Distinct().Order();
        if (!op.TargetBlocks.Select(b => b.Index).SequenceEqual(changed) || op.TargetBlocks.Any(b => !sources.TryGetValue(b.Index, out var s) || s != b)
            || plan.Fingerprint != MutationPlanJson.ComputeExperimentalFingerprint(plan)) throw Invalid("Combined target blocks or plan fingerprint drift.");
    }

    public static void ValidateTarget(PlannedPairedTransformTarget t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (!Hash(t.InputHash) || !Hash(t.TargetFingerprint) || t.StructuralProfileId != "root_paired_directional_preservation_visual" || t.StructuralProfileVersion != 1
            || t.BoundsPolicyId != "retain_expand_boxes_preserve_runtime_paired" || t.BoundsPolicyVersion != 1
            || t.Buffers is not { Count: > 0 } || t.Buffers.Any(b => b is null) || t.BoxTargets is not { Count: > 0 } || t.BoxTargets.Any(b => b is null)
            || t.PreservationTargets is not { Count: > 0 } || t.PreservationTargets.Any(p => p is null)
            || t.SourceBlocks is not { Count: > 0 and <= 65536 } || t.SourceBlocks.Any(b => b is null || b.Index < 0 || !Hash(b.InputHash) || string.IsNullOrWhiteSpace(b.Type))
            || !t.SourceBlocks.Select(b => b.Index).SequenceEqual(t.SourceBlocks.Select(b => b.Index).Distinct().Order())) throw Invalid("Incomplete paired policies or storage facts.");
        var op = new PlannedOperation("validate", "transform_component", 10, [], []) { PairedTransformTarget = t };
        ValidateRecipe(new() { SchemaVersion = 11, RecipeId = "validate", InputHash = t.InputHash, Operations = [Operation(op)] });
        if (t.Fields is not { Count: 2 } || t.Fields.Any(f => f is null) || !Same(t.Separation, Separation(t.PairedTransform.Fields)))
            throw Invalid("Missing or altered exact separation/field evidence.");
        for (var i = 0; i < 2; i++)
        {
            var field = t.PairedTransform.Fields[i]; var frozen = t.Fields[i]; var point = field.Field.Pivot.Point!;
            var pivot = new AffineEvidenceResolver().ResolvePivot(new(field.Field.Pivot, t.PairedTransform.Members[0].Lods.Select(l => l.Lod).ToArray(), [], []));
            if (frozen.FieldId != field.FieldId || frozen.Pivot is not { Kind: "explicit_point", CoordinateSpace: "model", ReferenceLod: null }
                || !Same(frozen.Pivot.Point, point) || frozen.Pivot.SourceHash != pivot.SourceHash || frozen.Pivot.SourceIdentity != pivot.SourceIdentity
                || !Same(frozen.Certificate, DirectionalContractValidator.Certificate(field.Field))) throw Invalid("Field pivot or full-domain certificate drift.");
        }
        if (!t.Buffers.SequenceEqual(t.Buffers.OrderBy(b => b.Lod).ThenBy(b => b.MeshOrdinal).ThenBy(b => b.VertexBufferOrdinal))
            || t.Buffers.Select(b => (b.MemberId, b.Lod)).Distinct().Count() != t.Buffers.Count
            || t.Buffers.Select(b => (b.MeshOrdinal, b.VertexBufferOrdinal)).Distinct().Count() != t.Buffers.Count
            || t.Buffers.Select(b => b.VertexResourceBlockIndex).Distinct().Count() != t.Buffers.Count
            || t.Buffers.Select(b => b.Codec).Distinct().Count() != 1) throw Invalid("Paired buffer ownership is overlapping or noncanonical.");
        var sources = t.SourceBlocks.ToDictionary(b => b.Index);
        var uniform = t.PairedTransform.Fields.All(f => f.Field.Scale.X == f.Field.Scale.Y && f.Field.Scale.Y == f.Field.Scale.Z);
        foreach (var b in t.Buffers)
        {
            var maps = t.PairedTransform.Members.Where(m => m.MemberId == b.MemberId).SelectMany(m => m.Lods).Where(l => l.Lod == b.Lod).ToArray();
            if (maps.Length != 1 || maps[0].ExpectedVertices != b.VertexCount) throw Invalid("Complete member/buffer count drift.");
            if (b.VertexCount > 1_000_000) throw Invalid("Complete buffer exceeds the bounded vertex inventory.");
            DirectionalContractValidator.ValidateBuffer(b, t.DisplacementLimit, uniform);
            if (!sources.TryGetValue(b.ResourceBlockIndex, out var mesh) || mesh.Type != "MDAT"
                || !sources.TryGetValue(b.VertexResourceBlockIndex, out var vertex) || vertex.Type != "MVTX" || vertex.InputHash != b.VertexBlockInputHash
                || !sources.TryGetValue(b.IndexResourceBlockIndex, out var index) || index.Type != "MIDX" || index.InputHash != b.IndexBlockInputHash) throw Invalid("Buffer/source block identity drift.");
        }
        if (t.Buffers.Count != t.PairedTransform.Members.Sum(m => m.Lods.Count) || !float.IsFinite(t.MaximumDisplacement)
            || t.MaximumDisplacement != t.Buffers.Max(b => b.MaximumDisplacement)) throw Invalid("Member coverage or operation displacement drift.");
        if (t.ContextBuffers is null || t.ContextBuffers.Any(c => c is null) || t.ContextBuffers.Sum(c => (long)c.VertexCount) > 1_000_000)
            throw Invalid("Complete context exceeds the bounded record inventory.");
        DirectionalContractValidator.ValidateStorage(ProtectedBufferStorage.From(t));
        ValidateDispatch(t); ValidateTriangles(t); ValidateProceduralInputs(t);
        if (t.TargetFingerprint != MutationPlanJson.ComputePairedTargetFingerprint(t)) throw Invalid("Paired target fingerprint drift.");
    }

    private static bool Hash(ContentHash h) => h.Value is { Length: 64 };
    private static bool Portable(string? p) => !string.IsNullOrWhiteSpace(p) && !p.StartsWith('/') && !p.Contains(':') && !p.Any(char.IsControl)
        && p == StableIdentity.NormalizePath(p) && !p.Split('/').Any(s => s is "" or "." or "..");
    private static Point3 Point(TransformVector3 v) => new(v.X, v.Y, v.Z);
    private static bool Same<T>(T a, T b) => JsonDefaults.Serialize(a) == JsonDefaults.Serialize(b);
    private static S2ModKitException Invalid(string message) => PairedContractJson.Invalid(message);
}
