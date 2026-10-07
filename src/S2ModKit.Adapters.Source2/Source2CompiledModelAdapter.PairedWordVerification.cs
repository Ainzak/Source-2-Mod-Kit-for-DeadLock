using System.Buffers.Binary;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    internal static PairedBufferDispatch AuditPairedStoredWords(byte[] before, byte[] after, PositionLayout position,
        PackedFrameLayout frame, int count, IReadOnlyList<PairedDirectionalField> fields, float limit, string member, int lod)
    {
        DirectionalFieldParameters Parameters(PairedDirectionalField f) => new(ToAffinePoint(f.Field.Pivot.Point!),
            ToAffinePoint(f.Field.OuterRadii), f.Field.CoreFraction, ToAffinePoint(f.Field.Scale), limit);
        var parameters = fields.Select(Parameters).ToArray();
        var pair = new PairedDirectionalFieldReconstruction(parameters[0], parameters[1]);
        var singles = parameters.Select(p => new DirectionalFieldReconstruction(p.Center, p.OuterRadii, p.CoreFraction, p.Scale, p.DisplacementLimit)).ToArray();
        List<int>[] cores = [[], []], transitions = [[], []], changedPositions = [[], []], changedFrames = [[], []];
        byte[][] weights = [new byte[checked(count * 12)], new byte[checked(count * 12)]];
        var pins = new List<int>(); var expected = before.ToArray();
        for (var vertex = 0; vertex < count; vertex++)
        {
            var po = checked(vertex * position.Stride + position.Offset); var fo = checked(vertex * frame.Stride + frame.Offset);
            var source = new Point3(BinaryPrimitives.ReadSingleLittleEndian(before.AsSpan(po)),
                BinaryPrimitives.ReadSingleLittleEndian(before.AsSpan(po + 4)), BinaryPrimitives.ReadSingleLittleEndian(before.AsSpan(po + 8)));
            var prescribed = pair.ReconstructPosition(source);
            for (var fi = 0; fi < 2; fi++)
            {
                var state = singles[fi].ReconstructPosition(source);
                if (state.Membership == EllipsoidMembership.Core) cores[fi].Add(vertex);
                else if (state.Membership == EllipsoidMembership.Transition) transitions[fi].Add(vertex);
                BinaryPrimitives.WriteInt32LittleEndian(weights[fi].AsSpan(vertex * 12), vertex);
                BinaryPrimitives.WriteInt64LittleEndian(weights[fi].AsSpan(vertex * 12 + 4), BitConverter.DoubleToInt64Bits(state.Weight));
            }
            var originalWord = BinaryPrimitives.ReadUInt32LittleEndian(before.AsSpan(fo));
            var originalFrame = Source2PackedFrameCodec.Decode(originalWord);
            var prescribedFrame = pair.ReconstructFrame(source, originalFrame);
            var word = ReferenceEquals(originalFrame, prescribedFrame) ? originalWord : Source2PackedFrameCodec.Encode(prescribedFrame);
            var packed = Source2PackedFrameCodec.Decode(word);
            if (VerificationFrameDot(prescribedFrame.Normal, packed.Normal) < .9999f || VerificationFrameDot(prescribedFrame.Tangent, packed.Tangent) < .9999f
                || packed.Handedness != originalFrame.Handedness) throw PairedDrift("Reconstructed frame directions or handedness failed.");
            if (prescribed.ActiveFieldIndex is { } active)
            {
                BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(po), prescribed.Position.X);
                BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(po + 4), prescribed.Position.Y);
                BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(po + 8), prescribed.Position.Z);
                if (!expected.AsSpan(po, 12).SequenceEqual(before.AsSpan(po, 12))) changedPositions[active].Add(vertex);
                if (word != originalWord) changedFrames[active].Add(vertex);
            }
            else pins.Add(vertex);
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(fo), word);
        }
        if (!expected.AsSpan().SequenceEqual(after)) throw PairedDrift($"Member {member}, LOD {lod}: output differs from independently reconstructed complete words.");
        var records = Enumerable.Range(0, 2).Select(fi =>
        {
            var record = new PairedFieldDispatch(fields[fi].FieldId, cores[fi], transitions[fi], changedPositions[fi], changedFrames[fi], default, ContentHash.Compute(weights[fi]));
            return record with { MembershipHash = PairedContractValidator.FieldMembershipHash(count, record) };
        }).ToArray();
        return new(member, lod, records, pins, PairedContractValidator.DispatchMembershipHash(count, records), PairedContractValidator.DispatchWeightHash(records));
    }

    private static (PlannedCoordinatedBuffer Facts, DirectionalWordAudit Audit, PairedBufferDispatch Dispatch) ReconstructPairedBuffer(
        ArtifactContent input, CoordinatedResolvedBuffer member, CoordinatedResolvedBuffer actual, PlannedPairedTransformTarget target)
    {
        var p = member.Profile; var current = actual.Profile; var v = p.Vertices.Snapshot; var index = p.Indices.Snapshot;
        VerifyExperimentalBufferLayout(v, current.Vertices.Snapshot, p.Vertices.Decoded, current.Vertices.Decoded, true);
        if (index != current.Indices.Snapshot || !p.Indices.Indices.SequenceEqual(current.Indices.Indices)
            || p.PackedFrameLayout != current.PackedFrameLayout || p.Mesh.Geometry.Codec != current.Mesh.Geometry.Codec)
            throw PairedDrift("Member indices, layout or codec changed.");
        var dispatch = AuditPairedStoredWords(p.Vertices.Decoded, current.Vertices.Decoded, v.PositionLayout, p.PackedFrameLayout,
            v.VertexCount, target.PairedTransform.Fields, target.DisplacementLimit, member.MemberId, p.Mesh.Lod);
        var positions = dispatch.Fields.SelectMany(f => f.ChangedPositionIndices).Order().ToArray();
        var frames = dispatch.Fields.SelectMany(f => f.ChangedFrameIndices).Order().ToArray();
        if (positions.Length == 0) throw PairedDrift("A complete member/LOD has no stored position effect.");
        var ids = Enumerable.Range(0, v.VertexCount).ToArray(); var observed = current.Vertices.Decoded;
        var points = ids.Select(i => Source2GeometryAnalyzer.ReadPosition(current.Vertices, i)).ToArray();
        var maximum = ids.Max(i => ConservativePointDistance.RoundUp(Source2GeometryAnalyzer.ReadPosition(p.Vertices, i), points[i]));
        if (maximum > target.DisplacementLimit) throw PairedDrift("Actual stored displacement exceeds the operation cap.");
        var facts = new PlannedCoordinatedBuffer(member.MemberId, p.Mesh.Lod, input.LogicalPath, p.Mesh.MeshOrdinal, p.Mesh.BlockIndex,
            v.Ordinal, index.Ordinal, v.ResourceBlockIndex, index.ResourceBlockIndex, v.EncodedHash, index.EncodedHash, v.DecodedHash,
            ContentHash.Compute(observed), index.DecodedHash, DirectionalContractValidator.VertexSetHash(ids), v.VertexCount, "exclusive",
            v.PositionLayout, p.PackedFrameLayout, p.Mesh.Geometry.Codec!, dispatch.MembershipHash, dispatch.WeightHash,
            dispatch.Fields.Sum(f => f.CoreIndices.Count), dispatch.Fields.Sum(f => f.TransitionIndices.Count), dispatch.PinnedIndices.Count, positions.Length, frames.Length,
            DirectionalPositionWords(p.Vertices.Decoded, v.PositionLayout, ids), DirectionalPositionWords(observed, v.PositionLayout, ids),
            Source2PackedFrameCodec.HashSelected(p.Vertices.Decoded, p.PackedFrameLayout, ids), Source2PackedFrameCodec.HashSelected(observed, p.PackedFrameLayout, ids),
            p.SelectionBounds, ToAffineBounds(Bounds3.FromPoints(points)), maximum);
        var pins = dispatch.PinnedIndices;
        var audit = new DirectionalWordAudit(member.MemberId, p.Mesh.Lod, positions, DirectionalContractValidator.VertexSetHash(positions),
            frames, DirectionalContractValidator.VertexSetHash(frames), pins, DirectionalContractValidator.VertexSetHash(pins),
            DirectionalPositionWords(p.Vertices.Decoded, v.PositionLayout, pins), DirectionalPositionWords(observed, v.PositionLayout, pins),
            Source2PackedFrameCodec.HashSelected(p.Vertices.Decoded, p.PackedFrameLayout, pins), Source2PackedFrameCodec.HashSelected(observed, p.PackedFrameLayout, pins),
            DirectionalUnchangedWords(p.Vertices.Decoded, v.PositionLayout, p.PackedFrameLayout), DirectionalUnchangedWords(observed, v.PositionLayout, p.PackedFrameLayout));
        return (facts, audit, dispatch);
    }
}
