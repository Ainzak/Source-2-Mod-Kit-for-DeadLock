using S2ModKit.Adapters.Source2;

namespace S2ModKit.Source2.Tests;

public sealed partial class Source2TransformMetadataAnalyzerTests
{
    [Fact]
    public void VisualInventoryRetainsAuthoredScenePaddingWithoutChangingStrictAdmission()
    {
        var descriptor = VertexDescriptor();
        var mesh = MeshDataWithBoneBounds(Bounds((-4f, -4f, -4f), (4f, 4f, 4f)),
            ("root", "", (0f, 0f, 0f), (2f, 2f, 2f), 1f));
        var geometry = Geometry(VisualVertices(), [0, 1, 2]);
        Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
            descriptor, mesh, geometry, "strict", boneSizeIsHalfExtent: true, requireAllBoneSpheres: false));
        var inventory = Source2TransformMetadataAnalyzer.AnalyzeVisualPreservationBuffers(descriptor, mesh, geometry, "visual");
        Assert.Equal(-4f, inventory.SceneBounds.Min.X);
        Assert.Equal(4f, inventory.SceneBounds.Max.X);
        var bone = Assert.Single(inventory.BoneBounds);
        Assert.Equal(2f, bone.LocalBoundsSize.X);
        Assert.Equal(1f, bone.SphereRadius);
        Assert.Equal([0, 1, 2], bone.InfluencedVertices);
    }

    [Fact]
    public void VisualInventoryIncludesUnindexedContributorsInsteadOfFollowingOnlyDrawCalls()
    {
        var mesh = MeshDataWithBoneBounds(Bounds((-4f, -4f, -4f), (4f, 4f, 4f)),
            ("root", "", (0f, 0f, 0f), (2f, 2f, 2f), 1f));
        var geometry = Geometry(VisualVertices(), [0, 1]);
        var inventory = Source2TransformMetadataAnalyzer.AnalyzeVisualPreservationBuffers(VertexDescriptor(), mesh, geometry, "visual");
        Assert.Equal(3, inventory.VertexCount);
        Assert.Equal([0, 1, 2], Assert.Single(inventory.BoneBounds).InfluencedVertices);
        Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(VertexDescriptor(), mesh, geometry, "strict"));
    }

    [Fact]
    public void VisualInventoryDoesNotRepairOrQualifySentinelBoxes()
    {
        var mesh = MeshDataWithBoneBounds(Bounds((-4f, -4f, -4f), (4f, 4f, 4f)),
            ("root", "", (0f, 0f, 0f), (0f, 0f, 0f), 1f));
        var inventory = Source2TransformMetadataAnalyzer.AnalyzeVisualPreservationBuffers(VertexDescriptor(), mesh,
            Geometry(VisualVertices(), [0, 1, 2]), "visual");
        var bone = Assert.Single(inventory.BoneBounds);
        Assert.Equal(0f, bone.LocalBoundsSize.X);
        Assert.Equal(0f, bone.LocalBoundsSize.Y);
        Assert.Equal(0f, bone.LocalBoundsSize.Z);
    }

    [Fact]
    public void VisualInventoryStillRejectsMalformedSkinningAndPerDrawBounds()
    {
        var mesh = MeshDataWithBoneBounds(Bounds((-4f, -4f, -4f), (4f, 4f, 4f)),
            ("root", "", (0f, 0f, 0f), (2f, 2f, 2f), 1f));
        var vertices = VisualVertices();
        vertices[24] = 254;
        Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeVisualPreservationBuffers(
            VertexDescriptor(), mesh, Geometry(vertices, [0, 1, 2]), "visual"));
        mesh["m_sceneObjects"][0]["m_drawBounds"].Add(Bounds((-1f, -1f, -1f), (1f, 1f, 1f)));
        Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeVisualPreservationBuffers(
            VertexDescriptor(), mesh, Geometry(VisualVertices(), [0, 1, 2]), "visual"));
    }

    private static byte[] VisualVertices() => SkinnedVertices(
        ((-1f, -1f, -1f), [0, 0, 0, 0], [255, 0, 0, 0]),
        ((1f, -1f, 1f), [0, 0, 0, 0], [255, 0, 0, 0]),
        ((0f, 1f, 0f), [0, 0, 0, 0], [255, 0, 0, 0]));
}
