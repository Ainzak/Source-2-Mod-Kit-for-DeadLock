using System.Buffers.Binary;
using System.Collections.Immutable;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;
using ValveResourceFormat.Utils;

namespace S2ModKit.Source2.Tests;

public sealed partial class Source2TransformMetadataAnalyzerTests
{
    private static readonly ContentHash EmptyHash = ContentHash.Compute(ReadOnlySpan<byte>.Empty);

    [Fact]
    public void AffineWholeMeshAcceptsWideSignedBlendIndicesWithoutChangingLegacyProfile()
    {
        var descriptor = WideBlendIndexVertexDescriptor();
        var meshData = MeshDataWithBoneBounds(
            Bounds((0f, 0f, 0f), (2f, 0f, 0f)),
            ("root", "", (1f, 0f, 0f), (1f, 0f, 0f), 2f));
        var geometry = WideBlendIndexGeometry(0);

        var result = Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            descriptor, meshData, geometry, "wide affine mesh", boneSizeIsHalfExtent: true,
            allowWideBlendIndices: true);

        Assert.Equal(3, result.VertexCount);
        Assert.Equal(["root"], result.LocalInfluencingBones);
        Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            descriptor, meshData, geometry, "legacy mesh"));
    }

    [Fact]
    public void BoundsDiagnosticReportsRemappedRawInfluenceAndExactFailingExtremalVertex()
    {
        var descriptor = WideBlendIndexVertexDescriptor();
        var meshData = MeshDataWithBoneBounds(
            Bounds((0f, 0f, 0f), (2f, 0f, 0f)),
            ("root", "", (0f, 0f, 0f), (1f, 0f, 0f), 1f),
            ("weapon", "root", (0f, 0f, 0f), (1f, 0f, 0f), 1f));
        var modelData = Object(("m_modelSkeleton", Object(("m_boneName", Array(
            "model-root", "model-weapon")))));

        var diagnostic = Source2TransformMetadataAnalyzer.DiagnoseBounds(
            0,
            7,
            12,
            1,
            descriptor,
            meshData,
            modelData,
            WideBlendIndexGeometry(0),
            [1, 0, 1],
            "synthetic remap table",
            "synthetic remapped bounds");

        Assert.Equal("analyzed", diagnostic.Status);
        Assert.Equal("synthetic remap table", diagnostic.BoneRemapSource);
        Assert.Equal("verified", diagnostic.BoneRemapStatus);
        Assert.Equal("verified", diagnostic.ModelBoneNamesStatus);
        Assert.Equal([1, 0, 1], diagnostic.BoneRemap);
        Assert.NotNull(diagnostic.BoneRemapIdentity);
        var bone = Assert.Single(diagnostic.Bones);
        Assert.Equal("root", bone.BoneName);
        Assert.Equal(1, bone.ResolvedModelBoneIndex);
        Assert.Equal("model-weapon", bone.ResolvedModelBoneName);
        Assert.Equal(2f, bone.CalculatedRequiredSphereRadius);
        Assert.Equal(1f, bone.SphereDelta);
        Assert.False(bone.SphereMatchesExistingAffineTolerance);
        var maxX = Assert.Single(bone.Extrema.Where(item => item.Axis == "x" && item.Side == "max"));
        Assert.Equal(2f, maxX.CalculatedValue);
        Assert.Equal(1f, maxX.StoredValue);
        Assert.False(maxX.StoredContainsExtremum);
        Assert.Equal(2, maxX.Vertex.VertexOrdinal);
        Assert.Equal(0, maxX.Vertex.VertexBufferOrdinal);
        Assert.Equal(14u, maxX.Vertex.BlendIndexFormat);
        Assert.Equal([0, 0, 0, 0], maxX.Vertex.RawBlendIndices);
        Assert.Equal([255, 0, 0, 0], maxX.Vertex.RawBlendWeights);
        Assert.Equal(1, maxX.Vertex.ResolvedBoneIndex);
        Assert.Equal("model-weapon", maxX.Vertex.BoneName);
        Assert.Equal(0, maxX.Vertex.MeshBoneIndex);
        Assert.Equal("root", maxX.Vertex.MeshBoneName);
        Assert.Equal(2f, maxX.Vertex.OriginalPosition.X);
        Assert.Equal(2f, maxX.Vertex.CalculatedBoneLocalPosition.X);
        Assert.Equal(3, Assert.Single(bone.ContributorBuffers).ContributorCount);
        Assert.NotEqual(EmptyHash, bone.ContributorSetIdentity);
        Assert.Equal(1, Assert.Single(diagnostic.Buffers).InfluenceCounts[0].ActiveInfluenceCount);
    }

    [Fact]
    public void BoundsDiagnosticRejectsMalformedBoneRemapsAndMissingActiveIndices()
    {
        var descriptor = WideBlendIndexVertexDescriptor();
        var meshData = MeshDataWithBoneBounds(
            Bounds((0f, 0f, 0f), (2f, 0f, 0f)),
            ("root", "", (1f, 0f, 0f), (1f, 0f, 0f), 2f));
        var modelData = Object(("m_modelSkeleton", Object(("m_boneName", Array("model-root")))));

        var negativeTarget = Source2TransformMetadataAnalyzer.DiagnoseBounds(
            0, 0, 3, 1, descriptor, meshData, modelData, WideBlendIndexGeometry(0),
            [-1], "malformed remap", "negative remap value");
        var missingActiveIndex = Source2TransformMetadataAnalyzer.DiagnoseBounds(
            0, 0, 3, 1, descriptor, meshData, modelData, WideBlendIndexGeometry(0),
            [], "truncated remap", "missing active remap index");

        Assert.Equal("unsupported", negativeTarget.Status);
        Assert.Contains("malformed mesh-to-model bone remap", negativeTarget.Failure, StringComparison.Ordinal);
        Assert.Equal("unsupported", missingActiveIndex.Status);
        Assert.Equal("unsupported", missingActiveIndex.BoneRemapStatus);
    }

    [Fact]
    public void BoundsDiagnosticDoesNotInventModelNamesWhenSkeletonNamesAreMissing()
    {
        var diagnostic = Source2TransformMetadataAnalyzer.DiagnoseBounds(
            0, 0, 3, 1, WideBlendIndexVertexDescriptor(),
            MeshDataWithBoneBounds(
                Bounds((0f, 0f, 0f), (2f, 0f, 0f)),
                ("root", "", (1f, 0f, 0f), (1f, 0f, 0f), 2f)),
            Object(), WideBlendIndexGeometry(0), [0], "synthetic remap", "missing model names");

        Assert.Equal("analyzed", diagnostic.Status);
        Assert.Equal("absent", diagnostic.ModelBoneNamesStatus);
        var bone = Assert.Single(diagnostic.Bones);
        Assert.Equal("root", bone.BoneName);
        Assert.Equal(0, bone.ResolvedModelBoneIndex);
        Assert.Null(bone.ResolvedModelBoneName);
        Assert.Null(bone.SphereExtremalVertex!.BoneName);
    }

    [Fact]
    public void BoundsDiagnosticDoesNotInventIdentityWhenBoneRemapIsAbsent()
    {
        var diagnostic = Source2TransformMetadataAnalyzer.DiagnoseBounds(
            0, 0, 3, 1, WideBlendIndexVertexDescriptor(),
            MeshDataWithBoneBounds(
                Bounds((0f, 0f, 0f), (2f, 0f, 0f)),
                ("render-only-name", "", (1f, 0f, 0f), (1f, 0f, 0f), 2f)),
            Object(("m_modelSkeleton", Object(("m_boneName", Array("model-root"))))),
            WideBlendIndexGeometry(0), null, "null remap table", "absent remap");

        Assert.Equal("analyzed", diagnostic.Status);
        Assert.Equal("absent", diagnostic.BoneRemapStatus);
        Assert.Null(diagnostic.BoneRemapIdentity);
        var bone = Assert.Single(diagnostic.Bones);
        Assert.Null(bone.ResolvedModelBoneIndex);
        Assert.Null(bone.ResolvedModelBoneName);
        Assert.Null(bone.SphereExtremalVertex!.ResolvedBoneIndex);
    }

    [Fact]
    public void BoundsDiagnosticRejectsMalformedModelSkeletonNamesInsteadOfUsingRenderNames()
    {
        var diagnostic = Source2TransformMetadataAnalyzer.DiagnoseBounds(
            0, 0, 3, 1, WideBlendIndexVertexDescriptor(),
            MeshDataWithBoneBounds(
                Bounds((0f, 0f, 0f), (2f, 0f, 0f)),
                ("render-only-name", "", (1f, 0f, 0f), (1f, 0f, 0f), 2f)),
            Object(("m_modelSkeleton", Object(("m_boneName", "malformed-not-an-array")))),
            WideBlendIndexGeometry(0), [0], "synthetic remap table", "malformed model names");

        Assert.Equal("unsupported", diagnostic.Status);
        Assert.Equal("unsupported", diagnostic.ModelBoneNamesStatus);
        Assert.Contains("m_modelSkeleton.m_boneName", diagnostic.Failure, StringComparison.Ordinal);
        Assert.Empty(diagnostic.Bones);
    }

    [Fact]
    public void BoundsDiagnosticReadsAllEightUnsignedShortBlendIndices()
    {
        const int stride = 44;
        var descriptor = Object(("m_vertexBuffers", Array(Object(
            ("m_inputLayoutFields", Array(
                Layout("POSITION", 6u, 0),
                Layout("BLENDINDICES", 4u, 20),
                Layout("BLENDWEIGHT", 11u, 36)))))));
        var meshData = WithWeightCount(
            MeshDataWithBoneBounds(
                Bounds((0f, 0f, 0f), (2f, 0f, 0f)),
                ("root", "", (1f, 0f, 0f), (1f, 0f, 0f), 2f),
                ("weapon", "root", (1f, 0f, 0f), (1f, 0f, 0f), 2f)),
            8);
        var bytes = new byte[stride * 3];
        for (var vertex = 0; vertex < 3; vertex++)
        {
            var offset = vertex * stride;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), BitConverter.SingleToInt32Bits(vertex));
            for (var slot = 0; slot < 8; slot++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 20 + (slot * sizeof(ushort))),
                    slot == 7 ? (ushort)1 : (ushort)0);
            }

            bytes[offset + 36] = 254;
            bytes[offset + 43] = 1;
        }

        var modelData = Object(("m_modelSkeleton", Object(("m_boneName", Array(
            "model-root", "model-weapon")))));
        var diagnostic = Source2TransformMetadataAnalyzer.DiagnoseBounds(
            0,
            0,
            3,
            1,
            descriptor,
            meshData,
            modelData,
            GeometrySource(bytes, stride, [[0, 1, 2]]),
            [0, 1],
            "synthetic eight-slot layout",
            "synthetic eight-slot bounds");

        Assert.Equal("analyzed", diagnostic.Status);
        var buffer = Assert.Single(diagnostic.Buffers);
        Assert.Equal(4u, buffer.BlendIndexFormat);
        Assert.Equal(2, buffer.InfluenceCounts.Single().ActiveInfluenceCount);
        Assert.Equal(3, buffer.InfluenceCounts.Single().VertexCount);
        var weapon = Assert.Single(diagnostic.Bones.Where(bone => bone.BoneName == "weapon"));
        Assert.Equal(3, weapon.InfluencedVertexCount);
        var witness = Assert.Single(weapon.Extrema.Where(item => item.Axis == "x" && item.Side == "max")).Vertex;
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 1], witness.RawBlendIndices);
        Assert.Equal([254, 0, 0, 0, 0, 0, 0, 1], witness.RawBlendWeights);
        Assert.Equal(2, witness.ActiveInfluenceCount);
    }

    [Fact]
    public void AffineWholeMeshRejectsNegativeActiveWideBlendIndex()
    {
        var error = Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            WideBlendIndexVertexDescriptor(),
            MeshDataWithBoneBounds(
                Bounds((0f, 0f, 0f), (2f, 0f, 0f)),
                ("root", "", (1f, 0f, 0f), (1f, 0f, 0f), 2f)),
            WideBlendIndexGeometry(-1), "bad wide mesh", boneSizeIsHalfExtent: true,
            allowWideBlendIndices: true));

        Assert.Contains("out-of-range bone -1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AffineWholeMeshRejectsOverlappingWideBlendIndexAndWeightFields()
    {
        var error = Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            WideBlendIndexVertexDescriptor(indexOffset: 24),
            MeshDataWithBoneBounds(
                Bounds((0f, 0f, 0f), (2f, 0f, 0f)),
                ("root", "", (1f, 0f, 0f), (1f, 0f, 0f), 2f)),
            WideBlendIndexGeometry(0), "overlap mesh", boneSizeIsHalfExtent: true,
            allowWideBlendIndices: true));

        Assert.Contains("overlap", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AffineWholeMeshAcceptsSeveralSkeletonRootsWithIndependentBoneBounds()
    {
        var meshData = MeshDataWithBoneBounds(
            Bounds((0f, 0f, 0f), (2f, 0f, 0f)),
            ("first", "", (0.5f, 0f, 0f), (0.5f, 0f, 0f), 1f),
            ("second", "", (2f, 0f, 0f), (0f, 0f, 0f), 2f));
        var geometry = WideBlendIndexGeometry(0, 1);

        var result = Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            WideBlendIndexVertexDescriptor(), meshData, geometry, "two-root affine mesh",
            boneSizeIsHalfExtent: true, allowWideBlendIndices: true, requireCommonSkinningRoot: false);

        Assert.Equal(string.Empty, result.LocalSkinningRootBone);
        Assert.Equal(["first", "second"], result.LocalInfluencingBones);
        Assert.Equal(2, result.BoneBounds.Length);
        Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            WideBlendIndexVertexDescriptor(), meshData, geometry, "old root requirement",
            boneSizeIsHalfExtent: true, allowWideBlendIndices: true));
    }

    [Fact]
    public void AnalyzeWholeMeshAcceptsRigidSingleInfluenceMesh()
    {
        var analysis = Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            RigidVertexDescriptor(),
            WithWeightCount(
                MeshDataWithBoneBounds(
                    Bounds((0f, 0f, 0f), (2f, 1f, 1f)),
                    ("root_motion", "", (0f, 0f, 0f), (0f, 0f, 0f), 0f),
                    ("weapon", "root_motion", (1f, 1f / 3f, 1f / 3f), (2f, 1f, 1f), MathF.Sqrt(5f))),
                1),
            RigidGeometry(
                ((0f, 0f, 0f), [1, 0, 0, 0]),
                ((1f, 1f, 0f), [1, 7, 3, 0]),
                ((2f, 0f, 1f), [1, 0, 0, 0])),
            "rigid mesh");

        Assert.Equal(3, analysis.VertexCount);
        Assert.Equal("weapon", analysis.LocalSkinningRootBone);
        Assert.Equal(["weapon"], analysis.LocalInfluencingBones);
        var bounds = Assert.Single(analysis.BoneBounds);
        Assert.Equal("weapon", bounds.BoneName);
        Assert.Equal([0, 1, 2], bounds.InfluencedVertices);
    }

    [Fact]
    public void AffineWholeMeshReadsBoneBoxSizeAsHalfExtentWithoutChangingLegacyReader()
    {
        var meshData = WithWeightCount(
            MeshDataWithBoneBounds(
                Bounds((0f, 0f, 0f), (2f, 0f, 0f)),
                ("weapon", "", (1f, 0f, 0f), (1f, 0f, 0f), 2f)),
            1);
        var geometry = RigidGeometry(
            ((0f, 0f, 0f), [0, 0, 0, 0]),
            ((1f, 0f, 0f), [0, 0, 0, 0]),
            ((2f, 0f, 0f), [0, 0, 0, 0]));

        var affine = Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            RigidVertexDescriptor(), meshData, geometry, "affine mesh", boneSizeIsHalfExtent: true);
        var legacy = Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            RigidVertexDescriptor(), meshData, geometry, "legacy mesh");

        Assert.Equal(0f, Assert.Single(affine.BoneBounds).LocalBounds.Min.X);
        Assert.Equal(2f, Assert.Single(affine.BoneBounds).LocalBounds.Max.X);
        Assert.Equal(0.5f, Assert.Single(legacy.BoneBounds).LocalBounds.Min.X);
        Assert.Equal(1.5f, Assert.Single(legacy.BoneBounds).LocalBounds.Max.X);
    }

    [Fact]
    public void AnalyzeWholeMeshFindsCommonRootForRigidMeshSplitAcrossBones()
    {
        var analysis = Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            RigidVertexDescriptor(),
            WithWeightCount(
                MeshDataWithBoneBounds(
                    Bounds((0f, 0f, 0f), (3f, 0f, 0f)),
                    ("root_motion", "", (0f, 0f, 0f), (0f, 0f, 0f), 0f),
                    ("suitcase1", "root_motion", (0.5f, 0f, 0f), (1f, 0f, 0f), 1f),
                    ("suitcase2", "root_motion", (2.5f, 0f, 0f), (1f, 0f, 0f), 3f)),
                1),
            RigidGeometry(
                ((0f, 0f, 0f), [1, 50, 0, 0]),
                ((1f, 0f, 0f), [1, 50, 0, 0]),
                ((2f, 0f, 0f), [2, 51, 0, 0]),
                ((3f, 0f, 0f), [2, 51, 0, 0])),
            "two-bone rigid mesh");

        Assert.Equal("root_motion", analysis.LocalSkinningRootBone);
        Assert.Equal(["suitcase1", "suitcase2"], analysis.LocalInfluencingBones);
        Assert.Collection(
            analysis.BoneBounds,
            first => Assert.Equal([0, 1], first.InfluencedVertices),
            second => Assert.Equal([2, 3], second.InfluencedVertices));
    }

    [Fact]
    public void AnalyzeWholeMeshRejectsRigidMeshWithOutOfRangeBoneIndex()
    {
        var exception = Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            RigidVertexDescriptor(),
            WithWeightCount(
                MeshData(Bounds((0f, 0f, 0f), (1f, 0f, 0f)), ("weapon", "")),
                1),
            RigidGeometry(
                ((0f, 0f, 0f), [0, 0, 0, 0]),
                ((1f, 0f, 0f), [2, 0, 0, 0])),
            "bad rigid bone"));

        Assert.Contains("out-of-range bone 2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeWholeMeshRejectsRigidMeshWithoutSingleWeightCount()
    {
        var exception = Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            RigidVertexDescriptor(),
            MeshData(Bounds((0f, 0f, 0f), (1f, 0f, 0f)), ("weapon", "")),
            RigidGeometry(
                ((0f, 0f, 0f), [0, 0, 0, 0]),
                ((1f, 0f, 0f), [0, 0, 0, 0])),
            "multi-weight rigid mesh"));

        Assert.Contains("blend weight count 4", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeWholeMeshFindsLocalSkinningRootForCompleteSkinnedMesh()
    {
        var analysis = Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            VertexDescriptor(),
            MeshDataWithBoneBounds(
                Bounds((0f, 0f, 0f), (2f, 1f, 1f)),
                ("root_motion", "", (0f, 0f, 0f), (0f, 0f, 0f), 0f),
                ("weapon", "root_motion", (0.5f, 0.5f, 0f), (1f, 1f, 0f), MathF.Sqrt(2f)),
                ("blade", "weapon", (1f, 1f, 0f), (0f, 0f, 0f), MathF.Sqrt(2f)),
                ("gem", "weapon", (2f, 0f, 1f), (0f, 0f, 0f), MathF.Sqrt(5f))),
            Geometry(
                SkinnedVertices(
                    ((0f, 0f, 0f), [1, 0, 0, 0], [255, 0, 0, 0]),
                    ((1f, 1f, 0f), [1, 2, 0, 0], [128, 127, 0, 0]),
                    ((2f, 0f, 1f), [3, 0, 0, 0], [255, 0, 0, 0])),
                [0, 1],
                [1, 2]),
            "test mesh");

        Assert.Equal(0, analysis.SceneObjectIndex);
        Assert.Equal(3, analysis.VertexCount);
        Assert.Equal(new ContentHash(VertexSetHash.Compute([0, 1, 2])), analysis.VertexSetHash);
        Assert.Equal("weapon", analysis.LocalSkinningRootBone);
        Assert.Equal(StringToken.Get("weapon"), analysis.LocalSkinningRootBoneHash);
        Assert.Equal(["weapon", "blade", "gem"], analysis.LocalInfluencingBones);
        Assert.Equal(3, analysis.BoneBounds.Length);
        Assert.Equal("weapon", analysis.BoneBounds[0].BoneName);
        Assert.Equal([0, 1], analysis.BoneBounds[0].InfluencedVertices);
        Assert.Equal(MathF.Sqrt(2f), analysis.BoneBounds[0].SphereRadius);
        Assert.Equal(0f, analysis.SceneBounds.Min.X);
        Assert.Equal(2f, analysis.SceneBounds.Max.X);
    }

    [Fact]
    public void AnalyzeWholeMeshRejectsIncompleteVertexCoverage()
    {
        var exception = Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            VertexDescriptor(),
            MeshData(Bounds((0f, 0f, 0f), (2f, 0f, 0f)), ("weapon", "")),
            Geometry(
                SkinnedVertices(
                    ((0f, 0f, 0f), [0, 0, 0, 0], [255, 0, 0, 0]),
                    ((1f, 0f, 0f), [0, 0, 0, 0], [255, 0, 0, 0]),
                    ((2f, 0f, 0f), [0, 0, 0, 0], [255, 0, 0, 0])),
                [0, 1]),
            "partial mesh"));

        Assert.Contains("cover 2 of 3 vertices", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeWholeMeshRejectsInvalidBlendWeights()
    {
        var exception = Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            VertexDescriptor(),
            MeshData(Bounds((0f, 0f, 0f), (1f, 0f, 0f)), ("weapon", "")),
            Geometry(
                SkinnedVertices(
                    ((0f, 0f, 0f), [0, 0, 0, 0], [254, 0, 0, 0]),
                    ((1f, 0f, 0f), [0, 0, 0, 0], [255, 0, 0, 0])),
                [0, 1]),
            "bad weights"));

        Assert.Contains("sum to 254; expected 255", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeWholeMeshRejectsOutOfRangeBoneIndex()
    {
        var exception = Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            VertexDescriptor(),
            MeshData(Bounds((0f, 0f, 0f), (1f, 0f, 0f)), ("weapon", "")),
            Geometry(
                SkinnedVertices(
                    ((0f, 0f, 0f), [0, 0, 0, 0], [255, 0, 0, 0]),
                    ((1f, 0f, 0f), [1, 0, 0, 0], [255, 0, 0, 0])),
                [0, 1]),
            "bad bone"));

        Assert.Contains("out-of-range bone 1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeWholeMeshRejectsSceneBoundsDrift()
    {
        var exception = Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            VertexDescriptor(),
            MeshData(Bounds((0f, 0f, 0f), (2f, 0f, 0f)), ("weapon", "")),
            Geometry(
                SkinnedVertices(
                    ((0f, 0f, 0f), [0, 0, 0, 0], [255, 0, 0, 0]),
                    ((1f, 0f, 0f), [0, 0, 0, 0], [255, 0, 0, 0])),
                [0, 1]),
            "stale bounds"));

        Assert.Contains("scene bounds do not exactly match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeWholeMeshRejectsBoneSphereThatDoesNotMatchInfluencedVertices()
    {
        var descriptor = VertexDescriptor();
        var meshData = MeshDataWithBoneBounds(
            Bounds((0f, 0f, 0f), (1f, 0f, 0f)),
            ("weapon", "", (0.5f, 0f, 0f), (1f, 0f, 0f), 2f));
        var geometry = Geometry(
            SkinnedVertices(
                ((0f, 0f, 0f), [0, 0, 0, 0], [255, 0, 0, 0]),
                ((1f, 0f, 0f), [0, 0, 0, 0], [255, 0, 0, 0])),
            [0, 1]);
        var exception = Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            descriptor, meshData, geometry, "bad sphere"));

        Assert.Contains("culling sphere cannot be reproduced", exception.Message, StringComparison.Ordinal);
        var readOnly = Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            descriptor, meshData, geometry, "unaffected sphere", boneSizeIsHalfExtent: true,
            requireAllBoneSpheres: false);
        Assert.Equal(2f, Assert.Single(readOnly.BoneBounds).SphereRadius);
    }

    [Fact]
    public void AnalyzeWholeMeshRejectsPerDrawBoundsThatCannotYetBeUpdated()
    {
        var meshData = MeshData(
            Bounds((0f, 0f, 0f), (1f, 1f, 0f)),
            ("root", string.Empty));
        meshData["m_sceneObjects"][0]["m_drawBounds"].Add(
            Bounds((0f, 0f, 0f), (1f, 1f, 0f)));

        var exception = Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            VertexDescriptor(),
            meshData,
            Geometry(
                SkinnedVertices(
                    ((0f, 0f, 0f), [0, 0, 0, 0], [255, 0, 0, 0]),
                    ((1f, 0f, 0f), [0, 0, 0, 0], [255, 0, 0, 0]),
                    ((0f, 1f, 0f), [0, 0, 0, 0], [255, 0, 0, 0])),
                [0, 1, 2]),
            "bounded mesh"));

        Assert.Contains("per-draw bounds", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadDistanceFieldsRetainsScaleSensitiveMetadataAndSampleIdentity()
    {
        var samples = Enumerable.Range(0, 24).Select(value => (byte)value).ToArray();
        var result = Assert.Single(Source2TransformMetadataAnalyzer.ReadDistanceFields(
            DistanceField(samples, Bounds((-1f, -0.5f, 2f), (0f, 1f, 4f))),
            57,
            "test distance field"));

        Assert.Equal(57, result.ResourceBlockIndex);
        Assert.Equal(0, result.FieldIndex);
        Assert.Equal(3957205779u, result.ParentBoneNameHash);
        Assert.Equal(-1, result.BodyGroupIndex);
        Assert.Equal(0, result.BodyGroupChoice);
        Assert.Equal((2, 3, 4), (result.ResolutionX, result.ResolutionY, result.ResolutionZ));
        Assert.Equal(0.5f, result.GridCellSize);
        Assert.Equal(3.25f, result.MaximumQuantizedDistance);
        Assert.Equal(0.5f, result.SurfaceBias);
        Assert.True(result.IsTwoSided);
        Assert.False(result.IsFarFieldOnly);
        Assert.True(result.UseForOcclusion);
        Assert.False(result.UseForCollision);
        Assert.Equal(ContentHash.Compute(samples), result.QuantizedDataHash);
        Assert.Equal(samples.Length, result.QuantizedDataLength);
    }

    [Fact]
    public void ReadDistanceFieldsRejectsSampleCountMismatch()
    {
        var exception = Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.ReadDistanceFields(
            DistanceField(new byte[23], Bounds((0f, 0f, 0f), (1f, 1.5f, 2f))),
            1,
            "truncated distance field"));

        Assert.Contains("contains 23 samples; expected 24", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadDistanceFieldsRejectsGridExtentMismatch()
    {
        var exception = Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.ReadDistanceFields(
            DistanceField(new byte[24], Bounds((0f, 0f, 0f), (1.1f, 1.5f, 2f))),
            1,
            "bad grid"));

        Assert.Contains("axis 0 extent is inconsistent", exception.Message, StringComparison.Ordinal);
    }

    private static KVObject VertexDescriptor() => Object(
        ("m_vertexBuffers", Array(Object(
            ("m_inputLayoutFields", Array(
                Layout("POSITION", 6u, 0),
                Layout("BLENDINDICES", 30u, 20),
                Layout("BLENDWEIGHT", 28u, 24)))))));

    private static KVObject WideBlendIndexVertexDescriptor(int indexOffset = 20) => Object(
        ("m_vertexBuffers", Array(Object(
            ("m_inputLayoutFields", Array(
                Layout("POSITION", 6u, 0),
                Layout("BLENDINDICES", 14u, indexOffset),
                Layout("BLENDWEIGHT", 28u, 28)))))));

    private static Source2GeometryAnalysis WideBlendIndexGeometry(short activeBoneIndex, short? lastBoneIndex = null)
    {
        const int stride = 32;
        var bytes = new byte[stride * 3];
        for (var vertex = 0; vertex < 3; vertex++)
        {
            var offset = vertex * stride;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), BitConverter.SingleToInt32Bits(vertex));
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset + 20),
                vertex == 2 && lastBoneIndex.HasValue ? lastBoneIndex.Value : activeBoneIndex);
            bytes[offset + 28] = byte.MaxValue;
        }

        int[][] vertexSets = [[0, 1, 2]];
        return GeometrySource(bytes, stride, vertexSets);
    }

    private static KVObject RigidVertexDescriptor() => Object(
        ("m_vertexBuffers", Array(Object(
            ("m_inputLayoutFields", Array(
                Layout("POSITION", 6u, 0),
                Layout("BLENDINDICES", 30u, 20)))))));

    private static KVObject Layout(string semantic, uint format, int offset) => Object(
        ("m_pSemanticName", semantic),
        ("m_nSemanticIndex", 0),
        ("m_Format", format),
        ("m_nOffset", offset),
        ("m_nSlot", 0),
        ("m_nSlotType", "RENDER_SLOT_PER_VERTEX"));

    private static KVObject MeshData(
        KVObject bounds,
        params (string Name, string Parent)[] bones) => Object(
        ("m_sceneObjects", Array(Object(
            ("m_vMinBounds", bounds["m_vMinBounds"]),
            ("m_vMaxBounds", bounds["m_vMaxBounds"]),
            ("m_drawBounds", Array())))),
        ("m_skeleton", Object(
            ("m_nBoneWeightCount", 4),
            ("m_bones", Array(bones.Select(bone => BoneData(
                bone.Name,
                bone.Parent,
                (0f, 0f, 0f),
                (0f, 0f, 0f),
                0f)).ToArray())))));

    private static KVObject MeshDataWithBoneBounds(
        KVObject bounds,
        params (string Name, string Parent, (float X, float Y, float Z) Center, (float X, float Y, float Z) Size, float Radius)[] bones) => Object(
        ("m_sceneObjects", Array(Object(
            ("m_vMinBounds", bounds["m_vMinBounds"]),
            ("m_vMaxBounds", bounds["m_vMaxBounds"]),
            ("m_drawBounds", Array())))),
        ("m_skeleton", Object(
            ("m_nBoneWeightCount", 4),
            ("m_bones", Array(bones.Select(bone => BoneData(
                bone.Name,
                bone.Parent,
                bone.Center,
                bone.Size,
                bone.Radius)).ToArray())))));

    private static KVObject WithWeightCount(KVObject meshData, int weightCount)
    {
        meshData["m_skeleton"]["m_nBoneWeightCount"] = (KVObject)weightCount;
        return meshData;
    }

    private static KVObject BoneData(
        string name,
        string parent,
        (float X, float Y, float Z) center,
        (float X, float Y, float Z) size,
        float radius) => Object(
        ("m_boneName", name),
        ("m_parentName", parent),
        ("m_invBindPose", Array(
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f)),
        ("m_bbox", Object(
            ("m_vecCenter", Array(center.X, center.Y, center.Z)),
            ("m_vecSize", Array(size.X, size.Y, size.Z)))),
        ("m_flSphereRadius", radius));

    private static KVObject DistanceField(byte[] samples, KVObject bounds) => Object(
        ("m_distanceFields", Array(Object(
            ("m_nParentBoneNameHash", 3957205779u),
            ("m_nBodyGroupIndex", -1),
            ("m_nBodyGroupChoice", 0),
            ("m_nResX", 2),
            ("m_nResY", 3),
            ("m_nResZ", 4),
            ("m_flGridCellSize", 0.5f),
            ("m_flMaxQuantizedDistance", 3.25f),
            ("m_flSurfaceBias", 0.5f),
            ("m_bIsTwoSided", true),
            ("m_bIsFarFieldOnly", false),
            ("m_bUseForOcclusion", true),
            ("m_bUseForCollision", false),
            ("m_bounds", bounds),
            ("m_quantizedData", KVObject.Blob(samples))))));

    private static KVObject Bounds(
        (float X, float Y, float Z) min,
        (float X, float Y, float Z) max) => Object(
        ("m_vMinBounds", Array(min.X, min.Y, min.Z)),
        ("m_vMaxBounds", Array(max.X, max.Y, max.Z)));

    private static Source2GeometryAnalysis Geometry(byte[] vertices, params int[][] vertexSets) =>
        GeometrySource(vertices, 28, vertexSets);

    private static Source2GeometryAnalysis RigidGeometry(
        params ((float X, float Y, float Z) Position, byte[] Indices)[] vertices) =>
        GeometrySource(RigidVertices(vertices), 24, [Enumerable.Range(0, vertices.Length).ToArray()]);

    private static Source2GeometryAnalysis GeometrySource(byte[] vertices, int stride, int[][] vertexSets)
    {
        var positionBounds = new GeometryBounds(
            new TransformVector3 { X = 0f, Y = 0f, Z = 0f },
            new TransformVector3 { X = 0f, Y = 0f, Z = 0f });
        var vertexSnapshot = new VertexBufferSnapshot(
            0,
            1,
            vertices.Length / stride,
            stride,
            ContentHash.Compute(vertices),
            ContentHash.Compute(vertices),
            new PositionLayout("R32G32B32_FLOAT", 0, stride));
        var indexSnapshot = new IndexBufferSnapshot(0, 2, 3, 2, EmptyHash, EmptyHash);
        var calls = vertexSets.Select((verticesForCall, ordinal) => new Source2DrawCallAnalysis(
            new DrawCallGeometrySnapshot(
                $"dc_{ordinal}",
                0,
                0,
                0,
                vertexSnapshot.VertexCount,
                verticesForCall.Distinct().Count(),
                new ContentHash(VertexSetHash.Compute(verticesForCall)),
                positionBounds,
                true),
            verticesForCall.Order().Distinct().ToImmutableArray())).ToArray();
        var snapshot = new MeshGeometrySnapshot(
            "ready",
            "test",
            [vertexSnapshot],
            [indexSnapshot],
            calls.Select(call => call.Snapshot).ToArray(),
            null);
        return new Source2GeometryAnalysis(
            snapshot,
            [new Source2VertexBufferAnalysis(vertexSnapshot, vertices)],
            [new Source2IndexBufferAnalysis(indexSnapshot, [0, 1, 2])],
            calls);
    }

    private static byte[] RigidVertices(
        params ((float X, float Y, float Z) Position, byte[] Indices)[] vertices)
    {
        const int stride = 24;
        var result = new byte[checked(stride * vertices.Length)];
        for (var ordinal = 0; ordinal < vertices.Length; ordinal++)
        {
            var offset = ordinal * stride;
            var vertex = vertices[ordinal];
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), BitConverter.SingleToInt32Bits(vertex.Position.X));
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset + 4), BitConverter.SingleToInt32Bits(vertex.Position.Y));
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset + 8), BitConverter.SingleToInt32Bits(vertex.Position.Z));
            result.AsSpan(offset + 12, 8).Fill(0x5a);
            vertex.Indices.CopyTo(result, offset + 20);
        }

        return result;
    }

    private static byte[] SkinnedVertices(
        params ((float X, float Y, float Z) Position, byte[] Indices, byte[] Weights)[] vertices)
    {
        const int stride = 28;
        var result = new byte[checked(stride * vertices.Length)];
        for (var ordinal = 0; ordinal < vertices.Length; ordinal++)
        {
            var offset = ordinal * stride;
            var vertex = vertices[ordinal];
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), BitConverter.SingleToInt32Bits(vertex.Position.X));
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset + 4), BitConverter.SingleToInt32Bits(vertex.Position.Y));
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset + 8), BitConverter.SingleToInt32Bits(vertex.Position.Z));
            vertex.Indices.CopyTo(result, offset + 20);
            vertex.Weights.CopyTo(result, offset + 24);
        }

        return result;
    }

    private static KVObject Object(params (string Key, KVObject Value)[] values)
    {
        var result = KVObject.Collection();
        foreach (var (key, value) in values)
        {
            result.Add(key, value);
        }

        return result;
    }

    private static KVObject Array(params KVObject[] values)
    {
        var result = KVObject.Array();
        foreach (var value in values)
        {
            result.Add(value);
        }

        return result;
    }
}
