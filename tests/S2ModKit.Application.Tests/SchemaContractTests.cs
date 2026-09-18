using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class SchemaContractTests
{
    [Fact]
    public async Task GuidedSessionVersionOneSchemaAcceptsInitialCheckpointAndRejectsUnknownProperties()
    {
        var schema = await JsonSchema.FromFileAsync(GetSchemaPath("guided-session.schema.json"), TestContext.Current.CancellationToken);
        var session = GuidedWorkflow.CreateSession(
            "C:/workspace/catalogue.json",
            [new GuidedSourceOption("source-1", GuidedWorkflowContract.BaseVpkSource, "Base game", "C:/game/pak01_dir.vpk")],
            expert: false);
        var json = JsonDefaults.Serialize(session);

        Assert.Empty(schema.Validate(json));
        Assert.NotEmpty(schema.Validate(json.Replace("\"expert\": false", "\"expert\": false, \"unexpected\": true", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RecipeVersionOneSchemaAcceptsRemoveAndRejectsUnknownProperties()
    {
        var schema = await JsonSchema.FromFileAsync(GetSchemaPath("v1/recipe.schema.json"), TestContext.Current.CancellationToken);
        const string valid = """
            {
              "schemaVersion": 1,
              "recipeId": "remove-accessory",
              "inputHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "operations": [
                {
                  "operationId": "remove-accessory",
                  "kind": "remove_component",
                  "version": 1,
                  "granularity": "draw_call",
                  "selector": { "kind": "material_exact", "materialPath": "materials/accessory.vmat" },
                  "lodPolicy": "all_present",
                  "expectedMatchesByLod": { "0": 1, "1": 1, "2": 1 },
                  "extensions": {}
                }
              ],
              "extensions": {}
            }
            """;
        const string invalid = """
            {
              "schemaVersion": 1,
              "recipeId": "remove-accessory",
              "inputHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "operations": [],
              "unexpected": true,
              "extensions": {}
            }
            """;

        Assert.Empty(schema.Validate(valid));
        Assert.NotEmpty(schema.Validate(invalid));

        var document = JsonDefaults.Deserialize<RecipeDocument>(System.Text.Encoding.UTF8.GetBytes(valid), "Recipe");
        RecipeValidator.Validate(document);
        Assert.IsType<RemoveComponentOperation>(document.Operations.Single());
    }

    [Fact]
    public async Task RecipeVersionTwoSchemaAcceptsTransformAndRejectsIdentity()
    {
        var schema = await JsonSchema.FromFileAsync(GetSchemaPath("v2/recipe.schema.json"), TestContext.Current.CancellationToken);
        const string valid = """
            {
              "schemaVersion": 2,
              "recipeId": "scale-accessory",
              "inputHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "operations": [
                {
                  "operationId": "scale-accessory",
                  "kind": "transform_component",
                  "version": 1,
                  "granularity": "draw_call_owned_vertices",
                  "selector": { "kind": "material_exact", "materialPath": "materials/accessory.vmat" },
                  "lodPolicy": "all_present",
                  "expectedMatchesByLod": { "0": 1, "1": 1 },
                  "expectedVerticesByLod": { "0": 12, "1": 8 },
                  "ownershipPolicy": "exclusive",
                  "transform": {
                    "pivot": { "kind": "selection_bounds_center", "referenceLod": 0 },
                    "uniformScale": 1.2,
                    "translation": { "x": 0.0, "y": 0.0, "z": 0.0 }
                  },
                  "limits": { "maximumVertexDisplacement": 32.0 },
                  "extensions": {}
                }
              ],
              "extensions": {}
            }
            """;
        const string identity = """
            {
              "schemaVersion": 2,
              "recipeId": "scale-accessory",
              "inputHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "operations": [
                {
                  "operationId": "scale-accessory",
                  "kind": "transform_component",
                  "version": 1,
                  "granularity": "draw_call_owned_vertices",
                  "selector": { "kind": "material_exact", "materialPath": "materials/accessory.vmat" },
                  "lodPolicy": "all_present",
                  "expectedMatchesByLod": { "0": 1 },
                  "expectedVerticesByLod": { "0": 12 },
                  "ownershipPolicy": "exclusive",
                  "transform": {
                    "pivot": { "kind": "selection_bounds_center", "referenceLod": 0 },
                    "uniformScale": 1.0,
                    "translation": { "x": 0.0, "y": 0.0, "z": 0.0 }
                  },
                  "limits": { "maximumVertexDisplacement": 32.0 },
                  "extensions": {}
                }
              ],
              "extensions": {}
            }
            """;

        Assert.Empty(schema.Validate(valid));
        Assert.NotEmpty(schema.Validate(identity));

        var document = JsonDefaults.Deserialize<RecipeDocument>(System.Text.Encoding.UTF8.GetBytes(valid), "Recipe");
        RecipeValidator.Validate(document);
        Assert.IsType<TransformComponentOperation>(document.Operations.Single());
    }

    [Fact]
    public void RecipeDeserializerRejectsUnknownOperationDiscriminator()
    {
        const string unknown = """
            {
              "schemaVersion": 2,
              "recipeId": "unknown-operation",
              "inputHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "operations": [{ "kind": "unknown_operation" }],
              "extensions": {}
            }
            """;

        var exception = Assert.Throws<S2ModKitException>(
            () => JsonDefaults.Deserialize<RecipeDocument>(System.Text.Encoding.UTF8.GetBytes(unknown), "Recipe"));

        Assert.Equal("JSON_INVALID", exception.Error.Code);
    }

    [Fact]
    public async Task RecipeVersionThreeAcceptsOnlyAtomicCoupledScaleContract()
    {
        const string valid = """
            {
              "schemaVersion": 3,
              "recipeId": "scale-coupled-accessory",
              "inputHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "operations": [{
                "operationId": "scale-coupled-accessory",
                "kind": "transform_component",
                "version": 2,
                "granularity": "draw_call_owned_vertices",
                "selector": { "kind": "draw_call_ids", "drawCallIds": ["dc_0123456789abcdef01234567"] },
                "lodPolicy": "all_present",
                "expectedMatchesByLod": { "0": 1 },
                "expectedVerticesByLod": { "0": 24 },
                "ownershipPolicy": "exclusive",
                "physicsPolicy": "transform_coupled_convex",
                "transform": {
                  "pivot": { "kind": "selection_bounds_center", "referenceLod": 0 },
                  "uniformScale": 2.0,
                  "translation": { "x": 0.0, "y": 0.0, "z": 0.0 }
                },
                "limits": { "maximumVertexDisplacement": 96.0, "maximumCollisionDisplacement": 96.0 },
                "extensions": {}
              }],
              "extensions": {}
            }
            """;
        var schema = await JsonSchema.FromFileAsync(GetSchemaPath("v3/recipe.schema.json"), TestContext.Current.CancellationToken);

        Assert.Empty(schema.Validate(valid));
        var document = JsonDefaults.Deserialize<RecipeDocument>(System.Text.Encoding.UTF8.GetBytes(valid), "Recipe");
        RecipeValidator.Validate(document);
        Assert.Equal(2, document.Operations.Single().Version);

        Assert.NotEmpty(schema.Validate(valid.Replace(
            "\"maximumCollisionDisplacement\": 96.0",
            "\"maximumCollisionDisplacement\": 96.0, \"unexpected\": true",
            StringComparison.Ordinal)));
        var translated = valid.Replace("\"x\": 0.0", "\"x\": 1.0", StringComparison.Ordinal);
        Assert.NotEmpty(schema.Validate(translated));
        Assert.Throws<S2ModKitException>(() => RecipeValidator.Validate(
            JsonDefaults.Deserialize<RecipeDocument>(System.Text.Encoding.UTF8.GetBytes(translated), "Recipe")));
    }

    [Fact]
    public async Task RecipeVersionFourAcceptsConnectedComponentTransform()
    {
        const string valid = """
            {
              "schemaVersion": 4,
              "recipeId": "scale-embedded-accessory",
              "inputHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "operations": [{
                "operationId": "scale-islands",
                "kind": "transform_component",
                "version": 3,
                "granularity": "connected_component_vertices",
                "selector": { "kind": "draw_call_ids", "drawCallIds": ["dc_0123456789abcdef01234567"] },
                "lodPolicy": "all_present",
                "expectedMatchesByLod": { "0": 1 },
                "expectedVerticesByLod": { "0": 12 },
                "connectedComponentIdsByLod": { "0": ["cc_0123456789abcdef01234567"] },
                "ownershipPolicy": "exclusive",
                "transform": {
                  "pivot": { "kind": "selection_bounds_center", "referenceLod": 0 },
                  "uniformScale": 2.0,
                  "translation": { "x": 0.0, "y": 0.0, "z": 0.0 }
                },
                "limits": { "maximumVertexDisplacement": 96.0 },
                "extensions": {}
              }],
              "extensions": {}
            }
            """;
        var schema = await JsonSchema.FromFileAsync(GetSchemaPath("recipe.schema.json"), TestContext.Current.CancellationToken);

        Assert.Empty(schema.Validate(valid));
        var document = JsonDefaults.Deserialize<RecipeDocument>(System.Text.Encoding.UTF8.GetBytes(valid), "Recipe");
        RecipeValidator.Validate(document);
        var operation = Assert.IsType<TransformComponentOperation>(Assert.Single(document.Operations));
        Assert.Equal(3, operation.Version);
        Assert.Single(operation.ConnectedComponentIdsByLod!["0"]);

        var duplicate = valid.Replace(
            "[\"cc_0123456789abcdef01234567\"]",
            "[\"cc_0123456789abcdef01234567\", \"cc_0123456789abcdef01234567\"]",
            StringComparison.Ordinal);
        Assert.NotEmpty(schema.Validate(duplicate));
        Assert.Throws<S2ModKitException>(() => RecipeValidator.Validate(
            JsonDefaults.Deserialize<RecipeDocument>(System.Text.Encoding.UTF8.GetBytes(duplicate), "Recipe")));
    }

    private static string GetSchemaPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "schemas", fileName);
    }
}
