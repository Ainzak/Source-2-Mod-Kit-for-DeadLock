using System.Text;
using System.Text.Json.Nodes;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class ExperimentalVisualContractTests
{
    private static readonly ContentHash Hash = ContentHash.Compute("synthetic-experimental"u8);

    [Fact]
    public async Task ExplicitRecipeRoundTripsAndMatchesSchema()
    {
        var recipe = Recipe();
        RecipeValidator.Validate(recipe);
        var json = JsonDefaults.Serialize(recipe);
        var parsed = ReadRecipe(json);
        RecipeValidator.Validate(parsed);
        Assert.Equal(json, JsonDefaults.Serialize(parsed));
        var schema = await JsonSchema.FromFileAsync(SchemaPath("recipe.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(schema.Validate(json));
        var legacy = await JsonSchema.FromFileAsync(SchemaPath("v5/recipe.schema.json"), TestContext.Current.CancellationToken);
        Assert.NotEmpty(legacy.Validate(json));
    }

    [Fact]
    public void AllTypedPivotsAreAdmittedWithoutAffineFields()
    {
        TransformPivot[] pivots =
        [
            new() { Kind = "selection_bounds_center", ReferenceLod = 0 },
            new() { Kind = "explicit_point", Point = new() { X = 1 } },
            new() { Kind = "bounds_face", ReferenceLod = 0, Face = "max_y" },
            new() { Kind = "bone_origin", ReferenceLod = 0, BoneName = "attachment" },
        ];
        foreach (var pivot in pivots)
        {
            var operation = Operation();
            RecipeValidator.Validate(Recipe(operation with { Transform = operation.Transform with { Pivot = pivot } }));
        }
    }

    [Fact]
    public void MissingAndUnknownAcknowledgementsReject()
    {
        foreach (var policy in new RuntimeMetadataPolicy?[] { null, new("preserve_unverified", 0), new("preserve_unverified", 2), new("verified", 1) })
        {
            Assert.Throws<S2ModKitException>(() => RecipeValidator.Validate(Recipe(Operation() with { RuntimeMetadataPolicy = policy })));
        }
    }

    [Fact]
    public void ExperimentalRecipesCannotMixOperationsOrUseLegacySchemas()
    {
        foreach (var version in Enumerable.Range(1, 5)) Assert.Throws<S2ModKitException>(() => RecipeValidator.Validate(Recipe() with { SchemaVersion = version }));
        Assert.Throws<S2ModKitException>(() => RecipeValidator.Validate(Recipe() with { Operations = [Operation(), Operation() with { OperationId = "other" }] }));
        Assert.Throws<S2ModKitException>(() => RecipeValidator.Validate(Recipe() with { Operations = [new RemoveComponentOperation()] }));
        Assert.Throws<S2ModKitException>(() => RecipeValidator.Validate(Recipe(Operation() with { Granularity = "connected_component_vertices" })));
    }

    [Fact]
    public void ExperimentalIntentLimitsRejectUnsafeOrIgnoredFields()
    {
        var operation = Operation();
        foreach (var scale in new[] { 0f, 0.49f, 1f, 2.01f, float.NaN, float.PositiveInfinity })
            Assert.Throws<S2ModKitException>(() => RecipeValidator.Validate(Recipe(operation with { Transform = operation.Transform with { UniformScale = scale } })));
        foreach (var limit in new[] { 0f, -1f, 64.01f, float.NaN, float.PositiveInfinity })
            Assert.Throws<S2ModKitException>(() => RecipeValidator.Validate(Recipe(operation with { Limits = new() { MaximumVertexDisplacement = limit } })));
        TransformComponentOperation[] unsupported =
        [
            operation with { Transform = operation.Transform with { Translation = new() { X = 1 } } },
            operation with { Transform = operation.Transform with { Scale = new() { X = 1, Y = 1, Z = 1 } } },
            operation with { Transform = operation.Transform with { Frame = new() { Kind = "model" } } },
            operation with { Transform = operation.Transform with { Rotation = new() { Kind = "identity" } } },
            operation with { PhysicsPolicy = "transform_coupled_convex" },
            operation with { Limits = new() { MaximumVertexDisplacement = 64, MaximumCollisionDisplacement = 1 } },
            operation with { ConnectedComponentIdsByLod = new Dictionary<string, IReadOnlyList<string>>() },
        ];
        foreach (var invalid in unsupported) Assert.Throws<S2ModKitException>(() => RecipeValidator.Validate(Recipe(invalid)));
        RecipeValidator.Validate(Recipe(operation with { Transform = operation.Transform with { UniformScale = 0.5f } }));
        RecipeValidator.Validate(Recipe(operation with { Transform = operation.Transform with { UniformScale = 2f } }));
    }

    [Fact]
    public async Task SchemaAndReaderRejectMissingRequiredFieldsInsteadOfDefaulting()
    {
        var schema = await JsonSchema.FromFileAsync(SchemaPath("recipe.schema.json"), TestContext.Current.CancellationToken);
        foreach (var path in new[] { "runtimeMetadataPolicy", "transform.translation", "transform.translation.x", "limits.maximumVertexDisplacement", "ownershipPolicy", "granularity", "lodPolicy" })
        {
            var node = JsonNode.Parse(JsonDefaults.Serialize(Recipe()))!;
            RemovePath(node["operations"]![0]!.AsObject(), path);
            var json = node.ToJsonString();
            Assert.NotEmpty(schema.Validate(json));
            Assert.Throws<S2ModKitException>(() => ReadRecipe(json));
        }

        foreach (var name in new[] { "kind", "version" })
        {
            var node = JsonNode.Parse(JsonDefaults.Serialize(Recipe()))!;
            node["operations"]![0]!["runtimeMetadataPolicy"]!.AsObject().Remove(name);
            Assert.Throws<S2ModKitException>(() => ReadRecipe(node.ToJsonString()));
        }
    }

    [Fact]
    public async Task SchemaRejectsUnknownPolicyAndExperimentalVersionInLegacyEnvelope()
    {
        var schema = await JsonSchema.FromFileAsync(SchemaPath("recipe.schema.json"), TestContext.Current.CancellationToken);
        var json = JsonDefaults.Serialize(Recipe());
        Assert.NotEmpty(schema.Validate(json.Replace("\"preserve_unverified\"", "\"verified\"", StringComparison.Ordinal)));
        Assert.NotEmpty(schema.Validate(json.Replace("\"schemaVersion\": 6", "\"schemaVersion\": 5", StringComparison.Ordinal)));
        Assert.NotEmpty(schema.Validate(json.Replace("\"uniformScale\": 1.5", "\"uniformScale\": 1", StringComparison.Ordinal)));
    }

    [Fact]
    public void LegacyRecipesAndPlansRetainTheirWireShape()
    {
        var operation = Operation() with { Version = 1, Granularity = "draw_call_owned_vertices", RuntimeMetadataPolicy = null };
        var recipe = Recipe(operation) with { SchemaVersion = 2 };
        RecipeValidator.Validate(recipe);
        var json = JsonDefaults.Serialize(recipe);
        Assert.DoesNotContain("runtimeMetadataPolicy", json, StringComparison.Ordinal);
        Assert.Equal(json, JsonDefaults.Serialize(ReadRecipe(json)));
        var smuggledNull = JsonNode.Parse(json)!;
        smuggledNull["operations"]![0]!["runtimeMetadataPolicy"] = null;
        Assert.Throws<S2ModKitException>(() => ReadRecipe(smuggledNull.ToJsonString()));
        Assert.Throws<S2ModKitException>(() => RecipeValidator.Validate(recipe with { Operations = [operation with { RuntimeMetadataPolicy = new("preserve_unverified", 1) }] }));
        var legacy = new MutationPlan("legacy", Hash, Hash, [new("remove", "remove_component", 1, [], [])]);
        var saved = JsonDefaults.Serialize(legacy);
        Assert.DoesNotContain("schemaVersion", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("experimentalTransformTarget", saved, StringComparison.Ordinal);
        Assert.Equal(saved, JsonDefaults.Serialize(ReadPlan(saved)));
    }

    [Fact]
    public void ExperimentalPlanRoundTripsWithTypedFactsAndRecomputableFingerprint()
    {
        var plan = Plan();
        var json = JsonDefaults.Serialize(plan);
        var parsed = ReadPlan(json);
        Assert.Equal(json, JsonDefaults.Serialize(parsed));
        Assert.Equal(plan.Fingerprint, MutationPlanJson.ComputeExperimentalFingerprint(parsed));
        Assert.Equal("root_sphere", parsed.Operations.Single().ExperimentalTransformTarget!.PreservationTargets[0].Category);
    }

    [Fact]
    public void NullUnknownAndMissingPlanVersionsCannotRouteExperimentalTargets()
    {
        foreach (var version in new JsonNode?[] { null, JsonValue.Create(1), JsonValue.Create(3), JsonValue.Create("2") })
        {
            var node = JsonNode.Parse(JsonDefaults.Serialize(Plan()))!.AsObject();
            node["schemaVersion"] = version?.DeepClone();
            Assert.Throws<S2ModKitException>(() => ReadPlan(node.ToJsonString()));
        }

        var missing = JsonNode.Parse(JsonDefaults.Serialize(Plan()))!.AsObject();
        missing.Remove("schemaVersion");
        Assert.Throws<S2ModKitException>(() => ReadPlan(missing.ToJsonString()));
        var legacy = JsonNode.Parse(JsonDefaults.Serialize(new MutationPlan("legacy", Hash, Hash, [new("remove", "remove_component", 1, [], [])])))!;
        legacy["operations"]![0]!["experimentalTransformTarget"] = null;
        Assert.Throws<S2ModKitException>(() => ReadPlan(legacy.ToJsonString()));
    }

    [Fact]
    public void PlanRequiredFactsCannotBeDroppedOrNull()
    {
        foreach (var name in new[] { "runtimeMetadataPolicy", "sourceBlocks", "boxTargets", "preservationTargets", "pivot", "selector", "displacementLimit" })
        {
            foreach (var remove in new[] { true, false })
            {
                var node = JsonNode.Parse(JsonDefaults.Serialize(Plan()))!;
                var target = node["operations"]![0]!["experimentalTransformTarget"]!.AsObject();
                if (remove) target.Remove(name); else target[name] = null;
                Assert.Throws<S2ModKitException>(() => ReadPlan(node.ToJsonString()));
            }
        }
    }

    [Fact]
    public void EveryPreservationAndBoxFactEntersTheFingerprint()
    {
        foreach (var path in new[] { "uniformScale", "boxTargets.0.expectedWords.0", "boxTargets.0.contributorCount", "preservationTargets.0.originalWords.0" })
        {
            var node = JsonNode.Parse(JsonDefaults.Serialize(Plan()))!;
            var target = node["operations"]![0]!["experimentalTransformTarget"]!;
            var parts = path.Split('.');
            foreach (var part in parts[..^1]) target = int.TryParse(part, out var index) ? target[index]! : target[part]!;
            var last = parts[^1];
            JsonNode changed = last == "sourcePayloadHash" ? JsonValue.Create(ContentHash.Compute("drift"u8).Value)! : JsonValue.Create(2)!;
            if (int.TryParse(last, out var arrayIndex)) target[arrayIndex] = changed; else target[last] = changed;
            Assert.Equal("PLAN_FINGERPRINT_DRIFT", Assert.Throws<S2ModKitException>(() => ReadPlan(node.ToJsonString())).Error.Code);
        }
    }

    [Fact]
    public void DuplicatedSourcesMissingItemsAndInvalidNumericalTargetsRejectEvenWithRehashedPlan()
    {
        var plan = Plan();
        var operation = plan.Operations.Single();
        var target = operation.ExperimentalTransformTarget!;
        PlannedExperimentalTransformTarget[] invalid =
        [
            target with { SourceBlocks = [.. target.SourceBlocks, target.SourceBlocks[0]] },
            target with { BoxTargets = [target.BoxTargets[0], target.BoxTargets[0]] },
            target with { PreservationTargets = [target.PreservationTargets[0], target.PreservationTargets[0]] },
            target with { GeometryTargets = [target.GeometryTargets[0], target.GeometryTargets[0]] },
            target with { GeometryTargets = [null!] },
            target with { BoxTargets = [target.BoxTargets[0] with { OriginalWords = new uint[6] }] },
            target with { BoxTargets = [target.BoxTargets[0] with { ExpectedWords = [0x7f800000, 0, 0, 0, 0, 0] }] },
            target with { PreservationTargets = [target.PreservationTargets[0] with { OriginalWords = [0] }] },
            target with { PreservationTargets = [target.PreservationTargets[0] with { SourcePayloadHash = ContentHash.Compute("drift"u8) }] },
        ];
        foreach (var invalidTarget in invalid)
        {
            var changed = plan with { Operations = [operation with { ExperimentalTransformTarget = invalidTarget }] };
            changed = changed with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(changed) };
            Assert.Throws<S2ModKitException>(() => ReadPlan(JsonDefaults.Serialize(changed)));
        }

        var nullSelection = plan with { Operations = [operation with { SelectedDrawCalls = [null!] }] };
        nullSelection = nullSelection with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(nullSelection) };
        Assert.Throws<S2ModKitException>(() => ReadPlan(JsonDefaults.Serialize(nullSelection)));
    }

    [Fact]
    public void UnknownMembersAndDuplicateDiscriminatorsReject()
    {
        var json = JsonDefaults.Serialize(Plan());
        Assert.Throws<S2ModKitException>(() => ReadPlan(json.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 2, \"schemaVersion\": 2", StringComparison.Ordinal)));
        var node = JsonNode.Parse(json)!;
        node["operations"]![0]!["experimentalTransformTarget"]!["unknown"] = true;
        Assert.Throws<S2ModKitException>(() => ReadPlan(node.ToJsonString()));
        var recipe = JsonDefaults.Serialize(Recipe()).Replace("\"schemaVersion\": 6", "\"schemaVersion\": 6, \"schemaVersion\": 5", StringComparison.Ordinal);
        Assert.Throws<S2ModKitException>(() => ReadRecipe(recipe));
    }

    [Fact]
    public void MachinePathsAndTraversalCannotEnterExperimentalPlanIdentities()
    {
        foreach (var path in new[] { "h:/local/model.vmdl_c", "/models/test.vmdl_c", "models/../test.vmdl_c", "models//test.vmdl_c", "Models/test.vmdl_c" })
        {
            var changed = Plan() with { Inputs = [new(path, Hash, 12)] };
            changed = changed with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(changed) };
            Assert.Throws<S2ModKitException>(() => ReadPlan(JsonDefaults.Serialize(changed)));
        }
    }

    private static RecipeDocument Recipe(TransformComponentOperation? operation = null) => new()
    {
        SchemaVersion = 6,
        RecipeId = "visual",
        InputHash = Hash,
        Operations = [operation ?? Operation()],
    };

    private static TransformComponentOperation Operation() => new()
    {
        OperationId = "scale",
        Version = 5,
        Granularity = "draw_call_vertices",
        Selector = new() { Kind = "material_exact", MaterialPath = "materials/part.vmat" },
        ExpectedMatchesByLod = new Dictionary<string, int> { ["0"] = 1 },
        ExpectedVerticesByLod = new Dictionary<string, int> { ["0"] = 3 },
        RuntimeMetadataPolicy = new("preserve_unverified", 1),
        Transform = new() { Pivot = new() { Kind = "selection_bounds_center", ReferenceLod = 0 }, UniformScale = 1.5f },
        Limits = new() { MaximumVertexDisplacement = 64 },
    };

    private static MutationPlan Plan()
    {
        var bounds = new GeometryBounds(new() { X = -1, Y = -1, Z = -1 }, new() { X = 1, Y = 1, Z = 1 });
        var codec = new GeometryCodecIdentity("synthetic", "test", "portable", Hash, "1");
        var geometry = new PlannedGeometryTarget(0, "models/test.vmdl_c", 0, 0, 0, 0, 1, 2, Hash, Hash, Hash, Hash, Hash, Hash,
            3, new("R32G32B32_FLOAT", 0, 28), bounds, bounds, new(), 1.5f, new(), 1f, ["POSITION"], codec);
        uint[] boxWords = [0xbf800000, 0xbf800000, 0xbf800000, 0x3f800000, 0x3f800000, 0x3f800000];
        var box = new PlannedExperimentalBoxTarget(0, "scene.0.bounds", "min_max", "model", Hash, new uint[12], Hash, 3, boxWords, boxWords.ToArray(), []);
        var preserved = new PlannedExperimentalPreservationTarget(3, "root.bone.0.sphere", "root_sphere", "affected", "preserve_unverified", Hash, [0x3f800000]);
        var target = new PlannedExperimentalTransformTarget("root_complete_buffer_visual_uniform", 1, "retain_expand_boxes_preserve_runtime", 1,
            new("preserve_unverified", 1), Operation().Selector, Operation().Transform.Pivot,
            new("selection_bounds_center", new(), "model", "selection", Hash, 0), 1.5f, 1f, 64f, [geometry], [box], [preserved],
            [new(0, "MDAT", Hash), new(1, "MVTX", Hash), new(2, "MIDX", Hash), new(3, "DATA", Hash)]);
        var selected = new SelectedDrawCall(0, "models/test.vmdl_c", 0, 0, "dc_000000000000000000000000", "materials/part.vmat", 0, 0, 3);
        var plan = new MutationPlan("visual", Hash, Hash, [new("scale", "transform_component", 5, [selected], [new(0, "MDAT", Hash), new(1, "MVTX", Hash)])
        { ExperimentalTransformTarget = target }])
        { SchemaVersion = 2, Inputs = [new("models/test.vmdl_c", Hash, 12)] };
        return plan with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(plan) };
    }

    private static RecipeDocument ReadRecipe(string json) => JsonDefaults.Deserialize<RecipeDocument>(Encoding.UTF8.GetBytes(json), "Recipe");
    private static MutationPlan ReadPlan(string json) => JsonDefaults.Deserialize<MutationPlan>(Encoding.UTF8.GetBytes(json), "Plan");

    private static void RemovePath(JsonObject value, string path)
    {
        var parts = path.Split('.');
        foreach (var part in parts[..^1]) value = value[part]!.AsObject();
        value.Remove(parts[^1]);
    }

    private static string SchemaPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "schemas", fileName);
    }
}
