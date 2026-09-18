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
    private const int PhysBlockIndex = 2;

    [Fact]
    public void ReadsCharacterizedConvexHullGraphIntoBoundedFacts()
    {
        var payload = "phys"u8.ToArray();
        var topology = BuildTopology(TetraFaces);

        var result = Source2ConvexPhysReader.AnalyzeDetailed(
            ControlRoot(),
            Envelope(payload),
            _ => PhysRoot(),
            VisualBounds(),
            "synthetic physics");

        Assert.Equal(PhysBlockIndex, result.ResourceBlockIndex);
        Assert.Equal(ContentHash.Compute(payload), result.PayloadHash);
        Assert.Equal(4, result.VertexPositions.Length);
        Assert.Equal(new Point3(1f, 0f, 0f), result.VertexPositions[1]);
        Assert.Equal(new Point3(0f, 0f, 0f), result.PositionBounds.Min);
        Assert.Equal(new Point3(1f, 1f, 1f), result.PositionBounds.Max);
        Assert.Equal(new Point3(0.25f, 0.25f, 0.25f), result.Centroid);
        Assert.Equal(0.16666667f, result.Volume);
        Assert.Equal(2.3660254f, result.SurfaceArea);
        Assert.Equal(0.5f, result.OrthographicAreaX);
        Assert.Equal(12, result.MassProperties.Length);
        Assert.Equal(12, result.HalfEdgeCount);
        Assert.Equal(4, result.FaceCount);
        Assert.Equal(4, result.HullVertexEdgeCount);
        Assert.Equal(3, result.RegionNodeCount);
        Assert.Equal(4, result.HullPlanes.Length);
        Assert.Equal(4, result.RegionPlanes.Length);
        Assert.Equal(4, result.Faces.Length);
        Assert.Equal(0, result.Faces[0].VertexIndices[0]);
        Assert.Equal(2, result.Faces[0].VertexIndices[1]);
        Assert.Equal(1, result.Faces[0].VertexIndices[2]);
        Assert.Equal(result.Volume, result.DerivedValues.Volume);
        Assert.Equal(new Point3(0f, 0f, -1f), result.HullPlanes[0].Normal);
        Assert.Equal(0f, result.HullPlanes[0].Offset);
        Assert.Equal(new Point3(0.57735026f, 0.57735026f, 0.57735026f), result.HullPlanes[3].Normal);
        Assert.Equal(0.57735026f, result.HullPlanes[3].Offset);
        Assert.Equal(ContentHash.Compute(topology.Edges), result.HalfEdgesHash);
        Assert.Equal(ContentHash.Compute(topology.Faces), result.FacesHash);
        Assert.Equal(ContentHash.Compute(topology.VertexEdges), result.HullVertexEdgesHash);
        Assert.Equal("default", result.CollisionGroupString);
    }

    [Fact]
    public void RepeatedAnalysisIsDeterministic()
    {
        var payload = "phys"u8.ToArray();

        var first = Source2ConvexPhysReader.AnalyzeDetailed(ControlRoot(), Envelope(payload), _ => PhysRoot(), VisualBounds(), "synthetic physics");
        var second = Source2ConvexPhysReader.AnalyzeDetailed(ControlRoot(), Envelope(payload), _ => PhysRoot(), VisualBounds(), "synthetic physics");

        Assert.Equal(first.PayloadHash, second.PayloadHash);
        Assert.Equal(first.VertexPositions, second.VertexPositions);
        Assert.Equal(first.PositionBounds, second.PositionBounds);
        Assert.Equal(first.HullPlanes.Select(p => (p.Normal, p.Offset)), second.HullPlanes.Select(p => (p.Normal, p.Offset)));
        Assert.Equal(first.RegionPlanes.Select(p => (p.Normal, p.Offset)), second.RegionPlanes.Select(p => (p.Normal, p.Offset)));
        Assert.Equal(
            (first.HalfEdgesHash, first.FacesHash, first.HullVertexEdgesHash, first.RegionNodesHash, first.HullPlanesHash, first.RegionPlanesHash),
            (second.HalfEdgesHash, second.FacesHash, second.HullVertexEdgesHash, second.RegionNodesHash, second.HullPlanesHash, second.RegionPlanesHash));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void WriterRecomputesConvexValuesAndPreservesTopology(int serializationVersion)
    {
        var payload = "phys"u8.ToArray();
        var source = Source2ConvexPhysReader.AnalyzeDetailed(ControlRoot(), Envelope(payload), _ => PhysRoot(), VisualBounds(), "synthetic physics");
        var plan = CollisionPlan(source, 2f);
        using var resource = new Resource();
        var block = PhysBlock(PhysRoot(), resource, serializationVersion);

        var result = Source2ConvexPhysWriter.Rewrite(block, ControlRoot(), Envelope(payload), VisualBounds(), VisualBounds(), plan);

        Assert.Equal(plan.ExpectedPositionHash, HashPoints(result.Reopened.VertexPositions));
        Assert.Equal(plan.ExpectedAfter.Bounds, ToBounds(result.Reopened.PositionBounds));
        Assert.Equal(source.HalfEdgesHash, result.Reopened.HalfEdgesHash);
        Assert.Equal(source.FacesHash, result.Reopened.FacesHash);
        Assert.Equal(source.RegionNodesHash, result.Reopened.RegionNodesHash);
        Assert.Equal(source.CollisionGroupString, result.Reopened.CollisionGroupString);
        Assert.Equal(source.OrthographicAreaX, result.Reopened.OrthographicAreaX);
        var rewrittenMassProperties = block.Data["m_parts"][0]["m_rnShape"]["m_hulls"][0]["m_Hull"]["m_MassProperties"];
        Assert.All(rewrittenMassProperties, value => Assert.Equal(KVValueType.FloatingPoint64, value.Value.ValueType));
        Assert.True(result.Payload.Length > 0);
        Assert.True(BinaryPrimitives.ReadUInt16LittleEndian(result.Payload.Span.Slice(44, 2)) > 0);
        Assert.True(BinaryPrimitives.ReadUInt16LittleEndian(result.Payload.Span.Slice(46, 2)) > 0);
    }

    [Fact]
    public void WriterIsDeterministicAcrossIndependentSemanticGraphs()
    {
        var payload = "phys"u8.ToArray();
        var source = Source2ConvexPhysReader.AnalyzeDetailed(ControlRoot(), Envelope(payload), _ => PhysRoot(), VisualBounds(), "synthetic physics");
        var plan = CollisionPlan(source, 0.5f);
        using var firstResource = new Resource();
        using var secondResource = new Resource();
        var firstBlock = PhysBlock(PhysRoot(), firstResource);
        var secondBlock = PhysBlock(PhysRoot(), secondResource);

        var first = Source2ConvexPhysWriter.Rewrite(firstBlock, ControlRoot(), Envelope(payload), VisualBounds(), VisualBounds(), plan);
        var second = Source2ConvexPhysWriter.Rewrite(secondBlock, ControlRoot(), Envelope(payload), VisualBounds(), VisualBounds(), plan);

        Assert.True(first.Payload.Span.SequenceEqual(second.Payload.Span));
        Assert.Equal(first.SemanticHash, second.SemanticHash);
    }

    [Fact]
    public void WriterRejectsTopologyDriftBeforeSerialization()
    {
        var payload = "phys"u8.ToArray();
        var source = Source2ConvexPhysReader.AnalyzeDetailed(ControlRoot(), Envelope(payload), _ => PhysRoot(), VisualBounds(), "synthetic physics");
        var plan = CollisionPlan(source, 2f) with { FacesHash = ContentHash.Compute([99]) };
        using var resource = new Resource();
        var block = PhysBlock(PhysRoot(), resource);

        var exception = Assert.Throws<S2ModKitException>(() => Source2ConvexPhysWriter.Rewrite(
            block,
            ControlRoot(),
            Envelope(payload),
            VisualBounds(),
            VisualBounds(),
            plan));

        Assert.Equal("COUPLED_TRANSFORM_INCOMPLETE", exception.Error.Code);
    }

}
