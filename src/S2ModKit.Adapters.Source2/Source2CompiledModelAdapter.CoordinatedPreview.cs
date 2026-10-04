using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter : ICoordinatedPreviewGeometryReader
{
    public Task<CoordinatedPreviewGeometry> ReadCoordinatedPreviewGeometryAsync(ArtifactContent input, MutationPlan plan, CancellationToken token = default)
    {
        _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(plan));
        if (!IsCoordinatedPlan(plan) || input.ContentHash != plan.InputHash || ContentHash.Compute(input.Bytes.Span) != input.ContentHash)
            throw CoordinatedDrift("Preview source identity differs from its frozen plan.");
        using var parsed = Parse(input, retainGeometryAnalysis: true);
        var operation = plan.Operations.Single(); var target = operation.CoordinatedTransformTarget!;
        var sources = parsed.Envelope.Blocks.Select(b => new PlannedTargetBlock(b.Index, b.Type, ContentHash.Compute(b.Payload.Span))).ToArray();
        if (!sources.SequenceEqual(target.SourceBlocks)) throw CoordinatedDrift("Preview source block inventory differs from its plan.");
        var request = CoordinatedRequest(input, parsed.Snapshot, operation);
        var members = ResolveCoordinatedProfiles(request, parsed);
        if (!members.Select(m => (m.MemberId, m.Profile.Mesh.Lod, m.Profile.Mesh.MeshOrdinal, m.Profile.Vertices.Snapshot.Ordinal))
            .SequenceEqual(target.Buffers.Select(b => (b.MemberId, b.Lod, b.MeshOrdinal, b.VertexBufferOrdinal))))
            throw CoordinatedDrift("Preview member/buffer coverage differs from its plan.");
        var lods = new List<CoordinatedPreviewLodGeometry>(); long points = 0, indices = 0;
        foreach (var lod in parsed.Snapshot.Lods)
        {
            var buffers = new List<CoordinatedPreviewBufferGeometry>();
            foreach (var member in members.Where(m => m.Profile.Mesh.Lod == lod.Level))
            {
                token.ThrowIfCancellationRequested();
                var p = member.Profile; var v = p.Vertices.Snapshot; var i = p.Indices.Snapshot;
                var facts = target.Buffers.Single(b => b.MemberId == member.MemberId && b.Lod == lod.Level);
                if (p.Mesh.BlockIndex != facts.ResourceBlockIndex || v.ResourceBlockIndex != facts.VertexResourceBlockIndex
                    || i.ResourceBlockIndex != facts.IndexResourceBlockIndex || i.Ordinal != facts.IndexBufferOrdinal
                    || v.EncodedHash != facts.VertexBlockInputHash || i.EncodedHash != facts.IndexBlockInputHash
                    || v.DecodedHash != facts.InputDecodedVertexBufferHash || i.DecodedHash != facts.DecodedIndexBufferHash
                    || v.PositionLayout != facts.PositionLayout || p.PackedFrameLayout != facts.PackedFrameLayout
                    || v.VertexCount != facts.VertexCount || p.VertexSetHash != facts.VertexSetHash || p.Mesh.Geometry.Codec != facts.Codec)
                    throw CoordinatedDrift("Preview layout, ownership or decoded identities differ from frozen source facts.");
                var calls = target.CoordinatedTransform.Members.Single(m => m.MemberId == member.MemberId).Lods.Single(l => l.Lod == lod.Level).DrawCallIds;
                var triangles = calls.SelectMany(id =>
                {
                    var draw = p.Mesh.GeometryAnalysis!.DrawCalls.Single(d => d.Snapshot.DrawCallId == id).Snapshot;
                    var call = request.SelectedDrawCalls.Single(c => c.DrawCallId == id);
                    return Enumerable.Range(checked((int)call.IndexStart), checked((int)call.IndexCount))
                        .Select(n => checked((int)p.Indices.Indices[n] + draw.BaseVertex));
                }).ToArray();
                points += v.VertexCount; indices += triangles.Length; CheckPreviewBudget(points, indices);
                buffers.Add(new(member.MemberId, lod.Level, p.Mesh.MeshOrdinal, v.Ordinal, calls.ToArray(),
                    Enumerable.Range(0, v.VertexCount).Select(n => Source2GeometryAnalyzer.ReadPosition(p.Vertices, n)).ToArray(), triangles));
            }
            var context = new List<CoordinatedPreviewContext>();
            foreach (var mesh in lod.Meshes.OrderBy(m => m.MeshOrdinal))
            {
                token.ThrowIfCancellationRequested();
                var analysis = parsed.MeshesByOrdinal[mesh.MeshOrdinal].GeometryAnalysis
                    ?? throw Errors.Unsupported("COORDINATED_PREVIEW_GEOMETRY_UNAVAILABLE", "Excluded context cannot be decoded completely.", "No partial preview is produced.");
                foreach (var buffer in analysis.VertexBuffers.OrderBy(b => b.Snapshot.Ordinal))
                {
                    if (buffers.Any(b => b.MeshOrdinal == mesh.MeshOrdinal && b.VertexBufferOrdinal == buffer.Snapshot.Ordinal)) continue;
                    points += buffer.Snapshot.VertexCount; CheckPreviewBudget(points, indices);
                    var ids = mesh.Geometry!.DrawCalls.Where(d => d.VertexBufferOrdinal == buffer.Snapshot.Ordinal).Select(d => d.DrawCallId).Order(StringComparer.Ordinal).ToArray();
                    context.Add(new(mesh.MeshOrdinal, buffer.Snapshot.Ordinal,
                        Enumerable.Range(0, buffer.Snapshot.VertexCount).Select(n => Source2GeometryAnalyzer.ReadPosition(buffer, n)).ToArray())
                    {
                        DrawCallIds = ids,
                        MaterialPaths = mesh.DrawCalls.Where(d => ids.Contains(d.Id)).Select(d => d.MaterialPath).Distinct().Order(StringComparer.Ordinal).ToArray(),
                        SourceLabel = mesh.MechanicalLineage?.SourceLabel,
                    });
                }
            }
            lods.Add(new(lod.Level, buffers, context));
        }
        return Task.FromResult(new CoordinatedPreviewGeometry(input.ContentHash, plan.Fingerprint, lods));
    }
}
