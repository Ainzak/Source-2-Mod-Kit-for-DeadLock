using System.Globalization;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;
using ValveKeyValue;

namespace S2ModKit.Source2.Tests;

public sealed class Source2LodExclusionInspectionTests
{
    [Fact]
    public void InspectionAdmitsMixedActiveAndZeroMasksWithExcludedMeshesOpaque()
    {
        var table = Source2CompiledModelAdapter.ValidateRootMeshTable(
            Descriptors((0, 2), (1, 3), (2, 4)),
            Masks(1, 0, 2),
            2,
            Facts(3),
            allowLodExcludedMeshes: true);

        Assert.Equal(3, table.Length);
        Assert.Equal(2, table[0].BlockIndex);
        Assert.Equal([0], table[0].MeshLods);
        Assert.Equal(0UL, table[1].LodMask);
        Assert.Empty(table[1].MeshLods);
        Assert.Equal([1], table[2].MeshLods);
        var activeLods = table.Where(entry => entry.LodMask != 0).SelectMany(entry => entry.MeshLods).ToHashSet();
        Assert.Equal([0, 1], Source2CompiledModelAdapter.CollectActiveLodLevels(activeLods, 2));
    }

    [Fact]
    public void StrictParsingRejectsBoundedZeroMaskInputsAsReadOnly()
    {
        var error = Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.ValidateRootMeshTable(
            Descriptors((0, 2), (1, 3), (2, 4)),
            Masks(1, 0, 2),
            2,
            Facts(3),
            allowLodExcludedMeshes: false));

        Assert.Equal("ZERO_LOD_MESH_READ_ONLY", error.Error.Code);
        Assert.Contains("Embedded mesh 1", error.Error.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleZerosSharedActiveMasksOrdinaryAndHighBitsAreBounded()
    {
        var table = Source2CompiledModelAdapter.ValidateRootMeshTable(
            Descriptors((0, 2), (1, 3), (2, 4), (3, 5), (4, 6)),
            Masks(0, 0, 3, 3, 1),
            2,
            Facts(5),
            allowLodExcludedMeshes: true);

        Assert.All(table.Where(entry => entry.LodMask == 0), entry => Assert.Empty(entry.MeshLods));
        Assert.Equal([0, 1], table[2].MeshLods);
        Assert.Equal([0, 1], table[3].MeshLods);
        Assert.Equal([0], table[4].MeshLods);

        var highBit = Source2CompiledModelAdapter.ValidateRootMeshTable(
            Descriptors((0, 2)),
            Array((KVObject)"9223372036854775808"),
            64,
            Facts(1),
            allowLodExcludedMeshes: false);
        Assert.Equal([63], highBit[0].MeshLods);
    }

    [Fact]
    public void InvalidLodCountsAreRejectedEvenForZeroMasks()
    {
        AssertTableFailure(Descriptors((0, 2)), Masks(0), 0, Facts(1), true, "LOD_MASK_UNSUPPORTED");
        AssertTableFailure(Descriptors((0, 2)), Masks(0), 65, Facts(1), true, "LOD_MASK_UNSUPPORTED");
    }

    [Fact]
    public void OutOfRangeNonzeroMasksKeepLodMaskUnsupportedInBothModes()
    {
        AssertTableFailure(Descriptors((0, 2), (1, 3)), Masks(1, 16), 4, Facts(2), true, "LOD_MASK_UNSUPPORTED");
        AssertTableFailure(Descriptors((0, 2), (1, 3)), Masks(16, 1), 4, Facts(2), false, "LOD_MASK_UNSUPPORTED");
    }

    [Fact]
    public void MalformedDescriptorsFailBeforeAnyZeroIsSkippedOrRejected()
    {
        AssertTableFailure(Descriptors((0, 2), (1, 0)), Masks(0, 1), 1, Facts(2), true, "EMBEDDED_MESH_REFERENCE_UNSUPPORTED");
        AssertTableFailure(Descriptors((0, 2), (1, 0)), Masks(0, 1), 1, Facts(2), false, "EMBEDDED_MESH_REFERENCE_UNSUPPORTED");
        AssertTableFailure(Descriptors((0, 2), (1, 2)), Masks(0, 1), 1, Facts(2), true, "EMBEDDED_MESH_REFERENCE_UNSUPPORTED");
        AssertTableFailure(Descriptors((0, 2), (2, 3)), Masks(0, 1), 1, Facts(2), true, "EMBEDDED_MESH_REFERENCE_UNSUPPORTED");
        AssertTableFailure(Descriptors((0, 2), (1, 3)), Masks(0), 1, Facts(2), true, "EMBEDDED_MESH_COUNT_UNSUPPORTED");
    }

    [Fact]
    public void AllZeroModelsActiveGapsAndDistanceDisagreementAreExplicitlyRejected()
    {
        Assert.Equal("LOD_INVENTORY_UNSUPPORTED", Assert.Throws<S2ModKitException>(
            () => Source2CompiledModelAdapter.CollectActiveLodLevels(new HashSet<int>(), 1)).Error.Code);
        Assert.Equal("LOD_INVENTORY_UNSUPPORTED", Assert.Throws<S2ModKitException>(
            () => Source2CompiledModelAdapter.CollectActiveLodLevels(new HashSet<int> { 0, 2 }, 2)).Error.Code);
        Assert.Equal("LOD_INVENTORY_UNSUPPORTED", Assert.Throws<S2ModKitException>(
            () => Source2CompiledModelAdapter.CollectActiveLodLevels(new HashSet<int> { 0, 1 }, 3)).Error.Code);
        Assert.Equal([0, 1, 2, 3], Source2CompiledModelAdapter.CollectActiveLodLevels(new HashSet<int> { 3, 1, 0, 2 }, 4));
    }

    private static RootMeshTableFacts Facts(int mdatCount) => new(
        mdatCount + 2,
        ["DATA", "CTRL", .. Enumerable.Repeat("MDAT", mdatCount)],
        Enumerable.Range(2, mdatCount).ToArray());

    private static KVObject Descriptors(params (int Ordinal, int BlockIndex)[] entries) =>
        Array(entries.Select(entry => Object(
            ("m_nMeshIndex", (KVObject)entry.Ordinal),
            ("m_nDataBlock", (KVObject)entry.BlockIndex))).ToArray());

    private static KVObject Masks(params ulong[] masks) =>
        Array(masks.Select(mask => (KVObject)mask.ToString(CultureInfo.InvariantCulture)).ToArray());

    private static void AssertTableFailure(
        KVObject descriptors,
        KVObject masks,
        int lodDistanceCount,
        RootMeshTableFacts facts,
        bool allowLodExcludedMeshes,
        string expectedCode)
    {
        var error = Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.ValidateRootMeshTable(
            descriptors, masks, lodDistanceCount, facts, allowLodExcludedMeshes));

        Assert.Equal(expectedCode, error.Error.Code);
    }

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
