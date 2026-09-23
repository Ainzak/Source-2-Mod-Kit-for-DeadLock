using System.Buffers.Binary;
using System.Collections.Immutable;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Source2.Tests;

public sealed partial class Source2TransformMetadataAnalyzerTests
{
    [Fact]
    public void AnalyzeMultiBufferMeshTracksAllVerticesAndDifferentlyNamedBones()
    {
        var result = Source2TransformMetadataAnalyzer.AnalyzeMultiBufferMesh(
            MultiBufferDescriptor(), MultiBufferData(), MultiBufferGeometry(), "synthetic lantern and cloak");

        Assert.Equal(4, result.VertexCount);
        Assert.Equal(string.Empty, result.LocalSkinningRootBone);
        Assert.Equal(3f, result.SceneBounds.Max.X);
        Assert.Collection(result.BoneBounds,
            first =>
            {
                Assert.Equal("lantern", first.BoneName);
                Assert.Equal([0, 1], first.InfluencedVertices);
                Assert.Equal((0f, 1f, 1f), (first.LocalBounds.Min.X, first.LocalBounds.Max.X, first.SphereRadius));
            },
            second =>
            {
                Assert.Equal("cloak", second.BoneName);
                Assert.Equal([2, 3], second.InfluencedVertices);
                Assert.Equal((2f, 3f, 3f), (second.LocalBounds.Min.X, second.LocalBounds.Max.X, second.SphereRadius));
            });
    }

    [Fact]
    public void AnalyzeMultiBufferMeshRejectsNearMatchWithIncompleteSecondBuffer()
    {
        var geometry = MultiBufferGeometry(coverSecondBuffer: false);
        var error = Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeMultiBufferMesh(
            MultiBufferDescriptor(), MultiBufferData(), geometry, "synthetic lantern and cloak"));
        Assert.Contains("incomplete draw-call coverage", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeMultiBufferMeshRejectsNearMatchWithStaleSceneBounds()
    {
        var error = Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeMultiBufferMesh(
            MultiBufferDescriptor(),
            MeshDataWithBoneBounds(Bounds((0f, 0f, 0f), (2f, 0f, 0f)),
                ("lantern", "", (0.5f, 0f, 0f), (0.5f, 0f, 0f), 1f),
                ("cloak", "", (2.5f, 0f, 0f), (0.5f, 0f, 0f), 3f)),
            MultiBufferGeometry(), "synthetic lantern and cloak"));
        Assert.Contains("scene bounds", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeMultiBufferMeshRejectsNearMatchWithUnknownSecondSkinningFormat()
    {
        var descriptor = MultiBufferDescriptor();
        descriptor["m_vertexBuffers"][1]["m_inputLayoutFields"][1]["m_Format"] = (ValveKeyValue.KVObject)99u;
        var error = Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeMultiBufferMesh(
            descriptor, MultiBufferData(), MultiBufferGeometry(), "synthetic lantern and cloak"));
        Assert.Contains("BLENDINDICES format 99", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeMultiBufferMeshRejectsNearMatchWithInvalidEightInfluenceWeights()
    {
        var geometry = MultiBufferGeometry();
        geometry.VertexBuffers[1].Decoded[36] = 254;
        var error = Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeMultiBufferMesh(
            MultiBufferDescriptor(), MultiBufferData(), geometry, "synthetic lantern and cloak"));
        Assert.Contains("blend weights sum to 254", error.Message, StringComparison.Ordinal);
    }

    private static ValveKeyValue.KVObject MultiBufferDescriptor() => Object(
        ("m_vertexBuffers", Array(
            Object(("m_inputLayoutFields", Array(
                Layout("POSITION", 6u, 0), Layout("BLENDINDICES", 12u, 20), Layout("BLENDWEIGHT", 11u, 28)))),
            Object(("m_inputLayoutFields", Array(
                Layout("POSITION", 6u, 0), Layout("BLENDINDICES", 4u, 20), Layout("BLENDWEIGHT", 11u, 36)))))));

    private static ValveKeyValue.KVObject MultiBufferData() => MeshDataWithBoneBounds(
        Bounds((0f, 0f, 0f), (3f, 0f, 0f)),
        ("lantern", "", (0.5f, 0f, 0f), (0.5f, 0f, 0f), 1f),
        ("cloak", "", (2.5f, 0f, 0f), (0.5f, 0f, 0f), 3f));

    private static Source2GeometryAnalysis MultiBufferGeometry(bool coverSecondBuffer = true)
    {
        var firstBytes = EightInfluenceBytes(36, 0, 0f, 1f);
        var secondBytes = EightInfluenceBytes(44, 1, 2f, 3f);
        var first = new VertexBufferSnapshot(0, 1, 2, 36, ContentHash.Compute(firstBytes),
            ContentHash.Compute(firstBytes), new PositionLayout("R32G32B32_FLOAT", 0, 36));
        var second = new VertexBufferSnapshot(1, 3, 2, 44, ContentHash.Compute(secondBytes),
            ContentHash.Compute(secondBytes), new PositionLayout("R32G32B32_FLOAT", 0, 44));
        var indices = new[]
        {
            new IndexBufferSnapshot(0, 2, 3, 2, EmptyHash, EmptyHash),
            new IndexBufferSnapshot(1, 4, 3, 2, EmptyHash, EmptyHash),
        };
        var zero = new GeometryBounds(new TransformVector3(), new TransformVector3());
        var calls = new[]
        {
            new Source2DrawCallAnalysis(new DrawCallGeometrySnapshot("dc_lantern", 0, 0, 0, 2, 2,
                new ContentHash(VertexSetHash.Compute([0, 1])), zero, true), [0, 1]),
            new Source2DrawCallAnalysis(new DrawCallGeometrySnapshot("dc_cloak", 1, 1, 0, 2,
                coverSecondBuffer ? 2 : 1,
                new ContentHash(VertexSetHash.Compute(coverSecondBuffer ? [0, 1] : [0])), zero, true),
                coverSecondBuffer ? [0, 1] : [0]),
        };
        var snapshot = new MeshGeometrySnapshot("ready", "synthetic",
            [first, second], indices, calls.Select(call => call.Snapshot).ToArray(), null);
        return new Source2GeometryAnalysis(snapshot,
            [new Source2VertexBufferAnalysis(first, firstBytes), new Source2VertexBufferAnalysis(second, secondBytes)],
            [new Source2IndexBufferAnalysis(indices[0], [0u, 1u, 0u]),
                new Source2IndexBufferAnalysis(indices[1], [0u, 1u, 0u])], calls);
    }

    private static byte[] EightInfluenceBytes(int stride, int boneIndex, float firstX, float secondX)
    {
        var result = new byte[stride * 2];
        for (var vertex = 0; vertex < 2; vertex++)
        {
            var offset = vertex * stride;
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset),
                BitConverter.SingleToInt32Bits(vertex == 0 ? firstX : secondX));
            if (stride == 44)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(offset + 20), checked((ushort)boneIndex));
            }
            else
            {
                result[offset + 20] = checked((byte)boneIndex);
            }

            result[offset + stride - 8] = byte.MaxValue;
        }

        return result;
    }
}
