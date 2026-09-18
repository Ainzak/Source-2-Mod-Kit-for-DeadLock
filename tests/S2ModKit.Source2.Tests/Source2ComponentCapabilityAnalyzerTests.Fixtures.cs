using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;
using ValveResourceFormat;

namespace S2ModKit.Source2.Tests;

public sealed partial class Source2ComponentCapabilityAnalyzerTests
{
    private static void AssertGeometryUnavailable(
        KVObject meshData,
        Action<List<(byte[] Indices, byte[] Weights)>> configureBlend,
        (float X, float Y, float Z)[]? positions = null,
        int? vertexCountOverride = null,
        int[]? coveredVertices = null)
    {
        var actualPositions = positions ?? Enumerable.Range(0, vertexCountOverride ?? 2)
            .Select(vertex => (vertex * 1f, 0f, 0f))
            .ToArray();
        var blend = Blended(actualPositions.Length, [0, 0, 0, 0], [255, 0, 0, 0]);
        configureBlend(blend);
        var mesh = ReadyMesh(
            0,
            1,
            10,
            28,
            actualPositions,
            decorate: vertexBytes => WritePackedBlend(vertexBytes, blend),
            VertexDescriptor(),
            meshData,
            (0L, 3L, coveredVertices ?? Enumerable.Range(0, actualPositions.Length).ToArray()));

        var profile = new ComponentCapabilityProfile([0], ResourcePath, [mesh]);
        var result = Assess(profile, Selection("cmp_all", Sel(0, 1, mesh.DrawCalls[0].Id, blockIndex: 10)));
        Assert.Equal(ComponentDiscoveryContract.Unsupported, result.Availability);
        Assert.Equal("TRANSFORM_GEOMETRY_UNAVAILABLE", Assert.Single(result.Reasons).Code);
        Assert.Empty(result.GeometryByLod);
    }

    private static void AssertAmbiguous(ComponentCapabilityProfile profile, ComponentCapabilitySelection selection)
    {
        var result = Assess(profile, selection);
        Assert.Equal(ComponentDiscoveryContract.Ambiguous, result.Availability);
        Assert.Equal("COMPONENT_CANDIDATE_MAPPING_AMBIGUOUS", Assert.Single(result.Reasons).Code);
        Assert.Empty(result.GeometryByLod);
    }

    private static void AssertAmbiguous(ComponentCapabilityProfile profile, SelectedDrawCall drawCall)
    {
        var result = Assess(profile, Selection("cmp_x", drawCall));
        Assert.Equal(ComponentDiscoveryContract.Ambiguous, result.Availability);
        Assert.Equal("COMPONENT_CANDIDATE_MAPPING_AMBIGUOUS", Assert.Single(result.Reasons).Code);
        Assert.Empty(result.GeometryByLod);
    }

    private static ComponentCapabilityAnalysis Assess(
        ComponentCapabilityProfile profile,
        ComponentCapabilitySelection selection) =>
        Assert.Single(
            Source2CompiledModelAdapter.AssessSelections(
                [selection],
                profile,
                CancellationToken.None));

    private static IReadOnlyList<ComponentCapabilityAnalysis> AssessSelections(
        ComponentCapabilityProfile profile,
        params ComponentCapabilitySelection[] selections) =>
        Source2CompiledModelAdapter.AssessSelections(
            selections,
            profile,
            CancellationToken.None);

    private static ModelSnapshot Snapshot(string path, ContentHash hash, long size) =>
        new(new ArtifactSnapshot(path, hash, size, []), []);

    private static SelectedDrawCall Sel(
        int lod,
        int meshOrdinal,
        string drawCallId,
        string materialPath = MaterialPath,
        int ordinal = 0,
        long start = 0,
        long count = 3,
        int blockIndex = 10) => new(
            lod,
            ResourcePath,
            meshOrdinal,
            blockIndex,
            drawCallId,
            materialPath,
            ordinal,
            start,
            count);

    private static ComponentCapabilitySelection Selection(string id, params SelectedDrawCall[] drawCalls) => new(
        id,
        ComponentDiscoveryContract.MaterialGroupKind,
        [MaterialPath],
        drawCalls);

    private static DrawCallSnapshot Call(int lod, int meshOrdinal, int ordinal, long start, long count) =>
        DrawCallSnapshot.Create(ResourcePath, lod, meshOrdinal, ordinal, MaterialPath, start, count);

