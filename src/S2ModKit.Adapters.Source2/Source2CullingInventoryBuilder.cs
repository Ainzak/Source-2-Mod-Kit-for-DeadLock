using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using S2ModKit.Application;
using S2ModKit.Domain;
using ValveKeyValue;

namespace S2ModKit.Adapters.Source2;

internal sealed record Source2CullingInventoryMeshInput(
    int MeshOrdinal,
    int MeshBlockIndex,
    ulong LodMask,
    KVObject MeshData,
    Source2GeometryAnalysis? Geometry,
    Source2BoundsDiagnosticMesh? Diagnostic);

internal static class Source2CullingInventoryBuilder
{
    private static readonly float[] ModelIdentityMatrix =
    [
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 1f, 0f,
    ];

    public static CullingInventoryDocument Build(
        ArtifactContent artifact,
        int modelDataBlockIndex,
        KVObject modelData,
        IReadOnlyList<int> presentLods,
        IReadOnlyList<Source2CullingInventoryMeshInput> meshes)
    {
        var resource = new CullingResourceIdentity(artifact.LogicalPath, artifact.ContentHash, artifact.Bytes.Length);
        var fields = ImmutableArray.CreateBuilder<CullingInventoryField>();
        var orderedMeshes = meshes.OrderBy(item => item.MeshOrdinal).ToArray();

        AddModelBounds(artifact, modelDataBlockIndex, modelData, presentLods, orderedMeshes, fields);
        foreach (var mesh in orderedMeshes)
        {
            AddSceneBounds(artifact, mesh, fields);
            AddRenderBoneBounds(artifact, mesh, fields);
        }

        AddRootModelSpheres(artifact, modelDataBlockIndex, modelData, presentLods, orderedMeshes, fields);
        var result = fields.ToImmutable();
        var status = result.Any(field => field.Status == "unsupported") ? "partial" : "verified";
        var document = new CullingInventoryDocument(
            CullingEnvelopeContract.InventorySchemaVersion,
            resource,
            status,
            result,
            BuildBoneRemaps(artifact, modelData, orderedMeshes),
            new Dictionary<string, System.Text.Json.JsonElement>());
        return document with { Extensions = Source2RootSphereAggregationAnalyzer.Attach(document) };
    }

    private static CullingMeshBoneRemapInventory[] BuildBoneRemaps(
        ArtifactContent artifact,
        KVObject modelData,
        IReadOnlyList<Source2CullingInventoryMeshInput> meshes)
    {
        var modelNames = ImmutableArray<string>.Empty;
        var modelNamesStatus = modelData.TryGetValue("m_modelSkeleton", out var rawModelSkeleton)
            && rawModelSkeleton is not null && rawModelSkeleton.IsCollection
                ? ReadModelBoneNames(rawModelSkeleton, out modelNames)
                : "absent";
        if (modelNamesStatus != "verified")
        {
            modelNames = [];
        }

        return meshes.Select(mesh =>
        {
            var renderNames = ImmutableArray<string>.Empty;
            var renderNamesStatus = mesh.MeshData.TryGetValue("m_skeleton", out var rawRenderSkeleton)
                && rawRenderSkeleton is not null && rawRenderSkeleton.IsCollection
                    ? ReadRenderBoneNames(rawRenderSkeleton, out renderNames)
                    : "absent";
            if (renderNamesStatus != "verified")
            {
                renderNames = [];
            }

            var diagnostic = mesh.Diagnostic;
            var remapStatus = diagnostic?.BoneRemapStatus ?? "unsupported";
            var remapValues = diagnostic is { BoneRemapStatus: "verified" }
                ? diagnostic.BoneRemap
                : ImmutableArray<int>.Empty;
            ContentHash? remapIdentity = diagnostic?.BoneRemapIdentity;
            string? reasonCode = null;
            if (remapStatus == "verified"
                && (diagnostic is null
                    || renderNamesStatus != "verified"
                    || remapValues.Any(index => index < 0)
                    || (modelNamesStatus == "verified" && remapValues.Any(index => index >= modelNames.Length))
                    || remapIdentity is null))
            {
                remapStatus = "unsupported";
                remapIdentity = null;
                remapValues = [];
                reasonCode = "BONE_REMAP_OR_NAME_LAYOUT_MALFORMED";
            }
            else if (remapStatus == "absent")
            {
                remapIdentity = null;
                remapValues = [];
                reasonCode = "MESH_TO_MODEL_BONE_REMAP_ABSENT";
            }
            else if (remapStatus != "verified")
            {
                remapStatus = "unsupported";
                remapIdentity = null;
                remapValues = [];
                reasonCode = "MESH_TO_MODEL_BONE_REMAP_UNSUPPORTED";
            }

            return new CullingMeshBoneRemapInventory(
                artifact.LogicalPath,
                artifact.ContentHash,
                mesh.MeshOrdinal,
                mesh.MeshBlockIndex,
                ExpandLods(mesh.LodMask),
                remapStatus,
                remapIdentity,
                remapStatus == "verified" ? remapValues : null,
                renderNamesStatus,
                renderNamesStatus == "verified" ? renderNames : null,
                modelNamesStatus,
                modelNamesStatus == "verified" ? modelNames : null,
                reasonCode);
        }).ToArray();
    }

