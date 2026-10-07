using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static DirectionalSourceBuffer[] ReadDirectionalVerificationContext(ArtifactContent input, ParsedModel before, ParsedModel after,
        Model model, IReadOnlyList<CoordinatedResolvedBuffer> members, bool pairedPreservation = false)
    {
        var selection = members.ToDictionary(m => (m.Profile.Mesh.MeshOrdinal, m.Profile.Vertices.Snapshot.Ordinal), m => m.MemberId);
        var masks = ExperimentalArray(model.Data, "m_refMeshGroupMasks");
        if (masks.Count != before.MeshesByOrdinal.Count || !model.Data.TryGetValue("m_nDefaultMeshGroupMask", out var defaultMask))
            throw DirectionalDrift("Complete authored view masks are absent.");
        var output = new List<DirectionalSourceBuffer>();
        foreach (var mesh in before.MeshesByOrdinal.Values.OrderBy(m => m.Lod).ThenBy(m => m.MeshOrdinal))
        {
            var current = after.MeshesByOrdinal[mesh.MeshOrdinal];
            var geometry = mesh.GeometryAnalysis ?? throw DirectionalDrift("Source context geometry is absent.");
            var actual = current.GeometryAnalysis ?? throw DirectionalDrift("Output context geometry is absent.");
            if (geometry.VertexBuffers.Count != geometry.IndexBuffers.Count || geometry.VertexBuffers.Count != actual.VertexBuffers.Count || HasMorphData(mesh.Block.Data))
                throw DirectionalDrift("Context storage/contributor layout is incomplete.");
            var metadata = pairedPreservation
                ? Source2TransformMetadataAnalyzer.AnalyzePairedPreservationBuffers(mesh.Descriptor, mesh.Block.Data, geometry, "independent paired context")
                : Source2TransformMetadataAnalyzer.AnalyzeVisualPreservationBuffers(mesh.Descriptor, mesh.Block.Data, geometry, "independent directional context");
            var remap = model.GetRemapTable(mesh.MeshOrdinal);
            if (remap is null || remap.Any(i => i < 0 || i >= model.Skeleton.Bones.Length) || metadata.BoneBounds.Any(b => b.BoneIndex >= remap.Length))
                throw DirectionalDrift("Complete root/render contributor mapping is absent.");
            var baseOffset = 0;
            foreach (var buffer in geometry.VertexBuffers)
            {
                var snapshot = buffer.Snapshot; var key = (mesh.MeshOrdinal, snapshot.Ordinal);
                var observedBuffer = actual.VertexBuffers[snapshot.Ordinal]; var frame = buffer.PackedFrameLayout;
                var selected = selection.TryGetValue(key, out var member);
                VerifyExperimentalBufferLayout(snapshot, observedBuffer.Snapshot, buffer.Decoded, observedBuffer.Decoded, selected);
                if (geometry.IndexBuffers[snapshot.Ordinal].Snapshot != actual.IndexBuffers[snapshot.Ordinal].Snapshot
                    || !geometry.IndexBuffers[snapshot.Ordinal].Indices.SequenceEqual(actual.IndexBuffers[snapshot.Ordinal].Indices)
                    || frame != observedBuffer.PackedFrameLayout || (selected && frame is null)) throw DirectionalDrift("Context index/frame storage changed.");
                var contributors = metadata.BoneBounds.GroupBy(b => remap[b.BoneIndex]).ToDictionary(g => g.Key,
                    g => g.SelectMany(b => b.InfluencedVertices).Where(i => i >= baseOffset && i < baseOffset + snapshot.VertexCount)
                        .Select(i => i - baseOffset).Distinct().Order().ToArray());
                var ids = Enumerable.Range(0, snapshot.VertexCount).ToArray();
                var skinning = MutationPlanJson.ComputeDirectionalFactsHash(new
                {
                    Descriptor = KvSemanticHasher.ComputeComplete(ExperimentalArray(mesh.Descriptor, "m_vertexBuffers")[snapshot.Ordinal]),
                    Contributors = contributors.OrderBy(p => p.Key).Select(p => new { Root = p.Key, Vertices = p.Value }).ToArray(),
                    Attributes = frame is null ? snapshot.DecodedHash : DirectionalUnchangedWords(buffer.Decoded, snapshot.PositionLayout, frame),
                });
                var facts = new DirectionalContextBuffer(mesh.Lod, mesh.MeshOrdinal, snapshot.Ordinal, mesh.BlockIndex, snapshot.ResourceBlockIndex,
                    geometry.IndexBuffers[snapshot.Ordinal].Snapshot.ResourceBlockIndex, snapshot.VertexCount, input.LogicalPath, selected,
                    DirectionalMask(masks[mesh.MeshOrdinal]), DirectionalMask(defaultMask), snapshot.DecodedHash,
                    DirectionalPositionWords(buffer.Decoded, snapshot.PositionLayout, ids),
                    frame is null ? snapshot.DecodedHash : Source2PackedFrameCodec.HashSelected(buffer.Decoded, frame, ids),
                    geometry.IndexBuffers[snapshot.Ordinal].Snapshot.DecodedHash, skinning, DirectionalContractValidator.VertexSetHash(remap));
                output.Add(new(facts, member, snapshot.PositionLayout, frame, buffer.Decoded, observedBuffer.Decoded,
                    ids.Select(i => Source2GeometryAnalyzer.ReadPosition(buffer, i)).ToArray(),
                    ids.Select(i => Source2GeometryAnalyzer.ReadPosition(observedBuffer, i)).ToArray(), contributors));
                baseOffset = checked(baseOffset + snapshot.VertexCount);
            }
        }
        if (output.GroupBy(c => c.Facts.VertexResourceBlockIndex).Any(g => g.Count() > 1 && g.Any(c => c.Facts.Selected)))
            throw DirectionalDrift("Selected storage aliases excluded context.");
        return output.ToArray();
    }

    internal static PlannedDirectionalProtection AuditDirectionalProtection(DirectionalProtection intent, IReadOnlyList<DirectionalSourceBuffer> context,
        ContentHash skeleton, IReadOnlyList<string> names)
    {
        var observations = new List<PlannedDirectionalAssertion>();
        foreach (var assertion in intent.Assertions)
        {
            var records = new List<DirectionalProtectedSet>();
            if (assertion is DirectionalVertexAssertion explicitVertices)
            {
                foreach (var source in context.Where(c => c.Facts.Selected))
                {
                    var set = explicitVertices.Sets.Single(s => s.MemberId == source.MemberId && s.Lod == source.Facts.Lod);
                    if (set.SourceDecodedBufferHash != source.Facts.DecodedBufferHash || set.VertexCount != set.VertexIndices.Count
                        || set.VertexSetHash != DirectionalContractValidator.VertexSetHash(set.VertexIndices)) throw DirectionalDrift("Explicit protected source set drifted.");
                    records.Add(AuditDirectionalFixedWords(source, set.VertexIndices));
                }
            }
            else if (assertion is DirectionalBoneAssertion bone)
            {
                if (bone.RootSkeletonHash != skeleton || (uint)bone.BoneIndex >= (uint)names.Count || names[bone.BoneIndex] != bone.BoneName)
                    throw DirectionalDrift("Protected root skeleton/name/index drifted.");
                foreach (var source in context) records.Add(AuditDirectionalFixedWords(source, source.RootContributors.GetValueOrDefault(bone.BoneIndex) ?? []));
                foreach (var expected in bone.Lods)
                {
                    var lodRecords = records.Where(r => r.Lod == expected.Lod).ToArray();
                    if (lodRecords.Sum(r => (long)r.VertexCount) != expected.ContributorCount
                        || DirectionalContractValidator.ContributorSetHash(lodRecords) != expected.ContributorSetHash) throw DirectionalDrift("Complete protected bone contributor closure drifted.");
                }
            }
            else throw DirectionalDrift("Unknown protected assertion.");
            if (records.Sum(r => (long)r.VertexCount) == 0) throw DirectionalDrift("Empty protected assertion.");
            observations.Add(new(assertion.AssertionId, records, DirectionalContractValidator.ContributorSetHash(records)));
        }
        var union = context.Select(c => AuditDirectionalFixedWords(c, observations.SelectMany(a => a.Sets)
            .Where(s => s.Lod == c.Facts.Lod && s.MeshOrdinal == c.Facts.MeshOrdinal && s.VertexBufferOrdinal == c.Facts.VertexBufferOrdinal)
            .SelectMany(s => s.VertexIndices).Distinct().Order().ToArray())).ToArray();
        foreach (var lod in context.GroupBy(c => c.Facts.Lod))
            if (!union.Any(s => s.Lod == lod.Key && s.VertexCount > 0 && lod.Any(c => c.Facts.Selected && c.Facts.MeshOrdinal == s.MeshOrdinal && c.Facts.VertexBufferOrdinal == s.VertexBufferOrdinal)))
                throw DirectionalDrift("A selected protected LOD union is empty.");
        return new(observations, union, MutationPlanJson.ComputeDirectionalFactsHash(union));
    }

    private static DirectionalProtectedSet AuditDirectionalFixedWords(DirectionalSourceBuffer source, IReadOnlyList<int> indices)
    {
        if (!indices.SequenceEqual(indices.Distinct().Order()) || indices.Any(i => i < 0 || i >= source.Facts.VertexCount)
            || (indices.Count > 0 && source.Frame is null)) throw DirectionalDrift("Protected indices/frame profile are unsupported.");
        foreach (var vertex in indices)
        {
            var position = checked(vertex * source.Position.Stride + source.Position.Offset);
            var frame = checked(vertex * source.Frame!.Stride + source.Frame.Offset);
            if (!source.Before.AsSpan(position, 12).SequenceEqual(source.After.AsSpan(position, 12)) || !source.Before.AsSpan(frame, 4).SequenceEqual(source.After.AsSpan(frame, 4)))
                throw DirectionalDrift($"Protected source words moved: LOD {source.Facts.Lod}, mesh {source.Facts.MeshOrdinal}, buffer {source.Facts.VertexBufferOrdinal}, vertex {vertex}.");
        }
        return new(source.Facts.Lod, source.Facts.MeshOrdinal, source.Facts.VertexBufferOrdinal, source.Facts.DecodedBufferHash,
            indices, DirectionalContractValidator.VertexSetHash(indices), indices.Count,
            DirectionalPositionWords(source.Before, source.Position, indices), DirectionalPositionWords(source.After, source.Position, indices),
            indices.Count == 0 ? ContentHash.Compute([]) : Source2PackedFrameCodec.HashSelected(source.Before, source.Frame!, indices),
            indices.Count == 0 ? ContentHash.Compute([]) : Source2PackedFrameCodec.HashSelected(source.After, source.Frame!, indices));
    }

    internal static DirectionalCoincidenceLod[] AuditDirectionalCoincidences(IReadOnlyList<DirectionalSourceBuffer> context)
    {
        static (uint X, uint Y, uint Z) Key(Point3 point) =>
            (point.X == 0 ? 0 : BitConverter.SingleToUInt32Bits(point.X), point.Y == 0 ? 0 : BitConverter.SingleToUInt32Bits(point.Y), point.Z == 0 ? 0 : BitConverter.SingleToUInt32Bits(point.Z));
        var observations = new List<DirectionalCoincidenceLod>();
        foreach (var lod in context.GroupBy(c => c.Facts.Lod).OrderBy(g => g.Key))
        {
            var records = lod.OrderBy(c => c.Facts.MeshOrdinal).ThenBy(c => c.Facts.VertexBufferOrdinal)
                .SelectMany(c => Enumerable.Range(0, c.Facts.VertexCount).Select(i => new
                { Mesh = c.Facts.MeshOrdinal, Buffer = c.Facts.VertexBufferOrdinal, Vertex = i, c.Facts.Selected, Source = c.SourcePoints[i], Output = c.FinalPoints[i] })).ToArray();
            var cohorts = records.GroupBy(r => Key(r.Source)).Where(g => g.Count() > 1).OrderBy(g => g.Key).ToArray();
            long pairs = 0;
            foreach (var cohort in cohorts)
            {
                pairs = checked(pairs + (long)cohort.Count() * (cohort.Count() - 1) / 2);
                if (cohort.Any(r => !r.Selected) && cohort.Any(r => r.Selected && Key(r.Output) != Key(r.Source))) throw DirectionalDrift("A moving selected record has an excluded exact mate.");
                if (cohort.Select(r => Key(r.Output)).Distinct().Count() != 1) throw DirectionalDrift("Exact coincident copies disagree in the observed output.");
            }
            var pairHash = MutationPlanJson.ComputeDirectionalFactsHash(cohorts.Select(g => new
            { X = g.Key.X, Y = g.Key.Y, Z = g.Key.Z, Records = g.Select(r => new { r.Mesh, r.Buffer, r.Vertex }).ToArray() }).ToArray());
            var sourceHash = MutationPlanJson.ComputeDirectionalFactsHash(records.Select(r => new
            { r.Mesh, r.Buffer, r.Vertex, X = BitConverter.SingleToUInt32Bits(r.Source.X), Y = BitConverter.SingleToUInt32Bits(r.Source.Y), Z = BitConverter.SingleToUInt32Bits(r.Source.Z) }).ToArray());
            observations.Add(new(lod.Key, sourceHash, pairHash, records.Length, pairs,
                records.Count(r => r.Selected && (BitConverter.SingleToUInt32Bits(r.Source.X) != BitConverter.SingleToUInt32Bits(r.Output.X)
                    || BitConverter.SingleToUInt32Bits(r.Source.Y) != BitConverter.SingleToUInt32Bits(r.Output.Y)
                    || BitConverter.SingleToUInt32Bits(r.Source.Z) != BitConverter.SingleToUInt32Bits(r.Output.Z))), 0));
        }
        return observations.ToArray();
    }
}
