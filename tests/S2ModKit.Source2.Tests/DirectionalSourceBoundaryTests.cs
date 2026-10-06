using System.Buffers.Binary;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Source2.Tests;

public sealed partial class DirectionalSourceBoundaryTests
{
    private static readonly PositionLayout Position = new("R32G32B32_FLOAT", 0, 24);
    private static readonly PackedFrameLayout Frame = new("R32_UINT", 12, 24, "source2_normal_tangent_v2");
    private static readonly ContentHash Hash = ContentHash.Compute("synthetic-source"u8);
    private static readonly uint Packed = Source2PackedFrameCodec.Encode(new(new Point3(1, 1, 1) * (1 / MathF.Sqrt(3)), new Point3(1, -1, 0) * (1 / MathF.Sqrt(2)), -1));

    [Fact]
    public void CompleteWordsIncludeUnindexedRecordsAnisotropicCoreAndPinnedSignedZero()
    {
        Point3[] points = [new(0, 0, -0f), new(1, 1, -0f), new(6, 1, -0f), new(10, 0, -0f), new(6, 1, -0f)];
        var bytes = Bytes(points); var immutable = bytes.ToArray();
        var result = Source2CompiledModelAdapter.CalculateDirectionalWords(bytes, Position, Frame, points.Length, Math());
        Assert.Equal(immutable, bytes); Assert.Equal(2, result.Core); Assert.Equal(2, result.Transition); Assert.Equal(1, result.Pinned);
        Assert.DoesNotContain(0, result.ChangedPositions); Assert.Contains(0, result.ChangedFrames);
        Assert.Contains(4, result.ChangedPositions); // No topology/indexed subset is used to omit this record.
        Assert.Equal(result.Points[2], result.Points[4]);
        Assert.Equal(immutable.AsSpan(72, 24).ToArray(), result.Bytes.AsSpan(72, 24).ToArray());
        for (var i = 0; i < points.Length; i++)
        {
            Assert.Equal(0x80000000u, BinaryPrimitives.ReadUInt32LittleEndian(result.Bytes.AsSpan(i * 24 + 8)));
            Assert.Equal(immutable.AsSpan(i * 24 + 16, 8).ToArray(), result.Bytes.AsSpan(i * 24 + 16, 8).ToArray());
            Assert.Equal(-1, Source2PackedFrameCodec.Decode(BinaryPrimitives.ReadUInt32LittleEndian(result.Bytes.AsSpan(i * 24 + 12))).Handedness);
        }
        var repeated = Source2CompiledModelAdapter.CalculateDirectionalWords(bytes, Position, Frame, points.Length, Math());
        Assert.Equal(result.Bytes, repeated.Bytes); Assert.Equal(result.MaskHash, repeated.MaskHash); Assert.Equal(result.WeightHash, repeated.WeightHash);
        Assert.Equal(Source2CompiledModelAdapter.DirectionalUnchangedWords(bytes, Position, Frame), Source2CompiledModelAdapter.DirectionalUnchangedWords(result.Bytes, Position, Frame));
    }

    [Fact]
    public void EqualAxisCoreRetainsPackedWordsWhileTransitionIsTransported()
    {
        var result = Source2CompiledModelAdapter.CalculateDirectionalWords(Bytes([new(1, 1, 1), new(6, 1, 1)]), Position, Frame, 2, Math(uniform: true));
        Assert.DoesNotContain(0, result.ChangedFrames); Assert.Contains(1, result.ChangedFrames);
        Assert.Equal(Packed, BinaryPrimitives.ReadUInt32LittleEndian(result.Bytes.AsSpan(12)));
    }

    [Fact]
    public void CertifiedFieldStillRejectsAQuantizedCollapsedTriangle()
    {
        const float pivot = 1048576;
        Point3[] points = [new(pivot, 0, 0), new(pivot + .125f, 0, 0), new(pivot, 1, 0)];
        var math = new DirectionalEllipsoidScale(new(pivot, 0, 0), new(10, 10, 10), .4f, new(.5f, 1, 1), 64);
        var result = Source2CompiledModelAdapter.CalculateDirectionalWords(Bytes(points), Position, Frame, points.Length, math);
        Assert.Equal(result.Points[0], result.Points[1]);
        Assert.Throws<ArgumentException>(() => RegionTriangleGuard.Validate(points, result.Points, [0, 1, 2]));
    }

    [Theory]
    [InlineData(23, 12)]
    [InlineData(24, 4)]
    [InlineData(25, 12)]
    public void MalformedOrOverlappingStorageRejectsBeforeChangingInput(int length, int offset)
    {
        var bytes = new byte[length]; var source = bytes.ToArray();
        Assert.Throws<ArgumentException>(() => Source2CompiledModelAdapter.CalculateDirectionalWords(bytes, Position, Frame with { Offset = offset }, 1, Math()));
        Assert.Equal(source, bytes);
    }

