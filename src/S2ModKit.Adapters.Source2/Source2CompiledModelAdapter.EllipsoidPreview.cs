using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    public Task<EllipsoidPreviewGeometry> ReadEllipsoidPreviewGeometryAsync(ArtifactContent input, MutationPlan plan, CancellationToken cancellationToken = default)
    {
        _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(plan));
        if (!IsEllipsoidPlan(plan) || input.ContentHash != plan.InputHash || ContentHash.Compute(input.Bytes.Span) != input.ContentHash)
            throw EllipsoidDrift("Read-only preview input differs from its exact plan.");
        using var parsed = Parse(input, retainGeometryAnalysis: true);
        var operation = plan.Operations.Single();
        var target = operation.EllipsoidTransformTarget!;
        var request = EllipsoidRequest(input, parsed.Snapshot, operation);
        var (profiles, _) = ResolveExperimentalPlanningProfiles(request, parsed);
        if (!profiles.Select(p => p.Mesh.Lod).SequenceEqual(target.Buffers.Select(b => b.Lod)))
            throw EllipsoidDrift("Preview plan does not cover every source LOD.");
        var blocks = parsed.Envelope.Blocks.Select(b => new PlannedTargetBlock(b.Index, b.Type, ContentHash.Compute(b.Payload.Span))).ToArray();
        if (!blocks.SequenceEqual(target.SourceBlocks)) throw EllipsoidDrift("Preview source inventory differs from the frozen plan.");
        var lods = new List<EllipsoidPreviewLodGeometry>();
        long pointCount = 0, indexCount = 0;
        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var facts = target.Buffers.Single(b => b.Lod == profile.Mesh.Lod);
            var vertex = profile.Vertices.Snapshot;
            var index = profile.Indices.Snapshot;
            if (profile.Mesh.MeshOrdinal != facts.MeshOrdinal || profile.Mesh.BlockIndex != facts.ResourceBlockIndex
                || vertex.Ordinal != facts.VertexBufferOrdinal || index.Ordinal != facts.IndexBufferOrdinal
                || vertex.ResourceBlockIndex != facts.VertexResourceBlockIndex || index.ResourceBlockIndex != facts.IndexResourceBlockIndex
                || vertex.EncodedHash != facts.VertexBlockInputHash || index.EncodedHash != facts.IndexBlockInputHash
                || vertex.PositionLayout != facts.PositionLayout || profile.PackedFrameLayout != facts.PackedFrameLayout
                || vertex.VertexCount != facts.VertexCount || vertex.DecodedHash != facts.InputDecodedVertexBufferHash
                || profile.Indices.Snapshot.DecodedHash != facts.DecodedIndexBufferHash
                || profile.Mesh.Geometry.Codec != facts.Codec || profile.VertexSetHash != facts.VertexSetHash)
                throw EllipsoidDrift("Preview buffer, index or codec identity differs from the frozen source.");
            pointCount += profile.SelectedVertices.Length;
            indexCount += request.SelectedDrawCalls.Where(c => c.Lod == profile.Mesh.Lod).Sum(c => c.IndexCount);
            CheckPreviewBudget(pointCount, indexCount);
            var points = profile.SelectedVertices.Select(v => Source2GeometryAnalyzer.ReadPosition(profile.Vertices, v)).ToArray();
            var triangles = request.SelectedDrawCalls.Where(c => c.Lod == profile.Mesh.Lod).SelectMany(call =>
            {
                var draw = profile.Mesh.GeometryAnalysis!.DrawCalls.Single(d => d.Snapshot.DrawCallId == call.DrawCallId);
                return Enumerable.Range(checked((int)call.IndexStart), checked((int)call.IndexCount)).Select(i => checked((int)profile.Indices.Indices[i] + draw.Snapshot.BaseVertex));
            }).ToArray();
            var context = new List<EllipsoidPreviewContext>();
            foreach (var mesh in parsed.Snapshot.Lods.Single(l => l.Level == profile.Mesh.Lod).Meshes.OrderBy(m => m.MeshOrdinal))
            {
                var parsedMesh = parsed.MeshesByOrdinal[mesh.MeshOrdinal];
                if (parsedMesh.GeometryAnalysis is null)
                    throw Errors.Unsupported("ELLIPSOID_PREVIEW_GEOMETRY_UNAVAILABLE", "Non-editable model context cannot be decoded completely.", "Use a fully decoded ordinary model for this bounded contact sheet.");
                foreach (var buffer in parsedMesh.GeometryAnalysis.VertexBuffers)
                {
                    if (mesh.MeshOrdinal == profile.Mesh.MeshOrdinal && buffer.Snapshot.Ordinal == profile.Vertices.Snapshot.Ordinal) continue;
                    pointCount += buffer.Snapshot.VertexCount;
                    CheckPreviewBudget(pointCount, indexCount);
                    context.Add(new(mesh.MeshOrdinal, buffer.Snapshot.Ordinal, Enumerable.Range(0, buffer.Snapshot.VertexCount)
                        .Select(v => Source2GeometryAnalyzer.ReadPosition(buffer, v)).ToArray()));
                }
            }
            CheckPreviewBudget(pointCount, indexCount);
            lods.Add(new(profile.Mesh.Lod, profile.Mesh.MeshOrdinal, profile.Vertices.Snapshot.Ordinal,
                request.SelectedDrawCalls.Where(c => c.Lod == profile.Mesh.Lod).Select(c => c.DrawCallId).Order(StringComparer.Ordinal).ToArray(), points, triangles, context));
        }
        return Task.FromResult(new EllipsoidPreviewGeometry(input.ContentHash, plan.Fingerprint, lods));
    }

    private static void CheckPreviewBudget(long points, long indices)
    {
        if (points > EllipsoidSelectionPreview.MaximumPoints || indices > EllipsoidSelectionPreview.MaximumTriangleIndices)
            throw Errors.Unsupported("ELLIPSOID_PREVIEW_LIMIT_EXCEEDED", "The complete preview exceeds its bounded point/topology budget.", "No partial or decimated preview was produced.");
    }
}
