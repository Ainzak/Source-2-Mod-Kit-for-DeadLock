using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;

namespace S2ModKit.Adapters.Source2;

internal sealed record Source2RawMbufRewriteResult(
    ReadOnlyMemory<byte> Payload,
    Source2RawMbufAnalysis Reopened,
    ContentHash VisualMetadataSemanticHash);

/// <summary>
/// Produces only isolated MBUF and in-memory visual-metadata results. It intentionally cannot
/// rebuild an envelope or return a publishable candidate; the coupled writer composes it with PHYS.
/// </summary>
internal static class Source2RawMbufWriter
{
    private static readonly string[] AllowedByteClasses =
    [
        "mbuf.selected_position_bytes",
        "mdat.scene_bounds",
        "mdat.bone_bounds",
        "mdat.bone_culling_radius",
    ];

    public static Source2RawMbufRewriteResult Rewrite(
        Source2ResourceBlock block,
        IReadOnlyList<GeometryDrawCallInput> drawCalls,
        KVObject meshData,
        Source2WholeMeshTransformAnalysis metadata,
        PlannedRawMbufTransformTarget target)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(drawCalls);
        ArgumentNullException.ThrowIfNull(meshData);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(target);
        var source = Source2RawMbufReader.AnalyzeDetailed(block, drawCalls, "planned raw-MBUF rewrite");
        ValidateSource(block, source, metadata, target);

        var transform = new UniformTransform(
            ToPoint(target.FrozenPivot),
            target.UniformScale,
            new Point3(0f, 0f, 0f));
        var vertices = source.Geometry.VertexBuffers[0];
        var selected = Enumerable.Range(0, vertices.Snapshot.VertexCount).ToArray();
        var transformed = DecodedPositionBufferTransformer.Apply(
            vertices.Decoded,
            vertices.Snapshot.VertexCount,
            vertices.Snapshot.PositionLayout,
            selected,
            transform);
        if (transformed.InputHash != target.DecodedVertexBufferHash
            || transformed.OutputHash != target.ExpectedDecodedVertexBufferHash
            || transformed.SelectedVertexSetHash != target.VertexSetHash
            || transformed.SelectedVertexCount != target.VertexCount
            || transformed.BoundsBefore != target.BeforeBounds
            || transformed.BoundsAfter != target.ExpectedAfterBounds
            || BitConverter.SingleToInt32Bits(transformed.MaximumDisplacement) != BitConverter.SingleToInt32Bits(target.MaximumDisplacement)
            || transformed.MaximumDisplacement > target.DisplacementLimit)
        {
            throw Drift("The recomputed visual transform differs from the coupled plan.");
        }

        var first = Source2RawMbufMutation.CreateTransformedPayload(block, source, transformed.TransformedDecoded.Span);
        var second = Source2RawMbufMutation.CreateTransformedPayload(block, source, transformed.TransformedDecoded.Span);
        if (!first.AsSpan().SequenceEqual(second)
            || ContentHash.Compute(first) != target.ExpectedMbufBlockHash)
        {
            throw Drift("Raw MBUF output is nondeterministic or differs from its planned hash.");
        }

        VerifyOnlyPositionBytesChanged(block.Payload.Span, first, source);
        var reopenedBlock = block with { Payload = first };
        var reopened = Source2RawMbufReader.AnalyzeDetailed(reopenedBlock, drawCalls, "reopened raw-MBUF rewrite");
        VerifyReopened(source, reopened, target);