    [Fact]
    public void ExcessDisplacementRejectsTheCompleteBuffer()
    {
        var bytes = Bytes([new(2, 2, 0)]); var source = bytes.ToArray();
        Assert.Equal("TRANSFORM_DISPLACEMENT_EXCEEDED", Assert.Throws<S2ModKitException>(() =>
            Source2CompiledModelAdapter.CalculateDirectionalWords(bytes, Position, Frame, 1, Math(cap: .01f))).Error.Code);
        Assert.Equal(source, bytes);
    }

    [Fact]
    public void OverlappingVertexAssertionsDeduplicateAndBindUnchangedPositionAndFrameWords()
    {
        var selected = Buffer(0, true, [new(1, 1, 0), new(10, 0, -0f)], transform: true);
        var excluded = Buffer(1, false, [new(12, 0, 0)]);
        var intent = new DirectionalProtection("keep_fixed", 1, [Assertion("first", selected, [1]), Assertion("second", selected, [1])]);
        var result = Source2CompiledModelAdapter.ResolveDirectionalProtection(intent, [selected, excluded], Hash, ["root"]);
        Assert.Equal(2, result.Assertions.Count); Assert.Equal(2, result.Union.Count); Assert.Equal([1], result.Union[0].VertexIndices); Assert.Empty(result.Union[1].VertexIndices);
        Assert.Equal(result.Union[0].SourcePositionHash, result.Union[0].ExpectedPositionHash);
        Assert.Equal(result.Union[0].SourcePackedFrameHash, result.Union[0].ExpectedPackedFrameHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProtectionRejectsMovedPositionsAndFixedPivotWithChangedCoreFrame(bool fixedPivot)
    {
        var selected = Buffer(0, true, [fixedPivot ? new(0, 0, 0) : new(1, 1, 0), new(10, 0, 0)], transform: true);
        var intent = new DirectionalProtection("keep_fixed", 1, [Assertion("fixed", selected, [0])]);
        var e = Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.ResolveDirectionalProtection(intent, [selected], Hash, ["root"]));
        Assert.Equal("DIRECTIONAL_PROTECTED_WORD_CHANGED", e.Error.Code); Assert.Contains("vertex 0", e.Error.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void OutOfRangeProtectedIndicesFailClosed(int index)
    {
        var selected = Buffer(0, true, [new(10, 0, 0), new(11, 0, 0)]);
        Assert.Equal("DIRECTIONAL_PROTECTION_INVALID", Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.ResolveDirectionalProtection(
            new("keep_fixed", 1, [Assertion("range", selected, [index])]), [selected], Hash, ["root"])).Error.Code);
    }

    [Fact]
    public void RootBoneClosureIncludesExcludedAndUnindexedTinyWeightContributorsAndExplicitZeroRows()
    {
        // The read-side metadata reader supplies every nonzero influence, regardless of its weight or index use.
        var selected = Buffer(0, true, [new(10, 0, 0), new(11, 0, 0)], contributors: [1]);
        var excluded = Buffer(1, false, [new(12, 0, 0)], contributors: [0]);
        var empty = Buffer(2, false, [new(13, 0, 0)]);
        var sets = new[] { Protected(selected, [1]), Protected(excluded, [0]), Protected(empty, []) };
        var bone = new DirectionalBoneAssertion
        {
            AssertionId = "root-bone",
            Version = 1,
            BoneIndex = 0,
            BoneName = "root",
            RootSkeletonHash = Hash,
            Lods = [new(0, DirectionalContractValidator.ContributorSetHash(sets), 2)]
        };
        var result = Source2CompiledModelAdapter.ResolveDirectionalProtection(new("keep_fixed", 1, [bone]), [selected, excluded, empty], Hash, ["root"]);
        Assert.Equal(3, result.Assertions[0].Sets.Count); Assert.Equal(2, result.Assertions[0].Sets.Sum(s => s.VertexCount));
        Assert.Equal(1, result.Union[1].VertexCount); Assert.Empty(result.Union[2].VertexIndices);
        Assert.Equal("DIRECTIONAL_PROTECTION_DRIFT", Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.ResolveDirectionalProtection(
            new("keep_fixed", 1, [bone with { Lods = [bone.Lods[0] with { ContributorCount = 1 }] }]), [selected, excluded, empty], Hash, ["root"])).Error.Code);
        Assert.Equal("DIRECTIONAL_PROTECTION_DRIFT", Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.ResolveDirectionalProtection(
            new("keep_fixed", 1, [bone with { RootSkeletonHash = ContentHash.Compute("drift"u8) }]), [selected, excluded, empty], Hash, ["root"])).Error.Code);
    }