    private static List<(byte[] Indices, byte[] Weights)> Blended(
        int vertexCount,
        byte[] indices,
        byte[] weights) => Enumerable.Range(0, vertexCount)
            .Select(_ => ((byte[])indices.Clone(), (byte[])weights.Clone()))
            .ToList();

    private static void WritePackedBlend(byte[] vertexBytes, List<(byte[] Indices, byte[] Weights)> blend)
    {
        for (var vertex = 0; vertex < blend.Count; vertex++)
        {
            blend[vertex].Indices.CopyTo(vertexBytes.AsSpan((vertex * 28) + 20, 4));
            blend[vertex].Weights.CopyTo(vertexBytes.AsSpan((vertex * 28) + 24, 4));
        }
    }

    private static ComponentProfileMesh AvailableMesh(
        int lod,
        int meshOrdinal,
        int blockIndex,
        int vertexCount,
        string rootBoneName = "root_motion",
        string tipBoneName = "weapon")
    {
        var positions = Enumerable.Range(0, vertexCount)
            .Select(vertex => (vertex * 1f, 0f, 0f))
            .ToArray();
        var blend = Blended(vertexCount, [1, 0, 0, 0], [255, 0, 0, 0]);
        return ReadyMesh(
            lod,
            meshOrdinal,
            blockIndex,
            28,
            positions,
            decorate: vertexBytes => WritePackedBlend(vertexBytes, blend),
            VertexDescriptor(),
            WeightedMeshData(
                Bounds((0f, 0f, 0f), ((vertexCount - 1) * 1f, 0f, 0f)),
                (rootBoneName, string.Empty, (0f, 0f, 0f), (0f, 0f, 0f), 0f),
                (tipBoneName, rootBoneName, ((vertexCount - 1) * 0.5f, 0f, 0f), ((vertexCount - 1) * 1f, 1f, 1f), (vertexCount - 1) * 1f)),
            (0L, 3L, Enumerable.Range(0, vertexCount).ToArray()));
    }

    private static ComponentProfileMesh PackedApolloLikeMesh()
    {
        var positions = new[] { (0f, 0f, 0f), (1f, 1f, 0f), (2f, 0f, 1f) };
        var bladeVertices = new[] { 0, 1 };
        var gemVertices = new[] { 1, 2 };
        var blend = new List<(byte[] Indices, byte[] Weights)>
        {
            ([1, 0, 0, 0], [255, 0, 0, 0]),
            ([1, 2, 0, 0], [252, 3, 0, 0]),
            ([3, 0, 0, 0], [255, 0, 0, 0]),
        };
        return ReadyMesh(
            0,
            1,
            10,
            28,
            positions,
            decorate: vertexBytes => WritePackedBlend(vertexBytes, blend),
            VertexDescriptor(),
            WeightedMeshData(
                Bounds((0f, 0f, 0f), (2f, 1f, 1f)),
                ("root_motion", string.Empty, (0f, 0f, 0f), (0f, 0f, 0f), 0f),
                ("weapon", "root_motion", (0.5f, 0.5f, 0f), (1f, 1f, 0f), MathF.Sqrt(2f)),
                ("blade", "weapon", (1f, 1f, 0f), (0f, 0f, 0f), MathF.Sqrt(2f)),
                ("gem", "weapon", (2f, 0f, 1f), (0f, 0f, 0f), MathF.Sqrt(5f))),
            (0L, 3L, bladeVertices), (3L, 3L, gemVertices));
    }

    private static ComponentProfileMesh RigidPocketLikeMesh()
    {
        var positions = new[] { (0f, 0f, 0f), (1f, 0f, 0f), (0f, 1f, 0f) };
        var caseVertices = new[] { 0, 1, 2 };
        var blendIndices = new[] { new byte[] { 1, 9, 8, 7 }, new byte[] { 1, 0, 0, 0 }, new byte[] { 2, 5, 3, 0 } };
        return ReadyMesh(
            0,
            3,
            10,
            24,
            positions,
            decorate: vertexBytes =>
            {
                for (var vertex = 0; vertex < blendIndices.Length; vertex++)
                {
                    blendIndices[vertex].CopyTo(vertexBytes.AsSpan((vertex * 24) + 20, 4));
                }
            },
            RigidVertexDescriptor(),
            RigidMeshData(
                Bounds((0f, 0f, 0f), (1f, 1f, 0f)),
                ("root_motion", string.Empty, (0f, 0f, 0f), (0f, 0f, 0f), 0f),
                ("suitcase1", "root_motion", (0.5f, 0f, 0f), (1f, 1f, 1f), 1f),
                ("suitcase2", "root_motion", (0f, 0.5f, 0f), (1f, 1f, 1f), 1f)),
            (0L, 3L, caseVertices));
    }

