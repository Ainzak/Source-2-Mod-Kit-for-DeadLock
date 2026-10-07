using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class ExperimentalVisualContractTests
{
    [Theory]
    [InlineData(1, 1, false, false, false)]
    [InlineData(2, 3, true, true, false)]
    [InlineData(16, 1, false, true, true)]
    [InlineData(1, 8, true, false, true)]
    public async Task PairedIntentAndPlanRoundTripClosedVariantsWithoutResourceAdmission(int members, int lods, bool bone, bool mixed, bool physics)
    {
        var plan = PairedPlan(members, lods, bone, mixed, physics);
        var recipe = new RecipeDocument { SchemaVersion = 11, RecipeId = plan.RecipeId, InputHash = plan.InputHash, Operations = [PairedContractValidator.Operation(plan.Operations[0])] };
        var recipeJson = JsonDefaults.Serialize(recipe); var planJson = JsonDefaults.Serialize(plan);
        Assert.Equal(recipeJson, JsonDefaults.Serialize(ReadRecipe(recipeJson))); Assert.Equal(planJson, JsonDefaults.Serialize(ReadPlan(planJson)));
        var recipeSchema = await JsonSchema.FromFileAsync(SchemaPath("recipe.schema.json"), TestContext.Current.CancellationToken);
        var planSchema = await JsonSchema.FromFileAsync(SchemaPath("mutation-plan.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(recipeSchema.Validate(recipeJson)); Assert.Empty(planSchema.Validate(planJson));
        var oldRecipe = await JsonSchema.FromFileAsync(SchemaPath("v10/recipe.schema.json"), TestContext.Current.CancellationToken);
        var oldPlan = await JsonSchema.FromFileAsync(SchemaPath("v6/mutation-plan.schema.json"), TestContext.Current.CancellationToken);
        Assert.NotEmpty(oldRecipe.Validate(recipeJson)); Assert.NotEmpty(oldPlan.Validate(planJson));
    }

    [Fact]
    public async Task PairedOptionsRoundTripRequireExplicitCompleteSourceBoundIntent()
    {
        var options = PairedOptions(); var json = PairedFieldOptionsJson.Write(options);
        Assert.Equal(json, PairedFieldOptionsJson.Write(PairedFieldOptionsJson.Read(Encoding.UTF8.GetBytes(json))));
        var schema = await JsonSchema.FromFileAsync(SchemaPath("paired-field-options.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(schema.Validate(json));
        foreach (var path in RequiredPaths(JsonNode.Parse(json)!))
        {
            var node = JsonNode.Parse(json)!; RemoveNodePath(node, path);
            Assert.NotEmpty(schema.Validate(node.ToJsonString()));
            Assert.Throws<S2ModKitException>(() => PairedFieldOptionsJson.Read(Encoding.UTF8.GetBytes(node.ToJsonString())));
        }
    }

    [Fact]
    public async Task PairedSchemasAndReadersRejectEveryMissingNestedSemanticFact()
    {
        var documents = new[] { (File: "recipe.schema.json", Json: JsonDefaults.Serialize(PairedRecipe()), Property: "pairedTransform"),
            (File: "mutation-plan.schema.json", Json: JsonDefaults.Serialize(PairedPlan(physics: true)), Property: "pairedTransformTarget"),
            (File: "evidence.schema.json", Json: JsonDefaults.Serialize(PairedReport(true, true, true)), Property: "pairedTransform") };
        foreach (var doc in documents)
        {
            var schema = await JsonSchema.FromFileAsync(SchemaPath(doc.File), TestContext.Current.CancellationToken);
            Assert.Empty(schema.Validate(doc.Json));
            foreach (var path in RequiredPaths(JsonNode.Parse(doc.Json)!["operations"]![0]![doc.Property]!))
            {
                var node = JsonNode.Parse(doc.Json)!; RemoveNodePath(node["operations"]![0]![doc.Property]!, path);
                Assert.NotEmpty(schema.Validate(node.ToJsonString()));
                Assert.Throws<S2ModKitException>(() => ReadDirectionalDocument(doc.File, node.ToJsonString()));
            }
        }
    }

    [Theory]
    [InlineData("transform")]
    [InlineData("directionalTransform")]
    [InlineData("coordinatedTransform")]
    [InlineData("region")]
    [InlineData("localTransform")]
    [InlineData("physicsPolicy")]
    [InlineData("connectedComponentIdsByLod")]
    public async Task PairedRecipesRejectOlderOperationPropertiesEvenWhenNull(string property)
    {
        var node = JsonNode.Parse(JsonDefaults.Serialize(PairedRecipe()))!; node["operations"]![0]![property] = null;
        Assert.Throws<S2ModKitException>(() => ReadRecipe(node.ToJsonString()));
        var schema = await JsonSchema.FromFileAsync(SchemaPath("recipe.schema.json"), TestContext.Current.CancellationToken);
        Assert.NotEmpty(schema.Validate(node.ToJsonString()));
    }

    [Fact]
    public void PairedRecipesRejectOverlapIdentityPartnersImplicitPivotsBadPoliciesAndPartialLods()
    {
        var recipe = PairedRecipe(2, 2); var op = (TransformComponentOperation)recipe.Operations[0]; var pair = op.PairedTransform!;
        var right = pair.Fields[1];
        var invalid = new[]
        {
            op with { PairedTransform = pair with { Fields = [pair.Fields[0]] } },
            op with { PairedTransform = pair with { Fields = pair.Fields.Reverse().ToArray() } },
            op with { PairedTransform = pair with { Fields = [pair.Fields[0], right with { FieldId = pair.Fields[0].FieldId }] } },
            op with { PairedTransform = pair with { Fields = [pair.Fields[0], right with { Field = right.Field with { Pivot = new() { Kind = "explicit_point", Point = new() { Y = -10 } } } }] } },
            op with { PairedTransform = pair with { Fields = [pair.Fields[0], right with { Field = right.Field with { Scale = new() { X = 1, Y = 1, Z = 1 } } }] } },
            op with { PairedTransform = pair with { Fields = [pair.Fields[0], right with { Field = right.Field with { Pivot = new() { Kind = "selection_bounds_center", ReferenceLod = 0 } } }] } },
            op with { PairedTransform = pair with { Members = [pair.Members[0] with { Lods = [pair.Members[0].Lods[0]] }, pair.Members[1]] } },
            op with { SourceTrianglePolicy = new("repair", 1) }, op with { ProceduralInputPolicy = new("simulation_independent", 1) },
            op with { RuntimeMetadataPolicy = new("verified", 1) }, op with { ZeroBoneBoxPolicy = new("preserve_all_zero_single_contributor_unverified", 1) },
            op with { ZeroRenderSpherePolicy = new("preserve_zero_render_sphere_with_zero_box_unverified", 1) },
            op with { Limits = new() { MaximumVertexDisplacement = 65 } }
        };
        foreach (var operation in invalid) Assert.Throws<S2ModKitException>(() => ReadRecipe(Raw(recipe with { Operations = [operation] })));
        var touching = recipe with { Operations = [op with { PairedTransform = pair with { Fields = [pair.Fields[0], right with { Field = right.Field with { Pivot = new() { Kind = "explicit_point", Point = new() } } }] } }] };
        var admittedIntent = ReadRecipe(Raw(touching));
        Assert.Equal("0", PairedContractValidator.Separation(((TransformComponentOperation)admittedIntent.Operations[0]).PairedTransform!.Fields).Gap.Numerator);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public async Task PairedEvidenceSeparatesPlansObservationsAndUnverifiedConsumers(bool built, bool bone, bool physics)
    {
        var report = PairedReport(built, bone, physics); var json = JsonDefaults.Serialize(report);
        Assert.Equal(json, JsonDefaults.Serialize(ReadEvidence(json)));
        var schema = await JsonSchema.FromFileAsync(SchemaPath("evidence.schema.json"), TestContext.Current.CancellationToken); Assert.Empty(schema.Validate(json));
        var old = await JsonSchema.FromFileAsync(SchemaPath("v11/evidence.schema.json"), TestContext.Current.CancellationToken); Assert.NotEmpty(old.Validate(json));
        foreach (var boundary in report.Boundaries)
        {
            Assert.Throws<S2ModKitException>(() => ReadEvidence(Raw(report with { Boundaries = report.Boundaries.Where(b => b != boundary).ToArray() })));
            Assert.Throws<S2ModKitException>(() => ReadEvidence(Raw(report with { Boundaries = report.Boundaries.Select(b => b == boundary ? b with { Status = b.Status == "passed" ? "untested" : "passed" } : b).ToArray() })));
        }
        Assert.All(report.Boundaries.Where(b => b.Name is "source_face_harmlessness" or "procedural_simulation" or "culling"), b => Assert.Equal("untested", b.Status));
    }

    [Fact]
    public async Task PairedContractsRejectNullUnknownDuplicateAndMixedNestedFacts()
    {
        foreach (var doc in new[] { (File: "recipe.schema.json", Json: JsonDefaults.Serialize(PairedRecipe()), Property: "pairedTransform"),
            (File: "mutation-plan.schema.json", Json: JsonDefaults.Serialize(PairedPlan()), Property: "pairedTransformTarget"),
            (File: "evidence.schema.json", Json: JsonDefaults.Serialize(PairedReport(true)), Property: "pairedTransform") })
        {
            var schema = await JsonSchema.FromFileAsync(SchemaPath(doc.File), TestContext.Current.CancellationToken);
            var node = JsonNode.Parse(doc.Json)!; node["operations"]![0]![doc.Property]!["guessedAnatomy"] = true;
            Assert.NotEmpty(schema.Validate(node.ToJsonString())); Assert.Throws<S2ModKitException>(() => ReadDirectionalDocument(doc.File, node.ToJsonString()));
            node = JsonNode.Parse(doc.Json)!; node["operations"]![0]![doc.Property] = null;
            Assert.NotEmpty(schema.Validate(node.ToJsonString())); Assert.Throws<S2ModKitException>(() => ReadDirectionalDocument(doc.File, node.ToJsonString()));
            var version = JsonNode.Parse(doc.Json)!["schemaVersion"]!.GetValue<int>();
            var compact = JsonNode.Parse(doc.Json)!.ToJsonString();
            var duplicateVersion = compact.Replace($"\"schemaVersion\":{version}", $"\"schemaVersion\":999,\"schemaVersion\":{version}", StringComparison.Ordinal);
            var duplicateField = compact.Replace("\"fieldId\":\"left\"", "\"fieldId\":\"bad\",\"fieldId\":\"left\"", StringComparison.Ordinal);
            Assert.NotEqual(compact, duplicateVersion); Assert.NotEqual(compact, duplicateField);
            Assert.Throws<S2ModKitException>(() => ReadDirectionalDocument(doc.File, duplicateVersion));
            Assert.Throws<S2ModKitException>(() => ReadDirectionalDocument(doc.File, duplicateField));
        }
        var recipe = JsonNode.Parse(JsonDefaults.Serialize(PairedRecipe()))!;
        var recipeSchema = await JsonSchema.FromFileAsync(SchemaPath("recipe.schema.json"), TestContext.Current.CancellationToken);
        foreach (var path in RequiredPaths(recipe["operations"]![0]!["pairedTransform"]!))
        {
            var node = JsonNode.Parse(JsonDefaults.Serialize(PairedRecipe()))!; var target = node["operations"]![0]!["pairedTransform"]!;
            SetNodePathNull(target, path);
            Assert.NotEmpty(recipeSchema.Validate(node.ToJsonString()));
            Assert.Throws<S2ModKitException>(() => ReadRecipe(node.ToJsonString()));
        }
    }

    [Fact]
    public void EarlierCanonicalContractsRejectPairedPropertiesAndKeepTheirIdentities()
    {
        foreach (var recipe in new[] { Recipe(), EllipsoidRecipe(), CoordinatedRecipe(), DirectionalRecipe() })
        {
            var json = JsonDefaults.Serialize(recipe); Assert.Equal(json, JsonDefaults.Serialize(ReadRecipe(json)));
            foreach (var property in new[] { "pairedTransform", "sourceTrianglePolicy", "proceduralInputPolicy" })
            {
                var node = JsonNode.Parse(json)!; node["operations"]![0]![property] = null;
                Assert.Throws<S2ModKitException>(() => ReadRecipe(node.ToJsonString()));
            }
            var op = (TransformComponentOperation)recipe.Operations[0];
            Assert.Throws<S2ModKitException>(() => JsonDefaults.Serialize(recipe with { Operations = [op with { PairedTransform = ((TransformComponentOperation)PairedRecipe().Operations[0]).PairedTransform }] }));
        }
        foreach (var plan in new[] { Plan(), EllipsoidPlan(), CoordinatedPlan(), DirectionalPlan() })
        {
            var json = JsonDefaults.Serialize(plan); Assert.Equal(json, JsonDefaults.Serialize(ReadPlan(json)));
            Assert.Equal(plan.Fingerprint, MutationPlanJson.ComputeExperimentalFingerprint(plan));
            var node = JsonNode.Parse(json)!; node["operations"]![0]!["pairedTransformTarget"] = null;
            Assert.Throws<S2ModKitException>(() => ReadPlan(node.ToJsonString()));
        }
        foreach (var report in new[] { ExperimentalReport(true), EllipsoidReport(true), CoordinatedReport(true), DirectionalReport(true) })
        {
            var json = JsonDefaults.Serialize(report); Assert.Equal(json, JsonDefaults.Serialize(ReadEvidence(json)));
            var node = JsonNode.Parse(json)!; node["operations"]![0]!["pairedTransform"] = null;
            Assert.Throws<S2ModKitException>(() => ReadEvidence(node.ToJsonString()));
        }
    }

    private static void SetNodePathNull(JsonNode node, string path)
    {
        var parts = path.Split('.');
        for (var i = 0; i < parts.Length - 1; i++) node = node is JsonArray a ? a[int.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture)]! : node[parts[i]]!;
        node[parts[^1]] = null;
    }
}
