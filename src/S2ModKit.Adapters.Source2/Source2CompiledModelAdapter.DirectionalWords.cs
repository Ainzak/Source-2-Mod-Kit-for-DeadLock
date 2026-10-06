using System.Buffers.Binary;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    internal sealed record DirectionalWordCalculation(byte[] Bytes, Point3[] Points, ContentHash MaskHash, ContentHash WeightHash,
        int Core, int Transition, int Pinned, int[] ChangedPositions, int[] ChangedFrames, int[] PinnedIndices, float MaximumDisplacement);

    // Planning and candidate writing only; the future independent verifier must reconstruct words separately.
    internal static DirectionalWordCalculation CalculateDirectionalWords(ReadOnlySpan<byte> decoded, PositionLayout position,
        PackedFrameLayout frame, int count, DirectionalEllipsoidScale math)
    {
        if (count <= 0 || position.Format != "R32G32B32_FLOAT" || frame.Format != "R32_UINT" || frame.EncodingProfile != Source2PackedFrameCodec.EncodingProfile
            || position.Offset < 0 || frame.Offset < 0 || (long)position.Offset + 12 > position.Stride || (long)frame.Offset + 4 > frame.Stride
            || position.Stride != frame.Stride || decoded.Length != (long)count * position.Stride
            || (position.Offset < (long)frame.Offset + 4 && frame.Offset < (long)position.Offset + 12)) throw new ArgumentException("Require complete characterized matching buffer layouts.");
        var bytes = decoded.ToArray(); var points = new Point3[count];
        var masks = new byte[checked(count * 8)]; var weights = new byte[checked(count * 12)];
        var changedPositions = new List<int>(); var changedFrames = new List<int>(); var pins = new List<int>();
        int core = 0, transition = 0; float maximum = 0;
        var uniform = math.Scale.X == math.Scale.Y && math.Scale.Y == math.Scale.Z;
        for (var vertex = 0; vertex < count; vertex++)
        {
            var offset = checked(vertex * position.Stride + position.Offset);
            var source = new Point3(BinaryPrimitives.ReadSingleLittleEndian(decoded[offset..]), BinaryPrimitives.ReadSingleLittleEndian(decoded[(offset + 4)..]), BinaryPrimitives.ReadSingleLittleEndian(decoded[(offset + 8)..]));
            EllipsoidPointResult result;
            try { result = math.Evaluate(source); }
            catch (ArgumentException e)
            {
                throw DirectionalFailure(e.ParamName == "point"
                ? "TRANSFORM_DISPLACEMENT_EXCEEDED" : "DIRECTIONAL_NUMERIC_UNREPRESENTABLE", $"Vertex {vertex}: {e.Message}");
            }
            points[vertex] = result.Position;
            BinaryPrimitives.WriteInt32LittleEndian(masks.AsSpan(vertex * 8), vertex);
            BinaryPrimitives.WriteInt32LittleEndian(masks.AsSpan(vertex * 8 + 4), (int)result.Membership);
            BinaryPrimitives.WriteInt32LittleEndian(weights.AsSpan(vertex * 12), vertex);
            BinaryPrimitives.WriteInt64LittleEndian(weights.AsSpan(vertex * 12 + 4), BitConverter.DoubleToInt64Bits(result.Weight));
            switch (result.Membership) { case EllipsoidMembership.Core: core++; break; case EllipsoidMembership.Transition: transition++; break; default: pins.Add(vertex); break; }
            if (RegionPositionWordsChanged(source, result.Position)) changedPositions.Add(vertex);
            maximum = Math.Max(maximum, result.MaximumDisplacement);
            var frameOffset = checked(vertex * frame.Stride + frame.Offset);
            var sourceWord = BinaryPrimitives.ReadUInt32LittleEndian(decoded[frameOffset..]);
            // Decode/validate even deliberately retained frames. Pinned words bypass output encoding.
            var sourceFrame = Source2PackedFrameCodec.Decode(sourceWord);
            TangentFrame transformed;
            try { transformed = math.TransformFrame(source, sourceFrame); }
            catch (ArgumentException e) { throw DirectionalFailure("DIRECTIONAL_NUMERIC_UNREPRESENTABLE", $"Frame {vertex}: {e.Message}"); }
            var encodeFrame = result.Membership == EllipsoidMembership.Transition || (result.Membership == EllipsoidMembership.Core && !uniform);
            var expectedWord = encodeFrame ? Source2PackedFrameCodec.Encode(transformed) : sourceWord;
            if (encodeFrame)
            {
                var reopened = Source2PackedFrameCodec.Decode(expectedWord);
                if (DirectionalFrameDot(transformed.Normal, reopened.Normal) < .9999f || DirectionalFrameDot(transformed.Tangent, reopened.Tangent) < .9999f
                    || transformed.Handedness != reopened.Handedness)
                    throw DirectionalFailure("AFFINE_PACKED_FRAME_UNSUPPORTED", $"Frame {vertex}: independent packed directions or handedness drifted.");
            }
            if (expectedWord != sourceWord) changedFrames.Add(vertex);
            if (result.Membership != EllipsoidMembership.Pinned) WritePosition(bytes, position, vertex, result.Position);
            if (encodeFrame) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(frameOffset), expectedWord);
        }
        return new(bytes, points, ContentHash.Compute(masks), ContentHash.Compute(weights), core, transition, pins.Count,
            changedPositions.ToArray(), changedFrames.ToArray(), pins.ToArray(), maximum);
    }

    private static float DirectionalFrameDot(Point3 a, Point3 b) => ((a.X * b.X) + (a.Y * b.Y)) + (a.Z * b.Z);

    internal static ContentHash DirectionalPositionWords(ReadOnlySpan<byte> bytes, PositionLayout layout, IReadOnlyList<int> indices)
    {
        var words = new byte[checked(indices.Count * 12)];
        for (var i = 0; i < indices.Count; i++) bytes.Slice(checked(indices[i] * layout.Stride + layout.Offset), 12).CopyTo(words.AsSpan(i * 12));
        return ContentHash.Compute(words);
    }

    internal static ContentHash DirectionalUnchangedWords(ReadOnlySpan<byte> bytes, PositionLayout position, PackedFrameLayout frame)
    {
        var retained = new byte[checked(bytes.Length / position.Stride * (position.Stride - 16))]; var cursor = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            var field = i % position.Stride;
            if ((field >= position.Offset && field < position.Offset + 12) || (field >= frame.Offset && field < frame.Offset + 4)) continue;
            retained[cursor++] = bytes[i];
        }
        return ContentHash.Compute(retained);
    }

    private static (PlannedCoordinatedBuffer Buffer, DirectionalWordAudit Audit) DirectionalBufferFacts(ArtifactContent input,
        CoordinatedResolvedBuffer member, DirectionalWordCalculation c)
    {
        var p = member.Profile; var v = p.Vertices.Snapshot; var i = p.Indices.Snapshot;
        var buffer = new PlannedCoordinatedBuffer(member.MemberId, p.Mesh.Lod, input.LogicalPath, p.Mesh.MeshOrdinal, p.Mesh.BlockIndex,
            v.Ordinal, i.Ordinal, v.ResourceBlockIndex, i.ResourceBlockIndex, v.EncodedHash, i.EncodedHash, v.DecodedHash, ContentHash.Compute(c.Bytes), i.DecodedHash,
            p.VertexSetHash, v.VertexCount, "exclusive", v.PositionLayout, p.PackedFrameLayout, p.Mesh.Geometry.Codec!, c.MaskHash, c.WeightHash,
            c.Core, c.Transition, c.Pinned, c.ChangedPositions.Length, c.ChangedFrames.Length,
            DirectionalPositionWords(p.Vertices.Decoded, v.PositionLayout, p.SelectedVertices), DirectionalPositionWords(c.Bytes, v.PositionLayout, p.SelectedVertices),
            Source2PackedFrameCodec.HashSelected(p.Vertices.Decoded, p.PackedFrameLayout, p.SelectedVertices), Source2PackedFrameCodec.HashSelected(c.Bytes, p.PackedFrameLayout, p.SelectedVertices),
            p.SelectionBounds, ToAffineBounds(Bounds3.FromPoints(c.Points)), c.MaximumDisplacement);
        var audit = new DirectionalWordAudit(member.MemberId, p.Mesh.Lod, c.ChangedPositions, DirectionalContractValidator.VertexSetHash(c.ChangedPositions),
            c.ChangedFrames, DirectionalContractValidator.VertexSetHash(c.ChangedFrames), c.PinnedIndices, DirectionalContractValidator.VertexSetHash(c.PinnedIndices),
            DirectionalPositionWords(p.Vertices.Decoded, v.PositionLayout, c.PinnedIndices), DirectionalPositionWords(c.Bytes, v.PositionLayout, c.PinnedIndices),
            Source2PackedFrameCodec.HashSelected(p.Vertices.Decoded, p.PackedFrameLayout, c.PinnedIndices), Source2PackedFrameCodec.HashSelected(c.Bytes, p.PackedFrameLayout, c.PinnedIndices),
            DirectionalUnchangedWords(p.Vertices.Decoded, v.PositionLayout, p.PackedFrameLayout), DirectionalUnchangedWords(c.Bytes, v.PositionLayout, p.PackedFrameLayout));
        return (buffer, audit);
    }

    private static S2ModKitException DirectionalFailure(string code, string message) => Errors.Unsupported(code, message,
        "Reject the whole directional edit; resolve complete source facts and adjust explicit intent without a legacy fallback.");
}
