using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed partial class ModelVerifier
{
    private static VerificationResult VerifyCoordinatedSnapshot(ModelSnapshot before, ModelSnapshot after, MutationPlan plan)
    {
        var operation = plan.Operations.Single();
        var target = operation.CoordinatedTransformTarget!;
        var boundaries = new List<BoundaryEvidence>();
        void Check(string name, bool passed) => boundaries.Add(new(name, passed ? "passed" : "failed",
            passed ? "Snapshot postcondition verified; resource-level box/preservation audit is separately required." : "Experimental snapshot postcondition failed."));
        Check("coordinated_source_inventory", before.Artifact.ContentHash == plan.InputHash
            && target.SourceBlocks.Count == before.Artifact.Blocks.Count
            && target.SourceBlocks.All(b => before.Artifact.Blocks.Any(a => a.Index == b.Index && a.Type == b.Type && a.ContentHash == b.InputHash)));
        Check("all_draw_calls_preserved", DictionariesEqual(Multiset(Flatten(before)), Multiset(Flatten(after))));
        Check("lod_inventory_preserved", before.Lods.Select(lod => lod.Level).Order().SequenceEqual(after.Lods.Select(lod => lod.Level).Order()));
        var original = before.Artifact.Blocks.ToDictionary(block => (block.Index, block.Type));
        var output = after.Artifact.Blocks.ToDictionary(block => (block.Index, block.Type));
        var inventory = original.Keys.ToHashSet().SetEquals(output.Keys);
        Check("resource_block_inventory_preserved", inventory);
        var mutable = operation.TargetBlocks.ToDictionary(block => (block.Index, block.Type));
        Check("target_block_fingerprints", mutable.All(item => original.TryGetValue(item.Key, out var block) && block.ContentHash == item.Value.InputHash));
        // Retained authored boxes may require no word changes; their MDAT remains an allowed
        // target but need not change. Every selected MVTX must have a nonempty position effect.
        Check("selected_vertex_blocks_changed", inventory && target.Buffers.All(item =>
            original.TryGetValue((item.VertexResourceBlockIndex, "MVTX"), out var source)
            && output.TryGetValue((item.VertexResourceBlockIndex, "MVTX"), out var current) && source.ContentHash != current.ContentHash));
        Check("non_target_blocks_byte_identical", inventory && original.Where(item => !mutable.ContainsKey(item.Key))
            .All(item => output[item.Key].ContentHash == item.Value.ContentHash));
        var meshes = after.Lods.SelectMany(lod => lod.Meshes.Select(mesh => ((lod.Level, mesh.MeshOrdinal), mesh))).ToDictionary();
        var oldMeshes = before.Lods.SelectMany(lod => lod.Meshes.Select(mesh => ((lod.Level, mesh.MeshOrdinal), mesh))).ToDictionary();
        var geometryMatches = target.Buffers.All(item =>
        {
            if (!oldMeshes.TryGetValue((item.Lod, item.MeshOrdinal), out var oldMesh)
                || !meshes.TryGetValue((item.Lod, item.MeshOrdinal), out var newMesh)
                || oldMesh.Geometry is not { Status: "ready" } oldGeometry || newMesh.Geometry is not { Status: "ready" } newGeometry
                || (uint)item.VertexBufferOrdinal >= (uint)oldGeometry.VertexBuffers.Count
                || (uint)item.VertexBufferOrdinal >= (uint)newGeometry.VertexBuffers.Count
                || (uint)item.IndexBufferOrdinal >= (uint)oldGeometry.IndexBuffers.Count
                || (uint)item.IndexBufferOrdinal >= (uint)newGeometry.IndexBuffers.Count) return false;
            var oldVertex = oldGeometry.VertexBuffers[item.VertexBufferOrdinal];
            var newVertex = newGeometry.VertexBuffers[item.VertexBufferOrdinal];
            var oldIndex = oldGeometry.IndexBuffers[item.IndexBufferOrdinal];
            return oldVertex.ResourceBlockIndex == item.VertexResourceBlockIndex && oldVertex.EncodedHash == item.VertexBlockInputHash
                && oldVertex.DecodedHash == item.InputDecodedVertexBufferHash && newVertex.DecodedHash == item.ExpectedDecodedVertexBufferHash
                && newVertex.ResourceBlockIndex == oldVertex.ResourceBlockIndex && newVertex.VertexCount == oldVertex.VertexCount
                && newVertex.Stride == oldVertex.Stride && newVertex.PositionLayout == oldVertex.PositionLayout
                && oldIndex.ResourceBlockIndex == item.IndexResourceBlockIndex && oldIndex.EncodedHash == item.IndexBlockInputHash
                && oldIndex.DecodedHash == item.DecodedIndexBufferHash && newGeometry.IndexBuffers[item.IndexBufferOrdinal] == oldIndex
                && oldGeometry.Codec == item.Codec && newGeometry.Codec == item.Codec;
        });
        Check("coordinated_snapshot_postconditions", geometryMatches);
        return new(boundaries.All(boundary => boundary.Status == "passed"), boundaries, []);
    }

}
