using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;

namespace S2ModKit.Adapters.Source2;

/// <summary>A collision plane with a unit outward normal, stored as <c>n dot x - d = 0</c>.</summary>
internal sealed record Source2PhysPlane(Point3 Normal, float Offset);

/// <summary>
/// Read-only facts for the characterized one-part/one-convex-hull embedded PHYS payload
/// accepted by ADR-0020. All byte arrays are defensive copies; the hashes identify the
/// byte-preserved topology and plane blobs for later coupled planning.
/// </summary>
internal sealed record Source2ConvexPhysAnalysis(
    int ResourceBlockIndex,
    ContentHash PayloadHash,
    Point3[] VertexPositions,
    Bounds3 PositionBounds,
    Point3 Centroid,
    float MaxAngularRadius,
    float Volume,
    float SurfaceArea,
    float OrthographicAreaX,
    float OrthographicAreaY,
    float OrthographicAreaZ,
    float[] MassProperties,
    Source2PhysPlane[] HullPlanes,
    Source2PhysPlane[] RegionPlanes,
    ImmutableArray<ConvexFace> Faces,
    ConvexHullDerivedValues DerivedValues,
    int HalfEdgeCount,
    int FaceCount,
    int HullVertexEdgeCount,
    int RegionNodeCount,
    ContentHash HullVertexEdgesHash,
    ContentHash HalfEdgesHash,
    ContentHash FacesHash,
    ContentHash RegionNodesHash,
    ContentHash HullPlanesHash,
    ContentHash RegionPlanesHash,
    string CollisionGroupString);

internal static class Source2ConvexPhysReader
{
    private const int MinimumHullVertexCount = 4;
    private const int MaximumHullVertexCount = 255;
    private const int MaximumFaceCount = 1020;
    private const int MaximumRegionPlaneCount = 4096;
    private const int MaximumRegionNodeBytes = 64 * 1024;
    private const int PositionRecordSize = 12;
    private const int HalfEdgeRecordSize = 4;
    private const int PlaneRecordSize = 16;
    private const int RegionNodeRecordSize = 4;
    private const int MassPropertyCount = 12;
    private const float MaximumUnitNormalDeviation = 1e-4f;
    private const float MaximumContainmentRelativeTolerance = 1e-3f;

    private static readonly string[] RootKeys =
    [
        "m_nFlags",
        "m_nRefCounter",
        "m_bonesHash",
        "m_boneNames",
        "m_indexNames",
        "m_indexHash",
        "m_bindPose",
        "m_parts",
        "m_constraints2",
        "m_joints",
        "m_pFeModel",
        "m_boneParents",
        "m_surfacePropertyHashes",
        "m_collisionAttributes",
        "m_debugPartNames",
        "m_embeddedKeyvalues",
    ];

    private static readonly string[] PartKeys =
    [
        "m_nFlags",
        "m_flMass",
        "m_rnShape",
        "m_nCollisionAttributeIndex",
        "m_nReserved",
        "m_flInertiaScale",
        "m_flLinearDamping",
        "m_flAngularDamping",
        "m_bOverrideMassCenter",
        "m_vMassCenterOverride",
    ];

    private static readonly string[] ShapeKeys =
    [
        "m_spheres",
        "m_capsules",
        "m_hulls",
        "m_meshes",
        "m_CollisionAttributeIndices",
    ];

    private static readonly string[] HullDescriptorKeys =
    [
        "m_nCollisionAttributeIndex",
        "m_nSurfacePropertyIndex",
        "m_UserFriendlyName",
        "m_bUserFriendlyNameSealed",
        "m_bUserFriendlyNameLong",
        "m_nToolMaterialHash",
        "m_Hull",
    ];

    private static readonly string[] HullKeys =
    [
        "m_vCentroid",
        "m_flMaxAngularRadius",
        "m_Bounds",
        "m_vOrthographicAreas",
        "m_MassProperties",
        "m_flVolume",
        "m_flSurfaceArea",
        "m_nFlags",
        "m_pRegionSVM",
        "m_Vertices",
        "m_VertexPositions",
        "m_Edges",
        "m_Faces",
        "m_Planes",
    ];

    private static readonly string[] BoundsKeys = ["m_vMinBounds", "m_vMaxBounds"];

    private static readonly string[] RegionKeys = ["m_Planes", "m_Nodes"];

    private static readonly string[] CollisionAttributeKeys =
    [
        "m_CollisionGroup",
        "m_InteractAs",
        "m_InteractWith",
        "m_InteractExclude",
        "m_CollisionGroupString",
        "m_InteractAsStrings",
        "m_InteractWithStrings",
        "m_InteractExcludeStrings",
    ];

    public static Source2ConvexPhysAnalysis AnalyzeDetailed(
        KVObject controlRoot,
        Source2ResourceEnvelope envelope,
        Func<int, KVObject?> openSemanticRoot,
        GeometryBounds visualBounds,
        string context)
    {
        ArgumentNullException.ThrowIfNull(controlRoot);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(openSemanticRoot);
        ArgumentNullException.ThrowIfNull(visualBounds);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        try
        {
            return AnalyzeCore(controlRoot, envelope, openSemanticRoot, visualBounds, context);
        }
        catch (S2ModKitException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidCastException
            or FormatException
            or OverflowException
            or IndexOutOfRangeException)
        {
            throw new S2ModKitException(
                new S2Error(
                    "PHYS_GRAPH_UNSUPPORTED",
                    "source2_adapter",
                    $"{context} contains a malformed or out-of-range convex-PHYS graph.",
                    "Use an intact one-part/one-convex-hull PHYS payload matching the accepted bounded profile.",
                    ErrorCategory.UnsupportedCapability),
                exception);
        }
    }

