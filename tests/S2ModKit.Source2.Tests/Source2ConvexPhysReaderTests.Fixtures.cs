using System.Buffers.Binary;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;
using ValveKeyValue.KeyValues3;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Source2.Tests;

public sealed partial class Source2ConvexPhysReaderTests
{
    private static readonly (float X, float Y, float Z)[] TetraVertices =
    [
        (0f, 0f, 0f),
        (1f, 0f, 0f),
        (0f, 1f, 0f),
        (0f, 0f, 1f),
    ];

    // Outward-oriented counter-clockwise face cycles of the unit tetrahedron.
    private static readonly int[][] TetraFaces = [[0, 2, 1], [0, 1, 3], [0, 3, 2], [1, 2, 3]];

    private static void AssertCode(string expected, KVObject root)
    {
        var exception = Assert.Throws<S2ModKitException>(() => Source2ConvexPhysReader.AnalyzeDetailed(
            ControlRoot(),
            Envelope("phys"u8.ToArray()),
            _ => root,
            VisualBounds(),
            "synthetic physics"));

        Assert.Equal(expected, exception.Error.Code);
    }

    private static KVObject ControlRoot(int declaredIndex = PhysBlockIndex) => Object(
        ("embedded_meshes", KvArray(Object(("name", "synthetic_hat")))),
        ("embedded_physics", Object(("phys_data_block", declaredIndex))));

    private static GeometryBounds VisualBounds() => new(
        new TransformVector3 { X = -1f, Y = -1f, Z = -1f },
        new TransformVector3 { X = 2f, Y = 2f, Z = 2f });

