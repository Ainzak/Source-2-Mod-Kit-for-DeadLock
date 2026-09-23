using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Globalization;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;

namespace S2ModKit.Adapters.Source2;

internal static partial class Source2TransformMetadataAnalyzer
{
    public static Source2WholeMeshTransformAnalysis AnalyzeMultiBufferMesh(
        KVObject embeddedMeshDescriptor,
        KVObject meshData,
        Source2GeometryAnalysis geometry,
        string context)
    {
        ArgumentNullException.ThrowIfNull(embeddedMeshDescriptor);
        ArgumentNullException.ThrowIfNull(meshData);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        var buffers = geometry.VertexBuffers;
        var descriptors = RequireArray(embeddedMeshDescriptor, "m_vertexBuffers", context);
        if (buffers.Count < 2 || buffers.Count != geometry.IndexBuffers.Count
            || buffers.Count != descriptors.Count || geometry.DrawCalls.Count == 0)
        {
            throw new InvalidDataException($"{context} has an incomplete multi-buffer geometry inventory.");
        }

        var sceneObjects = RequireArray(meshData, "m_sceneObjects", context);
        if (sceneObjects.Count != 1)
        {
            throw new InvalidDataException($"{context} must have exactly one scene object.");
        }

        var scene = RequireCollection(sceneObjects[0], $"{context}.m_sceneObjects[0]");
        if (RequireArray(scene, "m_drawBounds", context).Count != 0)
        {
            throw new InvalidDataException($"{context} has unsupported per-draw bounds.");
        }

        var sceneBounds = ReadBounds(scene, context);
        var bones = ReadSkeleton(meshData, context, allowExtendedInventory: true, boneSizeIsHalfExtent: true);
        var declaredWeightCount = ReadWeightCount(meshData, context);
        if (declaredWeightCount is < 1 or > 8)
        {
            throw new InvalidDataException($"{context} declares unsupported bone weight count {declaredWeightCount}.");
        }

        var influencedIndices = new HashSet<int>();
        var verticesByBone = new Dictionary<int, HashSet<int>>();
        var allPositions = new List<Point3>();
        for (var bufferOrdinal = 0; bufferOrdinal < buffers.Count; bufferOrdinal++)
        {
            var buffer = buffers[bufferOrdinal];
            if (buffer.Snapshot.Ordinal != bufferOrdinal)
            {
                throw new InvalidDataException($"{context} vertex-buffer ordinals are not canonical.");
            }

            var descriptor = RequireCollection(descriptors[bufferOrdinal], $"{context}.m_vertexBuffers[{bufferOrdinal}]");
            var layout = RequireArray(descriptor, "m_inputLayoutFields", context);
            var blendIndices = TryLayoutField(layout, "BLENDINDICES", context)
                ?? throw new InvalidDataException($"{context} buffer {bufferOrdinal} has no BLENDINDICES field.");
            var indexFormat = RequireUInt32(blendIndices, "m_Format", context);
            if (indexFormat is not (R8G8B8A8Uint or 12u or 4u))
            {
                throw new InvalidDataException($"{context} buffer {bufferOrdinal} BLENDINDICES format {indexFormat} is unsupported.");
            }

            ValidatePackedPerVertexField(blendIndices, "BLENDINDICES", indexFormat, context);
            var indexWidth = indexFormat switch { 4u => 16, 12u => 8, _ => PackedAttributeSize };
            var indexOffset = RequireInt32(blendIndices, "m_nOffset", context);
            if (indexOffset < 0 || indexOffset > buffer.Snapshot.Stride - indexWidth)
            {
                throw new InvalidDataException($"{context} buffer {bufferOrdinal} BLENDINDICES exceeds stride.");
            }
            var blendWeights = TryLayoutField(layout, "BLENDWEIGHT", context);
            int? weightOffset = null;
            var weightWidth = 0;
            var weightFormat = 0u;
            if (blendWeights is not null)
            {
                weightFormat = RequireUInt32(blendWeights, "m_Format", context);
                if ((indexFormat == R8G8B8A8Uint && weightFormat != R8G8B8A8Unorm)
                    || (indexFormat is 12u or 4u && weightFormat != 11u))
                {
                    throw new InvalidDataException($"{context} buffer {bufferOrdinal} BLENDINDICES/BLENDWEIGHT formats {indexFormat}/{weightFormat} are unsupported together.");
                }

                ValidatePackedPerVertexField(blendWeights, "BLENDWEIGHT", weightFormat, context);
                weightWidth = weightFormat == 11u ? 8 : PackedAttributeSize;
                weightOffset = RequireInt32(blendWeights, "m_nOffset", context);
                if (weightOffset < 0 || weightOffset > buffer.Snapshot.Stride - weightWidth)
                {
                    throw new InvalidDataException($"{context} buffer {bufferOrdinal} BLENDWEIGHT exceeds stride.");
                }

                if (indexOffset < weightOffset.Value + weightWidth
                    && weightOffset.Value < indexOffset + indexWidth)
                {
                    throw new InvalidDataException($"{context} blend indices and weights overlap in buffer {bufferOrdinal}.");
                }
            }
            else if (indexFormat != R8G8B8A8Uint || declaredWeightCount != 1)
            {
                throw new InvalidDataException($"{context} rigid buffer {bufferOrdinal} does not declare one influence per vertex.");
            }

            var covered = geometry.DrawCalls
                .Where(call => call.Snapshot.VertexBufferOrdinal == bufferOrdinal)
                .SelectMany(call => call.VertexIndices)
                .Distinct()
                .Order()
                .ToArray();
            if (covered.Length != buffer.Snapshot.VertexCount
                || covered.Where((vertex, index) => vertex != index).Any())
            {
                throw new InvalidDataException($"{context} buffer {bufferOrdinal} has incomplete draw-call coverage.");
            }

            var baseIndex = allPositions.Count;
            for (var vertex = 0; vertex < buffer.Snapshot.VertexCount; vertex++)
            {
                allPositions.Add(Source2GeometryAnalyzer.ReadPosition(buffer, vertex));
                var recordOffset = checked(vertex * buffer.Snapshot.Stride);
                var indices = buffer.Decoded.AsSpan(recordOffset + indexOffset, indexWidth);
                var globalVertex = checked(baseIndex + vertex);
                if (weightOffset is null)
                {
                    var boneIndex = indexFormat == 4u ? BinaryPrimitives.ReadUInt16LittleEndian(indices) : indices[0];
                    AddInfluence(boneIndex, globalVertex, bones, influencedIndices, verticesByBone, context);
                    continue;
                }

                var weights = buffer.Decoded.AsSpan(recordOffset + weightOffset.Value, weightWidth);
                var physicalInfluences = weightFormat == 11u ? 8 : PackedAttributeSize;
                if (declaredWeightCount > physicalInfluences)
                {
                    throw new InvalidDataException($"{context} buffer {bufferOrdinal} has fewer blend slots than the declared weight count.");
                }

                var weightSum = 0;
                for (var influence = 0; influence < physicalInfluences; influence++)
                {
                    var weight = weights[influence];
                    if (influence >= declaredWeightCount && weight != 0)
                    {
                        throw new InvalidDataException($"{context} buffer {bufferOrdinal} vertex {vertex} uses an undeclared blend influence.");
                    }
                    weightSum = checked(weightSum + weight);
                    if (weight != 0)
                    {
                        var boneIndex = indexFormat == 4u
                            ? BinaryPrimitives.ReadUInt16LittleEndian(indices.Slice(influence * sizeof(ushort)))
                            : indices[influence];
                        AddInfluence(boneIndex, globalVertex, bones, influencedIndices, verticesByBone, context);
                    }
                }

                if (weightSum != byte.MaxValue)
                {
                    throw new InvalidDataException($"{context} buffer {bufferOrdinal} vertex {vertex} blend weights sum to {weightSum}.");
                }
            }
        }

        if (allPositions.Count == 0 || influencedIndices.Count == 0
            || sceneBounds != ToDomainBounds(Bounds3.FromPoints(allPositions)))
        {
            throw new InvalidDataException($"{context} multi-buffer scene bounds or skinning cannot be reproduced.");
        }

        var boneBounds = influencedIndices.Order()
            .Select(index => AnalyzeMultiBufferBoneBounds(
                bones[index], verticesByBone[index], allPositions, context))
            .ToImmutableArray();
        return new Source2WholeMeshTransformAnalysis(
            0,
            sceneBounds,
            new ContentHash(VertexSetHash.Compute(Enumerable.Range(0, allPositions.Count).ToArray())),
            allPositions.Count,
            string.Empty,
            0,
            influencedIndices.Order().Select(index => bones[index].Name).ToImmutableArray(),
            boneBounds);
    }