    private static void AddModelBounds(
        ArtifactContent artifact,
        int modelDataBlockIndex,
        KVObject modelData,
        IReadOnlyList<int> presentLods,
        IReadOnlyList<Source2CullingInventoryMeshInput> meshes,
        ImmutableArray<CullingInventoryField>.Builder fields)
    {
        var hasMinimum = modelData.TryGetValue("m_vMinBounds", out var minimumValue);
        var hasMaximum = modelData.TryGetValue("m_vMaxBounds", out var maximumValue);
        var identity = CreateFieldIdentity(
            artifact,
            modelDataBlockIndex,
            "DATA",
            "m_vMinBounds+m_vMaxBounds",
            null,
            null,
            presentLods);
        if (!hasMinimum && !hasMaximum)
        {
            fields.Add(new CullingInventoryField(identity, "absent", null, null, null));
            return;
        }

        if (!hasMinimum || !hasMaximum
            || !TryReadVector(minimumValue, out var minimum)
            || !TryReadVector(maximumValue, out var maximum)
            || !IsOrdered(minimum!, maximum!))
        {
            fields.Add(new CullingInventoryField(identity, "unsupported", null, UnsupportedSpace("MODEL_AABB_LAYOUT_UNSUPPORTED"), UnsupportedContributors("MODEL_AABB_CONTRIBUTORS_UNAVAILABLE")));
            return;
        }

        var sources = meshes.SelectMany(mesh => BuildSources(artifact, mesh, null, null)).ToArray();
        if (sources.Length == 0 || meshes.Any(mesh => mesh.Geometry is null || mesh.Diagnostic?.Status != "analyzed"))
        {
            fields.Add(new CullingInventoryField(
                identity,
                "unsupported",
                MinMaxValue(minimum!, maximum!),
                UnsupportedSpace("MODEL_AABB_SPACE_UNVERIFIED"),
                UnsupportedContributors("MODEL_AABB_CONTRIBUTORS_INCOMPLETE")));
            return;
        }

        var vertexCount = sources.Sum(source => source.ContributorCount);
        var memberHash = HashText(string.Join('\n', sources.Select(source => $"{source.ResourceHash}:{source.MeshOrdinal}:{source.VertexBufferOrdinal}:all:{source.VertexCount}")));
        fields.Add(new CullingInventoryField(
            identity,
            "unsupported",
            MinMaxValue(minimum!, maximum!),
            UnsupportedSpace("MODEL_AABB_COORDINATE_SPACE_NOT_VERIFIED"),
            VerifiedContributors(vertexCount, sources, HashSetIdentity("model_aabb", [memberHash], sources))));
    }

