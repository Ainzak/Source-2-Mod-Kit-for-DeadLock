using System.Buffers.Binary;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static EllipsoidScale EllipsoidMath(EllipsoidVisualTransform intent, float limit)
    {
        RecipeValidator.ValidateEllipsoidIntent(intent, limit);
        try { return new(ToAffinePoint(intent.Field.Center), ToAffinePoint(intent.Field.OuterRadii), intent.Field.CoreFraction, intent.UniformScale, limit); }
        catch (ArgumentException e) { throw ExperimentalUnsupported("ELLIPSOID_JACOBIAN_UNPROVEN", e.Message); }
    }

    private TransformPlanningResult PlanEllipsoidTransform(TransformPlanningRequest request, ParsedModel parsed)
    {
        var (profiles, _) = ResolveExperimentalPlanningProfiles(request, parsed);
        var (model, spheres, roots) = ResolveExperimentalRootMetadata(parsed, profiles);
        var math = EllipsoidMath(request.Operation.LocalTransform!, request.Operation.Limits.MaximumVertexDisplacement);
        var buffers = new List<PlannedEllipsoidBuffer>();
        var boxes = new List<PlannedExperimentalBoxTarget>();
        var preserved = new List<PlannedExperimentalPreservationTarget>();
        using var codec = OpenGeometryCodec();
        foreach (var profile in profiles)
        {
            var (bytes, points, facts) = PlanEllipsoidBuffer(request.Input, profile, math);
            var encoded = EncodeDeterministically(codec, bytes, profile.Vertices.Snapshot, "ellipsoid planning");
            if (!codec.DecodeVertexBuffer(encoded, profile.Vertices.Snapshot.VertexCount, profile.Vertices.Snapshot.Stride).AsSpan().SequenceEqual(bytes))
                throw ExperimentalUnsupported("EXPERIMENTAL_CODEC_ROUNDTRIP_FAILED", "The ellipsoid buffer does not survive an exact codec round trip.");
            buffers.Add(facts);
            var allAfter = (Point3[])profile.AllBeforePositions.Clone();
            points.CopyTo(allAfter, profile.BufferBaseOffset);
            boxes.Add(PlanExperimentalSceneBox(request.Input, profile, allAfter));
            var affected = profile.Metadata.BoneBounds.Where(b => b.InfluencedVertices.Any(v =>
                v >= profile.BufferBaseOffset && v < profile.BufferBaseOffset + points.Length)).ToArray();
            if (affected.Length == 0) throw ExperimentalUnsupported("EXPERIMENTAL_SKINNING_UNSUPPORTED", "No complete affected bone contributor set is available.");
            foreach (var bone in affected) boxes.Add(PlanExperimentalBoneBox(request.Input, profile, bone, allAfter));
            AddExperimentalRenderSphereTargets(parsed, profile, affected.Select(b => b.BoneIndex).ToHashSet(), preserved);
        }
        AddExperimentalPlanningRootSpheres(parsed, model, spheres, roots, preserved);
        AddExperimentalBlockPreservation(parsed, preserved);
        var sourceBlocks = parsed.Envelope.Blocks.Select(b => new PlannedTargetBlock(b.Index, b.Type, ContentHash.Compute(b.Payload.Span))).ToArray();
        var target = new PlannedEllipsoidTransformTarget(request.Input.ContentHash, request.Input.ContentHash,
            "root_complete_buffer_ellipsoid_visual", 1, "retain_expand_boxes_preserve_runtime", 1,
            request.Operation.RuntimeMetadataPolicy!, NormalizeExperimentalSelector(request.Operation.Selector), request.Operation.LocalTransform!,
            EllipsoidContractValidator.Certificate(math.Certificate), buffers.Max(b => b.MaximumDisplacement), math.DisplacementLimit,
            buffers, boxes.OrderBy(b => b.ResourceBlockIndex).ThenBy(b => b.FieldPath, StringComparer.Ordinal).ToArray(),
            preserved.OrderBy(b => b.ResourceBlockIndex).ThenBy(b => b.FieldPath, StringComparer.Ordinal).ToArray(), sourceBlocks);
        target = target with { TargetFingerprint = MutationPlanJson.ComputeEllipsoidTargetFingerprint(target) };
        EllipsoidContractValidator.ValidateTarget(target);
        return new([], [], buffers.SelectMany(b => new[] { b.ResourceBlockIndex, b.VertexResourceBlockIndex }).Distinct().Order()
            .Select(i => sourceBlocks[i]).ToArray())
        { EllipsoidTransformTarget = target };
    }

    private static (byte[] Bytes, Point3[] Points, PlannedEllipsoidBuffer Facts) PlanEllipsoidBuffer(
        ArtifactContent input, Source2AffineProfile profile, EllipsoidScale math)
    {
        var calculated = CalculateEllipsoidWords(profile.Vertices.Decoded, profile.Vertices.Snapshot.PositionLayout, profile.PackedFrameLayout, profile.SelectedVertices.Length, math);
        var (bytes, points, maskHash, weightHash, core, transition, pinned, positions, frames, maximum) = calculated;
        if (positions == 0) throw ExperimentalUnsupported("ELLIPSOID_EMPTY_LOD_EFFECT", $"The field changes no stored position in LOD {profile.Mesh.Lod}.");
        try { ValidateRegionTriangles(profile, points); }
        catch (ArgumentException e) { throw ExperimentalUnsupported("ELLIPSOID_TRIANGLE_INVALID", e.Message); }
        var v = profile.Vertices.Snapshot;
        var i = profile.Indices.Snapshot;
        return (bytes, points, new(profile.Mesh.Lod, input.LogicalPath, profile.Mesh.MeshOrdinal, profile.Mesh.BlockIndex,
            v.Ordinal, i.Ordinal, v.ResourceBlockIndex, i.ResourceBlockIndex, v.EncodedHash, i.EncodedHash,
            v.DecodedHash, ContentHash.Compute(bytes), i.DecodedHash, profile.VertexSetHash, points.Length, "exclusive",
            v.PositionLayout, profile.PackedFrameLayout, profile.Mesh.Geometry.Codec!, maskHash, weightHash,
            core, transition, pinned, positions, frames, EllipsoidPositionHash(profile.Vertices.Decoded, v.PositionLayout, points.Length),
            EllipsoidPositionHash(bytes, v.PositionLayout, points.Length), Source2PackedFrameCodec.HashSelected(profile.Vertices.Decoded, profile.PackedFrameLayout, profile.SelectedVertices),
            Source2PackedFrameCodec.HashSelected(bytes, profile.PackedFrameLayout, profile.SelectedVertices),
            profile.SelectionBounds, ToAffineBounds(Bounds3.FromPoints(points)), maximum));
    }

    // Complete record deformation shared by planning/writing only. The reopen verifier
    // has a separate reconstruction loop and must never call this calculator.
    internal static EllipsoidWordCalculation CalculateEllipsoidWords(ReadOnlySpan<byte> decoded, PositionLayout positionLayout,
        PackedFrameLayout frameLayout, int count, EllipsoidScale math)
    {
        if (count <= 0 || positionLayout.Format != "R32G32B32_FLOAT" || frameLayout.Format != "R32_UINT"
            || frameLayout.EncodingProfile != Source2PackedFrameCodec.EncodingProfile || positionLayout.Offset < 0 || frameLayout.Offset < 0
            || (long)positionLayout.Offset + 12 > positionLayout.Stride || (long)frameLayout.Offset + 4 > frameLayout.Stride
            || (positionLayout.Offset < (long)frameLayout.Offset + 4 && frameLayout.Offset < (long)positionLayout.Offset + 12)
            || positionLayout.Stride != frameLayout.Stride || decoded.Length != (long)count * positionLayout.Stride)
            throw new ArgumentException("Require one complete decoded buffer with matching layouts.");
        var bytes = decoded.ToArray();
        var points = new Point3[count];
        var masks = new byte[checked(points.Length * 8)];
        var weights = new byte[checked(points.Length * 12)];
        int core = 0, transition = 0, pinned = 0, positions = 0, frames = 0;
        float maximum = 0;
        for (var vertex = 0; vertex < count; vertex++)
        {
            var original = new Point3(BinaryPrimitives.ReadSingleLittleEndian(decoded.Slice(vertex * positionLayout.Stride + positionLayout.Offset)), BinaryPrimitives.ReadSingleLittleEndian(decoded.Slice(vertex * positionLayout.Stride + positionLayout.Offset + 4)), BinaryPrimitives.ReadSingleLittleEndian(decoded.Slice(vertex * positionLayout.Stride + positionLayout.Offset + 8)));
            EllipsoidPointResult result;
            try { result = math.Evaluate(original); }
            catch (ArgumentException e) { throw EllipsoidNumericFailure(e); }
            points[vertex] = result.Position;
            BinaryPrimitives.WriteInt32LittleEndian(masks.AsSpan(vertex * 8), vertex);
            BinaryPrimitives.WriteInt32LittleEndian(masks.AsSpan(vertex * 8 + 4), (int)result.Membership);
            BinaryPrimitives.WriteInt32LittleEndian(weights.AsSpan(vertex * 12), vertex);
            BinaryPrimitives.WriteInt64LittleEndian(weights.AsSpan(vertex * 12 + 4), BitConverter.DoubleToInt64Bits(result.Weight));
            switch (result.Membership) { case EllipsoidMembership.Core: core++; break; case EllipsoidMembership.Transition: transition++; break; default: pinned++; break; }
            if (RegionPositionWordsChanged(original, result.Position)) positions++;
            maximum = Math.Max(maximum, result.MaximumDisplacement);
            var location = checked(vertex * frameLayout.Stride + frameLayout.Offset);
            var originalWord = BinaryPrimitives.ReadUInt32LittleEndian(decoded.Slice(location));
            // Validate every input frame, even where its exact packed word must be retained.
            uint packed;
            try
            {
                var frame = math.TransformFrame(original, Source2PackedFrameCodec.Decode(originalWord));
                packed = result.Membership == EllipsoidMembership.Transition ? Source2PackedFrameCodec.Encode(frame) : originalWord;
            }
            catch (ArgumentException e) { throw EllipsoidNumericFailure(e); }
            if (packed != originalWord) frames++;
            if (result.Membership != EllipsoidMembership.Pinned) WritePosition(bytes, positionLayout, vertex, result.Position);
            if (result.Membership == EllipsoidMembership.Transition) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(location), packed);
        }
        return new(bytes, points, ContentHash.Compute(masks), ContentHash.Compute(weights), core, transition, pinned, positions, frames, maximum);
    }

    internal sealed record EllipsoidWordCalculation(byte[] Bytes, Point3[] Points, ContentHash MaskHash, ContentHash WeightHash,
        int Core, int Transition, int Pinned, int ChangedPositions, int ChangedFrames, float MaximumDisplacement);

    internal static ContentHash EllipsoidPositionHash(ReadOnlySpan<byte> bytes, PositionLayout layout, int count)
    {
        var positions = new byte[checked(count * 12)];
        for (var vertex = 0; vertex < count; vertex++) bytes.Slice(checked(vertex * layout.Stride + layout.Offset), 12).CopyTo(positions.AsSpan(vertex * 12));
        return ContentHash.Compute(positions);
    }

    private static S2ModKitException EllipsoidNumericFailure(ArgumentException exception) => ExperimentalUnsupported(
        exception.Message.Contains("displacement", StringComparison.OrdinalIgnoreCase) ? "TRANSFORM_DISPLACEMENT_EXCEEDED" : "ELLIPSOID_NUMERIC_UNREPRESENTABLE", exception.Message);

    private static S2ModKitException EllipsoidDrift(string message) => Errors.Verification("ELLIPSOID_RESULT_DRIFT", message, "Reject the output; regenerate from immutable source facts.");
}
