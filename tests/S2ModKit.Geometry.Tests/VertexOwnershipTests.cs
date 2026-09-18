using System.Buffers.Binary;
using System.Security.Cryptography;
using S2ModKit.Geometry;

namespace S2ModKit.Geometry.Tests;

public sealed class VertexOwnershipAnalyzerTests
{
    [Fact]
    public void AnalyzeRejectsNullArguments()
    {
        var drawCalls = new[] { new DrawCallRange("dc_a", 0, 3, 0) };

        Assert.Throws<ArgumentNullException>(
            () => VertexOwnershipAnalyzer.Analyze(null!, 3, drawCalls, ["dc_a"]));
        Assert.Throws<ArgumentNullException>(
            () => VertexOwnershipAnalyzer.Analyze([0u, 1u, 2u], 3, null!, ["dc_a"]));
        Assert.Throws<ArgumentNullException>(
            () => VertexOwnershipAnalyzer.Analyze([0u, 1u, 2u], 3, drawCalls, null!));
    }

    [Fact]
    public void AnalyzeRejectsEmptySelectionAndNegativeVertexCount()
    {
        var drawCalls = new[] { new DrawCallRange("dc_a", 0, 3, 0) };

        Assert.Throws<ArgumentException>(
            () => VertexOwnershipAnalyzer.Analyze([0u, 1u, 2u], 3, drawCalls, []));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VertexOwnershipAnalyzer.Analyze([0u, 1u, 2u], -1, drawCalls, ["dc_a"]));
    }

    [Fact]
    public void AnalyzeRejectsMissingSelectedIdentifier()
    {
        var drawCalls = new[] { new DrawCallRange("dc_a", 0, 3, 0) };

        Assert.Throws<ArgumentException>(
            () => VertexOwnershipAnalyzer.Analyze([0u, 1u, 2u], 3, drawCalls, ["dc_missing"]));

        Assert.Throws<ArgumentException>(
            () => VertexOwnershipAnalyzer.Analyze([0u, 1u, 2u], 3, [], ["dc_a"]));
    }

    [Fact]
    public void AnalyzeRejectsDuplicatedIdentifiers()
    {
        var duplicatedRanges = new[]
        {
            new DrawCallRange("dc_a", 0, 3, 0),
            new DrawCallRange("dc_a", 3, 3, 0),
        };

        Assert.Throws<ArgumentException>(
            () => VertexOwnershipAnalyzer.Analyze([0u, 1u, 2u, 3u, 4u, 5u], 6, duplicatedRanges, ["dc_a"]));

        var ranges = new[] { new DrawCallRange("dc_a", 0, 3, 0), new DrawCallRange("dc_b", 3, 3, 0) };

        Assert.Throws<ArgumentException>(
            () => VertexOwnershipAnalyzer.Analyze([0u, 1u, 2u, 3u, 4u, 5u], 6, ranges, ["dc_a", "dc_a"]));

        Assert.Throws<ArgumentException>(
            () => VertexOwnershipAnalyzer.Analyze([0u, 1u, 2u], 3, [new DrawCallRange("  ", 0, 3, 0)], ["  "]));
    }

    [Fact]
    public void AnalyzeRejectsInvalidIndexRanges()
    {
        var indices = new uint[] { 0, 1, 2, 3, 4, 5 };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => VertexOwnershipAnalyzer.Analyze(indices, 6, [new DrawCallRange("dc_a", -1, 3, 0)], ["dc_a"]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VertexOwnershipAnalyzer.Analyze(indices, 6, [new DrawCallRange("dc_a", 0, -3, 0)], ["dc_a"]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VertexOwnershipAnalyzer.Analyze(indices, 6, [new DrawCallRange("dc_a", 4, 3, 0)], ["dc_a"]));
        Assert.Throws<OverflowException>(
            () => VertexOwnershipAnalyzer.Analyze(
                indices,
                6,
                [new DrawCallRange("dc_a", long.MaxValue, long.MaxValue, 0)],
                ["dc_a"]));
    }

    [Fact]
    public void AnalyzeRejectsNegativeAndOutOfRangeBaseVertex()
    {
        var indices = new uint[] { 0, 1, 2 };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => VertexOwnershipAnalyzer.Analyze(indices, 3, [new DrawCallRange("dc_a", 0, 3, -1)], ["dc_a"]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VertexOwnershipAnalyzer.Analyze(
                indices,
                3,
                [new DrawCallRange("dc_a", 0, 3, int.MaxValue)],
                ["dc_a"]));
    }

    [Fact]
    public void AnalyzeRejectsDecodedVertexOutsideDeclaredCount()
    {
        var indices = new uint[] { 2, 3, 4 };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => VertexOwnershipAnalyzer.Analyze(indices, 4, [new DrawCallRange("dc_a", 0, 3, 0)], ["dc_a"]));
    }

    [Fact]
    public void AnalyzeReportsOneExclusiveSelectedDrawCall()
    {
        var indices = new uint[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        var drawCalls = new[]
        {
            new DrawCallRange("dc_selected", 0, 6, 0),
            new DrawCallRange("dc_other", 6, 3, 0),
        };

        var ownership = VertexOwnershipAnalyzer.Analyze(indices, 9, drawCalls, ["dc_selected"]);

        Assert.Equal(9, ownership.DeclaredVertexCount);
        Assert.Equal(9, ownership.DecodedIndexCount);
        Assert.Equal(1, ownership.SelectedDrawCallCount);
        Assert.Equal(1, ownership.NonSelectedDrawCallCount);
        Assert.Equal(6, ownership.SelectedIndexCount);
        Assert.Equal(3, ownership.NonSelectedIndexCount);
        Assert.Equal([0, 1, 2, 3, 4, 5], ownership.SelectedVertices);
        Assert.Equal([6, 7, 8], ownership.NonSelectedVertices);
        Assert.Empty(ownership.SharedWithNonSelectedVertices);
        Assert.Equal(0, ownership.SharedVertexCount);
    }

    [Fact]
    public void AnalyzeAllowsVerticesSharedAmongSelectedDrawCallsOnly()
    {
        var indices = new uint[] { 0, 1, 2, 3, 3, 4, 5 };
        var drawCalls = new[]
        {
            new DrawCallRange("dc_first", 0, 4, 0),
            new DrawCallRange("dc_second", 4, 3, 0),
        };

        var ownership = VertexOwnershipAnalyzer.Analyze(indices, 6, drawCalls, ["dc_first", "dc_second"]);

        Assert.Equal(2, ownership.SelectedDrawCallCount);
        Assert.Equal(0, ownership.NonSelectedDrawCallCount);
        Assert.Equal([0, 1, 2, 3, 4, 5], ownership.SelectedVertices);
        Assert.Empty(ownership.NonSelectedVertices);
        Assert.Empty(ownership.SharedWithNonSelectedVertices);
    }

    [Fact]
    public void AnalyzeIgnoresSelectedIdentifierInputOrder()
    {
        var indices = new uint[] { 0, 1, 2, 3, 2, 3, 4, 5 };
        var drawCalls = new[]
        {
            new DrawCallRange("dc_first", 0, 4, 0),
            new DrawCallRange("dc_second", 4, 4, 0),
        };

        var forward = VertexOwnershipAnalyzer.Analyze(indices, 6, drawCalls, ["dc_first", "dc_second"]);
        var reversed = VertexOwnershipAnalyzer.Analyze(indices, 6, drawCalls, ["dc_second", "dc_first"]);

        Assert.Equal(forward.SelectedVertices, reversed.SelectedVertices);
        Assert.Equal(forward.SharedWithNonSelectedVertices, reversed.SharedWithNonSelectedVertices);
        Assert.Equal(forward.SelectedIndexCount, reversed.SelectedIndexCount);
    }

    [Fact]
    public void AnalyzeReportsSharingWithNonSelectedDrawCalls()
    {
        var indices = new uint[] { 0, 1, 2, 3, 2, 3, 4, 5, 6, 7 };
        var drawCalls = new[]
        {
            new DrawCallRange("dc_selected_a", 0, 4, 0),
            new DrawCallRange("dc_selected_b", 4, 4, 0),
            new DrawCallRange("dc_other", 8, 2, 0),
        };

        var ownership = VertexOwnershipAnalyzer.Analyze(indices, 8, drawCalls, ["dc_selected_a", "dc_selected_b"]);

        Assert.Equal([0, 1, 2, 3, 4, 5], ownership.SelectedVertices);
        Assert.Equal([6, 7], ownership.NonSelectedVertices);
        Assert.Equal(6, ownership.SelectedVertexCount);
        Assert.Empty(ownership.SharedWithNonSelectedVertices);

        var overlapping = new uint[] { 0, 1, 2, 3, 2, 3, 4, 5, 4, 5 };
        var overlappingCalls = new[]
        {
            new DrawCallRange("dc_selected_a", 0, 4, 0),
            new DrawCallRange("dc_selected_b", 4, 4, 0),
            new DrawCallRange("dc_other", 8, 2, 0),
        };

        var shared = VertexOwnershipAnalyzer.Analyze(overlapping, 6, overlappingCalls, ["dc_selected_a", "dc_selected_b"]);

        Assert.Equal([0, 1, 2, 3, 4, 5], shared.SelectedVertices);
        Assert.Equal([4, 5], shared.SharedWithNonSelectedVertices);
        Assert.Equal(2, shared.SharedVertexCount);
    }

    [Fact]
    public void AnalyzeAppliesPositiveBaseVertexWithCheckedArithmetic()
    {
        var indices = new uint[] { 5, 6, 7 };

        var ownership = VertexOwnershipAnalyzer.Analyze(
            indices,
            20,
            [new DrawCallRange("dc_shifted", 0, 3, 10)],
            ["dc_shifted"]);

        Assert.Equal([15, 16, 17], ownership.SelectedVertices);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VertexOwnershipAnalyzer.Analyze(indices, 17, [new DrawCallRange("dc_shifted", 0, 3, 10)], ["dc_shifted"]));
    }

    [Fact]
    public void AnalyzeReturnsSortedDistinctStableNonAliasingOutput()
    {
        var indices = new uint[] { 7, 3, 9, 3, 7, 0, 9, 9 };
        var drawCalls = new[]
        {
            new DrawCallRange("dc_a", 0, 4, 0),
            new DrawCallRange("dc_b", 4, 4, 0),
            new DrawCallRange("dc_c", 0, 0, 0),
        };
        var indicesSnapshot = (uint[])indices.Clone();
        var drawCallsSnapshot = (DrawCallRange[])drawCalls.Clone();

        var first = VertexOwnershipAnalyzer.Analyze(indices, 10, drawCalls, ["dc_a", "dc_b"]);
        var second = VertexOwnershipAnalyzer.Analyze(indices, 10, drawCalls, ["dc_b", "dc_a"]);

        Assert.Equal([0, 3, 7, 9], first.SelectedVertices);
        Assert.Equal(first.SelectedVertices, second.SelectedVertices);
        Assert.Equal(first.SharedWithNonSelectedVertices, second.SharedWithNonSelectedVertices);
        Assert.Equal(first.SelectedIndexCount, second.SelectedIndexCount);

        Assert.Equal(indicesSnapshot, indices);
        Assert.Equal(drawCallsSnapshot, drawCalls);

        indices[0] = 0u;
        drawCalls[0] = drawCalls[0] with { Id = "mutated" };
        Assert.Equal([0, 3, 7, 9], first.SelectedVertices);
        Assert.Equal(8, first.SelectedIndexCount);
    }

    [Fact]
    public void AnalyzeHandlesEmptyIndexSequenceExplicitly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VertexOwnershipAnalyzer.Analyze([], 4, [new DrawCallRange("dc_a", 0, 1, 0)], ["dc_a"]));

        var ownership = VertexOwnershipAnalyzer.Analyze(
            [],
            0,
            [new DrawCallRange("dc_a", 0, 0, 0)],
            ["dc_a"]);

        Assert.Equal(0, ownership.DecodedIndexCount);
        Assert.Equal(1, ownership.SelectedDrawCallCount);
        Assert.Equal(0, ownership.SelectedVertexCount);
        Assert.Empty(ownership.SelectedVertices);
        Assert.Empty(ownership.SharedWithNonSelectedVertices);
    }

    [Fact]
    public void AnalyzeHandlesLargeIdentifierSetsDeterministically()
    {
        const int drawCallCount = 4_096;
        var drawCalls = Enumerable.Range(0, drawCallCount)
            .Select(index => new DrawCallRange($"dc_{index:D4}", 0, 0, 0))
            .ToArray();
        var selected = Enumerable.Range(0, drawCallCount)
            .Where(index => index % 2 == 0)
            .Select(index => $"dc_{index:D4}")
            .Reverse()
            .ToArray();

        var ownership = VertexOwnershipAnalyzer.Analyze([], 0, drawCalls, selected);

        Assert.Equal(drawCallCount / 2, ownership.SelectedDrawCallCount);
        Assert.Equal(drawCallCount / 2, ownership.NonSelectedDrawCallCount);
    }

    [Fact]
    public void OwnershipCanOnlyBeCreatedByTheAnalyzer()
    {
        Assert.Empty(typeof(VertexOwnership).GetConstructors());
    }
}

