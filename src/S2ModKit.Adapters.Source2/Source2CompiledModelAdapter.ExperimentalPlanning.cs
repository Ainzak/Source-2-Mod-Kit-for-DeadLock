using System.Globalization;
using System.Text;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static readonly float[] ExperimentalIdentityMatrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];

    private TransformPlanningResult PlanExperimentalTransform(TransformPlanningRequest request, ParsedModel parsed)
    {
        var (profiles, presentLods) = ResolveExperimentalPlanningProfiles(request, parsed);
        var (model, rootSpheres, affectedRoots) = ResolveExperimentalPlanningRoot(parsed, profiles);
        var pivot = new AffineEvidenceResolver().ResolvePivot(new TypedPivotResolutionRequest(
            request.Operation.Transform.Pivot, presentLods,
            profiles.Select(profile => new AffineSelectionBoundsEvidence(profile.Mesh.Lod, profile.SelectionBounds, profile.VertexSetHash)).ToArray(),
            profiles.SelectMany(CreateBoneEvidence).ToArray()));
        var transform = new UniformTransform(ToAffinePoint(pivot.Point), request.Operation.Transform.UniformScale, default);
        var geometryTargets = new List<PlannedGeometryTarget>();
        var boxes = new List<PlannedExperimentalBoxTarget>();
        var preserved = new List<PlannedExperimentalPreservationTarget>();
        var regionFacts = new List<PlannedRegionBuffer>();
        using var codec = OpenGeometryCodec();
        foreach (var profile in profiles)
        {
            var (geometryTarget, allAfter) = PlanExperimentalBufferGeometry(request, profile, pivot, transform, codec, regionFacts);
            geometryTargets.Add(geometryTarget);
            boxes.Add(PlanExperimentalSceneBox(request.Input, profile, allAfter));
            var affectedBones = profile.Metadata.BoneBounds.Where(bone => bone.InfluencedVertices.Any(vertex =>
                vertex >= profile.BufferBaseOffset && vertex < profile.BufferBaseOffset + profile.Vertices.Snapshot.VertexCount)).ToArray();
            if (affectedBones.Length == 0) throw ExperimentalUnsupported("EXPERIMENTAL_SKINNING_UNSUPPORTED", "No complete affected bone contributors were found.");
            foreach (var bone in affectedBones) boxes.Add(PlanExperimentalBoneBox(request.Input, profile, bone, allAfter));
            AddExperimentalRenderSphereTargets(parsed, profile, affectedBones.Select(bone => bone.BoneIndex).ToHashSet(), preserved);
        }

        AddExperimentalPlanningRootSpheres(parsed, model, rootSpheres, affectedRoots, preserved);

        AddExperimentalBlockPreservation(parsed, preserved);
        var blockIndices = geometryTargets.SelectMany(target => new[] { target.ResourceBlockIndex, target.VertexResourceBlockIndex }).Distinct().Order();
        var sourceBlocks = parsed.Envelope.Blocks.Select(block => new PlannedTargetBlock(block.Index, block.Type, ContentHash.Compute(block.Payload.Span))).ToArray();
        return new TransformPlanningResult([], [], blockIndices.Select(index => sourceBlocks[index]).ToArray())
        {
            ExperimentalTransformTarget = new(request.Operation.Version == 6 ? "root_owned_axis_ramp_visual_scale" : "root_complete_buffer_visual_uniform", 1, "retain_expand_boxes_preserve_runtime", 1,
                request.Operation.RuntimeMetadataPolicy!, NormalizeExperimentalSelector(request.Operation.Selector), request.Operation.Transform.Pivot,
                pivot, transform.Scale, geometryTargets.Max(target => target.MaximumDisplacement), request.Operation.Limits.MaximumVertexDisplacement,
                geometryTargets, boxes.OrderBy(box => box.ResourceBlockIndex).ThenBy(box => box.FieldPath, StringComparer.Ordinal).ToArray(),
                preserved.OrderBy(target => target.ResourceBlockIndex).ThenBy(target => target.FieldPath, StringComparer.Ordinal).ToArray(), sourceBlocks)
            { Region = request.Operation.Region is { } selection ? new(selection, regionFacts) : null },
        };
    }

    private static (Source2AffineProfile[] Profiles, int[] PresentLods) ResolveExperimentalPlanningProfiles(
        TransformPlanningRequest request, ParsedModel parsed)
    {
        RecipeValidator.Validate(new RecipeDocument
        {
            SchemaVersion = request.Operation.Version == 6 ? 7 : 6,
            RecipeId = "experimental",
            InputHash = request.Input.ContentHash,
            Operations = [request.Operation],
        });
        if (HasIncompleteMdatCoverage(parsed.Snapshot) || parsed.Envelope.Blocks.Any(block => block.Type == "MBUF"))
            throw ExperimentalUnsupported("EXPERIMENTAL_LAYOUT_UNSUPPORTED", "The resource does not have a complete ordinary root-buffer inventory.");
        var profiles = request.SelectedDrawCalls.GroupBy(call => call.MeshOrdinal).OrderBy(group => group.Key)
            .Select(group => CreateRootBufferProfile(request, parsed, group.ToArray(), preserveAuthoredEnvelopes: true))
            .OrderBy(profile => profile.Mesh.Lod).ToArray();
        var presentLods = parsed.Snapshot.Lods.Select(lod => lod.Level).Order().ToArray();
        if (!profiles.Select(profile => profile.Mesh.Lod).SequenceEqual(presentLods))
            throw ExperimentalUnsupported("TRANSFORM_GEOMETRY_LOD_COVERAGE_INCOMPLETE", "Exactly one complete buffer must be selected in every present LOD.");
        foreach (var profile in profiles)
        {
            if (System.Numerics.BitOperations.PopCount(profile.Mesh.LodMask) != 1
                || profile.SelectedVertices.Length != profile.Vertices.Snapshot.VertexCount
                || profile.SelectedVertices.Where((vertex, index) => vertex != index).Any())
                throw ExperimentalUnsupported("EXPERIMENTAL_COMPLETE_BUFFER_REQUIRED", "Partial buffers and meshes shared across LODs remain unsupported.");
            ValidateExperimentalVertexStreams(profile);
        }
        return (profiles, presentLods);
    }

    private static (Model Model, KVObject Spheres, HashSet<int> AffectedRoots) ResolveExperimentalPlanningRoot(
        ParsedModel parsed, IReadOnlyList<Source2AffineProfile> profiles)
    {
        var model = parsed.Resource.Blocks.OfType<Model>().Single();
        if (HasMorphData(model.Data) || model.Data.ContainsKey("m_vMinBounds") || model.Data.ContainsKey("m_vMaxBounds"))
            throw ExperimentalUnsupported("EXPERIMENTAL_ROOT_METADATA_UNSUPPORTED", "Root model bounds or morph data require a separately reviewed update rule.");
        var rootSkeleton = ExperimentalCollection(model.Data, "m_modelSkeleton");
        var rootNames = ExperimentalArray(rootSkeleton, "m_boneName");
        var rootSpheres = ExperimentalArray(rootSkeleton, "m_boneSphere");
        if (rootNames.Count == 0 || rootNames.Count != rootSpheres.Count || model.Skeleton.Bones.Length != rootNames.Count)
            throw ExperimentalUnsupported("EXPERIMENTAL_ROOT_METADATA_UNSUPPORTED", "Root sphere and skeleton identities do not have the characterized layout.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < rootNames.Count; index++)
        {
            var name = rootNames[index].ToString(CultureInfo.InvariantCulture);
            if (rootNames[index].ValueType != KVValueType.String || string.IsNullOrWhiteSpace(name)
                || !names.Add(name) || model.Skeleton.Bones[index].Name != name)
                throw ExperimentalUnsupported("EXPERIMENTAL_ROOT_METADATA_UNSUPPORTED", "Root bone identities must be unique, typed and consistent with the render remap inventory.");
        }
        var affectedRoots = ResolveExperimentalAffectedRoots(model, profiles, rootNames.Count);
        return (model, rootSpheres, affectedRoots);
    }

    private static (PlannedGeometryTarget Target, Point3[] AllAfter) PlanExperimentalBufferGeometry(
        TransformPlanningRequest request, Source2AffineProfile profile, ResolvedTransformPivot pivot,
        UniformTransform transform, IMeshOptimizerCodec codec, List<PlannedRegionBuffer> regionFacts)
    {
        var beforePoints = profile.SelectedVertices.Select(vertex => Source2GeometryAnalyzer.ReadPosition(profile.Vertices, vertex)).ToArray();
        var afterPoints = new Point3[beforePoints.Length];
        TransformSummary summary;
        byte[]? regionBytes = null;
        if (request.Operation.Region is { } region)
        {
            var calculated = PlanRegionBuffer(profile, region, transform.Scale, pivot.Point);
            regionBytes = calculated.Bytes;
            afterPoints = calculated.Points;
            regionFacts.Add(calculated.Facts);
            summary = new(Bounds3.FromPoints(beforePoints), Bounds3.FromPoints(afterPoints), calculated.Facts.ChangedVertexCount, calculated.MaximumDisplacement);
        }
        else summary = transform.ApplyAndSummarize(beforePoints, afterPoints);
        if (summary.ChangedPointCount == 0 || summary.MaximumDisplacement <= 0f)
            throw ExperimentalUnsupported("TRANSFORM_EMPTY_SELECTION_EFFECT", "The experimental edit must change positions in every LOD.");
        if (summary.MaximumDisplacement > request.Operation.Limits.MaximumVertexDisplacement || summary.MaximumDisplacement > 64f)
            throw ExperimentalUnsupported("TRANSFORM_DISPLACEMENT_EXCEEDED", "The experimental edit exceeds its declared displacement cap.");
        var intended = regionBytes ?? (byte[])profile.Vertices.Decoded.Clone();
        var allAfter = (Point3[])profile.AllBeforePositions.Clone();
        for (var index = 0; index < afterPoints.Length; index++)
        {
            WritePosition(intended, profile.Vertices.Snapshot.PositionLayout, profile.SelectedVertices[index], afterPoints[index]);
            allAfter[profile.BufferBaseOffset + profile.SelectedVertices[index]] = afterPoints[index];
        }

        if (regionBytes is null) VerifyOnlyPositionBytesChanged(profile.Vertices.Decoded, intended, profile.Vertices.Snapshot.PositionLayout, profile.SelectedVertices);
        var encoded = EncodeDeterministically(codec, intended, profile.Vertices.Snapshot, "experimental planning");
        if (!codec.DecodeVertexBuffer(encoded, profile.Vertices.Snapshot.VertexCount, profile.Vertices.Snapshot.Stride).AsSpan().SequenceEqual(intended))
            throw ExperimentalUnsupported("EXPERIMENTAL_CODEC_ROUNDTRIP_FAILED", "Changed positions are not preserved by the configured codec.");
        var target = new PlannedGeometryTarget(profile.Mesh.Lod, request.Input.LogicalPath, profile.Mesh.MeshOrdinal,
            profile.Mesh.BlockIndex, profile.Vertices.Snapshot.Ordinal, profile.Indices.Snapshot.Ordinal,
            profile.Vertices.Snapshot.ResourceBlockIndex, profile.Indices.Snapshot.ResourceBlockIndex,
            profile.Vertices.Snapshot.EncodedHash, profile.Indices.Snapshot.EncodedHash, profile.Vertices.Snapshot.DecodedHash,
            ContentHash.Compute(intended), profile.Indices.Snapshot.DecodedHash, profile.VertexSetHash, profile.SelectedVertices.Length,
            profile.Vertices.Snapshot.PositionLayout, profile.SelectionBounds, ToAffineBounds(summary.AfterBounds), pivot.Point,
            transform.Scale, new(), summary.MaximumDisplacement, regionBytes is null ? ["position"] : ["normal_tangent", "position"], profile.Mesh.Geometry.Codec!);
        return (target, allAfter);
    }

    private static void AddExperimentalPlanningRootSpheres(ParsedModel parsed, Model model,
        KVObject rootSpheres, HashSet<int> affectedRoots, List<PlannedExperimentalPreservationTarget> preserved)
    {
        var modelBlockIndex = parsed.Resource.Blocks.Select((block, index) => (block, index)).Single(item => ReferenceEquals(item.block, model)).index;
        var rootHash = ContentHash.Compute(parsed.Envelope.Blocks[modelBlockIndex].Payload.Span);
        for (var index = 0; index < rootSpheres.Count; index++)
        {
            var radius = ExperimentalFloat(rootSpheres[index]);
            var affected = affectedRoots.Contains(index);
            ValidateExperimentalRadius(radius, affected);
            preserved.Add(new(modelBlockIndex, $"m_modelSkeleton.m_boneSphere[{index}]", "root_sphere",
                affected ? "affected" : "resource", "preserve_unverified", rootHash, [BitConverter.SingleToUInt32Bits(radius)]));
        }
    }

    private static HashSet<int> ResolveExperimentalAffectedRoots(Model model, IReadOnlyList<Source2AffineProfile> profiles, int rootCount)
    {
        var roots = new HashSet<int>();
        foreach (var profile in profiles)
        {
            var remap = model.GetRemapTable(profile.Mesh.MeshOrdinal);
            if (remap is null || remap.Any(index => index < 0 || index >= rootCount))
                throw ExperimentalUnsupported("EXPERIMENTAL_BONE_REMAP_UNSUPPORTED", "Complete mesh-to-root bone remap evidence is required.");
            foreach (var bone in profile.Metadata.BoneBounds.Where(bone => bone.InfluencedVertices.Any(vertex =>
                vertex >= profile.BufferBaseOffset && vertex < profile.BufferBaseOffset + profile.Vertices.Snapshot.VertexCount)))
            {
                if ((uint)bone.BoneIndex >= (uint)remap.Length)
                    throw ExperimentalUnsupported("EXPERIMENTAL_BONE_REMAP_UNSUPPORTED", "An affected bone is outside the serialized remap.");
                var index = remap[bone.BoneIndex];
                if (model.Skeleton.Bones[index].IsProceduralCloth)
                    throw ExperimentalUnsupported("EXPERIMENTAL_PROCEDURAL_UNSUPPORTED", "An affected bone participates in procedural cloth.");
                roots.Add(index);
            }
        }

        return roots;
    }

    private static PlannedExperimentalBoxTarget PlanExperimentalSceneBox(ArtifactContent input, Source2AffineProfile profile, Point3[] points)
    {
        var scene = ExperimentalArray(profile.Mesh.Block.Data, "m_sceneObjects")[0];
        var original = new Bounds3(ExperimentalVector(scene, "m_vMinBounds"), ExperimentalVector(scene, "m_vMaxBounds"));
        if (original.Min.X >= original.Max.X || original.Min.Y >= original.Max.Y || original.Min.Z >= original.Max.Z)
            throw ExperimentalUnsupported("EXPERIMENTAL_DEGENERATE_BOX_UNSUPPORTED", "A scene box has an inverted or zero extent.");
        var result = CullingEnvelope.Expand(original, points);
        return new(profile.Mesh.BlockIndex, "m_sceneObjects[0].m_vMinBounds+m_vMaxBounds", "min_max", "model",
            HashExperimentalWords(ExperimentalIdentityMatrix), Words(ExperimentalIdentityMatrix),
            ExperimentalContributorsHash(input, profile, Enumerable.Range(0, points.Length)), points.Length,
            Words(original.Min, original.Max), Words(result.Bounds.Min, result.Bounds.Max), ExperimentalGrowth(result.Growth));
    }

    private static PlannedExperimentalBoxTarget PlanExperimentalBoneBox(
        ArtifactContent input, Source2AffineProfile profile, Source2BoneBoundsAnalysis bone, Point3[] points)
    {
        var rawBone = ExperimentalArray(ExperimentalCollection(profile.Mesh.Block.Data, "m_skeleton"), "m_bones")[bone.BoneIndex];
        var rawMatrix = ExperimentalArray(rawBone, "m_invBindPose").Values.Select(ExperimentalFloat).ToArray();
        if (rawMatrix.Length != 12 || !rawMatrix.SequenceEqual(bone.InverseBindPose))
            throw ExperimentalUnsupported("EXPERIMENTAL_MATRIX_UNSUPPORTED", "The bone matrix must have twelve exactly represented float words.");
        var rawBox = ExperimentalCollection(rawBone, "m_bbox");
        var center = ExperimentalVector(rawBox, "m_vecCenter");
        var half = ExperimentalVector(rawBox, "m_vecSize");
        if (half.X <= 0 || half.Y <= 0 || half.Z <= 0)
            throw ExperimentalUnsupported("EXPERIMENTAL_DEGENERATE_BOX_UNSUPPORTED", "An affected bone box has an inverted or zero half-extent.");
        ValidateExperimentalRadius(bone.SphereRadius, affected: true);
        var original = new CenterHalfExtentBounds(center, half);
        var corners = new List<Point3>(checked(bone.InfluencedVertices.Length * 2));
        foreach (var index in bone.InfluencedVertices)
        {
            var enclosure = AffinePointEnclosure.Enclose(points[index], rawMatrix);
            corners.Add(enclosure.Min);
            corners.Add(enclosure.Max);
        }

        var result = CullingEnvelope.Expand(original, corners);
        return new(profile.Mesh.BlockIndex, $"m_skeleton.m_bones[{bone.BoneIndex}].m_bbox.m_vecCenter+m_vecSize", "center_half_extent",
            "render_inverse_bind", HashExperimentalWords(rawMatrix), Words(rawMatrix),
            ExperimentalContributorsHash(input, profile, bone.InfluencedVertices), bone.InfluencedVertices.Length,
            Words(original.Center, original.HalfExtents), Words(result.Bounds.Center, result.Bounds.HalfExtents), ExperimentalGrowth(result.Growth));
    }

    private static ContentHash ExperimentalContributorsHash(ArtifactContent input, Source2AffineProfile profile, IEnumerable<int> indices)
    {
        var text = new StringBuilder("experimental-contributors-v1\n").Append(input.ContentHash).Append('\n')
            .Append(profile.Mesh.BlockIndex.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var buffer in profile.Mesh.GeometryAnalysis!.VertexBuffers)
            text.Append(buffer.Snapshot.Ordinal.ToString(CultureInfo.InvariantCulture)).Append(':').Append(buffer.Snapshot.DecodedHash).Append('\n');
        foreach (var index in indices) text.Append(index.ToString(CultureInfo.InvariantCulture)).Append(',');
        return ContentHash.Compute(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static ComponentSelector NormalizeExperimentalSelector(ComponentSelector selector) => selector.Kind == "material_exact"
        ? selector with { MaterialPath = StableIdentity.NormalizePath(selector.MaterialPath!) }
        : selector with { DrawCallIds = selector.DrawCallIds!.Order(StringComparer.Ordinal).ToArray() };

    private static ExperimentalBoundsGrowth[] ExperimentalGrowth(IEnumerable<EnvelopeGrowth> growth) => growth
        .Select(value => new ExperimentalBoundsGrowth(value.Measure, value.OriginalValue, value.PlannedValue, value.Delta)).ToArray();

    private static uint[] Words(params float[] values) => values.Select(BitConverter.SingleToUInt32Bits).ToArray();
    private static uint[] Words(Point3 first, Point3 second) => Words(first.X, first.Y, first.Z, second.X, second.Y, second.Z);

    private static ContentHash HashExperimentalWords(float[] values)
    {
        var bytes = new byte[checked(values.Length * sizeof(float))];
        for (var index = 0; index < values.Length; index++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(float)), BitConverter.SingleToUInt32Bits(values[index]));
        return ContentHash.Compute(bytes);
    }

    private static S2ModKitException ExperimentalUnsupported(string code, string message) => Errors.Unsupported(code, message,
        "Choose a complete, nondegenerate root-buffer component satisfying the explicitly experimental visual-only contract; no fallback is performed.");
}