    private static Source2BoneBoundsAnalysis AnalyzeMultiBufferBoneBounds(
        Bone bone,
        HashSet<int> influencedVertices,
        List<Point3> allPositions,
        string context)
    {
        var indices = influencedVertices.Order().ToArray();
        var localPoints = indices
            .Select(vertex => TransformPoint(allPositions[vertex], bone.InverseBindPose))
            .ToArray();
        var radius = localPoints.Max(point => MathF.Sqrt(
            (point.X * point.X) + (point.Y * point.Y) + (point.Z * point.Z)));
        if (!float.IsFinite(radius))
        {
            throw new InvalidDataException(
                $"{context} bone '{bone.Name}' multi-buffer culling sphere differs from its influenced vertices " +
                $"(computed {radius.ToString("R", CultureInfo.InvariantCulture)}, stored {bone.SphereRadius.ToString("R", CultureInfo.InvariantCulture)}).");
        }

        return new Source2BoneBoundsAnalysis(
            bone.Index,
            bone.Name,
            HashMatrix(bone.InverseBindPose),
            bone.InverseBindPose,
            indices.ToImmutableArray(),
            bone.LocalBoundsCenter,
            bone.LocalBoundsSize,
            bone.LocalBounds,
            bone.SphereRadius);
    }
}
