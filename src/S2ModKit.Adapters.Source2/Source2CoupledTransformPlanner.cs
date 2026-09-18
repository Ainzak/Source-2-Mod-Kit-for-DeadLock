using System.Buffers.Binary;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

internal static class Source2CoupledTransformPlanner
{
    internal const float MinimumScale = 0.25f;
    internal const float MaximumScale = 4f;
    internal const float MaximumHardDisplacement = 256f;

    private static readonly string[] VisualAllowedByteClasses =
    [
        "mbuf.selected_position_bytes",
        "mdat.scene_bounds",
        "mdat.bone_bounds",
        "mdat.bone_culling_radius",
    ];

    private static readonly string[] CollisionAllowedByteClasses =
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

    public static PlannedCoupledTransformTarget Plan(
        int meshResourceBlockIndex,
        ContentHash meshBlockInputHash,
        Source2ResourceBlock mbufBlock,
        Source2RawMbufAnalysis visual,
        Source2WholeMeshTransformAnalysis visualMetadata,
        Source2ConvexPhysAnalysis collision,
        Point3 pivot,
        float uniformScale,
        float maximumVertexDisplacement,
        float maximumCollisionDisplacement)
    {
        ArgumentNullException.ThrowIfNull(mbufBlock);
        ArgumentNullException.ThrowIfNull(visual);
        ArgumentNullException.ThrowIfNull(visualMetadata);
        ArgumentNullException.ThrowIfNull(collision);
        ValidateLimits(uniformScale, maximumVertexDisplacement, maximumCollisionDisplacement);
        if (visual.Geometry.VertexBuffers.Count != 1
            || visual.Geometry.IndexBuffers.Count != 1
            || visual.Geometry.DrawCalls.Count != 1
            || visualMetadata.BoneBounds.Length == 0
            || visualMetadata.VertexCount != visual.Geometry.VertexBuffers[0].Snapshot.VertexCount
            || visualMetadata.SceneBounds != visual.Geometry.DrawCalls[0].Snapshot.Bounds
            || mbufBlock.Index != visual.Geometry.VertexBuffers[0].Snapshot.ResourceBlockIndex
            || collision.ResourceBlockIndex == mbufBlock.Index
            || collision.ResourceBlockIndex == meshResourceBlockIndex)
        {
            throw Incomplete("The visual and collision halves do not resolve to one distinct, complete coupled target.");
        }

        var transform = new UniformTransform(pivot, uniformScale, new Point3(0f, 0f, 0f));
        var vertices = visual.Geometry.VertexBuffers[0];
        var selectedVertices = Enumerable.Range(0, vertices.Snapshot.VertexCount).ToArray();
        var transformedVisual = DecodedPositionBufferTransformer.Apply(
            vertices.Decoded,
            vertices.Snapshot.VertexCount,
            vertices.Snapshot.PositionLayout,
            selectedVertices,
            transform);
        if (transformedVisual.ChangedVertexCount == 0
            || transformedVisual.MaximumDisplacement > maximumVertexDisplacement)
        {
            throw Incomplete("The visual transform is empty or exceeds its declared displacement ceiling.");
        }

        ConvexHullTransformResult transformedCollision;
        try
        {
            transformedCollision = ConvexHullGeometry.Transform(
                collision.VertexPositions,
                collision.Faces,
                collision.RegionPlanes.Select(plane => new Plane3(plane.Normal, plane.Offset)).ToArray(),
                transform);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
        {
            throw Incomplete("The collision transform cannot reproduce every required convex value.", exception);
        }

        if (transformedCollision.PositionSummary.ChangedPointCount == 0
            || transformedCollision.PositionSummary.MaximumDisplacement > maximumCollisionDisplacement)
        {
            throw Incomplete("The collision transform is empty or exceeds its declared displacement ceiling.");
        }

        var expectedMbuf = Source2RawMbufMutation.CreateTransformedPayload(mbufBlock, visual, transformedVisual.TransformedDecoded.Span);
        var visualTarget = new PlannedRawMbufTransformTarget(
            meshResourceBlockIndex,
            mbufBlock.Index,
            meshBlockInputHash,
            ContentHash.Compute(mbufBlock.Payload.Span),
            ContentHash.Compute(expectedMbuf),
            vertices.Snapshot.DecodedHash,
            transformedVisual.OutputHash,
            visual.Geometry.IndexBuffers[0].Snapshot.DecodedHash,
            transformedVisual.SelectedVertexSetHash,
            visual.DrawCall.Id,
            visual.DrawCall.MaterialPath,
            visualMetadata.LocalSkinningRootBone,
            visualMetadata.LocalSkinningRootBoneHash,
            vertices.Snapshot.VertexCount,
            vertices.Snapshot.PositionLayout,
            transformedVisual.BoundsBefore,
            transformedVisual.BoundsAfter,
            ToVector(pivot),
            uniformScale,
            transformedVisual.MaximumDisplacement,
            maximumVertexDisplacement,
            VisualAllowedByteClasses,
            PlanBoneBounds(visualMetadata, vertices, transform, pivot));

        var before = collision.DerivedValues;
        var after = transformedCollision.After;
        var collisionTarget = new PlannedConvexPhysTransformTarget(
            collision.ResourceBlockIndex,
            collision.PayloadHash,
            HashPoints(collision.VertexPositions),
            HashPoints(transformedCollision.Positions),
            collision.VertexPositions.Length,
            ToVector(pivot),
            uniformScale,
            transformedCollision.PositionSummary.MaximumDisplacement,
            maximumCollisionDisplacement,
            ToPlan(before, collision.MassProperties),
            ToPlan(after, ToMassProperties(after)),
            PlanPlanes(
                collision.HullPlanes,
                collision.HullPlanes.Select(plane => ConvexHullGeometry.TransformPlane(new Plane3(plane.Normal, plane.Offset), transform)).ToArray()),
            PlanPlanes(collision.RegionPlanes, transformedCollision.RegionPlanes),
            collision.HullVertexEdgesHash,
            collision.HalfEdgesHash,
            collision.FacesHash,
            collision.RegionNodesHash,
            collision.HullPlanesHash,
            collision.RegionPlanesHash,
            collision.HalfEdgeCount,
            collision.FaceCount,
            collision.RegionNodeCount,
            new TransformVector3
            {
                X = collision.OrthographicAreaX,
                Y = collision.OrthographicAreaY,
                Z = collision.OrthographicAreaZ,
            },
            collision.CollisionGroupString,
            CollisionAllowedByteClasses);
        return new PlannedCoupledTransformTarget(
            visualTarget,
            collisionTarget,
            [
                new PlannedTargetBlock(meshResourceBlockIndex, "MDAT", meshBlockInputHash),
                new PlannedTargetBlock(mbufBlock.Index, "MBUF", visualTarget.MbufBlockInputHash),
                new PlannedTargetBlock(collision.ResourceBlockIndex, "PHYS", collision.PayloadHash),
            ]);
    }

    private static void ValidateLimits(float scale, float visualLimit, float collisionLimit)
    {
        if (!float.IsFinite(scale) || scale < MinimumScale || scale > MaximumScale || scale == 1f
            || !float.IsFinite(visualLimit) || visualLimit <= 0f || visualLimit > MaximumHardDisplacement
            || !float.IsFinite(collisionLimit) || collisionLimit <= 0f || collisionLimit > MaximumHardDisplacement)
        {
            throw Incomplete("The coupled scale or displacement limits are outside the accepted bounded contract.");
        }
    }

    private static PlannedBoneBoundsTarget[] PlanBoneBounds(
        Source2WholeMeshTransformAnalysis metadata,
        Source2VertexBufferAnalysis vertices,
        UniformTransform transform,
        Point3 pivot) => metadata.BoneBounds.Select(bone =>
    {
        var localPivot = Source2TransformMetadataAnalyzer.TransformPoint(pivot, bone.InverseBindPose);
        var localTransform = new UniformTransform(localPivot, transform.Scale, new Point3(0f, 0f, 0f));
        var center = localTransform.Apply(new Point3(bone.LocalBoundsCenter.X, bone.LocalBoundsCenter.Y, bone.LocalBoundsCenter.Z));
        var size = new TransformVector3
        {
            X = checked(bone.LocalBoundsSize.X * transform.Scale),
            Y = checked(bone.LocalBoundsSize.Y * transform.Scale),
            Z = checked(bone.LocalBoundsSize.Z * transform.Scale),
        };
        var expectedRadius = bone.InfluencedVertices.Max(vertex =>
        {
            var transformed = transform.Apply(Source2GeometryAnalyzer.ReadPosition(vertices, vertex));
            var local = Source2TransformMetadataAnalyzer.TransformPoint(transformed, bone.InverseBindPose);
            return MathF.Sqrt((local.X * local.X) + (local.Y * local.Y) + (local.Z * local.Z));
        });
        var expectedCenter = ToVector(center);
        return new PlannedBoneBoundsTarget(
            bone.BoneIndex,
            bone.BoneName,
            bone.InverseBindPoseHash,
            new ContentHash(VertexSetHash.Compute(bone.InfluencedVertices)),
            bone.InfluencedVertices.Length,
            bone.LocalBoundsCenter,
            bone.LocalBoundsSize,
            bone.LocalBounds,
            expectedCenter,
            size,
            BoundsFromCenterSize(expectedCenter, size),
            ToVector(localPivot),
            new TransformVector3(),
            bone.SphereRadius,
            expectedRadius);
    }).ToArray();

    private static PlannedPlaneTransformTarget[] PlanPlanes(
        Source2PhysPlane[] before,
        IReadOnlyList<Plane3> after)
    {
        if (before.Length != after.Count)
        {
            throw Incomplete("The transformed collision plane cardinality changed.");
        }

        return before.Select((plane, index) => new PlannedPlaneTransformTarget(
            ToVector(plane.Normal),
            plane.Offset,
            after[index].Offset)).ToArray();
    }

    private static PlannedConvexDerivedValues ToPlan(ConvexHullDerivedValues values, IReadOnlyList<float> massProperties) => new(
        ToBounds(values.Bounds),
        ToVector(values.VertexCentroid),
        values.MaximumAngularRadius,
        values.Volume,
        values.SurfaceArea,
        ToVector(values.MassProperties.CenterOfMass),
        massProperties.ToArray());

    private static float[] ToMassProperties(ConvexHullDerivedValues values)
    {
        var inertia = values.MassProperties.Inertia;
        var center = values.MassProperties.CenterOfMass;
        return
        [
            inertia.XX, inertia.XY, inertia.XZ, center.X,
            inertia.XY, inertia.YY, inertia.YZ, center.Y,
            inertia.XZ, inertia.YZ, inertia.ZZ, center.Z,
        ];
    }

    private static ContentHash HashPoints(IReadOnlyList<Point3> points)
    {
        var bytes = new byte[checked(points.Count * 12)];
        for (var index = 0; index < points.Count; index++)
        {
            var offset = index * 12;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), BitConverter.SingleToInt32Bits(points[index].X));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 4), BitConverter.SingleToInt32Bits(points[index].Y));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 8), BitConverter.SingleToInt32Bits(points[index].Z));
        }

        return ContentHash.Compute(bytes);
    }

    private static TransformVector3 ToVector(Point3 point) => new() { X = point.X, Y = point.Y, Z = point.Z };

    private static GeometryBounds ToBounds(Bounds3 bounds) => new(ToVector(bounds.Min), ToVector(bounds.Max));

    private static GeometryBounds BoundsFromCenterSize(TransformVector3 center, TransformVector3 size) => new(
        new TransformVector3 { X = center.X - (size.X / 2f), Y = center.Y - (size.Y / 2f), Z = center.Z - (size.Z / 2f) },
        new TransformVector3 { X = center.X + (size.X / 2f), Y = center.Y + (size.Y / 2f), Z = center.Z + (size.Z / 2f) });

    private static S2ModKitException Incomplete(string summary, Exception? inner = null) => new(
        new S2Error(
            "COUPLED_TRANSFORM_INCOMPLETE",
            "source2_adapter",
            summary,
            "Regenerate the plan from an intact resource satisfying both bounded halves.",
            ErrorCategory.UnsupportedCapability),
        inner);
}
