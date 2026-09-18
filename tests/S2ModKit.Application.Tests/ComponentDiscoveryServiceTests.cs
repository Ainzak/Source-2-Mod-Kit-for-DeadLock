using System.Text;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed class ComponentDiscoveryServiceTests
{
    [Fact]
    public async Task DiscoverGroupsExactMaterialsAndIsIndependentOfEnumerationOrder()
    {
        var firstFixture = CreateCompleteFixture(reverseEnumeration: false);
        var secondFixture = CreateCompleteFixture(reverseEnumeration: true);
        var firstAnalyzer = new FakeCapabilityAnalyzer();
        var secondAnalyzer = new FakeCapabilityAnalyzer();

        var first = await new ComponentDiscoveryService(firstAnalyzer).DiscoverAsync(
            firstFixture.Input,
            firstFixture.Model,
            TestContext.Current.CancellationToken);
        var second = await new ComponentDiscoveryService(secondAnalyzer).DiscoverAsync(
            secondFixture.Input,
            secondFixture.Model,
            TestContext.Current.CancellationToken);

        Assert.Equal(first.DiscoveryFingerprint, second.DiscoveryFingerprint);
        Assert.Equal(first.Candidates.Select(item => item.CandidateId), second.Candidates.Select(item => item.CandidateId));
        Assert.Equal(["materials/body.vmat", "materials/hat.vmat"], first.Candidates.Select(item => item.MaterialPath));

        var hat = first.Candidates[1];
        Assert.StartsWith("cmp_", hat.CandidateId, StringComparison.Ordinal);
        Assert.Equal(28, hat.CandidateId.Length);
        Assert.Equal("hat", hat.DisplayLabel);
        Assert.Equal([2, 1, 1], hat.Lods.Select(item => item.DrawCallCount));
        Assert.All(hat.Lods, item => Assert.Equal(item.DrawCallCount, item.DrawCallIds.Count));
        Assert.Equal(
            ["remove_component", "transform_component"],
            hat.Capabilities.Select(item => item.OperationKind));
        Assert.All(hat.Capabilities, item => Assert.Equal(ComponentDiscoveryContract.Available, item.Availability));
        Assert.Equal([6, 3, 3], hat.Capabilities[1].GeometryByLod.Select(item => item.SelectedVertexCount));
        Assert.Equal(1, firstAnalyzer.CallCount);
        Assert.Equal(2, firstAnalyzer.LastSelections.Length);
    }

    [Fact]
    public async Task DiscoverListsIncompleteMaterialButDoesNotSendItToAnalyzer()
    {
        var fixture = CreateFixture(
            "component-model"u8.ToArray(),
            Lod(0, ("body-0", "materials/body.vmat"), ("badge-0", "materials/badge.vmat")),
            Lod(1, ("body-1", "materials/body.vmat"), ("badge-1", "materials/badge.vmat")),
            Lod(2, ("body-2", "materials/body.vmat")));
        var analyzer = new FakeCapabilityAnalyzer();

        var result = await new ComponentDiscoveryService(analyzer).DiscoverAsync(
            fixture.Input,
            fixture.Model,
            TestContext.Current.CancellationToken);

        var badge = result.Candidates.Single(item => item.MaterialPath == "materials/badge.vmat");
        Assert.Equal([1, 1, 0], badge.Lods.Select(item => item.DrawCallCount));
        Assert.All(badge.Capabilities, capability =>
        {
            Assert.Equal(ComponentDiscoveryContract.Unsupported, capability.Availability);
            Assert.Equal("COMPONENT_INCOMPLETE_LOD_COVERAGE", Assert.Single(capability.Reasons).Code);
        });
        Assert.DoesNotContain(analyzer.LastSelections, item => item.SelectionId == badge.CandidateId);
    }

    [Fact]
    public async Task DiscoverAcceptsCompleteFourLodCoverage()
    {
        var fixture = CreateFixture(
            "four-lod-model"u8.ToArray(),
            Lod(0, ("bag-0", "materials/bag.vmat")),
            Lod(1, ("bag-1", "materials/bag.vmat")),
            Lod(2, ("bag-2", "materials/bag.vmat")),
            Lod(3, ("bag-3", "materials/bag.vmat")));

        var result = await new ComponentDiscoveryService(new FakeCapabilityAnalyzer()).DiscoverAsync(
            fixture.Input,
            fixture.Model,
            TestContext.Current.CancellationToken);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal([0, 1, 2, 3], candidate.Lods.Select(lod => lod.Lod));
        Assert.All(candidate.Capabilities, capability =>
            Assert.Equal(ComponentDiscoveryContract.Available, capability.Availability));
    }

    [Fact]
    public async Task DiscoverWithoutAnalyzerMarksOnlyTransformBlocked()
    {
        var fixture = CreateCompleteFixture();

        var result = await new ComponentDiscoveryService().DiscoverAsync(
            fixture.Input,
            fixture.Model,
            TestContext.Current.CancellationToken);

        Assert.Equal("none", result.Analyzer.Name);
        Assert.All(result.Candidates, candidate =>
        {
            Assert.Equal(ComponentDiscoveryContract.Available, candidate.Capabilities[0].Availability);
            Assert.Equal(ComponentDiscoveryContract.Blocked, candidate.Capabilities[1].Availability);
            Assert.Equal(
                "COMPONENT_CAPABILITY_ANALYZER_UNAVAILABLE",
                Assert.Single(candidate.Capabilities[1].Reasons).Code);
        });
    }

    [Fact]
    public async Task DiscoverPreservesAllAcceptedAnalyzerAvailabilities()
    {
        var fixture = CreateFixture(
            "availability-model"u8.ToArray(),
            Lod(0,
                ("available-0", "materials/available.vmat"),
                ("blocked-0", "materials/blocked.vmat"),
                ("unsupported-0", "materials/unsupported.vmat"),
                ("ambiguous-0", "materials/ambiguous.vmat")));
        var analyzer = new FakeCapabilityAnalyzer(selection => selection.MaterialPaths.Single() switch
        {
            "materials/available.vmat" => ComponentDiscoveryContract.Available,
            "materials/blocked.vmat" => ComponentDiscoveryContract.Blocked,
            "materials/unsupported.vmat" => ComponentDiscoveryContract.Unsupported,
            _ => ComponentDiscoveryContract.Ambiguous,
        });

        var result = await new ComponentDiscoveryService(analyzer).DiscoverAsync(
            fixture.Input,
            fixture.Model,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [ComponentDiscoveryContract.Ambiguous, ComponentDiscoveryContract.Available, ComponentDiscoveryContract.Blocked, ComponentDiscoveryContract.Unsupported],
            result.Candidates.Select(candidate => candidate.Capabilities[1].Availability));
    }

    [Fact]
    public async Task DiscoveryFingerprintIncludesAnalyzerIdentityButCandidateIdsDoNot()
    {
        var fixture = CreateCompleteFixture();
        var first = await new ComponentDiscoveryService(new FakeCapabilityAnalyzer(version: "1")).DiscoverAsync(
            fixture.Input,
            fixture.Model,
            TestContext.Current.CancellationToken);
        var second = await new ComponentDiscoveryService(new FakeCapabilityAnalyzer(version: "2")).DiscoverAsync(
            fixture.Input,
            fixture.Model,
            TestContext.Current.CancellationToken);

        Assert.NotEqual(first.DiscoveryFingerprint, second.DiscoveryFingerprint);
        Assert.Equal(first.Candidates.Select(item => item.CandidateId), second.Candidates.Select(item => item.CandidateId));
    }

    [Fact]
    public async Task DiscoveryCanonicalizesAnalyzerReasonOrder()
    {
        var fixture = CreateCompleteFixture();
        var first = await new ComponentDiscoveryService(new FakeCapabilityAnalyzer()).DiscoverAsync(
            fixture.Input,
            fixture.Model,
            TestContext.Current.CancellationToken);
        var second = await new ComponentDiscoveryService(new FakeCapabilityAnalyzer(reverseReasons: true)).DiscoverAsync(
            fixture.Input,
            fixture.Model,
            TestContext.Current.CancellationToken);

        Assert.Equal(first.DiscoveryFingerprint, second.DiscoveryFingerprint);
        Assert.Equal(
            first.Candidates.SelectMany(candidate => candidate.Capabilities[1].Reasons).Select(reason => reason.Code),
            second.Candidates.SelectMany(candidate => candidate.Capabilities[1].Reasons).Select(reason => reason.Code));
    }

    [Fact]
    public async Task CandidateIdsChangeWithImmutableModelHash()
    {
        var firstFixture = CreateCompleteFixture(bytes: "first-model"u8.ToArray());
        var secondFixture = CreateCompleteFixture(bytes: "second-model"u8.ToArray());

        var first = await new ComponentDiscoveryService().DiscoverAsync(
            firstFixture.Input,
            firstFixture.Model,
            TestContext.Current.CancellationToken);
        var second = await new ComponentDiscoveryService().DiscoverAsync(
            secondFixture.Input,
            secondFixture.Model,
            TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Candidates[0].CandidateId, second.Candidates[0].CandidateId);
    }

    [Fact]
    public async Task DiscoverRejectsInputDrift()
    {
        var fixture = CreateCompleteFixture();
        var drifted = fixture.Input with { Bytes = new byte[] { 99 } };

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() =>
            new ComponentDiscoveryService().DiscoverAsync(
                drifted,
                fixture.Model,
                TestContext.Current.CancellationToken));

        Assert.Equal("COMPONENT_INPUT_DRIFT", exception.Error.Code);
        Assert.Equal(ErrorCategory.InputOrResolution, exception.Error.Category);
    }

    [Fact]
    public async Task DiscoverRejectsDuplicateLodIdentity()
    {
        var fixture = CreateFixture(
            "duplicate-lod"u8.ToArray(),
            Lod(0, ("first", "materials/hat.vmat")),
            Lod(0, ("second", "materials/hat.vmat")));

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() =>
            new ComponentDiscoveryService().DiscoverAsync(
                fixture.Input,
                fixture.Model,
                TestContext.Current.CancellationToken));

        Assert.Equal("COMPONENT_LOD_IDENTITY_DUPLICATE", exception.Error.Code);
    }

    [Fact]
    public async Task DiscoverRejectsEmptyModel()
    {
        var bytes = "empty-model"u8.ToArray();
        var hash = ContentHash.Compute(bytes);
        var input = new ArtifactContent("models/hero/empty.vmdl_c", hash, bytes);
        var model = new ModelSnapshot(
            new ArtifactSnapshot(input.LogicalPath, hash, bytes.Length, []),
            []);

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() =>
            new ComponentDiscoveryService().DiscoverAsync(
                input,
                model,
                TestContext.Current.CancellationToken));

        Assert.Equal("COMPONENT_MODEL_EMPTY", exception.Error.Code);
    }

    [Fact]
    public async Task SameDisplayLabelsRemainDistinctMaterialCandidates()
    {
        var fixture = CreateFixture(
            "label-collision"u8.ToArray(),
            Lod(0, ("left", "materials/left/trim.vmat"), ("right", "materials/right/trim.vmat")));

        var result = await new ComponentDiscoveryService().DiscoverAsync(
            fixture.Input,
            fixture.Model,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Candidates.Count);
        Assert.All(result.Candidates, candidate => Assert.Equal("trim", candidate.DisplayLabel));
        Assert.Equal(2, result.Candidates.Select(candidate => candidate.CandidateId).Distinct().Count());
    }

    [Fact]
    public async Task DiscoverRejectsDuplicateDrawCallIdentity()
    {
        var fixture = CreateFixture(
            "duplicate-call"u8.ToArray(),
            Lod(0, ("same", "materials/hat.vmat")),
            Lod(1, ("same", "materials/hat.vmat")));

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() =>
            new ComponentDiscoveryService().DiscoverAsync(
                fixture.Input,
                fixture.Model,
                TestContext.Current.CancellationToken));

        Assert.Equal("COMPONENT_DRAW_CALL_IDENTITY_DUPLICATE", exception.Error.Code);
    }

    [Fact]
    public async Task DiscoverRejectsPartialAnalyzerCoverage()
    {
        var fixture = CreateCompleteFixture();
        var analyzer = new FakeCapabilityAnalyzer(omitLast: true);

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() =>
            new ComponentDiscoveryService(analyzer).DiscoverAsync(
                fixture.Input,
                fixture.Model,
                TestContext.Current.CancellationToken));

        Assert.Equal("COMPONENT_CAPABILITY_ANALYSIS_INCOMPLETE", exception.Error.Code);
    }

    [Fact]
    public async Task DiscoverRejectsDuplicateAnalyzerCoverage()
    {
        var fixture = CreateCompleteFixture();
        var analyzer = new FakeCapabilityAnalyzer(duplicateFirst: true);

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() =>
            new ComponentDiscoveryService(analyzer).DiscoverAsync(
                fixture.Input,
                fixture.Model,
                TestContext.Current.CancellationToken));

        Assert.Equal("COMPONENT_CAPABILITY_ANALYSIS_AMBIGUOUS", exception.Error.Code);
    }

    [Fact]
    public async Task DiscoverRejectsAvailableAnalysisWithoutCompleteExclusiveGeometry()
    {
        var fixture = CreateCompleteFixture();
        var analyzer = new FakeCapabilityAnalyzer(incompleteAvailableGeometry: true);

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() =>
            new ComponentDiscoveryService(analyzer).DiscoverAsync(
                fixture.Input,
                fixture.Model,
                TestContext.Current.CancellationToken));

        Assert.Equal("COMPONENT_CAPABILITY_FACTS_INVALID", exception.Error.Code);
    }

    [Fact]
    public async Task DiscoverRejectsAvailableAnalysisWithoutAValidVertexSetHash()
    {
        var fixture = CreateCompleteFixture();
        var analyzer = new FakeCapabilityAnalyzer(invalidGeometryHash: true);

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() =>
            new ComponentDiscoveryService(analyzer).DiscoverAsync(
                fixture.Input,
                fixture.Model,
                TestContext.Current.CancellationToken));

        Assert.Equal("COMPONENT_CAPABILITY_FACTS_INVALID", exception.Error.Code);
    }

    private static Fixture CreateCompleteFixture(bool reverseEnumeration = false, byte[]? bytes = null)
    {
        var lods = new[]
        {
            Lod(0,
                ("hat-0b", "Materials\\Hat.vmat"),
                ("body-0", "materials/body.vmat"),
                ("hat-0a", "materials/hat.vmat")),
            Lod(1, ("body-1", "materials/body.vmat"), ("hat-1", "materials/hat.vmat")),
            Lod(2, ("hat-2", "materials/hat.vmat"), ("body-2", "materials/body.vmat")),
        };
        if (reverseEnumeration)
        {
            lods = lods.Reverse()
                .Select(lod => lod with { DrawCalls = lod.DrawCalls.Reverse().ToArray() })
                .ToArray();
        }

        return CreateFixture(bytes ?? "component-model"u8.ToArray(), lods);
    }

    private static Fixture CreateFixture(byte[] bytes, params LodDefinition[] lods)
    {
        var hash = ContentHash.Compute(bytes);
        var artifact = new ArtifactContent("Models/Hero/Test.vmdl_c", hash, bytes);
        var snapshots = lods.Select(lod =>
        {
            var ordinals = lod.DrawCalls.Select(drawCall => drawCall.Id)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select((id, ordinal) => (id, ordinal))
                .ToDictionary(item => item.id, item => item.ordinal, StringComparer.Ordinal);
            return new LodSnapshot(
                lod.Level,
                lod.DrawCalls.Select(drawCall =>
                {
                    var ordinal = ordinals[drawCall.Id];
                    return new MeshSnapshot(
                        "Models/Hero/Test.vmdl_c",
                        ordinal,
                        checked((lod.Level * 100) + ordinal),
                        ContentHash.Compute(Encoding.UTF8.GetBytes($"mesh-{lod.Level}-{ordinal}")),
                        [new DrawCallSnapshot(drawCall.Id, drawCall.Material, ordinal, ordinal * 3L, 3)]);
                }).ToArray());
        }).ToArray();
        var model = new ModelSnapshot(
            new ArtifactSnapshot("models/hero/test.vmdl_c", hash, bytes.Length, []),
            snapshots);
        return new Fixture(artifact, model);
    }

    private static LodDefinition Lod(
        int level,
        params (string Id, string Material)[] drawCalls) => new(level, drawCalls);

    private sealed record Fixture(ArtifactContent Input, ModelSnapshot Model);

    private sealed record LodDefinition(
        int Level,
        IReadOnlyList<(string Id, string Material)> DrawCalls);

    private sealed class FakeCapabilityAnalyzer(
        Func<ComponentCapabilitySelection, string>? availability = null,
        string version = "1",
        bool omitLast = false,
        bool duplicateFirst = false,
        bool incompleteAvailableGeometry = false,
        bool reverseReasons = false,
        bool invalidGeometryHash = false) : IComponentCapabilityAnalyzer
    {
        public string AnalyzerName => "synthetic";

        public string AnalyzerVersion => version;

        public IReadOnlyDictionary<string, string> ComponentVersions { get; } =
            new Dictionary<string, string> { ["synthetic.geometry"] = "1" };

        public int CallCount { get; private set; }

        public ComponentCapabilitySelection[] LastSelections { get; private set; } = [];

        public bool CanAnalyze(ArtifactContent input, ModelSnapshot model) => true;

        public Task<IReadOnlyList<ComponentCapabilityAnalysis>> AnalyzeAsync(
            ComponentCapabilityAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastSelections = request.Selections.ToArray();
            var selected = omitLast ? request.Selections.SkipLast(1) : request.Selections;
            var results = selected.Select(selection => CreateAnalysis(
                selection,
                availability?.Invoke(selection) ?? ComponentDiscoveryContract.Available,
                incompleteAvailableGeometry,
                reverseReasons,
                invalidGeometryHash)).ToList();
            if (duplicateFirst && results.Count > 0)
            {
                results.Add(results[0]);
            }

            return Task.FromResult<IReadOnlyList<ComponentCapabilityAnalysis>>(results);
        }

        private static ComponentCapabilityAnalysis CreateAnalysis(
            ComponentCapabilitySelection selection,
            string availability,
            bool incompleteGeometry,
            bool reverseReasons,
            bool invalidGeometryHash)
        {
            var reason = availability switch
            {
                ComponentDiscoveryContract.Available => "COMPONENT_CAPABILITY_AVAILABLE",
                ComponentDiscoveryContract.Blocked => "MESHOPTIMIZER_CAPABILITY_UNAVAILABLE",
                ComponentDiscoveryContract.Unsupported => "TRANSFORM_SOURCE2_PROFILE_UNSUPPORTED",
                _ => "COMPONENT_CANDIDATE_MAPPING_AMBIGUOUS",
            };
            var geometry = string.Equals(availability, ComponentDiscoveryContract.Available, StringComparison.Ordinal)
                ? selection.SelectedDrawCalls.GroupBy(item => item.Lod).OrderBy(group => group.Key)
                    .Select(group => new ComponentGeometryLodFacts(
                        group.Key,
                        group.Count() * 3,
                        invalidGeometryHash
                            ? default
                            : ContentHash.Compute(Encoding.UTF8.GetBytes(string.Join('|', group.Select(item => item.DrawCallId).Order(StringComparer.Ordinal)))),
                        true))
                    .ToArray()
                : [];
            if (incompleteGeometry && geometry.Length > 0)
            {
                geometry = geometry.SkipLast(1).ToArray();
            }

            var reasons = new[]
            {
                new ComponentCapabilityReason(reason, "Synthetic capability result."),
                new ComponentCapabilityReason("SYNTHETIC_PROFILE_OBSERVED", "Synthetic profile facts were observed."),
            };
            if (reverseReasons)
            {
                Array.Reverse(reasons);
            }

            return new ComponentCapabilityAnalysis(
                selection.SelectionId,
                "transform_component",
                1,
                availability,
                reasons,
                geometry);
        }
    }
}
