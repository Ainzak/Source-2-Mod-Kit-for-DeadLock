using System.Text;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed class PreciseComponentDiscoveryServiceTests
{
    private const string ResourcePath = "models/heroes/haze/haze.vmdl_c";
    private const string SharedMaterial = "materials/heroes/haze/haze_v2_gun.vmat_c";

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task DiscoverCreatesDeterministicMaterialAndWholeMeshLineageCandidates(int lodCount)
    {
        var fixture = CreateGunFixture(lodCount);
        var reversed = Reverse(fixture);

        var first = await new PreciseComponentDiscoveryService().DiscoverAsync(
            fixture.Input,
            fixture.Model,
            TestContext.Current.CancellationToken);
        var second = await new PreciseComponentDiscoveryService().DiscoverAsync(
            reversed.Input,
            reversed.Model,
            TestContext.Current.CancellationToken);

        Assert.Equal(ComponentDiscoveryV2Contract.SchemaVersion, first.SchemaVersion);
        Assert.Equal(first.DiscoveryFingerprint, second.DiscoveryFingerprint);
        Assert.Equal(
            first.Candidates.Select(candidate => candidate.CandidateId),
            second.Candidates.Select(candidate => candidate.CandidateId));
        Assert.IsType<MaterialGroupComponentCandidateV2>(first.Candidates[0]);
        var material = Assert.Single(first.Candidates.OfType<MaterialGroupComponentCandidateV2>());
        Assert.Equal(SharedMaterial, material.MaterialPath);
        Assert.All(material.Lods, lod => Assert.Equal(2, lod.DrawCallCount));

        var lineages = first.Candidates.OfType<MeshLineageComponentCandidateV2>().ToArray();
        Assert.Equal(["haze_gun_l", "haze_gun_r"], lineages.Select(candidate => candidate.LineageKey));
        Assert.All(lineages, candidate =>
        {
            Assert.Equal(lodCount, candidate.Lods.Count);
            Assert.All(candidate.Lods, lod =>
            {
                Assert.Equal(1, lod.DrawCallCount);
                Assert.Single(lod.DrawCallIds);
                Assert.Equal([SharedMaterial], lod.MaterialPaths);
            });
            Assert.Equal(ComponentDiscoveryContract.Available, candidate.Capabilities[0].Availability);
            Assert.Equal(ComponentDiscoveryContract.Blocked, candidate.Capabilities[1].Availability);
        });
        Assert.Empty(first.LineageDiagnostics);
    }

    [Fact]
    public async Task DiscoverSubmitsBothCandidateKindsToOneAnalyzerBatch()
    {
        var fixture = CreateGunFixture(3);
        var analyzer = new RecordingAnalyzer();

        var result = await new PreciseComponentDiscoveryService(analyzer).DiscoverAsync(
            fixture.Input,
            fixture.Model,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, analyzer.CallCount);
        Assert.Equal(
            [ComponentDiscoveryV2Contract.MaterialGroupKind, ComponentDiscoveryV2Contract.MeshLineageKind, ComponentDiscoveryV2Contract.MeshLineageKind],
            analyzer.Selections.Select(selection => selection.CandidateKind));
        Assert.All(result.Candidates, candidate =>
        {
            var transform = candidate.Capabilities.Single(capability => capability.OperationKind == "transform_component");
            Assert.Equal(ComponentDiscoveryContract.Available, transform.Availability);
            Assert.Equal([0, 1, 2], transform.GeometryByLod.Select(facts => facts.Lod));
        });
    }

    [Fact]
    public async Task IncompleteLineageIsRejectedWithoutRemovingMaterialCandidate()
    {
        var fixture = CreateGunFixture(3);
        var lods = fixture.Model.Lods.Select(lod => lod.Level == 2
            ? lod with
            {
                Meshes = lod.Meshes.Select(mesh => mesh.MechanicalLineage?.Key == "haze_gun_l"
                    ? mesh with { MechanicalLineage = null }
                    : mesh).ToArray(),
            }
            : lod).ToArray();

        var result = await new PreciseComponentDiscoveryService().DiscoverAsync(
            fixture.Input,
            fixture.Model with { Lods = lods },
            TestContext.Current.CancellationToken);

        Assert.Single(result.Candidates.OfType<MaterialGroupComponentCandidateV2>());
        Assert.DoesNotContain(result.Candidates.OfType<MeshLineageComponentCandidateV2>(), candidate => candidate.LineageKey == "haze_gun_l");
        Assert.Contains(result.LineageDiagnostics, diagnostic => diagnostic.Code == "COMPONENT_LINEAGE_FACT_MISSING");
        var incomplete = Assert.Single(
            result.LineageDiagnostics,
            diagnostic => diagnostic.Code == "COMPONENT_LINEAGE_INCOMPLETE_LOD_COVERAGE");
        Assert.Equal("haze_gun_l", incomplete.LineageKey);
        Assert.Equal([0, 1], incomplete.ObservedLods);
    }

    [Fact]
    public async Task DuplicateLineageInOneLodIsRejectedWithoutOrdinalGuessing()
    {
        var fixture = CreateGunFixture(3);
        var lods = fixture.Model.Lods.Select(lod => lod.Level == 1
            ? lod with
            {
                Meshes = lod.Meshes.Select(mesh => mesh.MechanicalLineage?.Key == "haze_gun_r"
                    ? mesh with { MechanicalLineage = MechanicalMeshLineage.FromSourceLabel("haze_gun_l") }
                    : mesh).ToArray(),
            }
            : lod).ToArray();

        var result = await new PreciseComponentDiscoveryService().DiscoverAsync(
            fixture.Input,
            fixture.Model with { Lods = lods },
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result.Candidates.OfType<MeshLineageComponentCandidateV2>(), candidate => candidate.LineageKey == "haze_gun_l");
        Assert.Contains(result.LineageDiagnostics, diagnostic =>
            diagnostic.LineageKey == "haze_gun_l"
            && diagnostic.Code == "COMPONENT_LINEAGE_DUPLICATE_LOD");
    }

    [Fact]
    public async Task ConflictingSourceLabelsForOneNormalizedKeyAreRejected()
    {
        var fixture = CreateGunFixture(3);
        var lods = fixture.Model.Lods.Select(lod => lod.Level == 2
            ? lod with
            {
                Meshes = lod.Meshes.Select(mesh => mesh.MechanicalLineage?.Key == "haze_gun_l"
                    ? mesh with { MechanicalLineage = MechanicalMeshLineage.FromSourceLabel("HAZE_GUN_L") }
                    : mesh).ToArray(),
            }
            : lod).ToArray();

        var result = await new PreciseComponentDiscoveryService().DiscoverAsync(
            fixture.Input,
            fixture.Model with { Lods = lods },
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result.Candidates.OfType<MeshLineageComponentCandidateV2>(), candidate => candidate.LineageKey == "haze_gun_l");
        Assert.Contains(result.LineageDiagnostics, diagnostic =>
            diagnostic.LineageKey == "haze_gun_l"
            && diagnostic.Code == "COMPONENT_LINEAGE_SOURCE_LABEL_CONFLICT");
    }

    [Fact]
    public async Task NonCanonicalLineageFactIsReportedAndNeverGrouped()
    {
        var fixture = CreateGunFixture(1);
        var mesh = fixture.Model.Lods[0].Meshes[0] with
        {
            MechanicalLineage = new MechanicalMeshLineage("WRONG", "haze_gun_l", "haze_gun_l"),
        };
        var model = fixture.Model with
        {
            Lods = [fixture.Model.Lods[0] with { Meshes = [mesh, fixture.Model.Lods[0].Meshes[1]] }],
        };

        var result = await new PreciseComponentDiscoveryService().DiscoverAsync(
            fixture.Input,
            model,
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result.Candidates.OfType<MeshLineageComponentCandidateV2>(), candidate => candidate.LineageKey == "haze_gun_l");
        Assert.Contains(result.LineageDiagnostics, diagnostic => diagnostic.Code == "COMPONENT_LINEAGE_FACT_INVALID");
    }

    [Fact]
    public async Task LineageMembershipIncludesEveryMaterialAndDrawCallOfEachMesh()
    {
        var bytes = "multi-material-lineage"u8.ToArray();
        var hash = ContentHash.Compute(bytes);
        var meshes = Enumerable.Range(0, 3).Select(lod => Mesh(
            lod,
            lod,
            10 + lod,
            "weapon",
            ("materials/blade.vmat_c", 0),
            ("materials/gem.vmat_c", 1))).ToArray();
        var fixture = Fixture(bytes, hash, meshes.Select(mesh => new LodSnapshot(mesh.MeshOrdinal, [mesh])).ToArray());

        var result = await new PreciseComponentDiscoveryService().DiscoverAsync(
            fixture.Input,
            fixture.Model,
            TestContext.Current.CancellationToken);

        var lineage = Assert.Single(result.Candidates.OfType<MeshLineageComponentCandidateV2>());
        Assert.Equal(["materials/blade.vmat_c", "materials/gem.vmat_c"], lineage.MaterialPaths);
        Assert.All(lineage.Lods, lod =>
        {
            Assert.Equal(2, lod.DrawCallCount);
            Assert.Equal(2, lod.DrawCallIds.Count);
            Assert.Equal(lineage.MaterialPaths, lod.MaterialPaths);
        });
    }

    [Fact]
    public async Task MixedCandidateUnionDeduplicatesOverlappingCurrentDrawCalls()
    {
        var fixture = CreateGunFixture(3);
        var discovery = await new PreciseComponentDiscoveryService().DiscoverAsync(
            fixture.Input,
            fixture.Model,
            TestContext.Current.CancellationToken);
        var material = Assert.Single(discovery.Candidates.OfType<MaterialGroupComponentCandidateV2>());
        var left = discovery.Candidates.OfType<MeshLineageComponentCandidateV2>()
            .Single(candidate => candidate.LineageKey == "haze_gun_l");

        var union = ComponentCandidateUnionBuilder.Create(fixture.Model, [left, material]);

        Assert.Equal(6, union.SelectedDrawCalls.Count);
        Assert.Equal(6, union.SelectedDrawCalls.Select(call => call.DrawCallId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal([ComponentDiscoveryV2Contract.MaterialGroupKind, ComponentDiscoveryV2Contract.MeshLineageKind], union.CandidateKinds);
        Assert.Equal([SharedMaterial], union.MaterialPaths);
    }

    [Fact]
    public async Task UnionRejectsStaleExactMeshFactsAndDuplicateCandidateIds()
    {
        var fixture = CreateGunFixture(3);
        var discovery = await new PreciseComponentDiscoveryService().DiscoverAsync(
            fixture.Input,
            fixture.Model,
            TestContext.Current.CancellationToken);
        var left = discovery.Candidates.OfType<MeshLineageComponentCandidateV2>()
            .Single(candidate => candidate.LineageKey == "haze_gun_l");
        var staleLods = left.Lods.Select((lod, index) => index == 0
            ? lod with { ResourceBlockIndex = lod.ResourceBlockIndex + 1 }
            : lod).ToArray();
        var stale = new MeshLineageComponentCandidateV2(
            left.CandidateId,
            left.Model,
            left.LineageKey,
            left.SourceLabel,
            left.DisplayLabel,
            left.MaterialPaths,
            staleLods,
            left.Capabilities);

        var staleError = Assert.Throws<S2ModKitException>(() =>
            ComponentCandidateUnionBuilder.Create(fixture.Model, [stale]));
        Assert.Equal("SCAFFOLD_COMPONENT_STALE", staleError.Error.Code);

        var duplicateError = Assert.Throws<S2ModKitException>(() =>
            ComponentCandidateUnionBuilder.Create(fixture.Model, [left, left]));
        Assert.Equal("SCAFFOLD_COMPONENT_DUPLICATE", duplicateError.Error.Code);
    }

    [Fact]
    public void MechanicalLineageNormalizationIsInvariantAndRejectsControlCharacters()
    {
        var lineage = MechanicalMeshLineage.FromSourceLabel("  HaZe_GuN_L  ");

        Assert.Equal("haze_gun_l", lineage.Key);
        Assert.Equal("HaZe_GuN_L", lineage.SourceLabel);
        Assert.True(lineage.IsCanonical());
        Assert.Throws<ArgumentException>(() => MechanicalMeshLineage.FromSourceLabel("gun\u0000left"));
    }

    private static SyntheticFixture CreateGunFixture(int lodCount)
    {
        var bytes = Encoding.UTF8.GetBytes($"haze-guns-{lodCount}");
        var hash = ContentHash.Compute(bytes);
        var lods = Enumerable.Range(0, lodCount).Select(lod => new LodSnapshot(
            lod,
            [
                Mesh(lod, lod * 10, 100 + (lod * 2), "haze_gun_l", (SharedMaterial, 0)),
                Mesh(lod, (lod * 10) + 1, 101 + (lod * 2), "haze_gun_r", (SharedMaterial, 0)),
            ])).ToArray();
        return Fixture(bytes, hash, lods);
    }

    private static SyntheticFixture Reverse(SyntheticFixture fixture) => new(
        fixture.Input,
        fixture.Model with
        {
            Lods = fixture.Model.Lods.Reverse()
                .Select(lod => lod with
                {
                    Meshes = lod.Meshes.Reverse()
                        .Select(mesh => mesh with { DrawCalls = mesh.DrawCalls.Reverse().ToArray() })
                        .ToArray(),
                })
                .ToArray(),
        });

    private static SyntheticFixture Fixture(byte[] bytes, ContentHash hash, IReadOnlyList<LodSnapshot> lods) => new(
        new ArtifactContent(ResourcePath, hash, bytes),
        new ModelSnapshot(new ArtifactSnapshot(ResourcePath, hash, bytes.Length, []), lods));

    private static MeshSnapshot Mesh(
        int lod,
        int meshOrdinal,
        int blockIndex,
        string? lineage,
        params (string Material, int DrawCallOrdinal)[] drawCalls)
    {
        var snapshots = drawCalls.Select(item => DrawCallSnapshot.Create(
            ResourcePath,
            lod,
            meshOrdinal,
            item.DrawCallOrdinal,
            item.Material,
            item.DrawCallOrdinal * 3L,
            3)).ToArray();
        return new MeshSnapshot(
            ResourcePath,
            meshOrdinal,
            blockIndex,
            ContentHash.Compute(Encoding.UTF8.GetBytes($"mesh-{lod}-{meshOrdinal}")),
            snapshots)
        {
            MechanicalLineage = lineage is null ? null : MechanicalMeshLineage.FromSourceLabel(lineage),
        };
    }

    private sealed record SyntheticFixture(ArtifactContent Input, ModelSnapshot Model);

    private sealed class RecordingAnalyzer : IComponentCapabilityAnalyzer
    {
        public string AnalyzerName => "synthetic-precise";

        public string AnalyzerVersion => "2";

        public IReadOnlyDictionary<string, string> ComponentVersions { get; } =
            new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["synthetic.precise"] = "2",
            };

        public int CallCount { get; private set; }

        public IReadOnlyList<ComponentCapabilitySelection> Selections { get; private set; } = [];

        public bool CanAnalyze(ArtifactContent input, ModelSnapshot model) => true;

        public Task<IReadOnlyList<ComponentCapabilityAnalysis>> AnalyzeAsync(
            ComponentCapabilityAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            Selections = request.Selections.ToArray();
            var results = request.Selections.Select(selection => new ComponentCapabilityAnalysis(
                selection.SelectionId,
                "transform_component",
                1,
                ComponentDiscoveryContract.Available,
                [new ComponentCapabilityReason("COMPONENT_CAPABILITY_AVAILABLE", "Synthetic exact selection is available.")],
                selection.SelectedDrawCalls.GroupBy(call => call.Lod).OrderBy(group => group.Key)
                    .Select(group => new ComponentGeometryLodFacts(
                        group.Key,
                        group.Count() * 3,
                        ContentHash.Compute(Encoding.UTF8.GetBytes(string.Join('|', group.Select(call => call.DrawCallId).Order(StringComparer.Ordinal)))),
                        true))
                    .ToArray())).ToArray();
            return Task.FromResult<IReadOnlyList<ComponentCapabilityAnalysis>>(results);
        }
    }
}
