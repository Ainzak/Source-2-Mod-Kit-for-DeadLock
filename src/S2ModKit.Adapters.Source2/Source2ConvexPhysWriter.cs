using System.Buffers.Binary;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

internal sealed record Source2ConvexPhysRewriteResult(
    ReadOnlyMemory<byte> Payload,
    Source2ConvexPhysAnalysis Reopened,
    ContentHash SemanticHash);

/// <summary>Isolated convex-PHYS rewrite. It cannot rebuild or publish a resource envelope.</summary>
internal static class Source2ConvexPhysWriter
{
    private static readonly string[] AllowedByteClasses =
    [
        "phys.convex_positions",
        "phys.convex_bounds",
        "phys.convex_centroid",
        "phys.convex_angular_radius",
        "phys.hull_plane_offsets",
        "phys.region_plane_offsets",
        "phys.convex_volume",
        "phys.convex_surface_area",
        "phys.convex_mass_properties",
    ];

    public static Source2ConvexPhysRewriteResult Rewrite(
        PhysAggregateData block,
        KVObject controlRoot,
        Source2ResourceEnvelope envelope,
        GeometryBounds beforeVisualBounds,
        GeometryBounds expectedVisualBounds,
        PlannedConvexPhysTransformTarget target)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(controlRoot);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(beforeVisualBounds);
        ArgumentNullException.ThrowIfNull(expectedVisualBounds);
        ArgumentNullException.ThrowIfNull(target);
        var source = Source2ConvexPhysReader.AnalyzeDetailed(
            controlRoot,
            envelope,
            index => index == target.ResourceBlockIndex ? block.Data : null,
            beforeVisualBounds,
            "planned convex-PHYS rewrite");
        ValidateSource(source, target);

        var transform = new UniformTransform(ToPoint(target.FrozenPivot), target.UniformScale, new Point3(0f, 0f, 0f));
        var transformed = ConvexHullGeometry.Transform(
            source.VertexPositions,
            source.Faces,
            source.RegionPlanes.Select(plane => new Plane3(plane.Normal, plane.Offset)).ToArray(),
            transform);
        ValidateExpectedResult(source, transformed, target);
        RewriteSemanticValues(block.Data, source, transformed, target);

