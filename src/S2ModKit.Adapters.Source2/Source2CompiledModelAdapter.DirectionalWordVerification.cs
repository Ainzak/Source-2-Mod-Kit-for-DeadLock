using System.Buffers.Binary;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static (PlannedCoordinatedBuffer Facts, DirectionalWordAudit Audit) ReconstructDirectionalBuffer(ArtifactContent input,
        CoordinatedResolvedBuffer member, CoordinatedResolvedBuffer actual, DirectionalFieldReconstruction reconstruction)
    {
        var p = member.Profile; var current = actual.Profile;
        var v = p.Vertices.Snapshot; var index = p.Indices.Snapshot;
        VerifyExperimentalBufferLayout(v, current.Vertices.Snapshot, p.Vertices.Decoded, current.Vertices.Decoded, true);
        if (index != current.Indices.Snapshot || !p.Indices.Indices.SequenceEqual(current.Indices.Indices)
            || p.PackedFrameLayout != current.PackedFrameLayout || p.Mesh.Geometry.Codec != current.Mesh.Geometry.Codec)
            throw DirectionalDrift("Member index/layout/codec identity changed.");
        var expected = p.Vertices.Decoded.ToArray();
        var points = new Point3[v.VertexCount]; var changedPositions = new List<int>(); var changedFrames = new List<int>(); var pins = new List<int>();
        var maskWords = new byte[checked(v.VertexCount * 8)]; var weightWords = new byte[checked(v.VertexCount * 12)];
        var coreCount = 0; var transitionCount = 0; float maximum = 0;
        for (var vertex = 0; vertex < v.VertexCount; vertex++)
        {
            var original = Source2GeometryAnalyzer.ReadPosition(p.Vertices, vertex);
            var field = reconstruction.ReconstructPosition(original);
            var positionOffset = checked(vertex * v.Stride + v.PositionLayout.Offset);
            var frameOffset = checked(vertex * p.PackedFrameLayout.Stride + p.PackedFrameLayout.Offset);
            var sourceFrameWord = BinaryPrimitives.ReadUInt32LittleEndian(p.Vertices.Decoded.AsSpan(frameOffset));
            var sourceFrame = Source2PackedFrameCodec.Decode(sourceFrameWord);
            var expectedFrame = reconstruction.ReconstructFrame(original, sourceFrame);
            // Reconstruction returns the source frame object only for the two prescribed retain cases.
            var frameWord = ReferenceEquals(sourceFrame, expectedFrame) ? sourceFrameWord : Source2PackedFrameCodec.Encode(expectedFrame);
            var outputFrame = Source2PackedFrameCodec.Decode(frameWord);
            if (VerificationFrameDot(expectedFrame.Normal, outputFrame.Normal) < .9999f || VerificationFrameDot(expectedFrame.Tangent, outputFrame.Tangent) < .9999f
                || outputFrame.Handedness != sourceFrame.Handedness) throw DirectionalDrift("Reconstructed packed-frame directions/handedness failed.");
            if (field.Membership != EllipsoidMembership.Pinned)
            {
                BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(positionOffset), field.Position.X);
                BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(positionOffset + 4), field.Position.Y);
                BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(positionOffset + 8), field.Position.Z);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(frameOffset), frameWord);
            if (!expected.AsSpan(positionOffset, 12).SequenceEqual(p.Vertices.Decoded.AsSpan(positionOffset, 12))) changedPositions.Add(vertex);
            if (frameWord != sourceFrameWord) changedFrames.Add(vertex);
            if (field.Membership == EllipsoidMembership.Core) coreCount++;
            else if (field.Membership == EllipsoidMembership.Transition) transitionCount++;
            else pins.Add(vertex);
            BinaryPrimitives.WriteInt32LittleEndian(maskWords.AsSpan(vertex * 8), vertex);
            BinaryPrimitives.WriteInt32LittleEndian(maskWords.AsSpan(vertex * 8 + 4), (int)field.Membership);
            BinaryPrimitives.WriteInt32LittleEndian(weightWords.AsSpan(vertex * 12), vertex);
            BinaryPrimitives.WriteInt64LittleEndian(weightWords.AsSpan(vertex * 12 + 4), BitConverter.DoubleToInt64Bits(field.Weight));
            points[vertex] = Source2GeometryAnalyzer.ReadPosition(current.Vertices, vertex);
            maximum = Math.Max(maximum, ConservativePointDistance.RoundUp(original, points[vertex]));
        }
        if (!expected.AsSpan().SequenceEqual(current.Vertices.Decoded)) throw DirectionalDrift($"Member {member.MemberId}, LOD {p.Mesh.Lod}: observed words differ from independent reconstruction.");
        if (changedPositions.Count == 0) throw DirectionalDrift("A member/LOD has no stored position effect.");
        ValidateRegionTriangles(p, points);
        var ids = Enumerable.Range(0, v.VertexCount).ToArray();
        var observedBytes = current.Vertices.Decoded;
        var facts = new PlannedCoordinatedBuffer(member.MemberId, p.Mesh.Lod, input.LogicalPath, p.Mesh.MeshOrdinal, p.Mesh.BlockIndex,
            v.Ordinal, index.Ordinal, v.ResourceBlockIndex, index.ResourceBlockIndex, v.EncodedHash, index.EncodedHash, v.DecodedHash,
            ContentHash.Compute(observedBytes), index.DecodedHash, DirectionalContractValidator.VertexSetHash(ids), v.VertexCount, "exclusive",
            v.PositionLayout, p.PackedFrameLayout, p.Mesh.Geometry.Codec!, ContentHash.Compute(maskWords), ContentHash.Compute(weightWords),
            coreCount, transitionCount, pins.Count, changedPositions.Count, changedFrames.Count,
            DirectionalPositionWords(p.Vertices.Decoded, v.PositionLayout, ids), DirectionalPositionWords(observedBytes, v.PositionLayout, ids),
            Source2PackedFrameCodec.HashSelected(p.Vertices.Decoded, p.PackedFrameLayout, ids), Source2PackedFrameCodec.HashSelected(observedBytes, p.PackedFrameLayout, ids),
            p.SelectionBounds, ToAffineBounds(Bounds3.FromPoints(points)), maximum);
        var audit = new DirectionalWordAudit(member.MemberId, p.Mesh.Lod, changedPositions, DirectionalContractValidator.VertexSetHash(changedPositions),
            changedFrames, DirectionalContractValidator.VertexSetHash(changedFrames), pins, DirectionalContractValidator.VertexSetHash(pins),
            DirectionalPositionWords(p.Vertices.Decoded, v.PositionLayout, pins), DirectionalPositionWords(observedBytes, v.PositionLayout, pins),
            Source2PackedFrameCodec.HashSelected(p.Vertices.Decoded, p.PackedFrameLayout, pins), Source2PackedFrameCodec.HashSelected(observedBytes, p.PackedFrameLayout, pins),
            DirectionalUnchangedWords(p.Vertices.Decoded, v.PositionLayout, p.PackedFrameLayout), DirectionalUnchangedWords(observedBytes, v.PositionLayout, p.PackedFrameLayout));
        return (facts, audit);
    }

    private static float VerificationFrameDot(Point3 first, Point3 second) => ((first.X * second.X) + (first.Y * second.Y)) + (first.Z * second.Z);
}
