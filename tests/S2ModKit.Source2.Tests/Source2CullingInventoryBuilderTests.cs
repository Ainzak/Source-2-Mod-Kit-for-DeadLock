using System.Text.Json;
using S2ModKit.Application;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;

namespace S2ModKit.Source2.Tests;

public sealed partial class Source2TransformMetadataAnalyzerTests
{
    [Fact]
    public void ReadOnlyInventoryUsesRealModelSkeletonNamesAllBuffersAndKeepsRootFrameUnresolved()
    {
        var meshData = MultiBufferData();
        meshData["m_sceneObjects"][0]["m_drawCalls"] = Array(Object(), Object());
        meshData["m_sceneObjects"][0]["m_vMinBounds"] = Array(0f, -1f, -1f);
        meshData["m_sceneObjects"][0]["m_vMaxBounds"] = Array(3f, 1f, 1f);
        meshData["m_skeleton"]["m_bones"][0]["m_bbox"]["m_vecSize"] = Array(0.5f, 0.5f, 0.5f);
        meshData["m_skeleton"]["m_bones"][1]["m_bbox"]["m_vecSize"] = Array(0.5f, 0.5f, 0.5f);
        meshData["m_skeleton"]["m_bones"][0]["m_invBindPose"] = Array(
            1f, 0f, 0f, -4f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f);
        meshData["m_skeleton"]["m_bones"][1]["m_invBindPose"] = Array(
            1f, 0f, 0f, -8f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f);
        var modelData = Object(("m_modelSkeleton", Object(
            ("m_boneName", Array("model-bone-zero", "model-bone-one")),
            ("m_bones", Array(Object(("m_boneName", "render-name-decoy")))),
            ("m_boneSphere", Array(9f, 10f)))));
        var geometry = MultiBufferGeometry();
        var diagnostic = Source2TransformMetadataAnalyzer.DiagnoseBounds(
            0,
            6,
            12,
            3,
            MultiBufferDescriptor(),
            meshData,
            modelData,
            geometry,
            [1, 0, 1],
            "test remap [1,0]",
            "inventory fixture");
        meshData["m_skeleton"]["m_bones"][1]["m_bbox"]["m_vecSize"] = Array(0f, 0.5f, 0.5f);
        var bytes = new byte[] { 1, 2, 3, 4 };
        var artifact = new ArtifactContent("models/test.vmdl_c", ContentHash.Compute(bytes), bytes);
        var inventory = Source2CullingInventoryBuilder.Build(
            artifact,
            0,
            modelData,
            [0, 1],
            [new Source2CullingInventoryMeshInput(6, 12, 3, meshData, geometry, diagnostic)]);

        var remap = Assert.Single(inventory.BoneRemaps);
        Assert.Equal("verified", remap.BoneRemapStatus);
        Assert.Equal([1, 0, 1], remap.BoneRemapValues);
        Assert.Equal(["lantern", "cloak"], remap.RenderBoneNames);
        Assert.Equal(["model-bone-zero", "model-bone-one"], remap.ModelBoneNames);

        var scene = Assert.Single(inventory.Fields, field => field.Identity.FieldPath == "m_sceneObjects[0].m_vMinBounds+m_vMaxBounds");
        Assert.Equal("verified", scene.Status);
        Assert.Equal("source2_model", scene.CoordinateSpace!.SpaceId);
        Assert.Equal([0, 1], scene.Identity.CoveredLods);
        Assert.Equal(2, scene.Contributors!.Sources!.Count);
        Assert.Equal(4, scene.Contributors.ContributorCount);

        var renderBoneBox = Assert.Single(inventory.Fields, field => field.Identity.FieldPath == "m_skeleton.m_bones[0].m_bbox.m_vecCenter+m_vecSize");
        Assert.Equal("verified", renderBoneBox.Status);
        Assert.Equal("source2_render_bone_bind_local", renderBoneBox.CoordinateSpace!.SpaceId);
        Assert.Equal(-4f, renderBoneBox.CoordinateSpace.Matrix3x4![3]);
        Assert.Equal("aabb_center_half_extents", renderBoneBox.RawOriginalValue!.Kind);
        Assert.Equal(0.5f, renderBoneBox.RawOriginalValue.HalfExtents!.X);
        Assert.Equal(2, renderBoneBox.Contributors!.Sources!.Count);

        var renderBoneSphere = Assert.Single(inventory.Fields, field => field.Identity.FieldPath == "m_skeleton.m_bones[0].m_flSphereRadius");
        Assert.Equal("unsupported", renderBoneSphere.Status);
        Assert.Equal(1f, renderBoneSphere.RawOriginalValue!.Radius);
        Assert.Equal("RENDER_BONE_SPHERE_SPACE_NOT_VERIFIED", renderBoneSphere.CoordinateSpace!.ReasonCode);
        Assert.Equal("verified", renderBoneSphere.Contributors!.Status);

        var degenerateRenderBoneBox = Assert.Single(inventory.Fields, field => field.Identity.FieldPath == "m_skeleton.m_bones[1].m_bbox.m_vecCenter+m_vecSize");
        Assert.Equal("unsupported", degenerateRenderBoneBox.Status);
        Assert.Equal(0f, degenerateRenderBoneBox.RawOriginalValue!.HalfExtents!.X);
        Assert.Equal("verified", degenerateRenderBoneBox.CoordinateSpace!.Status);
        Assert.Equal("verified", degenerateRenderBoneBox.Contributors!.Status);

        var rootSphere = Assert.Single(inventory.Fields, field => field.Identity.FieldPath == "m_modelSkeleton.m_boneSphere[0]");
        Assert.Equal("unsupported", rootSphere.Status);
        Assert.Equal(9f, rootSphere.RawOriginalValue!.Radius);
        Assert.Equal("MODEL_BONE_SPHERE_SPACE_NOT_VERIFIED", rootSphere.CoordinateSpace!.ReasonCode);
        Assert.Null(rootSphere.CoordinateSpace.MatrixIdentity);
        Assert.Equal("verified", rootSphere.Contributors!.Status);
        Assert.Equal(2, rootSphere.Contributors.ContributorCount);
        Assert.Equal("model-bone-zero", Assert.Single(diagnostic.RootBoneContributors, item => item.ModelBoneIndex == 0).ModelBoneName);
        var rootSources = rootSphere.Contributors.Sources!;
        Assert.Equal(0, Assert.Single(rootSources, source => source.VertexBufferOrdinal == 0).ContributorCount);
        Assert.Equal(2, Assert.Single(rootSources, source => source.VertexBufferOrdinal == 1).ContributorCount);
        Assert.Equal(0, Assert.Single(rootSources, source => source.VertexBufferOrdinal == 1).ResolvedModelBoneIndex);
        Assert.Equal("partial", inventory.Status);
    }

