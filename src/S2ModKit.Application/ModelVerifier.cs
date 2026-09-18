using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Application;

public sealed class ModelVerifier
{
    public static VerificationResult Verify(ModelSnapshot before, ModelSnapshot after, MutationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Operations.Count > 0
            && plan.Operations.All(operation => operation.Kind == "transform_component" && operation.Version is 1 or 3))
        {
            return VerifyTransform(before, after, plan);
        }

        if (plan.Operations is [{ Kind: "transform_component", Version: 2, CoupledTransformTarget: not null }])
        {
            return VerifyCoupledTransform(before, after, plan);
        }

        var selected = plan.Operations.SelectMany(operation => operation.SelectedDrawCalls).ToArray();
        var selectedKeys = new HashSet<DrawCallSemanticKey>(selected.Select(DrawCallSemanticKey.From));
        var beforeCalls = Flatten(before).ToArray();
        var afterCalls = Flatten(after).ToArray();
        var boundaries = new List<BoundaryEvidence>();

        var selectedRemain = afterCalls.Where(call => selectedKeys.Contains(DrawCallSemanticKey.From(call))).ToArray();
        boundaries.Add(selectedRemain.Length == 0
            ? new BoundaryEvidence("selected_draw_calls_removed", "passed", $"All {selected.Length} selected draw calls are absent.")
            : new BoundaryEvidence("selected_draw_calls_removed", "failed", $"{selectedRemain.Length} selected draw calls remain."));

        var expectedRemaining = Multiset(beforeCalls.Where(call => !selectedKeys.Contains(DrawCallSemanticKey.From(call))));
        var actualRemaining = Multiset(afterCalls);
        boundaries.Add(DictionariesEqual(expectedRemaining, actualRemaining)
            ? new BoundaryEvidence("non_target_draw_calls_preserved", "passed", $"All {expectedRemaining.Values.Sum()} non-target draw calls are preserved semantically.")
            : new BoundaryEvidence("non_target_draw_calls_preserved", "failed", "Non-target draw-call semantics changed."));

        var expectedLods = before.Lods.Select(lod => lod.Level).Order().ToArray();
        var actualLods = after.Lods.Select(lod => lod.Level).Order().ToArray();
        boundaries.Add(expectedLods.SequenceEqual(actualLods)
            ? new BoundaryEvidence("lod_inventory_preserved", "passed", $"LOD inventory [{string.Join(", ", expectedLods)}] is unchanged.")
            : new BoundaryEvidence("lod_inventory_preserved", "failed", "LOD inventory changed during rewrite."));

        var beforeMeshSemantics = MeshSemanticHashes(before);
        var afterMeshSemantics = MeshSemanticHashes(after);
        var meshSemanticsPreserved = beforeMeshSemantics.Count == afterMeshSemantics.Count
            && beforeMeshSemantics.All(entry => afterMeshSemantics.TryGetValue(entry.Key, out var hash) && hash == entry.Value);
        boundaries.Add(meshSemanticsPreserved
            ? new BoundaryEvidence("mesh_non_draw_call_semantics_preserved", "passed", $"All {beforeMeshSemantics.Count} mesh semantic fingerprints outside m_drawCalls are unchanged.")
            : new BoundaryEvidence("mesh_non_draw_call_semantics_preserved", "failed", "At least one mesh changed outside the permitted m_drawCalls collections."));

        var beforeBlocks = before.Artifact.Blocks.ToDictionary(block => (block.Type, block.Index));
        var afterBlocks = after.Artifact.Blocks.ToDictionary(block => (block.Type, block.Index));
        var targetBlocks = plan.Operations.SelectMany(operation => operation.TargetBlocks)
            .GroupBy(block => (block.Type, block.Index))
            .ToDictionary(group => group.Key, group => group.Select(block => block.InputHash).Distinct().ToArray());
        var blockInventoryPreserved = beforeBlocks.Keys.ToHashSet().SetEquals(afterBlocks.Keys);
        boundaries.Add(blockInventoryPreserved
            ? new BoundaryEvidence("resource_block_inventory_preserved", "passed", $"All {beforeBlocks.Count} resource block identities are preserved.")
            : new BoundaryEvidence("resource_block_inventory_preserved", "failed", "Resource block inventory changed."));

