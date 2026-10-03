using System.Text.Json.Nodes;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Application.Tests;

public sealed partial class ExperimentalVisualContractTests
{
    private static readonly ContentHash EllipsoidOutputHash = ContentHash.Compute("synthetic-ellipsoid-output"u8);
    private static readonly EllipsoidVisualTransform EllipsoidIntent = new(
        new("ellipsoid", 1, "model", new(), new() { X = 8, Y = 4, Z = 2 }, 1f / 16), 2, new("ellipsoid_numeric", 1));

    [Fact]
    public void EllipsoidPlanningRequiresSourceAdapterAndSnapshotChecksRejectUnchangedOutput()
    {
        var snapshot = new ModelSnapshot(new ArtifactSnapshot("models/test.vmdl_c", Hash, 12, []), []);
        Assert.Equal("LOD_COVERAGE_INCOMPLETE", Assert.Throws<S2ModKitException>(() => MutationPlanner.CreatePlan(snapshot, EllipsoidRecipe())).Error.Code);
        Assert.False(ModelVerifier.Verify(snapshot, snapshot, EllipsoidPlan()).IsValid);
    }

    [Fact]
    public void HistoricalExtensionDataKeepsItsMeaningWhenKeysResembleNewFields()
    {
        var recipe = Recipe(Operation() with
        {
            Extensions = new Dictionary<string, System.Text.Json.JsonElement>
            { ["localTransform"] = System.Text.Json.JsonSerializer.SerializeToElement(new { field = "historical-note" }) },
        });
        var json = JsonDefaults.Serialize(recipe);
        Assert.Equal(json, JsonDefaults.Serialize(ReadRecipe(json)));
        var report = ExperimentalReport(false) with
        { Extensions = new Dictionary<string, System.Text.Json.JsonElement> { ["ellipsoidTransform"] = System.Text.Json.JsonSerializer.SerializeToElement("historical-note") } };
        json = JsonDefaults.Serialize(report);
        Assert.Equal(json, JsonDefaults.Serialize(ReadEvidence(json)));
    }

