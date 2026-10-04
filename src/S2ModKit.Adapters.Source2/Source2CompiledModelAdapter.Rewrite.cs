using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Utils;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    public bool CanRewrite(ModelSnapshot model, MutationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.SchemaVersion == 5 || plan.Operations.Any(o => o.Version == 8 || o.CoordinatedTransformTarget is not null))
            return !HasIncompleteMdatCoverage(model) && !SelectsSharedLodMesh(model, plan) && CanRewriteCoordinated(model, plan);
        if (plan.SchemaVersion == 4 || plan.Operations.Any(o => o.Version == 7 || o.EllipsoidTransformTarget is not null))
            return !HasIncompleteMdatCoverage(model) && !SelectsSharedLodMesh(model, plan) && CanRewriteEllipsoid(model, plan);
        return !HasIncompleteMdatCoverage(model)
            && !SelectsSharedLodMesh(model, plan)
            && (CanRewriteRemoval(model, plan)
            || CanRewriteTransform(model, plan)
            || CanRewriteCoupledTransform(model, plan)
            || CanRewriteAffineTransform(model, plan)
            || CanRewriteExperimentalTransform(model, plan)
            || CanRewriteEllipsoid(model, plan));
    }

    // A snapshot whose LOD projection does not represent every distinct MDAT block of its artifact
    // describes an incomplete model (for example a LOD-excluded mesh omitted by inspection-only
    // parsing). The whole resource stays read-only; the omission is never a per-block exemption.
    // Distinct block identities are compared because shared meshes repeat across LODs.
    private static bool HasIncompleteMdatCoverage(ModelSnapshot model) =>
        model.Artifact.Blocks
            .Where(block => string.Equals(block.Type, "MDAT", StringComparison.Ordinal))
            .Select(block => block.Index)
            .Except(model.Lods.SelectMany(lod => lod.Meshes.Select(mesh => mesh.ResourceBlockIndex).Distinct()))
            .Any();

    private static bool SelectsSharedLodMesh(ModelSnapshot model, MutationPlan plan)
    {
        var sharedBlocks = model.Lods
            .SelectMany(lod => lod.Meshes.Select(mesh => (lod.Level, mesh.ResourceBlockIndex)))
            .GroupBy(item => item.ResourceBlockIndex)
            .Where(group => group.Select(item => item.Level).Distinct().Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet();
        return plan.Operations
            .SelectMany(operation => operation.SelectedDrawCalls)
            .Any(call => sharedBlocks.Contains(call.ResourceBlockIndex));
    }

    private static bool CanRewriteRemoval(ModelSnapshot model, MutationPlan plan)
    {
        var targetBlocks = plan.Operations.SelectMany(operation => operation.TargetBlocks).ToArray();
        var selected = plan.Operations.SelectMany(operation => operation.SelectedDrawCalls).ToArray();
        var targetIndices = targetBlocks.Select(block => block.Index).ToHashSet();
        var resourceTypes = model.Artifact.Blocks.Select(block => block.Type).ToHashSet(StringComparer.Ordinal);
        return plan.Operations.Count > 0
            && plan.Operations.All(operation => operation.Kind == "remove_component" && operation.Version == 1)
            && plan.InputHash == model.Artifact.ContentHash
            && resourceTypes.Contains("MVTX")
            && resourceTypes.Contains("MIDX")
            && !resourceTypes.Contains("MBUF")
            && targetBlocks.Length > 0
            && selected.Length > 0
            && targetBlocks.All(block => string.Equals(block.Type, "MDAT", StringComparison.Ordinal))
            && selected.All(drawCall => targetIndices.Contains(drawCall.ResourceBlockIndex));
    }

    public Task<RewriteCandidate> RewriteAsync(
        ArtifactContent input,
        ModelSnapshot model,
        MutationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        if (ContentHash.Compute(input.Bytes.Span) != input.ContentHash
            || model.Artifact.ContentHash != input.ContentHash
            || plan.InputHash != input.ContentHash)
        {
            throw Errors.Input("INPUT_HASH_DRIFT", "The compiled-model bytes, inspected snapshot, and mutation plan do not share one input hash.", "Re-import the immutable input and regenerate the plan.");
        }

        if (HasIncompleteMdatCoverage(model))
        {
            throw Errors.Unsupported(
                "MDAT_COVERAGE_INCOMPLETE",
                "The model snapshot does not represent every MDAT block of its artifact inventory.",
                "The layout includes meshes excluded from every LOD; the whole resource remains read-only.");
        }

        if (SelectsSharedLodMesh(model, plan))
        {
            throw Errors.Unsupported(
                "SHARED_LOD_MESH_READ_ONLY",
                "The plan selects a compiled mesh reused by multiple LODs.",
                "Inspect this model read-only; shared-LOD binary mutation has no verified writer.");
        }

        if (!CanRewrite(model, plan))
        {
            throw Errors.Unsupported("REWRITE_CAPABILITY_UNAVAILABLE", "The mutation plan does not match a supported Source 2 rewrite profile.", "Re-inspect the compiled model and create a supported, single-kind mutation plan.");
        }

        if (IsCoordinatedPlan(plan))
        {
            return Task.FromResult(RewriteCoordinated(input, model, plan, cancellationToken));
        }

        if (IsEllipsoidPlan(plan))
        {
            return Task.FromResult(RewriteEllipsoid(input, model, plan, cancellationToken));
        }

        if (IsExperimentalTransformPlan(plan))
        {
            return Task.FromResult(RewriteExperimentalTransform(input, model, plan, cancellationToken));
        }

        if (IsCoupledTransformPlan(plan))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(RewriteCoupledTransform(input, model, plan.Operations[0].CoupledTransformTarget!));
        }

        if (IsAffineTransformPlan(plan))
        {
            return Task.FromResult(RewriteAffineTransform(input, model, plan, cancellationToken));
        }

        if (IsTransformPlan(plan))
        {
            return Task.FromResult(RewriteTransform(input, model, plan, cancellationToken));
        }

        using var parsed = Parse(input);
        ValidateSnapshotAgreement(model, parsed.Snapshot);
        var selected = plan.Operations.SelectMany(operation => operation.SelectedDrawCalls).ToArray();
        var removals = new Dictionary<KVObject, List<int>>(ReferenceEqualityComparer.Instance);
        var affectedMeshes = new Dictionary<int, ParsedMesh>();
        foreach (var item in selected)
        {
            if (!parsed.MeshesByOrdinal.TryGetValue(item.MeshOrdinal, out var mesh)
                || mesh.Lod != item.Lod
                || mesh.BlockIndex != item.ResourceBlockIndex
                || !string.Equals(StableIdentity.NormalizePath(item.ResourcePath), input.LogicalPath, StringComparison.Ordinal))
            {
                throw Errors.Verification("PLANNED_DRAW_CALL_LOCATION_DRIFT", $"Draw call '{item.DrawCallId}' no longer maps to its planned mesh and LOD.", "Re-inspect the immutable input and regenerate the plan.");
            }

            var location = mesh.DrawCalls.SingleOrDefault(drawCall => drawCall.Snapshot.DrawCallOrdinal == item.DrawCallOrdinal);
            if (location is null || !Matches(location.Snapshot, item))
            {
                throw Errors.Verification("PLANNED_DRAW_CALL_SEMANTICS_DRIFT", $"Draw call '{item.DrawCallId}' no longer matches its planned semantic range.", "Re-inspect the immutable input and regenerate the plan.");
            }

            if (!removals.TryGetValue(location.Collection, out var indices))
            {
                indices = [];
                removals.Add(location.Collection, indices);
            }

            if (indices.Contains(location.CollectionIndex))
            {
                throw Errors.Verification("PLANNED_DRAW_CALL_DUPLICATE", $"Draw call '{item.DrawCallId}' is selected more than once.", "Regenerate a non-overlapping mutation plan.");
            }

            indices.Add(location.CollectionIndex);
            affectedMeshes[mesh.BlockIndex] = mesh;
        }

        ValidateTargetBlocks(parsed, plan, affectedMeshes.Keys);
        foreach (var removal in removals)
        {
            foreach (var index in removal.Value.OrderDescending())
            {
                removal.Key.RemoveAt(index);
            }
        }

        var replacements = new Dictionary<int, ReadOnlyMemory<byte>>();
        foreach (var affected in affectedMeshes.OrderBy(entry => entry.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var serialized = new MemoryStream();
                affected.Value.Block.Serialize(serialized);
                if (serialized.Length == 0 || serialized.Length > int.MaxValue)
                {
                    throw new InvalidDataException("The isolated MDAT serializer produced an empty or oversized block.");
                }

                replacements.Add(affected.Key, serialized.ToArray());
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or NotSupportedException or ArgumentException or OverflowException)
            {
                throw new S2ModKitException(
                    new S2Error("MDAT_SERIALIZATION_UNSUPPORTED", "source2_adapter", $"VRF could not serialize isolated MDAT block {affected.Key} safely.", "Do not publish this input; add a reviewed serializer profile for its exact KV layout.", ErrorCategory.UnsupportedCapability),
                    exception);
            }
        }

        var candidateBytes = ResourceEnvelopeWriter.Rebuild(parsed.Envelope, replacements);
        var candidateArtifact = new ArtifactContent(input.LogicalPath, ContentHash.Compute(candidateBytes), candidateBytes);
        ModelSnapshot candidateSnapshot;
        try
        {
            using var reopened = Parse(candidateArtifact);
            candidateSnapshot = reopened.Snapshot;
        }
        catch (S2ModKitException exception)
        {
            throw new S2ModKitException(
                new S2Error("MDAT_SERIALIZATION_ROUNDTRIP_UNSUPPORTED", "source2_adapter", "The rebuilt compiled model could not be reopened through the supported VRF profile.", "Reject the candidate; no build should be published.", ErrorCategory.UnsupportedCapability),
                exception);
        }

        var verification = ModelVerifier.Verify(model, candidateSnapshot, plan);
        if (!verification.IsValid)
        {
            var failed = verification.Boundaries.Where(boundary => boundary.Status == "failed").Select(boundary => boundary.Name);
            throw Errors.Verification("SOURCE2_REWRITE_VERIFICATION_FAILED", $"The isolated MDAT rewrite failed: {string.Join(", ", failed)}.", "Reject the candidate and inspect the adapter evidence.");
        }

        return Task.FromResult(new RewriteCandidate(input.LogicalPath, candidateBytes, candidateSnapshot));
    }
    private static void ValidateTargetBlocks(ParsedModel parsed, MutationPlan plan, IEnumerable<int> affectedBlockIndices)
    {
        var affected = affectedBlockIndices.ToHashSet();
        var planned = plan.Operations.SelectMany(operation => operation.TargetBlocks)
            .GroupBy(block => block.Index)
            .ToDictionary(group => group.Key, group => group.ToArray());
        if (!affected.SetEquals(planned.Keys))
        {
            throw Errors.Verification("TARGET_BLOCK_SET_DRIFT", "Affected MDAT blocks differ from the dry-run target-block set.", "Re-inspect the input and regenerate the plan.");
        }

        foreach (var entry in planned)
        {
            var raw = parsed.Envelope.Blocks[entry.Key];
            if (entry.Value.Select(block => (block.Type, block.InputHash)).Distinct().Count() != 1
                || !string.Equals(raw.Type, entry.Value[0].Type, StringComparison.Ordinal)
                || ContentHash.Compute(raw.Payload.Span) != entry.Value[0].InputHash)
            {
                throw Errors.Verification("TARGET_BLOCK_FINGERPRINT_DRIFT", $"Target block {entry.Key} no longer matches the dry-run fingerprint.", "Re-inspect the immutable input and regenerate the plan.");
            }
        }
    }

}
