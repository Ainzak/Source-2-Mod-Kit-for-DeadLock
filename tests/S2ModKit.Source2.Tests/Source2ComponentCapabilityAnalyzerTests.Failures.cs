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
    public void AssessSelectionsRejectsStructurallyUnsuitableSelections()
    {
        var exclusiveMesh = ExclusiveTwoDrawCallProfile();
        var exclusivePartial = Assess(
            new ComponentCapabilityProfile([0], ResourcePath, [exclusiveMesh]),
            Selection("cmp_half", Sel(0, 1, exclusiveMesh.DrawCalls[0].Id, blockIndex: 10)));
        Assert.Equal(ComponentDiscoveryContract.Unsupported, exclusivePartial.Availability);
        Assert.Equal("TRANSFORM_SOURCE2_PROFILE_UNSUPPORTED", Assert.Single(exclusivePartial.Reasons).Code);

        var firstMesh = AvailableMesh(0, 1, 10, 2);
        var secondMesh = AvailableMesh(0, 2, 20, 2);
        var twoMeshes = Assess(
            new ComponentCapabilityProfile([0], ResourcePath, [firstMesh, secondMesh]),
            Selection(
                "cmp_two",
                Sel(0, 1, firstMesh.DrawCalls[0].Id, blockIndex: 10),
                Sel(0, 2, secondMesh.DrawCalls[0].Id, blockIndex: 20)));
        Assert.Equal(ComponentDiscoveryContract.Unsupported, twoMeshes.Availability);
        Assert.Equal("TRANSFORM_SOURCE2_PROFILE_UNSUPPORTED", Assert.Single(twoMeshes.Reasons).Code);

        var lod0 = AvailableMesh(0, 1, 10, 2);
        var lod1 = AvailableMesh(1, 5, 30, 2);
        var incomplete = Assess(
            new ComponentCapabilityProfile([0, 1], ResourcePath, [lod0, lod1]),
            Selection("cmp_lod0", Sel(0, 1, lod0.DrawCalls[0].Id, blockIndex: 10)));
        Assert.Equal(ComponentDiscoveryContract.Unsupported, incomplete.Availability);
        Assert.Equal("COMPONENT_INCOMPLETE_LOD_COVERAGE", Assert.Single(incomplete.Reasons).Code);

        var lod1Drifted = AvailableMesh(1, 5, 30, 2, rootBoneName: "guard_root", tipBoneName: "guard_tip");
        var drifted = Assess(
            new ComponentCapabilityProfile([0, 1], ResourcePath, [AvailableMesh(0, 1, 10, 2), lod1Drifted]),
            Selection(
                "cmp_drift",
                Sel(0, 1, lod0.DrawCalls[0].Id, blockIndex: 10),
                Sel(1, 5, lod1Drifted.DrawCalls[0].Id, blockIndex: 30)));
        Assert.Equal(ComponentDiscoveryContract.Unsupported, drifted.Availability);
        Assert.Equal("TRANSFORM_SOURCE2_PROFILE_UNSUPPORTED", Assert.Single(drifted.Reasons).Code);
    }

    [Fact]
    public void AssessSelectionsNeverReportsAvailableForBrokenProfiles()
    {
        AssertGeometryUnavailable(WeightedMeshData(Bounds((0f, 0f, 0f), (1f, 0f, 0f)), SingleWeaponBone(1f)), blend =>
        {
            blend[0] = ([0, 0, 0, 0], [255, 0, 0, 0]);
            blend[1] = ([0, 0, 0, 0], [254, 0, 0, 0]);
        });
        AssertGeometryUnavailable(WeightedMeshData(Bounds((0f, 0f, 0f), (1f, 0f, 0f)), SingleWeaponBone(1f)), blend =>
        {
            blend[0] = ([1, 0, 0, 0], [255, 0, 0, 0]);
            blend[1] = ([1, 0, 0, 0], [255, 0, 0, 0]);
        }, positions: [(0f, 0f, 0f), (1f, 5f, 0f)]);
        AssertGeometryUnavailable(
            WeightedMeshData(Bounds((0f, 0f, 0f), (1f, 0f, 0f)), SingleWeaponBone(9f)),
            blend =>
            {
                blend[0] = ([1, 0, 0, 0], [255, 0, 0, 0]);
                blend[1] = ([1, 0, 0, 0], [255, 0, 0, 0]);
            });
        AssertGeometryUnavailable(WeightedMeshData(Bounds((0f, 0f, 0f), (2f, 0f, 0f)), SingleWeaponBone(2f)), blend =>
        {
            blend[0] = ([0, 0, 0, 0], [255, 0, 0, 0]);
            blend[1] = ([0, 0, 0, 0], [255, 0, 0, 0]);
            blend[2] = ([0, 0, 0, 0], [255, 0, 0, 0]);
        }, vertexCountOverride: 3, coveredVertices: [0, 1]);

        var unsupportedStatus = Assess(
            new ComponentCapabilityProfile(
                [0],
                ResourcePath,
                [Mesh(0, 1, 10, "unsupported", [Call(0, 1, 0, 0, 3)])]),
            Selection("cmp_all", Sel(0, 1, Call(0, 1, 0, 0, 3).Id)));
        Assert.Equal(ComponentDiscoveryContract.Unsupported, unsupportedStatus.Availability);
        Assert.Equal("TRANSFORM_GEOMETRY_UNAVAILABLE", Assert.Single(unsupportedStatus.Reasons).Code);
    }

    [Fact]
    public void AssessSelectionRejectsStaleMappingByIdentityOnly()
    {
        var mesh = AvailableMesh(0, 1, 10, 3);
        var profile = new ComponentCapabilityProfile([0], ResourcePath, [mesh]);
        var live = mesh.DrawCalls[0];

        AssertAmbiguous(profile, Sel(0, 1, "dc_unknown"));
        AssertAmbiguous(profile, Sel(0, 1, live.Id) with { IndexCount = live.IndexCount + 1 });
        AssertAmbiguous(profile, Sel(0, 1, live.Id) with { MaterialPath = "materials/other.vmat_c" });
        AssertAmbiguous(profile, Sel(0, 1, live.Id) with { ResourceBlockIndex = 99 });
        AssertAmbiguous(profile, Sel(0, 99, live.Id));
        AssertAmbiguous(profile, Sel(1, 1, live.Id));
        AssertAmbiguous(profile, Sel(0, 1, live.Id) with { ResourcePath = "models/test/other.vmdl_c" });
        AssertAmbiguous(profile, Sel(0, 1, live.Id) with { MaterialPath = string.Empty });
        AssertAmbiguous(profile, Sel(0, 1, live.Id) with { DrawCallOrdinal = live.DrawCallOrdinal + 7 });
        AssertAmbiguous(profile, new ComponentCapabilitySelection("cmp_x", "bone_group", [MaterialPath], [Sel(0, 1, live.Id)]));
        AssertAmbiguous(profile, new ComponentCapabilitySelection("cmp_x", ComponentDiscoveryContract.MaterialGroupKind, ["materials/part"], [Sel(0, 1, live.Id)]));
        AssertAmbiguous(profile, new ComponentCapabilitySelection("cmp_x", ComponentDiscoveryContract.MaterialGroupKind, [MaterialPath], []));
        AssertAmbiguous(profile with { ResourcePath = "models/test/other.vmdl_c" }, Sel(0, 1, live.Id));
    }

    [Fact]
    public void AssessSelectionsCoversEverySelectionExactlyOnceWithDistinctStatuses()
    {
        var availableMesh = AvailableMesh(0, 1, 10, 2);
        var blockedMesh = UnavailableMesh(0, 2, 20);
        var profile = new ComponentCapabilityProfile([0], ResourcePath, [availableMesh, blockedMesh]);
        var selections = new[]
        {
            Selection("cmp_z_blocked", Sel(0, 2, blockedMesh.DrawCalls[0].Id, blockIndex: 20)),
            Selection("cmp_a_available", Sel(0, 1, availableMesh.DrawCalls[0].Id, blockIndex: 10)),
        };

        var results = AssessSelections(profile, selections);

        Assert.Equal(2, results.Count);
        Assert.Equal(["cmp_a_available", "cmp_z_blocked"], results.Select(item => item.SelectionId).ToArray());
        Assert.Equal(ComponentDiscoveryContract.Available, results[0].Availability);
        Assert.Equal(ComponentDiscoveryContract.Blocked, results[1].Availability);
    }

    [Fact]
    public void AssessSelectionsFailsClosedOnDuplicateOrEmptySelectionIdentities()
    {
        var mesh = AvailableMesh(0, 1, 10, 2);
        var profile = new ComponentCapabilityProfile([0], ResourcePath, [mesh]);
        var selection = Selection("cmp_one", Sel(0, 1, mesh.DrawCalls[0].Id, blockIndex: 10));

        Assert.Throws<S2ModKitException>(() => AssessSelections(profile, selection, selection));
        Assert.Throws<S2ModKitException>(() => AssessSelections(profile, Selection(string.Empty, Sel(0, 1, mesh.DrawCalls[0].Id, blockIndex: 10))));
    }

    [Fact]
    public void AssessSelectionsHonorsCancellationAndPreservesCallerData()
    {
        var mesh = AvailableMesh(0, 1, 10, 2);
        var profile = new ComponentCapabilityProfile([0], ResourcePath, [mesh]);
        var calls = new List<SelectedDrawCall> { Sel(0, 1, mesh.DrawCalls[0].Id, blockIndex: 10) };
        var selections = new List<ComponentCapabilitySelection> { Selection("cmp_a", calls.ToArray()) };

        Assert.ThrowsAny<OperationCanceledException>(
            () => Source2CompiledModelAdapter.AssessSelections(selections, profile, new CancellationToken(canceled: true)));

        var results = Source2CompiledModelAdapter.AssessSelections(selections, profile, CancellationToken.None);
        Assert.Single(results);
        Assert.Equal(
            new[] { new SelectedDrawCall(0, ResourcePath, 1, 10, mesh.DrawCalls[0].Id, MaterialPath, 0, 0, 3) },
            calls);
        var preservedSelection = Assert.Single(selections);
        Assert.Equal("cmp_a", preservedSelection.SelectionId);
        Assert.Equal(calls.ToArray(), preservedSelection.SelectedDrawCalls.ToArray());
    }

    [Fact]
    public void PublicCapabilitySurfaceExposesNoNativeLibraryTypes()
    {
        var adapterType = typeof(Source2CompiledModelAdapter);
        Assert.Contains(typeof(IComponentCapabilityAnalyzer), adapterType.GetInterfaces());

        foreach (var method in typeof(IComponentCapabilityAnalyzer).GetMethods())
        {
            Assert.False(IsForeignLibraryType(method.ReturnType));
            foreach (var parameter in method.GetParameters())
            {
                Assert.False(IsForeignLibraryType(parameter.ParameterType));
            }
        }

        var visited = new HashSet<Type>();
        foreach (var surface in new[]
                 {
                     typeof(ComponentCapabilityAnalysis),
                     typeof(ComponentCapabilityAnalysisRequest),
                     typeof(ComponentCapabilitySelection),
                     typeof(ComponentGeometryLodFacts),
                     typeof(ComponentCapabilityReason),
                 })
        {
            Assert.True(IsNativeFree(surface, visited), $"{surface.Name} exposes a ValveResourceFormat or ValveKeyValue type.");
        }
    }

    [Fact]
    public void CapabilityAnalyzerSourceAvoidsMutationAndIoApis()
    {
        var sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "S2ModKit.Adapters.Source2", "Source2CompiledModelComponentDiscovery.cs");
        Assert.True(File.Exists(sourcePath), $"The capability analyzer source was not found at '{sourcePath}'.");
        var source = File.ReadAllText(sourcePath);
        foreach (var forbidden in new[]
                 {
                     "PlanTransform",
                     "DecodedPositionBufferTransformer",
                     "Encode",
                     "Rewrite",
                     "Serialize",
                     "ResourceEnvelopeWriter",
                     "File.",
                     "Directory.",
                     "Process.",
                     "Console.",
                     "Environment.",
                     "HttpClient",
                 })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
    }

}