        var targetFingerprintsMatch = blockInventoryPreserved
            && targetBlocks.All(entry => entry.Value.Length == 1
                && beforeBlocks.TryGetValue(entry.Key, out var block)
                && block.ContentHash == entry.Value[0]);
        boundaries.Add(targetFingerprintsMatch
            ? new BoundaryEvidence("target_block_fingerprints", "passed", $"All {targetBlocks.Count} target block hashes match the dry-run plan.")
            : new BoundaryEvidence("target_block_fingerprints", "failed", "At least one target block hash differs from the dry-run plan."));

        var nonTargetBlocksPreserved = blockInventoryPreserved && beforeBlocks
            .Where(entry => !targetBlocks.ContainsKey(entry.Key))
            .All(entry => afterBlocks[entry.Key].ContentHash == entry.Value.ContentHash);
        boundaries.Add(nonTargetBlocksPreserved
            ? new BoundaryEvidence("non_target_blocks_byte_identical", "passed", "Every non-target resource block payload hash is unchanged.")
            : new BoundaryEvidence("non_target_blocks_byte_identical", "failed", "At least one non-target block payload changed."));

        var vertexIndexBlocksPreserved = blockInventoryPreserved && beforeBlocks
            .Where(entry => entry.Key.Type is "MVTX" or "MIDX" or "VBIB")
            .All(entry => afterBlocks[entry.Key].ContentHash == entry.Value.ContentHash);
        boundaries.Add(vertexIndexBlocksPreserved
            ? new BoundaryEvidence("vertex_index_payloads_byte_identical", "passed", "All vertex and index block payload hashes are unchanged; unused geometry bytes may remain.")
            : new BoundaryEvidence("vertex_index_payloads_byte_identical", "failed", "At least one vertex or index payload changed."));

        var targetBlocksChanged = blockInventoryPreserved && targetBlocks.Keys.All(key => beforeBlocks[key].ContentHash != afterBlocks[key].ContentHash);
        boundaries.Add(targetBlocksChanged
            ? new BoundaryEvidence("target_blocks_changed", "passed", $"All {targetBlocks.Count} planned target blocks changed.")
            : new BoundaryEvidence("target_blocks_changed", "failed", "At least one planned target block did not change."));

