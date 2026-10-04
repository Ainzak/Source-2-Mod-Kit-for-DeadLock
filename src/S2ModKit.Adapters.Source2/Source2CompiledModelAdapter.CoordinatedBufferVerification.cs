using System.Buffers.Binary;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    // Separate output reconstruction: does not call the planner or its word calculator.
    private static CoordinatedBufferObservation VerifyCoordinatedBuffer(ArtifactContent input, Source2AffineProfile source,
        Source2AffineProfile observed, PlannedCoordinatedBuffer facts, CoordinatedFieldMath math)
    {
        ValidateExperimentalVertexStreams(source);
        ValidateExperimentalVertexStreams(observed);
        var v = source.Vertices.Snapshot;
        var i = source.Indices.Snapshot;
        if (source.SelectedVertices.Length != v.VertexCount || source.SelectedVertices.Where((n, index) => n != index).Any()
            || !source.SelectedVertices.SequenceEqual(observed.SelectedVertices) || source.VertexSetHash != facts.VertexSetHash
            || facts.Lod != source.Mesh.Lod || facts.ResourcePath != input.LogicalPath || facts.MeshOrdinal != source.Mesh.MeshOrdinal
            || facts.ResourceBlockIndex != source.Mesh.BlockIndex || facts.VertexBufferOrdinal != v.Ordinal || facts.IndexBufferOrdinal != i.Ordinal
            || facts.VertexResourceBlockIndex != v.ResourceBlockIndex || facts.IndexResourceBlockIndex != i.ResourceBlockIndex
            || facts.VertexBlockInputHash != v.EncodedHash || facts.IndexBlockInputHash != i.EncodedHash
            || facts.InputDecodedVertexBufferHash != v.DecodedHash || facts.DecodedIndexBufferHash != i.DecodedHash
            || facts.VertexCount != v.VertexCount || facts.OwnershipPolicy != "exclusive" || facts.PositionLayout != v.PositionLayout
            || facts.PackedFrameLayout != source.PackedFrameLayout || facts.PackedFrameLayout != observed.PackedFrameLayout
            || facts.Codec != source.Mesh.Geometry.Codec || facts.Codec != observed.Mesh.Geometry.Codec
            || facts.BeforeBounds != source.SelectionBounds || source.Indices.Snapshot != observed.Indices.Snapshot)
            throw CoordinatedDrift("Frozen buffer identities, ownership, layouts or input words differ from reopened source.");
        var expected = (byte[])source.Vertices.Decoded.Clone();
        var masks = new byte[checked(v.VertexCount * 8)];
        var weights = new byte[checked(v.VertexCount * 12)];
        var points = new Point3[v.VertexCount];
        int core = 0, transition = 0, pinned = 0, changedPositions = 0, changedFrames = 0;
        float maximum = 0;
        foreach (var vertex in source.SelectedVertices)
        {
            var original = Source2GeometryAnalyzer.ReadPosition(source.Vertices, vertex);
            var result = math.Evaluate(original);
            points[vertex] = Source2GeometryAnalyzer.ReadPosition(observed.Vertices, vertex);
            BinaryPrimitives.WriteInt32LittleEndian(masks.AsSpan(vertex * 8), vertex);
            BinaryPrimitives.WriteInt32LittleEndian(masks.AsSpan(vertex * 8 + 4), (int)result.Membership);
            BinaryPrimitives.WriteInt32LittleEndian(weights.AsSpan(vertex * 12), vertex);
            BinaryPrimitives.WriteInt64LittleEndian(weights.AsSpan(vertex * 12 + 4), BitConverter.DoubleToInt64Bits(result.Weight));
            switch (result.Membership) { case CoordinatedMembership.Full: core++; break; case CoordinatedMembership.Transition: transition++; break; default: pinned++; break; }
            if (RegionPositionWordsChanged(original, result.Position)) changedPositions++;
            maximum = Math.Max(maximum, result.MaximumDisplacement);
            var offset = checked(vertex * source.PackedFrameLayout.Stride + source.PackedFrameLayout.Offset);
            var originalWord = BinaryPrimitives.ReadUInt32LittleEndian(source.Vertices.Decoded.AsSpan(offset));
            var frame = math.TransformFrame(original, Source2PackedFrameCodec.Decode(originalWord));
            var packed = result.Membership == CoordinatedMembership.Transition ? Source2PackedFrameCodec.Encode(frame) : originalWord;
            if (packed != originalWord) changedFrames++;
            if (result.Membership != CoordinatedMembership.Pinned) WritePosition(expected, v.PositionLayout, vertex, result.Position);
            if (result.Membership == CoordinatedMembership.Transition) BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(offset), packed);
        }
        var maskHash = ContentHash.Compute(masks);
        var weightHash = ContentHash.Compute(weights);
        var positionHash = EllipsoidPositionHash(observed.Vertices.Decoded, v.PositionLayout, v.VertexCount);
        var frameHash = Source2PackedFrameCodec.HashSelected(observed.Vertices.Decoded, source.PackedFrameLayout, source.SelectedVertices);
        if (core != facts.FullVertexCount || transition != facts.TransitionVertexCount || pinned != facts.PinnedVertexCount
            || changedPositions == 0 || changedPositions != facts.ChangedPositionCount || changedFrames != facts.ChangedFrameCount
            || maskHash != facts.MaskHash || weightHash != facts.WeightHash || maximum != facts.MaximumDisplacement
            || facts.InputPositionHash != EllipsoidPositionHash(source.Vertices.Decoded, v.PositionLayout, v.VertexCount)
            || facts.InputPackedFrameHash != Source2PackedFrameCodec.HashSelected(source.Vertices.Decoded, source.PackedFrameLayout, source.SelectedVertices)
            || positionHash != facts.ExpectedPositionHash || frameHash != facts.ExpectedPackedFrameHash
            || ContentHash.Compute(expected) != facts.ExpectedDecodedVertexBufferHash
            || observed.Vertices.Snapshot.DecodedHash != facts.ExpectedDecodedVertexBufferHash
            || !expected.AsSpan().SequenceEqual(observed.Vertices.Decoded)
            || ToAffineBounds(Bounds3.FromPoints(points)) != facts.ExpectedAfterBounds)
            throw CoordinatedDrift("Independent masks, weights, counts, displacement, bounds or prescribed position/frame words differ.");
        ValidateRegionTriangles(source, points);
        return new(facts.MemberId, facts.Lod, facts.MeshOrdinal, facts.VertexBufferOrdinal, positionHash, frameHash, observed.Vertices.Snapshot.DecodedHash, maskHash, weightHash, maximum);
    }

}
