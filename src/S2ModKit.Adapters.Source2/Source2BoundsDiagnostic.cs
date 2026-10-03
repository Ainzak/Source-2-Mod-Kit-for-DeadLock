using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;

namespace S2ModKit.Adapters.Source2;

public sealed record Source2BoundsDiagnosticReport(
    int SchemaVersion,
    string ResourcePath,
    string InputSha256,
    string GeometryCodecIdentity,
    ImmutableArray<Source2BoundsDiagnosticMesh> Meshes,
    CullingInventoryDocument CullingInventory);

public sealed record Source2BoundsDiagnosticMesh(
    int Lod,
    int MeshOrdinal,
    int ResourceBlockIndex,
    ulong LodMask,
    string Status,
    string? Failure,
    int DeclaredInfluenceCount,
    string BoneRemapSource,
    string BoneRemapStatus,
    string ModelBoneNamesStatus,
    ImmutableArray<string>? ModelBoneNames,
    ImmutableArray<int> BoneRemap,
    ContentHash? BoneRemapIdentity,
    ImmutableArray<Source2BoundsDiagnosticBuffer> Buffers,
    ImmutableArray<Source2BoundsDiagnosticRootBoneContributor> RootBoneContributors,
    ImmutableArray<Source2BoundsDiagnosticBone> Bones);

public sealed record Source2BoundsDiagnosticBuffer(
    int VertexBufferOrdinal,
    int IndexBufferOrdinal,
    int VertexResourceBlockIndex,
    int IndexResourceBlockIndex,
    int VertexCount,
    int IndexCount,
    int Stride,
    uint BlendIndexFormat,
    int BlendIndexOffset,
    uint? BlendWeightFormat,
    int? BlendWeightOffset,
    ContentHash VertexBlockHash,
    ContentHash DecodedVertexBufferHash,
    ContentHash IndexBlockHash,
    ContentHash DecodedIndexBufferHash,
    ImmutableArray<Source2InfluenceCount> InfluenceCounts);

public sealed record Source2InfluenceCount(int ActiveInfluenceCount, int VertexCount);

public sealed record Source2BoundsDiagnosticBone(
    int BoneIndex,
    string BoneName,
    int? ResolvedModelBoneIndex,
    string? ResolvedModelBoneName,
    int InfluencedVertexCount,
    GeometryBounds StoredBounds,
    GeometryBounds CalculatedBounds,
    bool StoredBoundsContainAllVertices,
    bool BoundsExactlyEqual,
    bool BoundsMatchExistingAffineTolerance,
    float StoredSphereRadius,
    float CalculatedRequiredSphereRadius,
    float SphereDelta,
    bool StoredSphereContainsAllVertices,
    bool SphereRadiusExactlyEqual,
    bool SphereMatchesExistingAffineTolerance,
    TransformVector3 RawBoundsCenter,
    TransformVector3 RawBoundsHalfExtents,
    ImmutableArray<float> InverseBindPose,
    ContentHash InverseBindPoseIdentity,
    ContentHash ContributorSetIdentity,
    ImmutableArray<Source2BoundsDiagnosticBufferContribution> ContributorBuffers,
    Source2BoundsDiagnosticVertex? SphereExtremalVertex,
    ImmutableArray<Source2BoundsDiagnosticExtremum> Extrema);

public sealed record Source2BoundsDiagnosticRootBoneContributor(
    int ModelBoneIndex,
    string? ModelBoneName,
    int ContributorCount,
    ContentHash ContributorSetIdentity,
    ImmutableArray<Source2BoundsDiagnosticBufferContribution> ContributorBuffers);

public sealed record Source2BoundsDiagnosticBufferContribution(int VertexBufferOrdinal, int ContributorCount);

public sealed record Source2BoundsDiagnosticExtremum(
    string Axis,
    string Side,
    float StoredValue,
    float CalculatedValue,
    float Delta,
    bool StoredContainsExtremum,
    bool MatchesExistingAffineTolerance,
    Source2BoundsDiagnosticVertex Vertex);