        using var roundTrip = PhysBlockRoundTrip.SerializeAndVerify(block, "convex-PHYS rewrite");
        var reopenedEnvelope = ReplacePayload(envelope, target.ResourceBlockIndex, roundTrip.Payload);
        var reopened = Source2ConvexPhysReader.AnalyzeDetailed(
            controlRoot,
            reopenedEnvelope,
            index => index == target.ResourceBlockIndex ? roundTrip.Reopened.Data : null,
            expectedVisualBounds,
            "reopened convex-PHYS rewrite");
        VerifyReopened(source, reopened, target);
        return new Source2ConvexPhysRewriteResult(roundTrip.Payload, reopened, roundTrip.SemanticHash);
    }

    private static void ValidateSource(Source2ConvexPhysAnalysis source, PlannedConvexPhysTransformTarget target)
    {
        if (source.ResourceBlockIndex != target.ResourceBlockIndex
            || source.PayloadHash != target.PayloadInputHash
            || HashPoints(source.VertexPositions) != target.PositionInputHash
            || source.VertexPositions.Length != target.VertexCount
            || source.HullVertexEdgesHash != target.HullVertexEdgesHash
            || source.HalfEdgesHash != target.HalfEdgesHash
            || source.FacesHash != target.FacesHash
            || source.RegionNodesHash != target.RegionNodesHash
            || source.HullPlanesHash != target.HullPlanesInputHash
            || source.RegionPlanesHash != target.RegionPlanesInputHash
            || source.HalfEdgeCount != target.HalfEdgeCount
            || source.FaceCount != target.FaceCount
            || source.RegionNodeCount != target.RegionNodeCount
            || !string.Equals(source.CollisionGroupString, target.CollisionGroupString, StringComparison.Ordinal)
            || !EqualsDerived(source.DerivedValues, source.MassProperties, target.Before)
            || source.OrthographicAreaX != target.UnchangedOrthographicAreaFractions.X
            || source.OrthographicAreaY != target.UnchangedOrthographicAreaFractions.Y
            || source.OrthographicAreaZ != target.UnchangedOrthographicAreaFractions.Z
            || target.AllowedByteClasses is null
            || !target.AllowedByteClasses.SequenceEqual(AllowedByteClasses, StringComparer.Ordinal))
        {
            throw Incomplete("The current convex-PHYS identity, topology, or derived facts differ from the coupled plan.");
        }
    }

    private static void ValidateExpectedResult(
        Source2ConvexPhysAnalysis source,
        ConvexHullTransformResult transformed,
        PlannedConvexPhysTransformTarget target)
    {
        if (transformed.PositionSummary.ChangedPointCount == 0
            || transformed.PositionSummary.MaximumDisplacement > target.DisplacementLimit
            || BitConverter.SingleToInt32Bits(transformed.PositionSummary.MaximumDisplacement) != BitConverter.SingleToInt32Bits(target.MaximumDisplacement)
            || HashPoints(transformed.Positions) != target.ExpectedPositionHash
            || !EqualsDerived(transformed.After, ToMassProperties(transformed.After), target.ExpectedAfter)
            || !MatchesPlanes(source.HullPlanes, transformed.After.FacePlanes, target.HullPlanes)
            || !MatchesPlanes(source.RegionPlanes, transformed.RegionPlanes, target.RegionPlanes))
        {
            throw Incomplete("The recomputed convex-PHYS transform differs from the coupled plan.");
        }
    }

    private static void RewriteSemanticValues(
        KVObject root,
        Source2ConvexPhysAnalysis source,
        ConvexHullTransformResult transformed,
        PlannedConvexPhysTransformTarget target)
    {
        var hull = RequireHull(root);
        KvNumericMutation.ReplaceVector3(hull, "m_vCentroid", ToVector(source.Centroid), target.ExpectedAfter.VertexCentroid, "PHYS hull");
        KvNumericMutation.ReplaceSingle(hull, "m_flMaxAngularRadius", source.MaxAngularRadius, target.ExpectedAfter.MaximumAngularRadius, "PHYS hull");
        KvNumericMutation.ReplaceSingle(hull, "m_flVolume", source.Volume, target.ExpectedAfter.Volume, "PHYS hull");
        KvNumericMutation.ReplaceSingle(hull, "m_flSurfaceArea", source.SurfaceArea, target.ExpectedAfter.SurfaceArea, "PHYS hull");
        var bounds = RequireCollection(hull, "m_Bounds", "PHYS hull");
        KvNumericMutation.ReplaceVector3(bounds, "m_vMinBounds", target.Before.Bounds.Min, target.ExpectedAfter.Bounds.Min, "PHYS hull bounds");
        KvNumericMutation.ReplaceVector3(bounds, "m_vMaxBounds", target.Before.Bounds.Max, target.ExpectedAfter.Bounds.Max, "PHYS hull bounds");
        ReplaceBlob(hull, "m_VertexPositions", HashPoints(source.VertexPositions), SerializePoints(transformed.Positions), "PHYS hull");
        ReplaceBlob(hull, "m_Planes", source.HullPlanesHash, SerializePlanes(target.HullPlanes), "PHYS hull");
        var region = RequireCollection(hull, "m_pRegionSVM", "PHYS hull");
        ReplaceBlob(region, "m_Planes", source.RegionPlanesHash, SerializePlanes(target.RegionPlanes), "PHYS region");
        KvNumericMutation.ReplaceFloatArray(
            hull,
            "m_MassProperties",
            source.MassProperties,
            target.ExpectedAfter.MassProperties,
            "PHYS hull");
    }

    private static void VerifyReopened(
        Source2ConvexPhysAnalysis before,
        Source2ConvexPhysAnalysis after,
        PlannedConvexPhysTransformTarget target)
    {
        if (HashPoints(after.VertexPositions) != target.ExpectedPositionHash
            || !EqualsDerived(after.DerivedValues, after.MassProperties, target.ExpectedAfter)
            || after.HullVertexEdgesHash != before.HullVertexEdgesHash
            || after.HalfEdgesHash != before.HalfEdgesHash
            || after.FacesHash != before.FacesHash
            || after.RegionNodesHash != before.RegionNodesHash
            || after.HalfEdgeCount != before.HalfEdgeCount
            || after.FaceCount != before.FaceCount
            || after.RegionNodeCount != before.RegionNodeCount
            || !string.Equals(after.CollisionGroupString, before.CollisionGroupString, StringComparison.Ordinal)
            || after.OrthographicAreaX != before.OrthographicAreaX
            || after.OrthographicAreaY != before.OrthographicAreaY
            || after.OrthographicAreaZ != before.OrthographicAreaZ
            || !MatchesPlannedPlanes(after.HullPlanes, target.HullPlanes)
            || !MatchesPlannedPlanes(after.RegionPlanes, target.RegionPlanes))
        {
            throw Incomplete("The reopened convex-PHYS result violates the coupled plan or changed immutable facts.");
        }
    }

    private static KVObject RequireHull(KVObject root)
    {
        var parts = RequireArray(root, "m_parts", "PHYS root");
        if (parts.Count != 1)
        {
            throw Incomplete("PHYS root no longer contains exactly one part.");
        }

        var part = RequireCollectionValue(parts[0], "PHYS part");
        var shape = RequireCollection(part, "m_rnShape", "PHYS part");
        var hulls = RequireArray(shape, "m_hulls", "PHYS shape");
        if (hulls.Count != 1)
        {
            throw Incomplete("PHYS shape no longer contains exactly one hull.");
        }

        var descriptor = RequireCollectionValue(hulls[0], "PHYS hull descriptor");
        return RequireCollection(descriptor, "m_Hull", "PHYS hull descriptor");
    }

    private static KVObject RequireArray(KVObject parent, string key, string context)
    {
        if (!parent.TryGetValue(key, out var value) || value is null || !value.IsArray)
        {
            throw Incomplete($"{context} is missing array {key}.");
        }

        return value;
    }

    private static KVObject RequireCollection(KVObject parent, string key, string context)
    {
        if (!parent.TryGetValue(key, out var value) || value is null)
        {
            throw Incomplete($"{context} is missing collection {key}.");
        }

        return RequireCollectionValue(value, $"{context}.{key}");
    }

    private static KVObject RequireCollectionValue(KVObject value, string context)
    {
        if (!value.IsCollection)
        {
            throw Incomplete($"{context} is not a collection.");
        }

        return value;
    }

    private static void ReplaceBlob(KVObject parent, string key, ContentHash expectedHash, byte[] replacement, string context)
    {
        if (!parent.TryGetValue(key, out var current)
            || current is null
            || current.ValueType != KVValueType.BinaryBlob
            || ContentHash.Compute(current.AsBlob()) != expectedHash)
        {
            throw Incomplete($"{context}.{key} drifted from the coupled plan.");
        }

        var value = KVObject.Blob(replacement);
        value.Flag = current.Flag;
        parent[key] = value;
    }

    private static bool EqualsDerived(ConvexHullDerivedValues actual, IReadOnlyList<float> massProperties, PlannedConvexDerivedValues expected) =>
        ToBounds(actual.Bounds) == expected.Bounds
        && ToVector(actual.VertexCentroid) == expected.VertexCentroid
        && actual.MaximumAngularRadius == expected.MaximumAngularRadius
        && actual.Volume == expected.Volume
        && actual.SurfaceArea == expected.SurfaceArea
        && ToVector(actual.MassProperties.CenterOfMass) == expected.CenterOfMass
        && massProperties.SequenceEqual(expected.MassProperties);

    private static bool MatchesPlanes(
        Source2PhysPlane[] before,
        IReadOnlyList<Plane3> after,
        IReadOnlyList<PlannedPlaneTransformTarget> planned) =>
        before.Length == after.Count
        && before.Length == planned.Count
        && before.Select((plane, index) =>
            ToVector(plane.Normal) == planned[index].Normal
            && plane.Offset == planned[index].BeforeOffset
            && NearlyEqual(after[index].Normal.X, planned[index].Normal.X)
            && NearlyEqual(after[index].Normal.Y, planned[index].Normal.Y)
            && NearlyEqual(after[index].Normal.Z, planned[index].Normal.Z)
            && NearlyEqual(after[index].Offset, planned[index].ExpectedOffset)).All(value => value);

    private static bool MatchesPlannedPlanes(Source2PhysPlane[] actual, IReadOnlyList<PlannedPlaneTransformTarget> planned) =>
        actual.Length == planned.Count
        && actual.Select((plane, index) => ToVector(plane.Normal) == planned[index].Normal && plane.Offset == planned[index].ExpectedOffset).All(value => value);

    private static Source2ResourceEnvelope ReplacePayload(
        Source2ResourceEnvelope envelope,
        int blockIndex,
        ReadOnlyMemory<byte> payload) => envelope with
        {
            Blocks = envelope.Blocks.Select(block => block.Index == blockIndex ? block with { Payload = payload } : block).ToArray(),
        };

    private static byte[] SerializePoints(IReadOnlyList<Point3> points)
    {
        var result = new byte[checked(points.Count * 12)];
        for (var index = 0; index < points.Count; index++)
        {
            WriteSingle(result, index * 12, points[index].X);
            WriteSingle(result, (index * 12) + 4, points[index].Y);
            WriteSingle(result, (index * 12) + 8, points[index].Z);
        }

        return result;
    }

    private static byte[] SerializePlanes(IReadOnlyList<Plane3> planes)
    {
        var result = new byte[checked(planes.Count * 16)];
        for (var index = 0; index < planes.Count; index++)
        {
            WriteSingle(result, index * 16, planes[index].Normal.X);
            WriteSingle(result, (index * 16) + 4, planes[index].Normal.Y);
            WriteSingle(result, (index * 16) + 8, planes[index].Normal.Z);
            WriteSingle(result, (index * 16) + 12, planes[index].Offset);
        }

        return result;
    }

    private static byte[] SerializePlanes(IReadOnlyList<PlannedPlaneTransformTarget> planes)
    {
        var result = new byte[checked(planes.Count * 16)];
        for (var index = 0; index < planes.Count; index++)
        {
            WriteSingle(result, index * 16, planes[index].Normal.X);
            WriteSingle(result, (index * 16) + 4, planes[index].Normal.Y);
            WriteSingle(result, (index * 16) + 8, planes[index].Normal.Z);
            WriteSingle(result, (index * 16) + 12, planes[index].ExpectedOffset);
        }

        return result;
    }

    private static ContentHash HashPoints(IReadOnlyList<Point3> points) => ContentHash.Compute(SerializePoints(points));

    private static float[] ToMassProperties(ConvexHullDerivedValues values)
    {
        var inertia = values.MassProperties.Inertia;
        var center = values.MassProperties.CenterOfMass;
        return [inertia.XX, inertia.XY, inertia.XZ, center.X, inertia.XY, inertia.YY, inertia.YZ, center.Y, inertia.XZ, inertia.YZ, inertia.ZZ, center.Z];
    }

    private static void WriteSingle(byte[] bytes, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));

    private static TransformVector3 ToVector(Point3 point) => new() { X = point.X, Y = point.Y, Z = point.Z };
    private static Point3 ToPoint(TransformVector3 point) => new(point.X, point.Y, point.Z);
    private static GeometryBounds ToBounds(Bounds3 bounds) => new(ToVector(bounds.Min), ToVector(bounds.Max));

    private static bool NearlyEqual(float left, float right)
    {
        var tolerance = 2e-4f * MathF.Max(1f, MathF.Max(MathF.Abs(left), MathF.Abs(right)));
        return MathF.Abs(left - right) <= tolerance;
    }

    private static S2ModKitException Incomplete(string summary) => Errors.Verification(
        "COUPLED_TRANSFORM_INCOMPLETE",
        summary,
        "Discard the isolated collision result and regenerate the complete coupled plan.");
}
