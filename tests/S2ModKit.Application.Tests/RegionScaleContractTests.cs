using System.Text.Json.Nodes;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class ExperimentalVisualContractTests
{
    private static readonly RegionScaleSelection Ramp = new("axis_ramp", 1, "z", 0, 8);

    [Fact]
    public async Task RegionRecipeUsesSeparateVersionAndStrictShape()
    {
        var recipe = Recipe(Operation() with { Version = 6, Granularity = "axis_ramp_vertices", Region = Ramp }) with { SchemaVersion = 7 };
        var json = JsonDefaults.Serialize(recipe);
        RecipeValidator.Validate(ReadRecipe(json));
        var schema = await JsonSchema.FromFileAsync(SchemaPath("recipe.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(schema.Validate(json));
        var old = await JsonSchema.FromFileAsync(SchemaPath("v6/recipe.schema.json"), TestContext.Current.CancellationToken);
        Assert.NotEmpty(old.Validate(json));
        foreach (var field in new[] { "kind", "version", "axis", "pinnedThrough", "fullFrom" })
        {
            var node = JsonNode.Parse(json)!;
            node["operations"]![0]!["region"]!.AsObject().Remove(field);
            Assert.Throws<S2ModKitException>(() => ReadRecipe(node.ToJsonString()));
            Assert.NotEmpty(schema.Validate(node.ToJsonString()));
        }
        var operation = (TransformComponentOperation)recipe.Operations[0];
        foreach (var invalid in new[] { Ramp with { Axis = "q" }, Ramp with { PinnedThrough = 8 }, Ramp with { Version = 2 } })
            Assert.Throws<S2ModKitException>(() => RecipeValidator.Validate(recipe with { Operations = [operation with { Region = invalid }] }));
        var legacy = JsonNode.Parse(JsonDefaults.Serialize(Recipe()))!;
        legacy["operations"]![0]!["region"] = null;
        Assert.Throws<S2ModKitException>(() => ReadRecipe(legacy.ToJsonString()));
    }

    [Fact]
    public void RegionPlansFreezeAllMaskFactsAndRejectLegacySmuggling()
    {
        var plan = RegionPlan();
        var json = JsonDefaults.Serialize(plan);
        Assert.Equal(json, JsonDefaults.Serialize(ReadPlan(json)));
        var target = plan.Operations[0].ExperimentalTransformTarget!;
        var drift = plan with
        {
            Operations = [plan.Operations[0] with { ExperimentalTransformTarget = target with
        { Region = target.Region! with { Selection = Ramp with { FullFrom = 9 } } } }]
        };
        Assert.NotEqual(plan.Fingerprint, MutationPlanJson.ComputeExperimentalFingerprint(drift));
        Assert.Throws<S2ModKitException>(() => ReadPlan(JsonDefaults.Serialize(drift)));
        foreach (var field in new[] { "maskHash", "pinnedVertexCount", "packedFrameLayout", "expectedPackedFrameHash" })
        {
            var node = JsonNode.Parse(json)!;
            node["operations"]![0]!["experimentalTransformTarget"]!["region"]!["buffers"]![0]!.AsObject().Remove(field);
            Assert.Throws<S2ModKitException>(() => ReadPlan(node.ToJsonString()));
        }
        var legacy = JsonNode.Parse(JsonDefaults.Serialize(Plan()))!;
        legacy["operations"]![0]!["experimentalTransformTarget"]!["region"] = null;
        Assert.Throws<S2ModKitException>(() => ReadPlan(legacy.ToJsonString()));
        var invalid = plan with
        {
            Operations = [plan.Operations[0] with { ExperimentalTransformTarget = target with
        { Region = target.Region! with { Buffers = [target.Region.Buffers[0] with { PinnedVertexCount = 99 }] } } }]
        };
        invalid = invalid with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(invalid) };
        Assert.Throws<S2ModKitException>(() => ReadPlan(JsonDefaults.Serialize(invalid)));
    }

    [Fact]
    public async Task RegionEvidenceHasIndependentVersionAndRequiredFacts()
    {
        var schema = await JsonSchema.FromFileAsync(SchemaPath("evidence.schema.json"), TestContext.Current.CancellationToken);
        var old = await JsonSchema.FromFileAsync(SchemaPath("v7/evidence.schema.json"), TestContext.Current.CancellationToken);
        foreach (var built in new[] { false, true })
        {
            var report = ExperimentalReport(built);
            var operation = report.Operations[0];
            report = report with
            {
                SchemaVersion = 8,
                Operations = [operation with
            {
                Version = 6,
                ExperimentalTransform = operation.ExperimentalTransform! with { StructuralProfileId = "root_owned_axis_ramp_visual_scale", Region = RegionPlan().Operations[0].ExperimentalTransformTarget!.Region },
                GeometryChanges = [operation.GeometryChanges[0] with { ChangedAttributes = ["normal_tangent", "position"] }],
            }]
            };
            var json = JsonDefaults.Serialize(report);
            Assert.Empty(schema.Validate(json));
            Assert.NotEmpty(old.Validate(json));
            Assert.Equal(json, JsonDefaults.Serialize(ReadEvidence(json)));
            var node = JsonNode.Parse(json)!;
            node["operations"]![0]!["experimentalTransform"]!["region"]!["buffers"]![0]!.AsObject().Remove("maskHash");
            Assert.Throws<S2ModKitException>(() => ReadEvidence(node.ToJsonString()));
            Assert.NotEmpty(schema.Validate(node.ToJsonString()));
        }
    }

    private static MutationPlan RegionPlan()
    {
        var original = Plan();
        var operation = original.Operations[0];
        var target = operation.ExperimentalTransformTarget!;
        var geometry = target.GeometryTargets[0] with { AllowedChangedAttributes = ["normal_tangent", "position"] };
        target = target with
        {
            StructuralProfileId = "root_owned_axis_ramp_visual_scale",
            GeometryTargets = [geometry],
            Region = new(Ramp, [new(0, 0, 0, Hash, 1, 1, 1, 2, new("R32_UINT", 12, 28, "source2_normal_tangent_v2"), Hash, Hash)])
        };
        var plan = original with { SchemaVersion = 3, Operations = [operation with { Version = 6, ExperimentalTransformTarget = target }] };
        return plan with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(plan) };
    }
}