public sealed record Source2BoundsDiagnosticVertex(
    int VertexBufferOrdinal,
    int VertexOrdinal,
    uint BlendIndexFormat,
    ImmutableArray<int> RawBlendIndices,
    ImmutableArray<int> RawBlendWeights,
    int ActiveInfluenceCount,
    int MeshBoneIndex,
    string MeshBoneName,
    int? ResolvedBoneIndex,
    string? BoneName,
    TransformVector3 OriginalPosition,
    TransformVector3 CalculatedBoneLocalPosition,
    ImmutableArray<Source2BoundsDiagnosticInfluence> ResolvedInfluences);

public sealed record Source2BoundsDiagnosticInfluence(
    int Slot,
    int RawBlendIndex,
    int RawBlendWeight,
    int MeshBoneIndex,
    string MeshBoneName,
    int? ResolvedBoneIndex,
    string? BoneName);

internal sealed record ModelBoneNameInventory(string Status, ImmutableArray<string> Names);

internal static partial class Source2TransformMetadataAnalyzer
{
    private const uint R16G16B16A16Uint = 4;
    private const uint R8G8B8A8Unorm8Slots = 11;
    private const uint R8G8B8A8Uint8Slots = 12;

    public static Source2BoundsDiagnosticMesh DiagnoseBounds(
        int lod,
        int meshOrdinal,
        int resourceBlockIndex,
        ulong lodMask,
        KVObject embeddedMeshDescriptor,
        KVObject meshData,
        KVObject modelData,
        Source2GeometryAnalysis geometry,
        int[]? boneRemapTable,
        string boneRemapSource,
        string context)
    {
        ArgumentNullException.ThrowIfNull(embeddedMeshDescriptor);
        ArgumentNullException.ThrowIfNull(meshData);
        ArgumentNullException.ThrowIfNull(modelData);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        try
        {
            if (geometry.VertexBuffers.Count == 0
                || geometry.VertexBuffers.Count != geometry.IndexBuffers.Count)
            {
                throw new InvalidDataException($"{context} has no complete participating buffer pairs.");
            }

            var vertexDescriptors = RequireArray(embeddedMeshDescriptor, "m_vertexBuffers", context);
            if (vertexDescriptors.Count != geometry.VertexBuffers.Count)
            {
                throw new InvalidDataException($"{context} vertex descriptor count does not match decoded buffers.");
            }

            var bones = ReadSkeleton(meshData, context, allowExtendedInventory: true, boneSizeIsHalfExtent: true);
            var declaredInfluenceCount = ReadWeightCount(meshData, context);
            var modelBoneNameInventory = ReadModelBoneNames(modelData, context);
            var modelBoneNames = modelBoneNameInventory.Names;
            var remapStatus = boneRemapTable is null ? "absent" : "verified";
            // VRF's table maps raw mesh BLENDINDICES to model-skeleton indices. Its serialized
            // length can exceed the render m_bones[] count; used indices are checked at lookup.
            if (boneRemapTable is not null
                && (boneRemapTable.Any(index => index < 0)
                    || (modelBoneNameInventory.Status == "verified" && boneRemapTable.Any(index => index >= modelBoneNames.Length))))
            {
                throw new InvalidDataException($"{context} has a malformed mesh-to-model bone remap table.");
            }

            ContentHash? boneRemapIdentity = boneRemapTable is null ? null : HashIntegerSequence(boneRemapTable);
            var verticesByBone = bones.ToDictionary(bone => bone.Index, _ => new List<DiagnosticSourceVertex>());
            var verticesByModelBone = new Dictionary<int, List<DiagnosticSourceVertex>>();
            var buffers = ImmutableArray.CreateBuilder<Source2BoundsDiagnosticBuffer>(geometry.VertexBuffers.Count);

            for (var bufferOrdinal = 0; bufferOrdinal < geometry.VertexBuffers.Count; bufferOrdinal++)
            {
                var vertexBuffer = geometry.VertexBuffers[bufferOrdinal];
                var indexBuffer = geometry.IndexBuffers[bufferOrdinal];
                var descriptor = RequireCollection(vertexDescriptors[bufferOrdinal], $"{context}.m_vertexBuffers[{bufferOrdinal}]");
                var layout = RequireArray(descriptor, "m_inputLayoutFields", context);
                var indexField = TryLayoutField(layout, "BLENDINDICES", context);
                if (indexField is null)
                {
                    throw new InvalidDataException($"{context} buffer {bufferOrdinal} has no BLENDINDICES field.");
                }

                var indexFormat = RequireUInt32(indexField, "m_Format", context);
                var indexWidth = indexFormat switch
                {
                    R8G8B8A8Uint => 4,
                    R16G16B16A16Sint => 8,
                    R16G16B16A16Uint => 16,
                    R8G8B8A8Uint8Slots => 8,
                    _ => throw new InvalidDataException($"{context} buffer {bufferOrdinal} blend-index format {indexFormat} is not characterized for diagnostics."),
                };
                ValidatePackedPerVertexField(indexField, "BLENDINDICES", indexFormat, context);
                var indexOffset = RequireInt32(indexField, "m_nOffset", context);
                if (indexOffset < 0 || indexOffset > vertexBuffer.Snapshot.Stride - indexWidth)
                {
                    throw new InvalidDataException($"{context} buffer {bufferOrdinal} blend indices escape the vertex stride.");
                }

                var weightField = TryLayoutField(layout, "BLENDWEIGHT", context);
                int? weightOffset = null;
                uint? weightFormat = null;
                var weightWidth = 0;
                var weightSlots = 0;
                if (weightField is not null)
                {
                    weightFormat = RequireUInt32(weightField, "m_Format", context);
                    weightSlots = weightFormat.Value switch
                    {
                        R8G8B8A8Unorm => 4,
                        R8G8B8A8Unorm8Slots => 8,
                        _ => throw new InvalidDataException($"{context} buffer {bufferOrdinal} blend-weight format {weightFormat} is not characterized for diagnostics."),
                    };
                    weightWidth = weightSlots;
                    ValidatePackedPerVertexField(weightField, "BLENDWEIGHT", weightFormat.Value, context);
                    weightOffset = RequireInt32(weightField, "m_nOffset", context);
                    if (weightOffset < 0 || weightOffset > vertexBuffer.Snapshot.Stride - weightWidth
                        || RangesOverlap(indexOffset, indexWidth, weightOffset.Value, weightWidth))
                    {
                        throw new InvalidDataException($"{context} buffer {bufferOrdinal} blend fields overlap or escape the vertex stride.");
                    }
                }
                else
                {
                    weightSlots = 1;
                    if (declaredInfluenceCount != 1)
                    {
                        throw new InvalidDataException($"{context} buffer {bufferOrdinal} has no blend weights but declares {declaredInfluenceCount} influences.");
                    }
                }

                var observedCounts = new Dictionary<int, int>();
                for (var vertex = 0; vertex < vertexBuffer.Snapshot.VertexCount; vertex++)
                {
                    var recordOffset = checked(vertex * vertexBuffer.Snapshot.Stride);
                    var rawIndices = ReadRawBlendIndices(
                        vertexBuffer.Decoded.AsSpan(recordOffset + indexOffset, indexWidth), indexFormat);
                    var rawWeights = weightOffset is null
                        ? ImmutableArray<int>.Empty
                        : vertexBuffer.Decoded.AsSpan(recordOffset + weightOffset.Value, weightWidth)
                            .ToArray().Select(value => (int)value).ToImmutableArray();
                    var activeSlots = weightOffset is null
                        ? [0]
                        : Enumerable.Range(0, Math.Min(weightSlots, rawWeights.Length))
                            .Where(slot => rawWeights[slot] != 0).ToArray();
                    observedCounts[activeSlots.Length] = observedCounts.GetValueOrDefault(activeSlots.Length) + 1;
                    var influences = ImmutableArray.CreateBuilder<Source2BoundsDiagnosticInfluence>(activeSlots.Length);
                    foreach (var slot in activeSlots)
                    {
                        var rawBone = rawIndices[slot];
                        if (rawBone < 0 || rawBone >= bones.Length)
                        {
                            throw new InvalidDataException(
                                $"{context} buffer {bufferOrdinal} vertex {vertex} slot {slot} references out-of-range mesh bone {rawBone}.");
                        }

                        var resolvedBone = ResolveModelBoneIndex(rawBone, boneRemapTable);
                        if (boneRemapTable is not null && resolvedBone is null)
                        {
                            throw new InvalidDataException(
                                $"{context} active blend index {rawBone} has no entry in the mesh-to-model bone remap table.");
                        }

                        var meshBone = bones[rawBone];
                        var resolvedName = resolvedBone is { } resolvedIndex
                            ? ResolveModelBoneName(resolvedIndex, modelBoneNames)
                            : null;

                        influences.Add(new Source2BoundsDiagnosticInfluence(
                            slot,
                            rawBone,
                            weightOffset is null ? byte.MaxValue : rawWeights[slot],
                            rawBone,
                            meshBone.Name,
                            resolvedBone,
                            resolvedName));
                    }

                    if (influences.Count == 0)
                    {
                        throw new InvalidDataException($"{context} buffer {bufferOrdinal} vertex {vertex} has no non-zero blend influence.");
                    }

                    var source = new DiagnosticSourceVertex(
                        bufferOrdinal,
                        vertex,
                        indexFormat,
                        rawIndices,
                        rawWeights,
                        activeSlots.Length,
                        Source2GeometryAnalyzer.ReadPosition(vertexBuffer, vertex),
                        influences.ToImmutable());
                    foreach (var boneIndex in source.Influences.Select(item => item.MeshBoneIndex).Distinct())
                    {
                        verticesByBone[boneIndex].Add(source);
                    }

                    foreach (var modelBoneIndex in source.Influences
                                 .Where(item => item.ResolvedBoneIndex is not null)
                                 .Select(item => item.ResolvedBoneIndex!.Value)
                                 .Distinct())
                    {
                        if (!verticesByModelBone.TryGetValue(modelBoneIndex, out var rootBoneVertices))
                        {
                            rootBoneVertices = [];
                            verticesByModelBone.Add(modelBoneIndex, rootBoneVertices);
                        }

                        rootBoneVertices.Add(source);
                    }
                }

                buffers.Add(new Source2BoundsDiagnosticBuffer(
                    bufferOrdinal,
                    bufferOrdinal,
                    vertexBuffer.Snapshot.ResourceBlockIndex,
                    indexBuffer.Snapshot.ResourceBlockIndex,
                    vertexBuffer.Snapshot.VertexCount,
                    indexBuffer.Snapshot.IndexCount,
                    vertexBuffer.Snapshot.Stride,
                    indexFormat,
                    indexOffset,
                    weightFormat,
                    weightOffset,
                    vertexBuffer.Snapshot.EncodedHash,
                    vertexBuffer.Snapshot.DecodedHash,
                    indexBuffer.Snapshot.EncodedHash,
                    indexBuffer.Snapshot.DecodedHash,
                    observedCounts.OrderBy(item => item.Key)
                        .Select(item => new Source2InfluenceCount(item.Key, item.Value)).ToImmutableArray()));
            }

            var diagnosticBones = ImmutableArray.CreateBuilder<Source2BoundsDiagnosticBone>();
            foreach (var bone in bones)
            {
                var influenced = verticesByBone[bone.Index];
                if (influenced.Count == 0)
                {
                    continue;
                }

                var local = influenced.Select(vertex => new DiagnosticLocalVertex(
                    vertex,
                    TransformPoint(vertex.Position, bone.InverseBindPose))).ToArray();
                var calculatedBounds = ToDomainBounds(Bounds3.FromPoints(local.Select(item => item.LocalPosition).ToArray()));
                var sphereVertex = local.OrderByDescending(item => Radius(item.LocalPosition))
                    .ThenBy(item => item.Vertex.BufferOrdinal)
                    .ThenBy(item => item.Vertex.VertexOrdinal)
                    .First();
                var requiredRadius = Radius(sphereVertex.LocalPosition);
                var extrema = ImmutableArray.CreateBuilder<Source2BoundsDiagnosticExtremum>(6);
                AddExtrema(extrema, local, "x", calculatedBounds.Min.X, calculatedBounds.Max.X, bone.LocalBounds.Min.X, bone.LocalBounds.Max.X, bone);
                AddExtrema(extrema, local, "y", calculatedBounds.Min.Y, calculatedBounds.Max.Y, bone.LocalBounds.Min.Y, bone.LocalBounds.Max.Y, bone);
                AddExtrema(extrema, local, "z", calculatedBounds.Min.Z, calculatedBounds.Max.Z, bone.LocalBounds.Min.Z, bone.LocalBounds.Max.Z, bone);
                var sphereTolerance = MathF.Max(0.0001f, MathF.Max(requiredRadius, bone.SphereRadius) * 0.00001f);
                diagnosticBones.Add(new Source2BoundsDiagnosticBone(
                    bone.Index,
                    bone.Name,
                    ResolveModelBoneIndex(bone.Index, boneRemapTable),
                    ResolveModelBoneName(ResolveModelBoneIndex(bone.Index, boneRemapTable), modelBoneNames),
                    influenced.Count,
                    bone.LocalBounds,
                    calculatedBounds,
                    Contains(bone.LocalBounds, calculatedBounds),
                    bone.LocalBounds == calculatedBounds,
                    BoundsMatchExistingAffineTolerance(bone.LocalBounds, calculatedBounds),
                    bone.SphereRadius,
                    requiredRadius,
                    requiredRadius - bone.SphereRadius,
                    bone.SphereRadius >= requiredRadius,
                    BitConverter.SingleToInt32Bits(bone.SphereRadius) == BitConverter.SingleToInt32Bits(requiredRadius),
                    MathF.Abs(requiredRadius - bone.SphereRadius) <= sphereTolerance,
                    bone.LocalBoundsCenter,
                    bone.LocalBoundsSize,
                    bone.InverseBindPose,
                    HashFloatSequence(bone.InverseBindPose),
                    HashDiagnosticContributors(bone.Index, bone.InverseBindPose, influenced),
                    influenced.GroupBy(item => item.BufferOrdinal)
                        .OrderBy(group => group.Key)
                        .Select(group => new Source2BoundsDiagnosticBufferContribution(group.Key, group.Count()))
                        .ToImmutableArray(),
                    CreateVertex(bone, sphereVertex.Vertex, sphereVertex.LocalPosition),
                    extrema.ToImmutable()));
            }

            var rootBoneContributors = verticesByModelBone.OrderBy(item => item.Key)
                .Select(item => new Source2BoundsDiagnosticRootBoneContributor(
                    item.Key,
                    ResolveModelBoneName(item.Key, modelBoneNames),
                    item.Value.Count,
                    HashDiagnosticContributors(item.Key, [], item.Value),
                    item.Value.GroupBy(vertex => vertex.BufferOrdinal)
                        .OrderBy(group => group.Key)
                        .Select(group => new Source2BoundsDiagnosticBufferContribution(group.Key, group.Count()))
                        .ToImmutableArray()))
                .ToImmutableArray();

            return new Source2BoundsDiagnosticMesh(
                lod,
                meshOrdinal,
                resourceBlockIndex,
                lodMask,
                "analyzed",
                null,
                declaredInfluenceCount,
                boneRemapSource,
                remapStatus,
                modelBoneNameInventory.Status,
                modelBoneNameInventory.Status == "verified" ? modelBoneNames : null,
                boneRemapTable?.ToImmutableArray() ?? [],
                boneRemapIdentity,
                buffers.ToImmutable(),
                rootBoneContributors,
                diagnosticBones.ToImmutable());
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException or ArgumentException or IndexOutOfRangeException)
        {
            return new Source2BoundsDiagnosticMesh(
                lod,
                meshOrdinal,
                resourceBlockIndex,
                lodMask,
                "unsupported",
                exception.Message,
                0,
                "unresolved",
                "unsupported",
                "unsupported",
                null,
                [],
                null,
                [],
                [],
                []);
        }
    }

