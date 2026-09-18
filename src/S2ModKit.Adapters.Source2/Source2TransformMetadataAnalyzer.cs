using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;
using ValveResourceFormat.Utils;

namespace S2ModKit.Adapters.Source2;

internal sealed record Source2WholeMeshTransformAnalysis(
    int SceneObjectIndex,
    GeometryBounds SceneBounds,
    ContentHash VertexSetHash,
    int VertexCount,
    string LocalSkinningRootBone,
    uint LocalSkinningRootBoneHash,
    ImmutableArray<string> LocalInfluencingBones,
    ImmutableArray<Source2BoneBoundsAnalysis> BoneBounds);

internal sealed record Source2BoneBoundsAnalysis(
    int BoneIndex,
    string BoneName,
    ContentHash InverseBindPoseHash,
    ImmutableArray<float> InverseBindPose,
    ImmutableArray<int> InfluencedVertices,
    TransformVector3 LocalBoundsCenter,
    TransformVector3 LocalBoundsSize,
    GeometryBounds LocalBounds,
    float SphereRadius);

internal sealed record Source2DistanceFieldAnalysis(
    int ResourceBlockIndex,
    int FieldIndex,
    uint ParentBoneNameHash,
    int BodyGroupIndex,
    int BodyGroupChoice,
    int ResolutionX,
    int ResolutionY,
    int ResolutionZ,
    float GridCellSize,
    float MaximumQuantizedDistance,
    float SurfaceBias,
    bool IsTwoSided,
    bool IsFarFieldOnly,
    bool UseForOcclusion,
    bool UseForCollision,
    GeometryBounds Bounds,
    ContentHash QuantizedDataHash,
    int QuantizedDataLength);

internal static class Source2TransformMetadataAnalyzer
{
    private const uint R8G8B8A8Unorm = 28;
    private const uint R8G8B8A8Uint = 30;
    private const int PackedAttributeSize = 4;
    private const int MaximumDistanceFieldBytes = 512 * 1024 * 1024;

    public static Source2WholeMeshTransformAnalysis AnalyzeWholeMesh(
        KVObject embeddedMeshDescriptor,
        KVObject meshData,
        Source2GeometryAnalysis geometry,
        string context)
    {
        ArgumentNullException.ThrowIfNull(embeddedMeshDescriptor);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        if (geometry.VertexBuffers.Count != 1)
        {
            throw new InvalidDataException($"{context} does not have exactly one vertex buffer.");
        }

        var vertexBuffer = geometry.VertexBuffers[0];
        var vertexDescriptors = RequireArray(embeddedMeshDescriptor, "m_vertexBuffers", context);
        if (vertexDescriptors.Count != 1)
        {
            throw new InvalidDataException($"{context} does not have exactly one vertex descriptor.");
        }

        var vertexDescriptor = RequireCollection(vertexDescriptors[0], $"{context}.m_vertexBuffers[0]");
        var inputLayout = RequireArray(vertexDescriptor, "m_inputLayoutFields", context);
        var blendIndices = RequireLayoutField(inputLayout, "BLENDINDICES", R8G8B8A8Uint, context);
        var indexOffset = RequirePackedOffset(blendIndices, vertexBuffer.Snapshot.Stride, "BLENDINDICES", context);
        var blendWeights = TryLayoutField(inputLayout, "BLENDWEIGHT", context);
        int? weightOffset = null;
        if (blendWeights is not null)
        {
            ValidatePackedPerVertexField(blendWeights, "BLENDWEIGHT", R8G8B8A8Unorm, context);
            weightOffset = RequirePackedOffset(blendWeights, geometry.VertexBuffers[0].Snapshot.Stride, "BLENDWEIGHT", context);
            if (RangesOverlap(indexOffset, weightOffset.Value))
            {
                throw new InvalidDataException($"{context} blend-index and blend-weight byte ranges overlap.");
            }
        }

        return AnalyzeWholeMeshCore(meshData, geometry, indexOffset, weightOffset, context);
    }

