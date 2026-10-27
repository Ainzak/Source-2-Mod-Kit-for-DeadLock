using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter : IPositionAuthoringSourceReader
{
    public uint EncodePositionAuthoringFrame(TangentFrame frame) => Source2PackedFrameCodec.Encode(frame);

    public Task<PositionAuthoringSource> ReadPositionAuthoringSourceAsync(ArtifactContent input, IReadOnlyList<CoordinatedMember> members, CancellationToken token = default)
        => ReadDirectionalAuthoringFacts(() => ReadPositionAuthoringSourceCore(input, members, token));

    private Task<PositionAuthoringSource> ReadPositionAuthoringSourceCore(ArtifactContent input, IReadOnlyList<CoordinatedMember> members, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ContentHash.Compute(input.Bytes.Span) != input.ContentHash) throw PositionAuthoringJson.Invalid("IMPORT_SOURCE_DRIFT", "Immutable source hash differs.");
        using var parsed = Parse(input, retainGeometryAnalysis: true);
        if (HasIncompleteMdatCoverage(parsed.Snapshot) || parsed.Envelope.Blocks.Any(b => b.Type == "MBUF")
            || parsed.MeshesByOrdinal.Values.Any(m => BitOperations.PopCount(m.LodMask) != 1))
            throw PositionAuthoringJson.Invalid("IMPORT_LAYOUT_UNSUPPORTED", "Complete exclusive ordinary all-LOD source is required.");
        var model = parsed.Resource.Blocks.OfType<Model>().Single();
        if (HasMorphData(model.Data) || model.Data.ContainsKey("m_vMinBounds") || model.Data.ContainsKey("m_vMaxBounds"))
            throw PositionAuthoringJson.Invalid("IMPORT_LAYOUT_UNSUPPORTED", "Root bounds/morph data are outside the import profile.");
        var selected = new List<CoordinatedResolvedBuffer>();
        foreach (var member in members)
        {
            if (!member.Lods.Select(l => l.Lod).SequenceEqual(parsed.Snapshot.Lods.Select(l => l.Level).Order()))
                throw PositionAuthoringJson.Invalid("IMPORT_LOD_INCOMPLETE", "Every member must cover every present LOD.");
            foreach (var map in member.Lods)
            {
                var calls = parsed.Snapshot.Lods.SelectMany(l => l.Meshes.SelectMany(m => m.DrawCalls.Where(d => map.DrawCallIds.Contains(d.Id, StringComparer.Ordinal))
                    .Select(d => new SelectedDrawCall(l.Level, m.ResourcePath, m.MeshOrdinal, m.ResourceBlockIndex, d.Id, d.MaterialPath, d.DrawCallOrdinal, d.IndexStart, d.IndexCount)))).ToArray();
                if (calls.Length != map.DrawCallIds.Count || calls.Any(c => c.Lod != map.Lod) || calls.Select(c => c.MeshOrdinal).Distinct().Count() != 1)
                    throw PositionAuthoringJson.Invalid("IMPORT_MEMBER_INVALID", "Selection is incomplete or ambiguous.");
                var reader = new TransformComponentOperation
                {
                    Version = 5,
                    Granularity = "draw_call_vertices",
                    RuntimeMetadataPolicy = new("preserve_unverified", 1),
                    ExpectedVerticesByLod = member.Lods.ToDictionary(l => l.Lod.ToString(CultureInfo.InvariantCulture), l => l.ExpectedVertices)
                };
                var profile = CreateRootBufferProfile(new(input, parsed.Snapshot, reader, calls), parsed, calls, preserveAuthoredEnvelopes: true, completeOrdinaryBuffer: true, pairedPreservation: true);
                ValidateExperimentalVertexStreams(profile, pairedPreservation: true);
                selected.Add(new(member.MemberId, profile));
            }
        }
        if (selected.Select(s => s.Profile.Vertices.Snapshot.ResourceBlockIndex).Distinct().Count() != selected.Count)
            throw PositionAuthoringJson.Invalid("IMPORT_MEMBER_ALIAS", "Selected members alias the same storage.");
        var ordered = selected.OrderBy(s => s.Profile.Mesh.Lod).ThenBy(s => s.Profile.Mesh.MeshOrdinal).ThenBy(s => s.Profile.Vertices.Snapshot.Ordinal).ToArray();
        var context = ReadDirectionalVerificationContext(input, parsed, parsed, model, ordered, pairedPreservation: true);
        var procedural = ResolvePairedProcedural(parsed, model, context);
        var preservation = new List<PlannedExperimentalPreservationTarget>(); AddExperimentalBlockPreservation(parsed, preservation);
        var rootSpheres = ExperimentalArray(ExperimentalCollection(model.Data, "m_modelSkeleton"), "m_boneSphere");
        var buffers = new List<PositionSourceBuffer>();
        foreach (var buffer in context)
        {
            token.ThrowIfCancellationRequested();
            var facts = buffer.Facts; var geometry = parsed.MeshesByOrdinal[facts.MeshOrdinal].GeometryAnalysis!;
            var ranges = parsed.Snapshot.Lods.Single(l => l.Level == facts.Lod).Meshes.Single(m => m.MeshOrdinal == facts.MeshOrdinal).DrawCalls.ToDictionary(d => d.Id, StringComparer.Ordinal);
            var indices = geometry.DrawCalls.Where(c => c.Snapshot.VertexBufferOrdinal == facts.VertexBufferOrdinal).OrderBy(c => ranges[c.Snapshot.DrawCallId].DrawCallOrdinal).SelectMany(c =>
            {
                var range = ranges[c.Snapshot.DrawCallId];
                return Enumerable.Range(checked((int)range.IndexStart), checked((int)range.IndexCount)).Select(i => checked((int)geometry.IndexBuffers[c.Snapshot.IndexBufferOrdinal].Indices[i] + c.Snapshot.BaseVertex));
            }).ToArray();
            var words = buffer.Frame is null || !facts.Selected ? [] : Enumerable.Range(0, facts.VertexCount)
                .Select(i => BinaryPrimitives.ReadUInt32LittleEndian(buffer.Before.AsSpan(i * buffer.Frame.Stride + buffer.Frame.Offset, 4))).ToArray();
            var flags = buffer.RootContributors.Where(r => procedural.RootBones[r.Key].Procedural).SelectMany(r => r.Value).Distinct().Order().ToArray();
            var gates = new List<PositionBoundGate>();
            if (facts.Selected)
            {
                var p = ordered.Single(s => s.Profile.Mesh.MeshOrdinal == facts.MeshOrdinal && s.Profile.Vertices.Snapshot.Ordinal == facts.VertexBufferOrdinal).Profile;
                var remap = model.GetRemapTable(facts.MeshOrdinal)!;
                foreach (var bone in p.Metadata.BoneBounds)
                {
                    var ids = bone.InfluencedVertices.Where(i => i >= p.BufferBaseOffset && i < p.BufferBaseOffset + facts.VertexCount).Select(i => i - p.BufferBaseOffset).ToArray();
                    gates.Add(new($"renderBone:{bone.BoneIndex}/root:{remap[bone.BoneIndex]}", ids,
                        bone.LocalBoundsSize.X > 0 && bone.LocalBoundsSize.Y > 0 && bone.LocalBoundsSize.Z > 0 && bone.SphereRadius > 0
                        && ExperimentalFloat(rootSpheres[remap[bone.BoneIndex]]) > 0));
                }
                if (p.Metadata.SceneBounds.Max.X <= p.Metadata.SceneBounds.Min.X || p.Metadata.SceneBounds.Max.Y <= p.Metadata.SceneBounds.Min.Y || p.Metadata.SceneBounds.Max.Z <= p.Metadata.SceneBounds.Min.Z)
                    throw PositionAuthoringJson.Invalid("IMPORT_BOX_UNSUPPORTED", "Selected scene box is not strictly positive.");
            }
            buffers.Add(new(buffer.MemberId, facts, buffer.SourcePoints, indices, words, words.Select(Source2PackedFrameCodec.Decode).ToArray(), flags, gates));
        }
        return Task.FromResult(new PositionAuthoringSource(input.ContentHash, members, buffers, procedural,
            parsed.Envelope.Blocks.Select(b => new PlannedTargetBlock(b.Index, b.Type, ContentHash.Compute(b.Payload.Span))).ToArray()));
    }
}