    private static PlannedConvexPhysTransformTarget CollisionPlan(Source2ConvexPhysAnalysis source, float scale)
    {
        var pivot = new Point3(0f, 0f, 0f);
        var transformed = ConvexHullGeometry.Transform(
            source.VertexPositions,
            source.Faces,
            source.RegionPlanes.Select(plane => new Plane3(plane.Normal, plane.Offset)).ToArray(),
            new UniformTransform(pivot, scale, new Point3(0f, 0f, 0f)));
        return new PlannedConvexPhysTransformTarget(
            source.ResourceBlockIndex,
            source.PayloadHash,
            HashPoints(source.VertexPositions),
            HashPoints(transformed.Positions),
            source.VertexPositions.Length,
            ToVector(pivot),
            scale,
            transformed.PositionSummary.MaximumDisplacement,
            20f,
            ToPlan(source.DerivedValues, source.MassProperties),
            ToPlan(transformed.After, ToMassProperties(transformed.After)),
            source.HullPlanes.Select(plane =>
            {
                var after = ConvexHullGeometry.TransformPlane(new Plane3(plane.Normal, plane.Offset), new UniformTransform(pivot, scale, new Point3(0f, 0f, 0f)));
                return new PlannedPlaneTransformTarget(ToVector(plane.Normal), plane.Offset, after.Offset);
            }).ToArray(),
            source.RegionPlanes.Select((plane, index) => new PlannedPlaneTransformTarget(ToVector(plane.Normal), plane.Offset, transformed.RegionPlanes[index].Offset)).ToArray(),
            source.HullVertexEdgesHash,
            source.HalfEdgesHash,
            source.FacesHash,
            source.RegionNodesHash,
            source.HullPlanesHash,
            source.RegionPlanesHash,
            source.HalfEdgeCount,
            source.FaceCount,
            source.RegionNodeCount,
            new TransformVector3 { X = source.OrthographicAreaX, Y = source.OrthographicAreaY, Z = source.OrthographicAreaZ },
            source.CollisionGroupString,
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
            ]);
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
        return [inertia.XX, inertia.XY, inertia.XZ, center.X, inertia.XY, inertia.YY, inertia.YZ, center.Y, inertia.XZ, inertia.YZ, inertia.ZZ, center.Z];
    }

    private static ContentHash HashPoints(IReadOnlyList<Point3> points)
    {
        var bytes = new byte[points.Count * 12];
        for (var index = 0; index < points.Count; index++)
        {
            WriteSingle(bytes, index * 12, points[index].X);
            WriteSingle(bytes, (index * 12) + 4, points[index].Y);
            WriteSingle(bytes, (index * 12) + 8, points[index].Z);
        }

        return ContentHash.Compute(bytes);
    }

    private static TransformVector3 ToVector(Point3 point) => new() { X = point.X, Y = point.Y, Z = point.Z };

    private static GeometryBounds ToBounds(Bounds3 bounds) => new(ToVector(bounds.Min), ToVector(bounds.Max));

    private static PhysAggregateData PhysBlock(KVObject root, Resource resource, int serializationVersion = 5)
    {
        var encoded = new BinaryKV3(
            root,
            new KV3ID("generic", Guid.Parse("7412167c-06e9-4698-aff2-e63eb59037e7")),
            BlockType.PHYS)
        {
            SerializationVersion = serializationVersion,
            SerializationCompressionMethod = KV3BinaryCompressionMethod.Lz4,
            Resource = resource,
        };
        using var stream = new MemoryStream();
        encoded.Serialize(stream);
        var bytes = stream.ToArray();
        var result = new PhysAggregateData(BlockType.PHYS)
        {
            Offset = 0,
            Size = checked((uint)bytes.Length),
            Resource = resource,
        };
        using var input = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(input);
        result.Read(reader);
        return result;
    }

    private static Source2ResourceEnvelope Envelope(byte[] payload) => new(
        12,
        1,
        16,
        16,
        ReadOnlyMemory<byte>.Empty,
        [Block(PhysBlockIndex, payload)]);

    private static Source2ResourceBlock Block(int index, byte[] payload) => new(
        index,
        "PHYS",
        16,
        16,
        payload,
        ReadOnlyMemory<byte>.Empty);

    private static KVObject PhysRoot()
    {
        return Object(
            ("m_nFlags", 0),
            ("m_nRefCounter", 0),
            ("m_bonesHash", KvArray()),
            ("m_boneNames", KvArray()),
            ("m_indexNames", KvArray()),
            ("m_indexHash", KvArray()),
            ("m_bindPose", KvArray()),
            ("m_parts", KvArray(Part())),
            ("m_constraints2", KvArray()),
            ("m_joints", KvArray()),
            ("m_pFeModel", KVObject.Null()),
            ("m_boneParents", KvArray()),
            ("m_surfacePropertyHashes", KvArray(new KVObject(1977497166u))),
            ("m_collisionAttributes", KvArray(CollisionAttribute())),
            ("m_debugPartNames", KvArray()),
            ("m_embeddedKeyvalues", new KVObject(string.Empty)));
    }

    private static KVObject Part() => Object(
        ("m_nFlags", 0),
        ("m_flMass", 0f),
        ("m_rnShape", Object(
            ("m_spheres", KvArray()),
            ("m_capsules", KvArray()),
            ("m_hulls", KvArray(HullDescriptor())),
            ("m_meshes", KvArray()),
            ("m_CollisionAttributeIndices", KvArray()))),
        ("m_nCollisionAttributeIndex", 0),
        ("m_nReserved", 0),
        ("m_flInertiaScale", 1f),
        ("m_flLinearDamping", 0f),
        ("m_flAngularDamping", 0f),
        ("m_bOverrideMassCenter", false),
        ("m_vMassCenterOverride", Vector(0f, 0f, 0f)));

    private static KVObject HullDescriptor() => Object(
        ("m_nCollisionAttributeIndex", 0),
        ("m_nSurfacePropertyIndex", 0),
        ("m_UserFriendlyName", string.Empty),
        ("m_bUserFriendlyNameSealed", false),
        ("m_bUserFriendlyNameLong", false),
        ("m_nToolMaterialHash", 0),
        ("m_Hull", Hull()));

    private static KVObject Hull()
    {
        var (edges, faces, vertexEdges) = BuildTopology(TetraFaces);
        var planes = Planes();
        return Object(
            ("m_vCentroid", Vector(0.25f, 0.25f, 0.25f)),
            ("m_flMaxAngularRadius", 0.82915646f),
            ("m_Bounds", Object(
                ("m_vMinBounds", Vector(0f, 0f, 0f)),
                ("m_vMaxBounds", Vector(1f, 1f, 1f)))),
            ("m_vOrthographicAreas", Vector(0.5f, 0.5f, 0.5f)),
            ("m_MassProperties", MassProperties()),
            ("m_flVolume", 0.16666667f),
            ("m_flSurfaceArea", 2.3660254f),
            ("m_nFlags", 0),
            ("m_pRegionSVM", Object(
                ("m_Planes", KVObject.Blob(planes)),
                ("m_Nodes", KVObject.Blob(RegionNodes())))),
            ("m_Vertices", KVObject.Blob(vertexEdges)),
            ("m_VertexPositions", KVObject.Blob(Positions())),
            ("m_Edges", KVObject.Blob(edges)),
            ("m_Faces", KVObject.Blob(faces)),
            ("m_Planes", KVObject.Blob(planes)));
    }

    private static KVObject CollisionAttribute() => Object(
        ("m_CollisionGroup", 1977497166),
        ("m_InteractAs", KvArray()),
        ("m_InteractWith", KvArray()),
        ("m_InteractExclude", KvArray()),
        ("m_CollisionGroupString", "default"),
        ("m_InteractAsStrings", KvArray()),
        ("m_InteractWithStrings", KvArray()),
        ("m_InteractExcludeStrings", KvArray()));

    private static KVObject MassProperties()
    {
        const double diagonal = 1d / 80d;
        const double offDiagonal = 1d / 480d;
        return KvArray(
            new KVObject(diagonal),
            new KVObject(offDiagonal),
            new KVObject(offDiagonal),
            new KVObject(0.25d),
            new KVObject(offDiagonal),
            new KVObject(diagonal),
            new KVObject(offDiagonal),
            new KVObject(0.25d),
            new KVObject(offDiagonal),
            new KVObject(offDiagonal),
            new KVObject(diagonal),
            new KVObject(0.25d));
    }

    private static byte[] Positions()
    {
        var result = new byte[TetraVertices.Length * 12];
        for (var index = 0; index < TetraVertices.Length; index++)
        {
            WriteSingle(result, index * 12, TetraVertices[index].X);
            WriteSingle(result, (index * 12) + 4, TetraVertices[index].Y);
            WriteSingle(result, (index * 12) + 8, TetraVertices[index].Z);
        }

        return result;
    }

    private static byte[] Planes()
    {
        var result = new byte[TetraFaces.Length * 16];
        for (var face = 0; face < TetraFaces.Length; face++)
        {
            var cycle = TetraFaces[face];
            double nx = 0, ny = 0, nz = 0;
            for (var index = 0; index < cycle.Length; index++)
            {
                var a = TetraVertices[cycle[index]];
                var b = TetraVertices[cycle[(index + 1) % cycle.Length]];
                nx += (double)a.Y * b.Z - (double)a.Z * b.Y;
                ny += (double)a.Z * b.X - (double)a.X * b.Z;
                nz += (double)a.X * b.Y - (double)a.Y * b.X;
            }

            var length = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
            nx /= length;
            ny /= length;
            nz /= length;
            var first = TetraVertices[cycle[0]];
            var offset = (nx * first.X) + (ny * first.Y) + (nz * first.Z);
            WriteSingle(result, face * 16, (float)nx);
            WriteSingle(result, (face * 16) + 4, (float)ny);
            WriteSingle(result, (face * 16) + 8, (float)nz);
            WriteSingle(result, (face * 16) + 12, (float)offset);
        }

        return result;
    }

    private static byte[] RegionNodes()
    {
        var result = new byte[3 * 4];
        WriteUInt16(result, 0, 0);
        WriteUInt16(result, 2, 0x8000);
        WriteUInt16(result, 4, 1);
        WriteUInt16(result, 6, 0x0000);
        WriteUInt16(result, 8, 2);
        WriteUInt16(result, 10, 0x2000);
        return result;
    }

    private static (byte[] Edges, byte[] Faces, byte[] VertexEdges) BuildTopology(int[][] faces)
    {
        var origins = new List<int>();
        var destinations = new List<int>();
        var faceOf = new List<int>();
        var faceStart = new List<int>();
        for (var face = 0; face < faces.Length; face++)
        {
            faceStart.Add(origins.Count);
            var cycle = faces[face];
            for (var index = 0; index < cycle.Length; index++)
            {
                origins.Add(cycle[index]);
                destinations.Add(cycle[(index + 1) % cycle.Length]);
                faceOf.Add(face);
            }
        }

        var count = origins.Count;
        var edges = new byte[count * 4];
        for (var edge = 0; edge < count; edge++)
        {
            var face = faceOf[edge];
            var start = faceStart[face];
            var length = faces[face].Length;
            var position = edge - start;
            var next = start + ((position + 1) % length);
            var twin = -1;
            for (var other = 0; other < count; other++)
            {
                if (other != edge && origins[other] == destinations[edge] && destinations[other] == origins[edge])
                {
                    twin = other;
                    break;
                }
            }

            if (twin < 0)
            {
                throw new InvalidOperationException("The synthetic topology is not manifold.");
            }

            edges[(edge * 4) + 0] = (byte)next;
            edges[(edge * 4) + 1] = (byte)twin;
            edges[(edge * 4) + 2] = (byte)origins[edge];
            edges[(edge * 4) + 3] = (byte)face;
        }

        var vertexEdges = Enumerable.Range(0, origins.Max() + 1)
            .Select(vertex => (byte)origins.IndexOf(vertex))
            .ToArray();
        return (edges, faceStart.Select(start => (byte)start).ToArray(), vertexEdges);
    }

    private static KVObject Part(KVObject root) => (KVObject)((KVObject)root["m_parts"]!)[0]!;

    private static KVObject Shape(KVObject root) => (KVObject)Part(root)["m_rnShape"]!;

    private static KVObject HullDescriptor(KVObject root) => (KVObject)((KVObject)Shape(root)["m_hulls"]!)[0]!;

    private static KVObject Hull(KVObject root) => (KVObject)HullDescriptor(root)["m_Hull"]!;

    private static KVObject BoundsCollection(KVObject root) => (KVObject)Hull(root)["m_Bounds"]!;

    private static KVObject Region(KVObject root) => (KVObject)Hull(root)["m_pRegionSVM"]!;

    private static KVObject Vector(float x, float y, float z) => KvArray(new KVObject(x), new KVObject(y), new KVObject(z));

    private static KVObject Object(params (string Key, KVObject Value)[] values)
    {
        var result = KVObject.Collection();
        foreach (var (key, value) in values)
        {
            result.Add(key, value);
        }

        return result;
    }

    private static KVObject KvArray(params KVObject[] values)
    {
        var result = KVObject.Array();
        foreach (var value in values)
        {
            result.Add(value);
        }

        return result;
    }

    private static void WriteSingle(byte[] bytes, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, sizeof(float)), BitConverter.SingleToInt32Bits(value));

    private static void WriteUInt16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, sizeof(ushort)), value);
}
