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
    [Fact]
    public void RejectsHalfEdgeStrideViolation()
    {
        var root = PhysRoot();
        var broken = new byte[11];
        Hull(root)["m_Edges"] = KVObject.Blob(broken);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsPositionStrideViolation()
    {
        var root = PhysRoot();
        Hull(root)["m_VertexPositions"] = KVObject.Blob(new byte[45]);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsEdgeIndexOutsideTopology()
    {
        var root = PhysRoot();
        var (edges, _, _) = BuildTopology(TetraFaces);
        edges[0] = 200;
        Hull(root)["m_Edges"] = KVObject.Blob(edges);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsBrokenTwinInvolution()
    {
        var root = PhysRoot();
        var (edges, _, _) = BuildTopology(TetraFaces);
        edges[1] = 2;
        Hull(root)["m_Edges"] = KVObject.Blob(edges);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsEulerMismatch()
    {
        var root = PhysRoot();
        var (edges, _, _) = BuildTopology(TetraFaces);
        Hull(root)["m_Edges"] = KVObject.Blob(edges[..16]);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsDuplicateHullVertexEdge()
    {
        var root = PhysRoot();
        Hull(root)["m_Vertices"] = KVObject.Blob([0, 1, 2, 2]);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsVertexEdgeCountMismatch()
    {
        var root = PhysRoot();
        Hull(root)["m_Vertices"] = KVObject.Blob([0, 1, 2]);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsVertexEdgeWithWrongOrigin()
    {
        var root = PhysRoot();
        Hull(root)["m_Vertices"] = KVObject.Blob([0, 1, 1, 5]);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsVertexEdgeOutsideTopology()
    {
        var root = PhysRoot();
        Hull(root)["m_Vertices"] = KVObject.Blob([0, 2, 1, 255]);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsPlaneStrideViolation()
    {
        var root = PhysRoot();
        Hull(root)["m_Planes"] = KVObject.Blob(new byte[63]);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsNonUnitPlaneNormal()
    {
        var root = PhysRoot();
        var planes = Planes();
        WriteSingle(planes, 0, 0.5f);
        WriteSingle(planes, 4, 0.5f);
        WriteSingle(planes, 8, 0.5f);
        var region = Region(root);
        region["m_Planes"] = KVObject.Blob(planes);
        Hull(root)["m_Planes"] = KVObject.Blob(planes);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void RejectsNonFinitePlaneNormal(float value)
    {
        var root = PhysRoot();
        var planes = Planes();
        WriteSingle(planes, 0, value);
        Region(root)["m_Planes"] = KVObject.Blob(planes);
        Hull(root)["m_Planes"] = KVObject.Blob(planes);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.NegativeInfinity)]
    public void RejectsNonFinitePlaneOffset(float value)
    {
        var root = PhysRoot();
        var planes = Planes();
        WriteSingle(planes, 12, value);
        Region(root)["m_Planes"] = KVObject.Blob(planes);
        Hull(root)["m_Planes"] = KVObject.Blob(planes);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsVertexOutsideHullPlanes()
    {
        var root = PhysRoot();
        var positions = Positions();
        foreach (var vertex in new[] { 1, 2, 3 })
        {
            WriteSingle(positions, vertex * 12, 4f);
            WriteSingle(positions, (vertex * 12) + 4, 4f);
            WriteSingle(positions, (vertex * 12) + 8, 4f);
        }

        var bounds = BoundsCollection(root);
        bounds["m_vMaxBounds"] = Vector(4f, 4f, 4f);
        Hull(root)["m_VertexPositions"] = KVObject.Blob(positions);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsHullPlaneMissingFromRegionSet()
    {
        var root = PhysRoot();
        var planes = Planes();
        WriteSingle(planes, 3 * 16 + 12, 2f);
        Region(root)["m_Planes"] = KVObject.Blob(planes);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsFacePlaneCountMismatch()
    {
        var root = PhysRoot();
        var (edges, _, _) = BuildTopology(TetraFaces);
        var faces = new byte[3];
        Hull(root)["m_Faces"] = KVObject.Blob(faces);
        Hull(root)["m_Edges"] = KVObject.Blob(edges);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsFaceStartOutsideTopology()
    {
        var root = PhysRoot();
        var (_, faces, _) = BuildTopology(TetraFaces);
        faces[0] = 200;
        Hull(root)["m_Faces"] = KVObject.Blob(faces);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsFaceStartOwnedByDifferentFace()
    {
        var root = PhysRoot();
        var (_, faces, _) = BuildTopology(TetraFaces);
        faces[0] = faces[1];
        Hull(root)["m_Faces"] = KVObject.Blob(faces);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsSelfTwin()
    {
        var root = PhysRoot();
        var (edges, _, _) = BuildTopology(TetraFaces);
        edges[1] = 0;
        Hull(root)["m_Edges"] = KVObject.Blob(edges);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsTwinThatDoesNotReverseEndpoints()
    {
        var root = PhysRoot();
        var (edges, _, _) = BuildTopology(TetraFaces);
        edges[(0 * 4) + 1] = 9;
        edges[(9 * 4) + 1] = 0;
        edges[(1 * 4) + 1] = 8;
        edges[(8 * 4) + 1] = 1;
        Hull(root)["m_Edges"] = KVObject.Blob(edges);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsTwoEdgeFaceCycle()
    {
        var root = PhysRoot();
        var (edges, _, _) = BuildTopology(TetraFaces);
        edges[(0 * 4) + 0] = 1;
        edges[(1 * 4) + 0] = 0;
        Hull(root)["m_Edges"] = KVObject.Blob(edges);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsRegionNodeStrideViolation()
    {
        var root = PhysRoot();
        Region(root)["m_Nodes"] = KVObject.Blob(new byte[7]);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsRegionNodePlaneOutsidePlaneTable()
    {
        var root = PhysRoot();
        var nodes = RegionNodes();
        WriteUInt16(nodes, 2, 0x8004);
        Region(root)["m_Nodes"] = KVObject.Blob(nodes);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsRegionNodeBranchPayloadOutsideTable()
    {
        var root = PhysRoot();
        var nodes = RegionNodes();
        WriteUInt16(nodes, 0, 3);
        Region(root)["m_Nodes"] = KVObject.Blob(nodes);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsUnsupportedRegionLeafEncoding()
    {
        var root = PhysRoot();
        var nodes = RegionNodes();
        WriteUInt16(nodes, 6, 1);
        Region(root)["m_Nodes"] = KVObject.Blob(nodes);

        AssertCode("PHYS_CONVEX_TOPOLOGY_UNSUPPORTED", root);
    }

}
