using System.Text.Json;
using System.Text.Json.Nodes;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed class CullingEnvelopeContractTests
{
    private static readonly ContentHash Hash = ContentHash.Compute("culling-contract-test"u8);

    [Fact]
    public async Task VersionOneInventoryPlanAndEvidenceAreStrictAndRoundTrip()
    {
        var schema = await JsonSchema.FromFileAsync(
            GetSchemaPath(), TestContext.Current.CancellationToken);
        var inventory = CreateInventory();
        var plan = CreatePlan();
        var evidence = CreateEvidence();

        var inventoryJson = JsonDefaults.Serialize(inventory);
        var planJson = JsonDefaults.Serialize(plan);
        var evidenceJson = JsonDefaults.Serialize(evidence);

        Assert.Empty(schema.Validate(inventoryJson));
        Assert.Empty(schema.Validate(planJson));
        Assert.Empty(schema.Validate(evidenceJson));
        var roundTripPlan = JsonDefaults.Deserialize<ConservativeCullingPlanDocument>(
            System.Text.Encoding.UTF8.GetBytes(planJson), "Culling plan");
        var roundTripEvidence = JsonDefaults.Deserialize<ConservativeCullingEvidenceDocument>(
            System.Text.Encoding.UTF8.GetBytes(evidenceJson), "Culling evidence");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(planJson), JsonNode.Parse(JsonDefaults.Serialize(roundTripPlan))));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(evidenceJson), JsonNode.Parse(JsonDefaults.Serialize(roundTripEvidence))));
    }

    [Fact]
    public async Task InventoryDistinguishesAbsentAndUnsupportedFieldsWithoutSubstitutes()
    {
        var schema = await JsonSchema.FromFileAsync(
            GetSchemaPath(), TestContext.Current.CancellationToken);
        var inventory = CreateInventory() with
        {
            Fields =
            [
                new CullingInventoryField(Field("m_optionalBounds"), "absent", null, null, null),
                new CullingInventoryField(
                    Field("m_modelSkeleton.m_boneSphere[0]"),
                    "unsupported",
                    new CullingRawFieldValue("sphere_radius", null, null, null, null, 2f),
                    new CullingCoordinateSpaceIdentity("unsupported", null, null, null, "ROOT_SPHERE_SPACE_UNVERIFIED"),
                    new CullingContributorSetIdentity("unsupported", null, null, null, null, "ROOT_REMAP_UNAVAILABLE")),
            ],
        };

        Assert.Empty(schema.Validate(JsonDefaults.Serialize(inventory)));
    }

    [Fact]
    public async Task PlanRejectsMissingRawCenterUnknownPropertiesAndDifferentPolicyVersion()
    {
        var schema = await JsonSchema.FromFileAsync(
            GetSchemaPath(), TestContext.Current.CancellationToken);
        var json = JsonDefaults.Serialize(CreatePlan());
        var missingCenter = JsonNode.Parse(json)!;
        missingCenter["targets"]![0]!["originalValue"]!["center"] = null;
        var unknownProperty = JsonNode.Parse(json)!;
        unknownProperty["unexpected"] = true;
        var changedPolicy = JsonNode.Parse(json)!;
        changedPolicy["policy"]!["version"] = 2;

        Assert.NotEmpty(schema.Validate(missingCenter.ToJsonString()));
        Assert.NotEmpty(schema.Validate(unknownProperty.ToJsonString()));
        Assert.NotEmpty(schema.Validate(changedPolicy.ToJsonString()));
    }

    [Fact]
    public async Task EvidenceRejectsFailureThatClaimsEveryVerificationPassed()
    {
        var schema = await JsonSchema.FromFileAsync(
            GetSchemaPath(), TestContext.Current.CancellationToken);
        var evidence = CreateEvidence() with
        {
            Targets =
            [
                new ConservativeCullingTargetEvidence(
                    Field("m_sceneObjects[0].bounds"),
                    RawBox(1f),
                    CoordinateSpace(),
                    Contributors(),
                    RawBox(2f),
                    RawBox(2f),
                    new CullingVerificationResult("failed", true, true, true, null)),
            ],
        };

        Assert.NotEmpty(schema.Validate(JsonDefaults.Serialize(evidence)));
    }

    [Fact]
    public async Task PassedEvidenceRequiresObservedTargetValueAndOutputResource()
    {
        var schema = await JsonSchema.FromFileAsync(
            GetSchemaPath(), TestContext.Current.CancellationToken);
        var successfulTarget = new ConservativeCullingTargetEvidence(
            Field("m_sceneObjects[0].bounds"),
            RawBox(1f),
            CoordinateSpace(),
            Contributors(),
            RawBox(2f),
            RawBox(2f),
            new CullingVerificationResult("passed", true, true, true, null));
        var successfulEvidence = CreateEvidence() with
        {
            Output = Resource(),
            Targets = [successfulTarget],
        };
        var missingObservedValue = successfulEvidence with
        {
            Targets = [successfulTarget with { ObservedOutputValue = null }],
        };
        var missingOutputResource = successfulEvidence with { Output = null };

        Assert.Empty(schema.Validate(JsonDefaults.Serialize(successfulEvidence)));
        Assert.NotEmpty(schema.Validate(JsonDefaults.Serialize(missingObservedValue)));
        Assert.NotEmpty(schema.Validate(JsonDefaults.Serialize(missingOutputResource)));
    }

    private static CullingInventoryDocument CreateInventory() => new(
        CullingEnvelopeContract.InventorySchemaVersion,
        Resource(),
        "verified",
        [
            new CullingInventoryField(
                Field("m_skeleton.m_bones[0].m_bbox"),
                "verified",
                RawBox(1f),
                CoordinateSpace(),
                Contributors()),
        ],
        [
            new CullingMeshBoneRemapInventory(
                "models/test.vmdl_c",
                Hash,
                0,
                3,
                [0, 1],
                "verified",
                Hash,
                [1, 0],
                "verified",
                ["render-zero", "render-one"],
                "verified",
                ["model-zero", "model-one"],
                null),
        ],
        new Dictionary<string, JsonElement>());

    private static ConservativeCullingPlanDocument CreatePlan() => new(
        CullingEnvelopeContract.PlanSchemaVersion,
        "synthetic-culling-plan",
        new CullingEnvelopePolicyIdentity(CullingEnvelopeContract.PolicyId, CullingEnvelopeContract.PolicyVersion),
        Resource(),
        [
            new ConservativeCullingPlanTarget(
                Field("m_skeleton.m_bones[0].m_bbox"),
                RawBox(1f),
                CoordinateSpace(),
                Contributors(),
                RawBox(2f),
                [new CullingGrowthMeasurement("max_x", 1f, 2f, 1f)]),
        ],
        new Dictionary<string, JsonElement>());

    private static ConservativeCullingEvidenceDocument CreateEvidence() => new(
        CullingEnvelopeContract.EvidenceSchemaVersion,
        new CullingEnvelopePolicyIdentity(CullingEnvelopeContract.PolicyId, CullingEnvelopeContract.PolicyVersion),
        Hash,
        Resource(),
        null,
        [
            new ConservativeCullingTargetEvidence(
                Field("m_skeleton.m_bones[0].m_bbox"),
                RawBox(1f),
                CoordinateSpace(),
                Contributors(),
                RawBox(2f),
                null,
                new CullingVerificationResult("not_run", null, null, null, null)),
        ],
        new Dictionary<string, JsonElement>());

    private static CullingResourceIdentity Resource() => new("models/test.vmdl_c", Hash, 128);

    private static CullingFieldIdentity Field(string path) => new(
        "models/test.vmdl_c",
        Hash,
        3,
        "MDAT",
        path,
        0,
        0,
        [0, 1]);

    private static CullingRawFieldValue RawBox(float halfExtent) => new(
        "aabb_center_half_extents",
        null,
        null,
        new TransformVector3 { X = 0f, Y = 0f, Z = 0f },
        new TransformVector3 { X = halfExtent, Y = halfExtent, Z = halfExtent },
        null);

    private static CullingCoordinateSpaceIdentity CoordinateSpace() => new(
        "verified",
        "source2_render_bone_bind_local",
        Hash,
        [1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f],
        null);

    private static CullingContributorSetIdentity Contributors() => new(
        "verified",
        CullingEnvelopeContract.ContributorIdentityAlgorithm,
        Hash,
        3,
        [
            new CullingContributorSourceIdentity(
                "models/test.vmdl_c",
                Hash,
                [0, 1],
                0,
                3,
                0,
                4,
                0,
                5,
                8,
                3,
                Hash,
                Hash,
                Hash,
                Hash,
                14,
                28,
                "verified",
                Hash,
                1),
        ],
        null);

    private static string GetSchemaPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "schemas", "culling-envelope-contracts.schema.json");
    }
}