    [Fact]
    public void ReadOnlyInventoryKeepsMissingRootRemapUnsupportedInsteadOfUsingRenderIndices()
    {
        var meshData = MultiBufferData();
        meshData["m_sceneObjects"][0]["m_drawCalls"] = Array(Object(), Object());
        var modelData = Object(("m_modelSkeleton", Object(
            ("m_boneName", Array("model-bone-zero", "model-bone-one")),
            ("m_boneSphere", Array(9f, 10f)))));
        var geometry = MultiBufferGeometry();
        var diagnostic = Source2TransformMetadataAnalyzer.DiagnoseBounds(
            0, 6, 12, 3, MultiBufferDescriptor(), meshData, modelData, geometry,
            null, "VRF returned null", "inventory missing-remap fixture");
        var bytes = new byte[] { 9, 8, 7 };
        var inventory = Source2CullingInventoryBuilder.Build(
            new ArtifactContent("models/test.vmdl_c", ContentHash.Compute(bytes), bytes),
            0,
            modelData,
            [0, 1],
            [new Source2CullingInventoryMeshInput(6, 12, 3, meshData, geometry, diagnostic)]);

        var remap = Assert.Single(inventory.BoneRemaps);
        Assert.Equal("absent", remap.BoneRemapStatus);
        Assert.Null(remap.BoneRemapValues);
        Assert.Null(remap.BoneRemapIdentity);
        Assert.Equal("verified", remap.ModelBoneNamesStatus);

        var rootSphere = Assert.Single(inventory.Fields, field => field.Identity.FieldPath == "m_modelSkeleton.m_boneSphere[0]");
        Assert.Equal("unsupported", rootSphere.Contributors!.Status);
        Assert.Equal("ROOT_BONE_REMAP_OR_NAME_EVIDENCE_INCOMPLETE", rootSphere.Contributors.ReasonCode);
        Assert.Null(rootSphere.Contributors.IdentityHash);
        Assert.Null(rootSphere.CoordinateSpace!.MatrixIdentity);
    }

