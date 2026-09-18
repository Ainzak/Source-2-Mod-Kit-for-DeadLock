using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using S2ModKit.Application;
using S2ModKit.Cli;
using S2ModKit.Domain;

namespace S2ModKit.Cli.Tests;

public sealed partial class CliContractTests
{
    [Fact]
    public async Task PackageCreateWritesJsonEnvelopeAndForwardsRequiredExternalGate()
    {
        var packaging = new FakePackagingApplication();
        var cli = new S2ModKitCli(new FakeApplication(), packaging, "test-adapter", "1", externalVerifierAvailable: true);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var hash = ContentHash.Compute("source-vpk"u8);

        var exitCode = await cli.RunAsync(
            ["package", "create", "--project", "project", "--build", "build", "--source-vpk", "source.vpk", "--source-vpk-sha256", hash.Value, "--require-external"],
            output,
            error,
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(output.ToString());

        Assert.Equal(0, exitCode);
        Assert.Equal("package.create", document.RootElement.GetProperty("command").GetString());
        Assert.Equal("vpk-0123456789abcdef0123", document.RootElement.GetProperty("result").GetProperty("package").GetProperty("packageId").GetString());
        Assert.True(packaging.LastRequireExternal);
        Assert.Equal(hash, packaging.LastSourceHash);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task PackageCreateRejectsInvalidSourceHashAsSchemaFailure()
    {
        var cli = new S2ModKitCli(new FakeApplication(), new FakePackagingApplication(), "test-adapter", "1", externalVerifierAvailable: false);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(
            ["package", "create", "--project", "project", "--build", "build", "--source-vpk", "source.vpk", "--source-vpk-sha256", "bad"],
            output,
            error,
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(output.ToString());

        Assert.Equal((int)ErrorCategory.CliOrSchema, exitCode);
        Assert.Equal("CONTENT_HASH_INVALID", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task PackageCreateMinimalWritesJsonOnlyToStandardOutput()
    {
        var packaging = new FakePackagingApplication();
        var cli = new S2ModKitCli(new FakeApplication(), packaging, "test-adapter", "1", externalVerifierAvailable: false);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(
            ["package", "create-minimal", "--project", "project", "--build", "build"],
            output,
            error,
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(output.ToString());

        Assert.Equal(0, exitCode);
        Assert.Equal("package.create-minimal", document.RootElement.GetProperty("command").GetString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task PackageVerifyWritesJsonOnlyAndForwardsExternalGate()
    {
        var packaging = new FakePackagingApplication();
        var cli = new S2ModKitCli(new FakeApplication(), packaging, "test-adapter", "1", externalVerifierAvailable: true);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(
            ["package", "verify", "--project", "project", "--package", "vpk-0123456789abcdef0123", "--require-external"],
            output,
            error,
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(output.ToString());

        Assert.Equal(0, exitCode);
        Assert.Equal("package.verify", document.RootElement.GetProperty("command").GetString());
        Assert.True(packaging.LastRequireExternal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task ProjectCreateVpkWritesJsonOnlyAndForwardsExpectedDirectoryHash()
    {
        var application = new FakeApplication();
        var cli = new S2ModKitCli(application, "test-adapter", "1", externalVerifierAvailable: false);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var directoryHash = ContentHash.Compute("directory"u8);

        var exitCode = await cli.RunAsync(
            ["project", "create-vpk", "--root", "project", "--base-vpk", "pak01_dir.vpk", "--entry", "models/hero.vmdl_c", "--expect-directory-sha256", directoryHash.Value],
            output,
            error,
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(output.ToString());

        Assert.Equal(0, exitCode);
        Assert.Equal("project.create-vpk", document.RootElement.GetProperty("command").GetString());
        Assert.Equal(directoryHash, application.LastExpectedDirectoryHash);
        Assert.Equal("models/hero.vmdl_c", application.LastVpkEntry);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task PlanReadsVersionTwoTransformRecipeAndKeepsJsonOnStandardOutput()
    {
        var recipePath = Path.Combine(Path.GetTempPath(), $"s2modkit-recipe-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            recipePath,
            $$"""
            {
              "schemaVersion": 2,
              "recipeId": "scale-accessory",
              "inputHash": "{{FakeApplication.TestInputHash}}",
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
                    "uniformScale": 1.2,
                    "translation": { "x": 0, "y": 0, "z": 0 }
                  },
                  "limits": { "maximumVertexDisplacement": 32 },
                  "extensions": {}
                }
              ],
              "extensions": {}
            }
            """,
            TestContext.Current.CancellationToken);
        try
        {
            var application = new FakeApplication();
            var cli = new S2ModKitCli(application, "test-adapter", "1", externalVerifierAvailable: false);
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await cli.RunAsync(
                ["plan", "--project", "memory", "--recipe", recipePath, "--format", "json"],
                output,
                error,
                TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(output.ToString());

            Assert.Equal(0, exitCode);
            Assert.IsType<TransformComponentOperation>(application.LastRecipe!.Operations.Single());
            Assert.Equal("plan", document.RootElement.GetProperty("command").GetString());
            Assert.Empty(error.ToString());
        }
        finally
        {
            File.Delete(recipePath);
        }
    }

    [Fact]
    public async Task RecipeScaffoldForwardsExplicitUnionAndTransformParameters()
    {
        var application = new FakeApplication();
        var cli = new S2ModKitCli(application, "test-adapter", "1", externalVerifierAvailable: false);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(
            [
                "recipe", "scaffold",
                "--project", "memory",
                "--component", "cmp_111111111111111111111111",
                "--component", "cmp_222222222222222222222222",
                "--intent", "uniform-scale",
                "--output", "recipe.json",
                "--scale", "2",
                "--translate-x", "1.25",
                "--translate-z", "-3",
                "--reference-lod", "1",
                "--max-displacement", "64",
                "--max-collision-displacement", "48",
            ],
            output,
            error,
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(output.ToString());
        var request = Assert.IsType<RecipeScaffoldRequest>(application.LastScaffoldRequest);

        Assert.Equal(0, exitCode);
        Assert.Equal("recipe.scaffold", document.RootElement.GetProperty("command").GetString());
        Assert.Equal(
            ["cmp_111111111111111111111111", "cmp_222222222222222222222222"],
            request.ComponentIds);
        Assert.Equal(2f, request.UniformScale);
        Assert.Equal(1.25f, request.TranslationX);
        Assert.Null(request.TranslationY);
        Assert.Equal(-3f, request.TranslationZ);
        Assert.Equal(1, request.ReferenceLod);
        Assert.Equal(64f, request.MaximumVertexDisplacement);
        Assert.Equal(48f, request.MaximumCollisionDisplacement);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task RecipeScaffoldMissingComponentReturnsJsonSchemaFailure()
    {
        var cli = new S2ModKitCli(new FakeApplication(), "test-adapter", "1", externalVerifierAvailable: false);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(
            ["recipe", "scaffold", "--project", "memory", "--intent", "remove", "--output", "recipe.json"],
            output,
            error,
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(output.ToString());

        Assert.Equal((int)ErrorCategory.CliOrSchema, exitCode);
        Assert.Equal("recipe.scaffold", document.RootElement.GetProperty("command").GetString());
        Assert.Equal("CLI_PARSE_ERROR", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("1,25")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public async Task RecipeScaffoldRejectsNonInvariantOrNonFiniteNumbers(string value)
    {
        var application = new FakeApplication();
        var cli = new S2ModKitCli(application, "test-adapter", "1", externalVerifierAvailable: false);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(
            [
                "recipe", "scaffold",
                "--project", "memory",
                "--component", "cmp_111111111111111111111111",
                "--intent", "uniform-scale",
                "--output", "recipe.json",
                "--scale", value,
                "--max-displacement", "64",
            ],
            output,
            error,
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(output.ToString());

        Assert.Equal((int)ErrorCategory.CliOrSchema, exitCode);
        Assert.Equal("CLI_NUMBER_INVALID", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Null(application.LastScaffoldRequest);
        Assert.Empty(error.ToString());
    }

}