    public static Source2WholeMeshTransformAnalysis AnalyzeRawMbufWholeMesh(
        KVObject meshData,
        Source2RawMbufAnalysis raw,
        string context)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return AnalyzeWholeMeshCore(meshData, raw.Geometry, raw.BlendIndicesOffset, null, context);
    }

    private static Source2WholeMeshTransformAnalysis AnalyzeWholeMeshCore(
        KVObject meshData,
        Source2GeometryAnalysis geometry,
        int indexOffset,
        int? weightOffset,
        string context)
    {
        ArgumentNullException.ThrowIfNull(meshData);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        if (!string.Equals(geometry.Snapshot.Status, "ready", StringComparison.Ordinal)
            || geometry.VertexBuffers.Count != 1
            || geometry.IndexBuffers.Count != 1
            || geometry.DrawCalls.Count == 0)
        {
            throw new InvalidDataException($"{context} is not a ready single-buffer transform profile.");
        }

        var vertexBuffer = geometry.VertexBuffers[0];
        if (indexOffset < 0 || indexOffset > vertexBuffer.Snapshot.Stride - PackedAttributeSize)
        {
            throw new InvalidDataException($"{context} blend-index offset is outside the vertex stride.");
        }

        var selectedVertices = geometry.DrawCalls.SelectMany(item => item.VertexIndices).Distinct().Order().ToArray();
        if (selectedVertices.Length != vertexBuffer.Snapshot.VertexCount)
        {
            throw new InvalidDataException(
                $"{context} draw calls cover {selectedVertices.Length} of {vertexBuffer.Snapshot.VertexCount} vertices; whole-mesh transform requires complete coverage.");
        }

        for (var index = 0; index < selectedVertices.Length; index++)
        {
            if (selectedVertices[index] != index)
            {
                throw new InvalidDataException($"{context} whole-mesh vertex coverage is not canonical.");
            }
        }

        var sceneObjects = RequireArray(meshData, "m_sceneObjects", context);
        if (sceneObjects.Count != 1)
        {
            throw new InvalidDataException($"{context} has {sceneObjects.Count} scene objects; exactly one is required.");
        }

        var scene = RequireCollection(sceneObjects[0], $"{context}.m_sceneObjects[0]");
        var drawBounds = RequireArray(scene, "m_drawBounds", $"{context}.m_sceneObjects[0]");
        if (drawBounds.Count != 0)
        {
            throw new InvalidDataException($"{context} has unsupported per-draw bounds.");
        }

        var sceneBounds = ReadBounds(scene, context);
        var positionBounds = Bounds3.FromPoints(selectedVertices.Select(vertex => Source2GeometryAnalyzer.ReadPosition(vertexBuffer, vertex)).ToArray());
        if (sceneBounds != ToDomainBounds(positionBounds))
        {
            throw new InvalidDataException($"{context} scene bounds do not exactly match the rendered vertices.");
        }

        var rigidSingleInfluence = weightOffset is null;
        var declaredWeightCount = rigidSingleInfluence ? ReadWeightCount(meshData, context) : 0;
        if (rigidSingleInfluence && declaredWeightCount != 1)
        {
            throw new InvalidDataException(
                $"{context} has no BLENDWEIGHT field but declares blend weight count {declaredWeightCount}; the rigid profile requires exactly one influence per vertex.");
        }

        var bones = ReadSkeleton(meshData, context);
        var influencedIndices = new HashSet<int>();
        var verticesByBone = new Dictionary<int, HashSet<int>>();
        foreach (var vertex in selectedVertices)
        {
            var recordOffset = checked(vertex * vertexBuffer.Snapshot.Stride);
            var indices = vertexBuffer.Decoded.AsSpan(recordOffset + indexOffset, PackedAttributeSize);
            if (rigidSingleInfluence)
            {
                AddInfluence(indices[0], vertex, bones, influencedIndices, verticesByBone, context);
                continue;
            }

            var weights = vertexBuffer.Decoded.AsSpan(recordOffset + weightOffset!.Value, PackedAttributeSize);
            var weightSum = 0;
            for (var influence = 0; influence < PackedAttributeSize; influence++)
            {
                weightSum = checked(weightSum + weights[influence]);
                if (weights[influence] == 0)
                {
                    continue;
                }

                AddInfluence(indices[influence], vertex, bones, influencedIndices, verticesByBone, context);
            }

            if (weightSum != byte.MaxValue)
            {
                throw new InvalidDataException($"{context} vertex {vertex} blend weights sum to {weightSum}; expected 255.");
            }
        }

        if (influencedIndices.Count == 0)
        {
            throw new InvalidDataException($"{context} has no non-zero skinning influences.");
        }

        var rootIndex = FindDeepestCommonAncestor(bones, influencedIndices, context);
        var rootName = bones[rootIndex].Name;
        var boneBounds = influencedIndices.Order()
            .Select(index => AnalyzeBoneBounds(
                bones[index],
                verticesByBone[index],
                vertexBuffer,
                context))
            .ToImmutableArray();
        return new Source2WholeMeshTransformAnalysis(
            0,
            sceneBounds,
            new ContentHash(VertexSetHash.Compute(selectedVertices)),
            selectedVertices.Length,
            rootName,
            StringToken.Get(rootName),
            influencedIndices.Order().Select(index => bones[index].Name).ToImmutableArray(),
            boneBounds);
    }

    private static Source2BoneBoundsAnalysis AnalyzeBoneBounds(
        Bone bone,
        HashSet<int> influencedVertices,
        Source2VertexBufferAnalysis vertexBuffer,
        string context)
    {
        if (influencedVertices.Count == 0)
        {
            throw new InvalidDataException($"{context} bone '{bone.Name}' has no influenced vertices.");
        }

        var localPoints = influencedVertices.Order()
            .Select(vertex => TransformPoint(
                Source2GeometryAnalyzer.ReadPosition(vertexBuffer, vertex),
                bone.InverseBindPose))
            .ToArray();
        var computedRadius = localPoints.Max(point => MathF.Sqrt(
            (point.X * point.X) + (point.Y * point.Y) + (point.Z * point.Z)));
        if (!float.IsFinite(computedRadius)
            || !NearlyEqual(computedRadius, bone.SphereRadius))
        {
            throw new InvalidDataException(
                $"{context} bone '{bone.Name}' culling sphere cannot be reproduced from its influenced vertices and inverse bind pose " +
                $"(computed radius {computedRadius.ToString("R", CultureInfo.InvariantCulture)}; " +
                $"stored radius {bone.SphereRadius.ToString("R", CultureInfo.InvariantCulture)}).");
        }

        return new Source2BoneBoundsAnalysis(
            bone.Index,
            bone.Name,
            HashMatrix(bone.InverseBindPose),
            bone.InverseBindPose,
            influencedVertices.Order().ToImmutableArray(),
            bone.LocalBoundsCenter,
            bone.LocalBoundsSize,
            bone.LocalBounds,
            bone.SphereRadius);
    }

    internal static Point3 TransformPoint(Point3 point, ImmutableArray<float> matrix) => new(
        (((point.X * matrix[0]) + (point.Y * matrix[1])) + (point.Z * matrix[2])) + matrix[3],
        (((point.X * matrix[4]) + (point.Y * matrix[5])) + (point.Z * matrix[6])) + matrix[7],
        (((point.X * matrix[8]) + (point.Y * matrix[9])) + (point.Z * matrix[10])) + matrix[11]);

    internal static Point3 TransformDirection(Point3 direction, ImmutableArray<float> matrix) => new(
        ((direction.X * matrix[0]) + (direction.Y * matrix[1])) + (direction.Z * matrix[2]),
        ((direction.X * matrix[4]) + (direction.Y * matrix[5])) + (direction.Z * matrix[6]),
        ((direction.X * matrix[8]) + (direction.Y * matrix[9])) + (direction.Z * matrix[10]));

    private static ContentHash HashMatrix(ImmutableArray<float> matrix)
    {
        var bytes = new byte[checked(matrix.Length * sizeof(float))];
        for (var index = 0; index < matrix.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(index * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(matrix[index]));
        }

        return ContentHash.Compute(bytes);
    }

    private static bool NearlyEqual(float left, float right)
    {
        var tolerance = MathF.Max(0.0001f, MathF.Max(MathF.Abs(left), MathF.Abs(right)) * 0.00001f);
        return MathF.Abs(left - right) <= tolerance;
    }

    public static IReadOnlyList<Source2DistanceFieldAnalysis> ReadDistanceFields(
        KVObject distanceFieldRoot,
        int resourceBlockIndex,
        string context)
    {
        ArgumentNullException.ThrowIfNull(distanceFieldRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentOutOfRangeException.ThrowIfNegative(resourceBlockIndex);

        var fields = RequireArray(distanceFieldRoot, "m_distanceFields", context);
        if (fields.Count == 0)
        {
            throw new InvalidDataException($"{context} contains no distance fields.");
        }

        var result = new Source2DistanceFieldAnalysis[fields.Count];
        for (var fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
        {
            var field = RequireCollection(fields[fieldIndex], $"{context}.m_distanceFields[{fieldIndex}]");
            var resolutionX = RequirePositiveInt32(field, "m_nResX", context);
            var resolutionY = RequirePositiveInt32(field, "m_nResY", context);
            var resolutionZ = RequirePositiveInt32(field, "m_nResZ", context);
            var expectedLength = checked((long)resolutionX * resolutionY * resolutionZ);
            if (expectedLength > MaximumDistanceFieldBytes)
            {
                throw new InvalidDataException($"{context} distance field {fieldIndex} exceeds the supported sample budget.");
            }

            var quantizedData = RequireBlob(field, "m_quantizedData", context);
            if (quantizedData.Length != expectedLength)
            {
                throw new InvalidDataException(
                    $"{context} distance field {fieldIndex} contains {quantizedData.Length} samples; expected {expectedLength}.");
            }

            var gridCellSize = RequirePositiveFiniteSingle(field, "m_flGridCellSize", context);
            var maximumDistance = RequirePositiveFiniteSingle(field, "m_flMaxQuantizedDistance", context);
            var surfaceBias = RequireFiniteSingle(field, "m_flSurfaceBias", context);
            var bounds = ReadBounds(field, context);
            ValidateGridExtents(bounds, resolutionX, resolutionY, resolutionZ, gridCellSize, context, fieldIndex);
            result[fieldIndex] = new Source2DistanceFieldAnalysis(
                resourceBlockIndex,
                fieldIndex,
                RequireUInt32(field, "m_nParentBoneNameHash", context),
                RequireInt32(field, "m_nBodyGroupIndex", context),
                RequireInt32(field, "m_nBodyGroupChoice", context),
                resolutionX,
                resolutionY,
                resolutionZ,
                gridCellSize,
                maximumDistance,
                surfaceBias,
                RequireBoolean(field, "m_bIsTwoSided", context),
                RequireBoolean(field, "m_bIsFarFieldOnly", context),
                RequireBoolean(field, "m_bUseForOcclusion", context),
                RequireBoolean(field, "m_bUseForCollision", context),
                bounds,
                ContentHash.Compute(quantizedData),
                quantizedData.Length);
        }

        return result;
    }

    private static Bone[] ReadSkeleton(KVObject meshData, string context)
    {
        var skeleton = RequireCollectionProperty(meshData, "m_skeleton", context);
        var values = RequireArray(skeleton, "m_bones", context);
        if (values.Count == 0 || values.Count > byte.MaxValue + 1)
        {
            throw new InvalidDataException($"{context} skeleton bone count {values.Count} is unsupported for packed byte indices.");
        }

        var bones = new Bone[values.Count];
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            var bone = RequireCollection(values[index], $"{context}.m_skeleton.m_bones[{index}]");
            var name = RequireString(bone, "m_boneName", context);
            var parent = RequireStringAllowEmpty(bone, "m_parentName", context);
            if (!names.TryAdd(name, index))
            {
                throw new InvalidDataException($"{context} skeleton duplicates bone name '{name}'.");
            }

            var inverseBindPose = ReadMatrix(bone, "m_invBindPose", context);
            var (localBoundsCenter, localBoundsSize, localBounds) = ReadCenterSizeBounds(bone, "m_bbox", context);
            var sphereRadius = RequireNonNegativeFiniteSingle(bone, "m_flSphereRadius", context);
            bones[index] = new Bone(index, name, parent, -1, inverseBindPose, localBoundsCenter, localBoundsSize, localBounds, sphereRadius);
        }

        for (var index = 0; index < bones.Length; index++)
        {
            var parent = bones[index].ParentName;
            if (parent.Length == 0)
            {
                continue;
            }

            if (!names.TryGetValue(parent, out var parentIndex) || parentIndex == index)
            {
                throw new InvalidDataException($"{context} bone '{bones[index].Name}' has invalid parent '{parent}'.");
            }

            bones[index] = bones[index] with { ParentIndex = parentIndex };
        }

        for (var index = 0; index < bones.Length; index++)
        {
            _ = Ancestors(index, bones, context);
        }

        return bones;
    }

    private static ImmutableArray<float> ReadMatrix(KVObject parent, string key, string context)
    {
        var values = RequireArray(parent, key, context);
        if (values.Count != 12)
        {
            throw new InvalidDataException($"{context}.{key} must contain exactly 12 components.");
        }

        return values.Values.Select((value, index) =>
            RequireFiniteSingle(value, $"{context}.{key}[{index}]")).ToImmutableArray();
    }

    private static (TransformVector3 Center, TransformVector3 Size, GeometryBounds Bounds) ReadCenterSizeBounds(
        KVObject parent,
        string key,
        string context)
    {
        var bounds = RequireCollectionProperty(parent, key, context);
        var center = ReadVector(bounds, "m_vecCenter", context);
        var size = ReadVector(bounds, "m_vecSize", context);
        if (size.X < 0f || size.Y < 0f || size.Z < 0f)
        {
            throw new InvalidDataException($"{context}.{key} contains a negative size.");
        }

        var halfX = size.X * 0.5f;
        var halfY = size.Y * 0.5f;
        var halfZ = size.Z * 0.5f;
        return (
            center,
            size,
            new GeometryBounds(
                new TransformVector3 { X = center.X - halfX, Y = center.Y - halfY, Z = center.Z - halfZ },
                new TransformVector3 { X = center.X + halfX, Y = center.Y + halfY, Z = center.Z + halfZ }));
    }

    private static int FindDeepestCommonAncestor(Bone[] bones, IReadOnlySet<int> influencedIndices, string context)
    {
        HashSet<int>? common = null;
        foreach (var index in influencedIndices.Order())
        {
            var ancestors = Ancestors(index, bones, context).ToHashSet();
            if (common is null)
            {
                common = ancestors;
            }
            else
            {
                common.IntersectWith(ancestors);
            }
        }

        if (common is null || common.Count == 0)
        {
            throw new InvalidDataException($"{context} influenced bones have no common skeleton ancestor.");
        }

        return common.OrderByDescending(index => Ancestors(index, bones, context).Count)
            .ThenBy(index => index)
            .First();
    }

    private static List<int> Ancestors(int start, Bone[] bones, string context)
    {
        var result = new List<int>();
        var seen = new HashSet<int>();
        var current = start;
        while (current >= 0)
        {
            if (!seen.Add(current))
            {
                throw new InvalidDataException($"{context} skeleton contains a cycle at bone '{bones[current].Name}'.");
            }

            result.Add(current);
            current = bones[current].ParentIndex;
        }

        return result;
    }

    private static KVObject RequireLayoutField(KVObject layout, string semantic, uint format, string context)
    {
        var field = TryLayoutField(layout, semantic, context)
            ?? throw new InvalidDataException($"{context} {semantic} is not the supported packed per-vertex profile.");
        ValidatePackedPerVertexField(field, semantic, format, context);
        return field;
    }

    private static KVObject? TryLayoutField(KVObject layout, string semantic, string context)
    {
        var matches = layout.Values.Where(field =>
            field.IsCollection
            && TryString(field, "m_pSemanticName", out var name)
            && string.Equals(name, semantic, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidDataException($"{context} declares {matches.Length} {semantic} fields; at most one is supported."),
        };
    }

    private static void ValidatePackedPerVertexField(KVObject field, string semantic, uint format, string context)
    {
        if (RequireInt32(field, "m_nSemanticIndex", context) != 0
            || RequireUInt32(field, "m_Format", context) != format
            || RequireInt32(field, "m_nSlot", context) != 0
            || !string.Equals(RequireString(field, "m_nSlotType", context), "RENDER_SLOT_PER_VERTEX", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{context} {semantic} is not the supported packed per-vertex profile.");
        }
    }

    private static int ReadWeightCount(KVObject meshData, string context)
    {
        var skeleton = RequireCollectionProperty(meshData, "m_skeleton", context);
        return RequireInt32(skeleton, "m_nBoneWeightCount", context);
    }

    private static void AddInfluence(
        int boneIndex,
        int vertex,
        Bone[] bones,
        HashSet<int> influencedIndices,
        Dictionary<int, HashSet<int>> verticesByBone,
        string context)
    {
        if (boneIndex >= bones.Length)
        {
            throw new InvalidDataException($"{context} vertex {vertex} references out-of-range bone {boneIndex}.");
        }

        influencedIndices.Add(boneIndex);
        if (!verticesByBone.TryGetValue(boneIndex, out var influencedVertices))
        {
            influencedVertices = [];
            verticesByBone.Add(boneIndex, influencedVertices);
        }

        influencedVertices.Add(vertex);
    }

    private static int RequirePackedOffset(KVObject field, int stride, string semantic, string context)
    {
        var offset = RequireInt32(field, "m_nOffset", context);
        if (offset < 0 || offset > stride - PackedAttributeSize)
        {
            throw new InvalidDataException($"{context} {semantic} offset {offset} escapes stride {stride}.");
        }

        return offset;
    }

    private static bool RangesOverlap(int left, int right) =>
        left < right + PackedAttributeSize && right < left + PackedAttributeSize;

    private static void ValidateGridExtents(
        GeometryBounds bounds,
        int resolutionX,
        int resolutionY,
        int resolutionZ,
        float cellSize,
        string context,
        int fieldIndex)
    {
        var extents = new[]
        {
            (double)bounds.Max.X - bounds.Min.X,
            (double)bounds.Max.Y - bounds.Min.Y,
            (double)bounds.Max.Z - bounds.Min.Z,
        };
        var resolutions = new[] { resolutionX, resolutionY, resolutionZ };
        for (var axis = 0; axis < extents.Length; axis++)
        {
            var expected = resolutions[axis] * (double)cellSize;
            var relativeError = Math.Abs(extents[axis] - expected) / expected;
            if (relativeError > 0.001d)
            {
                throw new InvalidDataException(
                    $"{context} distance field {fieldIndex} axis {axis} extent is inconsistent with resolution and cell size.");
            }
        }
    }

    private static GeometryBounds ReadBounds(KVObject parent, string context)
    {
        var bounds = parent.ContainsKey("m_bounds")
            ? RequireCollectionProperty(parent, "m_bounds", context)
            : parent;
        return new GeometryBounds(
            ReadVector(bounds, "m_vMinBounds", context),
            ReadVector(bounds, "m_vMaxBounds", context));
    }

    private static TransformVector3 ReadVector(KVObject parent, string key, string context)
    {
        var values = RequireArray(parent, key, context);
        if (values.Count != 3)
        {
            throw new InvalidDataException($"{context}.{key} must contain exactly three components.");
        }

        return new TransformVector3
        {
            X = RequireFiniteSingle(values[0], $"{context}.{key}[0]"),
            Y = RequireFiniteSingle(values[1], $"{context}.{key}[1]"),
            Z = RequireFiniteSingle(values[2], $"{context}.{key}[2]"),
        };
    }

    private static GeometryBounds ToDomainBounds(Bounds3 bounds) => new(
        new TransformVector3 { X = bounds.Min.X, Y = bounds.Min.Y, Z = bounds.Min.Z },
        new TransformVector3 { X = bounds.Max.X, Y = bounds.Max.Y, Z = bounds.Max.Z });

    private static KVObject RequireArray(KVObject parent, string key, string context)
    {
        if (!parent.TryGetValue(key, out var value) || value is null || !value.IsArray)
        {
            throw new InvalidDataException($"Expected array '{context}.{key}'.");
        }

        return value;
    }

    private static KVObject RequireCollectionProperty(KVObject parent, string key, string context)
    {
        if (!parent.TryGetValue(key, out var value) || value is null)
        {
            throw new InvalidDataException($"Expected collection '{context}.{key}'.");
        }

        return RequireCollection(value, $"{context}.{key}");
    }

    private static KVObject RequireCollection(KVObject value, string context)
    {
        if (!value.IsCollection)
        {
            throw new InvalidDataException($"Expected collection '{context}'.");
        }

        return value;
    }

    private static string RequireString(KVObject parent, string key, string context)
    {
        if (!TryString(parent, key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Expected non-empty string '{context}.{key}'.");
        }

        return value;
    }

    private static string RequireStringAllowEmpty(KVObject parent, string key, string context)
    {
        if (!TryString(parent, key, out var value))
        {
            throw new InvalidDataException($"Expected string '{context}.{key}'.");
        }

        return value;
    }

    private static bool TryString(KVObject parent, string key, out string value)
    {
        if (parent.TryGetValue(key, out var item) && item is not null && item.ValueType == KVValueType.String)
        {
            value = item.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static int RequirePositiveInt32(KVObject parent, string key, string context)
    {
        var value = RequireInt32(parent, key, context);
        if (value < 1)
        {
            throw new InvalidDataException($"Expected positive integer '{context}.{key}'.");
        }

        return value;
    }

    private static int RequireInt32(KVObject parent, string key, string context)
    {
        var value = RequireScalar(parent, key, context);
        try
        {
            return checked((int)value.ToInt64(CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            throw new InvalidDataException($"Expected 32-bit integer '{context}.{key}'.", exception);
        }
    }

    private static uint RequireUInt32(KVObject parent, string key, string context)
    {
        var value = RequireScalar(parent, key, context);
        try
        {
            return value.ToUInt32(CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            throw new InvalidDataException($"Expected unsigned 32-bit integer '{context}.{key}'.", exception);
        }
    }

    private static bool RequireBoolean(KVObject parent, string key, string context)
    {
        var value = RequireScalar(parent, key, context);
        if (value.ValueType != KVValueType.Boolean)
        {
            throw new InvalidDataException($"Expected Boolean '{context}.{key}'.");
        }

        return value.ToBoolean(CultureInfo.InvariantCulture);
    }

    private static float RequirePositiveFiniteSingle(KVObject parent, string key, string context)
    {
        var value = RequireFiniteSingle(RequireScalar(parent, key, context), $"{context}.{key}");
        if (value <= 0)
        {
            throw new InvalidDataException($"Expected positive finite number '{context}.{key}'.");
        }

        return value;
    }

    private static float RequireNonNegativeFiniteSingle(KVObject parent, string key, string context)
    {
        var value = RequireFiniteSingle(parent, key, context);
        if (value < 0)
        {
            throw new InvalidDataException($"Expected non-negative finite number '{context}.{key}'.");
        }

        return value;
    }

    private static float RequireFiniteSingle(KVObject parent, string key, string context) =>
        RequireFiniteSingle(RequireScalar(parent, key, context), $"{context}.{key}");

    private static float RequireFiniteSingle(KVObject value, string context)
    {
        try
        {
            var result = value.ToSingle(CultureInfo.InvariantCulture);
            if (!float.IsFinite(result))
            {
                throw new InvalidDataException($"Expected finite number '{context}'.");
            }

            return result;
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            throw new InvalidDataException($"Expected finite single-precision number '{context}'.", exception);
        }
    }

    private static byte[] RequireBlob(KVObject parent, string key, string context)
    {
        var value = RequireScalar(parent, key, context);
        if (value.ValueType != KVValueType.BinaryBlob)
        {
            throw new InvalidDataException($"Expected binary blob '{context}.{key}'.");
        }

        return value.AsBlob();
    }

    private static KVObject RequireScalar(KVObject parent, string key, string context)
    {
        if (!parent.TryGetValue(key, out var value) || value is null || value.IsArray || value.IsCollection)
        {
            throw new InvalidDataException($"Expected scalar '{context}.{key}'.");
        }

        return value;
    }

    private sealed record Bone(
        int Index,
        string Name,
        string ParentName,
        int ParentIndex,
        ImmutableArray<float> InverseBindPose,
        TransformVector3 LocalBoundsCenter,
        TransformVector3 LocalBoundsSize,
        GeometryBounds LocalBounds,
        float SphereRadius);
}