    internal static void RequireUniqueCoupledControl(
        IReadOnlyList<KVObject> controlRoots,
        KVObject expectedMeshControl,
        string context)
    {
        ArgumentNullException.ThrowIfNull(controlRoots);
        ArgumentNullException.ThrowIfNull(expectedMeshControl);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        var physicsControls = controlRoots
            .Where(root => root.TryGetValue("embedded_physics", out _))
            .ToArray();
        if (physicsControls.Length != 1 || !ReferenceEquals(physicsControls[0], expectedMeshControl))
        {
            throw Unsupported(
                "PHYS_COUPLING_AMBIGUOUS",
                $"{context} must resolve embedded_meshes and embedded_physics through the same unique CTRL block; found {physicsControls.Length} CTRL block(s) with embedded_physics.");
        }
    }

    private static Source2ConvexPhysAnalysis AnalyzeCore(
        KVObject controlRoot,
        Source2ResourceEnvelope envelope,
        Func<int, KVObject?> openSemanticRoot,
        GeometryBounds visualBounds,
        string context)
    {
        var physicsControl = RequireCollection(controlRoot, "embedded_physics", context, "PHYS_REFERENCE_UNSUPPORTED");
        RequireExactKeys(physicsControl, ["phys_data_block"], $"{context}.embedded_physics", "PHYS_REFERENCE_UNSUPPORTED");
        var declaredIndex = RequireInt32(physicsControl, "phys_data_block", $"{context}.embedded_physics", "PHYS_REFERENCE_UNSUPPORTED");
        var physBlocks = envelope.Blocks
            .Where(block => string.Equals(block.Type, "PHYS", StringComparison.Ordinal))
            .ToArray();
        if (physBlocks.Length != 1 || physBlocks[0].Index != declaredIndex)
        {
            throw Unsupported(
                "PHYS_REFERENCE_UNSUPPORTED",
                $"{context} embedded_physics resolves {physBlocks.Length} PHYS block(s) with declared index {declaredIndex}; exactly one matching block is required.");
        }

        var physBlock = physBlocks[0];
        var root = openSemanticRoot(declaredIndex);
        if (root is null)
        {
            throw Unsupported(
                "PHYS_REFERENCE_UNSUPPORTED",
                $"{context} embedded physics block {declaredIndex} does not expose the characterized PhysAggregateData semantic payload.");
        }

        RequireExactKeys(root, RootKeys, $"{context} PHYS root", "PHYS_GRAPH_UNSUPPORTED");
        RequireInt32Value(root, "m_nFlags", 0, $"{context} PHYS root", "PHYS_GRAPH_UNSUPPORTED");
        RequireInt32Value(root, "m_nRefCounter", 0, $"{context} PHYS root", "PHYS_GRAPH_UNSUPPORTED");
        RequireEmptyArray(root, ["m_constraints2", "m_joints", "m_debugPartNames"], context, "PHYS_GRAPH_UNSUPPORTED");
        RequireEmptyArray(
            root,
            ["m_bonesHash", "m_boneNames", "m_indexNames", "m_indexHash", "m_bindPose", "m_boneParents"],
            context,
            "PHYS_COORDINATE_MAPPING_UNSUPPORTED");
        RequireNull(root, "m_pFeModel", context, "PHYS_GRAPH_UNSUPPORTED");
        RequireEmptyString(root, "m_embeddedKeyvalues", context, "PHYS_GRAPH_UNSUPPORTED");

        var parts = RequireArray(root, "m_parts", context, "PHYS_GRAPH_UNSUPPORTED");
        if (parts.Count != 1)
        {
            throw Unsupported(
                "PHYS_GRAPH_UNSUPPORTED",
                $"{context} declares {parts.Count} physics parts; exactly one is accepted.");
        }

        var surfacePropertyHashes = RequireArray(root, "m_surfacePropertyHashes", context, "PHYS_GRAPH_UNSUPPORTED");
        if (surfacePropertyHashes.Count != 1)
        {
            throw Unsupported(
                "PHYS_GRAPH_UNSUPPORTED",
                $"{context} declares {surfacePropertyHashes.Count} surface-property hashes; exactly one is accepted.");
        }

        RequireUnsignedInteger(
            surfacePropertyHashes[0],
            $"{context} m_surfacePropertyHashes[0]",
            "PHYS_GRAPH_UNSUPPORTED");
        var collisionAttributes = RequireArray(root, "m_collisionAttributes", context, "PHYS_GRAPH_UNSUPPORTED");
        if (collisionAttributes.Count != 1)
        {
            throw Unsupported(
                "PHYS_GRAPH_UNSUPPORTED",
                $"{context} declares {collisionAttributes.Count} collision attributes; exactly one is accepted.");
        }

        var collisionGroupString = ReadCollisionAttribute(collisionAttributes[0], context);

        var part = RequireCollection(parts[0], $"{context} m_parts[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireExactKeys(part, PartKeys, $"{context} m_parts[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireInt32Value(part, "m_nFlags", 0, $"{context} m_parts[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireSingleValue(part, "m_flMass", 0f, $"{context} m_parts[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireInt32Value(part, "m_nCollisionAttributeIndex", 0, $"{context} m_parts[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireInt32Value(part, "m_nReserved", 0, $"{context} m_parts[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireSingleValue(part, "m_flInertiaScale", 1f, $"{context} m_parts[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireSingleValue(part, "m_flLinearDamping", 0f, $"{context} m_parts[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireSingleValue(part, "m_flAngularDamping", 0f, $"{context} m_parts[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireBooleanValue(part, "m_bOverrideMassCenter", false, $"{context} m_parts[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireZeroVector(part, "m_vMassCenterOverride", $"{context} m_parts[0]", "PHYS_GRAPH_UNSUPPORTED");

        var shape = RequireCollection(part, "m_rnShape", $"{context} m_parts[0]", "PHYS_GRAPH_UNSUPPORTED");

        RequireExactKeys(shape, ShapeKeys, $"{context} m_rnShape", "PHYS_GRAPH_UNSUPPORTED");
        RequireEmptyArray(shape, ["m_spheres", "m_capsules", "m_meshes", "m_CollisionAttributeIndices"], context, "PHYS_GRAPH_UNSUPPORTED");
        var hulls = RequireArray(shape, "m_hulls", context, "PHYS_GRAPH_UNSUPPORTED");
        if (hulls.Count != 1)
        {
            throw Unsupported(
                "PHYS_GRAPH_UNSUPPORTED",
                $"{context} declares {hulls.Count} collision hulls; exactly one convex hull is accepted.");
        }

        var descriptor = RequireCollection(hulls[0], $"{context} m_hulls[0]", "PHYS_GRAPH_UNSUPPORTED");

        RequireExactKeys(descriptor, HullDescriptorKeys, $"{context} m_hulls[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireInt32Value(descriptor, "m_nCollisionAttributeIndex", 0, $"{context} m_hulls[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireInt32Value(descriptor, "m_nSurfacePropertyIndex", 0, $"{context} m_hulls[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireBoundedString(descriptor, "m_UserFriendlyName", $"{context} m_hulls[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireBooleanValue(descriptor, "m_bUserFriendlyNameSealed", false, $"{context} m_hulls[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireBooleanValue(descriptor, "m_bUserFriendlyNameLong", false, $"{context} m_hulls[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireInt32Value(descriptor, "m_nToolMaterialHash", 0, $"{context} m_hulls[0]", "PHYS_GRAPH_UNSUPPORTED");

        var hull = RequireCollection(descriptor, "m_Hull", $"{context} m_hulls[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireExactKeys(hull, HullKeys, $"{context} m_hulls[0].m_Hull", "PHYS_GRAPH_UNSUPPORTED");
        RequireInt32Value(hull, "m_nFlags", 0, $"{context} m_Hull", "PHYS_GRAPH_UNSUPPORTED");
        var centroid = RequireVector3(hull, "m_vCentroid", context, "PHYS_DERIVED_FIELD_UNSUPPORTED");
        var maxAngularRadius = RequirePositiveSingle(hull, "m_flMaxAngularRadius", context);
        var volume = RequirePositiveSingle(hull, "m_flVolume", context);
        var surfaceArea = RequirePositiveSingle(hull, "m_flSurfaceArea", context);
        var orthographicAreas = RequireVector3(hull, "m_vOrthographicAreas", context, "PHYS_DERIVED_FIELD_UNSUPPORTED");
        if (orthographicAreas.X is <= 0f or > 1f
            || orthographicAreas.Y is <= 0f or > 1f
            || orthographicAreas.Z is <= 0f or > 1f)
        {
            throw Unsupported(
                "PHYS_DERIVED_FIELD_UNSUPPORTED",
                $"{context} m_vOrthographicAreas are outside the characterized (0, 1] coverage fractions.");
        }

        var massProperties = RequireFiniteSingleArray(hull, "m_MassProperties", MassPropertyCount, context);
        var boundsCollection = RequireCollection(hull, "m_Bounds", context, "PHYS_DERIVED_FIELD_UNSUPPORTED");
        RequireExactKeys(boundsCollection, BoundsKeys, $"{context} m_Bounds", "PHYS_DERIVED_FIELD_UNSUPPORTED");
        var declaredMin = RequireVector3(boundsCollection, "m_vMinBounds", context, "PHYS_DERIVED_FIELD_UNSUPPORTED");
        var declaredMax = RequireVector3(boundsCollection, "m_vMaxBounds", context, "PHYS_DERIVED_FIELD_UNSUPPORTED");
        if (declaredMin.X > declaredMax.X || declaredMin.Y > declaredMax.Y || declaredMin.Z > declaredMax.Z)
        {
            throw Unsupported(
                "PHYS_DERIVED_FIELD_UNSUPPORTED",
                $"{context} declared hull bounds minimum exceeds its maximum.");
        }

        var positionBytes = RequireBlob(hull, "m_VertexPositions", context, "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED");
        var vertexEdgeBytes = RequireBlob(hull, "m_Vertices", context, "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED");
        var edgeBytes = RequireBlob(hull, "m_Edges", context, "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED");
        var faceBytes = RequireBlob(hull, "m_Faces", context, "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED");
        var hullPlaneBytes = RequireBlob(hull, "m_Planes", context, "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED");
        if (positionBytes.Length < MinimumHullVertexCount * PositionRecordSize
            || positionBytes.Length > MaximumHullVertexCount * PositionRecordSize
            || positionBytes.Length % PositionRecordSize != 0)
        {
            throw Unsupported(
                "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                $"{context} m_VertexPositions is {positionBytes.Length} bytes; the accepted profile needs a [4, {MaximumHullVertexCount}] count of 12-byte positions.");
        }

        var vertexCount = positionBytes.Length / PositionRecordSize;
        if (edgeBytes.Length % HalfEdgeRecordSize != 0)
        {
            throw Unsupported(
                "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                $"{context} m_Edges is {edgeBytes.Length} bytes, which is not a whole number of 4-byte half-edges.");
        }

        if (faceBytes.Length is < 4 or > MaximumFaceCount)
        {
            throw Unsupported(
                "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                $"{context} declares {faceBytes.Length} faces outside the accepted [4, {MaximumFaceCount}] range.");
        }

        var faceCount = faceBytes.Length;
        if (hullPlaneBytes.Length != faceCount * PlaneRecordSize)
        {
            throw Unsupported(
                "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                $"{context} has {faceCount} faces but {hullPlaneBytes.Length} bytes of hull planes; exactly one plane per face is required.");
        }

        var halfEdgeCount = edgeBytes.Length / HalfEdgeRecordSize;
        if (halfEdgeCount != checked(2 * (vertexCount + faceCount - 2)))
        {
            throw Unsupported(
                "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                $"{context} has {halfEdgeCount} half-edges; Euler's identity for {vertexCount} vertices and {faceCount} faces requires {2 * (vertexCount + faceCount - 2)}.");
        }

        if (vertexEdgeBytes.Length != vertexCount)
        {
            throw Unsupported(
                "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                $"{context} m_Vertices holds {vertexEdgeBytes.Length} vertex-edge records for {vertexCount} positions; equal counts are required.");
        }

        var positions = new Point3[vertexCount];
        for (var index = 0; index < vertexCount; index++)
        {
            var record = positionBytes.AsSpan(index * PositionRecordSize, PositionRecordSize);
            positions[index] = new Point3(
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(record)),
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(record.Slice(4))),
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(record.Slice(8))));
        }

        var positionBounds = Bounds3.FromPoints(positions);
        if (positionBounds.Min.X != declaredMin.X || positionBounds.Min.Y != declaredMin.Y || positionBounds.Min.Z != declaredMin.Z
            || positionBounds.Max.X != declaredMax.X || positionBounds.Max.Y != declaredMax.Y || positionBounds.Max.Z != declaredMax.Z)
        {
            throw Unsupported(
                "PHYS_DERIVED_FIELD_UNSUPPORTED",
                $"{context} declared hull bounds do not exactly equal the decoded vertex-position bounds.");
        }

        var edges = new (int Next, int Twin, int Origin, int Face)[halfEdgeCount];
        for (var index = 0; index < halfEdgeCount; index++)
        {
            var record = edgeBytes.AsSpan(index * HalfEdgeRecordSize, HalfEdgeRecordSize);
            var next = record[0];
            var twin = record[1];
            var origin = record[2];
            var face = record[3];
            if (next >= halfEdgeCount || twin >= halfEdgeCount || origin >= vertexCount || face >= faceCount)
            {
                throw Unsupported(
                    "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                    $"{context} half-edge {index} references next={next}, twin={twin}, origin={origin}, face={face} outside the hull topology.");
            }

            edges[index] = (next, twin, origin, face);
        }

        var seenVertexEdges = new HashSet<int>();
        for (var vertexIndex = 0; vertexIndex < vertexEdgeBytes.Length; vertexIndex++)
        {
            var edgeIndex = vertexEdgeBytes[vertexIndex];
            if (edgeIndex >= halfEdgeCount
                || !seenVertexEdges.Add(edgeIndex)
                || edges[edgeIndex].Origin != vertexIndex)
            {
                throw Unsupported(
                    "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                    $"{context} m_Vertices[{vertexIndex}]={edgeIndex} is not a unique outgoing half-edge for that vertex.");
            }
        }

        for (var index = 0; index < halfEdgeCount; index++)
        {
            var edge = edges[index];
            if (edge.Twin == index
                || edges[edge.Twin].Twin != index
                || edge.Next == index
                || edges[edge.Next].Face != edge.Face)
            {
                throw Unsupported(
                    "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                    $"{context} half-edge {index} violates the non-self twin involution or single-face next-chain invariants.");
            }
        }

        var visitedEdges = new bool[halfEdgeCount];
        var convexFaces = ImmutableArray.CreateBuilder<ConvexFace>(faceCount);
        for (var faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            var start = faceBytes[faceIndex];
            if (start >= halfEdgeCount || edges[start].Face != faceIndex)
            {
                throw Unsupported(
                    "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                    $"{context} m_Faces[{faceIndex}]={start} does not reference a half-edge owned by that face.");
            }

            var current = (int)start;
            var steps = 0;
            var faceVertices = new List<int>();
            while (true)
            {
                if (visitedEdges[current])
                {
                    throw Unsupported(
                        "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                        $"{context} half-edge {current} is claimed by more than one face cycle.");
                }

                visitedEdges[current] = true;
                faceVertices.Add(edges[current].Origin);
                current = edges[current].Next;
                steps++;
                if (current == start)
                {
                    break;
                }

                if (steps > halfEdgeCount)
                {
                    throw Unsupported(
                        "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                        $"{context} face {faceIndex} next-chain does not close.");
                }
            }

            if (steps < 3)
            {
                throw Unsupported(
                    "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                    $"{context} face {faceIndex} closes after {steps} half-edges; a face needs at least three.");
            }

            convexFaces.Add(new ConvexFace(faceVertices));
        }

        if (visitedEdges.Any(visited => !visited))
        {
            throw Unsupported(
                "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                $"{context} face cycles do not cover every half-edge exactly once.");
        }

        for (var index = 0; index < halfEdgeCount; index++)
        {
            var edge = edges[index];
            var destination = edges[edge.Next].Origin;
            var twin = edges[edge.Twin];
            var twinDestination = edges[twin.Next].Origin;
            if (twin.Origin != destination || twinDestination != edge.Origin)
            {
                throw Unsupported(
                    "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                    $"{context} half-edge {index} and twin {edge.Twin} do not describe the same edge in opposite directions.");
            }
        }

        var hullPlanes = ReadPlanes(hullPlaneBytes, $"{context} hull planes", faceCount);
        var extent = MathF.Max(
            MathF.Max(declaredMax.X - declaredMin.X, declaredMax.Y - declaredMin.Y),
            declaredMax.Z - declaredMin.Z);
        var containmentTolerance = MaximumContainmentRelativeTolerance * MathF.Max(1f, extent);
        for (var planeIndex = 0; planeIndex < hullPlanes.Length; planeIndex++)
        {
            var plane = hullPlanes[planeIndex];
            var worstSignedDistance = float.NegativeInfinity;
            var closestToPlane = float.PositiveInfinity;
            for (var vertexIndex = 0; vertexIndex < positions.Length; vertexIndex++)
            {
                var point = positions[vertexIndex];
                var signedDistance = (plane.Normal.X * point.X) + (plane.Normal.Y * point.Y) + (plane.Normal.Z * point.Z) - plane.Offset;
                worstSignedDistance = MathF.Max(worstSignedDistance, signedDistance);
                closestToPlane = MathF.Min(closestToPlane, MathF.Abs(signedDistance));
            }

            if (worstSignedDistance > containmentTolerance || closestToPlane > containmentTolerance)
            {
                throw Unsupported(
                    "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                $"{context} hull plane {planeIndex} does not bound every vertex or supports none of them.");
            }
        }

        ConvexHullDerivedValues derivedValues;
        try
        {
            derivedValues = ConvexHullGeometry.Derive(positions, convexFaces);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
        {
            throw new S2ModKitException(
                new S2Error(
                    "PHYS_DERIVED_FIELD_UNSUPPORTED",
                    "source2_adapter",
                    $"{context} convex derived values cannot be reproduced from positions and face topology.",
                    "Use an intact one-part/one-convex-hull PHYS payload matching the accepted bounded profile.",
                    ErrorCategory.UnsupportedCapability),
                exception);
        }

        RequireDerivedValues(
            derivedValues,
            centroid,
            maxAngularRadius,
            volume,
            surfaceArea,
            massProperties,
            hullPlanes,
            context);

        var region = RequireCollection(hull, "m_pRegionSVM", context, "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED");
        RequireExactKeys(region, RegionKeys, $"{context} m_pRegionSVM", "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED");
        var regionPlaneBytes = RequireBlob(region, "m_Planes", context, "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED");
        if (regionPlaneBytes.Length == 0
            || regionPlaneBytes.Length % PlaneRecordSize != 0
            || regionPlaneBytes.Length / PlaneRecordSize > MaximumRegionPlaneCount)
        {
            throw Unsupported(
                "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                $"{context} region planes are {regionPlaneBytes.Length} bytes; a non-empty multiple of {PlaneRecordSize} up to {MaximumRegionPlaneCount} planes is required.");
        }

        var regionPlanes = ReadPlanes(regionPlaneBytes, $"{context} region planes", regionPlaneBytes.Length / PlaneRecordSize);
        var regionPlaneKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var planeIndex = 0; planeIndex < regionPlanes.Length; planeIndex++)
        {
            regionPlaneKeys.Add(Convert.ToHexStringLower(regionPlaneBytes.AsSpan(planeIndex * PlaneRecordSize, PlaneRecordSize)));
        }

        for (var planeIndex = 0; planeIndex < hullPlanes.Length; planeIndex++)
        {
            if (!regionPlaneKeys.Contains(Convert.ToHexStringLower(hullPlaneBytes.AsSpan(planeIndex * PlaneRecordSize, PlaneRecordSize))))
            {
                throw Unsupported(
                    "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                    $"{context} hull plane {planeIndex} is not present in the spatial region plane set.");
            }
        }

        var regionNodeBytes = RequireBlob(region, "m_Nodes", context, "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED");
        if (regionNodeBytes.Length % RegionNodeRecordSize != 0 || regionNodeBytes.Length > MaximumRegionNodeBytes)
        {
            throw Unsupported(
                "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                $"{context} region nodes are {regionNodeBytes.Length} bytes; a multiple of {RegionNodeRecordSize} up to {MaximumRegionNodeBytes} is required.");
        }

        ValidateRegionNodes(regionNodeBytes, regionPlanes.Length, context);

        var visualMin = new Point3(visualBounds.Min.X, visualBounds.Min.Y, visualBounds.Min.Z);
        var visualMax = new Point3(visualBounds.Max.X, visualBounds.Max.Y, visualBounds.Max.Z);
        if (declaredMin.X > visualMax.X || visualMin.X > declaredMax.X
            || declaredMin.Y > visualMax.Y || visualMin.Y > declaredMax.Y
            || declaredMin.Z > visualMax.Z || visualMin.Z > declaredMax.Z)
        {
            throw Unsupported(
                "PHYS_COUPLING_AMBIGUOUS",
                $"{context} hull bounds do not intersect the visual component bounds in compiled-model space.");
        }

        return new Source2ConvexPhysAnalysis(
            physBlock.Index,
            ContentHash.Compute(physBlock.Payload.Span),
            positions,
            positionBounds,
            centroid,
            maxAngularRadius,
            volume,
            surfaceArea,
            orthographicAreas.X,
            orthographicAreas.Y,
            orthographicAreas.Z,
            massProperties,
            hullPlanes,
            regionPlanes,
            convexFaces.MoveToImmutable(),
            derivedValues,
            halfEdgeCount,
            faceCount,
            vertexEdgeBytes.Length,
            regionNodeBytes.Length / RegionNodeRecordSize,
            ContentHash.Compute(vertexEdgeBytes),
            ContentHash.Compute(edgeBytes),
            ContentHash.Compute(faceBytes),
            ContentHash.Compute(regionNodeBytes),
            ContentHash.Compute(hullPlaneBytes),
            ContentHash.Compute(regionPlaneBytes),
            collisionGroupString);
    }

    private static void RequireDerivedValues(
        ConvexHullDerivedValues derived,
        Point3 centroid,
        float maximumAngularRadius,
        float volume,
        float surfaceArea,
        float[] massProperties,
        Source2PhysPlane[] hullPlanes,
        string context)
    {
        RequireClose(derived.VertexCentroid, centroid, "m_vCentroid", context);
        RequireClose(derived.MaximumAngularRadius, maximumAngularRadius, "m_flMaxAngularRadius", context);
        RequireClose(derived.Volume, volume, "m_flVolume", context);
        RequireClose(derived.SurfaceArea, surfaceArea, "m_flSurfaceArea", context);
        RequireClose(massProperties[1], massProperties[4], "m_MassProperties XY symmetry", context);
        RequireClose(massProperties[2], massProperties[8], "m_MassProperties XZ symmetry", context);
        RequireClose(massProperties[6], massProperties[9], "m_MassProperties YZ symmetry", context);
        var storedMass = new ConvexMassProperties(
            volume,
            new Point3(massProperties[3], massProperties[7], massProperties[11]),
            new SymmetricInertia3(
                massProperties[0],
                massProperties[1],
                massProperties[2],
                massProperties[5],
                massProperties[6],
                massProperties[10]));
        RequireClose(derived.MassProperties.Mass, storedMass.Mass, "unit-density mass", context);
        RequireClose(derived.MassProperties.CenterOfMass, storedMass.CenterOfMass, "mass-property center", context);
        RequireClose(derived.MassProperties.Inertia.XX, storedMass.Inertia.XX, "inertia XX", context);
        RequireClose(derived.MassProperties.Inertia.XY, storedMass.Inertia.XY, "inertia XY", context);
        RequireClose(derived.MassProperties.Inertia.XZ, storedMass.Inertia.XZ, "inertia XZ", context);
        RequireClose(derived.MassProperties.Inertia.YY, storedMass.Inertia.YY, "inertia YY", context);
        RequireClose(derived.MassProperties.Inertia.YZ, storedMass.Inertia.YZ, "inertia YZ", context);
        RequireClose(derived.MassProperties.Inertia.ZZ, storedMass.Inertia.ZZ, "inertia ZZ", context);
        for (var index = 0; index < hullPlanes.Length; index++)
        {
            RequireClose(derived.FacePlanes[index].Normal, hullPlanes[index].Normal, $"m_Planes[{index}] normal", context);
            RequireClose(derived.FacePlanes[index].Offset, hullPlanes[index].Offset, $"m_Planes[{index}] offset", context);
        }
    }

    private static void RequireClose(Point3 calculated, Point3 stored, string field, string context)
    {
        RequireClose(calculated.X, stored.X, $"{field}.x", context);
        RequireClose(calculated.Y, stored.Y, $"{field}.y", context);
        RequireClose(calculated.Z, stored.Z, $"{field}.z", context);
    }

    private static void RequireClose(float calculated, float stored, string field, string context)
    {
        const float relativeTolerance = 1e-3f;
        var tolerance = relativeTolerance * MathF.Max(1f, MathF.Abs(stored));
        if (!float.IsFinite(calculated) || MathF.Abs(calculated - stored) > tolerance)
        {
            throw Unsupported(
                "PHYS_DERIVED_FIELD_UNSUPPORTED",
                $"{context} {field} cannot be reproduced from convex positions and topology (calculated {calculated.ToString("R", CultureInfo.InvariantCulture)}, stored {stored.ToString("R", CultureInfo.InvariantCulture)}).");
        }
    }

    private static string ReadCollisionAttribute(KVObject value, string context)
    {
        var attribute = RequireCollection(value, $"{context} m_collisionAttributes[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireExactKeys(attribute, CollisionAttributeKeys, $"{context} m_collisionAttributes[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireInt32(attribute, "m_CollisionGroup", $"{context} m_collisionAttributes[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireBoundedString(attribute, "m_CollisionGroupString", $"{context} m_collisionAttributes[0]", "PHYS_GRAPH_UNSUPPORTED");
        RequireEmptyArray(
            attribute,
            ["m_InteractAs", "m_InteractWith", "m_InteractExclude", "m_InteractAsStrings", "m_InteractWithStrings", "m_InteractExcludeStrings"],
            context,
            "PHYS_GRAPH_UNSUPPORTED");
        return attribute["m_CollisionGroupString"].ToString(CultureInfo.InvariantCulture);
    }

    private static Source2PhysPlane[] ReadPlanes(byte[] planeBytes, string context, int planeCount)
    {
        var planes = new Source2PhysPlane[planeCount];
        for (var index = 0; index < planeCount; index++)
        {
            var record = planeBytes.AsSpan(index * PlaneRecordSize, PlaneRecordSize);
            var normalX = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(record));
            var normalY = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(record.Slice(4)));
            var normalZ = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(record.Slice(8)));
            var offset = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(record.Slice(12)));
            if (!float.IsFinite(normalX)
                || !float.IsFinite(normalY)
                || !float.IsFinite(normalZ)
                || !float.IsFinite(offset))
            {
                throw Unsupported(
                    "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                    $"{context} record {index} has non-finite coefficients or a non-unit normal.");
            }

            var normal = new Point3(normalX, normalY, normalZ);
            var length = MathF.Sqrt(
                (normal.X * normal.X) + (normal.Y * normal.Y) + (normal.Z * normal.Z));
            if (!float.IsFinite(length) || MathF.Abs(length - 1f) > MaximumUnitNormalDeviation)
            {
                throw Unsupported(
                    "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                    $"{context} record {index} has non-finite coefficients or a non-unit normal.");
            }

            planes[index] = new Source2PhysPlane(normal, offset);
        }

        return planes;
    }

    private static void ValidateRegionNodes(byte[] nodeBytes, int planeCount, string context)
    {
        if (nodeBytes.Length == 0)
        {
            return;
        }

        var internalCount = 0;
        var leafCount = 0;
        var nodeCount = nodeBytes.Length / RegionNodeRecordSize;
        for (var index = 0; index < nodeCount; index++)
        {
            var record = nodeBytes.AsSpan(index * RegionNodeRecordSize, RegionNodeRecordSize);
            var payload = BinaryPrimitives.ReadUInt16LittleEndian(record);
            var encodedPlane = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(sizeof(ushort)));
            if ((encodedPlane & 0x8000) != 0)
            {
                internalCount++;
                var planeIndex = encodedPlane & 0x7fff;
                if (planeIndex >= planeCount || payload >= nodeCount)
                {
                    throw Unsupported(
                        "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                        $"{context} region node {index} references plane {planeIndex} or branch payload {payload} outside the characterized region graph.");
                }
            }
            else
            {
                leafCount++;
                if ((encodedPlane & 0x1fff) != 0 || (payload & 0x8000) != 0)
                {
                    throw Unsupported(
                        "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                        $"{context} region node {index} has an unsupported leaf encoding.");
                }
            }
        }

        if (leafCount != internalCount + 1)
        {
            throw Unsupported(
                "PHYS_CONVEX_TOPOLOGY_UNSUPPORTED",
                $"{context} region nodes do not form the characterized full binary decision table ({internalCount} internal, {leafCount} leaf).");
        }
    }

    private static void RequireExactKeys(
        KVObject collection,
        string[] expected,
        string context,
        string code)
    {
        if (!collection.IsCollection || collection.IsArray)
        {
            throw Unsupported(code, $"{context} is not a KV collection.");
        }

        var actual = collection.Children.Select(child => child.Key).ToArray();
        if (actual.Length != expected.Length)
        {
            throw Unsupported(
                code,
                $"{context} has {actual.Length} properties; the characterized profile requires exactly {expected.Length}.");
        }

        var expectedSet = new HashSet<string>(expected, StringComparer.Ordinal);
        var missing = expected.Where(key => !actual.Contains(key)).Order(StringComparer.Ordinal).ToArray();
        var unexpected = actual.Where(key => !expectedSet.Contains(key)).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0 || unexpected.Length > 0)
        {
            throw Unsupported(
                code,
                $"{context} key set does not match the characterized profile (missing: [{string.Join(", ", missing)}]; unexpected: [{string.Join(", ", unexpected)}]).");
        }
    }

    private static KVObject RequireCollection(KVObject? value, string context, string code)
    {
        if (value is null || !value.IsCollection || value.IsArray)
        {
            throw Unsupported(code, $"{context} is not a KV collection.");
        }

        return value;
    }

    private static KVObject RequireCollection(KVObject parent, string key, string context, string code)
    {
        if (!parent.TryGetValue(key, out var value)
            || value is null
            || !value.IsCollection
            || value.IsArray)
        {
            throw Unsupported(code, $"{context} is missing collection {key}.");
        }

        return value;
    }

    private static KVObject RequireArray(KVObject parent, string key, string context, string code)
    {
        if (!parent.TryGetValue(key, out var value) || value is null || !value.IsArray)
        {
            throw Unsupported(code, $"{context} is missing array {key}.");
        }

        return value;
    }

    private static void RequireEmptyArray(KVObject parent, IReadOnlyList<string> keys, string context, string code)
    {
        foreach (var key in keys)
        {
            var array = RequireArray(parent, key, context, code);
            if (array.Count != 0)
            {
                throw Unsupported(
                    code,
                    $"{context} contains {array.Count.ToString(CultureInfo.InvariantCulture)} entries in {key}; the characterized profile requires none.");
            }
        }
    }

    private static void RequireNull(KVObject parent, string key, string context, string code)
    {
        if (!parent.TryGetValue(key, out var value)
            || value is null
            || value.IsArray
            || value.IsCollection
            || value.ValueType != KVValueType.Null)
        {
            throw Unsupported(code, $"{context} requires a null {key}.");
        }
    }

    private static void RequireEmptyString(KVObject parent, string key, string context, string code)
    {
        if (!parent.TryGetValue(key, out var value)
            || value is null
            || value.ValueType != KVValueType.String
            || value.ToString(CultureInfo.InvariantCulture).Length != 0)
        {
            throw Unsupported(code, $"{context} requires an empty string {key}.");
        }
    }

    private static void RequireBoundedString(KVObject parent, string key, string context, string code)
    {
        if (!parent.TryGetValue(key, out var value)
            || value is null
            || value.ValueType != KVValueType.String)
        {
            throw Unsupported(code, $"{context} is missing string {key}.");
        }

        var text = value.ToString(CultureInfo.InvariantCulture);
        if (text.Length > 1024 || text.Any(char.IsControl))
        {
            throw Unsupported(code, $"{context} {key} is not bounded non-control text.");
        }
    }

    private static int RequireInt32(KVObject parent, string key, string context, string code)
    {
        if (!parent.TryGetValue(key, out var value) || value is null || value.IsArray || value.IsCollection)
        {
            throw Unsupported(code, $"{context} is missing integer {key}.");
        }

        try
        {
            return checked((int)value.ToInt64(CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            throw new S2ModKitException(
                new S2Error(
                    code,
                    "source2_adapter",
                    $"{context} contains invalid integer {key}.",
                    "Use an intact one-part/one-convex-hull PHYS payload matching the accepted bounded profile.",
                    ErrorCategory.UnsupportedCapability),
                exception);
        }
    }

    private static void RequireInt32Value(KVObject parent, string key, int expected, string context, string code)
    {
        if (RequireInt32(parent, key, context, code) != expected)
        {
            throw Unsupported(code, $"{context} {key} must equal {expected.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private static ulong RequireUnsignedInteger(KVObject value, string context, string code)
    {
        if (value is null || value.IsArray || value.IsCollection)
        {
            throw Unsupported(code, $"{context} is not an integer.");
        }

        try
        {
            return value.ToUInt64(CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            throw new S2ModKitException(
                new S2Error(
                    code,
                    "source2_adapter",
                    $"{context} is not an unsigned integer.",
                    "Use an intact one-part/one-convex-hull PHYS payload matching the accepted bounded profile.",
                    ErrorCategory.UnsupportedCapability),
                exception);
        }
    }

    private static float RequireSingle(KVObject parent, string key, string context, string code)
    {
        if (!parent.TryGetValue(key, out var value) || value is null || value.IsArray || value.IsCollection)
        {
            throw Unsupported(code, $"{context} is missing floating-point scalar {key}.");
        }

        float result;
        try
        {
            result = value.ToSingle(CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            throw new S2ModKitException(
                new S2Error(
                    code,
                    "source2_adapter",
                    $"{context} contains invalid floating-point scalar {key}.",
                    "Use an intact one-part/one-convex-hull PHYS payload matching the accepted bounded profile.",
                    ErrorCategory.UnsupportedCapability),
                exception);
        }

        if (!float.IsFinite(result))
        {
            throw Unsupported(code, $"{context} {key} is not finite.");
        }

        return result;
    }

    private static void RequireSingleValue(KVObject parent, string key, float expected, string context, string code)
    {
        if (RequireSingle(parent, key, context, code) != expected)
        {
            throw Unsupported(
                code,
                $"{context} {key} must equal {expected.ToString("R", CultureInfo.InvariantCulture)}.");
        }
    }

    private static float RequirePositiveSingle(KVObject parent, string key, string context)
    {
        var value = RequireSingle(parent, key, context, "PHYS_DERIVED_FIELD_UNSUPPORTED");
        if (value <= 0f)
        {
            throw Unsupported("PHYS_DERIVED_FIELD_UNSUPPORTED", $"{context} {key} must be finite and positive.");
        }

        return value;
    }

    private static void RequireBooleanValue(KVObject parent, string key, bool expected, string context, string code)
    {
        if (!parent.TryGetValue(key, out var value)
            || value is null
            || value.ValueType != KVValueType.Boolean
            || value.ToBoolean(CultureInfo.InvariantCulture) != expected)
        {
            throw Unsupported(code, $"{context} {key} must equal {expected}.");
        }
    }

    private static Point3 RequireVector3(KVObject parent, string key, string context, string code)
    {
        var vector = RequireArray(parent, key, context, code);
        if (vector.Count != 3)
        {
            throw Unsupported(code, $"{context} {key} is not a three-component vector.");
        }

        var components = new float[3];
        for (var index = 0; index < 3; index++)
        {
            var value = vector[index];
            if (value is null || value.IsArray || value.IsCollection)
            {
                throw Unsupported(code, $"{context} {key}[{index}] is not a scalar.");
            }

            try
            {
                components[index] = value.ToSingle(CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
            {
                throw new S2ModKitException(
                    new S2Error(
                        code,
                        "source2_adapter",
                        $"{context} {key}[{index}] is not a floating-point scalar.",
                        "Use an intact one-part/one-convex-hull PHYS payload matching the accepted bounded profile.",
                        ErrorCategory.UnsupportedCapability),
                    exception);
            }

            if (!float.IsFinite(components[index]))
            {
                throw Unsupported(code, $"{context} {key}[{index}] is not finite.");
            }
        }

        return new Point3(components[0], components[1], components[2]);
    }

    private static void RequireZeroVector(KVObject parent, string key, string context, string code)
    {
        var vector = RequireVector3(parent, key, context, code);
        if (vector.X != 0f || vector.Y != 0f || vector.Z != 0f)
        {
            throw Unsupported(code, $"{context} {key} must be the zero vector.");
        }
    }

    private static float[] RequireFiniteSingleArray(KVObject parent, string key, int expectedCount, string context)
    {
        var array = RequireArray(parent, key, context, "PHYS_DERIVED_FIELD_UNSUPPORTED");
        if (array.Count != expectedCount)
        {
            throw Unsupported(
                "PHYS_DERIVED_FIELD_UNSUPPORTED",
                $"{context} {key} holds {array.Count} values; the characterized {expectedCount}-value layout is required.");
        }

        var values = new float[array.Count];
        for (var index = 0; index < values.Length; index++)
        {
            var value = array[index];
            if (value is null || value.IsArray || value.IsCollection)
            {
                throw Unsupported("PHYS_DERIVED_FIELD_UNSUPPORTED", $"{context} {key}[{index}] is not a scalar.");
            }

            try
            {
                values[index] = value.ToSingle(CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
            {
                throw new S2ModKitException(
                    new S2Error(
                        "PHYS_DERIVED_FIELD_UNSUPPORTED",
                        "source2_adapter",
                        $"{context} {key}[{index}] is not a floating-point scalar.",
                        "Use an intact one-part/one-convex-hull PHYS payload matching the accepted bounded profile.",
                        ErrorCategory.UnsupportedCapability),
                    exception);
            }

            if (!float.IsFinite(values[index]))
            {
                throw Unsupported("PHYS_DERIVED_FIELD_UNSUPPORTED", $"{context} {key}[{index}] is not finite.");
            }
        }

        return values;
    }

    private static byte[] RequireBlob(KVObject parent, string key, string context, string code)
    {
        if (!parent.TryGetValue(key, out var value)
            || value is null
            || value.ValueType != KVValueType.BinaryBlob)
        {
            throw Unsupported(code, $"{context} is missing binary blob {key}.");
        }

        return value.AsBlob();
    }

    private static S2ModKitException Unsupported(string code, string summary) => Errors.Unsupported(
        code,
        summary,
        "Use an intact one-part/one-convex-hull PHYS payload matching the accepted bounded profile.");
}
