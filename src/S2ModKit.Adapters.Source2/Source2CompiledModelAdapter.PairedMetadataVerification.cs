using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static (ExperimentalBoxEvidence[] Boxes, DirectionalBoxClosure[] Closures, ExperimentalPreservationEvidence[] Preserved)
        AuditPairedMetadata(ParsedModel source, ParsedModel observed, CoordinatedResolvedBuffer[] members,
            Dictionary<(string MemberId, int Lod), CoordinatedResolvedBuffer> actual,
            DirectionalSourceBuffer[] context, PlannedPairedTransformTarget target)
    {
        var boxes = new List<ExperimentalBoxEvidence>(); var closures = new List<DirectionalBoxClosure>();
        var preserved = new List<PlannedExperimentalPreservationTarget>();
        foreach (var meshGroup in members.GroupBy(m => m.Profile.Mesh.MeshOrdinal))
        {
            var original = meshGroup.First().Profile;
            var current = actual[(meshGroup.First().MemberId, original.Mesh.Lod)].Profile;
            VerifyCoordinatedMeshStorage(original, current, meshGroup.Select(m => m.Profile.Vertices.Snapshot.Ordinal).ToHashSet());
            var selected = context.Where(c => c.Facts.Selected && c.Facts.MeshOrdinal == original.Mesh.MeshOrdinal)
                .SelectMany(c => Enumerable.Range(0, c.Facts.VertexCount)
                    .Where(i => !c.Before.AsSpan(i * c.Position.Stride + c.Position.Offset, 12).SequenceEqual(c.After.AsSpan(i * c.Position.Stride + c.Position.Offset, 12))
                        || !c.Before.AsSpan(i * c.Frame!.Stride + c.Frame.Offset, 4).SequenceEqual(c.After.AsSpan(i * c.Frame.Stride + c.Frame.Offset, 4)))
                    .Select(i => i + meshGroup.Single(m => m.Profile.Vertices.Snapshot.Ordinal == c.Facts.VertexBufferOrdinal).Profile.BufferBaseOffset)).ToHashSet();
            var affected = original.Metadata.BoneBounds.Where(b => b.InfluencedVertices.Any(selected.Contains)).ToArray();
            if (affected.Length == 0) throw PairedDrift("Affected contributor closure is absent.");
            var required = affected.Select(b => $"m_skeleton.m_bones[{b.BoneIndex}].m_bbox.m_vecCenter+m_vecSize")
                .Prepend("m_sceneObjects[0].m_vMinBounds+m_vMaxBounds").ToHashSet(StringComparer.Ordinal);
            var meshTargets = target.BoxTargets.Where(b => b.ResourceBlockIndex == original.Mesh.BlockIndex).ToArray();
            if (meshTargets.Length != required.Count || !required.SetEquals(meshTargets.Select(b => b.FieldPath))) throw PairedDrift("Unique scene/bone target inventory differs from complete source closure.");
            foreach (var box in meshTargets)
            {
                var beforeWords = ReadExperimentalBox(original.Mesh.Block.Data, box);
                var afterWords = ReadExperimentalBox(current.Mesh.Block.Data, box);
                if (!beforeWords.SequenceEqual(box.OriginalWords) || !afterWords.SequenceEqual(box.ExpectedWords)) throw PairedDrift("Original/observed box words differ from the target.");
                float[] matrix; int[] membership; Point3[] points; EnvelopeVerification policy;
                string storage; string space;
                if (box.FieldPath == "m_sceneObjects[0].m_vMinBounds+m_vMaxBounds")
                {
                    storage = "min_max"; space = "model"; matrix = ExperimentalIdentityMatrix;
                    membership = Enumerable.Range(0, current.AllBeforePositions.Length).ToArray(); points = current.AllBeforePositions;
                    policy = CullingEnvelopeVerifier.Verify(new Bounds3(ExperimentalPointWords(beforeWords, 0), ExperimentalPointWords(beforeWords, 3)), points,
                        new Bounds3(ExperimentalPointWords(afterWords, 0), ExperimentalPointWords(afterWords, 3)));
                }
                else
                {
                    storage = "center_half_extent"; space = "render_inverse_bind";
                    var bone = affected.Single(b => box.FieldPath == $"m_skeleton.m_bones[{b.BoneIndex}].m_bbox.m_vecCenter+m_vecSize");
                    matrix = bone.InverseBindPose.ToArray(); membership = bone.InfluencedVertices.ToArray();
                    var rawBone = ExperimentalArray(ExperimentalCollection(original.Mesh.Block.Data, "m_skeleton"), "m_bones")[bone.BoneIndex];
                    if (!Words(matrix).SequenceEqual(Words(ExperimentalArray(rawBone, "m_invBindPose").Values.Select(ExperimentalFloat).ToArray()))) throw PairedDrift("Serialized inverse bind identity differs.");
                    points = membership.SelectMany(i =>
                    {
                        var enclosure = AffinePointEnclosure.Enclose(current.AllBeforePositions[i], matrix);
                        return new[] { enclosure.Min, enclosure.Max };
                    }).ToArray();
                    policy = CullingEnvelopeVerifier.Verify(new CenterHalfExtentBounds(ExperimentalPointWords(beforeWords, 0), ExperimentalPointWords(beforeWords, 3)), points,
                        new CenterHalfExtentBounds(ExperimentalPointWords(afterWords, 0), ExperimentalPointWords(afterWords, 3)));
                }
                if (policy.Status != EnvelopeVerificationStatus.Passed || box.Storage != storage || box.CoordinateSpace != space
                    || box.ContributorCount != membership.Length || box.CoordinateMatrixHash != HashExperimentalWords(matrix) || !box.CoordinateMatrixWords.SequenceEqual(Words(matrix)))
                    throw PairedDrift($"Independent prescribed box policy/matrix/count failed for {box.FieldPath}: {policy.Status}.");
                var rows = new List<DirectionalBoxContributor>(); var start = 0;
                foreach (var buffer in context.Where(c => c.Facts.MeshOrdinal == original.Mesh.MeshOrdinal))
                {
                    var local = membership.Where(i => i >= start && i < start + buffer.Facts.VertexCount).Select(i => i - start).ToArray();
                    rows.Add(new(buffer.Facts.Lod, buffer.Facts.MeshOrdinal, buffer.Facts.VertexBufferOrdinal, local.Length,
                        DirectionalContractValidator.VertexSetHash(local), DirectionalPositionWords(buffer.Before, buffer.Position, Enumerable.Range(0, buffer.Facts.VertexCount).ToArray()),
                        DirectionalPositionWords(buffer.After, buffer.Position, Enumerable.Range(0, buffer.Facts.VertexCount).ToArray())));
                    start = checked(start + buffer.Facts.VertexCount);
                }
                if (start != current.AllBeforePositions.Length || rows.Sum(r => (long)r.ContributorCount) != membership.Length) throw PairedDrift("Unindexed/unchanged box contributor rows are incomplete.");
                var closureHash = MutationPlanJson.ComputeDirectionalFactsHash(rows);
                if (closureHash != box.ContributorSetHash) throw PairedDrift("Complete box contributor identity differs.");
                closures.Add(new(box.ResourceBlockIndex, box.FieldPath, original.Mesh.Lod, original.Mesh.MeshOrdinal, rows, closureHash));
                VerifyEllipsoidBoxGrowth(box);
                boxes.Add(new(box, afterWords, "passed"));
            }
            // Read-only inventory helpers enumerate every authored preserved sphere; they do not derive replacements.
            AddExperimentalRenderSphereTargets(source, original, affected.Select(b => b.BoneIndex).ToHashSet(), preserved);
            foreach (var box in meshTargets) ReplaceExperimentalBox(current.Mesh.Block.Data, box, reverse: true);
            if (KvSemanticHasher.ComputeComplete(original.Mesh.Block.Data) != KvSemanticHasher.ComputeComplete(current.Mesh.Block.Data))
                throw PairedDrift("MDAT changed outside the independently prescribed box words.");
        }
        if (boxes.Count != target.BoxTargets.Count) throw PairedDrift("Box inventory is incomplete.");
        var uniqueSource = members.Select(m => m.Profile).DistinctBy(p => p.Mesh.MeshOrdinal).ToArray();
        ReadPairedVerificationRootPreservation(source, context, preserved);
        var sortedPreserved = preserved.OrderBy(p => p.ResourceBlockIndex).ThenBy(p => p.FieldPath, StringComparer.Ordinal).ToArray();
        SamePaired(sortedPreserved, target.PreservationTargets, "full preservation inventory");
        var uniqueOutput = actual.Values.Select(m => m.Profile).DistinctBy(p => p.Mesh.MeshOrdinal).ToDictionary(p => p.Mesh.MeshOrdinal);
        var observedPreservation = sortedPreserved.Select(p => VerifyExperimentalPreservedField(observed, uniqueSource, uniqueOutput, p)).ToArray();
        return (boxes.OrderBy(b => b.Target.ResourceBlockIndex).ThenBy(b => b.Target.FieldPath, StringComparer.Ordinal).ToArray(),
            closures.OrderBy(c => c.ResourceBlockIndex).ThenBy(c => c.FieldPath, StringComparer.Ordinal).ToArray(), observedPreservation);
    }
    private static void ReadPairedVerificationRootPreservation(ParsedModel source, DirectionalSourceBuffer[] context,
        List<PlannedExperimentalPreservationTarget> preserved)
    {
        var model = source.Resource.Blocks.OfType<ValveResourceFormat.ResourceTypes.Model>().Single();
        var skeleton = ExperimentalCollection(model.Data, "m_modelSkeleton"); var spheres = ExperimentalArray(skeleton, "m_boneSphere");
        var block = source.Resource.Blocks.Select((b, i) => (b, i)).Single(row => ReferenceEquals(row.b, model)).i;
        var affected = new HashSet<int>();
        foreach (var c in context.Where(c => c.Facts.Selected))
        {
            var changed = Enumerable.Range(0, c.Facts.VertexCount).Where(i =>
                !c.Before.AsSpan(i * c.Position.Stride + c.Position.Offset, 12).SequenceEqual(c.After.AsSpan(i * c.Position.Stride + c.Position.Offset, 12))
                || !c.Before.AsSpan(i * c.Frame!.Stride + c.Frame.Offset, 4).SequenceEqual(c.After.AsSpan(i * c.Frame.Stride + c.Frame.Offset, 4))).ToHashSet();
            foreach (var root in c.RootContributors.Where(row => row.Value.Any(changed.Contains))) affected.Add(root.Key);
        }
        for (var i = 0; i < spheres.Count; i++)
        {
            var radius = ExperimentalFloat(spheres[i]); ValidateExperimentalRadius(radius, affected.Contains(i));
            preserved.Add(new(block, $"m_modelSkeleton.m_boneSphere[{i}]", "root_sphere", affected.Contains(i) ? "affected" : "resource",
                "preserve_unverified", ContentHash.Compute(source.Envelope.Blocks[block].Payload.Span), Words(radius)));
        }
        AddExperimentalBlockPreservation(source, preserved);
    }

}
