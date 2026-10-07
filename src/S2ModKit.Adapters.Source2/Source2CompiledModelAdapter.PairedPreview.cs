using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter : IPairedPreviewGeometryReader
{
    public Task<DirectionalPreviewGeometry> ReadPairedPreviewGeometryAsync(ArtifactContent input, MutationPlan plan, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(plan));
        if (!IsPairedPlan(plan) || input.ContentHash != plan.InputHash || ContentHash.Compute(input.Bytes.Span) != input.ContentHash)
            throw PairedDrift("Preview source identity differs from its frozen plan.");
        using var parsed = Parse(input, retainGeometryAnalysis: true);
        CheckPreviewBudget(parsed.Snapshot.Lods.Sum(l => l.Meshes.Sum(m => m.Geometry!.VertexBuffers.Sum(b => (long)b.VertexCount))),
            parsed.Snapshot.Lods.Sum(l => l.Meshes.Sum(m => m.DrawCalls.Sum(c => (long)c.IndexCount))));
        var operation = plan.Operations.Single(); var target = operation.PairedTransformTarget!;
        SamePaired(parsed.Envelope.Blocks.Select(b => new PlannedTargetBlock(b.Index, b.Type, ContentHash.Compute(b.Payload.Span))).ToArray(), target.SourceBlocks, "preview source blocks");
        var members = DiscoverPairedVerificationMembers(input, parsed, operation);
        var model = parsed.Resource.Blocks.OfType<ValveResourceFormat.ResourceTypes.Model>().Single();
        var context = ReadDirectionalVerificationContext(input, parsed, parsed, model, members, pairedPreservation: true);
        SamePaired(context.Select(c => c.Facts).ToArray(), target.ContextBuffers, "preview source inventory");
        SamePaired(AuditDirectionalProtection(target.PairedTransform.Protection, context,
            KvSemanticHasher.ComputeComplete(ExperimentalCollection(model.Data, "m_modelSkeleton")), model.Skeleton.Bones.Select(b => b.Name).ToArray()), target.Protection, "preview protected source");
        var result = new List<DirectionalPreviewSourceBuffer>(); long points = 0, indices = 0;
        foreach (var buffer in context)
        {
            token.ThrowIfCancellationRequested();
            var facts = buffer.Facts; var mesh = parsed.MeshesByOrdinal[facts.MeshOrdinal]; var geometry = mesh.GeometryAnalysis!;
            var ranges = parsed.Snapshot.Lods.Single(l => l.Level == facts.Lod).Meshes.Single(m => m.MeshOrdinal == facts.MeshOrdinal).DrawCalls.ToDictionary(d => d.Id, StringComparer.Ordinal);
            var calls = geometry.DrawCalls.Where(c => c.Snapshot.VertexBufferOrdinal == facts.VertexBufferOrdinal).ToArray();
            points += facts.VertexCount; indices += calls.Sum(c => (long)ranges[c.Snapshot.DrawCallId].IndexCount); CheckPreviewBudget(points, indices);
            var triangles = calls.OrderBy(c => ranges[c.Snapshot.DrawCallId].IndexStart)
                .DistinctBy(c => (c.Snapshot.IndexBufferOrdinal, c.Snapshot.BaseVertex, ranges[c.Snapshot.DrawCallId].IndexStart, ranges[c.Snapshot.DrawCallId].IndexCount)).SelectMany(c =>
            {
                var draw = c.Snapshot;
                var range = ranges[draw.DrawCallId];
                return Enumerable.Range(checked((int)range.IndexStart), checked((int)range.IndexCount))
                    .Select(i => checked((int)geometry.IndexBuffers[draw.IndexBufferOrdinal].Indices[i] + draw.BaseVertex));
            }).ToArray();
            result.Add(new(facts, buffer.MemberId, calls.Select(c => c.Snapshot.DrawCallId).Order(StringComparer.Ordinal).ToArray(), buffer.SourcePoints, triangles));
        }
        return Task.FromResult(new DirectionalPreviewGeometry(input.ContentHash, plan.Fingerprint, result));
    }
}
