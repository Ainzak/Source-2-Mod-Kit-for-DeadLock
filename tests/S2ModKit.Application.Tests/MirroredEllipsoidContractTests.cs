using System.Text.Json.Nodes;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class ExperimentalVisualContractTests
{
    [Fact]
    public async Task MirroredIntentAndPlanAreClosedAndSingleContractsKeepTheirBytes()
    {
        var single = EllipsoidRecipe(); var oldBytes = JsonDefaults.Serialize(single);
        var op = (TransformComponentOperation)single.Operations[0];
        var field = op.LocalTransform!.Field with
        {
            Kind = "mirrored_ellipsoids",
            Center = new() { X = 4 },
            OuterRadii = new() { X = 4, Y = 4, Z = 4 },
            MirrorPlane = new("x", 0),
        };
        var intent = op.LocalTransform with { Field = field };
        var recipe = single with { Operations = [op with { LocalTransform = intent }] };
        var schema = await JsonSchema.FromFileAsync(SchemaPath("recipe.schema.json"), TestContext.Current.CancellationToken);
        var json = JsonDefaults.Serialize(recipe);
        Assert.Empty(schema.Validate(json)); Assert.Equal(json, JsonDefaults.Serialize(ReadRecipe(json)));
        foreach (var name in new[] { "mirrorPlane", "center", "outerRadii" })
        {
            var bad = JsonNode.Parse(json)!; bad["operations"]![0]!["localTransform"]!["field"]!.AsObject().Remove(name);
            Assert.Throws<S2ModKitException>(() => ReadRecipe(bad.ToJsonString())); Assert.NotEmpty(schema.Validate(bad.ToJsonString()));
        }
        var math = EllipsoidFieldMath.Create(intent, 64);
        var plan = EllipsoidPlan(); var target = plan.Operations[0].EllipsoidTransformTarget!;
        var buffer = target.Buffers[0] with { MirroredMasks = [new(Hash, Hash, 1, 0, 2, 1), new(Hash, Hash, 0, 1, 2, 1)] };
        target = target with { LocalTransform = intent, Certificate = EllipsoidContractValidator.Certificate(math.Certificate), Mirror = EllipsoidFieldMath.Mirror(math), Buffers = [buffer] };
        plan = RehashMirrored(plan, target);
        var planJson = JsonDefaults.Serialize(plan);
        Assert.Equal(planJson, JsonDefaults.Serialize(MutationPlanJson.Read(System.Text.Encoding.UTF8.GetBytes(planJson))));
        var report = EllipsoidReport(true);
        report = report with
        {
            PlanFingerprint = plan.Fingerprint,
            Operations = [report.Operations[0] with
        { EllipsoidTransform = report.Operations[0].EllipsoidTransform! with { Target = plan.Operations[0].EllipsoidTransformTarget! } }]
        };
        var evidenceJson = JsonDefaults.Serialize(report);
        Assert.Equal(evidenceJson, JsonDefaults.Serialize(ReadEvidence(evidenceJson)));
        var evidenceSchema = await JsonSchema.FromFileAsync(SchemaPath("evidence.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(evidenceSchema.Validate(evidenceJson));
        var planSchema = await JsonSchema.FromFileAsync(SchemaPath("mutation-plan.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(planSchema.Validate(planJson));
        foreach (var invalid in new[]
        {
            target with { Mirror = target.Mirror! with { ReflectedCenter = new() { X = -5 } } },
            target with { Mirror = null },
            target with { Buffers = [buffer with { MirroredMasks = [buffer.MirroredMasks![0]] }] },
            target with { Buffers = [buffer with { MirroredMasks = [buffer.MirroredMasks![0], buffer.MirroredMasks[1] with { ChangedPositionCount = 0 }] }] },
        }) Assert.Throws<S2ModKitException>(() => MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(RehashMirrored(plan, invalid))));
        var coordinated = CoordinatedRecipe(); var coordinatedOp = (TransformComponentOperation)coordinated.Operations[0];
        coordinated = coordinated with
        {
            Operations = [coordinatedOp with { CoordinatedTransform = coordinatedOp.CoordinatedTransform! with
        { Field = new CoordinatedEllipsoidField { Version = 1, CoordinateSpace = "model", Intent = intent } } }]
        };
        Assert.Throws<S2ModKitException>(() => ReadRecipe(JsonDefaults.Serialize(coordinated)));
        Assert.NotEmpty(schema.Validate(JsonDefaults.Serialize(coordinated)));
        Assert.Equal(oldBytes, JsonDefaults.Serialize(ReadRecipe(oldBytes)));
        Assert.DoesNotContain("mirror", oldBytes, StringComparison.Ordinal);
    }

    private static MutationPlan RehashMirrored(MutationPlan plan, PlannedEllipsoidTransformTarget target)
    {
        target = target with { TargetFingerprint = MutationPlanJson.ComputeEllipsoidTargetFingerprint(target) };
        plan = plan with { Operations = [plan.Operations[0] with { EllipsoidTransformTarget = target }] };
        return plan with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(plan) };
    }
}