    [Fact]
    public void ReadOnlyInventoryDoesNotMapScenesAfterMalformedSceneBounds()
    {
        var meshData = MultiBufferData();
        meshData["m_sceneObjects"] = Array(
            Object(
                ("m_vMinBounds", Array(0f, 0f)),
                ("m_vMaxBounds", Array(1f, 1f, 1f)),
                ("m_drawCalls", Array(Object()))),
            Object(
                ("m_vMinBounds", Array(-1f, -1f, -1f)),
                ("m_vMaxBounds", Array(1f, 1f, 1f)),
                ("m_drawCalls", Array(Object()))));
        var modelData = Object(("m_modelSkeleton", Object(
            ("m_boneName", Array("model-bone-zero", "model-bone-one")),
            ("m_boneSphere", Array(9f, 10f)))));
        var geometry = MultiBufferGeometry();
        var diagnostic = Source2TransformMetadataAnalyzer.DiagnoseBounds(
            0,
            6,
            12,
            3,
            MultiBufferDescriptor(),
            meshData,
            modelData,
            geometry,
            [1, 0, 1],
            "test remap [1,0]",
            "malformed first scene bounds fixture");
        var bytes = new byte[] { 4, 3, 2, 1 };
        var inventory = Source2CullingInventoryBuilder.Build(
            new ArtifactContent("models/test.vmdl_c", ContentHash.Compute(bytes), bytes),
            0,
            modelData,
            [0, 1],
            [new Source2CullingInventoryMeshInput(6, 12, 3, meshData, geometry, diagnostic)]);

        var firstScene = Assert.Single(inventory.Fields, field => field.Identity.FieldPath == "m_sceneObjects[0].m_vMinBounds+m_vMaxBounds");
        var secondScene = Assert.Single(inventory.Fields, field => field.Identity.FieldPath == "m_sceneObjects[1].m_vMinBounds+m_vMaxBounds");
        Assert.Equal("unsupported", firstScene.Status);
        Assert.Equal("unsupported", secondScene.Status);
        Assert.Equal("SCENE_DRAW_CALL_MAPPING_INVALIDATED", secondScene.Contributors!.ReasonCode);
        Assert.Null(secondScene.Contributors.Sources);
    }