        var valid = boundaries.All(boundary => boundary.Status == "passed");
        return new VerificationResult(valid, boundaries, []);
    }

    private static VerificationResult VerifyCoupledTransform(ModelSnapshot before, ModelSnapshot after, MutationPlan plan)
    {
        var operation = plan.Operations.Single();
        var coupled = operation.CoupledTransformTarget!;
        var boundaries = new List<BoundaryEvidence>();
        var callsPreserved = DictionariesEqual(Multiset(Flatten(before)), Multiset(Flatten(after)));
        boundaries.Add(callsPreserved
            ? new BoundaryEvidence("all_draw_calls_preserved", "passed", "All draw-call semantics are unchanged.")
            : new BoundaryEvidence("all_draw_calls_preserved", "failed", "Draw-call semantics changed during the coupled rewrite."));

        var beforeBlocks = before.Artifact.Blocks.ToDictionary(block => (block.Index, block.Type));
        var afterBlocks = after.Artifact.Blocks.ToDictionary(block => (block.Index, block.Type));
        var targets = coupled.TargetBlocks.ToDictionary(block => (block.Index, block.Type));
        var inventoryPreserved = beforeBlocks.Keys.ToHashSet().SetEquals(afterBlocks.Keys);
        boundaries.Add(inventoryPreserved
            ? new BoundaryEvidence("resource_block_inventory_preserved", "passed", $"All {beforeBlocks.Count} block identities are preserved.")
            : new BoundaryEvidence("resource_block_inventory_preserved", "failed", "Resource block inventory changed."));

        var targetFingerprintsMatch = inventoryPreserved && targets.All(entry =>
            beforeBlocks.TryGetValue(entry.Key, out var block) && block.ContentHash == entry.Value.InputHash);
        boundaries.Add(targetFingerprintsMatch
            ? new BoundaryEvidence("target_block_fingerprints", "passed", "The MDAT, MBUF, and PHYS inputs match the plan.")
            : new BoundaryEvidence("target_block_fingerprints", "failed", "A coupled target no longer matches its planned input hash."));

        var targetsChanged = inventoryPreserved && targets.Keys.All(key => beforeBlocks[key].ContentHash != afterBlocks[key].ContentHash);
        boundaries.Add(targetsChanged
            ? new BoundaryEvidence("coupled_target_blocks_changed", "passed", "The planned MDAT, MBUF, and PHYS blocks all changed.")
            : new BoundaryEvidence("coupled_target_blocks_changed", "failed", "At least one coupled target block did not change."));

        var unrelatedPreserved = inventoryPreserved && beforeBlocks
            .Where(entry => !targets.ContainsKey(entry.Key))
            .All(entry => afterBlocks[entry.Key].ContentHash == entry.Value.ContentHash);
        boundaries.Add(unrelatedPreserved
            ? new BoundaryEvidence("non_target_blocks_byte_identical", "passed", "Every unrelated resource block remains byte-identical.")
            : new BoundaryEvidence("non_target_blocks_byte_identical", "failed", "An unrelated resource block changed."));

        var lodsPreserved = before.Lods.Select(lod => lod.Level).Order().SequenceEqual(after.Lods.Select(lod => lod.Level).Order());
        boundaries.Add(lodsPreserved
            ? new BoundaryEvidence("lod_inventory_preserved", "passed", "The LOD inventory is unchanged.")
            : new BoundaryEvidence("lod_inventory_preserved", "failed", "The LOD inventory changed."));

        return new VerificationResult(boundaries.All(boundary => boundary.Status == "passed"), boundaries, []);
    }

    private static VerificationResult VerifyTransform(ModelSnapshot before, ModelSnapshot after, MutationPlan plan)
    {
        var boundaries = new List<BoundaryEvidence>();
        var selected = plan.Operations.SelectMany(operation => operation.SelectedDrawCalls).ToArray();
        var selectedKeys = selected.Select(DrawCallSemanticKey.From).ToHashSet();
        var beforeCalls = Flatten(before).ToArray();
        var afterCalls = Flatten(after).ToArray();
        var preservedSelected = afterCalls.Count(call => selectedKeys.Contains(DrawCallSemanticKey.From(call))) == selectedKeys.Count;
        boundaries.Add(preservedSelected
            ? new BoundaryEvidence("selected_draw_calls_preserved", "passed", $"All {selectedKeys.Count} selected draw calls remain semantically unchanged.")
            : new BoundaryEvidence("selected_draw_calls_preserved", "failed", "At least one selected draw call changed or disappeared."));

        var allCallsPreserved = DictionariesEqual(Multiset(beforeCalls), Multiset(afterCalls));
        boundaries.Add(allCallsPreserved
            ? new BoundaryEvidence("all_draw_calls_preserved", "passed", $"All {beforeCalls.Length} draw calls are preserved semantically.")
            : new BoundaryEvidence("all_draw_calls_preserved", "failed", "Draw-call semantics changed during position rewrite."));

        var expectedLods = before.Lods.Select(lod => lod.Level).Order().ToArray();
        var actualLods = after.Lods.Select(lod => lod.Level).Order().ToArray();
        boundaries.Add(expectedLods.SequenceEqual(actualLods)
            ? new BoundaryEvidence("lod_inventory_preserved", "passed", $"LOD inventory [{string.Join(", ", expectedLods)}] is unchanged.")
            : new BoundaryEvidence("lod_inventory_preserved", "failed", "LOD inventory changed during rewrite."));

        var targetMeshes = plan.Operations.SelectMany(operation => operation.GeometryTargets)
            .Select(target => new MeshSemanticKey(target.Lod, StableIdentity.NormalizePath(target.ResourcePath), target.MeshOrdinal, target.ResourceBlockIndex))
            .ToHashSet();
        var beforeMeshSemantics = MeshSemanticHashes(before);
        var afterMeshSemantics = MeshSemanticHashes(after);
        var nonTargetMeshSemanticsPreserved = beforeMeshSemantics.Count == afterMeshSemantics.Count
            && beforeMeshSemantics.Where(entry => !targetMeshes.Contains(entry.Key))
                .All(entry => afterMeshSemantics.TryGetValue(entry.Key, out var hash) && hash == entry.Value);
        boundaries.Add(nonTargetMeshSemanticsPreserved
            ? new BoundaryEvidence("non_target_mesh_semantics_preserved", "passed", "Every non-target MDAT semantic fingerprint is unchanged.")
            : new BoundaryEvidence("non_target_mesh_semantics_preserved", "failed", "At least one non-target MDAT semantic fingerprint changed."));

        var beforeBlocks = before.Artifact.Blocks.ToDictionary(block => (block.Type, block.Index));
        var afterBlocks = after.Artifact.Blocks.ToDictionary(block => (block.Type, block.Index));
        var targetBlocks = plan.Operations.SelectMany(operation => operation.TargetBlocks)
            .GroupBy(block => (block.Type, block.Index))
            .ToDictionary(group => group.Key, group => group.Select(block => block.InputHash).Distinct().ToArray());
        var blockInventoryPreserved = beforeBlocks.Keys.ToHashSet().SetEquals(afterBlocks.Keys);
        boundaries.Add(blockInventoryPreserved
            ? new BoundaryEvidence("resource_block_inventory_preserved", "passed", $"All {beforeBlocks.Count} resource block identities are preserved.")
            : new BoundaryEvidence("resource_block_inventory_preserved", "failed", "Resource block inventory changed."));

        var targetFingerprintsMatch = blockInventoryPreserved
            && targetBlocks.All(entry => entry.Value.Length == 1
                && beforeBlocks.TryGetValue(entry.Key, out var block)
                && block.ContentHash == entry.Value[0]);
        boundaries.Add(targetFingerprintsMatch
            ? new BoundaryEvidence("target_block_fingerprints", "passed", $"All {targetBlocks.Count} target block hashes match the dry-run plan.")
            : new BoundaryEvidence("target_block_fingerprints", "failed", "At least one target block hash differs from the dry-run plan."));

        var nonTargetBlocksPreserved = blockInventoryPreserved && beforeBlocks
            .Where(entry => !targetBlocks.ContainsKey(entry.Key))
            .All(entry => afterBlocks[entry.Key].ContentHash == entry.Value.ContentHash);
        boundaries.Add(nonTargetBlocksPreserved
            ? new BoundaryEvidence("non_target_blocks_byte_identical", "passed", "Every non-target resource block payload hash is unchanged.")
            : new BoundaryEvidence("non_target_blocks_byte_identical", "failed", "At least one non-target block payload changed."));

        var indexBlocksPreserved = blockInventoryPreserved && beforeBlocks
            .Where(entry => entry.Key.Type is "MIDX" or "VBIB")
            .All(entry => afterBlocks[entry.Key].ContentHash == entry.Value.ContentHash);
        boundaries.Add(indexBlocksPreserved
            ? new BoundaryEvidence("index_payloads_byte_identical", "passed", "Every index payload remains byte-identical.")
            : new BoundaryEvidence("index_payloads_byte_identical", "failed", "At least one index payload changed."));

        var targetBlocksChanged = blockInventoryPreserved
            && targetBlocks.Keys.All(key => beforeBlocks[key].ContentHash != afterBlocks[key].ContentHash);
        boundaries.Add(targetBlocksChanged
            ? new BoundaryEvidence("target_blocks_changed", "passed", $"All {targetBlocks.Count} planned target blocks changed.")
            : new BoundaryEvidence("target_blocks_changed", "failed", "At least one planned target block did not change."));

        var geometryMatches = VerifyTransformGeometry(before, after, plan);
        boundaries.Add(geometryMatches
            ? new BoundaryEvidence("transform_geometry_postconditions", "passed", "Target buffer identities, draw-call ownership, position bounds, and codecs match the transform plan.")
            : new BoundaryEvidence("transform_geometry_postconditions", "failed", "Transformed geometry differs from the planned postconditions."));

        return new VerificationResult(boundaries.All(boundary => boundary.Status == "passed"), boundaries, []);
    }

    private static bool VerifyTransformGeometry(ModelSnapshot before, ModelSnapshot after, MutationPlan plan)
    {
        var beforeMeshes = before.Lods.SelectMany(lod => lod.Meshes.Select(mesh => ((lod.Level, mesh.MeshOrdinal), mesh))).ToDictionary();
        var afterMeshes = after.Lods.SelectMany(lod => lod.Meshes.Select(mesh => ((lod.Level, mesh.MeshOrdinal), mesh))).ToDictionary();
        var afterBlocks = after.Artifact.Blocks.ToDictionary(block => (block.Type, block.Index));
        foreach (var target in plan.Operations.SelectMany(operation => operation.GeometryTargets))
        {
            var operation = plan.Operations.Single(item => item.GeometryTargets.Contains(target));
            if (!beforeMeshes.TryGetValue((target.Lod, target.MeshOrdinal), out var beforeMesh)
                || !afterMeshes.TryGetValue((target.Lod, target.MeshOrdinal), out var afterMesh)
                || beforeMesh.Geometry is not { Status: "ready" } beforeGeometry
                || afterMesh.Geometry is not { Status: "ready" } afterGeometry
                || (uint)target.VertexBufferOrdinal >= (uint)beforeGeometry.VertexBuffers.Count
                || (uint)target.VertexBufferOrdinal >= (uint)afterGeometry.VertexBuffers.Count
                || (uint)target.IndexBufferOrdinal >= (uint)beforeGeometry.IndexBuffers.Count
                || (uint)target.IndexBufferOrdinal >= (uint)afterGeometry.IndexBuffers.Count)
            {
                return false;
            }

            var beforeVertex = beforeGeometry.VertexBuffers[target.VertexBufferOrdinal];
            var afterVertex = afterGeometry.VertexBuffers[target.VertexBufferOrdinal];
            var beforeIndex = beforeGeometry.IndexBuffers[target.IndexBufferOrdinal];
            var afterIndex = afterGeometry.IndexBuffers[target.IndexBufferOrdinal];
            if (beforeVertex.ResourceBlockIndex != target.VertexResourceBlockIndex
                || beforeVertex.EncodedHash != target.VertexBlockInputHash
                || beforeVertex.DecodedHash != target.DecodedVertexBufferHash
                || afterVertex.ResourceBlockIndex != beforeVertex.ResourceBlockIndex
                || afterVertex.VertexCount != beforeVertex.VertexCount
                || afterVertex.Stride != beforeVertex.Stride
                || afterVertex.PositionLayout != beforeVertex.PositionLayout
                || afterVertex.DecodedHash != target.ExpectedDecodedVertexBufferHash
                || !afterBlocks.TryGetValue(("MVTX", target.VertexResourceBlockIndex), out var afterVertexBlock)
                || afterVertex.EncodedHash != afterVertexBlock.ContentHash
                || beforeIndex.ResourceBlockIndex != target.IndexResourceBlockIndex
                || beforeIndex.EncodedHash != target.IndexBlockInputHash
                || beforeIndex.DecodedHash != target.DecodedIndexBufferHash
                || afterIndex != beforeIndex
                || beforeGeometry.Codec != target.Codec
                || afterGeometry.Codec != target.Codec)
            {
                return false;
            }

            var beforeCalls = beforeGeometry.DrawCalls.OrderBy(call => call.DrawCallId, StringComparer.Ordinal).ToArray();
            var afterCalls = afterGeometry.DrawCalls.OrderBy(call => call.DrawCallId, StringComparer.Ordinal).ToArray();
            if (beforeCalls.Length != afterCalls.Length || beforeCalls.Length == 0)
            {
                return false;
            }

            var transform = new UniformTransform(ToPoint(target.FrozenPivot), target.UniformScale, ToPoint(target.Translation));
            if (operation.Version == 3)
            {
                var selectedIds = target.ConnectedComponentIds.ToHashSet(StringComparer.Ordinal);
                var beforeComponents = beforeGeometry.ConnectedComponents.ToDictionary(component => component.Id, StringComparer.Ordinal);
                var afterComponents = afterGeometry.ConnectedComponents.ToDictionary(component => component.Id, StringComparer.Ordinal);
                if (selectedIds.Count == 0
                    || !beforeComponents.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(afterComponents.Keys)
                    || selectedIds.Any(id => !beforeComponents.ContainsKey(id))
                    || beforeComponents.Where(entry => !selectedIds.Contains(entry.Key)).Any(entry => afterComponents[entry.Key] != entry.Value)
                    || selectedIds.Any(id => beforeComponents[id] with { Bounds = afterComponents[id].Bounds } != afterComponents[id]
                        || TransformBounds(beforeComponents[id].Bounds, transform) != afterComponents[id].Bounds)
                    || UnionBounds(selectedIds.Select(id => beforeComponents[id].Bounds)) != target.BeforeBounds
                    || UnionBounds(selectedIds.Select(id => afterComponents[id].Bounds)) != target.ExpectedAfterBounds)
                {
                    return false;
                }

                continue;
            }

            for (var index = 0; index < beforeCalls.Length; index++)
            {
                var beforeCall = beforeCalls[index];
                var afterCall = afterCalls[index];
                if (beforeCall with { Bounds = afterCall.Bounds } != afterCall
                    || TransformBounds(beforeCall.Bounds, transform) != afterCall.Bounds)
                {
                    return false;
                }
            }

            if (UnionBounds(beforeCalls.Select(call => call.Bounds)) != target.BeforeBounds
                || UnionBounds(afterCalls.Select(call => call.Bounds)) != target.ExpectedAfterBounds)
            {
                return false;
            }
        }

        return true;
    }

    private static GeometryBounds TransformBounds(GeometryBounds bounds, UniformTransform transform)
    {
        var transformed = transform.Apply([ToPoint(bounds.Min), ToPoint(bounds.Max)]);
        return new GeometryBounds(ToVector(transformed[0]), ToVector(transformed[1]));
    }

    private static GeometryBounds UnionBounds(IEnumerable<GeometryBounds> values)
    {
        var bounds = values.ToArray();
        return new GeometryBounds(
            new TransformVector3
            {
                X = bounds.Min(value => value.Min.X),
                Y = bounds.Min(value => value.Min.Y),
                Z = bounds.Min(value => value.Min.Z),
            },
            new TransformVector3
            {
                X = bounds.Max(value => value.Max.X),
                Y = bounds.Max(value => value.Max.Y),
                Z = bounds.Max(value => value.Max.Z),
            });
    }

    private static Point3 ToPoint(TransformVector3 value) => new(value.X, value.Y, value.Z);

    private static TransformVector3 ToVector(Point3 value) => new() { X = value.X, Y = value.Y, Z = value.Z };

    private static IEnumerable<SelectedDrawCall> Flatten(ModelSnapshot model)
    {
        foreach (var lod in model.Lods)
        {
            foreach (var mesh in lod.Meshes)
            {
                foreach (var drawCall in mesh.DrawCalls)
                {
                    yield return new SelectedDrawCall(lod.Level, StableIdentity.NormalizePath(mesh.ResourcePath), mesh.MeshOrdinal, mesh.ResourceBlockIndex, drawCall.Id, drawCall.MaterialPath, drawCall.DrawCallOrdinal, drawCall.IndexStart, drawCall.IndexCount);
                }
            }
        }
    }

    private static Dictionary<DrawCallSemanticKey, int> Multiset(IEnumerable<SelectedDrawCall> values) =>
        values.GroupBy(DrawCallSemanticKey.From).ToDictionary(group => group.Key, group => group.Count());

    private static Dictionary<MeshSemanticKey, ContentHash> MeshSemanticHashes(ModelSnapshot model) =>
        model.Lods.SelectMany(lod => lod.Meshes.Select(mesh => new KeyValuePair<MeshSemanticKey, ContentHash>(
            new MeshSemanticKey(lod.Level, StableIdentity.NormalizePath(mesh.ResourcePath), mesh.MeshOrdinal, mesh.ResourceBlockIndex),
            mesh.ImmutableSemanticHash))).ToDictionary();

    private static bool DictionariesEqual(Dictionary<DrawCallSemanticKey, int> left, Dictionary<DrawCallSemanticKey, int> right) =>
        left.Count == right.Count && left.All(entry => right.TryGetValue(entry.Key, out var count) && count == entry.Value);

    private sealed record DrawCallSemanticKey(int Lod, string ResourcePath, int MeshOrdinal, string MaterialPath, long IndexStart, long IndexCount)
    {
        public static DrawCallSemanticKey From(SelectedDrawCall call) =>
            new(call.Lod, call.ResourcePath, call.MeshOrdinal, call.MaterialPath, call.IndexStart, call.IndexCount);
    }

    private sealed record MeshSemanticKey(int Lod, string ResourcePath, int MeshOrdinal, int ResourceBlockIndex);
}