    private static ComponentProfileMesh ExclusiveTwoDrawCallProfile()
    {
        var positions = new[] { (0f, 0f, 0f), (1f, 0f, 0f), (2f, 0f, 0f), (3f, 0f, 0f) };
        var blend = Blended(4, [1, 0, 0, 0], [255, 0, 0, 0]);
        var firstVertices = new[] { 0, 1 };
        var secondVertices = new[] { 2, 3 };
        return ReadyMesh(
            0,
            1,
            10,
            28,
            positions,
            decorate: vertexBytes => WritePackedBlend(vertexBytes, blend),
            VertexDescriptor(),
            WeightedMeshData(
                Bounds((0f, 0f, 0f), (3f, 0f, 0f)),
                ("root_motion", string.Empty, (0f, 0f, 0f), (0f, 0f, 0f), 0f),
                ("weapon", "root_motion", (1.5f, 0f, 0f), (3f, 1f, 1f), 1.5f)),
            (0L, 3L, firstVertices), (3L, 3L, secondVertices));
    }

    private static ComponentProfileMesh UnavailableMesh(int lod, int meshOrdinal, int blockIndex) => Mesh(
        lod,
        meshOrdinal,
        blockIndex,
        "unavailable",
        [Call(lod, meshOrdinal, 0, 0, 3)]);

    private static ComponentProfileMesh ReadyMesh(
        int lod,
        int meshOrdinal,
        int blockIndex,
        int stride,
        (float X, float Y, float Z)[] positions,
        Action<byte[]> decorate,
        KVObject descriptor,
        KVObject meshData,
        params (long Start, long Count, int[] Vertices)[] calls)
    {
        var drawCalls = calls
            .Select((call, ordinal) => DrawCallSnapshot.Create(
                ResourcePath,
                lod,
                meshOrdinal,
                ordinal,
                MaterialPath,
                call.Start,
                call.Count))
            .ToArray();
        var analysis = BuildAnalysis(
            stride,
            positions,
            decorate,
            drawCalls,
            calls.Select(call => call.Vertices).ToArray());
        return Mesh(lod, meshOrdinal, blockIndex, "ready", drawCalls, descriptor, meshData, analysis);
    }

    private static ComponentProfileMesh Mesh(
        int lod,
        int meshOrdinal,
        int blockIndex,
        string status,
        IReadOnlyList<DrawCallSnapshot> drawCalls,
        KVObject? descriptor = null,
        KVObject? meshData = null,
        Source2GeometryAnalysis? analysis = null) => new(
            meshOrdinal,
            lod,
            blockIndex,
            ResourcePath,
            drawCalls,
            status,
            descriptor,
            meshData,
            analysis);

