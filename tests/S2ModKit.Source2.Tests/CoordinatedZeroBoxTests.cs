using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;

namespace S2ModKit.Source2.Tests;

public sealed class CoordinatedZeroBoxTests
{
    private static readonly ZeroBoneBoxPolicy Policy = new("preserve_all_zero_single_contributor_unverified", 1);
    private static readonly float[] Matrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];
    private static readonly Point3[] Points = [new(1, 2, 3), new(2, 3, 4)];

    [Fact]
    public void ExactPositiveZeroSingleContributorIsPreservedWithoutGeometricReinterpretation()
    {
        var result = Read(Box(), [1]);
        Assert.Equal(new uint[6], result.OriginalWords);
        Assert.Equal(Matrix.Select(BitConverter.SingleToUInt32Bits), result.MatrixWords);
        Assert.Equal(1, result.ContributorMeshVertexIndex);
        // The point is not at the zero-box center; admission makes no containment claim.
        Assert.NotEqual(default, Points[result.ContributorMeshVertexIndex]);
    }

    [Fact]
    public void MissingWrongAndRejectionPoliciesCannotAdmitTheException()
    {
        foreach (var policy in new ZeroBoneBoxPolicy?[] { null, new("reject", 1), new("preserve_unverified", 1), Policy with { Version = 2 } })
            Assert.Throws<S2ModKitException>(() => Read(Box(), [0], policy: policy, replacePolicy: true));
    }

    [Fact]
    public void EveryNonzeroSignedZeroAndNonBinary32StorageNearMatchRejects()
    {
        for (var index = 0; index < 6; index++)
        {
            foreach (var invalid in new[] { new KVObject(-0f), new KVObject(float.Epsilon), new KVObject(-1f),
                new KVObject(0), new KVObject(0d), new KVObject("0") })
            {
                var box = Box(index, invalid);
                Assert.Throws<S2ModKitException>(() => Read(box, [0]));
            }
        }
        var extra = Box(); extra.Add("unknown", new KVObject(0));
        Assert.Throws<S2ModKitException>(() => Read(extra, [0]));
    }

    [Fact]
    public void IncompleteAmbiguousContributorsProceduralDependencyAndInvalidSpheresReject()
    {
        foreach (var contributors in new int[][] { [], [0, 1], [0, 0], [-1], [2] })
            Assert.Throws<S2ModKitException>(() => Read(Box(), contributors));
        Assert.Throws<S2ModKitException>(() => Read(Box(), [0], procedural: true));
        foreach (var radius in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
            Assert.Throws<S2ModKitException>(() => Read(Box(), [0], radius: radius));
    }

    [Fact]
    public void MissingNonfiniteAndBitDriftedInverseBindMatricesReject()
    {
        var shortMatrix = Array(Matrix[..11]);
        Assert.Throws<S2ModKitException>(() => Read(Box(), [0], matrix: shortMatrix));
        foreach (var value in new[] { float.NaN, float.PositiveInfinity, -0f, 2f })
        {
            var matrix = Array(Matrix.Select((word, index) => index == 1 ? new KVObject(value) : new KVObject(word)).ToArray());
            Assert.Throws<S2ModKitException>(() => Read(Box(), [0], matrix: matrix));
        }
        var inexact = Array(Matrix.Select((word, index) => index == 1 ? new KVObject(0.1d) : new KVObject(word)).ToArray());
        Assert.Throws<S2ModKitException>(() => Read(Box(), [0], matrix: inexact));
    }

    [Fact]
    public void PairedPositiveZeroSphereRequiresSeparatePolicyAndRetainsItsActualStorage()
    {
        var policy = new ZeroRenderSpherePolicy("preserve_zero_render_sphere_with_zero_box_unverified", 1);
        foreach (var (raw, storage) in new[] { (new KVObject(0f), "binary32"), (new KVObject(0d), "binary64") })
        {
            var result = Source2CompiledModelAdapter.ReadCoordinatedZeroBoxSource(Box(), [0], Points,
                Array(Matrix), Matrix, 0, false, Policy, raw, policy);
            Assert.Equal(new CoordinatedZeroRenderSphereSource(storage, 0), result.ZeroRenderSphere);
        }
        foreach (var invalid in new[] { new KVObject(-0f), new KVObject(-0d), new KVObject(0), new KVObject(double.Epsilon), new KVObject("0") })
            Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.ReadCoordinatedZeroBoxSource(Box(), [0], Points,
                Array(Matrix), Matrix, 0, false, Policy, invalid, policy));
        foreach (var invalid in new ZeroRenderSpherePolicy?[] { null, policy with { Version = 2 }, new("preserve_unverified", 1) })
            Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.ReadCoordinatedZeroBoxSource(Box(), [0], Points,
                Array(Matrix), Matrix, 0, false, Policy, new KVObject(0d), invalid));
    }

    [Fact]
    public void PairedSpherePolicyCannotWaiveTheZeroBoxOrContributorGate()
    {
        var policy = new ZeroRenderSpherePolicy("preserve_zero_render_sphere_with_zero_box_unverified", 1);
        foreach (var contributors in new int[][] { [], [0, 1] })
            Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.ReadCoordinatedZeroBoxSource(Box(), contributors, Points,
                Array(Matrix), Matrix, 0, false, Policy, new KVObject(0d), policy));
        Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.ReadCoordinatedZeroBoxSource(Box(0, new KVObject(1f)), [0], Points,
            Array(Matrix), Matrix, 0, false, Policy, new KVObject(0d), policy));
        Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.ReadCoordinatedZeroBoxSource(Box(), [0], Points,
            Array(Matrix), Matrix, 0, true, Policy, new KVObject(0d), policy));
    }

    private static CoordinatedZeroBoxSource Read(KVObject box, int[] contributors, KVObject? matrix = null,
        float radius = 1, bool procedural = false, ZeroBoneBoxPolicy? policy = null, bool replacePolicy = false) =>
        Source2CompiledModelAdapter.ReadCoordinatedZeroBoxSource(box, contributors, Points,
            matrix ?? Array(Matrix), Matrix, radius, procedural, replacePolicy ? policy : Policy);
    private static KVObject Box(int replacementIndex = -1, KVObject? replacement = null)
    {
        var box = KVObject.Collection();
        box.Add("m_vecCenter", Array(Enumerable.Range(0, 3).Select(index => index == replacementIndex ? replacement! : new KVObject(0f)).ToArray()));
        box.Add("m_vecSize", Array(Enumerable.Range(3, 3).Select(index => index == replacementIndex ? replacement! : new KVObject(0f)).ToArray()));
        return box;
    }
    private static KVObject Array(float[] values)
    {
        return Array(values.Select(value => new KVObject(value)).ToArray());
    }
    private static KVObject Array(KVObject[] values)
    {
        var result = KVObject.Array();
        foreach (var value in values) result.Add(value);
        return result;
    }
}
