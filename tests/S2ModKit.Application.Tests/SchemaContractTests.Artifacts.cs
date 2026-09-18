using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class SchemaContractTests
{
    [Theory]
    [InlineData("project.schema.json")]
    [InlineData("recipe.schema.json")]
    [InlineData("component-discovery.schema.json")]
    [InlineData("hero-catalogue.schema.json")]
    [InlineData("guided-session.schema.json")]
    [InlineData("evidence.schema.json")]
    [InlineData("package.schema.json")]
    [InlineData("installation.schema.json")]
    [InlineData("runtime-observation.schema.json")]
    [InlineData("v1/project.schema.json")]
    [InlineData("v1/recipe.schema.json")]
    [InlineData("v1/component-discovery.schema.json")]
    [InlineData("v1/evidence.schema.json")]
    [InlineData("v1/package.schema.json")]
    [InlineData("v2/evidence.schema.json")]
    [InlineData("v2/recipe.schema.json")]
    [InlineData("v3/evidence.schema.json")]
    [InlineData("v3/recipe.schema.json")]
    [InlineData("v4/evidence.schema.json")]
    public async Task PublishedSchemaLoadsAsDraftSeven(string fileName)
    {
        var schema = await JsonSchema.FromFileAsync(GetSchemaPath(fileName), TestContext.Current.CancellationToken);

        Assert.NotNull(schema);
        Assert.Contains("draft-07", schema.SchemaVersion?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SerializedProjectManifestConformsToPublishedSchema()
    {
        var hash = ContentHash.Compute("project-input"u8);
        var manifest = new ProjectManifest
        {
            ProjectId = "schema-project",
            CreatedUtc = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero),
            Input = new ProjectArtifactManifest
            {
                LogicalPath = "models/heroes/test/test.vmdl_c",
                ContentHash = hash,
                Size = 13,
                ObjectRelativePath = $"objects/sha256/{hash}/content",
                SourcePath = "H:\\fixtures\\test.vmdl_c",
                CatalogIdentity = "H:\\fixtures",
            },
            DependencyGraphComplete = true,
            ResourceRoots = ["H:\\fixtures"],
        };
        var schema = await JsonSchema.FromFileAsync(GetSchemaPath("project.schema.json"), TestContext.Current.CancellationToken);

        var errors = schema.Validate(JsonDefaults.Serialize(manifest));

        Assert.Empty(errors);
    }

    [Fact]
    public async Task SerializedComponentDiscoveryConformsToStrictPublishedSchema()
    {
        var hash = ContentHash.Compute("component-input"u8);
        var vertexHash = ContentHash.Compute("component-vertices"u8);
        var model = new ComponentModelIdentity("models/test.vmdl_c", hash, 128);
        var capabilities = new ComponentCapability[]
        {
            new(
                "remove_component",
                1,
                ComponentDiscoveryContract.Available,
                [new ComponentCapabilityReason("COMPONENT_CAPABILITY_AVAILABLE", "Complete LOD coverage.")],
                []),
            new(
                "transform_component",
                1,
                ComponentDiscoveryContract.Available,
                [new ComponentCapabilityReason("COMPONENT_CAPABILITY_AVAILABLE", "Exclusive geometry.")],
                [new ComponentGeometryLodFacts(0, 12, vertexHash, true)]),
        };
        var result = new ComponentDiscoveryResultV2(
            ComponentDiscoveryV2Contract.SchemaVersion,
            model,
            ContentHash.Compute("discovery"u8),
            new ComponentCapabilityAnalyzerIdentity(
                "test-analyzer",
                "1",
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["test.component"] = "1",
                }),
            [
                new MaterialGroupComponentCandidateV2(
                    "cmp_0123456789abcdef01234567",
                    model,
                    "materials/accessory.vmat",
                    "accessory",
                    [new ComponentCandidateLod(0, ["dc_0123456789abcdef01234567"], 1)],
                    capabilities),
                new MeshLineageComponentCandidateV2(
                    "cmp_111111111111111111111111",
                    model,
                    "accessory_mesh",
                    "accessory_mesh",
                    "accessory_mesh",
                    ["materials/accessory.vmat"],
                    [new MeshLineageCandidateLod(
                        0,
                        "models/test.vmdl_c",
                        0,
                        3,
                        ContentHash.Compute("mesh"u8),
                        "accessory_mesh",
                        ["materials/accessory.vmat"],
                        ["dc_0123456789abcdef01234567"],
                        1)],
                    capabilities),
            ],
            [new ComponentLineageDiagnostic(null, "COMPONENT_LINEAGE_NAME_MISSING", "An unnamed mesh was not grouped.", [0])]);
        var schema = await JsonSchema.FromFileAsync(
            GetSchemaPath("component-discovery.schema.json"),
            TestContext.Current.CancellationToken);
        var json = JsonDefaults.Serialize(result);

        Assert.Empty(schema.Validate(json));
        Assert.NotEmpty(schema.Validate(json.Replace(
            "\"extensions\": {}",
            "\"unexpected\": true, \"extensions\": {}",
            StringComparison.Ordinal)));
    }

    [Fact]
    public async Task VersionOneComponentDiscoveryRemainsSchemaValid()
    {
        var hash = ContentHash.Compute("component-v1"u8);
        var model = new ComponentModelIdentity("models/test.vmdl_c", hash, 64);
        var result = new ComponentDiscoveryResult(
            ComponentDiscoveryContract.SchemaVersion,
            model,
            ContentHash.Compute("discovery-v1"u8),
            new ComponentCapabilityAnalyzerIdentity("legacy", "1", new Dictionary<string, string>()),
            [new ComponentCandidate(
                "cmp_0123456789abcdef01234567",
                ComponentDiscoveryContract.MaterialGroupKind,
                model,
                "materials/accessory.vmat",
                "accessory",
                [new ComponentCandidateLod(0, ["dc_0123456789abcdef01234567"], 1)],
                [
                    new ComponentCapability(
                        "remove_component",
                        1,
                        ComponentDiscoveryContract.Available,
                        [new ComponentCapabilityReason("COMPONENT_CAPABILITY_AVAILABLE", "Complete LOD coverage.")],
                        []),
                    new ComponentCapability(
                        "transform_component",
                        1,
                        ComponentDiscoveryContract.Unsupported,
                        [new ComponentCapabilityReason("TRANSFORM_SOURCE2_PROFILE_UNSUPPORTED", "No supported geometry profile.")],
                        []),
                ])]);
        var schema = await JsonSchema.FromFileAsync(
            GetSchemaPath("v1/component-discovery.schema.json"),
            TestContext.Current.CancellationToken);

        Assert.Empty(schema.Validate(JsonDefaults.Serialize(result)));
    }

}