    [Fact]
    public void ReadOnlyInventoryDoesNotMapScenesAfterPartialVertexMembershipFailure()
    {
        var meshData = MultiBufferData();
        meshData["m_sceneObjects"][0]["m_drawCalls"] = Array(Object(), Object());
        var modelData = Object(("m_modelSkeleton", Object(
            ("m_boneName", Array("model-bone-zero", "model-bone-one")),
            ("m_boneSphere", Array(9f, 10f)))));
        var geometry = MultiBufferGeometry();
        var diagnostic = Source2TransformMetadataAnalyzer.DiagnoseBounds(
            0,
            6,
            12,
            3,
            MultiBufferDescriptor(),
            meshData,
            modelData,
            geometry,
            [1, 0, 1],
            "test remap [1,0]",
            "partial scene draw-call collection fixture");

        meshData["m_sceneObjects"] = Array(
            Object(
                ("m_vMinBounds", Array(-1f, -1f, -1f)),
                ("m_vMaxBounds", Array(1f, 1f, 1f)),
                ("m_drawCalls", Array(Object()))),
            Object(
                ("m_vMinBounds", Array(-1f, -1f, -1f)),
                ("m_vMaxBounds", Array(1f, 1f, 1f)),
                ("m_drawCalls", Array(Object()))));
        var corruptedFirstDrawCall = geometry.DrawCalls[0] with { VertexIndices = [int.MaxValue] };
        var malformedGeometry = geometry with
        {
            DrawCalls = [corruptedFirstDrawCall, geometry.DrawCalls[1]],
        };
        var bytes = new byte[] { 5, 6, 7, 8 };
        var inventory = Source2CullingInventoryBuilder.Build(
            new ArtifactContent("models/test.vmdl_c", ContentHash.Compute(bytes), bytes),
            0,
            modelData,
            [0, 1],
            [new Source2CullingInventoryMeshInput(6, 12, 3, meshData, malformedGeometry, diagnostic)]);

        var firstScene = Assert.Single(inventory.Fields, field => field.Identity.FieldPath == "m_sceneObjects[0].m_vMinBounds+m_vMaxBounds");
        var secondScene = Assert.Single(inventory.Fields, field => field.Identity.FieldPath == "m_sceneObjects[1].m_vMinBounds+m_vMaxBounds");
        Assert.Equal("unsupported", firstScene.Status);
        Assert.Equal("unsupported", secondScene.Status);
        Assert.Equal("SCENE_DRAW_CALL_MAPPING_INVALIDATED", secondScene.Contributors!.ReasonCode);
        Assert.Null(secondScene.Contributors.Sources);
    }

    [Fact]
    public void ReadOnlyInventoryAttachesRootSphereAggregationDiagnosticExtension()
    {
        var meshData = MeshDataWithBoneBounds(
            Bounds((0f, 0f, 0f), (3f, 0f, 0f)),
            ("model-bone-one", "", (0.5f, 0f, 0f), (0.5f, 0f, 0f), 1f),
            ("model-bone-zero", "", (2.5f, 0f, 0f), (0.5f, 0f, 0f), 3f));
        var modelData = Object(("m_modelSkeleton", Object(
            ("m_boneName", Array("model-bone-zero", "model-bone-one")),
            ("m_boneSphere", Array(9f, 10f)))));
        var geometry = MultiBufferGeometry();
        var diagnostic = Source2TransformMetadataAnalyzer.DiagnoseBounds(
            0,
            6,
            12,
            3,
            MultiBufferDescriptor(),
            meshData,
            modelData,
            geometry,
            [1, 0, 1],
            "test remap [1,0,1]",
            "root aggregation fixture");
        var bytes = new byte[] { 3, 1, 4, 1 };
        var inventory = Source2CullingInventoryBuilder.Build(
            new ArtifactContent("models/test.vmdl_c", ContentHash.Compute(bytes), bytes),
            0,
            modelData,
            [0, 1],
            [new Source2CullingInventoryMeshInput(6, 12, 3, meshData, geometry, diagnostic)]);

        Assert.True(inventory.Extensions.ContainsKey("rootSphereAggregationV1"));
        var extension = inventory.Extensions["rootSphereAggregationV1"];
        Assert.Equal(1, extension.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("mapped_render_radius_max", extension.GetProperty("relation").GetString());
        Assert.Equal("offline_static", extension.GetProperty("evidenceScope").GetString());
        Assert.Equal("complete", extension.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, extension.GetProperty("reasonCode").ValueKind);
        var rows = extension.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());

        Assert.Equal("m_modelSkeleton.m_boneSphere[0]", rows[0].GetProperty("rootField").GetProperty("fieldPath").GetString());
        Assert.Equal("model-bone-zero", rows[0].GetProperty("rootBoneName").GetString());
        Assert.Equal(9f, rows[0].GetProperty("storedRadius").GetSingle());
        Assert.Equal(3f, rows[0].GetProperty("mappedMaximum").GetSingle());
        Assert.Equal("mismatch", rows[0].GetProperty("status").GetString());
        var source = Assert.Single(rows[0].GetProperty("sources").EnumerateArray());
        Assert.Equal("m_skeleton.m_bones[1].m_flSphereRadius", source.GetProperty("fieldPath").GetString());
        Assert.Equal(6, source.GetProperty("meshOrdinal").GetInt32());

        Assert.Equal("model-bone-one", rows[1].GetProperty("rootBoneName").GetString());
        Assert.Equal(10f, rows[1].GetProperty("storedRadius").GetSingle());
        Assert.Equal(1f, rows[1].GetProperty("mappedMaximum").GetSingle());
        Assert.Equal("mismatch", rows[1].GetProperty("status").GetString());

        Assert.Equal(
            "unsupported",
            Assert.Single(inventory.Fields, field => field.Identity.FieldPath == "m_modelSkeleton.m_boneSphere[0]").Status);
        Assert.Equal(
            "unsupported",
            Assert.Single(inventory.Fields, field => field.Identity.FieldPath == "m_skeleton.m_bones[0].m_flSphereRadius").Status);
    }

