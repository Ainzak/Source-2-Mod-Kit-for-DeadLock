using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Source2.Tests;

public sealed partial class DirectionalSourceBoundaryTests
{
    [Fact]
    public void IndependentBoneProtectionReadsExcludedUnindexedAndExplicitZeroRows()
    {
        var selected = Buffer(0, true, [new(10, 0, 0), new(11, 0, 0)], contributors: [1]);
        var excluded = Buffer(1, false, [new(12, 0, 0)], contributors: [0]);
        var empty = Buffer(2, false, [new(13, 0, 0)]);
        var expected = new[] { Protected(selected, [1]), Protected(excluded, [0]), Protected(empty, []) };
        var bone = new DirectionalBoneAssertion
        {
            AssertionId = "root",
            Version = 1,
            BoneName = "root",
            BoneIndex = 0,
            RootSkeletonHash = Hash,
            Lods = [new(0, DirectionalContractValidator.ContributorSetHash(expected), 2)]
        };
        var observed = Source2CompiledModelAdapter.AuditDirectionalProtection(new("keep_fixed", 1, [bone]), [selected, excluded, empty], Hash, ["root"]);
        Assert.Equal(JsonDefaults.Serialize(expected), JsonDefaults.Serialize(observed.Assertions[0].Sets));
        Assert.Equal(2, observed.Union.Sum(s => s.VertexCount));
        Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.AuditDirectionalProtection(new("keep_fixed", 1,
            [bone with { Lods = [bone.Lods[0] with { ContributorCount = 1 }] }]), [selected, excluded, empty], Hash, ["root"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndependentProtectionRejectsMovedPositionOrCompleteFrame(bool frame)
    {
        var source = Buffer(0, true, [new(10, 0, 0)]);
        var modified = source.After.ToArray(); modified[frame ? 12 : 0] ^= 1;
        Assert.Equal("DIRECTIONAL_RESULT_DRIFT", Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.AuditDirectionalProtection(
            new("keep_fixed", 1, [Assertion("fixed", source, [0])]), [source with { After = modified }], Hash, ["root"])).Error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndependentCoincidencesUseExactBinary32NotRoundedDisplayOrProximity(bool exact)
    {
        var selected = Buffer(0, true, [new(1.1f, 1, -0f)], transform: true);
        var excluded = Buffer(1, false, [new(exact ? (float)1.100000023841858 : float.BitIncrement(1.1f), 1, 0f)]);
        if (exact) Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.AuditDirectionalCoincidences([selected, excluded]));
        else Assert.Equal(0, Assert.Single(Source2CompiledModelAdapter.AuditDirectionalCoincidences([selected, excluded])).PairCount);
    }

    [Fact]
    public void IndependentCoincidenceInventoryIsCanonicalAndOpaqueFramesCannotBeProtected()
    {
        var first = Buffer(0, true, [new(1, 1, -0f), new(1, 1, 0f)], transform: true);
        var second = Buffer(1, true, [new(1, 1, 0f)], transform: true);
        var result = Assert.Single(Source2CompiledModelAdapter.AuditDirectionalCoincidences([second, first]));
        Assert.Equal(3, result.PairCount); Assert.Equal(3, result.MovingSelectedRecordCount);
        Assert.Equal(result, Assert.Single(Source2CompiledModelAdapter.AuditDirectionalCoincidences([first, second])));
        var opaque = Buffer(2, false, [new(12, 0, 0)], contributors: [0]) with { Frame = null };
        var bone = new DirectionalBoneAssertion { AssertionId = "opaque", Version = 1, BoneName = "root", BoneIndex = 0, RootSkeletonHash = Hash, Lods = [] };
        Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.AuditDirectionalProtection(new("keep_fixed", 1, [bone]), [first, opaque], Hash, ["root"]));
    }
}