    private static void AddSceneBounds(
        ArtifactContent artifact,
        Source2CullingInventoryMeshInput mesh,
        ImmutableArray<CullingInventoryField>.Builder fields)
    {
        var coveredLods = ExpandLods(mesh.LodMask);
        if (!mesh.MeshData.TryGetValue("m_sceneObjects", out var sceneObjects))
        {
            fields.Add(new CullingInventoryField(
                CreateFieldIdentity(artifact, mesh.MeshBlockIndex, "MDAT", "m_sceneObjects[].m_vMinBounds+m_vMaxBounds", mesh.MeshOrdinal, null, coveredLods),
                "absent",
                null,
                null,
                null));
            return;
        }

        if (sceneObjects is null || !sceneObjects.IsArray || sceneObjects.Count == 0)
        {
            fields.Add(new CullingInventoryField(
                CreateFieldIdentity(artifact, mesh.MeshBlockIndex, "MDAT", "m_sceneObjects[].m_vMinBounds+m_vMaxBounds", mesh.MeshOrdinal, null, coveredLods),
                "unsupported",
                null,
                UnsupportedSpace("SCENE_BOUNDS_LAYOUT_UNSUPPORTED"),
                UnsupportedContributors("SCENE_BOUNDS_CONTRIBUTORS_UNAVAILABLE")));
            return;
        }

        var drawCallOrdinal = 0;
        var sceneContributorMappingAvailable = true;
        for (var sceneIndex = 0; sceneIndex < sceneObjects.Count; sceneIndex++)
        {
            var fieldPath = $"m_sceneObjects[{sceneIndex}].m_vMinBounds+m_vMaxBounds";
            var identity = CreateFieldIdentity(artifact, mesh.MeshBlockIndex, "MDAT", fieldPath, mesh.MeshOrdinal, sceneIndex, coveredLods);
            var scene = sceneObjects[sceneIndex];
            if (!TryReadSceneBounds(scene, out var minimum, out var maximum))
            {
                sceneContributorMappingAvailable = false;
                fields.Add(new CullingInventoryField(identity, "unsupported", null, UnsupportedSpace("SCENE_BOUNDS_LAYOUT_UNSUPPORTED"), UnsupportedContributors("SCENE_BOUNDS_CONTRIBUTORS_UNAVAILABLE")));
                continue;
            }

            if (!sceneContributorMappingAvailable)
            {
                fields.Add(new CullingInventoryField(
                    identity,
                    "unsupported",
                    MinMaxValue(minimum!, maximum!),
                    ModelSpace(),
                    UnsupportedContributors("SCENE_DRAW_CALL_MAPPING_INVALIDATED")));
                continue;
            }

            if (!TryBuildSceneContributors(artifact, mesh, scene, ref drawCallOrdinal, out var contributorSet))
            {
                sceneContributorMappingAvailable = false;
                fields.Add(new CullingInventoryField(
                    identity,
                    "unsupported",
                    MinMaxValue(minimum!, maximum!),
                    ModelSpace(),
                    contributorSet));
                continue;
            }

            var boundsStatus = HasStrictlyPositiveExtent(minimum!, maximum!) ? "verified" : "unsupported";
            fields.Add(new CullingInventoryField(identity, boundsStatus, MinMaxValue(minimum!, maximum!), ModelSpace(), contributorSet));
        }
    }

    private static bool TryReadSceneBounds(
        KVObject? scene,
        out TransformVector3? minimum,
        out TransformVector3? maximum)
    {
        minimum = null;
        maximum = null;
        return scene is not null && scene.IsCollection
            && scene.TryGetValue("m_vMinBounds", out var rawMinimum)
            && scene.TryGetValue("m_vMaxBounds", out var rawMaximum)
            && TryReadVector(rawMinimum, out minimum)
            && TryReadVector(rawMaximum, out maximum)
            && IsOrdered(minimum!, maximum!);
    }

