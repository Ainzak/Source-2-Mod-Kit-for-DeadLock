using System.Buffers.Binary;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static (byte[] Bytes, Point3[] Points, PlannedCoordinatedBuffer Facts) PlanCoordinatedBuffer(
        ArtifactContent input, CoordinatedResolvedBuffer member, CoordinatedFieldMath math)
    {
        var profile = member.Profile;
        var v = profile.Vertices.Snapshot;
        var i = profile.Indices.Snapshot;
        var c = CalculateCoordinatedWords(profile.Vertices.Decoded, v.PositionLayout, profile.PackedFrameLayout, v.VertexCount, math);
        if (c.ChangedPositions == 0) throw CoordinatedUnsupported($"Member {member.MemberId} has no stored-position effect in LOD {profile.Mesh.Lod}.");
        ValidateRegionTriangles(profile, c.Points);
        return (c.Bytes, c.Points, new(member.MemberId, profile.Mesh.Lod, input.LogicalPath, profile.Mesh.MeshOrdinal, profile.Mesh.BlockIndex,
            v.Ordinal, i.Ordinal, v.ResourceBlockIndex, i.ResourceBlockIndex, v.EncodedHash, i.EncodedHash, v.DecodedHash, ContentHash.Compute(c.Bytes), i.DecodedHash,
            profile.VertexSetHash, v.VertexCount, "exclusive", v.PositionLayout, profile.PackedFrameLayout, profile.Mesh.Geometry.Codec!, c.MaskHash, c.WeightHash,
            c.Full, c.Transition, c.Pinned, c.ChangedPositions, c.ChangedFrames, EllipsoidPositionHash(profile.Vertices.Decoded, v.PositionLayout, v.VertexCount),
            EllipsoidPositionHash(c.Bytes, v.PositionLayout, v.VertexCount), Source2PackedFrameCodec.HashSelected(profile.Vertices.Decoded, profile.PackedFrameLayout, profile.SelectedVertices),
            Source2PackedFrameCodec.HashSelected(c.Bytes, profile.PackedFrameLayout, profile.SelectedVertices), profile.SelectionBounds, ToAffineBounds(Bounds3.FromPoints(c.Points)), c.MaximumDisplacement));
    }

    // Planning/writing only. Independent reopen must reconstruct this in a separate loop.
    internal static CoordinatedWordCalculation CalculateCoordinatedWords(ReadOnlySpan<byte> decoded, PositionLayout position,
        PackedFrameLayout frame, int count, CoordinatedFieldMath math)
    {
        if (count <= 0 || position.Format != "R32G32B32_FLOAT" || frame.Format != "R32_UINT" || frame.EncodingProfile != Source2PackedFrameCodec.EncodingProfile
            || position.Offset < 0 || frame.Offset < 0 || (long)position.Offset + 12 > position.Stride || (long)frame.Offset + 4 > frame.Stride
            || position.Stride != frame.Stride || decoded.Length != (long)count * position.Stride
            || (position.Offset < (long)frame.Offset + 4 && frame.Offset < (long)position.Offset + 12)) throw new ArgumentException("Require complete characterized matching buffer layouts.");
        var bytes = decoded.ToArray();
        var points = new Point3[count];
        var masks = new byte[checked(count * 8)];
        var weights = new byte[checked(count * 12)];
        int full = 0, transition = 0, pinned = 0, changedPositions = 0, changedFrames = 0;
        float maximum = 0;
        for (var vertex = 0; vertex < count; vertex++)
        {
            var offset = checked(vertex * position.Stride + position.Offset);
            var source = new Point3(BinaryPrimitives.ReadSingleLittleEndian(decoded[offset..]), BinaryPrimitives.ReadSingleLittleEndian(decoded[(offset + 4)..]), BinaryPrimitives.ReadSingleLittleEndian(decoded[(offset + 8)..]));
            var result = math.Evaluate(source);
            points[vertex] = result.Position;
            BinaryPrimitives.WriteInt32LittleEndian(masks.AsSpan(vertex * 8), vertex);
            BinaryPrimitives.WriteInt32LittleEndian(masks.AsSpan(vertex * 8 + 4), (int)result.Membership);
            BinaryPrimitives.WriteInt32LittleEndian(weights.AsSpan(vertex * 12), vertex);
            BinaryPrimitives.WriteInt64LittleEndian(weights.AsSpan(vertex * 12 + 4), BitConverter.DoubleToInt64Bits(result.Weight));
            switch (result.Membership) { case CoordinatedMembership.Full: full++; break; case CoordinatedMembership.Transition: transition++; break; default: pinned++; break; }
            if (RegionPositionWordsChanged(source, result.Position)) changedPositions++;
            maximum = Math.Max(maximum, result.MaximumDisplacement);
            var frameOffset = checked(vertex * frame.Stride + frame.Offset);
            var sourceWord = BinaryPrimitives.ReadUInt32LittleEndian(decoded[frameOffset..]);
            var transformed = math.TransformFrame(source, Source2PackedFrameCodec.Decode(sourceWord));
            var expectedWord = result.Membership == CoordinatedMembership.Transition ? Source2PackedFrameCodec.Encode(transformed) : sourceWord;
            if (expectedWord != sourceWord) changedFrames++;
            if (result.Membership != CoordinatedMembership.Pinned) WritePosition(bytes, position, vertex, result.Position);
            if (result.Membership == CoordinatedMembership.Transition) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(frameOffset), expectedWord);
        }
        return new(bytes, points, ContentHash.Compute(masks), ContentHash.Compute(weights), full, transition, pinned, changedPositions, changedFrames, maximum);
    }

    internal sealed record CoordinatedWordCalculation(byte[] Bytes, Point3[] Points, ContentHash MaskHash, ContentHash WeightHash,
        int Full, int Transition, int Pinned, int ChangedPositions, int ChangedFrames, float MaximumDisplacement);
}
