using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using ValveKeyValue;

namespace S2ModKit.Source2.Tests;

public sealed class Source2MeshLayoutClassifierTests
{
    [Fact]
    public void ClassifiesSupportedRootMdatMvtxMidxProfile()
    {
        var result = Classify([], "DATA", "CTRL", "MDAT", "MVTX", "MIDX");

        Assert.True(result.IsSupported);
        Assert.Equal(Source2MeshLayoutKind.RootMdatBuffers, result.Kind);
        Assert.Null(result.ErrorCode);
        Assert.Empty(result.NonEmptyReferences);
        Assert.Equal(0, result.EmptyReferenceCount);
        Source2MeshLayoutClassifier.RequireSupportedRoot(result);
    }

    [Fact]
    public void ClassifiesEmptyHandleWithEmbeddedMbufWithoutCallingItExternal()
    {
        var result = Classify([string.Empty], "DATA", "CTRL", "MDAT", "MBUF", "PHYS");

        Assert.True(result.IsSupported);
        Assert.Equal(Source2MeshLayoutKind.EmbeddedMbuf, result.Kind);
        Assert.Null(result.ErrorCode);
        Assert.Equal(1, result.EmptyReferenceCount);
        Assert.Empty(result.NonEmptyReferences);
        var exception = Assert.Throws<S2ModKitException>(() =>
            Source2MeshLayoutClassifier.RequireSupportedRoot(result));
        Assert.Equal("MESH_BUFFER_LAYOUT_MALFORMED", exception.Error.Code);
    }

    [Fact]
    public void ClassifiesOneRealExternalMeshResourceReference()
    {
        var result = Classify(["Models/Shared/Weapon.vmesh"], "DATA", "CTRL");

        Assert.Equal(Source2MeshLayoutKind.ExternalResource, result.Kind);
        Assert.Equal("EXTERNAL_MESH_RESOURCE_UNSUPPORTED", result.ErrorCode);
        Assert.Equal(["models/shared/weapon.vmesh"], result.NonEmptyReferences);
    }

    [Fact]
    public void ClassifiesUnresolvedEmptyHandleWithoutMbufPrecisely()
    {
        var result = Classify(["  "], "DATA", "CTRL");

        Assert.Equal(Source2MeshLayoutKind.EmptyReference, result.Kind);
        Assert.Equal("EMPTY_MESH_REFERENCE_UNSUPPORTED", result.ErrorCode);
        Assert.Equal(1, result.EmptyReferenceCount);
    }

    [Theory]
    [MemberData(nameof(MalformedProfiles))]
    public void RejectsMalformedOrAmbiguousStorageCombinations(
        string[] references,
        string[] blockTypes,
        string expectedCode)
    {
        var result = Classify(references, blockTypes);

        Assert.Equal(Source2MeshLayoutKind.Malformed, result.Kind);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.False(result.IsSupported);
    }

    public static TheoryData<string[], string[], string> MalformedProfiles => new()
    {
        { [string.Empty, "models/shared/weapon.vmesh"], ["DATA", "CTRL"], "MESH_LAYOUT_COMBINATION_MALFORMED" },
        { [], ["DATA", "CTRL", "MDAT", "MVTX", "MIDX", "MBUF"], "MESH_BUFFER_LAYOUT_MALFORMED" },
        { [], ["DATA", "CTRL", "MDAT", "MVTX"], "MESH_BUFFER_LAYOUT_MALFORMED" },
        { [], ["DATA", "CTRL"], "MESH_BUFFER_LAYOUT_MALFORMED" },
    };

    [Fact]
    public void NonEmptyHandleRemainsARealExternalReferenceWhenEmbeddedBlocksAlsoExist()
    {
        var result = Classify(
            ["models/shared/weapon.vmesh"],
            "DATA", "CTRL", "MDAT", "MVTX", "MIDX");

        Assert.Equal(Source2MeshLayoutKind.ExternalResource, result.Kind);
        Assert.Equal("EXTERNAL_MESH_RESOURCE_UNSUPPORTED", result.ErrorCode);
    }

    [Fact]
    public void RejectsMissingOrNonStringMeshReferenceTables()
    {
        var missing = Source2MeshLayoutClassifier.Classify(Object(), ["DATA"]);
        Assert.Equal("MESH_REFERENCE_TABLE_MALFORMED", missing.ErrorCode);

        var nonString = Source2MeshLayoutClassifier.Classify(
            Object(("m_refMeshes", Array(Object(("path", "models/shared/weapon.vmesh"))))),
            ["DATA"]);
        Assert.Equal("MESH_REFERENCE_TABLE_MALFORMED", nonString.ErrorCode);
    }

    [Fact]
    public void ExtractsCanonicalMechanicalLineageOnlyFromDescriptorName()
    {
        var descriptor = Object(
            ("m_Name", "  HaZe_GuN_L  "),
            ("m_nMeshIndex", 42));

        var lineage = Source2MeshLineageExtractor.Extract(descriptor, 0, "embedded_meshes[42]");

        Assert.Equal("haze_gun_l", lineage.Key);
        Assert.Equal("HaZe_GuN_L", lineage.SourceLabel);
        Assert.Equal("HaZe_GuN_L", lineage.SourceName);
        Assert.True(lineage.IsCanonical());
    }

    [Fact]
    public void NormalizesOnlyAnExactSuffixMatchingTheAuthoritativeLod()
    {
        var matched = Source2MeshLineageExtractor.Extract(
            Object(("m_Name", "haze_gun_l_lod2")),
            2,
            "embedded_meshes[7]");
        var mismatched = Source2MeshLineageExtractor.Extract(
            Object(("m_Name", "haze_gun_l_lod3")),
            2,
            "embedded_meshes[7]");

        Assert.Equal("haze_gun_l", matched.Key);
        Assert.Equal("haze_gun_l", matched.SourceLabel);
        Assert.Equal("haze_gun_l_lod2", matched.SourceName);
        Assert.Equal("haze_gun_l_lod3", mismatched.Key);
        Assert.Equal("haze_gun_l_lod3", mismatched.SourceName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gun\u0000left")]
    public void RejectsInvalidDescriptorNames(string sourceName)
    {
        var exception = Assert.Throws<S2ModKitException>(() =>
            Source2MeshLineageExtractor.Extract(
                Object(("m_Name", sourceName)),
                0,
                "embedded_meshes[0]"));

        Assert.Equal("MESH_LINEAGE_SOURCE_NAME_INVALID", exception.Error.Code);
    }

    [Fact]
    public void RejectsNonStringDescriptorName()
    {
        var exception = Assert.Throws<S2ModKitException>(() =>
            Source2MeshLineageExtractor.Extract(
                Object(("m_Name", 7)),
                0,
                "embedded_meshes[0]"));

        Assert.Equal("MESH_LINEAGE_SOURCE_NAME_INVALID", exception.Error.Code);
    }

    private static Source2MeshLayoutClassification Classify(
        string[] references,
        params string[] blockTypes) => Source2MeshLayoutClassifier.Classify(
            Object(("m_refMeshes", Array(references.Select(reference => (KVObject)reference).ToArray()))),
            blockTypes);

    private static KVObject Object(params (string Key, KVObject Value)[] values)
    {
        var result = KVObject.Collection();
        foreach (var (key, value) in values)
        {
            result.Add(key, value);
        }

        return result;
    }

    private static KVObject Array(params KVObject[] values)
    {
        var result = KVObject.Array();
        foreach (var value in values)
        {
            result.Add(value);
        }

        return result;
    }
}