    private static bool TryBuildSceneContributors(
        ArtifactContent artifact,
        Source2CullingInventoryMeshInput mesh,
        KVObject scene,
        ref int drawCallOrdinal,
        out CullingContributorSetIdentity contributors)
    {
        contributors = UnsupportedContributors("SCENE_DRAW_CALL_MEMBERSHIP_UNAVAILABLE");
        if (mesh.Geometry is null || mesh.Diagnostic?.Status != "analyzed"
            || !scene.TryGetValue("m_drawCalls", out var sceneDrawCalls)
            || sceneDrawCalls is null || !sceneDrawCalls.IsArray)
        {
            return false;
        }

        var selectedVertices = new Dictionary<int, SortedSet<int>>();
        var candidateDrawCallOrdinal = drawCallOrdinal;
        for (var index = 0; index < sceneDrawCalls.Count; index++)
        {
            if (candidateDrawCallOrdinal >= mesh.Geometry.DrawCalls.Count)
            {
                return false;
            }

            var drawCall = mesh.Geometry.DrawCalls[candidateDrawCallOrdinal++];
            var bufferOrdinal = drawCall.Snapshot.VertexBufferOrdinal;
            if (!selectedVertices.TryGetValue(bufferOrdinal, out var vertices))
            {
                vertices = [];
                selectedVertices.Add(bufferOrdinal, vertices);
            }

            vertices.UnionWith(drawCall.VertexIndices);
        }

        var sourceFacts = new List<(Source2BoundsDiagnosticBuffer Buffer, int Count, ContentHash MembershipHash)>();
        foreach (var buffer in mesh.Diagnostic.Buffers)
        {
            var vertices = selectedVertices.GetValueOrDefault(buffer.VertexBufferOrdinal) ?? [];
            var membershipText = new StringBuilder();
            foreach (var vertexIndex in vertices)
            {
                if (vertexIndex < 0 || vertexIndex >= mesh.Geometry.VertexBuffers[buffer.VertexBufferOrdinal].Snapshot.VertexCount)
                {
                    return false;
                }

                var point = Source2GeometryAnalyzer.ReadPosition(mesh.Geometry.VertexBuffers[buffer.VertexBufferOrdinal], vertexIndex);
                membershipText.Append(vertexIndex.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(BitConverter.SingleToInt32Bits(point.X).ToString("x8", CultureInfo.InvariantCulture)).Append(',')
                    .Append(BitConverter.SingleToInt32Bits(point.Y).ToString("x8", CultureInfo.InvariantCulture)).Append(',')
                    .Append(BitConverter.SingleToInt32Bits(point.Z).ToString("x8", CultureInfo.InvariantCulture)).Append('\n');
            }

            sourceFacts.Add((buffer, vertices.Count, HashText(membershipText.ToString())));
        }

        if (sourceFacts.Count == 0 || sourceFacts.Count != mesh.Geometry.VertexBuffers.Count
            || sourceFacts.Count != mesh.Geometry.IndexBuffers.Count)
        {
            return false;
        }

        var sources = sourceFacts.Select(item => CreateSource(artifact, mesh, item.Buffer, item.Count)).ToArray();
        var identityHash = HashSetIdentity("scene", sourceFacts.Select(item => item.MembershipHash), sources);
        contributors = VerifiedContributors(sourceFacts.Sum(item => item.Count), sources, identityHash);
        drawCallOrdinal = candidateDrawCallOrdinal;
        return true;
    }

    private static void AddRenderBoneBounds(
        ArtifactContent artifact,
        Source2CullingInventoryMeshInput mesh,
        ImmutableArray<CullingInventoryField>.Builder fields)
    {
        var coveredLods = ExpandLods(mesh.LodMask);
        if (!mesh.MeshData.TryGetValue("m_skeleton", out var rawSkeleton))
        {
            AddUnsupportedRenderBoneFields(artifact, mesh, coveredLods, "RENDER_SKELETON_ABSENT", fields);
            return;
        }

        if (rawSkeleton is null || !rawSkeleton.IsCollection
            || !rawSkeleton.TryGetValue("m_bones", out var rawBones)
            || rawBones is null || !rawBones.IsArray || rawBones.Count == 0)
        {
            AddUnsupportedRenderBoneFields(artifact, mesh, coveredLods, "RENDER_SKELETON_LAYOUT_UNSUPPORTED", fields);
            return;
        }

        var diagnosedBones = mesh.Diagnostic?.Bones.ToDictionary(bone => bone.BoneIndex)
            ?? new Dictionary<int, Source2BoundsDiagnosticBone>();
        var geometryReady = mesh.Geometry is not null && mesh.Diagnostic?.Status == "analyzed";
        for (var boneIndex = 0; boneIndex < rawBones.Count; boneIndex++)
        {
            var fieldPrefix = $"m_skeleton.m_bones[{boneIndex}]";
            var bone = rawBones[boneIndex];
            if (bone is null || !bone.IsCollection)
            {
                AddUnsupportedBone(artifact, mesh, boneIndex, coveredLods, "RENDER_BONE_LAYOUT_UNSUPPORTED", fields);
                continue;
            }

            var hasBox = TryReadCenterHalfExtents(bone, "m_bbox", out var center, out var halfExtents);
            var hasMatrix = TryReadFloatArray(bone, "m_invBindPose", 12, out var matrix);
            var hasRadius = TryReadFiniteFloat(bone, "m_flSphereRadius", out var radius) && radius >= 0f;
            var diagnosed = diagnosedBones.GetValueOrDefault(boneIndex);
            var coordinate = hasMatrix
                ? BoneSpace(matrix)
                : UnsupportedSpace("RENDER_BONE_MATRIX_UNSUPPORTED");
            var contributors = geometryReady
                ? BoneContributors(artifact, mesh, boneIndex, diagnosed)
                : UnsupportedContributors("RENDER_BONE_CONTRIBUTORS_UNAVAILABLE");
            var bboxStatus = hasBox && HasStrictlyPositiveExtent(halfExtents!) && hasMatrix && contributors.Status == "verified"
                ? "verified"
                : "unsupported";
            var boxIdentity = CreateFieldIdentity(artifact, mesh.MeshBlockIndex, "MDAT", $"{fieldPrefix}.m_bbox.m_vecCenter+m_vecSize", mesh.MeshOrdinal, boneIndex, coveredLods);
            fields.Add(new CullingInventoryField(
                boxIdentity,
                bboxStatus,
                hasBox ? CenterHalfExtentsValue(center!, halfExtents!) : null,
                coordinate,
                contributors));

            var sphereIdentity = CreateFieldIdentity(artifact, mesh.MeshBlockIndex, "MDAT", $"{fieldPrefix}.m_flSphereRadius", mesh.MeshOrdinal, boneIndex, coveredLods);
            fields.Add(new CullingInventoryField(
                sphereIdentity,
                "unsupported",
                hasRadius ? SphereValue(radius) : null,
                UnsupportedSpace("RENDER_BONE_SPHERE_SPACE_NOT_VERIFIED"),
                contributors));

        }
    }

    private static void AddRootModelSpheres(
        ArtifactContent artifact,
        int modelDataBlockIndex,
        KVObject modelData,
        IReadOnlyList<int> presentLods,
        IReadOnlyList<Source2CullingInventoryMeshInput> meshes,
        ImmutableArray<CullingInventoryField>.Builder fields)
    {
        var identity = CreateFieldIdentity(
            artifact,
            modelDataBlockIndex,
            "DATA",
            "m_modelSkeleton.m_boneSphere",
            null,
            null,
            presentLods);
        if (!modelData.TryGetValue("m_modelSkeleton", out var skeleton))
        {
            fields.Add(new CullingInventoryField(identity, "absent", null, null, null));
            return;
        }

        if (skeleton is null || !skeleton.IsCollection)
        {
            fields.Add(new CullingInventoryField(identity, "unsupported", null, UnsupportedSpace("MODEL_SKELETON_LAYOUT_UNSUPPORTED"), UnsupportedContributors("MODEL_SKELETON_CONTRIBUTORS_UNAVAILABLE")));
            return;
        }

        if (!skeleton.TryGetValue("m_boneSphere", out var sphereValues))
        {
            fields.Add(new CullingInventoryField(identity, "absent", null, null, null));
            return;
        }

        if (sphereValues is null || !sphereValues.IsArray || sphereValues.Count == 0)
        {
            fields.Add(new CullingInventoryField(identity, "unsupported", null, UnsupportedSpace("MODEL_SKELETON_SPHERE_LAYOUT_UNSUPPORTED"), UnsupportedContributors("MODEL_SKELETON_CONTRIBUTORS_UNAVAILABLE")));
            return;
        }

        var namesStatus = ReadModelBoneNames(skeleton, out var names);
        for (var modelBoneIndex = 0; modelBoneIndex < sphereValues.Count; modelBoneIndex++)
        {
            var fieldPath = $"m_modelSkeleton.m_boneSphere[{modelBoneIndex}]";
            var fieldIdentity = CreateFieldIdentity(artifact, modelDataBlockIndex, "DATA", fieldPath, null, modelBoneIndex, presentLods);
            var hasRadius = TryReadFiniteFloat(sphereValues[modelBoneIndex], out var radius) && radius >= 0f;
            var rootContributors = RootBoneContributors(artifact, modelBoneIndex, meshes, namesStatus, names);
            fields.Add(new CullingInventoryField(
                fieldIdentity,
                "unsupported",
                hasRadius ? SphereValue(radius) : null,
                UnsupportedSpace("MODEL_BONE_SPHERE_SPACE_NOT_VERIFIED"),
                rootContributors));
        }
    }

    private static CullingContributorSetIdentity RootBoneContributors(
        ArtifactContent artifact,
        int modelBoneIndex,
        IReadOnlyList<Source2CullingInventoryMeshInput> meshes,
        string namesStatus,
        ImmutableArray<string> names)
    {
        if (namesStatus != "verified" || modelBoneIndex >= names.Length
            || meshes.Any(mesh => mesh.Geometry is null
                || mesh.Diagnostic?.Status != "analyzed"
                || mesh.Diagnostic.BoneRemapStatus != "verified"))
        {
            return UnsupportedContributors("ROOT_BONE_REMAP_OR_NAME_EVIDENCE_INCOMPLETE");
        }

        var sources = new List<CullingContributorSourceIdentity>();
        var memberHashes = new List<ContentHash>();
        var count = 0;
        foreach (var mesh in meshes.OrderBy(item => item.MeshOrdinal))
        {
            var rootBone = mesh.Diagnostic!.RootBoneContributors.SingleOrDefault(item => item.ModelBoneIndex == modelBoneIndex);
            count += rootBone?.ContributorCount ?? 0;
            memberHashes.Add(rootBone?.ContributorSetIdentity ?? HashText($"empty|{mesh.MeshOrdinal}|{modelBoneIndex}"));
            var perBuffer = rootBone?.ContributorBuffers.ToDictionary(item => item.VertexBufferOrdinal, item => item.ContributorCount)
                ?? new Dictionary<int, int>();
            sources.AddRange(BuildSources(artifact, mesh, perBuffer, modelBoneIndex));
        }

        if (sources.Count == 0)
        {
            return UnsupportedContributors("ROOT_BONE_SOURCE_BUFFERS_UNAVAILABLE");
        }

        return VerifiedContributors(count, sources, HashSetIdentity($"root-bone:{modelBoneIndex}", memberHashes, sources));
    }

    private static CullingContributorSetIdentity BoneContributors(
        ArtifactContent artifact,
        Source2CullingInventoryMeshInput mesh,
        int boneIndex,
        Source2BoundsDiagnosticBone? bone)
    {
        if (mesh.Diagnostic is null || mesh.Geometry is null)
        {
            return UnsupportedContributors("RENDER_BONE_CONTRIBUTORS_UNAVAILABLE");
        }

        var perBuffer = bone?.ContributorBuffers.ToDictionary(item => item.VertexBufferOrdinal, item => item.ContributorCount)
            ?? new Dictionary<int, int>();
        var sources = BuildSources(artifact, mesh, perBuffer, null).ToArray();
        if (sources.Length == 0)
        {
            return UnsupportedContributors("RENDER_BONE_SOURCE_BUFFERS_UNAVAILABLE");
        }

        var memberHash = bone?.ContributorSetIdentity ?? HashText($"empty|{mesh.MeshOrdinal}|{boneIndex}");
        return VerifiedContributors(perBuffer.Values.Sum(), sources, HashSetIdentity($"render-bone:{boneIndex}", [memberHash], sources));
    }

    private static IEnumerable<CullingContributorSourceIdentity> BuildSources(
        ArtifactContent artifact,
        Source2CullingInventoryMeshInput mesh,
        IReadOnlyDictionary<int, int>? contributorsByBuffer,
        int? resolvedModelBoneIndex)
    {
        if (mesh.Diagnostic is null)
        {
            yield break;
        }

        foreach (var buffer in mesh.Diagnostic.Buffers.OrderBy(item => item.VertexBufferOrdinal))
        {
            var count = contributorsByBuffer is null
                ? buffer.VertexCount
                : contributorsByBuffer.GetValueOrDefault(buffer.VertexBufferOrdinal);
            yield return CreateSource(artifact, mesh, buffer, count, resolvedModelBoneIndex);
        }
    }

    private static CullingContributorSourceIdentity CreateSource(
        ArtifactContent artifact,
        Source2CullingInventoryMeshInput mesh,
        Source2BoundsDiagnosticBuffer buffer,
        int contributorCount,
        int? resolvedModelBoneIndex = null) => new(
        artifact.LogicalPath,
        artifact.ContentHash,
        ExpandLods(mesh.LodMask),
        mesh.MeshOrdinal,
        mesh.MeshBlockIndex,
        buffer.VertexBufferOrdinal,
        buffer.VertexResourceBlockIndex,
        buffer.IndexBufferOrdinal,
        buffer.IndexResourceBlockIndex,
        buffer.VertexCount,
        contributorCount,
        buffer.VertexBlockHash,
        buffer.DecodedVertexBufferHash,
        buffer.IndexBlockHash,
        buffer.DecodedIndexBufferHash,
        buffer.BlendIndexFormat,
        buffer.BlendWeightFormat,
        mesh.Diagnostic?.BoneRemapStatus ?? "unsupported",
        mesh.Diagnostic?.BoneRemapIdentity,
        resolvedModelBoneIndex);

    private static CullingInventoryField CreateUnsupportedField(
        CullingFieldIdentity identity,
        string reasonCode) => new(
        identity,
        "unsupported",
        null,
        UnsupportedSpace(reasonCode),
        UnsupportedContributors(reasonCode));

    private static void AddUnsupportedRenderBoneFields(
        ArtifactContent artifact,
        Source2CullingInventoryMeshInput mesh,
        IReadOnlyList<int> coveredLods,
        string reasonCode,
        ImmutableArray<CullingInventoryField>.Builder fields)
    {
        fields.Add(CreateUnsupportedField(
            CreateFieldIdentity(artifact, mesh.MeshBlockIndex, "MDAT", "m_skeleton.m_bones[].m_bbox", mesh.MeshOrdinal, null, coveredLods),
            reasonCode));
        fields.Add(CreateUnsupportedField(
            CreateFieldIdentity(artifact, mesh.MeshBlockIndex, "MDAT", "m_skeleton.m_bones[].m_flSphereRadius", mesh.MeshOrdinal, null, coveredLods),
            reasonCode));
    }

    private static void AddUnsupportedBone(
        ArtifactContent artifact,
        Source2CullingInventoryMeshInput mesh,
        int boneIndex,
        IReadOnlyList<int> coveredLods,
        string reasonCode,
        ImmutableArray<CullingInventoryField>.Builder fields)
    {
        fields.Add(CreateUnsupportedField(
            CreateFieldIdentity(artifact, mesh.MeshBlockIndex, "MDAT", $"m_skeleton.m_bones[{boneIndex}].m_bbox", mesh.MeshOrdinal, boneIndex, coveredLods),
            reasonCode));
        fields.Add(CreateUnsupportedField(
            CreateFieldIdentity(artifact, mesh.MeshBlockIndex, "MDAT", $"m_skeleton.m_bones[{boneIndex}].m_flSphereRadius", mesh.MeshOrdinal, boneIndex, coveredLods),
            reasonCode));
    }

    private static CullingFieldIdentity CreateFieldIdentity(
        ArtifactContent artifact,
        int blockIndex,
        string blockType,
        string fieldPath,
        int? meshOrdinal,
        int? fieldOrdinal,
        IReadOnlyList<int> coveredLods) => new(
        artifact.LogicalPath,
        artifact.ContentHash,
        blockIndex,
        blockType,
        fieldPath,
        meshOrdinal,
        fieldOrdinal,
        coveredLods);

    private static CullingRawFieldValue MinMaxValue(TransformVector3 minimum, TransformVector3 maximum) =>
        new("aabb_min_max", minimum, maximum, null, null, null);

    private static CullingRawFieldValue CenterHalfExtentsValue(TransformVector3 center, TransformVector3 halfExtents) =>
        new("aabb_center_half_extents", null, null, center, halfExtents, null);

    private static CullingRawFieldValue SphereValue(float radius) => new("sphere_radius", null, null, null, null, radius);

    private static CullingCoordinateSpaceIdentity ModelSpace() =>
        VerifiedSpace("source2_model", ModelIdentityMatrix);

    private static CullingCoordinateSpaceIdentity BoneSpace(IReadOnlyList<float> matrix) =>
        VerifiedSpace("source2_render_bone_bind_local", matrix);

    private static CullingCoordinateSpaceIdentity VerifiedSpace(string spaceId, IReadOnlyList<float> matrix) =>
        new("verified", spaceId, HashFloatSequence(matrix), matrix.ToImmutableArray(), null);

    private static CullingCoordinateSpaceIdentity UnsupportedSpace(string reasonCode) =>
        new("unsupported", null, null, null, reasonCode);

    private static CullingContributorSetIdentity VerifiedContributors(
        int count,
        IReadOnlyList<CullingContributorSourceIdentity> sources,
        ContentHash identityHash) => new(
        "verified",
        CullingEnvelopeContract.ContributorIdentityAlgorithm,
        identityHash,
        count,
        sources,
        null);

    private static CullingContributorSetIdentity UnsupportedContributors(string reasonCode) =>
        new("unsupported", null, null, null, null, reasonCode);

    private static ContentHash HashSetIdentity(
        string scope,
        IEnumerable<ContentHash> memberHashes,
        IEnumerable<CullingContributorSourceIdentity> sources)
    {
        var builder = new StringBuilder(CullingEnvelopeContract.ContributorIdentityAlgorithm)
            .Append('|').Append(scope).Append('\n');
        foreach (var hash in memberHashes.OrderBy(item => item.Value, StringComparer.Ordinal))
        {
            builder.Append("member|").Append(hash.Value).Append('\n');
        }

        foreach (var source in sources.OrderBy(item => item.ResourcePath, StringComparer.Ordinal)
                     .ThenBy(item => item.MeshOrdinal)
                     .ThenBy(item => item.VertexBufferOrdinal))
        {
            builder.Append(source.ResourcePath).Append('|').Append(source.ResourceHash.Value).Append('|')
                .Append(source.MeshOrdinal.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(source.MeshResourceBlockIndex.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(source.VertexBufferOrdinal.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(source.VertexResourceBlockIndex.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(source.IndexBufferOrdinal.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(source.IndexResourceBlockIndex.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(source.ContributorCount.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(source.VertexBlockHash.Value).Append('|').Append(source.DecodedVertexBufferHash.Value).Append('|')
                .Append(source.IndexBlockHash.Value).Append('|').Append(source.DecodedIndexBufferHash.Value).Append('|')
                .Append(source.BoneRemapStatus).Append('|').Append(source.BoneRemapIdentity?.Value ?? "absent").Append('|')
                .Append(source.ResolvedModelBoneIndex?.ToString(CultureInfo.InvariantCulture) ?? "absent").Append('\n');
        }

        return HashText(builder.ToString());
    }

    private static bool TryReadCenterHalfExtents(
        KVObject parent,
        string field,
        out TransformVector3? center,
        out TransformVector3? halfExtents)
    {
        center = null;
        halfExtents = null;
        if (!parent.TryGetValue(field, out var bounds) || bounds is null || !bounds.IsCollection
            || !bounds.TryGetValue("m_vecCenter", out var rawCenter)
            || !bounds.TryGetValue("m_vecSize", out var rawSize)
            || !TryReadVector(rawCenter, out center)
            || !TryReadVector(rawSize, out halfExtents))
        {
            return false;
        }

        return halfExtents is not null && halfExtents.X >= 0f && halfExtents.Y >= 0f && halfExtents.Z >= 0f;
    }

    private static bool TryReadFloatArray(KVObject parent, string key, int length, out ImmutableArray<float> values)
    {
        values = [];
        if (!parent.TryGetValue(key, out var array) || array is null || !array.IsArray || array.Count != length)
        {
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<float>(length);
        for (var index = 0; index < length; index++)
        {
            if (!TryReadFiniteFloat(array[index], out var value))
            {
                return false;
            }

            builder.Add(value);
        }

        values = builder.ToImmutable();
        return true;
    }

    private static bool TryReadVector(KVObject? value, out TransformVector3? result)
    {
        result = null;
        if (value is null || !value.IsArray || value.Count != 3
            || !TryReadFiniteFloat(value[0], out var x)
            || !TryReadFiniteFloat(value[1], out var y)
            || !TryReadFiniteFloat(value[2], out var z))
        {
            return false;
        }

        result = new TransformVector3 { X = x, Y = y, Z = z };
        return true;
    }

    private static bool TryReadFiniteFloat(KVObject parent, string key, out float result)
    {
        if (parent.TryGetValue(key, out var value))
        {
            return TryReadFiniteFloat(value, out result);
        }

        result = 0f;
        return false;
    }

    private static bool TryReadFiniteFloat(KVObject? value, out float result)
    {
        result = 0f;
        if (value is null || value.IsArray || value.IsCollection)
        {
            return false;
        }

        try
        {
            result = value.ToSingle(CultureInfo.InvariantCulture);
            return float.IsFinite(result);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static string ReadString(KVObject parent, string key) =>
        parent.TryGetValue(key, out var value) && value is not null && value.ValueType == KVValueType.String
            ? value.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

    private static bool TryReadModelBoneNames(KVObject skeleton, out ImmutableArray<string> names) =>
        ReadModelBoneNames(skeleton, out names) == "verified";

    private static string ReadModelBoneNames(KVObject skeleton, out ImmutableArray<string> names)
    {
        names = [];
        if (!skeleton.TryGetValue("m_boneName", out var rawNames))
        {
            return "absent";
        }

        if (rawNames is null || !rawNames.IsArray || rawNames.Count == 0)
        {
            return "unsupported";
        }

        var builder = ImmutableArray.CreateBuilder<string>(rawNames.Count);
        for (var index = 0; index < rawNames.Count; index++)
        {
            var name = rawNames[index];
            if (name is null || name.ValueType != KVValueType.String || string.IsNullOrWhiteSpace(name.ToString(CultureInfo.InvariantCulture)))
            {
                return "unsupported";
            }

            builder.Add(name.ToString(CultureInfo.InvariantCulture));
        }

        names = builder.ToImmutable();
        return "verified";
    }

    private static string ReadRenderBoneNames(KVObject skeleton, out ImmutableArray<string> names)
    {
        names = [];
        if (!skeleton.TryGetValue("m_bones", out var rawBones))
        {
            return "absent";
        }

        if (rawBones is null || !rawBones.IsArray || rawBones.Count == 0)
        {
            return "unsupported";
        }

        var builder = ImmutableArray.CreateBuilder<string>(rawBones.Count);
        for (var index = 0; index < rawBones.Count; index++)
        {
            var bone = rawBones[index];
            if (bone is null || !bone.IsCollection
                || !bone.TryGetValue("m_boneName", out var rawName)
                || rawName is null || rawName.ValueType != KVValueType.String
                || string.IsNullOrWhiteSpace(rawName.ToString(CultureInfo.InvariantCulture)))
            {
                return "unsupported";
            }

            builder.Add(rawName.ToString(CultureInfo.InvariantCulture));
        }

        names = builder.ToImmutable();
        return "verified";
    }

    private static bool IsOrdered(TransformVector3 minimum, TransformVector3 maximum) =>
        minimum.X <= maximum.X && minimum.Y <= maximum.Y && minimum.Z <= maximum.Z;

    private static bool HasStrictlyPositiveExtent(TransformVector3 halfExtents) =>
        halfExtents.X > 0f && halfExtents.Y > 0f && halfExtents.Z > 0f;

    private static bool HasStrictlyPositiveExtent(TransformVector3 minimum, TransformVector3 maximum) =>
        maximum.X > minimum.X && maximum.Y > minimum.Y && maximum.Z > minimum.Z;

    private static ImmutableArray<int> ExpandLods(ulong mask) =>
        Enumerable.Range(0, 64).Where(lod => (mask & (1UL << lod)) != 0).ToImmutableArray();

    private static ContentHash HashFloatSequence(IEnumerable<float> values)
    {
        var bytes = values.SelectMany(value => BitConverter.GetBytes(BitConverter.SingleToInt32Bits(value))).ToArray();
        return ContentHash.Compute(bytes);
    }

    private static ContentHash HashText(string text) => ContentHash.Compute(Encoding.UTF8.GetBytes(text));
}
