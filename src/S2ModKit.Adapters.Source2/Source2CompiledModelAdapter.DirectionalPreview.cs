using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter : IDirectionalPreviewGeometryReader
{
    public Task<DirectionalPreviewGeometry> ReadDirectionalPreviewGeometryAsync(ArtifactContent input, MutationPlan plan, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(plan));
        if (!IsDirectionalPlan(plan) || input.ContentHash != plan.InputHash || ContentHash.Compute(input.Bytes.Span) != input.ContentHash)
            throw DirectionalDrift("Preview source identity differs from its frozen plan.");
        using var parsed = Parse(input, retainGeometryAnalysis: true);
        CheckPreviewBudget(parsed.Snapshot.Lods.Sum(l => l.Meshes.Sum(m => m.Geometry!.VertexBuffers.Sum(b => (long)b.VertexCount))),
            parsed.Snapshot.Lods.Sum(l => l.Meshes.Sum(m => m.DrawCalls.Sum(c => (long)c.IndexCount))));
        var operation = plan.Operations.Single(); var target = operation.DirectionalTransformTarget!;
        SameDirectional(parsed.Envelope.Blocks.Select(b => new PlannedTargetBlock(b.Index, b.Type, ContentHash.Compute(b.Payload.Span))).ToArray(), target.SourceBlocks, "preview source blocks");
        var members = DiscoverDirectionalVerificationMembers(input, parsed, operation);
        var (model, _, _) = ResolveExperimentalRootMetadata(parsed, members.Select(m => m.Profile).ToArray());
        SameDirectional(DiscoverDirectionalVerificationPivot(parsed, members, target.DirectionalTransform.Field.Pivot), target.Pivot, "preview pivot");
        var context = ReadDirectionalVerificationContext(input, parsed, parsed, model, members);
        SameDirectional(context.Select(c => c.Facts).ToArray(), target.ContextBuffers, "preview source inventory");
        SameDirectional(AuditDirectionalProtection(target.DirectionalTransform.Protection, context,
            KvSemanticHasher.ComputeComplete(ExperimentalCollection(model.Data, "m_modelSkeleton")), model.Skeleton.Bones.Select(b => b.Name).ToArray()), target.Protection, "preview protected source");
        var result = new List<DirectionalPreviewSourceBuffer>(); long points = 0, indices = 0;
        foreach (var buffer in context)
        {
            token.ThrowIfCancellationRequested();
            var facts = buffer.Facts; var mesh = parsed.MeshesByOrdinal[facts.MeshOrdinal]; var geometry = mesh.GeometryAnalysis!;
            var ranges = parsed.Snapshot.Lods.Single(l => l.Level == facts.Lod).Meshes.Single(m => m.MeshOrdinal == facts.MeshOrdinal).DrawCalls.ToDictionary(d => d.Id, StringComparer.Ordinal);
            var calls = geometry.DrawCalls.Where(c => c.Snapshot.VertexBufferOrdinal == facts.VertexBufferOrdinal).ToArray();
            points += facts.VertexCount; indices += calls.Sum(c => (long)ranges[c.Snapshot.DrawCallId].IndexCount); CheckPreviewBudget(points, indices);
            var triangles = calls.SelectMany(c =>
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
