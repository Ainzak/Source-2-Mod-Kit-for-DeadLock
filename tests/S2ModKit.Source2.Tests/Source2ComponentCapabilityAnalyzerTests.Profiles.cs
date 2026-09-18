using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;
using ValveResourceFormat;

namespace S2ModKit.Source2.Tests;

public sealed partial class Source2ComponentCapabilityAnalyzerTests
{
    [Fact]
    public void AssessSelectionReportsAvailablePackedWeightedProfile()
    {
        var mesh = PackedApolloLikeMesh();
        var profile = new ComponentCapabilityProfile([0], ResourcePath, [mesh]);
        var selection = Selection(
            "cmp_sword",
            Sel(0, 1, mesh.DrawCalls[0].Id, blockIndex: 10, ordinal: 0),
            Sel(0, 1, mesh.DrawCalls[1].Id, blockIndex: 10, ordinal: 1, start: 3));

        var result = Assess(profile, selection);

        Assert.Equal("transform_component", result.OperationKind);
        Assert.Equal(1, result.OperationVersion);
        Assert.Equal(ComponentDiscoveryContract.Available, result.Availability);
        var reason = Assert.Single(result.Reasons);
        Assert.Equal("COMPONENT_CAPABILITY_AVAILABLE", reason.Code);
        var fact = Assert.Single(result.GeometryByLod);
        Assert.Equal(0, fact.Lod);
        Assert.Equal(3, fact.SelectedVertexCount);
        Assert.Equal(new ContentHash(VertexSetHash.Compute([0, 1, 2])), fact.VertexSetHash);
        Assert.True(fact.ExclusivelyOwned);
    }

    [Theory]
    [InlineData(ComponentDiscoveryV2Contract.MeshLineageKind)]
    [InlineData(ComponentDiscoveryV2Contract.CandidateUnionKind)]
    public void AssessSelectionAcceptsPreciseAndUnionCandidateKinds(string candidateKind)
    {
        var mesh = AvailableMesh(0, 0, 10, 2);
        var selection = Selection("cmp_precise", Sel(0, 0, mesh.DrawCalls[0].Id, blockIndex: 10)) with
        {
            CandidateKind = candidateKind,
        };

        var result = Assess(new ComponentCapabilityProfile([0], ResourcePath, [mesh]), selection);

        Assert.Equal(ComponentDiscoveryContract.Available, result.Availability);
        Assert.Equal(2, Assert.Single(result.GeometryByLod).SelectedVertexCount);
    }

    [Fact]
    public void AssessSelectionRecomputesAvailableExactMultiMaterialUnion()
    {
        const string gemstoneMaterial = "materials/gem.vmat_c";
        var original = PackedApolloLikeMesh();
        var first = original.DrawCalls[0];
        var second = DrawCallSnapshot.Create(ResourcePath, 0, 1, 1, gemstoneMaterial, 3, 3);
        var sourceGeometry = original.GeometryAnalysis!;
        var geometryCalls = new[]
        {
            sourceGeometry.DrawCalls[0],
            sourceGeometry.DrawCalls[1] with
            {
                Snapshot = sourceGeometry.DrawCalls[1].Snapshot with { DrawCallId = second.Id },
            },
        };
        var geometry = sourceGeometry with
        {
            Snapshot = sourceGeometry.Snapshot with
            {
                DrawCalls = geometryCalls.Select(item => item.Snapshot).ToArray(),
            },
            DrawCalls = geometryCalls,
        };
        var mesh = original with
        {
            DrawCalls = [first, second],
            GeometryAnalysis = geometry,
        };
        var selection = new ComponentCapabilitySelection(
            "cmp_union",
            ComponentDiscoveryContract.MaterialGroupKind,
            [gemstoneMaterial, MaterialPath],
            [
                Sel(0, 1, first.Id, blockIndex: 10, ordinal: 0),
                Sel(0, 1, second.Id, gemstoneMaterial, blockIndex: 10, ordinal: 1, start: 3),
            ]);

        var result = Assess(new ComponentCapabilityProfile([0], ResourcePath, [mesh]), selection);

        Assert.Equal(ComponentDiscoveryContract.Available, result.Availability);
        Assert.Equal(3, Assert.Single(result.GeometryByLod).SelectedVertexCount);

        var incompleteMaterials = selection with { MaterialPaths = [MaterialPath] };
        AssertAmbiguous(new ComponentCapabilityProfile([0], ResourcePath, [mesh]), incompleteMaterials);
        var unsortedMaterials = selection with { MaterialPaths = [MaterialPath, gemstoneMaterial] };
        AssertAmbiguous(new ComponentCapabilityProfile([0], ResourcePath, [mesh]), unsortedMaterials);
    }

    [Fact]
    public void AssessSelectionReportsAvailableRigidSingleInfluenceProfile()
    {
        var mesh = RigidPocketLikeMesh();
        var profile = new ComponentCapabilityProfile([0], ResourcePath, [mesh]);
        var selection = Selection("cmp_case", Sel(0, 3, mesh.DrawCalls[0].Id, blockIndex: 10));

        var result = Assess(profile, selection);

        Assert.Equal(ComponentDiscoveryContract.Available, result.Availability);
        var fact = Assert.Single(result.GeometryByLod);
        Assert.Equal(3, fact.SelectedVertexCount);
        Assert.Equal(new ContentHash(VertexSetHash.Compute([0, 1, 2])), fact.VertexSetHash);
        Assert.True(fact.ExclusivelyOwned);
    }

