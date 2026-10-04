using System.Buffers.Binary;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    public Task<EllipsoidTransformVerification> VerifyEllipsoidTransformAsync(ArtifactContent input, ArtifactContent output,
        MutationPlan plan, CancellationToken cancellationToken = default)
    {
        try { return Task.FromResult(VerifyEllipsoidCore(input, output, plan, cancellationToken)); }
        catch (S2ModKitException e) when (e.Error.Code.StartsWith("EXPERIMENTAL_", StringComparison.Ordinal)) { throw EllipsoidDrift(e.Message); }
        catch (S2ModKitException) { throw; }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException or NotSupportedException
            or OverflowException or IndexOutOfRangeException or KeyNotFoundException or ValveResourceFormat.Utils.UnexpectedMagicException)
        { throw EllipsoidDrift($"Independent resource reopen failed: {e.Message}"); }
    }

    private EllipsoidTransformVerification VerifyEllipsoidCore(ArtifactContent input, ArtifactContent output, MutationPlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(plan));
        if (!IsEllipsoidPlan(plan) || ContentHash.Compute(input.Bytes.Span) != input.ContentHash || input.ContentHash != plan.InputHash
            || ContentHash.Compute(output.Bytes.Span) != output.ContentHash || input.LogicalPath != output.LogicalPath)
            throw EllipsoidDrift("Immutable input/output identities differ from the versioned contract.");
        using var before = Parse(input, retainGeometryAnalysis: true);
        using var after = Parse(output, retainGeometryAnalysis: true);
        ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.OutputReopened);
        var operation = plan.Operations.Single();
        var target = operation.EllipsoidTransformTarget!;
        var mutable = VerifyExperimentalEnvelope(before, after.Envelope, output, target.SourceBlocks, operation.TargetBlocks);
        var request = EllipsoidRequest(input, before.Snapshot, operation);
        var outputRequest = EllipsoidRequest(output, after.Snapshot, operation);
        // Read-only structural resolution is shared. No planning calculator is called here.
        var sources = ExperimentalProfiles(request, before).OrderBy(p => p.Mesh.Lod).ToArray();
        var observed = ExperimentalProfiles(outputRequest, after).ToDictionary(p => p.Mesh.MeshOrdinal);
        if (HasIncompleteMdatCoverage(before.Snapshot) || HasIncompleteMdatCoverage(after.Snapshot)
            || SelectsSharedLodMesh(before.Snapshot, plan) || SelectsSharedLodMesh(after.Snapshot, plan)
            || before.Envelope.Blocks.Any(b => b.Type == "MBUF")
            || !sources.Select(p => p.Mesh.Lod).SequenceEqual(before.Snapshot.Lods.Select(l => l.Level).Order())
            || !sources.Select(p => p.Mesh.Lod).SequenceEqual(after.Snapshot.Lods.Select(l => l.Level).Order())
            || !sources.Select(p => p.Mesh.Lod).SequenceEqual(target.Buffers.Select(b => b.Lod))
            || !mutable.SetEquals(sources.SelectMany(p => new[] { p.Mesh.BlockIndex, p.Vertices.Snapshot.ResourceBlockIndex })))
            throw EllipsoidDrift("The complete ordinary buffer ownership, LODs or mutation closure changed.");
        _ = ResolveExperimentalRootMetadata(before, sources);
        _ = ResolveExperimentalRootMetadata(after, observed.Values.ToArray());
        var math = EllipsoidMath(target.LocalTransform, target.DisplacementLimit);
        if (target.Certificate != EllipsoidContractValidator.Certificate(math.Certificate)) throw EllipsoidDrift("The exact field certificate is stale.");
        var buffers = new List<EllipsoidBufferObservation>();
        var boxes = new List<ExperimentalBoxEvidence>();
        var preserved = new List<PlannedExperimentalPreservationTarget>();
        var requiredBoxes = new HashSet<(int, string)>();
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            var current = observed[source.Mesh.MeshOrdinal];
            var facts = target.Buffers.Single(b => b.Lod == source.Mesh.Lod);
            buffers.Add(VerifyEllipsoidBuffer(input, source, current, facts, math));
            var (affected, fields, evidence) = VerifyExperimentalBoxes(input, source, current, target.BoxTargets);
            if (affected.Length == 0) throw EllipsoidDrift("No affected bone contributors were independently resolved.");
            boxes.AddRange(evidence);
            foreach (var box in fields)
            {
                requiredBoxes.Add((box.ResourceBlockIndex, box.FieldPath));
                VerifyEllipsoidBoxGrowth(box);
                ReplaceExperimentalBox(current.Mesh.Block.Data, box, reverse: true);
            }
            if (KvSemanticHasher.ComputeComplete(source.Mesh.Block.Data) != KvSemanticHasher.ComputeComplete(current.Mesh.Block.Data))
                throw EllipsoidDrift("MDAT changed outside the characterized box-word allowlist.");
            AddExperimentalRenderSphereTargets(before, source, affected.Select(b => b.BoneIndex).ToHashSet(), preserved);
        }
        if (!requiredBoxes.SetEquals(target.BoxTargets.Select(b => (b.ResourceBlockIndex, b.FieldPath)))) throw EllipsoidDrift("The plan contains extra or missing box fields.");
        if (target.MaximumDisplacement != buffers.Max(b => b.MaximumDisplacement)) throw EllipsoidDrift("The target maximum displacement is stale.");
        AddExperimentalVerificationRootPreservation(before, sources, preserved);
        var sorted = preserved.OrderBy(p => p.ResourceBlockIndex).ThenBy(p => p.FieldPath, StringComparer.Ordinal).ToArray();
        if (JsonDefaults.Serialize(sorted) != JsonDefaults.Serialize(target.PreservationTargets)) throw EllipsoidDrift("The preserved field/payload inventory is incomplete or forged.");
        var preservation = sorted.Select(p => VerifyExperimentalPreservedField(after, sources, observed, p)).ToArray();
        return new(EllipsoidContractValidator.PlannedBoundaries().Where(b => b.Name != "runtime")
            .Select(b => b.Status == "not_applicable" ? b with { Status = "passed", Summary = "Independent source/output audit passed: ownership, words, topology, exact box policy and preservation." } : b).ToArray(),
            buffers, boxes.OrderBy(b => b.Target.ResourceBlockIndex).ThenBy(b => b.Target.FieldPath, StringComparer.Ordinal).ToArray(), preservation);
    }

    // Deliberately separate from PlanEllipsoidBuffer. Only pure field/frame/triangle/hash
    // primitives are shared; counts and all prescribed words are reconstructed here.
    private static EllipsoidBufferObservation VerifyEllipsoidBuffer(ArtifactContent input, Source2AffineProfile source,
        Source2AffineProfile observed, PlannedEllipsoidBuffer facts, IEllipsoidScale math)
    {
        ValidateExperimentalVertexStreams(source);
        ValidateExperimentalVertexStreams(observed);
        var v = source.Vertices.Snapshot;
        var i = source.Indices.Snapshot;
        if (source.SelectedVertices.Length != v.VertexCount || source.SelectedVertices.Where((n, index) => n != index).Any()
            || !source.SelectedVertices.SequenceEqual(observed.SelectedVertices) || source.VertexSetHash != facts.VertexSetHash
            || facts.Lod != source.Mesh.Lod || facts.ResourcePath != input.LogicalPath || facts.MeshOrdinal != source.Mesh.MeshOrdinal
            || facts.ResourceBlockIndex != source.Mesh.BlockIndex || facts.VertexBufferOrdinal != v.Ordinal || facts.IndexBufferOrdinal != i.Ordinal
            || facts.VertexResourceBlockIndex != v.ResourceBlockIndex || facts.IndexResourceBlockIndex != i.ResourceBlockIndex
            || facts.VertexBlockInputHash != v.EncodedHash || facts.IndexBlockInputHash != i.EncodedHash
            || facts.InputDecodedVertexBufferHash != v.DecodedHash || facts.DecodedIndexBufferHash != i.DecodedHash
            || facts.VertexCount != v.VertexCount || facts.OwnershipPolicy != "exclusive" || facts.PositionLayout != v.PositionLayout
            || facts.PackedFrameLayout != source.PackedFrameLayout || facts.PackedFrameLayout != observed.PackedFrameLayout
            || facts.Codec != source.Mesh.Geometry.Codec || facts.Codec != observed.Mesh.Geometry.Codec
            || facts.BeforeBounds != source.SelectionBounds || source.Indices.Snapshot != observed.Indices.Snapshot)
            throw EllipsoidDrift("Frozen buffer identities, ownership, layouts or input words differ from reopened source.");
        var expected = (byte[])source.Vertices.Decoded.Clone();
        var masks = new byte[checked(v.VertexCount * 8)];
        var weights = new byte[checked(v.VertexCount * 12)];
        var points = new Point3[v.VertexCount];
        int core = 0, transition = 0, pinned = 0, changedPositions = 0, changedFrames = 0;
        float maximum = 0;
        var memberMaskWords = math is MirroredEllipsoidScale ? new[] { new byte[v.VertexCount * 8], new byte[v.VertexCount * 8] } : null;
        var memberWeightWords = math is MirroredEllipsoidScale ? new[] { new byte[v.VertexCount * 12], new byte[v.VertexCount * 12] } : null;
        var memberCounts = new int[2, 4];
        foreach (var vertex in source.SelectedVertices)
        {
            var original = Source2GeometryAnalyzer.ReadPosition(source.Vertices, vertex);
            var result = math.Evaluate(original);
            if (math is MirroredEllipsoidScale mirrored)
            {
                // Independently reconstruct both field inventories, not the planning calculator's result.
                EllipsoidScale[] fields = [mirrored.Base, mirrored.Reflected];
                for (var side = 0; side < fields.Length; side++)
                {
                    var prescribed = fields[side].Evaluate(original);
                    BinaryPrimitives.WriteInt32LittleEndian(memberMaskWords![side].AsSpan(vertex * 8), vertex);
                    BinaryPrimitives.WriteInt32LittleEndian(memberMaskWords![side].AsSpan(vertex * 8 + 4), (int)prescribed.Membership);
                    BinaryPrimitives.WriteInt32LittleEndian(memberWeightWords![side].AsSpan(vertex * 12), vertex);
                    BinaryPrimitives.WriteInt64LittleEndian(memberWeightWords![side].AsSpan(vertex * 12 + 4), BitConverter.DoubleToInt64Bits(prescribed.Weight));
                    memberCounts[side, (int)prescribed.Membership]++;
                    if (RegionPositionWordsChanged(original, prescribed.Position)) memberCounts[side, 3]++;
                }
            }
            points[vertex] = Source2GeometryAnalyzer.ReadPosition(observed.Vertices, vertex);
            BinaryPrimitives.WriteInt32LittleEndian(masks.AsSpan(vertex * 8), vertex);
            BinaryPrimitives.WriteInt32LittleEndian(masks.AsSpan(vertex * 8 + 4), (int)result.Membership);
            BinaryPrimitives.WriteInt32LittleEndian(weights.AsSpan(vertex * 12), vertex);
            BinaryPrimitives.WriteInt64LittleEndian(weights.AsSpan(vertex * 12 + 4), BitConverter.DoubleToInt64Bits(result.Weight));
            switch (result.Membership) { case EllipsoidMembership.Core: core++; break; case EllipsoidMembership.Transition: transition++; break; default: pinned++; break; }
            if (RegionPositionWordsChanged(original, result.Position)) changedPositions++;
            maximum = Math.Max(maximum, result.MaximumDisplacement);
            var offset = checked(vertex * source.PackedFrameLayout.Stride + source.PackedFrameLayout.Offset);
            var originalWord = BinaryPrimitives.ReadUInt32LittleEndian(source.Vertices.Decoded.AsSpan(offset));
            var frame = math.TransformFrame(original, Source2PackedFrameCodec.Decode(originalWord));
            var packed = result.Membership == EllipsoidMembership.Transition ? Source2PackedFrameCodec.Encode(frame) : originalWord;
            if (packed != originalWord) changedFrames++;
            if (result.Membership != EllipsoidMembership.Pinned) WritePosition(expected, v.PositionLayout, vertex, result.Position);
            if (result.Membership == EllipsoidMembership.Transition) BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(offset), packed);
        }
        if (math is MirroredEllipsoidScale)
        {
            var reconstructed = Enumerable.Range(0, 2).Select(side => new EllipsoidFieldMask(ContentHash.Compute(memberMaskWords![side]),
                ContentHash.Compute(memberWeightWords![side]), memberCounts[side, 0], memberCounts[side, 1], memberCounts[side, 2], memberCounts[side, 3])).ToArray();
            if (reconstructed.Any(m => m.ChangedPositionCount == 0) || JsonDefaults.Serialize(reconstructed) != JsonDefaults.Serialize(facts.MirroredMasks))
                throw EllipsoidDrift("Independent mirrored field masks or per-side LOD effects differ.");
        }
        var maskHash = ContentHash.Compute(masks);
        var weightHash = ContentHash.Compute(weights);
        var positionHash = EllipsoidPositionHash(observed.Vertices.Decoded, v.PositionLayout, v.VertexCount);
        var frameHash = Source2PackedFrameCodec.HashSelected(observed.Vertices.Decoded, source.PackedFrameLayout, source.SelectedVertices);
        if (core != facts.CoreVertexCount || transition != facts.TransitionVertexCount || pinned != facts.PinnedVertexCount
            || changedPositions == 0 || changedPositions != facts.ChangedPositionCount || changedFrames != facts.ChangedFrameCount
            || maskHash != facts.MaskHash || weightHash != facts.WeightHash || maximum != facts.MaximumDisplacement
            || facts.InputPositionHash != EllipsoidPositionHash(source.Vertices.Decoded, v.PositionLayout, v.VertexCount)
            || facts.InputPackedFrameHash != Source2PackedFrameCodec.HashSelected(source.Vertices.Decoded, source.PackedFrameLayout, source.SelectedVertices)
            || positionHash != facts.ExpectedPositionHash || frameHash != facts.ExpectedPackedFrameHash
            || ContentHash.Compute(expected) != facts.ExpectedDecodedVertexBufferHash
            || observed.Vertices.Snapshot.DecodedHash != facts.ExpectedDecodedVertexBufferHash
            || !expected.AsSpan().SequenceEqual(observed.Vertices.Decoded)
            || ToAffineBounds(Bounds3.FromPoints(points)) != facts.ExpectedAfterBounds)
            throw EllipsoidDrift("Independent masks, weights, counts, displacement, bounds or prescribed position/frame words differ.");
        ValidateRegionTriangles(source, points);
        var oldGeometry = source.Mesh.GeometryAnalysis!;
        var newGeometry = observed.Mesh.GeometryAnalysis!;
        if (oldGeometry.VertexBuffers.Count != newGeometry.VertexBuffers.Count || oldGeometry.IndexBuffers.Count != newGeometry.IndexBuffers.Count)
            throw EllipsoidDrift("Participating buffer cardinality changed.");
        for (var index = 0; index < oldGeometry.VertexBuffers.Count; index++)
        {
            var old = oldGeometry.VertexBuffers[index];
            var current = newGeometry.VertexBuffers[index];
            VerifyExperimentalBufferLayout(old.Snapshot, current.Snapshot, old.Decoded, current.Decoded, index == v.Ordinal);
            if (oldGeometry.IndexBuffers[index].Snapshot != newGeometry.IndexBuffers[index].Snapshot
                || !oldGeometry.IndexBuffers[index].Indices.SequenceEqual(newGeometry.IndexBuffers[index].Indices)) throw EllipsoidDrift("An index buffer changed.");
        }
        return new(facts.Lod, facts.MeshOrdinal, facts.VertexBufferOrdinal, positionHash, frameHash, observed.Vertices.Snapshot.DecodedHash, maskHash, weightHash, maximum);
    }

    private static void VerifyEllipsoidBoxGrowth(PlannedExperimentalBoxTarget box)
    {
        // Diagnostic measurements use serialized endpoints, never the planning calculator.
        var original = box.OriginalWords;
        var output = box.ExpectedWords;
        var growth = ExperimentalGrowth(box.Storage == "min_max"
            ? CullingEnvelopeVerifier.MeasureGrowth(new Bounds3(ExperimentalPointWords(original, 0), ExperimentalPointWords(original, 3)), new Bounds3(ExperimentalPointWords(output, 0), ExperimentalPointWords(output, 3)))
            : CullingEnvelopeVerifier.MeasureGrowth(new CenterHalfExtentBounds(ExperimentalPointWords(original, 0), ExperimentalPointWords(original, 3)), new CenterHalfExtentBounds(ExperimentalPointWords(output, 0), ExperimentalPointWords(output, 3))));
        if (JsonDefaults.Serialize(growth) != JsonDefaults.Serialize(box.Growth)) throw EllipsoidDrift("Frozen box growth measurements differ from observed box words.");
    }
}
