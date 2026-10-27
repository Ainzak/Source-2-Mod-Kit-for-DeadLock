using System.Text;
using System.Text.Json.Nodes;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;

namespace S2ModKit.Application.Tests;

public sealed class PositionImportLifecycleTests
{
    [Fact]
    public async Task TypedRecipeUsesClosedSchemaAndRetainsExactBoundArtifactIdentities()
    {
        var f = PositionReturnValidatorTests.Fixture(); var a = new PositionImportAssets(PositionAuthoringJson.Canonical(f.Selection), f.Bytes, PositionAuthoringJson.Canonical(f.Delta));
        var binding = PositionImportContract.Binding(a); var recipe = PositionImportContract.Recipe(f.Selection, binding);
        var bytes = JsonDefaults.SerializeToUtf8(recipe); var parsed = JsonDefaults.Deserialize<RecipeDocument>(bytes, "Import recipe");
        Assert.Equal(bytes, JsonDefaults.SerializeToUtf8(parsed)); Assert.IsType<ImportPositionsOperation>(parsed.Operations[0]);
        var schema = await JsonSchema.FromFileAsync(Schema("v12/recipe.schema.json"), TestContext.Current.CancellationToken); Assert.Empty(schema.Validate(Encoding.UTF8.GetString(bytes)));
        var json = JsonNode.Parse(bytes)!; json["operations"]![0]!["transform"] = null;
        Assert.Throws<S2ModKitException>(() => JsonDefaults.Deserialize<RecipeDocument>(Encoding.UTF8.GetBytes(json.ToJsonString()), "Mixed recipe"));
        json = JsonNode.Parse(bytes)!; json["operations"]![0]!["positionImport"]!["framePolicy"]!["version"] = 2;
        Assert.Throws<S2ModKitException>(() => JsonDefaults.Deserialize<RecipeDocument>(Encoding.UTF8.GetBytes(json.ToJsonString()), "Wrong frame policy"));
        Assert.Throws<S2ModKitException>(() => JsonDefaults.SerializeToUtf8(recipe with { SchemaVersion = 11 }));
    }
    [Theory]
    [InlineData("selection")]
    [InlineData("bundle")]
    [InlineData("delta")]
    [InlineData("policy")]
    [InlineData("path")]
    public void ArtifactBindingsRejectIndependentDrift(string field)
    {
        var f = PositionReturnValidatorTests.Fixture(); var a = new PositionImportAssets(PositionAuthoringJson.Canonical(f.Selection), f.Bytes, PositionAuthoringJson.Canonical(f.Delta)); var b = PositionImportContract.Binding(a);
        var hash = ContentHash.Compute("wrong"u8);
        if (field == "selection") a = a with { Selection = PositionAuthoringJson.Canonical(f.Selection with { MaximumDisplacement = 3 }) };
        if (field == "bundle") a = a with { Bundle = "{}"u8.ToArray() }; if (field == "delta") a = a with { Delta = "{}"u8.ToArray() };
        if (field == "policy") b = b with { PolicySha256 = hash }; if (field == "path") b = b with { DeltaArtifact = new("../delta.json", b.DeltaSha256) };
        Assert.Throws<S2ModKitException>(() => PositionImportContract.ValidateAssets(b, a));
    }
    [Fact]
    public async Task ImmutableProjectArtifactsAreIdempotentAndDetectStoredCorruption()
    {
        var f = PositionReturnValidatorTests.Fixture(); var a = new PositionImportAssets(PositionAuthoringJson.Canonical(f.Selection), f.Bytes, PositionAuthoringJson.Canonical(f.Delta)); var b = PositionImportContract.Binding(a);
        var root = Path.Combine(Environment.GetEnvironmentVariable("S2MODKIT_TEST_TEMP_ROOT") ?? Path.GetTempPath(), "s2mod-import-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSystemProjectWorkspace(); await store.StorePositionImportAsync(root, b, a, TestContext.Current.CancellationToken); await store.StorePositionImportAsync(root, b, a, TestContext.Current.CancellationToken);
            var read = await store.LoadPositionImportAsync(root, b, TestContext.Current.CancellationToken); Assert.Equal(a.Delta.ToArray(), read.Delta.ToArray());
            await File.WriteAllBytesAsync(Path.Combine(root, b.DeltaArtifact.Path), "{}"u8.ToArray(), TestContext.Current.CancellationToken);
            Assert.Equal("IMPORT_ARTIFACT_DRIFT", (await Assert.ThrowsAsync<S2ModKitException>(() => store.LoadPositionImportAsync(root, b, TestContext.Current.CancellationToken))).Error.Code);
            await Assert.ThrowsAsync<S2ModKitException>(() => store.StorePositionImportAsync(root, b, a, TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task ImportBuildRequiresIndependentAdapterBeforeReadingOrPublishingAnything()
    {
        var f = PositionReturnValidatorTests.Fixture(); var a = new PositionImportAssets(PositionAuthoringJson.Canonical(f.Selection), f.Bytes, PositionAuthoringJson.Canonical(f.Delta)); var recipe = PositionImportContract.Recipe(f.Selection, PositionImportContract.Binding(a));
        var app = new S2ModKitApplication(new FileSystemProjectWorkspace(), null!, new UnusedDependencies(), null!, null!, null!, null!, new DirectoryProjectResourceSourceFactory());
        Assert.Equal("IMPORT_VERIFIER_REQUIRED", (await Assert.ThrowsAsync<S2ModKitException>(() => app.BuildAsync("unused", recipe, TestContext.Current.CancellationToken))).Error.Code);
    }
    private static string Schema(string name)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory); while (d is not null && !File.Exists(Path.Combine(d.FullName, "global.json"))) d = d.Parent;
        return Path.Combine(d!.FullName, "schemas", name);
    }
    private sealed class UnusedDependencies : IResourceDependencyReader
    {
        public bool CanReadDependencies(ArtifactContent artifact) => throw new InvalidOperationException("Must reject before reading.");
        public Task<IReadOnlyList<ResourceDependency>> ReadDependenciesAsync(ArtifactContent artifact, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Must reject before reading.");
    }
}
