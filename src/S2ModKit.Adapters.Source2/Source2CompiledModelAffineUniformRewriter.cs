using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static bool IsAffineTransformPlan(MutationPlan plan) =>
        plan.Operations is [{ Kind: "transform_component", Version: 4, AffineTransformTarget: not null }];

    private static bool IsPositionOnlyAffineTransform(PlannedAffineTransformTarget affine) =>
        affine.Rotation.Kind == "identity"
        && affine.Scale.X == affine.Scale.Y
        && affine.Scale.Y == affine.Scale.Z;

    private static bool CanRewriteAffineTransform(ModelSnapshot model, MutationPlan plan)
    {
        if (!IsAffineTransformPlan(plan) || plan.InputHash != model.Artifact.ContentHash)
        {
            return false;
        }

        try
        {
            var operation = plan.Operations[0];
            var affine = operation.AffineTransformTarget!;
            var positionOnly = IsPositionOnlyAffineTransform(affine);
            var expectedAttributes = positionOnly ? new[] { "position" } : ["normal_tangent", "position"];
            if (operation.GeometryTargets.Count != 0
                || operation.DistanceFieldTargets.Count != 0
                || operation.CoupledTransformTarget is not null
                || operation.SelectedDrawCalls.Count == 0
                || affine.GeometryTargets.Count == 0
                || affine.GeometryTargets.Any(target => !target.AllowedChangedAttributes.SequenceEqual(expectedAttributes, StringComparer.Ordinal)
                    || (positionOnly
                        ? target.InputPackedFrameHash != target.ExpectedPackedFrameHash
                        : target.InputPackedFrameHash == target.ExpectedPackedFrameHash)
                    || target.BoneBoundsTargets.Count == 0))
            {
                return false;
            }

            var expected = new Dictionary<int, string>();
            foreach (var target in affine.GeometryTargets)
            {
                if (!TryAddExpectedBlock(expected, target.ResourceBlockIndex, "MDAT")
                    || !TryAddExpectedBlock(expected, target.VertexResourceBlockIndex, "MVTX")
                    || target.IndexResourceBlockIndex == target.VertexResourceBlockIndex)
                {
                    return false;
                }
            }

            var modelBlocks = model.Artifact.Blocks.ToDictionary(block => block.Index);
            return operation.TargetBlocks.Count == expected.Count
                && operation.TargetBlocks.All(block => expected.TryGetValue(block.Index, out var type)
                    && type == block.Type
                    && modelBlocks.TryGetValue(block.Index, out var modelBlock)
                    && modelBlock.Type == block.Type
                    && modelBlock.ContentHash == block.InputHash);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return false;
        }
    }

    private RewriteCandidate RewriteAffineTransform(
        ArtifactContent input,
        ModelSnapshot model,
        MutationPlan plan,
        CancellationToken cancellationToken)
    {
        try
        {
            using var parsed = Parse(input, retainGeometryAnalysis: true);
            ValidateSnapshotAgreement(model, parsed.Snapshot);
            var operation = plan.Operations.Single();
            var affine = operation.AffineTransformTarget!;
            var positionOnly = IsPositionOnlyAffineTransform(affine);
            ValidateAffineTargetBlocks(parsed, operation, affine);

            var replacements = new Dictionary<int, ReadOnlyMemory<byte>>();
            var intendedDecoded = new Dictionary<int, byte[]>();
            var expectedSemanticHashes = new Dictionary<int, ContentHash>();
            using var codec = OpenGeometryCodec();
            foreach (var target in affine.GeometryTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var profile = ValidateAffineUniformTarget(parsed, input, operation, target, codec.Identity, positionOnly);
                var selectedVertices = ResolveAffineVertices(profile.Mesh, operation, target);
                var transform = CreateAffineTransform(affine);
                var intended = (byte[])profile.Vertices.Decoded.Clone();
                var beforePoints = selectedVertices
                    .Select(vertex => Source2GeometryAnalyzer.ReadPosition(profile.Vertices, vertex))
                    .ToArray();
                var afterPoints = new Point3[selectedVertices.Length];
                var summary = transform.ApplyAndSummarize(beforePoints, afterPoints);
                for (var vertexIndex = 0; vertexIndex < selectedVertices.Length; vertexIndex++)
                {
                    WritePosition(intended, profile.Vertices.Snapshot.PositionLayout, selectedVertices[vertexIndex], afterPoints[vertexIndex]);
                }
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.PositionsTransformed);

                if (ContentHash.Compute(profile.Vertices.Decoded) != target.DecodedVertexBufferHash
                    || new ContentHash(VertexSetHash.Compute(selectedVertices)) != target.VertexSetHash
                    || selectedVertices.Length != target.SelectedVertexCount
                    || ToAffineBounds(summary.BeforeBounds) != target.SelectionBeforeBounds
                    || ToAffineBounds(summary.AfterBounds) != target.SelectionExpectedAfterBounds
                    || summary.ChangedPointCount < 1
                    || !HasIdenticalSingle(summary.MaximumDisplacement, target.MaximumDisplacement))
                {
                    throw Errors.Verification(
                        "AFFINE_RESULT_DRIFT",
                        $"LOD {target.Lod} affine position result differs from its dry-run plan.",
                        "Reject the candidate and regenerate the plan.");
                }

                if (!positionOnly)
                {
                    Source2PackedFrameCodec.TransformSelected(intended, target.PackedFrameLayout, selectedVertices, transform);
                }
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.PackedFramesTransformed);

                if (ContentHash.Compute(intended) != target.ExpectedDecodedVertexBufferHash)
                {
                    throw Errors.Verification(
                        "AFFINE_RESULT_DRIFT",
                        $"LOD {target.Lod} affine decoded-buffer hash differs from its dry-run plan.",
                        "Reject the candidate and regenerate the plan.");
                }

                VerifyAffineAllowedVertexBytesChanged(
                    profile.Vertices.Decoded,
                    intended,
                    profile.Vertices.Snapshot.PositionLayout,
                    target.PackedFrameLayout,
                    selectedVertices,
                    positionOnly);
                if (Source2PackedFrameCodec.HashSelected(
                        intended,
                        target.PackedFrameLayout,
                        selectedVertices) != target.ExpectedPackedFrameHash)
                {
                    throw Errors.Verification(
                        "AFFINE_PACKED_FRAME_REENCODE_MISMATCH",
                        $"LOD {target.Lod} packed normal/tangent frames differ from the affine plan.",
                        "Reject the candidate and regenerate the plan.");
                }

                var encoded = EncodeDeterministically(
                    codec,
                    intended,
                    profile.Vertices.Snapshot,
                    $"LOD {target.Lod} affine MVTX block {target.VertexResourceBlockIndex}");
                var reopenedDecoded = codec.DecodeVertexBuffer(
                    encoded,
                    profile.Vertices.Snapshot.VertexCount,
                    profile.Vertices.Snapshot.Stride);
                if (!reopenedDecoded.AsSpan().SequenceEqual(intended))
                {
                    throw new InvalidDataException($"LOD {target.Lod} affine MVTX changed during encode/decode verification.");
                }
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.VertexBufferEncoded);

                replacements.Add(target.VertexResourceBlockIndex, encoded);
                intendedDecoded.Add(target.VertexResourceBlockIndex, intended);
                UpdateAffineMeshMetadata(profile.Mesh.Block.Data, profile.Metadata, target,
                    boneSizeIsHalfExtent: true);
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.BoundsUpdated);
                expectedSemanticHashes.Add(target.ResourceBlockIndex, KvSemanticHasher.ComputeComplete(profile.Mesh.Block.Data));
                replacements.Add(
                    target.ResourceBlockIndex,
                    SerializeDeterministically(profile.Mesh.Block.Serialize, $"LOD {target.Lod} affine MDAT block {target.ResourceBlockIndex}"));
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.MetadataSerialized);
            }

            if (!operation.TargetBlocks.Select(block => block.Index).ToHashSet().SetEquals(replacements.Keys))
            {
                throw new InvalidDataException("Uniform affine replacements do not cover the exact target-block set.");
            }

            var candidateBytes = ResourceEnvelopeWriter.Rebuild(parsed.Envelope, replacements);
            ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.EnvelopeRebuilt);
            var candidateArtifact = new ArtifactContent(input.LogicalPath, ContentHash.Compute(candidateBytes), candidateBytes);
            using var reopened = Parse(candidateArtifact, retainGeometryAnalysis: true);
            VerifyAffineUniformReopen(
                model,
                parsed,
                reopened,
                operation,
                affine,
                replacements,
                intendedDecoded,
                expectedSemanticHashes);
            return new RewriteCandidate(input.LogicalPath, candidateBytes, reopened.Snapshot);
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
                    "AFFINE_RESULT_DRIFT",
                    "source2_adapter",
                    $"The uniform affine candidate could not be rewritten and reopened safely: {exception.Message}",
                    "Reject the candidate and regenerate it from current immutable evidence.",
                    ErrorCategory.UnsupportedCapability),
                exception);
        }
    }

    private static AffineUniformRewriteProfile ValidateAffineUniformTarget(
        ParsedModel parsed,
        ArtifactContent input,
        PlannedOperation operation,
        PlannedAffineGeometryTarget target,
        GeometryCodecIdentity codecIdentity,
        bool positionOnly)
    {
        if (!parsed.MeshesByOrdinal.TryGetValue(target.MeshOrdinal, out var mesh)
            || mesh.Lod != target.Lod
            || mesh.BlockIndex != target.ResourceBlockIndex
            || !string.Equals(StableIdentity.NormalizePath(target.ResourcePath), input.LogicalPath, StringComparison.Ordinal)
            || mesh.GeometryAnalysis is null
            || mesh.Geometry.Codec != codecIdentity
            || mesh.GeometryAnalysis.VertexBuffers.Count != mesh.GeometryAnalysis.IndexBuffers.Count
            || (operation.AffineTransformTarget!.StructuralProfileId == RootAffineProfileId
                ? mesh.GeometryAnalysis.VertexBuffers.Count != 1
                : operation.AffineTransformTarget.StructuralProfileId != MultiBufferAffineProfileId
                    || mesh.GeometryAnalysis.VertexBuffers.Count < 2)
            || (uint)target.VertexBufferOrdinal >= (uint)mesh.GeometryAnalysis.VertexBuffers.Count
            || target.IndexBufferOrdinal != target.VertexBufferOrdinal)
        {
            throw Errors.Verification(
                "AFFINE_RESULT_DRIFT",
                $"LOD {target.Lod} affine geometry location or codec changed.",
                "Regenerate the plan from the immutable input.");
        }

        var geometry = mesh.GeometryAnalysis;
        var vertices = geometry.VertexBuffers[target.VertexBufferOrdinal];
        var indices = geometry.IndexBuffers[target.IndexBufferOrdinal];
        var selectedVertices = ResolveAffineVertices(mesh, operation, target);
        var metadata = geometry.VertexBuffers.Count > 1
            ? Source2TransformMetadataAnalyzer.AnalyzeMultiBufferMesh(mesh.Descriptor, mesh.Block.Data,
                geometry, $"embedded mesh {mesh.MeshOrdinal}")
            : Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(mesh.Descriptor, mesh.Block.Data,
                geometry, $"embedded mesh {mesh.MeshOrdinal}", boneSizeIsHalfExtent: true);
        if (vertices.Snapshot.ResourceBlockIndex != target.VertexResourceBlockIndex
            || indices.Snapshot.ResourceBlockIndex != target.IndexResourceBlockIndex
            || vertices.Snapshot.EncodedHash != target.VertexBlockInputHash
            || indices.Snapshot.EncodedHash != target.IndexBlockInputHash
            || vertices.Snapshot.DecodedHash != target.DecodedVertexBufferHash
            || indices.Snapshot.DecodedHash != target.DecodedIndexBufferHash
            || vertices.Snapshot.PositionLayout != target.PositionLayout
            || vertices.PackedFrameLayout != target.PackedFrameLayout
            || new ContentHash(VertexSetHash.Compute(selectedVertices)) != target.VertexSetHash
            || selectedVertices.Length != target.SelectedVertexCount
            || metadata.SceneBounds != target.MeshBeforeBounds
            || Source2PackedFrameCodec.HashSelected(vertices.Decoded, target.PackedFrameLayout, selectedVertices) != target.InputPackedFrameHash
            || (positionOnly
                ? target.InputPackedFrameHash != target.ExpectedPackedFrameHash
                    || !target.AllowedChangedAttributes.SequenceEqual(["position"], StringComparer.Ordinal)
                : target.InputPackedFrameHash == target.ExpectedPackedFrameHash
                    || !target.AllowedChangedAttributes.SequenceEqual(["normal_tangent", "position"], StringComparer.Ordinal))
            || target.Codec != codecIdentity)
        {
            throw Errors.Verification(
                "AFFINE_RESULT_DRIFT",
                $"LOD {target.Lod} affine geometry facts drifted from the plan.",
                "Re-inspect the immutable input and regenerate the plan.");
        }

        ValidateAffineBoneFacts(metadata, target, afterMutation: false);
        return new AffineUniformRewriteProfile(mesh, vertices, indices, metadata);
    }

    private static int[] ResolveAffineVertices(
        ParsedMesh mesh,
        PlannedOperation operation,
        PlannedAffineGeometryTarget target)
    {
        var geometry = mesh.GeometryAnalysis
            ?? throw new InvalidDataException($"LOD {target.Lod} has no decoded affine geometry.");
        if (target.ConnectedComponentIds.Count > 0)
        {
            var byId = geometry.ConnectedComponents.ToDictionary(item => item.Snapshot.Id, StringComparer.Ordinal);
            var components = target.ConnectedComponentIds.Select(id => byId.TryGetValue(id, out var component)
                    ? component
                    : throw new InvalidDataException($"LOD {target.Lod} connected component '{id}' is missing.")).ToArray();
            if (components.Any(component => component.Snapshot.VertexBufferOrdinal != target.VertexBufferOrdinal
                || component.Snapshot.IndexBufferOrdinal != target.IndexBufferOrdinal))
            {
                throw new InvalidDataException($"LOD {target.Lod} affine component escaped its planned buffer pair.");
            }

            return components
                .SelectMany(component => component.VertexIndices)
                .Distinct()
                .Order()
                .ToArray();
        }

        var selectedIds = operation.SelectedDrawCalls
            .Where(item => item.Lod == target.Lod && item.MeshOrdinal == target.MeshOrdinal)
            .Select(item => item.DrawCallId)
            .ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Count == 0)
        {
            throw new InvalidDataException($"LOD {target.Lod} affine draw-call selection is empty.");
        }

        var calls = geometry.DrawCalls.Where(call => selectedIds.Contains(call.Snapshot.DrawCallId)).ToArray();
        if (calls.Length != selectedIds.Count || calls.Any(call =>
            call.Snapshot.VertexBufferOrdinal != target.VertexBufferOrdinal
            || call.Snapshot.IndexBufferOrdinal != target.IndexBufferOrdinal))
        {
            throw new InvalidDataException($"LOD {target.Lod} affine draw calls escaped their planned buffer pair.");
        }

        return calls
            .SelectMany(call => call.VertexIndices)
            .Distinct()
            .Order()
            .ToArray();
    }

    private static void UpdateAffineMeshMetadata(
        KVObject meshData,
        Source2WholeMeshTransformAnalysis metadata,
        PlannedAffineGeometryTarget target,
        bool boneSizeIsHalfExtent)
    {
        var sceneObjects = RequireArray(meshData, "m_sceneObjects", $"MDAT mesh {target.MeshOrdinal}");
        if (sceneObjects.Count != 1)
        {
            throw new InvalidDataException($"MDAT mesh {target.MeshOrdinal} no longer has exactly one scene object.");
        }

        var scene = RequireCollection(sceneObjects[0], $"MDAT mesh {target.MeshOrdinal}.m_sceneObjects[0]");
        KvNumericMutation.ReplaceVector3(scene, "m_vMinBounds", target.MeshBeforeBounds.Min, target.MeshExpectedAfterBounds.Min, $"MDAT mesh {target.MeshOrdinal}.scene");
        KvNumericMutation.ReplaceVector3(scene, "m_vMaxBounds", target.MeshBeforeBounds.Max, target.MeshExpectedAfterBounds.Max, $"MDAT mesh {target.MeshOrdinal}.scene");
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

            var bone = RequireCollection(bones[planned.BoneIndex], $"MDAT mesh {target.MeshOrdinal}.bone[{planned.BoneIndex}]");
            var source = metadata.BoneBounds.SingleOrDefault(item => item.BoneIndex == planned.BoneIndex);
            if (!bone.TryGetValue("m_boneName", out var name)
                || name is null
                || !string.Equals(name.ToString(System.Globalization.CultureInfo.InvariantCulture), planned.BoneName, StringComparison.Ordinal)
                || source is null
                || !bone.TryGetValue("m_bbox", out var box)
                || box is null
                || !box.IsCollection)
            {
                throw new InvalidDataException($"MDAT mesh {target.MeshOrdinal} bone {planned.BoneIndex} identity or bounds layout drifted.");
            }

            KvNumericMutation.ReplaceVector3(box, "m_vecCenter", source.LocalBoundsCenter, BoundsCenter(planned.ExpectedAfterBounds), $"MDAT mesh {target.MeshOrdinal}.bone[{planned.BoneIndex}].m_bbox");
            var expectedSize = BoundsSize(planned.ExpectedAfterBounds);
            if (boneSizeIsHalfExtent)
            {
                expectedSize = new TransformVector3
                {
                    X = expectedSize.X * 0.5f,
                    Y = expectedSize.Y * 0.5f,
                    Z = expectedSize.Z * 0.5f,
                };
            }

            KvNumericMutation.ReplaceVector3(box, "m_vecSize", source.LocalBoundsSize, expectedSize, $"MDAT mesh {target.MeshOrdinal}.bone[{planned.BoneIndex}].m_bbox");
            KvNumericMutation.ReplaceSingle(bone, "m_flSphereRadius", planned.BeforeSphereRadius, planned.ExpectedSphereRadius, $"MDAT mesh {target.MeshOrdinal}.bone[{planned.BoneIndex}]");
        }
    }

    private static void ValidateAffineTargetBlocks(
        ParsedModel parsed,
        PlannedOperation operation,
        PlannedAffineTransformTarget affine)
    {
        var expected = new Dictionary<int, string>();
        foreach (var target in affine.GeometryTargets)
        {
            _ = TryAddExpectedBlock(expected, target.ResourceBlockIndex, "MDAT");
            _ = TryAddExpectedBlock(expected, target.VertexResourceBlockIndex, "MVTX");
        }

        var planned = operation.TargetBlocks.ToDictionary(block => block.Index);
        if (!expected.Keys.ToHashSet().SetEquals(planned.Keys))
        {
            throw Errors.Verification(
                "TRANSFORM_TARGET_BLOCK_SET_DRIFT",
                "The uniform affine plan does not exactly cover its geometry and metadata blocks.",
                "Regenerate the plan from the immutable input.");
        }

        foreach (var (index, type) in expected)
        {
            var raw = parsed.Envelope.Blocks[index];
            if (!planned.TryGetValue(index, out var fingerprint)
                || raw.Type != type
                || fingerprint.Type != type
                || ContentHash.Compute(raw.Payload.Span) != fingerprint.InputHash)
            {
                throw Errors.Verification(
                    "TRANSFORM_TARGET_BLOCK_FINGERPRINT_DRIFT",
                    $"Planned affine block {index} no longer matches its type or input hash.",
                    "Regenerate the plan from the immutable input.");
            }
        }
    }

    private static void VerifyAffineUniformReopen(
        ModelSnapshot before,
        ParsedModel source,
        ParsedModel candidate,
        PlannedOperation operation,
        PlannedAffineTransformTarget affine,
        Dictionary<int, ReadOnlyMemory<byte>> replacements,
        Dictionary<int, byte[]> intendedDecoded,
        Dictionary<int, ContentHash> expectedSemanticHashes)
    {
        if (!Flatten(before).SequenceEqual(Flatten(candidate.Snapshot))
            || source.Envelope.Blocks.Count != candidate.Envelope.Blocks.Count)
        {
            throw Errors.Verification(
                "AFFINE_RESULT_DRIFT",
                "Draw-call or block inventory changed during uniform affine reopen.",
                "Reject the candidate.");
        }

        var targetIndices = operation.TargetBlocks.Select(block => block.Index).ToHashSet();
        for (var index = 0; index < source.Envelope.Blocks.Count; index++)
        {
            var original = source.Envelope.Blocks[index];
            var reopened = candidate.Envelope.Blocks[index];
            if (original.Type != reopened.Type
                || (targetIndices.Contains(index)
                    ? !replacements[index].Span.SequenceEqual(reopened.Payload.Span) || original.Payload.Span.SequenceEqual(reopened.Payload.Span)
                    : !original.Payload.Span.SequenceEqual(reopened.Payload.Span)))
            {
                throw Errors.Verification(
                    "AFFINE_RESULT_DRIFT",
                    $"Resource block {index} failed the uniform affine allowed-change audit.",
                    "Reject the candidate.");
            }
        }

        foreach (var target in affine.GeometryTargets)
        {
            if (!candidate.MeshesByOrdinal.TryGetValue(target.MeshOrdinal, out var mesh)
                || mesh.GeometryAnalysis is null
                || mesh.Lod != target.Lod
                || mesh.BlockIndex != target.ResourceBlockIndex)
            {
                throw Errors.Verification("AFFINE_RESULT_DRIFT", $"LOD {target.Lod} affine geometry is missing after reopen.", "Reject the candidate.");
            }

            var vertices = mesh.GeometryAnalysis.VertexBuffers[target.VertexBufferOrdinal];
            var indices = mesh.GeometryAnalysis.IndexBuffers[target.IndexBufferOrdinal];
            var selectedVertices = ResolveAffineVertices(mesh, operation, target);
            if (!vertices.Decoded.AsSpan().SequenceEqual(intendedDecoded[target.VertexResourceBlockIndex])
                || vertices.Snapshot.DecodedHash != target.ExpectedDecodedVertexBufferHash
                || vertices.Snapshot.EncodedHash != ContentHash.Compute(replacements[target.VertexResourceBlockIndex].Span)
                || indices.Snapshot.EncodedHash != target.IndexBlockInputHash
                || indices.Snapshot.DecodedHash != target.DecodedIndexBufferHash
                || mesh.Geometry.Codec != target.Codec
                || Source2PackedFrameCodec.HashSelected(vertices.Decoded, target.PackedFrameLayout, selectedVertices) != target.ExpectedPackedFrameHash
                || KvSemanticHasher.ComputeComplete(mesh.Block.Data) != expectedSemanticHashes[target.ResourceBlockIndex])
            {
                throw Errors.Verification(
                    "AFFINE_RESULT_DRIFT",
                    $"LOD {target.Lod} affine buffers or metadata differ after reopen.",
                    "Reject the candidate.");
            }

            var metadata = mesh.GeometryAnalysis.VertexBuffers.Count > 1
                ? Source2TransformMetadataAnalyzer.AnalyzeMultiBufferMesh(mesh.Descriptor, mesh.Block.Data,
                    mesh.GeometryAnalysis, $"reopened embedded mesh {mesh.MeshOrdinal}")
                : Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(mesh.Descriptor, mesh.Block.Data,
                    mesh.GeometryAnalysis, $"reopened embedded mesh {mesh.MeshOrdinal}", boneSizeIsHalfExtent: true);
            if (metadata.SceneBounds != target.MeshExpectedAfterBounds)
            {
                throw Errors.Verification("AFFINE_RESULT_DRIFT", $"LOD {target.Lod} scene bounds differ after reopen.", "Reject the candidate.");
            }

            ValidateAffineBoneFacts(metadata, target, afterMutation: true);
        }
    }

    private static void ValidateAffineBoneFacts(
        Source2WholeMeshTransformAnalysis metadata,
        PlannedAffineGeometryTarget target,
        bool afterMutation)
    {
        var byIndex = metadata.BoneBounds.ToDictionary(bone => bone.BoneIndex);
        foreach (var planned in target.BoneBoundsTargets)
        {
            if (!byIndex.TryGetValue(planned.BoneIndex, out var actual)
                || actual.BoneName != planned.BoneName
                || actual.InverseBindPoseHash != planned.InverseBindPoseHash
                || new ContentHash(VertexSetHash.Compute(actual.InfluencedVertices)) != planned.InfluencedVertexSetHash
                || actual.InfluencedVertices.Length != planned.InfluencedVertexCount
                || actual.LocalBounds != (afterMutation ? planned.ExpectedAfterBounds : planned.BeforeBounds)
                || !HasIdenticalSingle(actual.SphereRadius, afterMutation ? planned.ExpectedSphereRadius : planned.BeforeSphereRadius))
            {
                throw Errors.Verification(
                    "AFFINE_RESULT_DRIFT",
                    $"LOD {target.Lod} bone '{planned.BoneName}' bounds evidence drifted.",
                    "Regenerate or reject the candidate.");
            }
        }
    }

    private static TransformVector3 BoundsCenter(GeometryBounds bounds) => new()
    {
        X = (float)(((double)bounds.Min.X + bounds.Max.X) * 0.5d),
        Y = (float)(((double)bounds.Min.Y + bounds.Max.Y) * 0.5d),
        Z = (float)(((double)bounds.Min.Z + bounds.Max.Z) * 0.5d),
    };

    private static TransformVector3 BoundsSize(GeometryBounds bounds) => new()
    {
        X = bounds.Max.X - bounds.Min.X,
        Y = bounds.Max.Y - bounds.Min.Y,
        Z = bounds.Max.Z - bounds.Min.Z,
    };

    private static AffineTransform CreateAffineTransform(PlannedAffineTransformTarget target)
    {
        var rotation = target.Rotation.Kind == "identity"
            ? AxisAngleRotation.Identity
            : new AxisAngleRotation(ToAffinePoint(target.Rotation.Axis!), target.Rotation.Degrees!.Value);
        return new AffineTransform(
            ToAffinePoint(target.Pivot.Point),
            new AffineScale(target.Scale.X, target.Scale.Y, target.Scale.Z),
            rotation,
            new RigidFrame(ToAffineMatrix(target.Frame.ToModel)),
            ToAffinePoint(target.Translation));
    }

    private static void VerifyAffineAllowedVertexBytesChanged(
        ReadOnlySpan<byte> before,
        ReadOnlySpan<byte> after,
        PositionLayout positionLayout,
        PackedFrameLayout packedFrameLayout,
        IReadOnlyCollection<int> selectedVertices,
        bool positionOnly)
    {
        if (before.Length != after.Length || before.Length % positionLayout.Stride != 0)
        {
            throw new InvalidDataException("Decoded MVTX length changed during affine transform.");
        }

        var selected = selectedVertices.ToHashSet();
        for (var vertex = 0; vertex < before.Length / positionLayout.Stride; vertex++)
        {
            var recordOffset = checked(vertex * positionLayout.Stride);
            for (var offset = 0; offset < positionLayout.Stride; offset++)
            {
                var positionByte = selected.Contains(vertex)
                    && offset >= positionLayout.Offset
                    && offset < positionLayout.Offset + (sizeof(float) * 3);
                var frameByte = !positionOnly
                    && selected.Contains(vertex)
                    && offset >= packedFrameLayout.Offset
                    && offset < packedFrameLayout.Offset + sizeof(uint);
                if (!positionByte && !frameByte && before[recordOffset + offset] != after[recordOffset + offset])
                {
                    throw new InvalidDataException($"Decoded MVTX changed an unplanned byte {offset} of vertex {vertex}.");
                }
            }
        }
    }

    private sealed record AffineUniformRewriteProfile(
        ParsedMesh Mesh,
        Source2VertexBufferAnalysis Vertices,
        Source2IndexBufferAnalysis Indices,
        Source2WholeMeshTransformAnalysis Metadata);
}