    private static Source2GeometryAnalysis BuildAnalysis(
        int stride,
        (float X, float Y, float Z)[] positions,
        Action<byte[]> decorate,
        IReadOnlyList<DrawCallSnapshot> drawCalls,
        int[][] vertexSets)
    {
        var vertexBytes = new byte[checked(stride * positions.Length)];
        for (var vertex = 0; vertex < positions.Length; vertex++)
        {
            var offset = vertex * stride;
            BinaryPrimitives.WriteInt32LittleEndian(vertexBytes.AsSpan(offset), BitConverter.SingleToInt32Bits(positions[vertex].Item1));
            BinaryPrimitives.WriteInt32LittleEndian(vertexBytes.AsSpan(offset + 4), BitConverter.SingleToInt32Bits(positions[vertex].Item2));
            BinaryPrimitives.WriteInt32LittleEndian(vertexBytes.AsSpan(offset + 8), BitConverter.SingleToInt32Bits(positions[vertex].Item3));
            vertexBytes.AsSpan(offset + 12, stride - 12).Fill(0x5a);
        }

        decorate(vertexBytes);
        var vertexSnapshot = new VertexBufferSnapshot(
            0,
            1,
            positions.Length,
            stride,
            EmptyHash,
            EmptyHash,
            new PositionLayout("R32G32B32_FLOAT", 0, stride));
        var indexSnapshot = new IndexBufferSnapshot(0, 2, 3, 2, EmptyHash, EmptyHash);
        var anyBounds = new GeometryBounds(
            new TransformVector3 { X = 0f, Y = 0f, Z = 0f },
            new TransformVector3 { X = 0f, Y = 0f, Z = 0f });
        var analyzedCalls = drawCalls.Zip(vertexSets, (call, vertices) => new Source2DrawCallAnalysis(
            new DrawCallGeometrySnapshot(
                call.Id,
                0,
                0,
                0,
                positions.Length,
                vertices.Length,
                new ContentHash(VertexSetHash.Compute(vertices)),
                anyBounds,
                true),
            ImmutableArray.Create(vertices))).ToArray();
        var snapshot = new MeshGeometrySnapshot(
            "ready",
            "test",
            [vertexSnapshot],
            [indexSnapshot],
            analyzedCalls.Select(item => item.Snapshot).ToArray(),
            null);
        return new Source2GeometryAnalysis(
            snapshot,
            [new Source2VertexBufferAnalysis(vertexSnapshot, vertexBytes)],
            [new Source2IndexBufferAnalysis(indexSnapshot, [0u, 1u, 2u])],
            analyzedCalls);
    }

    private static (string Name, string Parent, (float X, float Y, float Z) Center, (float X, float Y, float Z) Size, float Radius) SingleWeaponBone(
        float radius) =>
        ("weapon", "root_motion", (0.5f, 0f, 0f), (1f, 1f, 1f), radius);

    private static bool IsForeignLibraryType(Type type)
    {
        if (type.IsArray)
        {
            return IsForeignLibraryType(type.GetElementType()!);
        }

        if (type.IsGenericType)
        {
            return type.GetGenericArguments().Any(IsForeignLibraryType);
        }

        var assembly = type.Assembly;
        return assembly == typeof(KVObject).Assembly || assembly == typeof(Resource).Assembly;
    }

    private static bool IsNativeFree(Type type, HashSet<Type> visited)
    {
        if (!visited.Add(type))
        {
            return true;
        }

        var assembly = type.Assembly;
        if (assembly == typeof(KVObject).Assembly || assembly == typeof(Resource).Assembly)
        {
            return false;
        }

        if (type.IsPrimitive || type == typeof(string) || type.IsEnum)
        {
            return true;
        }

        if (type.IsArray)
        {
            return IsNativeFree(type.GetElementType()!, visited);
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                if (!IsNativeFree(argument, visited))
                {
                    return false;
                }
            }
        }

        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .All(property => IsNativeFree(property.PropertyType, visited));
    }

    private static KVObject VertexDescriptor() => Object(
        ("m_vertexBuffers", Array(Object(
            ("m_inputLayoutFields", Array(
                Layout("POSITION", 6u, 0),
                Layout("BLENDINDICES", 30u, 20),
                Layout("BLENDWEIGHT", 28u, 24)))))));

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

    private static KVObject WeightedMeshData(
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

    private static KVObject RigidMeshData(
        KVObject bounds,
        params (string Name, string Parent, (float X, float Y, float Z) Center, (float X, float Y, float Z) Size, float Radius)[] bones) => Object(
        ("m_sceneObjects", Array(Object(
            ("m_vMinBounds", bounds["m_vMinBounds"]),
            ("m_vMaxBounds", bounds["m_vMaxBounds"]),
            ("m_drawBounds", Array())))),
        ("m_skeleton", Object(
            ("m_nBoneWeightCount", 1),
            ("m_bones", Array(bones.Select(bone => BoneData(
                bone.Name,
                bone.Parent,
                bone.Center,
                bone.Size,
                bone.Radius)).ToArray())))));

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

    private static KVObject Bounds((float X, float Y, float Z) min, (float X, float Y, float Z) max) => Object(
        ("m_vMinBounds", Array(min.X, min.Y, min.Z)),
        ("m_vMaxBounds", Array(max.X, max.Y, max.Z)));

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
