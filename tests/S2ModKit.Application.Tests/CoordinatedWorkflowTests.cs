using System.Text;
using System.Text.Json.Nodes;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class PreciseComponentDiscoveryServiceTests
{
    [Fact]
    public async Task CoordinatedDiscoveryHasNewIdentitiesStrictCapabilitiesAndHistoricalCompatibility()
    {
        var fixture = CoordinatedFixture(); var token = TestContext.Current.CancellationToken;
        var service = new ExperimentalComponentDiscoveryService(null, null);
        var old = await service.DiscoverEllipsoidAsync(fixture.Input, fixture.Model, token);
        var current = await service.DiscoverCoordinatedAsync(fixture.Input, fixture.Model, token);
        Assert.Equal(5, current.SchemaVersion);
        Assert.Empty(old.Candidates.Select(c => c.CandidateId).Intersect(current.Candidates.Select(c => c.CandidateId)));
        Assert.All(current.Candidates, c => Assert.Equal("unsupported", Assert.Single(c.Capabilities, cap => cap.OperationVersion == 8).Availability));
        var json = JsonDefaults.Serialize(current);
        Assert.Equal(json, JsonDefaults.Serialize(ReadDiscovery(json)));
        var schema = await JsonSchema.FromFileAsync(ExperimentalSchemaPath("component-discovery.schema.json"), token);
        Assert.Empty(schema.Validate(json));
        var legacy = await JsonSchema.FromFileAsync(ExperimentalSchemaPath("v4/component-discovery.schema.json"), token);
        Assert.Empty(legacy.Validate(JsonDefaults.Serialize(old))); Assert.NotEmpty(legacy.Validate(json));
        var downgraded = JsonNode.Parse(json)!; downgraded["schemaVersion"] = 4;
        Assert.Throws<S2ModKitException>(() => ReadDiscovery(downgraded.ToJsonString()));
        Assert.Equal(json, JsonDefaults.Serialize(await service.DiscoverCoordinatedAsync(fixture.Input, Reverse(fixture).Model, token)));
    }

    [Fact]
    public async Task CoordinatedScaffoldDeduplicatesOverlappingViewsAndFreezesCompleteMembers()
    {
        var fixture = CoordinatedFixture(); var token = TestContext.Current.CancellationToken;
        var discovery = await new ExperimentalComponentDiscoveryService(null, null).DiscoverCoordinatedAsync(fixture.Input, fixture.Model, token);
        var material = discovery.Candidates.OfType<MaterialGroupComponentCandidateV2>().Single();
        var lineage = discovery.Candidates.OfType<MeshLineageComponentCandidateV2>().First();
        var request = new RecipeScaffoldRequest([lineage.CandidateId, material.CandidateId], "coordinated-field", "unused.json", ExperimentalDiscovery: true,
            ExperimentalDiscoverySchemaVersion: 5, Coordinated: CommonOptions());
        var scaffolder = new ComponentRecipeScaffolder(null);
        var recipe = await scaffolder.CreateAsync(fixture.Input, fixture.Model, discovery, request, token);
        var operation = Assert.IsType<TransformComponentOperation>(Assert.Single(recipe.Operations));
        Assert.Equal(9, recipe.SchemaVersion); Assert.Equal(8, operation.Version); Assert.Null(operation.Transform);
        Assert.Equal(2, operation.CoordinatedTransform!.Members.Count);
        Assert.All(operation.ExpectedMatchesByLod, l => Assert.Equal(2, l.Value));
        Assert.All(operation.ExpectedVerticesByLod!, l => Assert.Equal(6, l.Value));
        Assert.Equal(JsonDefaults.Serialize(recipe), JsonDefaults.Serialize(await scaffolder.CreateAsync(fixture.Input, fixture.Model, discovery,
            request with { ComponentIds = request.ComponentIds.Reverse().ToArray() }, token)));
        foreach (var invalid in new[] { request with { ComponentIds = [material.CandidateId, material.CandidateId] }, request with { ComponentIds = ["cmp_000000000000000000000000"] },
            request with { ComponentIds = [lineage.CandidateId] }, request with { ExperimentalDiscovery = false }, request with { Coordinated = null }, request with { MaximumVertexDisplacement = 64 },
            request with { UniformScale = 1.5f }, request with { TranslationX = 0 }, request with { Intent = "remove" } })
            await Assert.ThrowsAsync<S2ModKitException>(() => scaffolder.CreateAsync(fixture.Input, fixture.Model, discovery, invalid, token));
    }

    [Theory]
    [InlineData("missing_lineage")]
    [InlineData("missing_geometry")]
    [InlineData("material_drift")]
    [InlineData("partial_buffer")]
    [InlineData("duplicate_buffer_signature")]
    public async Task MechanicalMemberMappingRejectsIncompleteOrAmbiguousFacts(string failure)
    {
        var fixture = CoordinatedFixture(); var token = TestContext.Current.CancellationToken;
        var discovery = await new ExperimentalComponentDiscoveryService(null, null).DiscoverCoordinatedAsync(fixture.Input, fixture.Model, token);
        var union = ComponentCandidateUnionBuilder.Create(fixture.Model, [discovery.Candidates[0]], 5);
        var mesh = fixture.Model.Lods[0].Meshes[0]; var geometry = mesh.Geometry!;
        var altered = failure switch
        {
            "missing_lineage" => mesh with { MechanicalLineage = null },
            "missing_geometry" => mesh with { Geometry = null },
            "material_drift" => mesh with { DrawCalls = [mesh.DrawCalls[0] with { MaterialPath = "materials/changed.vmat_c" }] },
            "partial_buffer" => mesh with
            {
                Geometry = geometry with { DrawCalls = [.. geometry.DrawCalls, geometry.DrawCalls[0] with { DrawCallId = "dc_ffffffffffffffffffffffff" }] },
                DrawCalls = [.. mesh.DrawCalls, mesh.DrawCalls[0] with { Id = "dc_ffffffffffffffffffffffff" }]
            },
            _ => mesh with { Geometry = geometry with { DrawCalls = [.. geometry.DrawCalls, geometry.DrawCalls[0] with { VertexBufferOrdinal = 1 }] } },
        };
        var model = fixture.Model with { Lods = [fixture.Model.Lods[0] with { Meshes = [altered, fixture.Model.Lods[0].Meshes[1]] }, .. fixture.Model.Lods.Skip(1)] };
        Assert.Throws<S2ModKitException>(() => CoordinatedSelection.ResolveMembers(model, union));
    }

    private static CoordinatedScaffoldOptions CommonOptions()
    {
        var op = (TransformComponentOperation)ExperimentalVisualContractTests.CoordinatedRecipe().Operations[0];
        return new(op.RuntimeMetadataPolicy!, op.ZeroBoneBoxPolicy!, op.ZeroRenderSpherePolicy!, op.CoordinatedTransform!.Field, 64);
    }
    private static SyntheticFixture CoordinatedFixture()
    {
        var fixture = CreateGunFixture(3); var b = ExperimentalVisualContractTests.CoordinatedPlan().Operations[0].CoordinatedTransformTarget!.Buffers[0];
        return fixture with
        {
            Model = fixture.Model with
            {
                Lods = fixture.Model.Lods.Select(l => l with
                {
                    Meshes = l.Meshes.Select(m => m with
                    {
                        Geometry = new("ready", "synthetic", [new(l.Level, 1 + m.MeshOrdinal, 3, 28, b.VertexBlockInputHash, b.InputDecodedVertexBufferHash, b.PositionLayout)],
            [new(l.Level, 100 + m.MeshOrdinal, 3, 2, b.IndexBlockInputHash, b.DecodedIndexBufferHash)],
            [new(m.DrawCalls[0].Id, l.Level, l.Level, 0, 3, 3, m.ImmutableSemanticHash, b.BeforeBounds, true)], b.Codec)
                    }).ToArray()
                }).ToArray()
            }
        };
    }
}

