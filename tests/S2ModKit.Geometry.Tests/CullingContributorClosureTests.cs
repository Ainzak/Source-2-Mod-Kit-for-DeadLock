using System.Text.Json;

namespace S2ModKit.Geometry.Tests;

public sealed class CullingContributorClosureTests
{
    private static readonly CullingVertexId A = new(0, 0, 0);
    private static readonly CullingVertexId B = new(0, 0, 1);
    private static readonly CullingVertexId C = new(0, 1, 0);
    private static readonly CullingVertexId D = new(1, 0, 0);
    private static readonly string[] ExpectedAffectedFields = ["bone0", "root", "scene0"];

    [Fact]
    public void RootIncludesUnchangedMeshesBuffersAndUnindexedBoneContributors()
    {
        var fields = new CullingContributorField[]
        {
            new("scene0", [A, B], []),
            // C represents an unindexed, but influenced, decoded vertex.
            new("bone0", [A, B, C], []),
            new("bone1", [D], []),
            new("root", [], ["bone0", "bone1"]),
            new("scene1", [D], [])
        };
        var closure = CullingContributorClosure.Resolve(fields, [A]);
        Assert.Equal(ExpectedAffectedFields, closure.Select(field => field.FieldId));
        Assert.Equal(new[] { A }, closure.Single(field => field.FieldId == "root").ChangedContributors);
        Assert.Equal(new[] { B, C, D }, closure.Single(field => field.FieldId == "root").UnchangedContributors);
        Assert.Equal(new[] { B }, closure.Single(field => field.FieldId == "scene0").UnchangedContributors);
    }

    [Fact]
    public void MultipleInfluencesAndDiamondEdgesDeduplicateVerticesNotFields()
    {
        var closure = CullingContributorClosure.Resolve([
            new("left", [A, A, B], []),
            new("right", [A, C], []),
            new("union", [D], ["left", "right"]),
            new("root", [], ["left", "union"])
        ], [C, A]);
        Assert.Equal(4, closure.Length);
        Assert.Equal(new[] { A, C }, closure.Single(field => field.FieldId == "root").ChangedContributors);
        Assert.Equal(new[] { B, D }, closure.Single(field => field.FieldId == "root").UnchangedContributors);
    }

    [Fact]
    public void SameVertexIndexInDifferentBuffersAndMeshesDoesNotAlias()
    {
        var closure = CullingContributorClosure.Resolve([
            new("a", [A], []), new("c", [C], []), new("d", [D], [])
        ], [A]);
        Assert.Equal("a", Assert.Single(closure).FieldId);
    }

    [Fact]
    public void InputOrderingCannotChangeResultAndInputsAreNotModified()
    {
        var direct = new List<CullingVertexId> { D, C, B, A };
        var fields = new List<CullingContributorField>
        {
            new("z", direct, []), new("a", [], ["z"])
        };
        var before = JsonSerializer.Serialize(fields);
        var result = JsonSerializer.Serialize(CullingContributorClosure.Resolve(fields, [C, A]));
        Assert.Equal(before, JsonSerializer.Serialize(fields));
        Assert.Equal(result, JsonSerializer.Serialize(CullingContributorClosure.Resolve([
            new("a", [], ["z"]), new("z", [A, B, C, D], [])
        ], [A, C])));
    }