    [Fact]
    public async Task EllipsoidRecipeRoundTripsStrictlyAndRetainsHistoricalEnvelope()
    {
        var json = JsonDefaults.Serialize(EllipsoidRecipe());
        Assert.DoesNotContain("\"transform\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"pivot\"", json, StringComparison.Ordinal);
        Assert.Equal(json, JsonDefaults.Serialize(ReadRecipe(json)));
        var schema = await JsonSchema.FromFileAsync(SchemaPath("recipe.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(schema.Validate(json));
        var old = await JsonSchema.FromFileAsync(SchemaPath("v7/recipe.schema.json"), TestContext.Current.CancellationToken);
        Assert.NotEmpty(old.Validate(json));
        Assert.Empty(old.Validate(JsonDefaults.Serialize(Recipe())));
        foreach (var path in RequiredPaths(JsonNode.Parse(json)!["operations"]![0]!["localTransform"]!))
        {
            var node = JsonNode.Parse(json)!;
            RemovePath(node["operations"]![0]!["localTransform"]!.AsObject(), path);
            Assert.Throws<S2ModKitException>(() => ReadRecipe(node.ToJsonString()));
            Assert.NotEmpty(schema.Validate(node.ToJsonString()));
        }
        foreach (var name in new[] { "transform", "region", "physicsPolicy", "connectedComponentIdsByLod" })
        {
            var node = JsonNode.Parse(json)!;
            node["operations"]![0]![name] = null;
            Assert.Throws<S2ModKitException>(() => ReadRecipe(node.ToJsonString()));
            Assert.NotEmpty(schema.Validate(node.ToJsonString()));
        }
    }

    [Fact]
    public void EllipsoidScopePoliciesAndFoldingRequestsFailClosed()
    {
        var recipe = EllipsoidRecipe();
        var operation = (TransformComponentOperation)recipe.Operations[0];
        foreach (var invalid in new[]
        {
            operation with { Transform = new() }, operation with { RuntimeMetadataPolicy = null },
            operation with { LocalTransform = null }, operation with { LocalTransform = EllipsoidIntent with { NumericalPolicy = new("unknown", 1) } },
            operation with { LocalTransform = EllipsoidIntent with { Field = EllipsoidIntent.Field with { Kind = "mirrored_ellipsoids" } } },
            operation with { LocalTransform = EllipsoidIntent with { Field = EllipsoidIntent.Field with { CoreFraction = 0.25f } } },
            operation with { LocalTransform = EllipsoidIntent with { Field = EllipsoidIntent.Field with { OuterRadii = new() { X = 9, Y = 1, Z = 1 } } } },
            operation with { LocalTransform = EllipsoidIntent with { UniformScale = 1 } },
        }) Assert.Throws<S2ModKitException>(() => EllipsoidContractValidator.ValidateRecipe(recipe with { Operations = [invalid] }));
        Assert.Throws<S2ModKitException>(() => ReadRecipe(JsonDefaults.Serialize(recipe with { Operations = [operation, operation with { OperationId = "other" }] })));
        foreach (var version in Enumerable.Range(1, 7))
        {
            Assert.Throws<S2ModKitException>(() => ReadRecipe(JsonDefaults.Serialize(recipe with { SchemaVersion = version })));
            var node = JsonNode.Parse(JsonDefaults.Serialize(Recipe()))!;
            node["operations"]![0]!["localTransform"] = null;
            Assert.Throws<S2ModKitException>(() => ReadRecipe(node.ToJsonString()));
        }
        var json = JsonDefaults.Serialize(recipe);
        Assert.Throws<S2ModKitException>(() => ReadRecipe(json.Replace("\"schemaVersion\": 8", "\"schemaVersion\": 7, \"schemaVersion\": 8", StringComparison.Ordinal)));
        var unknown = JsonNode.Parse(json)!;
        unknown["operations"]![0]!["localTransform"]!["field"]!["pivot"] = new JsonObject();
        Assert.Throws<S2ModKitException>(() => ReadRecipe(unknown.ToJsonString()));
    }

    [Fact]
    public async Task EllipsoidPlanRequiresEveryFactAndBothFingerprints()
    {
        var plan = EllipsoidPlan();
        var json = JsonDefaults.Serialize(plan);
        Assert.Equal(json, JsonDefaults.Serialize(ReadPlan(json)));
        var schema = await JsonSchema.FromFileAsync(SchemaPath("mutation-plan.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(schema.Validate(json));
        foreach (var path in RequiredPaths(JsonNode.Parse(json)!["operations"]![0]!["ellipsoidTransformTarget"]!))
        {
            var node = JsonNode.Parse(json)!;
            RemoveNodePath(node["operations"]![0]!["ellipsoidTransformTarget"]!, path);
            Assert.Throws<S2ModKitException>(() => ReadPlan(node.ToJsonString()));
            Assert.NotEmpty(schema.Validate(node.ToJsonString()));
        }
        var target = plan.Operations[0].EllipsoidTransformTarget!;
        var drift = target with { LocalTransform = target.LocalTransform with { Field = target.LocalTransform.Field with { Center = new() { X = 1 } } } };
        Assert.NotEqual(target.TargetFingerprint, MutationPlanJson.ComputeEllipsoidTargetFingerprint(drift));
        var changed = plan with { Operations = [plan.Operations[0] with { EllipsoidTransformTarget = drift }] };
        Assert.NotEqual(plan.Fingerprint, MutationPlanJson.ComputeExperimentalFingerprint(changed));
        // Rehashing only the outer plan must not hide a stale target identity.
        changed = changed with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(changed) };
        Assert.Throws<S2ModKitException>(() => ReadPlan(JsonDefaults.Serialize(changed)));
        foreach (var name in new[] { "experimentalTransformTarget", "affineTransformTarget", "coupledTransformTarget" })
        {
            var node = JsonNode.Parse(json)!;
            node["operations"]![0]![name] = null;
            Assert.Throws<S2ModKitException>(() => ReadPlan(node.ToJsonString()));
            Assert.NotEmpty(schema.Validate(node.ToJsonString()));
        }
        foreach (var version in new[] { 2, 3 }) Assert.Throws<S2ModKitException>(() => ReadPlan(JsonDefaults.Serialize(plan with { SchemaVersion = version })));
        var legacy = JsonNode.Parse(JsonDefaults.Serialize(Plan()))!;
        legacy["operations"]![0]!["ellipsoidTransformTarget"] = null;
        Assert.Throws<S2ModKitException>(() => ReadPlan(legacy.ToJsonString()));
    }

    [Fact]
    public void RehashedEllipsoidPlansRejectInvalidMasksCertificatesLayoutsAndStorage()
    {
        var plan = EllipsoidPlan(); var target = plan.Operations[0].EllipsoidTransformTarget!; var buffer = target.Buffers[0];
        foreach (var invalid in new[]
        {
            target with { Certificate = target.Certificate with { Numerator = "0" + target.Certificate.Numerator } },
            target with { Certificate = target.Certificate with { Numerator = "2", Denominator = "4" } },
            target with { Certificate = target.Certificate with { LowerBound = 0.25 } },
            target with { Selector = new() { Kind = "unknown" } },
            target with { Buffers = [buffer with { PinnedVertexCount = 99 }] },
            target with { Buffers = [buffer with { ChangedPositionCount = 0 }] },
            target with { Buffers = [buffer with { ChangedFrameCount = 2 }] },
            target with { Buffers = [buffer with { PackedFrameLayout = buffer.PackedFrameLayout with { Offset = 8 } }] },
            target with { Buffers = [buffer with { OwnershipPolicy = "shared" }] },
            target with { Buffers = [buffer with { InputPositionHash = buffer.ExpectedPositionHash }] },
            target with { Buffers = [buffer with { MaximumDisplacement = 65 }] },
            target with { BoxTargets = [target.BoxTargets[0] with { ContributorCount = 0 }] },
            target with { SourceBlocks = [.. target.SourceBlocks, target.SourceBlocks[0]] },
            target with { PreservationTargets = [target.PreservationTargets[0] with { SourcePayloadHash = EllipsoidOutputHash }] },
        })
        {
            var rehashed = invalid with { TargetFingerprint = MutationPlanJson.ComputeEllipsoidTargetFingerprint(invalid) };
            var changed = plan with { Operations = [plan.Operations[0] with { EllipsoidTransformTarget = rehashed }] };
            changed = changed with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(changed) };
            Assert.Throws<S2ModKitException>(() => ReadPlan(JsonDefaults.Serialize(changed)));
        }
    }

    [Fact]
    public async Task EllipsoidEvidenceSeparatesPlannedAndObservedFactsAndUntestedConsumers()
    {
        var schema = await JsonSchema.FromFileAsync(SchemaPath("evidence.schema.json"), TestContext.Current.CancellationToken);
        var old = await JsonSchema.FromFileAsync(SchemaPath("v8/evidence.schema.json"), TestContext.Current.CancellationToken);
        foreach (var built in new[] { false, true })
        {
            var report = EllipsoidReport(built); var json = JsonDefaults.Serialize(report);
            Assert.Empty(schema.Validate(json));
            Assert.NotEmpty(old.Validate(json));
            Assert.Equal(json, JsonDefaults.Serialize(ReadEvidence(json)));
            foreach (var name in new[] { "observedBuffers", "target", "boxes", "preservedMetadata" })
            {
                var node = JsonNode.Parse(json)!;
                node["operations"]![0]!["ellipsoidTransform"]!.AsObject().Remove(name);
                Assert.Throws<S2ModKitException>(() => ReadEvidence(node.ToJsonString()));
                Assert.NotEmpty(schema.Validate(node.ToJsonString()));
            }
            var incorrectBoundary = report with { Boundaries = report.Boundaries.Select(b => b.Name == "runtime" ? b with { Status = "passed" } : b).ToArray() };
            Assert.Throws<S2ModKitException>(() => ReadEvidence(JsonDefaults.Serialize(incorrectBoundary)));
            var mixed = JsonNode.Parse(json)!;
            mixed["operations"]![0]!["experimentalTransform"] = null;
            Assert.Throws<S2ModKitException>(() => ReadEvidence(mixed.ToJsonString()));
        }
        var passed = EllipsoidReport(true); var operation = passed.Operations[0]; var visual = operation.EllipsoidTransform!;
        Assert.Throws<S2ModKitException>(() => ReadEvidence(JsonDefaults.Serialize(passed with { Operations = [operation with { EllipsoidTransform = visual with { ObservedBuffers = [visual.ObservedBuffers![0] with { PositionHash = Hash }] } }] })));
        var planned = EllipsoidReport(false);
        Assert.Throws<S2ModKitException>(() => ReadEvidence(JsonDefaults.Serialize(planned with { Operations = [planned.Operations[0] with { EllipsoidTransform = planned.Operations[0].EllipsoidTransform! with { ObservedBuffers = visual.ObservedBuffers } }] })));
        var legacy = JsonNode.Parse(JsonDefaults.Serialize(ExperimentalReport(false)))!;
        legacy["operations"]![0]!["ellipsoidTransform"] = null;
        Assert.Throws<S2ModKitException>(() => ReadEvidence(legacy.ToJsonString()));
        var unknownTarget = visual.Target with { Selector = new() { Kind = "unknown" } };
        unknownTarget = unknownTarget with { TargetFingerprint = MutationPlanJson.ComputeEllipsoidTargetFingerprint(unknownTarget) };
        Assert.Throws<S2ModKitException>(() => ReadEvidence(JsonDefaults.Serialize(passed with
        { Operations = [operation with { EllipsoidTransform = visual with { Target = unknownTarget } }] })));
    }

    internal static RecipeDocument EllipsoidRecipe() => Recipe(Operation() with
    { Version = 7, Granularity = "ellipsoid_vertices", Transform = null!, LocalTransform = EllipsoidIntent }) with
    { SchemaVersion = 8 };

    internal static MutationPlan EllipsoidPlan()
    {
        var original = Plan(); var old = original.Operations[0].ExperimentalTransformTarget!; var g = old.GeometryTargets[0];
        var certificate = EllipsoidContractValidator.Certificate(new EllipsoidScale(default, new(8, 4, 2), 1f / 16, 2, 64).Certificate);
        var b = new PlannedEllipsoidBuffer(g.Lod, g.ResourcePath, g.MeshOrdinal, g.ResourceBlockIndex, g.VertexBufferOrdinal, g.IndexBufferOrdinal,
            g.VertexResourceBlockIndex, g.IndexResourceBlockIndex, Hash, Hash, Hash, EllipsoidOutputHash, Hash, Hash, 3, "exclusive", g.PositionLayout,
            new("R32_UINT", 12, 28, "source2_normal_tangent_v2"), g.Codec, Hash, Hash, 1, 1, 1, 2, 1,
            Hash, EllipsoidOutputHash, Hash, EllipsoidOutputHash, g.BeforeBounds, g.ExpectedAfterBounds, 1);
        var target = new PlannedEllipsoidTransformTarget(Hash, Hash, "root_complete_buffer_ellipsoid_visual", 1, old.BoundsPolicyId, 1,
            old.RuntimeMetadataPolicy, old.Selector, EllipsoidIntent, certificate, 1, 64, [b],
            [old.BoxTargets[0] with { Growth = [new("volume", 8, 8, 0)] }], old.PreservationTargets, old.SourceBlocks);
        target = target with { TargetFingerprint = MutationPlanJson.ComputeEllipsoidTargetFingerprint(target) };
        var plan = original with { SchemaVersion = 4, Operations = [original.Operations[0] with { Version = 7, ExperimentalTransformTarget = null, EllipsoidTransformTarget = target }] };
        return plan with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(plan) };
    }

    private static EvidenceReport EllipsoidReport(bool built)
    {
        var plan = EllipsoidPlan(); var target = plan.Operations[0].EllipsoidTransformTarget!;
        var observations = target.Buffers.Select(b => new EllipsoidBufferObservation(b.Lod, b.MeshOrdinal, b.VertexBufferOrdinal, b.ExpectedPositionHash,
            b.ExpectedPackedFrameHash, b.ExpectedDecodedVertexBufferHash, b.MaskHash, b.WeightHash, b.MaximumDisplacement)).ToArray();
        return ExperimentalReport(built) with
        {
            SchemaVersion = 9,
            PlanFingerprint = plan.Fingerprint,
            Output = built ? new("models/test.vmdl_c", EllipsoidOutputHash, 12) : null,
            Boundaries = EllipsoidContractValidator.PlannedBoundaries().Select(b => built && b.Status == "not_applicable" ? b with { Status = "passed" } : b).ToArray(),
            Operations = [new("scale", "transform_component", 7, [plan.Operations[0].SelectedDrawCalls[0].DrawCallId], ["models/test.vmdl_c"])
            {
                EllipsoidTransform = new(target, built ? observations : null,
                    target.BoxTargets.Select(b => new ExperimentalBoxEvidence(b, built ? b.ExpectedWords : null, built ? "passed" : "planned")).ToArray(),
                    target.PreservationTargets.Select(p => new ExperimentalPreservationEvidence(p, built ? p.SourcePayloadHash : null, built ? p.OriginalWords : null, built ? "passed" : "planned")).ToArray()),
            }],
        };
    }

    private static IEnumerable<string> RequiredPaths(JsonNode node, string prefix = "")
    {
        if (node is JsonObject obj)
            foreach (var (name, value) in obj)
            {
                var path = prefix + name;
                yield return path;
                if (value is not null) foreach (var child in RequiredPaths(value, path + ".")) yield return child;
            }
        else if (node is JsonArray array)
            for (var i = 0; i < array.Count; i++)
                if (array[i] is { } child) foreach (var path in RequiredPaths(child, prefix + i + ".")) yield return path;
    }
    private static void RemoveNodePath(JsonNode node, string path)
    {
        var parts = path.Split('.');
        for (var i = 0; i < parts.Length - 1; i++) node = node is JsonArray a ? a[int.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture)]! : node[parts[i]]!;
        node.AsObject().Remove(parts[^1]);
    }
}
