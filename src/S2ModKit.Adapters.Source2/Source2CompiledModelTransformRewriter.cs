using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static bool IsTransformPlan(MutationPlan plan) =>
        plan.Operations is { Count: 1 }
        && plan.Operations[0].Kind == "transform_component"
        && plan.Operations[0].Version is 1 or 3;

    private static bool IsCoupledTransformPlan(MutationPlan plan) =>
        plan.Operations is { Count: 1 }
        && plan.Operations[0].Kind == "transform_component"
        && plan.Operations[0].Version == 2;

    private static bool CanRewriteCoupledTransform(ModelSnapshot model, MutationPlan plan)
    {
        if (!IsCoupledTransformPlan(plan)
            || plan.InputHash != model.Artifact.ContentHash
            || plan.Operations[0].CoupledTransformTarget is not { } coupled
            || plan.Operations[0].GeometryTargets.Count != 0
            || plan.Operations[0].DistanceFieldTargets.Count != 0
            || plan.Operations[0].SelectedDrawCalls.Count != 1)
        {
            return false;
        }

        var operation = plan.Operations[0];
        var expected = coupled.TargetBlocks.OrderBy(block => block.Index).ThenBy(block => block.Type, StringComparer.Ordinal).ToArray();
        var actual = operation.TargetBlocks.OrderBy(block => block.Index).ThenBy(block => block.Type, StringComparer.Ordinal).ToArray();
        var blocks = model.Artifact.Blocks.ToDictionary(block => (block.Index, block.Type));
        return expected.SequenceEqual(actual)
            && expected.Count(block => block.Type == "MDAT") == 1
            && expected.Count(block => block.Type == "MBUF") == 1
            && expected.Count(block => block.Type == "PHYS") == 1
            && expected.All(block => blocks.TryGetValue((block.Index, block.Type), out var snapshot)
                && snapshot.ContentHash == block.InputHash);
    }

    private static bool CanRewriteTransform(ModelSnapshot model, MutationPlan plan)
    {
        if (!IsTransformPlan(plan) || plan.InputHash != model.Artifact.ContentHash)
        {
            return false;
        }

        try
        {
            var operation = plan.Operations[0];
            if (operation.SelectedDrawCalls.Count == 0
                || operation.GeometryTargets.Count == 0
                || operation.TargetBlocks.Count == 0
                || operation.SelectedDrawCalls.Select(item => item.DrawCallId).Distinct(StringComparer.Ordinal).Count() != operation.SelectedDrawCalls.Count
                || operation.GeometryTargets.GroupBy(item => (item.Lod, item.MeshOrdinal, item.VertexBufferOrdinal)).Any(group => group.Count() != 1)
                || operation.DistanceFieldTargets.GroupBy(item => (item.ResourceBlockIndex, item.FieldIndex)).Any(group => group.Count() != 1))
            {
                return false;
            }

            var expectedBlocks = new Dictionary<int, string>();
            foreach (var target in operation.GeometryTargets)
            {
                if ((operation.Version != 3 && !TryAddExpectedBlock(expectedBlocks, target.ResourceBlockIndex, "MDAT"))
                    || !TryAddExpectedBlock(expectedBlocks, target.VertexResourceBlockIndex, "MVTX")
                    || target.IndexResourceBlockIndex == target.VertexResourceBlockIndex
                    || (operation.Version != 3 && target.BoneBoundsTargets.Count == 0)
                    || (operation.Version == 3 && target.ConnectedComponentIds.Count == 0))
                {
                    return false;
                }
            }

            foreach (var target in operation.DistanceFieldTargets)
            {
                if (!TryAddExpectedBlock(expectedBlocks, target.ResourceBlockIndex, "DSTF"))
                {
                    return false;
                }
            }

            var plannedBlocks = operation.TargetBlocks.GroupBy(item => item.Index).ToArray();
            if (plannedBlocks.Length != operation.TargetBlocks.Count || plannedBlocks.Length != expectedBlocks.Count)
            {
                return false;
            }

            if (plannedBlocks.Any(group => !expectedBlocks.TryGetValue(group.Key, out var type)
                || !string.Equals(group.Single().Type, type, StringComparison.Ordinal)))
            {
                return false;
            }

            var geometryMeshes = operation.GeometryTargets.Select(item => (item.Lod, item.MeshOrdinal)).ToHashSet();
            return operation.SelectedDrawCalls.All(item => geometryMeshes.Contains((item.Lod, item.MeshOrdinal)))
                && operation.GeometryTargets.All(target => operation.SelectedDrawCalls.Any(item => item.Lod == target.Lod && item.MeshOrdinal == target.MeshOrdinal));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return false;
        }
    }

    private static bool TryAddExpectedBlock(Dictionary<int, string> blocks, int index, string type)
    {
        if (index < 0)
        {
            return false;
        }

        if (!blocks.TryGetValue(index, out var existing))
        {
            blocks.Add(index, type);
            return true;
        }

        return string.Equals(existing, type, StringComparison.Ordinal);
    }

    private RewriteCandidate RewriteTransform(
        ArtifactContent input,
        ModelSnapshot model,
        MutationPlan plan,
        CancellationToken cancellationToken)
    {
        try
        {
            return RewriteTransformCore(input, model, plan, cancellationToken);
        }
        catch (S2ModKitException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or OverflowException
            or IndexOutOfRangeException
            or KeyNotFoundException)
        {
            throw new S2ModKitException(
                new S2Error(
                    "TRANSFORM_SOURCE2_REWRITE_UNSUPPORTED",
                    "source2_adapter",
                    $"The transform candidate could not be rewritten and reopened safely: {exception.Message}",
                    "Reject the candidate and add a reviewed profile for the exact semantic layout before retrying.",
                    ErrorCategory.UnsupportedCapability),
                exception);
        }
    }

    private RewriteCandidate RewriteTransformCore(
        ArtifactContent input,
        ModelSnapshot model,
        MutationPlan plan,
        CancellationToken cancellationToken)
    {
        using var parsed = Parse(input, retainGeometryAnalysis: true);
        ValidateSnapshotAgreement(model, parsed.Snapshot);
        var operation = plan.Operations.Single();
        ValidateTransformTargetBlocks(parsed, operation);

        var replacements = new Dictionary<int, ReadOnlyMemory<byte>>();
        var intendedDecoded = new Dictionary<int, byte[]>();
        var expectedSemanticHashes = new Dictionary<int, ContentHash>();
        using var codec = OpenGeometryCodec();
        foreach (var target in operation.GeometryTargets.OrderBy(item => item.Lod).ThenBy(item => item.MeshOrdinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = ValidateTransformTarget(parsed, input, operation, target, codec.Identity);
            var transform = new UniformTransform(
                ToPoint(target.FrozenPivot),
                target.UniformScale,
                ToPoint(target.Translation));
            var selectedVertices = operation.Version == 3
                ? ResolveConnectedComponentVertices(profile.Mesh, target)
                : Enumerable.Range(0, profile.Vertices.Snapshot.VertexCount).ToArray();
            var transformed = DecodedPositionBufferTransformer.Apply(
                profile.Vertices.Decoded,
                profile.Vertices.Snapshot.VertexCount,
                profile.Vertices.Snapshot.PositionLayout,
                selectedVertices,
                transform);
            ValidateTransformResult(target, transformed);
            VerifyOnlyPositionBytesChanged(
                profile.Vertices.Decoded,
                transformed.TransformedDecoded.Span,
                profile.Vertices.Snapshot.PositionLayout,
                selectedVertices);

            var intended = transformed.TransformedDecoded.ToArray();
            var encoded = EncodeDeterministically(codec, intended, profile.Vertices.Snapshot, $"LOD {target.Lod} MVTX block {target.VertexResourceBlockIndex}");
            var reopenedDecoded = codec.DecodeVertexBuffer(encoded, profile.Vertices.Snapshot.VertexCount, profile.Vertices.Snapshot.Stride);
            if (!reopenedDecoded.AsSpan().SequenceEqual(intended))
            {
                throw new InvalidDataException($"LOD {target.Lod} MVTX changed decoded bytes during encode/decode verification.");
            }

            replacements.Add(target.VertexResourceBlockIndex, encoded);
            intendedDecoded.Add(target.VertexResourceBlockIndex, intended);
            if (operation.Version != 3)
            {
                UpdateMeshMetadata(profile.Mesh.Block.Data, target);
                expectedSemanticHashes.Add(target.ResourceBlockIndex, KvSemanticHasher.ComputeComplete(profile.Mesh.Block.Data));
                replacements.Add(
                    target.ResourceBlockIndex,
                    SerializeDeterministically(profile.Mesh.Block.Serialize, $"LOD {target.Lod} MDAT block {target.ResourceBlockIndex}"));
            }
        }

        foreach (var blockGroup in operation.DistanceFieldTargets.GroupBy(item => item.ResourceBlockIndex).OrderBy(group => group.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (parsed.Resource.Blocks[blockGroup.Key] is not BinaryKV3 block
                || !string.Equals(block.Type.ToString(), "DSTF", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Planned DSTF block {blockGroup.Key} is not a BinaryKV3 distance-field block.");
            }

            UpdateDistanceFields(block, blockGroup.OrderBy(item => item.FieldIndex).ToArray());
            var roundTrip = BinaryKv3BlockRoundTrip.SerializeAndVerify(block, $"DSTF block {blockGroup.Key}");
            expectedSemanticHashes.Add(blockGroup.Key, roundTrip.SemanticHash);
            replacements.Add(blockGroup.Key, roundTrip.Payload);
        }

        var plannedBlockIndices = operation.TargetBlocks.Select(item => item.Index).ToHashSet();
        if (!plannedBlockIndices.SetEquals(replacements.Keys))
        {
            throw new InvalidDataException("Transform replacements do not exactly cover the planned target-block set.");
        }

        var candidateBytes = ResourceEnvelopeWriter.Rebuild(parsed.Envelope, replacements);
        var candidateArtifact = new ArtifactContent(input.LogicalPath, ContentHash.Compute(candidateBytes), candidateBytes);
        using var reopened = Parse(candidateArtifact, retainGeometryAnalysis: true);
        VerifyTransformReopen(
            model,
            parsed,
            reopened,
            operation,
            replacements,
            intendedDecoded,
            expectedSemanticHashes);
        return new RewriteCandidate(input.LogicalPath, candidateBytes, reopened.Snapshot);
    }

    private static void ValidateTransformTargetBlocks(ParsedModel parsed, PlannedOperation operation)
    {
        var expected = new Dictionary<int, string>();
        foreach (var target in operation.GeometryTargets)
        {
            if (operation.Version != 3)
            {
                _ = TryAddExpectedBlock(expected, target.ResourceBlockIndex, "MDAT");
            }
            _ = TryAddExpectedBlock(expected, target.VertexResourceBlockIndex, "MVTX");
        }

        foreach (var target in operation.DistanceFieldTargets)
        {
            _ = TryAddExpectedBlock(expected, target.ResourceBlockIndex, "DSTF");
        }

        var planned = operation.TargetBlocks.ToDictionary(item => item.Index);
        if (!expected.Keys.ToHashSet().SetEquals(planned.Keys))
        {
            throw Errors.Verification("TRANSFORM_TARGET_BLOCK_SET_DRIFT", "The transform plan does not exactly cover its geometry and metadata blocks.", "Regenerate the plan from the immutable input.");
        }

        foreach (var entry in expected)
        {
            if ((uint)entry.Key >= (uint)parsed.Envelope.Blocks.Count)
            {
                throw Errors.Verification("TRANSFORM_TARGET_BLOCK_RANGE_DRIFT", $"Planned block {entry.Key} is outside the current resource.", "Regenerate the plan from the immutable input.");
            }

            var raw = parsed.Envelope.Blocks[entry.Key];
            var fingerprint = planned[entry.Key];
            if (!string.Equals(raw.Type, entry.Value, StringComparison.Ordinal)
                || !string.Equals(fingerprint.Type, entry.Value, StringComparison.Ordinal)
                || ContentHash.Compute(raw.Payload.Span) != fingerprint.InputHash)
            {
                throw Errors.Verification("TRANSFORM_TARGET_BLOCK_FINGERPRINT_DRIFT", $"Planned block {entry.Key} no longer matches its type and input hash.", "Regenerate the plan from the immutable input.");
            }
        }
    }

    private static TransformRewriteProfile ValidateTransformTarget(
        ParsedModel parsed,
        ArtifactContent input,
        PlannedOperation operation,
        PlannedGeometryTarget target,
        GeometryCodecIdentity codecIdentity)
    {
        if (!parsed.MeshesByOrdinal.TryGetValue(target.MeshOrdinal, out var mesh)
            || mesh.Lod != target.Lod
            || mesh.BlockIndex != target.ResourceBlockIndex
            || !string.Equals(StableIdentity.NormalizePath(target.ResourcePath), input.LogicalPath, StringComparison.Ordinal)
            || mesh.GeometryAnalysis is null
            || mesh.Geometry.Codec != codecIdentity)
        {
            throw Errors.Verification("TRANSFORM_GEOMETRY_LOCATION_DRIFT", $"LOD {target.Lod} mesh {target.MeshOrdinal} no longer matches its planned geometry location and codec.", "Regenerate the plan from the immutable input.");
        }

        var selected = operation.SelectedDrawCalls.Where(item => item.Lod == target.Lod && item.MeshOrdinal == target.MeshOrdinal).ToArray();
        var selectedIds = selected.Select(item => item.DrawCallId).ToHashSet(StringComparer.Ordinal);
        if ((operation.Version != 3 && selected.Length != mesh.DrawCalls.Count)
            || (operation.Version != 3 && !selectedIds.SetEquals(mesh.DrawCalls.Select(item => item.Snapshot.Id)))
            || (operation.Version == 3 && target.ConnectedComponentIds.Count == 0)
            || selected.Any(item => !string.Equals(StableIdentity.NormalizePath(item.ResourcePath), input.LogicalPath, StringComparison.Ordinal)
                || !Matches(mesh.DrawCalls.Single(location => location.Snapshot.Id == item.DrawCallId).Snapshot, item)))
        {
            throw Errors.Verification("TRANSFORM_DRAW_CALL_DRIFT", $"LOD {target.Lod} mesh {target.MeshOrdinal} is no longer selected as one complete draw-call set.", "Regenerate the plan from the immutable input.");
        }

        var geometry = mesh.GeometryAnalysis;
        if ((uint)target.VertexBufferOrdinal >= (uint)geometry.VertexBuffers.Count
            || (uint)target.IndexBufferOrdinal >= (uint)geometry.IndexBuffers.Count)
        {
            throw Errors.Verification("TRANSFORM_BUFFER_ORDINAL_DRIFT", $"LOD {target.Lod} planned buffer ordinal is outside the current mesh.", "Regenerate the plan from the immutable input.");
        }

        var vertices = geometry.VertexBuffers[target.VertexBufferOrdinal];
        var indices = geometry.IndexBuffers[target.IndexBufferOrdinal];
        var metadata = operation.Version == 3
            ? null
            : Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(mesh.Descriptor, mesh.Block.Data, geometry, $"embedded mesh {mesh.MeshOrdinal}");
        var connectedVertices = operation.Version == 3 ? ResolveConnectedComponentVertices(mesh, target) : null;
        var connectedSetHash = connectedVertices is null ? default : new ContentHash(VertexSetHash.Compute(connectedVertices));
        if (vertices.Snapshot.ResourceBlockIndex != target.VertexResourceBlockIndex
            || indices.Snapshot.ResourceBlockIndex != target.IndexResourceBlockIndex
            || vertices.Snapshot.EncodedHash != target.VertexBlockInputHash
            || indices.Snapshot.EncodedHash != target.IndexBlockInputHash
            || vertices.Snapshot.DecodedHash != target.DecodedVertexBufferHash
            || indices.Snapshot.DecodedHash != target.DecodedIndexBufferHash
            || vertices.Snapshot.PositionLayout != target.PositionLayout
            || (operation.Version != 3 && (metadata!.VertexSetHash != target.VertexSetHash
                || metadata.VertexCount != target.SelectedVertexCount
                || metadata.SceneBounds != target.BeforeBounds))
            || (operation.Version == 3 && (connectedSetHash != target.VertexSetHash
                || connectedVertices!.Length != target.SelectedVertexCount))
            || target.AllowedChangedAttributes.Count != 1
            || !string.Equals(target.AllowedChangedAttributes[0], "position", StringComparison.Ordinal)
            || target.Codec != codecIdentity)
        {
            throw Errors.Verification("TRANSFORM_GEOMETRY_FACT_DRIFT", $"LOD {target.Lod} geometry facts no longer match the dry-run plan.", "Regenerate the plan from the immutable input.");
        }

        var transform = new UniformTransform(ToPoint(target.FrozenPivot), target.UniformScale, ToPoint(target.Translation));
        var replannedBones = operation.Version == 3
            ? []
            : metadata!.BoneBounds.Select(bone => PlanBoneBounds(
                bone,
                vertices,
                transform,
                target.FrozenPivot,
                target.Translation)).ToArray();
        if (!target.BoneBoundsTargets.SequenceEqual(replannedBones))
        {
            throw Errors.Verification("TRANSFORM_BONE_BOUNDS_DRIFT", $"LOD {target.Lod} bone-culling facts no longer match the dry-run plan.", "Regenerate the plan from the immutable input.");
        }

        return new TransformRewriteProfile(mesh, vertices, indices);
    }

    private static int[] ResolveConnectedComponentVertices(ParsedMesh mesh, PlannedGeometryTarget target)
    {
        var geometry = mesh.GeometryAnalysis
            ?? throw new InvalidDataException($"LOD {target.Lod} has no decoded connected-component geometry.");
        var byId = geometry.ConnectedComponents.ToDictionary(item => item.Snapshot.Id, StringComparer.Ordinal);
        var selectedDrawCallIds = target.ConnectedComponentIds.Select(id =>
        {
            if (!byId.TryGetValue(id, out var component))
            {
                throw new InvalidDataException($"LOD {target.Lod} connected component '{id}' is missing.");
            }

            if (!component.Snapshot.ExclusivelyOwned
                || component.Snapshot.VertexBufferOrdinal != target.VertexBufferOrdinal
                || component.Snapshot.IndexBufferOrdinal != target.IndexBufferOrdinal)
            {
                throw new InvalidDataException($"LOD {target.Lod} connected component '{id}' changed ownership or buffer location.");
            }

            return component;
        }).ToArray();
        if (selectedDrawCallIds.Length == 0)
        {
            throw new InvalidDataException($"LOD {target.Lod} connected-component selection is empty.");
        }

        return selectedDrawCallIds.SelectMany(item => item.VertexIndices).Distinct().Order().ToArray();
    }

    private static void ValidateTransformResult(PlannedGeometryTarget target, DecodedPositionTransformResult result)
    {
        if (result.InputHash != target.DecodedVertexBufferHash
            || result.OutputHash != target.ExpectedDecodedVertexBufferHash
            || result.SelectedVertexSetHash != target.VertexSetHash
            || result.SelectedVertexCount != target.SelectedVertexCount
            || result.BoundsBefore != target.BeforeBounds
            || result.BoundsAfter != target.ExpectedAfterBounds
            || result.ChangedVertexCount < 1
            || BitConverter.SingleToInt32Bits(result.MaximumDisplacement) != BitConverter.SingleToInt32Bits(target.MaximumDisplacement))
        {
            throw Errors.Verification("TRANSFORM_RESULT_DRIFT", $"LOD {target.Lod} decoded position result differs from the dry-run plan.", "Reject the candidate and regenerate the plan with the current geometry kernel.");
        }
    }

    private static byte[] EncodeDeterministically(
        IMeshOptimizerCodec codec,
        byte[] decoded,
        VertexBufferSnapshot snapshot,
        string context)
    {
        var first = codec.EncodeVertexBuffer(decoded, snapshot.VertexCount, snapshot.Stride, version: 1);
        var second = codec.EncodeVertexBuffer(decoded, snapshot.VertexCount, snapshot.Stride, version: 1);
        if (!first.AsSpan().SequenceEqual(second))
        {
            throw new InvalidDataException($"{context} meshoptimizer encoding is not deterministic.");
        }

        return first;
    }

    private static void VerifyOnlyPositionBytesChanged(
        ReadOnlySpan<byte> before,
        ReadOnlySpan<byte> after,
        PositionLayout layout,
        IReadOnlyCollection<int> selectedVertices)
    {
        if (before.Length != after.Length)
        {
            throw new InvalidDataException("Decoded MVTX length changed during position transform.");
        }

        var selected = selectedVertices.ToHashSet();
        for (var vertex = 0; vertex < before.Length / layout.Stride; vertex++)
        {
            var recordOffset = checked(vertex * layout.Stride);
            for (var offset = 0; offset < layout.Stride; offset++)
            {
                var positionByte = selected.Contains(vertex)
                    && offset >= layout.Offset
                    && offset < layout.Offset + (sizeof(float) * 3);
                if (!positionByte && before[recordOffset + offset] != after[recordOffset + offset])
                {
                    throw new InvalidDataException($"Decoded MVTX changed non-position byte {offset} of vertex {vertex}.");
                }
            }
        }
    }

    private static void UpdateMeshMetadata(KVObject meshData, PlannedGeometryTarget target)
    {
        var sceneObjects = RequireArray(meshData, "m_sceneObjects", $"MDAT mesh {target.MeshOrdinal}");
        if (sceneObjects.Count != 1)
        {
            throw new InvalidDataException($"MDAT mesh {target.MeshOrdinal} no longer has exactly one scene object.");
        }

        var scene = RequireCollection(sceneObjects[0], $"MDAT mesh {target.MeshOrdinal}.m_sceneObjects[0]");
        KvNumericMutation.ReplaceVector3(scene, "m_vMinBounds", target.BeforeBounds.Min, target.ExpectedAfterBounds.Min, $"MDAT mesh {target.MeshOrdinal}.scene");
        KvNumericMutation.ReplaceVector3(scene, "m_vMaxBounds", target.BeforeBounds.Max, target.ExpectedAfterBounds.Max, $"MDAT mesh {target.MeshOrdinal}.scene");

        if (!meshData.TryGetValue("m_skeleton", out var skeleton) || skeleton is null || !skeleton.IsCollection)
        {
            throw new InvalidDataException($"MDAT mesh {target.MeshOrdinal} has no supported skeleton collection.");
        }

        var bones = RequireArray(skeleton, "m_bones", $"MDAT mesh {target.MeshOrdinal}.m_skeleton");
        foreach (var planned in target.BoneBoundsTargets)
        {
            if ((uint)planned.BoneIndex >= (uint)bones.Count)
            {
                throw new InvalidDataException($"MDAT mesh {target.MeshOrdinal} planned bone {planned.BoneIndex} is out of range.");
            }

            var bone = RequireCollection(bones[planned.BoneIndex], $"MDAT mesh {target.MeshOrdinal}.m_skeleton.m_bones[{planned.BoneIndex}]");
            if (!bone.TryGetValue("m_boneName", out var name)
                || name is null
                || name.ValueType != KVValueType.String
                || !string.Equals(name.ToString(System.Globalization.CultureInfo.InvariantCulture), planned.BoneName, StringComparison.Ordinal)
                || !bone.TryGetValue("m_bbox", out var box)
                || box is null
                || !box.IsCollection)
            {
                throw new InvalidDataException($"MDAT mesh {target.MeshOrdinal} bone {planned.BoneIndex} identity or bounds layout drifted.");
            }

            KvNumericMutation.ReplaceVector3(box, "m_vecCenter", planned.BeforeCenter, planned.ExpectedCenter, $"MDAT mesh {target.MeshOrdinal}.bone[{planned.BoneIndex}].m_bbox");
            KvNumericMutation.ReplaceVector3(box, "m_vecSize", planned.BeforeSize, planned.ExpectedSize, $"MDAT mesh {target.MeshOrdinal}.bone[{planned.BoneIndex}].m_bbox");
            KvNumericMutation.ReplaceSingle(bone, "m_flSphereRadius", planned.SphereRadius, planned.ExpectedSphereRadius, $"MDAT mesh {target.MeshOrdinal}.bone[{planned.BoneIndex}]");
        }
    }

    private static void UpdateDistanceFields(BinaryKV3 block, PlannedDistanceFieldTarget[] targets)
    {
        var current = Source2TransformMetadataAnalyzer.ReadDistanceFields(block.Data.Root, targets[0].ResourceBlockIndex, $"DSTF block {targets[0].ResourceBlockIndex}");
        var fields = RequireArray(block.Data.Root, "m_distanceFields", $"DSTF block {targets[0].ResourceBlockIndex}");
        foreach (var target in targets)
        {
            if ((uint)target.FieldIndex >= (uint)current.Count
                || !MatchesDistanceField(current[target.FieldIndex], target, afterMutation: false))
            {
                throw Errors.Verification("TRANSFORM_DISTANCE_FIELD_DRIFT", $"DSTF block {target.ResourceBlockIndex} field {target.FieldIndex} no longer matches the dry-run plan.", "Regenerate the plan from the immutable input.");
            }

            var field = RequireCollection(fields[target.FieldIndex], $"DSTF block {target.ResourceBlockIndex}.m_distanceFields[{target.FieldIndex}]");
            if (!field.TryGetValue("m_bounds", out var bounds) || bounds is null || !bounds.IsCollection)
            {
                throw new InvalidDataException($"DSTF block {target.ResourceBlockIndex} field {target.FieldIndex} has no supported bounds collection.");
            }

            KvNumericMutation.ReplaceVector3(bounds, "m_vMinBounds", target.BeforeBounds.Min, target.ExpectedAfterBounds.Min, $"DSTF block {target.ResourceBlockIndex}.field[{target.FieldIndex}].m_bounds");
            KvNumericMutation.ReplaceVector3(bounds, "m_vMaxBounds", target.BeforeBounds.Max, target.ExpectedAfterBounds.Max, $"DSTF block {target.ResourceBlockIndex}.field[{target.FieldIndex}].m_bounds");
            KvNumericMutation.ReplaceSingle(field, "m_flGridCellSize", target.GridCellSize, target.ExpectedGridCellSize, $"DSTF block {target.ResourceBlockIndex}.field[{target.FieldIndex}]");
            KvNumericMutation.ReplaceSingle(field, "m_flMaxQuantizedDistance", target.MaximumQuantizedDistance, target.ExpectedMaximumQuantizedDistance, $"DSTF block {target.ResourceBlockIndex}.field[{target.FieldIndex}]");
        }
    }

    private static bool MatchesDistanceField(
        Source2DistanceFieldAnalysis actual,
        PlannedDistanceFieldTarget planned,
        bool afterMutation) =>
        actual.ResourceBlockIndex == planned.ResourceBlockIndex
        && actual.FieldIndex == planned.FieldIndex
        && actual.ParentBoneNameHash == planned.ParentBoneNameHash
        && actual.BodyGroupIndex == planned.BodyGroupIndex
        && actual.BodyGroupChoice == planned.BodyGroupChoice
        && actual.ResolutionX == planned.ResolutionX
        && actual.ResolutionY == planned.ResolutionY
        && actual.ResolutionZ == planned.ResolutionZ
        && actual.Bounds == (afterMutation ? planned.ExpectedAfterBounds : planned.BeforeBounds)
        && HasIdenticalSingle(actual.GridCellSize, afterMutation ? planned.ExpectedGridCellSize : planned.GridCellSize)
        && HasIdenticalSingle(actual.MaximumQuantizedDistance, afterMutation ? planned.ExpectedMaximumQuantizedDistance : planned.MaximumQuantizedDistance)
        && HasIdenticalSingle(actual.SurfaceBias, planned.SurfaceBias)
        && actual.IsTwoSided == planned.IsTwoSided
        && actual.IsFarFieldOnly == planned.IsFarFieldOnly
        && actual.UseForOcclusion == planned.UseForOcclusion
        && actual.UseForCollision == planned.UseForCollision
        && actual.QuantizedDataHash == planned.QuantizedDataHash
        && actual.QuantizedDataLength == planned.QuantizedDataLength;

    private static bool HasIdenticalSingle(float left, float right) =>
        BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

    private static byte[] SerializeDeterministically(Action<Stream> serialize, string context)
    {
        static byte[] SerializeOnce(Action<Stream> action)
        {
            using var stream = new MemoryStream();
            action(stream);
            return stream.ToArray();
        }

        var first = SerializeOnce(serialize);
        var second = SerializeOnce(serialize);
        if (first.Length == 0 || !first.AsSpan().SequenceEqual(second))
        {
            throw new InvalidDataException($"{context} serialization is empty or non-deterministic.");
        }

        return first;
    }

    private static void VerifyTransformReopen(
        ModelSnapshot before,
        ParsedModel source,
        ParsedModel candidate,
        PlannedOperation operation,
        Dictionary<int, ReadOnlyMemory<byte>> replacements,
        Dictionary<int, byte[]> intendedDecoded,
        Dictionary<int, ContentHash> expectedSemanticHashes)
    {
        if (!Flatten(before).SequenceEqual(Flatten(candidate.Snapshot)))
        {
            throw Errors.Verification("TRANSFORM_DRAW_CALL_REOPEN_DRIFT", "Draw-call semantics changed after transform rewrite.", "Reject the candidate; no build should be published.");
        }

        var targetIndices = operation.TargetBlocks.Select(item => item.Index).ToHashSet();
        if (source.Envelope.Blocks.Count != candidate.Envelope.Blocks.Count)
        {
            throw Errors.Verification("TRANSFORM_BLOCK_INVENTORY_DRIFT", "Resource block count changed after transform rewrite.", "Reject the candidate; no build should be published.");
        }

        for (var index = 0; index < source.Envelope.Blocks.Count; index++)
        {
            var original = source.Envelope.Blocks[index];
            var reopened = candidate.Envelope.Blocks[index];
            if (!string.Equals(original.Type, reopened.Type, StringComparison.Ordinal)
                || (targetIndices.Contains(index)
                    ? !replacements[index].Span.SequenceEqual(reopened.Payload.Span) || original.Payload.Span.SequenceEqual(reopened.Payload.Span)
                    : !original.Payload.Span.SequenceEqual(reopened.Payload.Span)))
            {
                throw Errors.Verification("TRANSFORM_BLOCK_AUDIT_FAILED", $"Resource block {index} failed the target/non-target byte audit.", "Reject the candidate; no build should be published.");
            }
        }

        foreach (var target in operation.GeometryTargets)
        {
            if (!candidate.MeshesByOrdinal.TryGetValue(target.MeshOrdinal, out var mesh)
                || mesh.GeometryAnalysis is null
                || mesh.Lod != target.Lod
                || mesh.BlockIndex != target.ResourceBlockIndex)
            {
                throw Errors.Verification("TRANSFORM_GEOMETRY_REOPEN_MISSING", $"LOD {target.Lod} transformed mesh is unavailable after reopen.", "Reject the candidate; no build should be published.");
            }

            var geometry = mesh.GeometryAnalysis;
            var vertices = geometry.VertexBuffers[target.VertexBufferOrdinal];
            var indices = geometry.IndexBuffers[target.IndexBufferOrdinal];
            var intended = intendedDecoded[target.VertexResourceBlockIndex];
            if (!vertices.Decoded.AsSpan().SequenceEqual(intended)
                || vertices.Snapshot.DecodedHash != ContentHash.Compute(intended)
                || vertices.Snapshot.EncodedHash != ContentHash.Compute(replacements[target.VertexResourceBlockIndex].Span)
                || indices.Snapshot.EncodedHash != target.IndexBlockInputHash
                || indices.Snapshot.DecodedHash != target.DecodedIndexBufferHash
                || mesh.Geometry.Codec != target.Codec
                || (operation.Version != 3 && KvSemanticHasher.ComputeComplete(mesh.Block.Data) != expectedSemanticHashes[target.ResourceBlockIndex]))
            {
                throw Errors.Verification("TRANSFORM_GEOMETRY_REOPEN_DRIFT", $"LOD {target.Lod} transformed buffers or MDAT semantics differ after reopen.", "Reject the candidate; no build should be published.");
            }

            if (operation.Version == 3)
            {
                var selectedVertices = ResolveConnectedComponentVertices(mesh, target);
                if (selectedVertices.Length != target.SelectedVertexCount
                    || new ContentHash(VertexSetHash.Compute(selectedVertices)) != target.VertexSetHash)
                {
                    throw Errors.Verification("TRANSFORM_COMPONENT_REOPEN_DRIFT", $"LOD {target.Lod} connected-component identities changed after reopen.", "Reject the candidate; no build should be published.");
                }

                continue;
            }

            var metadata = Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(mesh.Descriptor, mesh.Block.Data, geometry, $"reopened embedded mesh {mesh.MeshOrdinal}");
            if (metadata.SceneBounds != target.ExpectedAfterBounds
                || metadata.VertexSetHash != target.VertexSetHash
                || metadata.VertexCount != target.SelectedVertexCount
                || metadata.BoneBounds.Length != target.BoneBoundsTargets.Count)
            {
                throw Errors.Verification("TRANSFORM_METADATA_REOPEN_DRIFT", $"LOD {target.Lod} scene or bone-culling inventory differs after reopen.", "Reject the candidate; no build should be published.");
            }

            for (var boneIndex = 0; boneIndex < metadata.BoneBounds.Length; boneIndex++)
            {
                var actual = metadata.BoneBounds[boneIndex];
                var planned = target.BoneBoundsTargets[boneIndex];
                if (actual.BoneIndex != planned.BoneIndex
                    || !string.Equals(actual.BoneName, planned.BoneName, StringComparison.Ordinal)
                    || actual.InverseBindPoseHash != planned.InverseBindPoseHash
                    || new ContentHash(VertexSetHash.Compute(actual.InfluencedVertices)) != planned.InfluencedVertexSetHash
                    || actual.InfluencedVertices.Length != planned.InfluencedVertexCount
                    || actual.LocalBoundsCenter != planned.ExpectedCenter
                    || actual.LocalBoundsSize != planned.ExpectedSize
                    || actual.LocalBounds != planned.ExpectedAfterBounds
                    || !HasIdenticalSingle(actual.SphereRadius, planned.ExpectedSphereRadius))
                {
                    throw Errors.Verification("TRANSFORM_BONE_BOUNDS_REOPEN_DRIFT", $"LOD {target.Lod} bone '{planned.BoneName}' culling facts differ after reopen.", "Reject the candidate; no build should be published.");
                }
            }
        }

        foreach (var group in operation.DistanceFieldTargets.GroupBy(item => item.ResourceBlockIndex))
        {
            if (candidate.Resource.Blocks[group.Key] is not BinaryKV3 block
                || KvSemanticHasher.ComputeComplete(block.Data.Root) != expectedSemanticHashes[group.Key])
            {
                throw Errors.Verification("TRANSFORM_DISTANCE_FIELD_REOPEN_DRIFT", $"DSTF block {group.Key} semantic hash differs after reopen.", "Reject the candidate; no build should be published.");
            }

            var actual = Source2TransformMetadataAnalyzer.ReadDistanceFields(block.Data.Root, group.Key, $"reopened DSTF block {group.Key}");
            if (group.Any(planned => (uint)planned.FieldIndex >= (uint)actual.Count || !MatchesDistanceField(actual[planned.FieldIndex], planned, afterMutation: true)))
            {
                throw Errors.Verification("TRANSFORM_DISTANCE_FIELD_REOPEN_DRIFT", $"DSTF block {group.Key} planned field semantics differ after reopen.", "Reject the candidate; no build should be published.");
            }
        }
    }

    private sealed record TransformRewriteProfile(
        ParsedMesh Mesh,
        Source2VertexBufferAnalysis Vertices,
        Source2IndexBufferAnalysis Indices);
}