    [Fact]
    public void EmptyCompleteGraphAndNoChangesAreValid()
    {
        Assert.Empty(CullingContributorClosure.Resolve([], []));
        Assert.Empty(CullingContributorClosure.Resolve([new("empty", [], [])], []));
        Assert.Empty(CullingContributorClosure.Resolve([new("nonempty", [A], [])], []));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingCollectionsRejectEvenInUnrelatedFields(bool missingDirect)
    {
        Assert.Throws<ArgumentException>(() => CullingContributorClosure.Resolve([
            new("selected", [A], []),
            new("unrelated", missingDirect ? null : [], missingDirect ? [] : null)
        ], [A]));
    }

    [Fact]
    public void UnknownChangedVertexAndDuplicateChangedIdentityReject()
    {
        Assert.Throws<ArgumentException>(() => CullingContributorClosure.Resolve([new("a", [A], [])], [B]));
        Assert.Throws<ArgumentException>(() => CullingContributorClosure.Resolve([new("a", [A], [])], [A, A]));
    }

    [Fact]
    public void UnknownAndDuplicateDependenciesReject()
    {
        Assert.Throws<ArgumentException>(() => CullingContributorClosure.Resolve([new("a", [], ["missing"])], []));
        Assert.Throws<ArgumentException>(() => CullingContributorClosure.Resolve([
            new("a", [A], []), new("root", [], ["a", "a"])
        ], [A]));
    }

    [Fact]
    public void DuplicateAndEmptyFieldIdentifiersReject()
    {
        Assert.Throws<ArgumentException>(() => CullingContributorClosure.Resolve([
            new("a", [A], []), new("a", [B], [])
        ], [A]));
        Assert.Throws<ArgumentException>(() => CullingContributorClosure.Resolve([new(" ", [], [])], []));
        Assert.Throws<ArgumentException>(() => CullingContributorClosure.Resolve([new("a", [], [""])], []));
    }

    [Fact]
    public void CyclesRejectIncludingUnrelatedCycles()
    {
        Assert.Throws<ArgumentException>(() => CullingContributorClosure.Resolve([new("a", [A], ["a"])], [A]));
        Assert.Throws<ArgumentException>(() => CullingContributorClosure.Resolve([
            new("selected", [A], []), new("b", [], ["c"]), new("c", [], ["b"])
        ], [A]));
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, -1)]
    public void NegativeOrdinalsReject(int mesh, int buffer, int vertex)
    {
        var invalid = new CullingVertexId(mesh, buffer, vertex);
        Assert.Throws<ArgumentException>(() => CullingContributorClosure.Resolve([new("a", [invalid], [])], []));
        Assert.Throws<ArgumentException>(() => CullingContributorClosure.Resolve([], [invalid]));
    }

    [Fact]
    public void NullInputsReject()
    {
        Assert.Throws<ArgumentNullException>(() => CullingContributorClosure.Resolve(null!, []));
        Assert.Throws<ArgumentNullException>(() => CullingContributorClosure.Resolve([], null!));
        Assert.Throws<ArgumentException>(() => CullingContributorClosure.Resolve([null!], []));
    }

    [Fact]
    public void LongDependencyChainDoesNotUseRecursiveTraversal()
    {
        var fields = Enumerable.Range(0, 10_000).Select(index => new CullingContributorField(
            index.ToString(System.Globalization.CultureInfo.InvariantCulture), index == 0 ? [A] : [],
            index == 0 ? [] : [(index - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)])).ToArray();
        var closure = CullingContributorClosure.Resolve(fields, [A]);
        Assert.Equal(10_000, closure.Length);
        Assert.All(closure, field => Assert.Equal(new[] { A }, field.ChangedContributors));
    }

    [Fact]
    public void GeneratedDagClosureMatchesIndependentReachabilityOracle()
    {
        var random = new Random(2829);
        for (var sample = 0; sample < 200; sample++)
        {
            var fields = new List<CullingContributorField>();
            for (var index = 0; index < 20; index++)
            {
                var direct = Enumerable.Range(0, 6).Where(_ => random.Next(3) == 0)
                    .Select(vertex => new CullingVertexId(index % 3, index % 2, vertex)).ToArray();
                var children = Enumerable.Range(0, index).Where(_ => random.Next(5) == 0)
                    .Select(child => $"f{child}").ToArray();
                fields.Add(new($"f{index}", direct, children));
            }

            var all = fields.SelectMany(field => field.DirectContributors!).Distinct().ToArray();
            var changed = all.Where(_ => random.Next(4) == 0).ToArray();
            var actual = CullingContributorClosure.Resolve(fields.OrderBy(_ => random.Next()).ToArray(), changed);
            var expected = new Dictionary<string, HashSet<CullingVertexId>>(StringComparer.Ordinal);
            foreach (var start in fields)
            {
                // Oracle walks reachable nodes, never uses production's child-first unions.
                var visited = new HashSet<string>();
                var vertices = new HashSet<CullingVertexId>();
                var stack = new Stack<string>();
                stack.Push(start.Id);
                while (stack.TryPop(out var id))
                {
                    if (!visited.Add(id)) continue;
                    var field = fields.Single(item => item.Id == id);
                    vertices.UnionWith(field.DirectContributors!);
                    foreach (var child in field.ContributingFields!) stack.Push(child);
                }

                if (vertices.Overlaps(changed)) expected.Add(start.Id, vertices);
            }

            Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Select(field => field.FieldId));
            foreach (var field in actual)
            {
                Assert.True(expected[field.FieldId].SetEquals(field.ChangedContributors.Concat(field.UnchangedContributors)));
                Assert.True(field.ChangedContributors.ToHashSet().SetEquals(expected[field.FieldId].Intersect(changed)));
                Assert.True(field.UnchangedContributors.ToHashSet().SetEquals(expected[field.FieldId].Except(changed)));
            }
        }
    }
}
