using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    public Task<CoordinatedTransformVerification> VerifyCoordinatedTransformAsync(ArtifactContent input, ArtifactContent output,
        MutationPlan plan, CancellationToken cancellationToken = default)
    {
        try { return Task.FromResult(VerifyCoordinatedCore(input, output, plan, cancellationToken)); }
        catch (S2ModKitException) { throw; }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException or NotSupportedException
            or OverflowException or IndexOutOfRangeException or KeyNotFoundException or ValveResourceFormat.Utils.UnexpectedMagicException)
        { throw CoordinatedDrift($"Independent coordinated reopen failed: {e.Message}"); }
    }

    private CoordinatedTransformVerification VerifyCoordinatedCore(ArtifactContent input, ArtifactContent output, MutationPlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(plan));
        if (!IsCoordinatedPlan(plan) || ContentHash.Compute(input.Bytes.Span) != input.ContentHash || input.ContentHash != plan.InputHash
            || ContentHash.Compute(output.Bytes.Span) != output.ContentHash || input.LogicalPath != output.LogicalPath)
            throw CoordinatedDrift("Immutable input/output identities disagree with the coordinated contract.");
        using var before = Parse(input, retainGeometryAnalysis: true);
        using var after = Parse(output, retainGeometryAnalysis: true);
        ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.OutputReopened);
        var operation = plan.Operations.Single(); var target = operation.CoordinatedTransformTarget!;
        var mutable = VerifyExperimentalEnvelope(before, after.Envelope, output, target.SourceBlocks, operation.TargetBlocks);
        var sources = ResolveCoordinatedProfiles(CoordinatedRequest(input, before.Snapshot, operation), before);
        var observed = ResolveCoordinatedProfiles(CoordinatedRequest(output, after.Snapshot, operation), after)
            .ToDictionary(m => (m.MemberId, m.Profile.Mesh.Lod));
        if (sources.Length != target.Buffers.Count || sources.Length != observed.Count
            || !mutable.SetEquals(sources.SelectMany(m => new[] { m.Profile.Mesh.BlockIndex, m.Profile.Vertices.Snapshot.ResourceBlockIndex })))
            throw CoordinatedDrift("The independent complete member and mutation closure differs.");
        var sourceProfiles = sources.Select(m => m.Profile).ToArray();
        _ = ResolveExperimentalRootMetadata(before, sourceProfiles);
        _ = ResolveExperimentalRootMetadata(after, observed.Values.Select(m => m.Profile).ToArray());
        var math = new CoordinatedFieldMath(target.CoordinatedTransform.Field, target.DisplacementLimit);
        if (JsonDefaults.Serialize(math.Proof) != JsonDefaults.Serialize(target.FieldProof)) throw CoordinatedDrift("The common field proof is stale.");
        var buffers = new List<CoordinatedBufferObservation>();
        var boxes = new List<ExperimentalBoxEvidence>();
        var zeroBoxes = new List<ZeroBoneBoxPreservationEvidence>();
        var zeroSpheres = new List<ZeroRenderSpherePreservationEvidence>();
        var preserved = new List<PlannedExperimentalPreservationTarget>();
        var outputPoints = new Dictionary<(int Mesh, int Buffer), Point3[]>();
        foreach (var member in sources)
        {
            token.ThrowIfCancellationRequested();
            var p = member.Profile; var current = observed[(member.MemberId, p.Mesh.Lod)].Profile;
            var facts = target.Buffers.Single(b => b.MemberId == member.MemberId && b.Lod == p.Mesh.Lod);
            buffers.Add(VerifyCoordinatedBuffer(input, p, current, facts, math));
            outputPoints.Add((p.Mesh.MeshOrdinal, p.Vertices.Snapshot.Ordinal), Enumerable.Range(0, current.Vertices.Snapshot.VertexCount)
                .Select(v => Source2GeometryAnalyzer.ReadPosition(current.Vertices, v)).ToArray());
        }
        var seams = ResolveCoordinatedSeams(before, sources, outputPoints);
        if (seams != target.SeamInventory) throw CoordinatedDrift("Complete source seam inventory differs from final observed member words.");
        // One full mesh closure, independent of the number of selected member buffers.
        foreach (var group in sources.GroupBy(m => m.Profile.Mesh.MeshOrdinal))
        {
            var source = group.First().Profile;
            var current = observed[(group.First().MemberId, source.Mesh.Lod)].Profile;
            var selectedBuffers = group.Select(m => m.Profile.Vertices.Snapshot.Ordinal).ToHashSet();
            VerifyCoordinatedMeshStorage(source, current, selectedBuffers);
            var selected = group.SelectMany(m => m.Profile.SelectedVertices.Select(v => v + m.Profile.BufferBaseOffset)).ToHashSet();
            var affected = source.Metadata.BoneBounds.Where(b => b.InfluencedVertices.Any(selected.Contains)).ToArray();
            if (affected.Length == 0) throw CoordinatedDrift("No complete affected bone closure was resolved.");
            var positive = affected.Where(b => b.LocalBoundsSize.X > 0 && b.LocalBoundsSize.Y > 0 && b.LocalBoundsSize.Z > 0).ToArray();
            boxes.AddRange(VerifyCoordinatedPositiveBoxes(input, source, current, positive, target.BoxTargets));
            foreach (var bone in affected.Except(positive))
            {
                var (box, sphere) = VerifyCoordinatedZeroFields(input, before, source, current, bone, target, operation);
                zeroBoxes.Add(box);
                if (sphere is not null) zeroSpheres.Add(sphere);
            }
            AddCoordinatedRenderSphereTargets(before, source, affected.Select(b => b.BoneIndex).ToHashSet(), target.ZeroRenderSphereTargets, preserved);
            // Undo only the allowed positive box words, then audit every MDAT field,
            // including unchanged zero boxes/spheres, remaps, skin weights and draw calls.
            foreach (var box in target.BoxTargets.Where(b => b.ResourceBlockIndex == source.Mesh.BlockIndex))
                ReplaceExperimentalBox(current.Mesh.Block.Data, box, reverse: true);
            if (KvSemanticHasher.ComputeComplete(source.Mesh.Block.Data) != KvSemanticHasher.ComputeComplete(current.Mesh.Block.Data))
                throw CoordinatedDrift("MDAT changed outside the prescribed positive box words.");
        }
        if (boxes.Count != target.BoxTargets.Count || zeroBoxes.Count != target.ZeroBoxTargets.Count || zeroSpheres.Count != target.ZeroRenderSphereTargets.Count)
            throw CoordinatedDrift("The independently observed field inventory is incomplete or contains extra targets.");
        if (target.MaximumDisplacement != buffers.Max(b => b.MaximumDisplacement)) throw CoordinatedDrift("The combined maximum displacement is stale.");
        var uniqueSources = sourceProfiles.DistinctBy(p => p.Mesh.MeshOrdinal).ToArray();
        AddExperimentalVerificationRootPreservation(before, sourceProfiles, preserved);
        var sorted = preserved.OrderBy(p => p.ResourceBlockIndex).ThenBy(p => p.FieldPath, StringComparer.Ordinal).ToArray();
        if (JsonDefaults.Serialize(sorted) != JsonDefaults.Serialize(target.PreservationTargets)) throw CoordinatedDrift("The preserved metadata inventory is missing, duplicated or forged.");
        var uniqueOutputs = observed.Values.Select(m => m.Profile).DistinctBy(p => p.Mesh.MeshOrdinal).ToDictionary(p => p.Mesh.MeshOrdinal);
        var preservation = sorted.Select(p => VerifyExperimentalPreservedField(after, uniqueSources, uniqueOutputs, p)).ToArray();
        return new(CoordinatedContractValidator.PlannedBoundaries().Where(b => b.Name != "runtime")
            .Select(b => b.Status == "not_applicable" ? b with { Status = "passed", Summary = "Independent combined source/output audit passed." } : b).ToArray(),
            buffers, boxes.OrderBy(b => b.Target.ResourceBlockIndex).ThenBy(b => b.Target.FieldPath, StringComparer.Ordinal).ToArray(),
            zeroBoxes.OrderBy(b => b.Target.ResourceBlockIndex).ThenBy(b => b.Target.FieldPath, StringComparer.Ordinal).ToArray(),
            zeroSpheres.OrderBy(b => b.Target.ResourceBlockIndex).ThenBy(b => b.Target.FieldPath, StringComparer.Ordinal).ToArray(), preservation);
    }

    private static void VerifyCoordinatedMeshStorage(Source2AffineProfile source, Source2AffineProfile observed, HashSet<int> selectedBuffers)
    {
        var old = source.Mesh.GeometryAnalysis!; var current = observed.Mesh.GeometryAnalysis!;
        if (old.VertexBuffers.Count != current.VertexBuffers.Count || old.IndexBuffers.Count != current.IndexBuffers.Count)
            throw CoordinatedDrift("Participating mesh buffer cardinality changed.");
        for (var index = 0; index < old.VertexBuffers.Count; index++)
        {
            VerifyExperimentalBufferLayout(old.VertexBuffers[index].Snapshot, current.VertexBuffers[index].Snapshot,
                old.VertexBuffers[index].Decoded, current.VertexBuffers[index].Decoded, selectedBuffers.Contains(index));
            if (old.IndexBuffers[index].Snapshot != current.IndexBuffers[index].Snapshot || !old.IndexBuffers[index].Indices.SequenceEqual(current.IndexBuffers[index].Indices))
                throw CoordinatedDrift("An index buffer changed.");
        }
    }
}