    [Fact]
    public void AssessSelectionsIsOrderIndependentWithAscendingFacts()
    {
        foreach (var lodCount in new[] { 3, 4 })
        {
            var meshes = new List<ComponentProfileMesh>();
            var selectedCalls = new List<SelectedDrawCall>();
            var expectedFacts = new List<ComponentGeometryLodFacts>();
            for (var lod = 0; lod < lodCount; lod++)
            {
                var mesh = AvailableMesh(lod, lod, 10 + lod, 2);
                meshes.Add(mesh);
                selectedCalls.Add(Sel(lod, lod, mesh.DrawCalls[0].Id, blockIndex: 10 + lod));
                expectedFacts.Add(new ComponentGeometryLodFacts(lod, 2, new ContentHash(VertexSetHash.Compute([0, 1])), true));
            }

            var profile = new ComponentCapabilityProfile(Enumerable.Range(0, lodCount).ToArray(), ResourcePath, meshes);
            var forward = AssessSelections(profile, Selection("cmp_forward", selectedCalls.ToArray()));
            var reverse = AssessSelections(profile, Selection("cmp_forward", selectedCalls.AsEnumerable().Reverse().ToArray()));

            foreach (var result in new[] { forward, reverse }.Select(Assert.Single))
            {
                Assert.Equal(ComponentDiscoveryContract.Available, result.Availability);
                Assert.Equal(expectedFacts, result.GeometryByLod);
            }
        }
    }

    [Fact]
    public void AssessSelectionsBlocksWhenCodecIsUnavailable()
    {
        var mesh = UnavailableMesh(0, 1, 10);
        var profile = new ComponentCapabilityProfile([0], ResourcePath, [mesh]);
        var selection = Selection("cmp_case", Sel(0, 1, mesh.DrawCalls[0].Id, blockIndex: 10));

        var result = Assess(profile, selection);

        Assert.Equal(ComponentDiscoveryContract.Blocked, result.Availability);
        var reason = Assert.Single(result.Reasons);
        Assert.Equal("MESHOPTIMIZER_CAPABILITY_UNAVAILABLE", reason.Code);
        Assert.Empty(result.GeometryByLod);
    }

    [Fact]
    public void AssessSelectionReportsSharedVertexOwnershipForPartialMesh()
    {
        var positions = new[] { (0f, 0f, 0f), (1f, 0f, 0f), (2f, 0f, 0f) };
        var blend = Blended(3, [1, 0, 0, 0], [255, 0, 0, 0]);
        var firstVertices = new[] { 0, 1 };
        var secondVertices = new[] { 1, 2 };
        var mesh = ReadyMesh(
            0,
            1,
            10,
            28,
            positions,
            decorate: vertexBytes => WritePackedBlend(vertexBytes, blend),
            VertexDescriptor(),
            WeightedMeshData(Bounds((0f, 0f, 0f), (2f, 0f, 0f)), SingleWeaponBone(2f)),
            (0L, 3L, firstVertices), (3L, 3L, secondVertices));
        var profile = new ComponentCapabilityProfile([0], ResourcePath, [mesh]);

        var result = Assess(profile, Selection("cmp_half", Sel(0, 1, mesh.DrawCalls[0].Id, blockIndex: 10)));

        Assert.Equal(ComponentDiscoveryContract.Unsupported, result.Availability);
        var reason = Assert.Single(result.Reasons);
        Assert.Equal("TRANSFORM_SHARED_VERTEX_OWNERSHIP", reason.Code);
    }

    [Fact]
    public void OwnershipComparisonKeepsVertexBufferOrdinalsDistinct()
    {
        var mesh = ExclusiveTwoDrawCallProfile();
        var source = mesh.GeometryAnalysis!;
        var firstBuffer = source.VertexBuffers[0];
        var secondSnapshot = firstBuffer.Snapshot with { Ordinal = 1 };
        var secondBuffer = new Source2VertexBufferAnalysis(secondSnapshot, (byte[])firstBuffer.Decoded.Clone());
        var firstCall = source.DrawCalls[0];
        var secondCall = source.DrawCalls[1];
        var firstCallSnapshot = firstCall.Snapshot with { VertexBufferOrdinal = 0 };
        var secondCallSnapshot = secondCall.Snapshot with { VertexBufferOrdinal = 1 };
        var separated = new Source2GeometryAnalysis(
            source.Snapshot with
            {
                VertexBuffers = [firstBuffer.Snapshot, secondSnapshot],
                DrawCalls = [firstCallSnapshot, secondCallSnapshot],
            },
            [firstBuffer, secondBuffer],
            source.IndexBuffers,
            [
                firstCall with { Snapshot = firstCallSnapshot },
                secondCall with { Snapshot = secondCallSnapshot },
            ]);
        var profile = new ComponentCapabilityProfile(
            [0],
            ResourcePath,
            [mesh with { GeometryAnalysis = separated }]);

        var result = Assess(
            profile,
            Selection("cmp_buffer_zero", Sel(0, 1, mesh.DrawCalls[0].Id, blockIndex: 10)));

        Assert.Equal(ComponentDiscoveryContract.Unsupported, result.Availability);
        Assert.Equal("TRANSFORM_SOURCE2_PROFILE_UNSUPPORTED", Assert.Single(result.Reasons).Code);
    }

}