    private static void AddExtrema(
        ImmutableArray<Source2BoundsDiagnosticExtremum>.Builder target,
        DiagnosticLocalVertex[] vertices,
        string axis,
        float calculatedMin,
        float calculatedMax,
        float storedMin,
        float storedMax,
        Bone bone)
    {
        foreach (var (side, calculated, stored, selector) in new (string, float, float, Func<Point3, float>)[]
        {
            ("min", calculatedMin, storedMin, point => Axis(point, axis)),
            ("max", calculatedMax, storedMax, point => Axis(point, axis)),
        })
        {
            var extremal = (side == "min"
                    ? vertices.OrderBy(item => selector(item.LocalPosition))
                    : vertices.OrderByDescending(item => selector(item.LocalPosition)))
                .ThenBy(item => item.Vertex.BufferOrdinal)
                .ThenBy(item => item.Vertex.VertexOrdinal)
                .First();
            target.Add(new Source2BoundsDiagnosticExtremum(
                axis,
                side,
                stored,
                calculated,
                calculated - stored,
                side == "min" ? stored <= calculated : stored >= calculated,
                NearAffineValue(calculated, stored),
                CreateVertex(bone, extremal.Vertex, extremal.LocalPosition)));
        }
    }

    private static Source2BoundsDiagnosticVertex CreateVertex(Bone bone, DiagnosticSourceVertex source, Point3 localPosition)
    {
        var responsible = source.Influences.FirstOrDefault(item => item.MeshBoneIndex == bone.Index);
        return new Source2BoundsDiagnosticVertex(
            source.BufferOrdinal,
            source.VertexOrdinal,
            source.BlendIndexFormat,
            source.RawBlendIndices,
            source.RawBlendWeights,
            source.ActiveInfluenceCount,
            responsible?.MeshBoneIndex ?? bone.Index,
            responsible?.MeshBoneName ?? bone.Name,
            responsible?.ResolvedBoneIndex,
            responsible?.BoneName,
            ToVector(source.Position),
            ToVector(localPosition),
            source.Influences);
    }