public sealed class VertexSetHashTests
{
    [Fact]
    public void ComputeIsStableAcrossOrderAndDuplicates()
    {
        var first = VertexSetHash.Compute([7, 3, 9, 3, 7]);
        var second = VertexSetHash.Compute([3, 7, 9]);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.Equal(first.ToLowerInvariant(), first);
    }

    [Fact]
    public void ComputeMatchesManualCanonicalSha256()
    {
        var payload = new byte[3 * sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), 5);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8, 4), 11);
        var expected = Convert.ToHexStringLower(SHA256.HashData(payload));

        Assert.Equal(expected, VertexSetHash.Compute([11, 2, 5]));
    }

    [Fact]
    public void ComputeDistinguishesDifferentSetsAndRejectsNull()
    {
        Assert.NotEqual(VertexSetHash.Compute([1, 2, 3]), VertexSetHash.Compute([1, 2, 4]));
        Assert.Equal(VertexSetHash.Compute([]), Convert.ToHexStringLower(SHA256.HashData([])));
        Assert.Throws<ArgumentNullException>(() => VertexSetHash.Compute(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => VertexSetHash.Compute([-1, 0, 1]));
    }

    [Fact]
    public void ComputeDoesNotMutateInput()
    {
        var vertices = new[] { 9, 1, 5 };
        var snapshot = (int[])vertices.Clone();

        VertexSetHash.Compute(vertices);

        Assert.Equal(snapshot, vertices);
    }
}
