using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private const string RootAffineProfileId = "root_mvtx_affine";
    private const int RootAffineProfileVersion = 1;

    private static TransformPlanningResult PlanAffineTransform(TransformPlanningRequest request, ParsedModel parsed)
    {
        var operation = request.Operation;
        if (operation.Transform.Rotation is not { Kind: "identity" })
        {
            throw Errors.Unsupported(
                "AFFINE_ROTATION_UNSUPPORTED",
                "Rotation planning is not implemented for the current Source 2 affine profile.",
                "Use identity rotation until the bounded rotation writer is available.");
        }

        var expectedLods = operation.ExpectedVerticesByLod.Keys
            .Select(ParseAffineLod)
            .Order()
            .ToArray();
        var profiles = request.SelectedDrawCalls
            .GroupBy(item => item.MeshOrdinal)
            .OrderBy(group => group.Key)
            .Select(group => CreateAffineProfile(request, parsed, group.ToArray()))
            .OrderBy(profile => profile.Mesh.Lod)
            .ThenBy(profile => profile.Mesh.MeshOrdinal)
            .ToArray();
        var actualLods = profiles.Select(profile => profile.Mesh.Lod).Distinct().Order().ToArray();
        if (!actualLods.SequenceEqual(expectedLods)
            || profiles.GroupBy(profile => profile.Mesh.Lod).Any(group => group.Count() != 1))
        {
            throw Errors.Selection(
                "TRANSFORM_GEOMETRY_LOD_COVERAGE_INCOMPLETE",
                "The affine selection does not map to exactly one characterized root geometry buffer in every declared LOD.",
                "Select one complete affine-capable component in every present LOD.");
        }

        var boundsEvidence = profiles.Select(profile => new AffineSelectionBoundsEvidence(
            profile.Mesh.Lod,
            profile.SelectionBounds,
            profile.VertexSetHash)).ToArray();
        var boneEvidence = profiles.SelectMany(CreateBoneEvidence).ToArray();
        var resolver = new AffineEvidenceResolver();
        var pivot = resolver.ResolvePivot(new TypedPivotResolutionRequest(
            operation.Transform.Pivot,
            expectedLods,
            boundsEvidence,
            boneEvidence));
        var frame = resolver.ResolveFrame(new TypedFrameResolutionRequest(
            operation.Transform.Frame!,
            expectedLods,
            boneEvidence));
        var scale = operation.Transform.Scale!;
        var transform = new AffineTransform(
            ToAffinePoint(pivot.Point),
            new AffineScale(scale.X, scale.Y, scale.Z),
            AxisAngleRotation.Identity,
            new RigidFrame(ToAffineMatrix(frame.ToModel)),
            ToAffinePoint(operation.Transform.Translation));
        var uniformPositionOnly = scale.X == scale.Y && scale.Y == scale.Z;
        var targets = profiles.Select(profile => PlanAffineGeometry(
            request,
            parsed,
            profile,
            transform,
            uniformPositionOnly)).ToArray();
        var maximumDisplacement = targets.Max(target => target.MaximumDisplacement);
        if (maximumDisplacement > operation.Limits.MaximumVertexDisplacement
            || maximumDisplacement > RecipeValidator.MaximumTransformDisplacement)
        {
            throw Errors.Selection(
                "TRANSFORM_DISPLACEMENT_EXCEEDED",
                $"The affine transform would move a vertex by up to {maximumDisplacement.ToString("R", CultureInfo.InvariantCulture)} Source units.",
                "Reduce the transform or raise the recipe cap within the hard safety limit.");
        }

        RejectAffectedDistanceFields(parsed, profiles);
        var targetBlocks = new Dictionary<(string Type, int Index), PlannedTargetBlock>();
        foreach (var target in targets)
        {
            AddTargetBlock(targetBlocks, parsed, target.ResourceBlockIndex);
            AddTargetBlock(targetBlocks, parsed, target.VertexResourceBlockIndex);
        }

        return new TransformPlanningResult(
            [],
            [],
            targetBlocks.Values.OrderBy(block => block.Index).ThenBy(block => block.Type, StringComparer.Ordinal).ToArray())
        {
            AffineTransformTarget = new PlannedAffineTransformTarget(
                operation.Granularity,
                RootAffineProfileId,
                RootAffineProfileVersion,
                pivot,
                frame,
                scale,
                operation.Transform.Rotation!,
                operation.Transform.Translation,
                ToContractMatrix(transform.LinearMap),
                maximumDisplacement,
                operation.Limits.MaximumVertexDisplacement,
                targets),
        };
    }

    private static Source2AffineProfile CreateAffineProfile(
        TransformPlanningRequest request,
        ParsedModel parsed,
        SelectedDrawCall[] selected)
    {
        var first = selected[0];
        if (!parsed.MeshesByOrdinal.TryGetValue(first.MeshOrdinal, out var mesh)
            || mesh.Lod != first.Lod
            || mesh.BlockIndex != first.ResourceBlockIndex
            || mesh.GeometryAnalysis is null
            || mesh.Geometry.Codec is null
            || mesh.GeometryAnalysis.VertexBuffers.Count != 1
            || mesh.GeometryAnalysis.IndexBuffers.Count != 1
            || selected.Any(item => item.Lod != mesh.Lod
                || item.ResourceBlockIndex != mesh.BlockIndex
                || !string.Equals(StableIdentity.NormalizePath(item.ResourcePath), request.Input.LogicalPath, StringComparison.Ordinal)))
        {
            throw Errors.Unsupported(
                "AFFINE_LAYOUT_UNSUPPORTED",
                "The affine selection is not one characterized root MVTX/MIDX geometry profile.",
                "Use one decoded single-buffer root mesh per LOD.");
        }

        if (mesh.RawMbufAnalysis is not null || mesh.PhysicsAnalysis is not null)
        {
            throw Errors.Unsupported(
                "AFFINE_PHYSICS_COUPLING_UNSUPPORTED",
                "The selected geometry participates in a characterized collision-coupled profile.",
                "Use the atomic positive-uniform collision transform instead.");
        }

        if (HasMorphData(mesh.Block.Data))
        {
            throw Errors.Unsupported(
                "AFFINE_MORPH_UNSUPPORTED",
                "The selected mesh exposes position-dependent morph data outside the affine allowed-change contract.",
                "Use a mesh without morph data or wait for a separately characterized morph writer.");
        }

        var geometry = mesh.GeometryAnalysis;
        var vertices = geometry.VertexBuffers[0];
        var indices = geometry.IndexBuffers[0];
        var packedLayout = vertices.PackedFrameLayout;
        if (packedLayout is null
            || mesh.DrawCalls.Any(location => !Mesh.IsCompressedNormalTangent(location.Data)))
        {
            throw Errors.Unsupported(
                "AFFINE_PACKED_FRAME_UNSUPPORTED",
                "The selected geometry does not have one characterized compressed NORMAL R32_UINT frame on every draw call.",
                "Use the characterized packed normal/tangent profile.");
        }

        var metadata = Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            mesh.Descriptor,
            mesh.Block.Data,
            geometry,
            $"embedded mesh {mesh.MeshOrdinal.ToString(CultureInfo.InvariantCulture)}");
        var selectedIds = selected.Select(item => item.DrawCallId).ToHashSet(StringComparer.Ordinal);
        if (selected.Any(item => !mesh.DrawCalls.Any(location => Matches(location.Snapshot, item))))
        {
            throw Errors.Verification(
                "TRANSFORM_DRAW_CALL_DRIFT",
                $"LOD {mesh.Lod} draw-call selection no longer matches the immutable input.",
                "Re-inspect the input and regenerate the affine plan.");
        }

        int[] selectedVertices;
        string[] componentIds;
        if (request.Operation.Granularity == "draw_call_vertices")
        {
            selectedVertices = geometry.DrawCalls
                .Where(call => selectedIds.Contains(call.Snapshot.DrawCallId))
                .SelectMany(call => call.VertexIndices)
                .Distinct()
                .Order()
                .ToArray();
            componentIds = [];
        }
        else if (request.Operation.Granularity == "connected_component_vertices")
        {
            var lodKey = mesh.Lod.ToString(CultureInfo.InvariantCulture);
            if (request.Operation.ConnectedComponentIdsByLod is null
                || !request.Operation.ConnectedComponentIdsByLod.TryGetValue(lodKey, out var requestedIds))
            {
                throw Errors.Selection(
                    "CONNECTED_COMPONENT_LOD_MISSING",
                    $"LOD {mesh.Lod} has no affine connected-component selection.",
                    "Declare exact connected-component identities for every LOD.");
            }

            var byId = geometry.ConnectedComponents.ToDictionary(item => item.Snapshot.Id, StringComparer.Ordinal);
            var components = requestedIds.Select(id => byId.TryGetValue(id, out var component)
                ? component
                : throw Errors.Selection(
                    "CONNECTED_COMPONENT_ID_NOT_FOUND",
                    $"LOD {mesh.Lod} connected component '{id}' is absent.",
                    "Regenerate the recipe from current discovery evidence.")).ToArray();
            if (components.Any(component => !selectedIds.Contains(component.Snapshot.DrawCallId)
                || component.Snapshot.VertexBufferOrdinal != 0
                || component.Snapshot.IndexBufferOrdinal != 0))
            {
                throw Errors.Unsupported(
                    "AFFINE_MULTI_BUFFER_OWNERSHIP_UNSUPPORTED",
                    "A selected connected component drifted outside the planned draw calls or root buffers.",
                    "Regenerate one exact single-buffer component selection.");
            }

            selectedVertices = components.SelectMany(component => component.VertexIndices).Distinct().Order().ToArray();
            componentIds = requestedIds.Order(StringComparer.Ordinal).ToArray();
        }
        else
        {
            throw Errors.Unsupported(
                "AFFINE_LAYOUT_UNSUPPORTED",
                $"Affine selection kind '{request.Operation.Granularity}' is unsupported.",
                "Use draw_call_vertices or connected_component_vertices.");
        }

        if (selectedVertices.Length == 0)
        {
            throw Errors.Selection("TRANSFORM_EMPTY_SELECTION_EFFECT", "The affine vertex selection is empty.", "Select at least one owned vertex.");
        }

        var selectedSet = selectedVertices.ToHashSet();
        var shared = geometry.DrawCalls
            .Where(call => !selectedIds.Contains(call.Snapshot.DrawCallId))
            .SelectMany(call => call.VertexIndices)
            .Any(selectedSet.Contains);
        if (shared)
        {
            throw Errors.Unsupported(
                "AFFINE_MULTI_BUFFER_OWNERSHIP_UNSUPPORTED",
                "One or more selected position or packed-frame records are shared with an unselected draw call.",
                "Select an exclusively owned vertex set.");
        }

        var expected = request.Operation.ExpectedVerticesByLod[mesh.Lod.ToString(CultureInfo.InvariantCulture)];
        if (selectedVertices.Length != expected)
        {
            throw Errors.Selection(
                "VERTEX_CARDINALITY_MISMATCH",
                $"LOD {mesh.Lod} contains {selectedVertices.Length} selected affine vertices; expected {expected}.",
                "Copy the current exact vertex count into the recipe.");
        }

        var selectionBounds = ToAffineBounds(Bounds3.FromPoints(
            selectedVertices.Select(vertex => Source2GeometryAnalyzer.ReadPosition(vertices, vertex)).ToArray()));
        return new Source2AffineProfile(
            mesh,
            vertices,
            indices,
            metadata,
            packedLayout,
            selectedVertices,
            componentIds,
            new ContentHash(VertexSetHash.Compute(selectedVertices)),
            selectionBounds);
    }

    private static PlannedAffineGeometryTarget PlanAffineGeometry(
        TransformPlanningRequest request,
        ParsedModel parsed,
        Source2AffineProfile profile,
        AffineTransform transform,
        bool uniformPositionOnly)
    {
        var output = (byte[])profile.Vertices.Decoded.Clone();
        var beforePoints = profile.SelectedVertices
            .Select(vertex => Source2GeometryAnalyzer.ReadPosition(profile.Vertices, vertex))
            .ToArray();
        var afterPoints = new Point3[beforePoints.Length];
        var summary = transform.ApplyAndSummarize(beforePoints, afterPoints);
        if (summary.ChangedPointCount == 0 || summary.MaximumDisplacement <= 0f)
        {
            throw Errors.Selection(
                "TRANSFORM_EMPTY_SELECTION_EFFECT",
                $"The affine transform does not change any selected position in LOD {profile.Mesh.Lod}.",
                "Choose a transform with a non-empty effect in every LOD.");
        }

        if (summary.MaximumDisplacement > request.Operation.Limits.MaximumVertexDisplacement
            || summary.MaximumDisplacement > RecipeValidator.MaximumTransformDisplacement)
        {
            throw Errors.Selection(
                "TRANSFORM_DISPLACEMENT_EXCEEDED",
                $"LOD {profile.Mesh.Lod} would move a vertex by up to {summary.MaximumDisplacement.ToString("R", CultureInfo.InvariantCulture)} Source units.",
                "Reduce the transform or raise the recipe cap within the hard safety limit.");
        }

        for (var index = 0; index < profile.SelectedVertices.Length; index++)
        {
            WritePosition(output, profile.Vertices.Snapshot.PositionLayout, profile.SelectedVertices[index], afterPoints[index]);
        }

        var inputPackedHash = Source2PackedFrameCodec.HashSelected(
            profile.Vertices.Decoded,
            profile.PackedFrameLayout,
            profile.SelectedVertices);
        if (!uniformPositionOnly)
        {
            Source2PackedFrameCodec.TransformSelected(
                output,
                profile.PackedFrameLayout,
                profile.SelectedVertices,
                transform);
        }

        var expectedPackedHash = Source2PackedFrameCodec.HashSelected(
            output,
            profile.PackedFrameLayout,
            profile.SelectedVertices);
        var allAfterPoints = Enumerable.Range(0, profile.Vertices.Snapshot.VertexCount)
            .Select(vertex => ReadPosition(output, profile.Vertices.Snapshot.PositionLayout, vertex))
            .ToArray();
        var boneTargets = profile.Metadata.BoneBounds
            .Where(bone => bone.InfluencedVertices.Intersect(profile.SelectedVertices).Any())
            .Select(bone => PlanAffineBoneBounds(bone, allAfterPoints, transform))
            .OrderBy(bone => bone.BoneIndex)
            .ThenBy(bone => bone.BoneName, StringComparer.Ordinal)
            .ToArray();
        if (boneTargets.Length == 0)
        {
            throw Errors.Unsupported(
                "AFFINE_SKINNING_UNSUPPORTED",
                "The affine selection has no reproducible affected bone-culling bounds.",
                "Use geometry whose selected vertices have characterized skinning evidence.");
        }

        var vertexBlock = parsed.Envelope.Blocks[profile.Vertices.Snapshot.ResourceBlockIndex];
        var indexBlock = parsed.Envelope.Blocks[profile.Indices.Snapshot.ResourceBlockIndex];
        return new PlannedAffineGeometryTarget(
            profile.Mesh.Lod,
            request.Input.LogicalPath,
            profile.Mesh.MeshOrdinal,
            profile.Mesh.BlockIndex,
            profile.Vertices.Snapshot.Ordinal,
            profile.Indices.Snapshot.Ordinal,
            profile.Vertices.Snapshot.ResourceBlockIndex,
            profile.Indices.Snapshot.ResourceBlockIndex,
            ContentHash.Compute(vertexBlock.Payload.Span),
            ContentHash.Compute(indexBlock.Payload.Span),
            profile.Vertices.Snapshot.DecodedHash,
            ContentHash.Compute(output),
            profile.Indices.Snapshot.DecodedHash,
            profile.VertexSetHash,
            profile.SelectedVertices.Length,
            profile.Vertices.Snapshot.PositionLayout,
            profile.PackedFrameLayout,
            profile.SelectionBounds,
            ToAffineBounds(summary.AfterBounds),
            profile.Metadata.SceneBounds,
            ToAffineBounds(Bounds3.FromPoints(allAfterPoints)),
            summary.MaximumDisplacement,
            inputPackedHash,
            expectedPackedHash,
            uniformPositionOnly ? ["position"] : ["normal_tangent", "position"],
            profile.Mesh.Geometry.Codec!)
        {
            ConnectedComponentIds = profile.ComponentIds,
            BoneBoundsTargets = boneTargets,
        };
    }

    private static PlannedAffineBoneBoundsTarget PlanAffineBoneBounds(
        Source2BoneBoundsAnalysis bone,
        Point3[] postTransformPositions,
        AffineTransform transform)
    {
        var min = ToPoint(bone.LocalBounds.Min);
        var max = ToPoint(bone.LocalBounds.Max);
        var transformedCorners = new List<Point3>(8);
        for (var x = 0; x < 2; x++)
        {
            for (var y = 0; y < 2; y++)
            {
                for (var z = 0; z < 2; z++)
                {
                    var localCorner = new Point3(
                        x == 0 ? min.X : max.X,
                        y == 0 ? min.Y : max.Y,
                        z == 0 ? min.Z : max.Z);
                    var model = TransformBoneLocalToModel(localCorner, bone.InverseBindPose, bone.BoneName);
                    var changed = transform.Apply(model);
                    transformedCorners.Add(Source2TransformMetadataAnalyzer.TransformPoint(changed, bone.InverseBindPose));
                }
            }
        }

        var local = bone.InfluencedVertices
            .Select(vertex => Source2TransformMetadataAnalyzer.TransformPoint(postTransformPositions[vertex], bone.InverseBindPose))
            .ToArray();
        var bounds = CanonicalizeBoneBounds(Bounds3.FromPoints(transformedCorners), bone.BoneName);
        var radius = local.Max(point => MathF.Sqrt((point.X * point.X) + (point.Y * point.Y) + (point.Z * point.Z)));
        if (!float.IsFinite(radius))
        {
            throw Errors.Unsupported(
                "AFFINE_BOUNDS_UNSUPPORTED",
                $"Bone '{bone.BoneName}' produced a non-finite affine culling sphere.",
                "Reject the affine candidate.");
        }

        return new PlannedAffineBoneBoundsTarget(
            bone.BoneIndex,
            bone.BoneName,
            bone.InverseBindPoseHash,
            new ContentHash(VertexSetHash.Compute(bone.InfluencedVertices)),
            bone.InfluencedVertices.Length,
            bone.LocalBounds,
            bounds,
            bone.SphereRadius,
            radius);
    }

    private static Point3 TransformBoneLocalToModel(
        Point3 local,
        ImmutableArray<float> inverseBindPose,
        string boneName)
    {
        var a = inverseBindPose[0];
        var b = inverseBindPose[1];
        var c = inverseBindPose[2];
        var d = inverseBindPose[4];
        var e = inverseBindPose[5];
        var f = inverseBindPose[6];
        var g = inverseBindPose[8];
        var h = inverseBindPose[9];
        var i = inverseBindPose[10];
        var determinant = (a * ((e * i) - (f * h))) - (b * ((d * i) - (f * g))) + (c * ((d * h) - (e * g)));
        if (!float.IsFinite(determinant) || determinant == 0f)
        {
            throw Errors.Unsupported(
                "AFFINE_BOUNDS_UNSUPPORTED",
                $"Bone '{boneName}' inverse bind pose cannot map its stored bounds into the model frame.",
                "Reject the candidate until this bone frame is characterized.");
        }

        var reciprocal = 1f / determinant;
        var x = local.X - inverseBindPose[3];
        var y = local.Y - inverseBindPose[7];
        var z = local.Z - inverseBindPose[11];
        var result = new Point3(
            ((((e * i) - (f * h)) * x) + (((c * h) - (b * i)) * y) + (((b * f) - (c * e)) * z) ) * reciprocal,
            ((((f * g) - (d * i)) * x) + (((a * i) - (c * g)) * y) + (((c * d) - (a * f)) * z) ) * reciprocal,
            ((((d * h) - (e * g)) * x) + (((b * g) - (a * h)) * y) + (((a * e) - (b * d)) * z) ) * reciprocal);
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) || !float.IsFinite(result.Z))
        {
            throw Errors.Unsupported(
                "AFFINE_BOUNDS_UNSUPPORTED",
                $"Bone '{boneName}' inverse bind pose produced non-finite model-space bounds.",
                "Reject the candidate rather than publish invalid culling bounds.");
        }

        return result;
    }

    private static GeometryBounds CanonicalizeBoneBounds(Bounds3 exact, string boneName)
    {
        var stored = ToAffineBounds(exact);
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var center = new Point3(
                (float)(((double)stored.Min.X + stored.Max.X) * 0.5d),
                (float)(((double)stored.Min.Y + stored.Max.Y) * 0.5d),
                (float)(((double)stored.Min.Z + stored.Max.Z) * 0.5d));
            var size = new Point3(
                stored.Max.X - stored.Min.X,
                stored.Max.Y - stored.Min.Y,
                stored.Max.Z - stored.Min.Z);
            var next = new GeometryBounds(
                new TransformVector3
                {
                    X = center.X - (size.X * 0.5f),
                    Y = center.Y - (size.Y * 0.5f),
                    Z = center.Z - (size.Z * 0.5f),
                },
                new TransformVector3
                {
                    X = center.X + (size.X * 0.5f),
                    Y = center.Y + (size.Y * 0.5f),
                    Z = center.Z + (size.Z * 0.5f),
                });
            if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) || !float.IsFinite(center.Z)
                || !float.IsFinite(size.X) || !float.IsFinite(size.Y) || !float.IsFinite(size.Z)
                || size.X < 0f || size.Y < 0f || size.Z < 0f
                || !float.IsFinite(next.Min.X) || !float.IsFinite(next.Min.Y) || !float.IsFinite(next.Min.Z)
                || !float.IsFinite(next.Max.X) || !float.IsFinite(next.Max.Y) || !float.IsFinite(next.Max.Z))
            {
                throw Errors.Unsupported(
                    "AFFINE_BOUNDS_UNSUPPORTED",
                    $"Bone '{boneName}' affine bounds cannot be represented as finite non-negative center/size values.",
                    "Reject the candidate rather than publish invalid culling evidence.");
            }

            if (next == stored)
            {
                return stored;
            }

            stored = next;
        }

        throw Errors.Unsupported(
            "AFFINE_BOUNDS_UNSUPPORTED",
            $"Bone '{boneName}' affine bounds do not stabilize through the stored center/size representation.",
            "Reject the candidate rather than publish rounded culling evidence.");
    }

    private static IEnumerable<AffineBoneBindEvidence> CreateBoneEvidence(Source2AffineProfile profile)
    {
        var skeletonIdentity = ContentHash.Compute(Encoding.UTF8.GetBytes(string.Join('|',
            profile.Metadata.BoneBounds
                .OrderBy(bone => bone.BoneIndex)
                .Select(bone => $"{bone.BoneIndex}:{bone.BoneName}:{bone.InverseBindPoseHash}")))).ToString();
        var selected = profile.SelectedVertices.ToHashSet();
        return profile.Metadata.BoneBounds.Select(bone => new AffineBoneBindEvidence(
            profile.Mesh.Lod,
            skeletonIdentity,
            bone.BoneName,
            bone.InfluencedVertices.Any(selected.Contains),
            bone.InverseBindPoseHash,
            ToBindMatrix(bone.InverseBindPose)));
    }

    private static void RejectAffectedDistanceFields(ParsedModel parsed, IReadOnlyList<Source2AffineProfile> profiles)
    {
        var affected = profiles.SelectMany(profile => profile.Metadata.BoneBounds
                .Where(bone => bone.InfluencedVertices.Intersect(profile.SelectedVertices).Any())
                .Select(bone => ValveResourceFormat.Utils.StringToken.Get(bone.BoneName)))
            .ToHashSet();
        for (var index = 0; index < parsed.Resource.Blocks.Count; index++)
        {
            if (parsed.Resource.Blocks[index] is not BinaryKV3 binary
                || !string.Equals(binary.Type.ToString(), "DSTF", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = Source2TransformMetadataAnalyzer.ReadDistanceFields(binary.Data.Root, index, $"DSTF block {index}");
            if (fields.Any(field => affected.Contains(field.ParentBoneNameHash)))
            {
                throw Errors.Unsupported(
                    "AFFINE_DISTANCE_FIELD_UNSUPPORTED",
                    "An affected bone owns a distance field that cannot be anisotropically resampled by the affine contract.",
                    "Use an existing uniform operation or select geometry without an affected distance field.");
            }
        }
    }

    private static bool HasMorphData(KVObject meshData)
    {
        foreach (var key in new[] { "m_morphSet", "m_morphData", "m_morphTargets" })
        {
            if (!meshData.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            if (value.IsArray || value.IsCollection || !string.IsNullOrEmpty(value.ToString(CultureInfo.InvariantCulture)))
            {
                return true;
            }
        }

        return false;
    }

    private static AffineBindMatrix ToBindMatrix(ImmutableArray<float> values) => new(
        values[0], values[1], values[2], values[3],
        values[4], values[5], values[6], values[7],
        values[8], values[9], values[10], values[11]);

    private static void WritePosition(byte[] bytes, PositionLayout layout, int vertex, Point3 value)
    {
        var offset = checked((vertex * layout.Stride) + layout.Offset);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), BitConverter.SingleToInt32Bits(value.X));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + sizeof(float)), BitConverter.SingleToInt32Bits(value.Y));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + (sizeof(float) * 2)), BitConverter.SingleToInt32Bits(value.Z));
    }

    private static Point3 ReadPosition(byte[] bytes, PositionLayout layout, int vertex)
    {
        var offset = checked((vertex * layout.Stride) + layout.Offset);
        return new Point3(
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset))),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + sizeof(float)))),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + (sizeof(float) * 2)))));
    }

    private static int ParseAffineLod(string value) =>
        int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);

    private static Point3 ToAffinePoint(TransformVector3 value) => new(value.X, value.Y, value.Z);

    private static Matrix3 ToAffineMatrix(TransformMatrix3 value) => new(
        value.M11, value.M12, value.M13,
        value.M21, value.M22, value.M23,
        value.M31, value.M32, value.M33);

    private static TransformMatrix3 ToContractMatrix(Matrix3 value) => new(
        value.M11, value.M12, value.M13,
        value.M21, value.M22, value.M23,
        value.M31, value.M32, value.M33);

    private static GeometryBounds ToAffineBounds(Bounds3 value) => new(
        new TransformVector3 { X = value.Min.X, Y = value.Min.Y, Z = value.Min.Z },
        new TransformVector3 { X = value.Max.X, Y = value.Max.Y, Z = value.Max.Z });

    private sealed record Source2AffineProfile(
        ParsedMesh Mesh,
        Source2VertexBufferAnalysis Vertices,
        Source2IndexBufferAnalysis Indices,
        Source2WholeMeshTransformAnalysis Metadata,
        PackedFrameLayout PackedFrameLayout,
        int[] SelectedVertices,
        string[] ComponentIds,
        ContentHash VertexSetHash,
        GeometryBounds SelectionBounds);
}