public sealed class CoordinatedOptionsContractTests
{
    [Fact]
    public async Task CommonFieldOptionsRequireEveryVersionPolicyAndFieldFact()
    {
        var op = (TransformComponentOperation)ExperimentalVisualContractTests.CoordinatedRecipe().Operations[0];
        var options = new CoordinatedScaffoldOptions(op.RuntimeMetadataPolicy!, op.ZeroBoneBoxPolicy!, op.ZeroRenderSpherePolicy!, op.CoordinatedTransform!.Field, 64);
        var json = JsonDefaults.Serialize(options); Assert.Equal(json, JsonDefaults.Serialize(CoordinatedSelection.ReadOptions(Encoding.UTF8.GetBytes(json))));
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (!File.Exists(Path.Combine(root!.FullName, "S2ModKit.slnx"))) root = root.Parent;
        var schema = await JsonSchema.FromFileAsync(Path.Combine(root.FullName, "schemas/coordinated-field-options.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(schema.Validate(json));
        foreach (var property in JsonNode.Parse(json)!.AsObject().Select(p => p.Key).ToArray())
        {
            var invalid = JsonNode.Parse(json)!; invalid.AsObject().Remove(property);
            Assert.Throws<S2ModKitException>(() => CoordinatedSelection.ReadOptions(Encoding.UTF8.GetBytes(invalid.ToJsonString())));
            Assert.NotEmpty(schema.Validate(invalid.ToJsonString()));
        }
        foreach (var invalid in new[] { options with { SchemaVersion = 2 }, options with { Kind = "unknown" }, options with { ZeroBoneBoxPolicy = new("reject", 1) }, options with { MaximumDisplacement = 0 } })
            Assert.Throws<S2ModKitException>(() => CoordinatedSelection.ReadOptions(JsonDefaults.SerializeToUtf8(invalid)));
        Assert.Throws<S2ModKitException>(() => CoordinatedSelection.ReadOptions("{invalid"u8));
        Assert.Throws<S2ModKitException>(() => CoordinatedSelection.ReadOptions(Encoding.UTF8.GetBytes(json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1", StringComparison.Ordinal))));
    }
}