    private static ImmutableArray<int> ReadRawBlendIndices(ReadOnlySpan<byte> bytes, uint format)
    {
        if (format is R8G8B8A8Uint or R8G8B8A8Uint8Slots)
        {
            return bytes.ToArray().Select(value => (int)value).ToImmutableArray();
        }

        var width = format switch
        {
            R16G16B16A16Sint or R16G16B16A16Uint => sizeof(ushort),
            _ => throw new InvalidDataException($"Blend-index format {format} is not supported by the bounds diagnostic."),
        };
        var values = ImmutableArray.CreateBuilder<int>(bytes.Length / width);
        for (var slot = 0; slot < bytes.Length / width; slot++)
        {
            var pair = bytes.Slice(slot * width, width);
            values.Add(format == R16G16B16A16Sint
                ? System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pair)
                : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(pair));
        }

        return values.ToImmutable();
    }

    private static ModelBoneNameInventory ReadModelBoneNames(KVObject modelData, string context)
    {
        if (!modelData.TryGetValue("m_modelSkeleton", out var skeleton))
        {
            return new ModelBoneNameInventory("absent", []);
        }

        if (skeleton is null || !skeleton.IsCollection)
        {
            throw new InvalidDataException($"{context}.m_modelSkeleton exists but is not a collection.");
        }

        if (!skeleton.TryGetValue("m_boneName", out var boneValues))
        {
            return new ModelBoneNameInventory("absent", []);
        }

        if (boneValues is null || !boneValues.IsArray || boneValues.Count == 0)
        {
            throw new InvalidDataException($"{context}.m_modelSkeleton.m_boneName exists but is not a non-empty array.");
        }

        var names = ImmutableArray.CreateBuilder<string>(boneValues.Count);
        for (var index = 0; index < boneValues.Count; index++)
        {
            var bone = boneValues[index];
            if (bone is null || bone.ValueType != KVValueType.String || string.IsNullOrWhiteSpace((string)bone))
            {
                throw new InvalidDataException($"{context}.m_modelSkeleton.m_boneName[{index}] is not a non-empty string.");
            }

            names.Add((string)bone);
        }

        return new ModelBoneNameInventory("verified", names.ToImmutable());
    }

    private static int? ResolveModelBoneIndex(int meshBoneIndex, int[]? remap) =>
        remap is null ? null
            : meshBoneIndex >= 0 && meshBoneIndex < remap.Length ? remap[meshBoneIndex] : null;

    private static string? ResolveModelBoneName(int? modelBoneIndex, ImmutableArray<string> names) =>
        modelBoneIndex is { } index && index >= 0 && index < names.Length ? names[index] : null;

    private static ContentHash HashIntegerSequence(IEnumerable<int> values) =>
        ContentHash.Compute(values.SelectMany(value => BitConverter.GetBytes(value)).ToArray());

    private static ContentHash HashFloatSequence(IEnumerable<float> values) =>
        ContentHash.Compute(values.SelectMany(value => BitConverter.GetBytes(BitConverter.SingleToInt32Bits(value))).ToArray());

    private static ContentHash HashDiagnosticContributors(
        int targetBoneIndex,
        ImmutableArray<float> matrix,
        IEnumerable<DiagnosticSourceVertex> sourceVertices)
    {
        var canonical = new StringBuilder();
        canonical.Append("culling-contributors-v1|")
            .Append(targetBoneIndex.ToString(CultureInfo.InvariantCulture)).Append('|');
        foreach (var value in matrix)
        {
            canonical.Append(BitConverter.SingleToInt32Bits(value).ToString("x8", CultureInfo.InvariantCulture)).Append(',');
        }

        canonical.Append('\n');
        foreach (var vertex in sourceVertices.OrderBy(item => item.BufferOrdinal).ThenBy(item => item.VertexOrdinal))
        {
            canonical.Append(vertex.BufferOrdinal.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(vertex.VertexOrdinal.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(vertex.BlendIndexFormat.ToString(CultureInfo.InvariantCulture)).Append('|');
            AppendIntegers(canonical, vertex.RawBlendIndices);
            canonical.Append('|');
            AppendIntegers(canonical, vertex.RawBlendWeights);
            canonical.Append('|').Append(BitConverter.SingleToInt32Bits(vertex.Position.X).ToString("x8", CultureInfo.InvariantCulture))
                .Append(',').Append(BitConverter.SingleToInt32Bits(vertex.Position.Y).ToString("x8", CultureInfo.InvariantCulture))
                .Append(',').Append(BitConverter.SingleToInt32Bits(vertex.Position.Z).ToString("x8", CultureInfo.InvariantCulture))
                .Append('|');
            foreach (var influence in vertex.Influences.OrderBy(item => item.Slot))
            {
                canonical.Append(influence.Slot.ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(influence.RawBlendIndex.ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(influence.RawBlendWeight.ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(influence.ResolvedBoneIndex?.ToString(CultureInfo.InvariantCulture) ?? "absent").Append(',');
            }

            canonical.Append('\n');
        }

        return ContentHash.Compute(Encoding.UTF8.GetBytes(canonical.ToString()));
    }

    private static void AppendIntegers(StringBuilder target, IEnumerable<int> values)
    {
        foreach (var value in values)
        {
            target.Append(value.ToString(CultureInfo.InvariantCulture)).Append(',');
        }
    }

    private static bool Contains(GeometryBounds outer, GeometryBounds inner) =>
        outer.Min.X <= inner.Min.X && outer.Min.Y <= inner.Min.Y && outer.Min.Z <= inner.Min.Z
        && outer.Max.X >= inner.Max.X && outer.Max.Y >= inner.Max.Y && outer.Max.Z >= inner.Max.Z;

    private static bool BoundsMatchExistingAffineTolerance(GeometryBounds stored, GeometryBounds calculated) =>
        NearAffineValue(stored.Min.X, calculated.Min.X) && NearAffineValue(stored.Min.Y, calculated.Min.Y)
        && NearAffineValue(stored.Min.Z, calculated.Min.Z) && NearAffineValue(stored.Max.X, calculated.Max.X)
        && NearAffineValue(stored.Max.Y, calculated.Max.Y) && NearAffineValue(stored.Max.Z, calculated.Max.Z);

    private static bool NearAffineValue(float left, float right) => MathF.Abs(left - right) <=
        MathF.Max(0.00001f, MathF.Max(MathF.Abs(left), MathF.Abs(right)) * 0.00001f);

    private static float Axis(Point3 point, string axis) => axis switch
    {
        "x" => point.X,
        "y" => point.Y,
        _ => point.Z,
    };

    private static float Radius(Point3 point) => MathF.Sqrt(
        (point.X * point.X) + (point.Y * point.Y) + (point.Z * point.Z));

    private static TransformVector3 ToVector(Point3 point) => new() { X = point.X, Y = point.Y, Z = point.Z };

    private sealed record DiagnosticSourceVertex(
        int BufferOrdinal,
        int VertexOrdinal,
        uint BlendIndexFormat,
        ImmutableArray<int> RawBlendIndices,
        ImmutableArray<int> RawBlendWeights,
        int ActiveInfluenceCount,
        Point3 Position,
        ImmutableArray<Source2BoundsDiagnosticInfluence> Influences);

    private sealed record DiagnosticLocalVertex(DiagnosticSourceVertex Vertex, Point3 LocalPosition);
}

public sealed partial class Source2CompiledModelAdapter
{
    public Source2BoundsDiagnosticReport DiagnoseBounds(ArtifactContent artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (GeometryCodecCapability.Status != "ready")
        {
            throw Errors.Unsupported(
                "BOUNDS_DIAGNOSTIC_CODEC_UNAVAILABLE",
                GeometryCodecCapability.Summary,
                "Configure the already-qualified meshoptimizer library and rerun the read-only diagnostic.");
        }

        using var parsed = Parse(artifact, retainGeometryAnalysis: true);
        return DiagnoseBounds(artifact, parsed);
    }

    private Source2BoundsDiagnosticReport DiagnoseBounds(ArtifactContent artifact, ParsedModel parsed)
    {
        var results = ImmutableArray.CreateBuilder<Source2BoundsDiagnosticMesh>();
        var model = parsed.Resource.Blocks.OfType<ValveResourceFormat.ResourceTypes.Model>().Single();
        var modelData = model.Data;
        foreach (var mesh in parsed.MeshesByOrdinal.Values.OrderBy(item => item.MeshOrdinal))
        {
            if (mesh.GeometryAnalysis is null)
            {
                foreach (var lod in ExpandRootLodMask(mesh.LodMask, parsed.Snapshot.Lods.Count, mesh.MeshOrdinal))
                {
                    results.Add(new Source2BoundsDiagnosticMesh(
                        lod,
                        mesh.MeshOrdinal,
                        mesh.BlockIndex,
                        mesh.LodMask,
                        "unsupported",
                        mesh.Geometry.Summary,
                        0,
                        "unresolved",
                        "unsupported",
                        "unsupported",
                        null,
                        [],
                        null,
                        [],
                        [],
                        []));
                }

                continue;
            }

            var boneRemapTable = model.GetRemapTable(mesh.MeshOrdinal);
            foreach (var lod in ExpandRootLodMask(mesh.LodMask, parsed.Snapshot.Lods.Count, mesh.MeshOrdinal))
            {
                results.Add(Source2TransformMetadataAnalyzer.DiagnoseBounds(
                    lod,
                    mesh.MeshOrdinal,
                    mesh.BlockIndex,
                    mesh.LodMask,
                    mesh.Descriptor,
                    mesh.Block.Data,
                    modelData,
                    mesh.GeometryAnalysis,
                    boneRemapTable,
                    boneRemapTable is null
                        ? $"VRF Model.GetRemapTable({mesh.MeshOrdinal}) returned null; remap absent, model index unresolved"
                        : $"VRF Model.GetRemapTable({mesh.MeshOrdinal})",
                    $"mesh {mesh.MeshOrdinal} LOD {lod}"));
            }
        }

        var meshRows = results.ToImmutable();
        var meshInputs = parsed.MeshesByOrdinal.Values.OrderBy(item => item.MeshOrdinal)
            .Select(mesh => new Source2CullingInventoryMeshInput(
                mesh.MeshOrdinal,
                mesh.BlockIndex,
                mesh.LodMask,
                mesh.Block.Data,
                mesh.GeometryAnalysis,
                meshRows.FirstOrDefault(row => row.MeshOrdinal == mesh.MeshOrdinal)))
            .ToArray();
        var modelDataBlockIndex = parsed.Resource.Blocks.Select((block, index) => (block, index))
            .Single(item => item.block is ValveResourceFormat.ResourceTypes.Model).index;
        var inventory = Source2CullingInventoryBuilder.Build(
            artifact,
            modelDataBlockIndex,
            modelData,
            parsed.Snapshot.Lods.Select(lod => lod.Level).ToArray(),
            meshInputs);

        return new Source2BoundsDiagnosticReport(
            2,
            artifact.LogicalPath,
            artifact.ContentHash.Value,
            GeometryCodecCapability.Identity?.ToString() ?? "unavailable",
            meshRows,
            inventory);
    }
}
