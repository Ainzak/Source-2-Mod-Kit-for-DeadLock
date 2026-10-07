using System.Buffers.Binary;
using S2ModKit.Adapters.Source2;
using ValveKeyValue;

namespace S2ModKit.Source2.Tests;

public sealed partial class Source2TransformMetadataAnalyzerTests
{
    [Fact]
    public void PairedMixedSignedReaderIncludesTinyAndUnindexedContributorsWithoutChangingOldAdmission()
    {
        var (descriptor, mesh, geometry) = SignedInventory();
        var raw = geometry.VertexBuffers[1].Decoded;
        raw[36] = 254; raw[37] = 1;
        BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(22), 0);
        BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(24), -1); // Opaque zero-weight padding.
        var before = geometry.VertexBuffers.Select(buffer => buffer.Decoded.ToArray()).ToArray();
        var result = Source2TransformMetadataAnalyzer.AnalyzePairedPreservationBuffers(descriptor, mesh, geometry, "paired signed inventory");
        Assert.Equal(4, result.VertexCount);
        Assert.Equal([0, 1, 2], result.BoneBounds.Single(bone => bone.BoneName == "lantern").InfluencedVertices);
        Assert.Equal([2, 3], result.BoneBounds.Single(bone => bone.BoneName == "cloak").InfluencedVertices);
        Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeVisualPreservationBuffers(descriptor, mesh, geometry, "old visual"));
        Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeMultiBufferMesh(descriptor, mesh, geometry, "old multi buffer"));
        for (var index = 0; index < before.Length; index++) Assert.Equal(before[index], geometry.VertexBuffers[index].Decoded);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("range")]
    [InlineData("sum")]
    [InlineData("count")]
    [InlineData("undeclared")]
    [InlineData("weight-format")]
    [InlineData("no-weight")]
    [InlineData("overlap")]
    [InlineData("stride")]
    [InlineData("semantic-duplicate")]
    [InlineData("slot")]
    public void PairedMixedSignedReaderRejectsMalformedCompleteRows(string defect)
    {
        var (descriptor, mesh, geometry) = SignedInventory();
        var layout = descriptor["m_vertexBuffers"][1]["m_inputLayoutFields"];
        var raw = geometry.VertexBuffers[1].Decoded;
        switch (defect)
        {
            case "negative": BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(20), -1); break;
            case "range": BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(20), short.MaxValue); break;
            case "sum": raw[36] = 254; break;
            case "count": mesh["m_skeleton"]["m_nBoneWeightCount"] = (KVObject)5; break;
            case "undeclared": mesh["m_skeleton"]["m_nBoneWeightCount"] = (KVObject)1; raw[36] = 254; raw[37] = 1; break;
            case "weight-format": layout[2]["m_Format"] = (KVObject)11u; break;
            case "no-weight": layout[2]["m_pSemanticName"] = (KVObject)"TEXCOORD"; break;
            case "overlap": layout[2]["m_nOffset"] = (KVObject)24; break;
            case "stride": layout[1]["m_nOffset"] = (KVObject)40; break;
            case "semantic-duplicate": layout.Add(Layout("BLENDINDICES", 14u, 20)); break;
            case "slot": layout[1]["m_nSlot"] = (KVObject)1; break;
        }
        Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzePairedPreservationBuffers(descriptor, mesh, geometry, "paired malformed signed inventory"));
    }

    private static (KVObject Descriptor, KVObject Mesh, Source2GeometryAnalysis Geometry) SignedInventory()
    {
        var descriptor = MultiBufferDescriptor(); var mesh = MultiBufferData();
        descriptor["m_vertexBuffers"][1]["m_inputLayoutFields"][1]["m_Format"] = (KVObject)14u;
        descriptor["m_vertexBuffers"][1]["m_inputLayoutFields"][2]["m_Format"] = (KVObject)28u;
        mesh["m_skeleton"]["m_nBoneWeightCount"] = (KVObject)4;
        return (descriptor, mesh, MultiBufferGeometry(coverSecondBuffer: false));
    }
}
