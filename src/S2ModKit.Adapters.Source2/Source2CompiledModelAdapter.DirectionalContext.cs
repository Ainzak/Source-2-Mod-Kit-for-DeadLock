using System.Globalization;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    internal sealed record DirectionalSourceBuffer(DirectionalContextBuffer Facts, string? MemberId,
        PositionLayout Position, PackedFrameLayout? Frame, byte[] Before, byte[] After, Point3[] SourcePoints, Point3[] FinalPoints,
        IReadOnlyDictionary<int, int[]> RootContributors);

    private static DirectionalSourceBuffer[] ResolveDirectionalContext(ArtifactContent input, ParsedModel parsed, Model model,
        IReadOnlyList<CoordinatedResolvedBuffer> members, Dictionary<(int Mesh, int Buffer), DirectionalWordCalculation> calculations, bool pairedPreservation = false)
    {
        var selected = members.ToDictionary(m => (m.Profile.Mesh.MeshOrdinal, m.Profile.Vertices.Snapshot.Ordinal));
        var masks = ExperimentalArray(model.Data, "m_refMeshGroupMasks");
        if (masks.Count != parsed.MeshesByOrdinal.Count || !model.Data.TryGetValue("m_nDefaultMeshGroupMask", out var defaultMask))
            throw DirectionalFailure("DIRECTIONAL_COINCIDENT_CONTEXT_INCOMPLETE", "Authored mesh/bodygroup masks are incomplete.");
        var bodyMask = DirectionalMask(defaultMask);
        var result = new List<DirectionalSourceBuffer>();
        foreach (var mesh in parsed.MeshesByOrdinal.Values.OrderBy(m => m.Lod).ThenBy(m => m.MeshOrdinal))
        {
            var geometry = mesh.GeometryAnalysis;
            if (geometry is null || geometry.VertexBuffers.Count != geometry.IndexBuffers.Count || HasMorphData(mesh.Block.Data))
                throw DirectionalFailure("DIRECTIONAL_COINCIDENT_CONTEXT_INCOMPLETE", "Every represented context buffer needs characterized geometry and contributors.");
            var metadata = pairedPreservation
                ? Source2TransformMetadataAnalyzer.AnalyzePairedPreservationBuffers(mesh.Descriptor, mesh.Block.Data, geometry, "paired source context")
                : Source2TransformMetadataAnalyzer.AnalyzeVisualPreservationBuffers(mesh.Descriptor, mesh.Block.Data, geometry, "directional source context");
            var remap = model.GetRemapTable(mesh.MeshOrdinal);
            if (remap is null || remap.Any(i => i < 0 || i >= model.Skeleton.Bones.Length) || metadata.BoneBounds.Any(b => b.BoneIndex >= remap.Length))
                throw DirectionalFailure("EXPERIMENTAL_BONE_REMAP_UNSUPPORTED", "Complete source render/root remaps are required for excluded contributors too.");
            var remapHash = DirectionalContractValidator.VertexSetHash(remap);
            var meshMask = DirectionalMask(masks[mesh.MeshOrdinal]); var baseOffset = 0;
            foreach (var v in geometry.VertexBuffers)
            {
                var snapshot = v.Snapshot; var key = (mesh.MeshOrdinal, snapshot.Ordinal);
                var frame = v.PackedFrameLayout;
                if (selected.ContainsKey(key) && frame is null)
                    throw DirectionalFailure("DIRECTIONAL_COINCIDENT_CONTEXT_INCOMPLETE", "Selected frames must have a characterized complete-word identity.");
                var source = Enumerable.Range(0, snapshot.VertexCount).Select(i => Source2GeometryAnalyzer.ReadPosition(v, i)).ToArray();
                var after = calculations.TryGetValue(key, out var c) ? c.Bytes : v.Decoded;
                var final = c?.Points ?? source;
                var contributors = metadata.BoneBounds.GroupBy(b => remap[b.BoneIndex]).ToDictionary(g => g.Key,
                    g => g.SelectMany(b => b.InfluencedVertices).Where(i => i >= baseOffset && i < baseOffset + snapshot.VertexCount)
                        .Select(i => i - baseOffset).Distinct().Order().ToArray());
                var indices = Enumerable.Range(0, snapshot.VertexCount).ToArray();
                var skinning = MutationPlanJson.ComputeDirectionalFactsHash(new
                {
                    Descriptor = KvSemanticHasher.ComputeComplete(ExperimentalArray(mesh.Descriptor, "m_vertexBuffers")[snapshot.Ordinal]),
                    Contributors = contributors.OrderBy(p => p.Key).Select(p => new { Root = p.Key, Vertices = p.Value }).ToArray(),
                    // Raw immutable attributes include the actual blend words, not just membership.
                    Attributes = frame is null ? snapshot.DecodedHash : DirectionalUnchangedWords(v.Decoded, snapshot.PositionLayout, frame),
                });
                var facts = new DirectionalContextBuffer(mesh.Lod, mesh.MeshOrdinal, snapshot.Ordinal, mesh.BlockIndex, snapshot.ResourceBlockIndex,
                    geometry.IndexBuffers[snapshot.Ordinal].Snapshot.ResourceBlockIndex, snapshot.VertexCount, input.LogicalPath, selected.ContainsKey(key), meshMask, bodyMask,
                    snapshot.DecodedHash, DirectionalPositionWords(v.Decoded, snapshot.PositionLayout, indices),
                    // Uncharacterized excluded frames bind the whole immutable decoded buffer.
                    // They grant no selected-frame or nonempty protection capability.
                    frame is null ? snapshot.DecodedHash : Source2PackedFrameCodec.HashSelected(v.Decoded, frame, indices),
                    geometry.IndexBuffers[snapshot.Ordinal].Snapshot.DecodedHash, skinning, remapHash);
                result.Add(new(facts, selected.GetValueOrDefault(key)?.MemberId, snapshot.PositionLayout, frame, v.Decoded, after, source, final, contributors));
                baseOffset = checked(baseOffset + snapshot.VertexCount);
            }
        }
        if (result.GroupBy(c => c.Facts.VertexResourceBlockIndex).Any(g => g.Count() > 1 && g.Any(c => c.Facts.Selected)))
            throw DirectionalFailure("AFFINE_MULTI_BUFFER_OWNERSHIP_UNSUPPORTED", "Selected vertex storage aliases context storage.");
        return result.ToArray();
    }

    private static ulong DirectionalMask(KVObject value)
    {
        if (value.ValueType is not (KVValueType.UInt64 or KVValueType.UInt32 or KVValueType.Int32 or KVValueType.Int64)
            || (value.ValueType is KVValueType.Int32 or KVValueType.Int64 && value.ToInt64(CultureInfo.InvariantCulture) < 0))
            throw DirectionalFailure("DIRECTIONAL_COINCIDENT_CONTEXT_INCOMPLETE", "View masks must be exact nonnegative integer words.");
        return value.ToUInt64(CultureInfo.InvariantCulture);
    }

    internal static PlannedDirectionalProtection ResolveDirectionalProtection(DirectionalProtection intent, IReadOnlyList<DirectionalSourceBuffer> context,
        ContentHash skeletonHash, IReadOnlyList<string> rootNames)
    {
        var assertions = new List<PlannedDirectionalAssertion>();
        foreach (var assertion in intent.Assertions)
        {
            var sets = new List<DirectionalProtectedSet>();
            switch (assertion)
            {
                case DirectionalVertexAssertion vertex:
                    foreach (var buffer in context.Where(c => c.Facts.Selected))
                    {
                        var declared = vertex.Sets.Single(s => s.MemberId == buffer.MemberId && s.Lod == buffer.Facts.Lod);
                        if (declared.SourceDecodedBufferHash != buffer.Facts.DecodedBufferHash || declared.VertexCount != declared.VertexIndices.Count
                            || declared.VertexSetHash != DirectionalContractValidator.VertexSetHash(declared.VertexIndices))
                            throw DirectionalFailure("DIRECTIONAL_PROTECTION_DRIFT", $"Assertion {assertion.AssertionId}: source vertex identity drift.");
                        sets.Add(DirectionalProtectedWords(buffer, declared.VertexIndices, assertion.AssertionId));
                    }
                    break;
                case DirectionalBoneAssertion bone:
                    if ((uint)bone.BoneIndex >= (uint)rootNames.Count || rootNames[bone.BoneIndex] != bone.BoneName || skeletonHash != bone.RootSkeletonHash)
                        throw DirectionalFailure("DIRECTIONAL_PROTECTION_DRIFT", $"Assertion {assertion.AssertionId}: root skeleton/name/index drift.");
                    foreach (var buffer in context)
                        sets.Add(DirectionalProtectedWords(buffer, buffer.RootContributors.GetValueOrDefault(bone.BoneIndex) ?? [], assertion.AssertionId));
                    foreach (var lod in bone.Lods)
                    {
                        var rows = sets.Where(s => s.Lod == lod.Lod).ToArray();
                        if (rows.Sum(s => (long)s.VertexCount) != lod.ContributorCount || DirectionalContractValidator.ContributorSetHash(rows) != lod.ContributorSetHash)
                            throw DirectionalFailure("DIRECTIONAL_PROTECTION_DRIFT", $"Assertion {assertion.AssertionId}, LOD {lod.Lod}: complete bone contributors drifted.");
                    }
                    break;
                default: throw DirectionalFailure("DIRECTIONAL_PROTECTION_INVALID", "Unknown keep-fixed assertion.");
            }
            if (sets.Sum(s => (long)s.VertexCount) == 0) throw DirectionalFailure("DIRECTIONAL_PROTECTION_INVALID", $"Assertion {assertion.AssertionId} is empty.");
            assertions.Add(new(assertion.AssertionId, sets, DirectionalContractValidator.ContributorSetHash(sets)));
        }
        var union = context.Select(c => DirectionalProtectedWords(c, assertions.SelectMany(a => a.Sets)
            .Where(s => s.Lod == c.Facts.Lod && s.MeshOrdinal == c.Facts.MeshOrdinal && s.VertexBufferOrdinal == c.Facts.VertexBufferOrdinal)
            .SelectMany(s => s.VertexIndices).Distinct().Order().ToArray(), "union")).ToArray();
        foreach (var lod in context.Select(c => c.Facts.Lod).Distinct())
            if (union.Where(s => s.Lod == lod && context.Any(c => c.Facts.Selected && c.Facts.MeshOrdinal == s.MeshOrdinal && c.Facts.VertexBufferOrdinal == s.VertexBufferOrdinal)).Sum(s => (long)s.VertexCount) == 0)
                throw DirectionalFailure("DIRECTIONAL_PROTECTION_INVALID", $"The selected protected union is empty in LOD {lod}.");
        return new(assertions, union, MutationPlanJson.ComputeDirectionalFactsHash(union));
    }

    private static DirectionalProtectedSet DirectionalProtectedWords(DirectionalSourceBuffer c, IReadOnlyList<int> indices, string assertion)
    {
        if (!indices.SequenceEqual(indices.Distinct().Order()) || indices.Any(i => i < 0 || i >= c.Facts.VertexCount))
            throw DirectionalFailure("DIRECTIONAL_PROTECTION_INVALID", $"Assertion {assertion}: invalid indices in LOD {c.Facts.Lod}, mesh {c.Facts.MeshOrdinal}.");
        if (indices.Count > 0 && c.Frame is null)
            throw DirectionalFailure("DIRECTIONAL_PROTECTION_INVALID", $"Assertion {assertion}: nonempty protected context needs characterized packed frames.");
        foreach (var i in indices)
            if (!c.Before.AsSpan(i * c.Position.Stride + c.Position.Offset, 12).SequenceEqual(c.After.AsSpan(i * c.Position.Stride + c.Position.Offset, 12))
                || !c.Before.AsSpan(i * c.Frame!.Stride + c.Frame.Offset, 4).SequenceEqual(c.After.AsSpan(i * c.Frame.Stride + c.Frame.Offset, 4)))
                throw DirectionalFailure("DIRECTIONAL_PROTECTED_WORD_CHANGED", $"Assertion {assertion}, LOD {c.Facts.Lod}, mesh {c.Facts.MeshOrdinal}, buffer {c.Facts.VertexBufferOrdinal}, vertex {i}: position or complete frame changes.");
        return new(c.Facts.Lod, c.Facts.MeshOrdinal, c.Facts.VertexBufferOrdinal, c.Facts.DecodedBufferHash, indices,
            DirectionalContractValidator.VertexSetHash(indices), indices.Count,
            DirectionalPositionWords(c.Before, c.Position, indices), DirectionalPositionWords(c.After, c.Position, indices),
            indices.Count == 0 ? ContentHash.Compute([]) : Source2PackedFrameCodec.HashSelected(c.Before, c.Frame!, indices),
            indices.Count == 0 ? ContentHash.Compute([]) : Source2PackedFrameCodec.HashSelected(c.After, c.Frame!, indices));
    }
}
