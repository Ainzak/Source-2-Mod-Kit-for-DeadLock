using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;

namespace S2ModKit.Adapters.Source2;

internal sealed record GeometryDrawCallInput(
    DrawCallSnapshot Snapshot,
    KVObject Data);

internal sealed record Source2VertexBufferAnalysis(
    VertexBufferSnapshot Snapshot,
    byte[] Decoded)
{
    public PackedFrameLayout? PackedFrameLayout { get; init; }
}

internal sealed record Source2IndexBufferAnalysis(
    IndexBufferSnapshot Snapshot,
    uint[] Indices);

internal sealed record Source2DrawCallAnalysis(
    DrawCallGeometrySnapshot Snapshot,
    ImmutableArray<int> VertexIndices);

internal sealed record Source2ConnectedComponentAnalysis(
    ConnectedComponentSnapshot Snapshot,
    ImmutableArray<int> VertexIndices);

internal sealed record Source2GeometryAnalysis(
    MeshGeometrySnapshot Snapshot,
    IReadOnlyList<Source2VertexBufferAnalysis> VertexBuffers,
    IReadOnlyList<Source2IndexBufferAnalysis> IndexBuffers,
    IReadOnlyList<Source2DrawCallAnalysis> DrawCalls)
{
    public IReadOnlyList<Source2ConnectedComponentAnalysis> ConnectedComponents { get; init; } = [];
}

internal static class Source2GeometryAnalyzer
{
    private const int MaximumBuffersPerMesh = 64;
    private const int MaximumDrawCallsPerGeometryMesh = 256;
    private const int MaximumDecodedBufferBytes = 512 * 1024 * 1024;
    private const uint R32G32B32Float = 6;
    private const uint R32UInt = 42;

    public static MeshGeometrySnapshot Analyze(
        KVObject embeddedMeshDescriptor,
        Source2ResourceEnvelope envelope,
        IReadOnlyList<GeometryDrawCallInput> drawCalls,
        IMeshOptimizerCodec codec,
        string context) => AnalyzeDetailed(embeddedMeshDescriptor, envelope, drawCalls, codec, context).Snapshot;

    public static Source2GeometryAnalysis AnalyzeDetailed(
        KVObject embeddedMeshDescriptor,
        Source2ResourceEnvelope envelope,
        IReadOnlyList<GeometryDrawCallInput> drawCalls,
        IMeshOptimizerCodec codec,
        string context)
    {
        ArgumentNullException.ThrowIfNull(embeddedMeshDescriptor);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(drawCalls);
        ArgumentNullException.ThrowIfNull(codec);
        if (drawCalls.Count is < 1 or > MaximumDrawCallsPerGeometryMesh)
        {
            throw new InvalidDataException(
                $"{context} declares {drawCalls.Count} draw calls; the geometry profile supports [1, {MaximumDrawCallsPerGeometryMesh}].");
        }

        var vertexDescriptors = RequireArray(embeddedMeshDescriptor, "m_vertexBuffers", context);
        var indexDescriptors = RequireArray(embeddedMeshDescriptor, "m_indexBuffers", context);
        if (vertexDescriptors.Count is < 1 or > MaximumBuffersPerMesh
            || indexDescriptors.Count is < 1 or > MaximumBuffersPerMesh)
        {
            throw new InvalidDataException(
                $"{context} declares {vertexDescriptors.Count} vertex and {indexDescriptors.Count} index buffers; each count must be within [1, {MaximumBuffersPerMesh}].");
        }

        ValidateAggregateDecodedBudget(vertexDescriptors, indexDescriptors, context);

        var vertices = new Source2VertexBufferAnalysis[vertexDescriptors.Count];
        for (var ordinal = 0; ordinal < vertices.Length; ordinal++)
        {
            vertices[ordinal] = ReadVertexBuffer(
                RequireCollection(vertexDescriptors[ordinal], $"{context}.m_vertexBuffers[{ordinal}]"),
                ordinal,
                envelope,
                codec,
                context);
        }

        var indices = new Source2IndexBufferAnalysis[indexDescriptors.Count];
        for (var ordinal = 0; ordinal < indices.Length; ordinal++)
        {
            indices[ordinal] = ReadIndexBuffer(
                RequireCollection(indexDescriptors[ordinal], $"{context}.m_indexBuffers[{ordinal}]"),
                ordinal,
                envelope,
                codec,
                context);
        }

        var references = drawCalls.Select(item => ReadDrawCall(item, vertices, indices, context)).ToArray();
        RequireCompleteReferences(references, vertices.Length, indices.Length, context);
        var analyzedCalls = AnalyzeOwnership(references, vertices, indices, context);
        var connectedComponents = AnalyzeConnectedComponents(drawCalls, analyzedCalls, vertices, indices, context);
        var snapshot = new MeshGeometrySnapshot(
            "ready",
            $"{vertices.Length} vertex buffer(s), {indices.Length} index buffer(s), and {analyzedCalls.Length} draw call(s) passed the float3/meshoptimizer profile.",
            vertices.Select(item => item.Snapshot).ToArray(),
            indices.Select(item => item.Snapshot).ToArray(),
            analyzedCalls.Select(item => item.Snapshot).ToArray(),
            codec.Identity)
        {
            ConnectedComponents = connectedComponents.Select(item => item.Snapshot).ToArray(),
        };
        return new Source2GeometryAnalysis(snapshot, vertices, indices, analyzedCalls)
        {
            ConnectedComponents = connectedComponents,
        };
    }

