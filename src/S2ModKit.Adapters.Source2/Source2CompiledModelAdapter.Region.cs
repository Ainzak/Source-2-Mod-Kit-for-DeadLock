using System.Buffers.Binary;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static AxisRampScale RegionMath(RegionScaleSelection intent, float scale, TransformVector3 pivot) =>
        new(intent.Axis switch { "x" => 0, "y" => 1, "z" => 2, _ => throw ExperimentalAuditFailure("Unknown region axis.") },
            intent.PinnedThrough, intent.FullFrom, scale, ToAffinePoint(pivot));

    private static (byte[] Bytes, Point3[] Points, PlannedRegionBuffer Facts, float MaximumDisplacement) PlanRegionBuffer(
        Source2AffineProfile profile, RegionScaleSelection intent, float scale, TransformVector3 pivot)
    {
        var math = RegionMath(intent, scale, pivot);
        var bytes = (byte[])profile.Vertices.Decoded.Clone();
        var points = new Point3[profile.SelectedVertices.Length];
        var mask = new byte[checked(points.Length * 12)];
        int pinned = 0, transition = 0, full = 0, changed = 0;
        double max = 0;
        for (var offset = 0; offset < points.Length; offset++)
        {
            var vertex = profile.SelectedVertices[offset];
            var original = Source2GeometryAnalyzer.ReadPosition(profile.Vertices, vertex);
            var result = math.Evaluate(original);
            points[offset] = result.Position;
            BinaryPrimitives.WriteInt32LittleEndian(mask.AsSpan(offset * 12), vertex);
            BinaryPrimitives.WriteInt64LittleEndian(mask.AsSpan((offset * 12) + 4), BitConverter.DoubleToInt64Bits(result.Weight));
            if (result.Weight == 0) pinned++;
            else if (result.Weight == 1) full++;
            else transition++;
            if (RegionPositionWordsChanged(original, result.Position)) changed++;
            max = Math.Max(max, ConservativePointDistance.RoundUp(original, result.Position));
            if (result.Weight == 0) continue;
            WritePosition(bytes, profile.Vertices.Snapshot.PositionLayout, vertex, result.Position);
            if (result.Weight == 1) continue;
            var location = checked((vertex * profile.PackedFrameLayout.Stride) + profile.PackedFrameLayout.Offset);
            var inputFrame = Source2PackedFrameCodec.Decode(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(location)));
            var packed = Source2PackedFrameCodec.Encode(math.TransformFrame(original, inputFrame));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(location), packed);
        }
        if (changed == 0 || !double.IsFinite(max) || max > 64) throw ExperimentalAuditFailure("The region has no effect or exceeds the hard displacement limit.");
        ValidateRegionTriangles(profile, points);
        var facts = new PlannedRegionBuffer(profile.Mesh.Lod, profile.Mesh.MeshOrdinal, profile.Vertices.Snapshot.Ordinal,
            ContentHash.Compute(mask), pinned, transition, full, changed, profile.PackedFrameLayout,
            Source2PackedFrameCodec.HashSelected(profile.Vertices.Decoded, profile.PackedFrameLayout, profile.SelectedVertices),
            Source2PackedFrameCodec.HashSelected(bytes, profile.PackedFrameLayout, profile.SelectedVertices));
        // Round an observed maximum upward; a narrowed lower value cannot authorize a
        // displacement just above the recipe limit.
        var serializedMax = (float)max;
        if (serializedMax < max) serializedMax = MathF.BitIncrement(serializedMax);
        return (bytes, points, facts, serializedMax);
    }

    private static void ValidateRegionTriangles(Source2AffineProfile profile, Point3[] output)
    {
        var original = profile.SelectedVertices.Select(vertex => Source2GeometryAnalyzer.ReadPosition(profile.Vertices, vertex)).ToArray();
        foreach (var draw in profile.Mesh.GeometryAnalysis!.DrawCalls.Where(call => call.Snapshot.VertexBufferOrdinal == profile.Vertices.Snapshot.Ordinal))
        {
            var source = profile.Mesh.DrawCalls.Single(call => call.Snapshot.Id == draw.Snapshot.DrawCallId).Snapshot;
            var triangles = Enumerable.Range(checked((int)source.IndexStart), checked((int)source.IndexCount))
                .Select(index => checked((int)profile.Indices.Indices[index] + draw.Snapshot.BaseVertex)).ToArray();
            RegionTriangleGuard.Validate(original, output, triangles);
        }
    }

    // Independent reopen path: never invoke PlanRegionBuffer or consume its success.
    // Only the reviewed pure deformation/codec/triangle primitives are shared.
    private static byte[] VerifyRegionBuffer(Source2AffineProfile source, Source2AffineProfile observed,
        PlannedExperimentalTransformTarget target, PlannedGeometryTarget geometry)
    {
        var region = target.Region!;
        var facts = region.Buffers.Single(buffer => buffer.MeshOrdinal == source.Mesh.MeshOrdinal);
        if (facts.PackedFrameLayout != source.PackedFrameLayout || facts.PackedFrameLayout != observed.PackedFrameLayout)
            throw ExperimentalAuditFailure("The source/output packed frame layout drifted.");
        var math = RegionMath(region.Selection, target.UniformScale, target.Pivot.Point);
        var expected = (byte[])source.Vertices.Decoded.Clone();
        var maskWords = new byte[checked(source.SelectedVertices.Length * 12)];
        int pinned = 0, transition = 0, full = 0, changed = 0;
        double maximum = 0;
        var outputPoints = new Point3[source.SelectedVertices.Length];
        foreach (var vertex in source.SelectedVertices)
        {
            var point = Source2GeometryAnalyzer.ReadPosition(source.Vertices, vertex);
            var transformed = math.Evaluate(point);
            outputPoints[vertex] = Source2GeometryAnalyzer.ReadPosition(observed.Vertices, vertex);
            BinaryPrimitives.WriteInt32LittleEndian(maskWords.AsSpan(vertex * 12), vertex);
            BinaryPrimitives.WriteInt64LittleEndian(maskWords.AsSpan((vertex * 12) + 4), BitConverter.DoubleToInt64Bits(transformed.Weight));
            if (transformed.Weight == 0) pinned++;
            else if (transformed.Weight == 1) full++;
            else transition++;
            if (RegionPositionWordsChanged(point, transformed.Position)) changed++;
            maximum = Math.Max(maximum, ConservativePointDistance.RoundUp(point, transformed.Position));
            if (transformed.Weight == 0) continue;
            WritePosition(expected, source.Vertices.Snapshot.PositionLayout, vertex, transformed.Position);
            if (transformed.Weight == 1) continue;
            var location = checked((vertex * source.PackedFrameLayout.Stride) + source.PackedFrameLayout.Offset);
            var originalWord = BinaryPrimitives.ReadUInt32LittleEndian(source.Vertices.Decoded.AsSpan(location));
            var originalFrame = Source2PackedFrameCodec.Decode(originalWord);
            var frame = math.TransformFrame(point, originalFrame);
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(location), Source2PackedFrameCodec.Encode(frame));
        }
        var maximumWord = (float)maximum;
        if (maximumWord < maximum) maximumWord = MathF.BitIncrement(maximumWord);
        if (ContentHash.Compute(maskWords) != facts.MaskHash || pinned != facts.PinnedVertexCount
            || transition != facts.TransitionVertexCount || full != facts.FullVertexCount || changed != facts.ChangedVertexCount
            || changed == 0 || maximum > target.DisplacementLimit || maximumWord != geometry.MaximumDisplacement
            || Source2PackedFrameCodec.HashSelected(source.Vertices.Decoded, source.PackedFrameLayout, source.SelectedVertices) != facts.InputPackedFrameHash
            || Source2PackedFrameCodec.HashSelected(observed.Vertices.Decoded, observed.PackedFrameLayout, source.SelectedVertices) != facts.ExpectedPackedFrameHash)
            throw ExperimentalAuditFailure("Observed region membership, pinned counts, displacement or packed-frame identities differ from source policy.");
        if (!expected.AsSpan().SequenceEqual(observed.Vertices.Decoded))
            throw ExperimentalAuditFailure("Reopened region positions, frames, pinned records or undeclared words differ from independent policy evaluation.");
        ValidateRegionTriangles(source, outputPoints);
        return expected;
    }

    private static bool RegionPositionWordsChanged(Point3 before, Point3 after) =>
        BitConverter.SingleToUInt32Bits(before.X) != BitConverter.SingleToUInt32Bits(after.X)
        || BitConverter.SingleToUInt32Bits(before.Y) != BitConverter.SingleToUInt32Bits(after.Y)
        || BitConverter.SingleToUInt32Bits(before.Z) != BitConverter.SingleToUInt32Bits(after.Z);
}
