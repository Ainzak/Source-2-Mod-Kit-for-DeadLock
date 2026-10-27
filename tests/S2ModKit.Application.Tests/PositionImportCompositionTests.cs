using System.Text;
using System.Text.Json.Nodes;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;

namespace S2ModKit.Application.Tests;

public sealed class PositionImportCompositionTests
{
    [Fact]
    public async Task ExactCompositionRecipeIsClosedAndEarlierContractsStillRejectStacking()
    {
        var r = Recipe(); var bytes = JsonDefaults.SerializeToUtf8(r);
        Assert.Equal(bytes, JsonDefaults.SerializeToUtf8(JsonDefaults.Deserialize<RecipeDocument>(bytes, "Composition")));
        var schema = await JsonSchema.FromFileAsync(Schema("v13/recipe.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(schema.Validate(Encoding.UTF8.GetString(bytes)));
        Assert.Throws<S2ModKitException>(() => JsonDefaults.SerializeToUtf8(r with { SchemaVersion = 12 }));
        var doc = JsonNode.Parse(bytes)!; doc["operations"]![1]!["positionImport"] = null;
        Assert.NotEmpty(schema.Validate(doc.ToJsonString()));
        Assert.Throws<S2ModKitException>(() => JsonDefaults.Deserialize<RecipeDocument>(Encoding.UTF8.GetBytes(doc.ToJsonString()), "Mixed null"));
    }
    [Theory]
    [InlineData("overlap")]
    [InlineData("order")]
    [InlineData("duplicate_operation")]
    [InlineData("material_selector")]
    [InlineData("partial_lod")]
    public void AmbiguousOrConflictingCompositionRejectsBeforeSourceAccess(string defect)
    {
        var r = Recipe(); var import = (ImportPositionsOperation)r.Operations[0]; var remove = (RemoveComponentOperation)r.Operations[1];
        if (defect == "overlap") remove = remove with { Selector = import.Selector };
        if (defect == "duplicate_operation") remove = remove with { OperationId = import.OperationId };
        if (defect == "material_selector") remove = remove with { Selector = new() { Kind = "material_path", MaterialPath = "a.vmat" } };
        if (defect == "partial_lod") remove = remove with { LodPolicy = "selected" };
        r = r with { Operations = defect == "order" ? [remove, import] : [import, remove] };
        Assert.Throws<S2ModKitException>(() => JsonDefaults.SerializeToUtf8(r));
    }
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    public void RecipeWhitespaceCannotBypassSixteenMiBReadBudget(int version)
    {
        var r = Recipe(); if (version == 12) r = PositionImportCompositionContract.ImportRecipe(r);
        var text = Encoding.UTF8.GetString(JsonDefaults.SerializeToUtf8(r));
        var oversized = Encoding.UTF8.GetBytes(text + new string(' ', PositionImportCompositionContract.MaximumRecipeBytes));
        Assert.Throws<S2ModKitException>(() => JsonDefaults.Deserialize<RecipeDocument>(oversized, "Oversized recipe"));
    }
    [Fact]
    public async Task ImportRequestUsesSixteenMiBBudgetBeforeParsing()
    {
        var app = new S2ModKitApplication(new FileSystemProjectWorkspace(), null!, new UnusedDependencies(), null!, null!, null!, null!, new DirectoryProjectResourceSourceFactory());
        var bytes = new byte[PositionImportCompositionContract.MaximumRecipeBytes + 1];
        var error = await Assert.ThrowsAsync<S2ModKitException>(() => app.RunPositionImportRequestAsync(bytes, TestContext.Current.CancellationToken));
        Assert.Equal("IMPORT_JSON_BUDGET", error.Error.Code);
    }
    private static RecipeDocument Recipe()
    {
        var f = PositionReturnValidatorTests.Fixture(); var assets = new PositionImportAssets(PositionAuthoringJson.Canonical(f.Selection), f.Bytes, PositionAuthoringJson.Canonical(f.Delta));
        var imported = PositionImportContract.Recipe(f.Selection, PositionImportContract.Binding(assets));
        var remove = new RemoveComponentOperation { OperationId = "remove", Selector = new() { Kind = "draw_call_ids", DrawCallIds = ["dc_aaaaaaaaaaaaaaaaaaaaaaaa"] }, ExpectedMatchesByLod = new Dictionary<string, int> { ["0"] = 1 } };
        return imported with { SchemaVersion = 13, Operations = [imported.Operations[0], remove] };
    }
    private static string Schema(string name)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory); while (d is not null && !File.Exists(Path.Combine(d.FullName, "global.json"))) d = d.Parent;
        return Path.Combine(d!.FullName, "schemas", name);
    }
    private sealed class UnusedDependencies : IResourceDependencyReader
    {
        public bool CanReadDependencies(ArtifactContent artifact) => throw new InvalidOperationException("Must reject before source access.");
        public Task<IReadOnlyList<ResourceDependency>> ReadDependenciesAsync(ArtifactContent artifact, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Must reject before source access.");
    }
}