    internal static Source2ConnectedComponentAnalysis[] AnalyzeConnectedComponents(
        IReadOnlyList<GeometryDrawCallInput> inputs,
        IReadOnlyList<Source2DrawCallAnalysis> drawCalls,
        Source2VertexBufferAnalysis[] vertices,
        Source2IndexBufferAnalysis[] indices,
        string context)
    {
        var inputById = inputs.ToDictionary(item => item.Snapshot.Id, StringComparer.Ordinal);
        var result = new List<Source2ConnectedComponentAnalysis>();
        foreach (var drawCall in drawCalls)
        {
            var snapshot = drawCall.Snapshot;
            var input = inputById[snapshot.DrawCallId].Snapshot;
            var source = indices[snapshot.IndexBufferOrdinal].Indices;
            var start = checked((int)input.IndexStart);
            var count = checked((int)input.IndexCount);
            if (count % 3 != 0)
            {
                throw new InvalidDataException($"{context} draw call '{snapshot.DrawCallId}' index count {count} is not a triangle list.");
            }

            var parent = new Dictionary<int, int>();
            var rank = new Dictionary<int, byte>();
            var triangles = new (int A, int B, int C)[count / 3];
            for (var offset = 0; offset < count; offset += 3)
            {
                var a = checked((int)source[start + offset] + snapshot.BaseVertex);
                var b = checked((int)source[start + offset + 1] + snapshot.BaseVertex);
                var c = checked((int)source[start + offset + 2] + snapshot.BaseVertex);
                if ((uint)a >= (uint)vertices[snapshot.VertexBufferOrdinal].Snapshot.VertexCount
                    || (uint)b >= (uint)vertices[snapshot.VertexBufferOrdinal].Snapshot.VertexCount
                    || (uint)c >= (uint)vertices[snapshot.VertexBufferOrdinal].Snapshot.VertexCount)
                {
                    throw new InvalidDataException($"{context} draw call '{snapshot.DrawCallId}' contains an out-of-range triangle index.");
                }

                Add(a);
                Add(b);
                Add(c);
                Union(a, b);
                Union(b, c);
                triangles[offset / 3] = (a, b, c);
            }

            var verticesByRoot = new Dictionary<int, List<int>>();
            foreach (var vertex in parent.Keys.Order())
            {
                var root = Find(vertex);
                if (!verticesByRoot.TryGetValue(root, out var members))
                {
                    members = [];
                    verticesByRoot.Add(root, members);
                }

                members.Add(vertex);
            }

            var trianglesByRoot = new Dictionary<int, int>();
            foreach (var triangle in triangles)
            {
                var root = Find(triangle.A);
                trianglesByRoot[root] = trianglesByRoot.GetValueOrDefault(root) + 1;
            }

            foreach (var (root, members) in verticesByRoot)
            {
                members.Sort();
                var memberArray = members.ToImmutableArray();
                var vertexSetHash = new ContentHash(VertexSetHash.Compute(memberArray));
                var identitySeed = Encoding.UTF8.GetBytes($"connected_component@1|{snapshot.DrawCallId}|{vertexSetHash}");
                var identity = SHA256.HashData(identitySeed);
                var points = memberArray.Select(vertex => ReadPosition(vertices[snapshot.VertexBufferOrdinal], vertex)).ToArray();
                result.Add(new Source2ConnectedComponentAnalysis(
                    new ConnectedComponentSnapshot(
                        $"cc_{Convert.ToHexStringLower(identity.AsSpan(0, 12))}",
                        snapshot.DrawCallId,
                        snapshot.VertexBufferOrdinal,
                        snapshot.IndexBufferOrdinal,
                        memberArray.Length,
                        trianglesByRoot[root],
                        vertexSetHash,
                        ToDomainBounds(Bounds3.FromPoints(points)),
                        snapshot.ExclusivelyOwned),
                    memberArray));
            }

            continue;

            void Add(int vertex)
            {
                if (parent.TryAdd(vertex, vertex))
                {
                    rank.Add(vertex, 0);
                }
            }

            int Find(int vertex)
            {
                var current = vertex;
                while (parent[current] != current)
                {
                    parent[current] = parent[parent[current]];
                    current = parent[current];
                }

                return current;
            }

            void Union(int left, int right)
            {
                var leftRoot = Find(left);
                var rightRoot = Find(right);
                if (leftRoot == rightRoot)
                {
                    return;
                }

                var leftRank = rank[leftRoot];
                var rightRank = rank[rightRoot];
                if (leftRank < rightRank)
                {
                    parent[leftRoot] = rightRoot;
                }
                else if (leftRank > rightRank)
                {
                    parent[rightRoot] = leftRoot;
                }
                else
                {
                    parent[rightRoot] = leftRoot;
                    rank[leftRoot]++;
                }
            }
        }

        return result.OrderBy(item => item.Snapshot.DrawCallId, StringComparer.Ordinal)
            .ThenBy(item => item.Snapshot.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static Source2VertexBufferAnalysis ReadVertexBuffer(
        KVObject descriptor,
        int ordinal,
        Source2ResourceEnvelope envelope,
        IMeshOptimizerCodec codec,
        string context)
    {
        RequireMeshOptimizerProfile(descriptor, $"{context}.m_vertexBuffers[{ordinal}]");
        var block = RequireBlock(descriptor, envelope, "MVTX", $"{context}.m_vertexBuffers[{ordinal}]");
        var count = RequirePositiveInt32(descriptor, "m_nElementCount", context);
        var stride = RequirePositiveInt32(descriptor, "m_nElementSizeInBytes", context);
        var layout = RequireArray(descriptor, "m_inputLayoutFields", context);
        ValidateDecodedShape(count, stride, vertexBuffer: true, context);
        if (layout.Count is < 1 or > 64)
        {
            throw new InvalidDataException($"{context} vertex buffer {ordinal} has unsupported input-layout field count {layout.Count}.");
        }
        var positionFields = layout.Values
            .Where(field => field.IsCollection
                && TryString(field, "m_pSemanticName", out var semantic)
                && string.Equals(semantic, "POSITION", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (positionFields.Length != 1)
        {
            throw new InvalidDataException($"{context} vertex buffer {ordinal} must contain exactly one POSITION field.");
        }

        var position = positionFields[0];
        if (RequireInt32(position, "m_nSemanticIndex", context) != 0
            || RequireUInt32(position, "m_Format", context) != R32G32B32Float
            || RequireInt32(position, "m_nSlot", context) != 0
            || !string.Equals(RequireString(position, "m_nSlotType", context), "RENDER_SLOT_PER_VERTEX", StringComparison.Ordinal)
            || ReadOptionalInt32(position, "m_nInstanceStepRate", context) != 0)
        {
            throw new InvalidDataException($"{context} vertex buffer {ordinal} POSITION is not the supported per-vertex R32G32B32_FLOAT profile.");
        }

        var positionOffset = RequireInt32(position, "m_nOffset", context);
        if (positionOffset < 0 || positionOffset > stride - (sizeof(float) * 3))
        {
            throw new InvalidDataException($"{context} vertex buffer {ordinal} POSITION offset {positionOffset} escapes stride {stride}.");
        }

        var roundTrip = MeshOptimizerRoundTrip.VerifyUnchangedVertexBuffer(
            codec,
            block.Payload.ToArray(),
            count,
            stride,
            version: 1);
        var expectedLength = checked(count * stride);
        if (roundTrip.Decoded.Length != expectedLength)
        {
            throw new InvalidDataException($"{context} vertex buffer {ordinal} decoded to {roundTrip.Decoded.Length} bytes; expected {expectedLength}.");
        }

        var snapshot = new VertexBufferSnapshot(
            ordinal,
            block.Index,
            count,
            stride,
            ContentHash.Compute(block.Payload.Span),
            roundTrip.DecodedHash,
            new PositionLayout("R32G32B32_FLOAT", positionOffset, stride));
        return new Source2VertexBufferAnalysis(snapshot, roundTrip.Decoded)
        {
            PackedFrameLayout = ReadPackedFrameLayout(layout, positionOffset, stride, context, ordinal),
        };
    }

    private static PackedFrameLayout? ReadPackedFrameLayout(
        KVObject layout,
        int positionOffset,
        int stride,
        string context,
        int ordinal)
    {
        var normalFields = layout.Values
            .Where(field => field.IsCollection
                && TryString(field, "m_pSemanticName", out var semantic)
                && string.Equals(semantic, "NORMAL", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var tangentFields = layout.Values
            .Where(field => field.IsCollection
                && TryString(field, "m_pSemanticName", out var semantic)
                && string.Equals(semantic, "TANGENT", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (normalFields.Length != 1 || tangentFields.Length != 0)
        {
            return null;
        }

        var normal = normalFields[0];
        if (RequireInt32(normal, "m_nSemanticIndex", context) != 0
            || RequireUInt32(normal, "m_Format", context) != R32UInt
            || RequireInt32(normal, "m_nSlot", context) != 0
            || !string.Equals(RequireString(normal, "m_nSlotType", context), "RENDER_SLOT_PER_VERTEX", StringComparison.Ordinal)
            || ReadOptionalInt32(normal, "m_nInstanceStepRate", context) != 0)
        {
            return null;
        }

        var normalOffset = RequireInt32(normal, "m_nOffset", context);
        if (normalOffset < 0
            || normalOffset > stride - sizeof(uint)
            || RangesOverlap(positionOffset, sizeof(float) * 3, normalOffset, sizeof(uint)))
        {
            throw new InvalidDataException(
                $"{context} vertex buffer {ordinal} NORMAL R32_UINT escapes the stride or overlaps POSITION.");
        }

        return new PackedFrameLayout(
            "R32_UINT",
            normalOffset,
            stride,
            Source2PackedFrameCodec.EncodingProfile);
    }

    private static bool RangesOverlap(int firstOffset, int firstLength, int secondOffset, int secondLength) =>
        firstOffset < secondOffset + secondLength && secondOffset < firstOffset + firstLength;

    private static Source2IndexBufferAnalysis ReadIndexBuffer(
        KVObject descriptor,
        int ordinal,
        Source2ResourceEnvelope envelope,
        IMeshOptimizerCodec codec,
        string context)
    {
        RequireMeshOptimizerProfile(descriptor, $"{context}.m_indexBuffers[{ordinal}]");
        var block = RequireBlock(descriptor, envelope, "MIDX", $"{context}.m_indexBuffers[{ordinal}]");
        var count = RequirePositiveInt32(descriptor, "m_nElementCount", context);
        var stride = RequirePositiveInt32(descriptor, "m_nElementSizeInBytes", context);
        if (stride is not (2 or 4))
        {
            throw new InvalidDataException($"{context} index buffer {ordinal} stride {stride} is unsupported.");
        }

        ValidateDecodedShape(count, stride, vertexBuffer: false, context);

        var decoded = codec.DecodeIndexBuffer(block.Payload.ToArray(), count, stride);
        var expectedLength = checked(count * stride);
        if (decoded.Length != expectedLength)
        {
            throw new InvalidDataException($"{context} index buffer {ordinal} decoded to {decoded.Length} bytes; expected {expectedLength}.");
        }

        var values = new uint[count];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = stride == 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(decoded.AsSpan(index * stride, stride))
                : BinaryPrimitives.ReadUInt32LittleEndian(decoded.AsSpan(index * stride, stride));
        }

        var snapshot = new IndexBufferSnapshot(
            ordinal,
            block.Index,
            count,
            stride,
            ContentHash.Compute(block.Payload.Span),
            ContentHash.Compute(decoded));
        return new Source2IndexBufferAnalysis(snapshot, values);
    }

    private static DrawCallReference ReadDrawCall(
        GeometryDrawCallInput input,
        Source2VertexBufferAnalysis[] vertices,
        Source2IndexBufferAnalysis[] indices,
        string context)
    {
        var data = input.Data;
        if (!string.Equals(RequireString(data, "m_nPrimitiveType", context), "RENDER_PRIM_TRIANGLES", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{context} draw call '{input.Snapshot.Id}' is not a triangle-list primitive.");
        }

        var indexReference = RequireCollectionProperty(data, "m_indexBuffer", context);
        var indexOrdinal = ReadZeroBindReference(indexReference, context);
        var vertexReferences = RequireArray(data, "m_vertexBuffers", context);
        if (vertexReferences.Count != 1)
        {
            throw new InvalidDataException($"{context} draw call '{input.Snapshot.Id}' references {vertexReferences.Count} vertex buffers; exactly one is supported.");
        }

        var vertexOrdinal = ReadZeroBindReference(
            RequireCollection(vertexReferences[0], $"{context}.m_vertexBuffers[0]"),
            context);
        if ((uint)vertexOrdinal >= (uint)vertices.Length || (uint)indexOrdinal >= (uint)indices.Length)
        {
            throw new InvalidDataException($"{context} draw call '{input.Snapshot.Id}' references an out-of-range buffer handle.");
        }

        var declaredBaseVertex = RequireInt32(data, "m_nBaseVertex", context);
        if (declaredBaseVertex != 0)
        {
            throw new InvalidDataException($"{context} draw call '{input.Snapshot.Id}' uses unsupported non-zero m_nBaseVertex {declaredBaseVertex}.");
        }

        var appliedVertexOffset = RequireInt32(data, "m_nAppliedIndexOffset", context);
        var vertexEndExclusive = RequirePositiveInt32(data, "m_nVertexCount", context);
        if (appliedVertexOffset < 0
            || vertexEndExclusive <= appliedVertexOffset
            || vertexEndExclusive > vertices[vertexOrdinal].Snapshot.VertexCount)
        {
            throw new InvalidDataException($"{context} draw call '{input.Snapshot.Id}' has invalid applied-offset window [{appliedVertexOffset}..{vertexEndExclusive}).");
        }

        var indexEnd = checked(input.Snapshot.IndexStart + input.Snapshot.IndexCount);
        if (input.Snapshot.IndexStart < 0 || input.Snapshot.IndexCount <= 0 || indexEnd > indices[indexOrdinal].Snapshot.IndexCount)
        {
            throw new InvalidDataException($"{context} draw call '{input.Snapshot.Id}' has an invalid index range.");
        }

        return new DrawCallReference(input.Snapshot, vertexOrdinal, indexOrdinal, appliedVertexOffset, vertexEndExclusive);
    }

    private static Source2DrawCallAnalysis[] AnalyzeOwnership(
        IReadOnlyList<DrawCallReference> drawCalls,
        Source2VertexBufferAnalysis[] vertices,
        Source2IndexBufferAnalysis[] indices,
        string context)
    {
        List<Source2DrawCallAnalysis> result = [];
        for (var vertexOrdinal = 0; vertexOrdinal < vertices.Length; vertexOrdinal++)
        {
            var relevant = drawCalls.Where(item => item.VertexBufferOrdinal == vertexOrdinal)
                .OrderBy(item => item.Snapshot.DrawCallOrdinal)
                .ToArray();
            List<uint> combinedIndices = [];
            List<DrawCallRange> ranges = [];
            var effectiveBases = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var drawCall in relevant)
            {
                var source = indices[drawCall.IndexBufferOrdinal].Indices;
                var combinedStart = combinedIndices.Count;
                var sourceStart = checked((int)drawCall.Snapshot.IndexStart);
                var sourceCount = checked((int)drawCall.Snapshot.IndexCount);
                var effectiveBase = ResolveEffectiveBaseVertex(
                    source.AsSpan(sourceStart, sourceCount),
                    drawCall.AppliedVertexOffset,
                    drawCall.VertexEndExclusive,
                    vertices[vertexOrdinal].Snapshot.VertexCount,
                    drawCall.Snapshot.Id,
                    context);
                for (var offset = 0; offset < sourceCount; offset++)
                {
                    combinedIndices.Add(source[sourceStart + offset]);
                }

                ranges.Add(new DrawCallRange(drawCall.Snapshot.Id, combinedStart, sourceCount, effectiveBase));
                effectiveBases.Add(drawCall.Snapshot.Id, effectiveBase);
            }

            foreach (var drawCall in relevant)
            {
                var ownership = VertexOwnershipAnalyzer.Analyze(
                    combinedIndices,
                    vertices[vertexOrdinal].Snapshot.VertexCount,
                    ranges,
                    [drawCall.Snapshot.Id]);
                foreach (var selectedVertex in ownership.SelectedVertices)
                {
                    if (selectedVertex >= drawCall.VertexEndExclusive)
                    {
                        throw new InvalidDataException($"{context} draw call '{drawCall.Snapshot.Id}' references vertex {selectedVertex} outside its declared vertex end {drawCall.VertexEndExclusive}.");
                    }
                }

                var positions = ownership.SelectedVertices
                    .Select(index => ReadPosition(vertices[vertexOrdinal], index))
                    .ToArray();
                var bounds = Bounds3.FromPoints(positions);
                result.Add(new Source2DrawCallAnalysis(
                    new DrawCallGeometrySnapshot(
                        drawCall.Snapshot.Id,
                        vertexOrdinal,
                        drawCall.IndexBufferOrdinal,
                        effectiveBases[drawCall.Snapshot.Id],
                        drawCall.VertexEndExclusive,
                        ownership.SelectedVertexCount,
                        new ContentHash(VertexSetHash.Compute(ownership.SelectedVertices)),
                        ToDomainBounds(bounds),
                        ownership.SharedVertexCount == 0),
                    ownership.SelectedVertices));
            }
        }

        var ordinals = drawCalls.ToDictionary(item => item.Snapshot.Id, item => item.Snapshot.DrawCallOrdinal, StringComparer.Ordinal);
        return result.OrderBy(item => ordinals[item.Snapshot.DrawCallId]).ToArray();
    }

    private static int ResolveEffectiveBaseVertex(
        ReadOnlySpan<uint> rawIndices,
        int appliedVertexOffset,
        int vertexEndExclusive,
        int vertexCount,
        string drawCallId,
        string context)
    {
        if (rawIndices.IsEmpty)
        {
            throw new InvalidDataException($"{context} draw call '{drawCallId}' has an empty decoded index range.");
        }

        if (appliedVertexOffset == 0)
        {
            return 0;
        }

        var localVertexCount = vertexEndExclusive - appliedVertexOffset;
        var localValid = true;
        var globalValid = true;
        foreach (var index in rawIndices)
        {
            localValid &= index < localVertexCount && (long)index + appliedVertexOffset < vertexCount;
            globalValid &= index >= appliedVertexOffset && index < vertexEndExclusive && index < vertexCount;
        }

        if (localValid == globalValid)
        {
            throw new InvalidDataException(
                $"{context} draw call '{drawCallId}' has {(localValid ? "ambiguous" : "invalid")} local/global index-offset semantics.");
        }

        return localValid ? appliedVertexOffset : 0;
    }

    internal static Point3 ReadPosition(Source2VertexBufferAnalysis buffer, int vertex)
    {
        var offset = checked((vertex * buffer.Snapshot.Stride) + buffer.Snapshot.PositionLayout.Offset);
        var bytes = buffer.Decoded.AsSpan(offset, sizeof(float) * 3);
        return new Point3(
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[sizeof(float)..])),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[(sizeof(float) * 2)..])));
    }

    private static GeometryBounds ToDomainBounds(Bounds3 bounds) => new(
        new TransformVector3 { X = bounds.Min.X, Y = bounds.Min.Y, Z = bounds.Min.Z },
        new TransformVector3 { X = bounds.Max.X, Y = bounds.Max.Y, Z = bounds.Max.Z });

    private static void RequireCompleteReferences(
        IReadOnlyList<DrawCallReference> drawCalls,
        int vertexBufferCount,
        int indexBufferCount,
        string context)
    {
        var vertexOrdinals = drawCalls.Select(item => item.VertexBufferOrdinal).ToHashSet();
        var indexOrdinals = drawCalls.Select(item => item.IndexBufferOrdinal).ToHashSet();
        if (!vertexOrdinals.SetEquals(Enumerable.Range(0, vertexBufferCount))
            || !indexOrdinals.SetEquals(Enumerable.Range(0, indexBufferCount)))
        {
            throw new InvalidDataException($"{context} contains an unreferenced vertex or index buffer.");
        }
    }

    private static void RequireMeshOptimizerProfile(KVObject descriptor, string context)
    {
        if (!RequireBoolean(descriptor, "m_bMeshoptCompressed", context)
            || RequireBoolean(descriptor, "m_bMeshoptIndexSequence", context)
            || RequireBoolean(descriptor, "m_bCompressedZSTD", context))
        {
            throw new InvalidDataException($"{context} does not use the supported meshoptimizer buffer profile.");
        }
    }

    private static void ValidateDecodedShape(int count, int stride, bool vertexBuffer, string context)
    {
        if (vertexBuffer && (stride < sizeof(float) * 3 || stride > 256 || stride % sizeof(float) != 0))
        {
            throw new InvalidDataException($"{context} vertex stride {stride} is outside the supported aligned range [12, 256].");
        }

        var length = checked((long)count * stride);
        if (length > MaximumDecodedBufferBytes)
        {
            throw new InvalidDataException($"{context} decoded buffer length {length} exceeds {MaximumDecodedBufferBytes} bytes.");
        }
    }

    private static void ValidateAggregateDecodedBudget(
        KVObject vertexDescriptors,
        KVObject indexDescriptors,
        string context)
    {
        long total = 0;
        foreach (var (descriptors, kind) in new[]
        {
            (vertexDescriptors, "vertex"),
            (indexDescriptors, "index"),
        })
        {
            for (var ordinal = 0; ordinal < descriptors.Count; ordinal++)
            {
                var descriptor = RequireCollection(descriptors[ordinal], $"{context}.m_{kind}Buffers[{ordinal}]");
                var count = RequirePositiveInt32(descriptor, "m_nElementCount", context);
                var stride = RequirePositiveInt32(descriptor, "m_nElementSizeInBytes", context);
                total = checked(total + ((long)count * stride));
                if (total > MaximumDecodedBufferBytes)
                {
                    throw new InvalidDataException(
                        $"{context} declares {total} aggregate decoded vertex/index bytes; the per-mesh limit is {MaximumDecodedBufferBytes} bytes.");
                }
            }
        }
    }

    private static Source2ResourceBlock RequireBlock(
        KVObject descriptor,
        Source2ResourceEnvelope envelope,
        string expectedType,
        string context)
    {
        var blockIndex = RequireInt32(descriptor, "m_nBlockIndex", context);
        if ((uint)blockIndex >= (uint)envelope.Blocks.Count
            || envelope.Blocks[blockIndex].Index != blockIndex
            || !string.Equals(envelope.Blocks[blockIndex].Type, expectedType, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{context} does not reference one valid {expectedType} block.");
        }

        return envelope.Blocks[blockIndex];
    }

    private static int ReadZeroBindReference(KVObject reference, string context)
    {
        var handle = RequireInt32(reference, "m_hBuffer", context);
        if (RequireInt32(reference, "m_nBindOffsetBytes", context) != 0)
        {
            throw new InvalidDataException($"{context} uses a non-zero buffer bind offset.");
        }

        return handle;
    }

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

    private static bool RequireBoolean(KVObject parent, string key, string context)
    {
        var value = RequireScalar(parent, key, context);
        if (value.ValueType != KVValueType.Boolean)
        {
            throw new InvalidDataException($"Expected Boolean '{context}.{key}'.");
        }

        try
        {
            return value.ToBoolean(CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException)
        {
            throw new InvalidDataException($"Expected Boolean '{context}.{key}'.", exception);
        }
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

    private static int ReadOptionalInt32(KVObject parent, string key, string context) =>
        parent.ContainsKey(key) ? RequireInt32(parent, key, context) : 0;

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

    private static string RequireString(KVObject parent, string key, string context)
    {
        if (!TryString(parent, key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Expected non-empty string '{context}.{key}'.");
        }

        return value;
    }

    private static bool TryString(KVObject parent, string key, out string value)
    {
        if (parent.TryGetValue(key, out var item)
            && item is not null
            && item.ValueType == KVValueType.String)
        {
            value = item.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static KVObject RequireScalar(KVObject parent, string key, string context)
    {
        if (!parent.TryGetValue(key, out var value) || value is null || value.IsArray || value.IsCollection)
        {
            throw new InvalidDataException($"Expected scalar '{context}.{key}'.");
        }

        return value;
    }

    private sealed record DrawCallReference(
        DrawCallSnapshot Snapshot,
        int VertexBufferOrdinal,
        int IndexBufferOrdinal,
        int AppliedVertexOffset,
        int VertexEndExclusive);
}