        RewriteVisualBounds(meshData, target);
        var semanticHash = KvSemanticHasher.ComputeComplete(meshData);
        return new Source2RawMbufRewriteResult(first, reopened, semanticHash);
    }

    private static void ValidateSource(
        Source2ResourceBlock block,
        Source2RawMbufAnalysis source,
        Source2WholeMeshTransformAnalysis metadata,
        PlannedRawMbufTransformTarget target)
    {
        var vertices = source.Geometry.VertexBuffers[0].Snapshot;
        var indices = source.Geometry.IndexBuffers[0].Snapshot;
        if (block.Index != target.MbufResourceBlockIndex
            || ContentHash.Compute(block.Payload.Span) != target.MbufBlockInputHash
            || vertices.DecodedHash != target.DecodedVertexBufferHash
            || indices.DecodedHash != target.DecodedIndexBufferHash
            || vertices.VertexCount != target.VertexCount
            || vertices.PositionLayout != target.PositionLayout
            || source.Geometry.DrawCalls[0].Snapshot.VertexSetHash != target.VertexSetHash
            || !string.Equals(source.DrawCall.Id, target.DrawCallId, StringComparison.Ordinal)
            || !string.Equals(source.DrawCall.MaterialPath, target.MaterialPath, StringComparison.Ordinal)
            || metadata.SceneBounds != target.BeforeBounds
            || metadata.VertexSetHash != target.VertexSetHash
            || metadata.VertexCount != target.VertexCount
            || !string.Equals(metadata.LocalSkinningRootBone, target.SkinningRootBone, StringComparison.Ordinal)
            || metadata.LocalSkinningRootBoneHash != target.SkinningRootBoneHash
            || target.AllowedByteClasses is null
            || !target.AllowedByteClasses.SequenceEqual(AllowedByteClasses, StringComparer.Ordinal)
            || target.BoneBoundsTargets.Count == 0)
        {
            throw Drift("The current MBUF, ownership, metadata, or allowed-byte facts differ from the coupled plan.");
        }

        if (metadata.BoneBounds.Length != target.BoneBoundsTargets.Count
            || target.BoneBoundsTargets.Select(item => item.BoneIndex).Distinct().Count() != target.BoneBoundsTargets.Count
            || metadata.BoneBounds.Any(bone => !MatchesBone(bone, target.BoneBoundsTargets.FirstOrDefault(item => item.BoneIndex == bone.BoneIndex))))
        {
            throw Drift("The current raw-MBUF skinning or bone-culling facts differ from the coupled plan.");
        }
    }

    private static bool MatchesBone(Source2BoneBoundsAnalysis source, PlannedBoneBoundsTarget? target) =>
        target is not null
        && string.Equals(source.BoneName, target.BoneName, StringComparison.Ordinal)
        && source.InverseBindPoseHash == target.InverseBindPoseHash
        && new ContentHash(VertexSetHash.Compute(source.InfluencedVertices)) == target.InfluencedVertexSetHash
        && source.InfluencedVertices.Length == target.InfluencedVertexCount
        && source.LocalBoundsCenter == target.BeforeCenter
        && source.LocalBoundsSize == target.BeforeSize
        && source.LocalBounds == target.BeforeBounds
        && BitConverter.SingleToInt32Bits(source.SphereRadius) == BitConverter.SingleToInt32Bits(target.SphereRadius);

    private static void VerifyOnlyPositionBytesChanged(
        ReadOnlySpan<byte> before,
        ReadOnlySpan<byte> after,
        Source2RawMbufAnalysis source)
    {
        if (before.Length != after.Length)
        {
            throw Drift("The raw MBUF payload length changed.");
        }

        var positionLayout = source.Geometry.VertexBuffers[0].Snapshot.PositionLayout;
        for (var offset = 0; offset < before.Length; offset++)
        {
            var relative = offset - source.VertexDataOffset;
            var withinVertexData = relative >= 0 && relative < source.VertexDataLength;
            var recordOffset = withinVertexData ? relative % positionLayout.Stride : -1;
            var allowed = withinVertexData
                && recordOffset >= positionLayout.Offset
                && recordOffset < positionLayout.Offset + 12;
            if (!allowed && before[offset] != after[offset])
            {
                throw Drift($"Raw MBUF changed forbidden byte {offset}.");
            }
        }
    }

    private static void VerifyReopened(
        Source2RawMbufAnalysis before,
        Source2RawMbufAnalysis after,
        PlannedRawMbufTransformTarget target)
    {
        var beforeVertices = before.Geometry.VertexBuffers[0];
        var afterVertices = after.Geometry.VertexBuffers[0];
        var beforeIndices = before.Geometry.IndexBuffers[0];
        var afterIndices = after.Geometry.IndexBuffers[0];
        if (afterVertices.Snapshot.DecodedHash != target.ExpectedDecodedVertexBufferHash
            || after.Geometry.DrawCalls[0].Snapshot.Bounds != target.ExpectedAfterBounds
            || beforeIndices.Snapshot.DecodedHash != afterIndices.Snapshot.DecodedHash
            || !beforeIndices.Indices.SequenceEqual(afterIndices.Indices)
            || before.VertexDataOffset != after.VertexDataOffset
            || before.IndexDataOffset != after.IndexDataOffset
            || before.BlendIndicesOffset != after.BlendIndicesOffset)
        {
            throw Drift("Reopened MBUF geometry, indices, or layout differs from the plan.");
        }

        var layout = beforeVertices.Snapshot.PositionLayout;
        for (var vertex = 0; vertex < target.VertexCount; vertex++)
        {
            var start = vertex * layout.Stride;
            for (var offset = 0; offset < layout.Stride; offset++)
            {
                if (offset >= layout.Offset && offset < layout.Offset + 12)
                {
                    continue;
                }

                if (beforeVertices.Decoded[start + offset] != afterVertices.Decoded[start + offset])
                {
                    throw Drift($"Reopened MBUF changed non-position attribute byte {offset} of vertex {vertex}.");
                }
            }
        }
    }

    private static void RewriteVisualBounds(KVObject meshData, PlannedRawMbufTransformTarget target)
    {
        var sceneObjects = RequireArray(meshData, "m_sceneObjects", "raw-MBUF MDAT");
        if (sceneObjects.Count != 1)
        {
            throw Drift("Raw-MBUF MDAT no longer has exactly one scene object.");
        }

        var scene = RequireCollection(sceneObjects[0], "raw-MBUF MDAT scene");
        KvNumericMutation.ReplaceVector3(scene, "m_vMinBounds", target.BeforeBounds.Min, target.ExpectedAfterBounds.Min, "raw-MBUF MDAT scene");
        KvNumericMutation.ReplaceVector3(scene, "m_vMaxBounds", target.BeforeBounds.Max, target.ExpectedAfterBounds.Max, "raw-MBUF MDAT scene");
        if (!meshData.TryGetValue("m_skeleton", out var skeleton) || skeleton is null || !skeleton.IsCollection)
        {
            throw Drift("Raw-MBUF MDAT skeleton is missing.");
        }

        var bones = RequireArray(skeleton, "m_bones", "raw-MBUF MDAT skeleton");
        foreach (var planned in target.BoneBoundsTargets)
        {
            if ((uint)planned.BoneIndex >= (uint)bones.Count)
            {
                throw Drift("A planned raw-MBUF bone is outside the current skeleton.");
            }

            var bone = RequireCollection(bones[planned.BoneIndex], $"raw-MBUF bone {planned.BoneIndex}");
            if (!bone.TryGetValue("m_boneName", out var name)
                || name is null
                || name.ValueType != KVValueType.String
                || !string.Equals(name.ToString(System.Globalization.CultureInfo.InvariantCulture), planned.BoneName, StringComparison.Ordinal)
                || !bone.TryGetValue("m_bbox", out var box)
                || box is null
                || !box.IsCollection)
            {
                throw Drift("A raw-MBUF bone identity or bounds layout changed.");
            }

            KvNumericMutation.ReplaceVector3(box, "m_vecCenter", planned.BeforeCenter, planned.ExpectedCenter, $"raw-MBUF bone {planned.BoneIndex}");
            KvNumericMutation.ReplaceVector3(box, "m_vecSize", planned.BeforeSize, planned.ExpectedSize, $"raw-MBUF bone {planned.BoneIndex}");
            KvNumericMutation.ReplaceSingle(bone, "m_flSphereRadius", planned.SphereRadius, planned.ExpectedSphereRadius, $"raw-MBUF bone {planned.BoneIndex}");
        }
    }

    private static KVObject RequireArray(KVObject parent, string key, string context)
    {
        if (!parent.TryGetValue(key, out var value) || value is null || !value.IsArray)
        {
            throw Drift($"{context} is missing array {key}.");
        }

        return value;
    }

    private static KVObject RequireCollection(KVObject value, string context)
    {
        if (!value.IsCollection)
        {
            throw Drift($"{context} is not a collection.");
        }

        return value;
    }

    private static Point3 ToPoint(TransformVector3 value) => new(value.X, value.Y, value.Z);

    private static S2ModKitException Drift(string summary) => Errors.Verification(
        "COUPLED_TRANSFORM_INCOMPLETE",
        summary,
        "Discard the isolated visual result and regenerate the complete coupled plan.");
}