    [Fact]
    public void ReadOnlyInventoryRejectsFlatSceneBoundsButConsumesTheirKnownDrawCalls()
    {
        var meshData = MultiBufferData();
        meshData["m_sceneObjects"][0]["m_drawCalls"] = Array(Object(), Object());
        var modelData = Object(("m_modelSkeleton", Object(
            ("m_boneName", Array("model-bone-zero", "model-bone-one")),
            ("m_boneSphere", Array(9f, 10f)))));
        var geometry = MultiBufferGeometry();
        var diagnostic = Source2TransformMetadataAnalyzer.DiagnoseBounds(
            0,
            6,
            12,
            3,
            MultiBufferDescriptor(),
            meshData,
            modelData,
            geometry,
            [1, 0, 1],
            "test remap [1,0]",
            "flat scene bounds fixture");

        meshData["m_sceneObjects"] = Array(
            Object(
                ("m_vMinBounds", Array(0f, 0f, 0f)),
                ("m_vMaxBounds", Array(1f, 0f, 1f)),
                ("m_drawCalls", Array(Object()))),
            Object(
                ("m_vMinBounds", Array(-1f, -1f, -1f)),
                ("m_vMaxBounds", Array(1f, 1f, 1f)),
                ("m_drawCalls", Array(Object()))));
        var bytes = new byte[] { 2, 4, 6, 8 };
        var inventory = Source2CullingInventoryBuilder.Build(
            new ArtifactContent("models/test.vmdl_c", ContentHash.Compute(bytes), bytes),
            0,
            modelData,
            [0, 1],
            [new Source2CullingInventoryMeshInput(6, 12, 3, meshData, geometry, diagnostic)]);

        var flatScene = Assert.Single(inventory.Fields, field => field.Identity.FieldPath == "m_sceneObjects[0].m_vMinBounds+m_vMaxBounds");
        var nextScene = Assert.Single(inventory.Fields, field => field.Identity.FieldPath == "m_sceneObjects[1].m_vMinBounds+m_vMaxBounds");
        Assert.Equal("unsupported", flatScene.Status);
        Assert.Equal("aabb_min_max", flatScene.RawOriginalValue!.Kind);
        Assert.Equal("verified", flatScene.Contributors!.Status);
        Assert.Equal("verified", nextScene.Status);
        Assert.Equal(2, Assert.Single(nextScene.Contributors!.Sources!, source => source.VertexBufferOrdinal == 1).ContributorCount);
    }
}