    [Fact]
    public void NonemptyUncharacterizedFrameProtectionRejectsButOpaqueExcludedContextIsInventoried()
    {
        var selected = Buffer(0, true, [new(10, 0, 0)]);
        var excluded = Buffer(1, false, [new(12, 0, 0)], contributors: [0]) with { Frame = null };
        Assert.Equal(2, Assert.Single(Source2CompiledModelAdapter.DirectionalCoincidences([selected, excluded])).RecordCount);
        var bone = new DirectionalBoneAssertion { AssertionId = "opaque", Version = 1, BoneName = "root", BoneIndex = 0, RootSkeletonHash = Hash, Lods = [] };
        Assert.Equal("DIRECTIONAL_PROTECTION_INVALID", Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.ResolveDirectionalProtection(
            new("keep_fixed", 1, [bone]), [selected, excluded], Hash, ["root"])).Error.Code);
    }

    [Fact]
    public void CoincidenceGroupsRetainIndependentRecordsAndNormalizeOnlySignedZero()
    {
        var first = Buffer(0, true, [new(1, 1, -0f), new(1, 1, 0f)], transform: true);
        var second = Buffer(1, true, [new(1, 1, 0f)], transform: true);
        var result = Assert.Single(Source2CompiledModelAdapter.DirectionalCoincidences([first, second]));
        Assert.Equal(3, result.RecordCount); Assert.Equal(3, result.PairCount); Assert.Equal(3, result.MovingSelectedRecordCount);
        Assert.Equal(result, Assert.Single(Source2CompiledModelAdapter.DirectionalCoincidences([second, first])));
        var excludedPinned = Buffer(2, false, [new(10, 0, -0f)]);
        var selectedPinned = Buffer(3, true, [new(10, 0, 0f)]);
        Assert.Equal(1, Assert.Single(Source2CompiledModelAdapter.DirectionalCoincidences([excludedPinned, selectedPinned])).PairCount);
    }

    [Fact]
    public void MovingExcludedCoincidentMateRejectsAfterRestoringBinary32Precision()
    {
        var first = Buffer(0, true, [new(1.1f, 1, -0f)], transform: true);
        var second = Buffer(1, false, [new((float)1.100000023841858, 1, 0f)]);
        Assert.Equal("DIRECTIONAL_EXCLUDED_COINCIDENT_MATE", Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.DirectionalCoincidences([first, second])).Error.Code);
        var near = Buffer(1, false, [new(float.BitIncrement(1.1f), 1, 0f)]);
        Assert.Equal(0, Assert.Single(Source2CompiledModelAdapter.DirectionalCoincidences([first, near])).PairCount);
    }

    private static DirectionalEllipsoidScale Math(bool uniform = false, float cap = 64) => new(default, new(10, 10, 10), .4f, uniform ? new(1.5f, 1.5f, 1.5f) : new(1.5f, 1.25f, 1), cap);
    private static byte[] Bytes(Point3[] points)
    {
        var bytes = Enumerable.Repeat((byte)0xa5, points.Length * 24).ToArray();
        for (var i = 0; i < points.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 24), points[i].X); BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 24 + 4), points[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 24 + 8), points[i].Z); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 24 + 12), Packed);
        }
        return bytes;
    }
    private static Source2CompiledModelAdapter.DirectionalSourceBuffer Buffer(int mesh, bool selected, Point3[] points, bool transform = false, int[]? contributors = null)
    {
        var before = Bytes(points); var calc = transform ? Source2CompiledModelAdapter.CalculateDirectionalWords(before, Position, Frame, points.Length, Math()) : null;
        var all = Enumerable.Range(0, points.Length).ToArray();
        var facts = new DirectionalContextBuffer(0, mesh, 0, mesh * 3, mesh * 3 + 1, mesh * 3 + 2, points.Length, "models/test.vmdl_c", selected, 1, 1,
            ContentHash.Compute(before), Source2CompiledModelAdapter.DirectionalPositionWords(before, Position, all), Source2PackedFrameCodec.HashSelected(before, Frame, all), Hash, Hash, Hash);
        return new(facts, selected ? $"member-{mesh}" : null, Position, Frame, before, calc?.Bytes ?? before, points, calc?.Points ?? points,
            contributors is null ? new Dictionary<int, int[]>() : new Dictionary<int, int[]> { [0] = contributors });
    }
    private static DirectionalVertexAssertion Assertion(string id, Source2CompiledModelAdapter.DirectionalSourceBuffer buffer, int[] indices) => new()
    {
        Version = 1,
        AssertionId = id,
        Sets = [new(buffer.MemberId!, 0, buffer.Facts.DecodedBufferHash, indices, DirectionalContractValidator.VertexSetHash(indices), indices.Length)],
    };
    private static DirectionalProtectedSet Protected(Source2CompiledModelAdapter.DirectionalSourceBuffer b, int[] indices) => new(0, b.Facts.MeshOrdinal, 0,
        b.Facts.DecodedBufferHash, indices, DirectionalContractValidator.VertexSetHash(indices), indices.Length,
        Source2CompiledModelAdapter.DirectionalPositionWords(b.Before, Position, indices), Source2CompiledModelAdapter.DirectionalPositionWords(b.After, Position, indices),
        Source2PackedFrameCodec.HashSelected(b.Before, Frame, indices), Source2PackedFrameCodec.HashSelected(b.After, Frame, indices));
}
