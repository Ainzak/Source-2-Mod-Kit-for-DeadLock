using System.CommandLine;
using S2ModKit.Adapters.Blender;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private Command CreatePositionAuthoringCommand(TextWriter output, TextWriter error)
    {
        var group = new Command("authoring", "Agent source export, immutable Blender scenes and independent returned-position validation.");
        foreach (var name in new[] { "export", "validate", "scene-create", "scene-extract", "import-recipe" })
        {
            var command = new Command(name);
            var project = new Option<string>("--project") { Required = true }; var selection = new Option<string>("--selection") { Required = true };
            var bundle = new Option<string>("--bundle") { Required = true }; var hash = new Option<string>("--bundle-sha256") { Required = true };
            var delta = new Option<string>("--delta") { Required = true }; var destination = new Option<string>("--output") { Required = true };
            var blender = new Option<string>("--blender") { Required = true }; var bridge = new Option<string>("--bridge-script") { Required = true };
            var scene = new Option<string>("--scene") { Required = true };
            if (name is "export" or "validate" or "import-recipe") { command.Options.Add(project); command.Options.Add(selection); }
            if (name != "export") { command.Options.Add(bundle); command.Options.Add(hash); }
            if (name is "validate" or "import-recipe") command.Options.Add(delta);
            if (name != "validate") command.Options.Add(destination);
            if (name.StartsWith("scene-", StringComparison.Ordinal)) { command.Options.Add(blender); command.Options.Add(bridge); }
            if (name == "scene-extract") command.Options.Add(scene);
            command.SetAction((parse, token) => ExecuteAsync("authoring." + name, true, output, error, async () =>
            {
                if (name.StartsWith("scene-", StringComparison.Ordinal)) return (object)await BlenderSceneBridge.RunAsync(parse.GetRequiredValue(blender), parse.GetRequiredValue(bridge),
                    name == "scene-create" ? "create" : "extract", parse.GetRequiredValue(bundle), ReadAuthoringHash(parse.GetRequiredValue(hash)),
                    name == "scene-extract" ? parse.GetRequiredValue(scene) : null, parse.GetRequiredValue(destination), token).ConfigureAwait(false);
                var authoring = application as IPositionAuthoringApplication ?? throw Errors.Unsupported("IMPORT_AUTHORING_UNAVAILABLE", "No authoring application configured.", "Use the normal CLI composition.");
                var selectionBytes = await ReadAuthoringFile(parse.GetRequiredValue(selection), 16 * 1024 * 1024, token).ConfigureAwait(false);
                if (name == "import-recipe")
                {
                    var importer = application as IPositionImportApplication ?? throw Errors.Unsupported("IMPORT_AUTHORING_UNAVAILABLE", "No compiled-position authoring application configured.", "Use normal CLI composition.");
                    var assets = new PositionImportAssets(selectionBytes, await ReadAuthoringFile(parse.GetRequiredValue(bundle), PositionAuthoringJson.MaximumArtifactBytes, token).ConfigureAwait(false), await ReadAuthoringFile(parse.GetRequiredValue(delta), PositionAuthoringJson.MaximumArtifactBytes, token).ConfigureAwait(false));
                    return (object)await importer.ScaffoldPositionImportAsync(parse.GetRequiredValue(project), assets, ReadAuthoringHash(parse.GetRequiredValue(hash)), parse.GetRequiredValue(destination), token).ConfigureAwait(false);
                }
                if (name == "export") return (object)await authoring.ExportPositionAuthoringAsync(parse.GetRequiredValue(project), selectionBytes, parse.GetRequiredValue(destination), token).ConfigureAwait(false);
                return await authoring.ValidatePositionReturnAsync(parse.GetRequiredValue(project), selectionBytes,
                    await ReadAuthoringFile(parse.GetRequiredValue(bundle), PositionAuthoringJson.MaximumArtifactBytes, token).ConfigureAwait(false), ReadAuthoringHash(parse.GetRequiredValue(hash)),
                    await ReadAuthoringFile(parse.GetRequiredValue(delta), PositionAuthoringJson.MaximumArtifactBytes, token).ConfigureAwait(false), token).ConfigureAwait(false);
            }, renderText: null, token));
            group.Subcommands.Add(command);
        }
        var requestCommand = new Command("import-request", "Validate or plan a version-1 position import request from bound workspace artifacts.");
        var requestPath = new Option<string>("--request") { Required = true }; requestCommand.Options.Add(requestPath);
        requestCommand.SetAction((parse, token) => ExecuteAsync("authoring.import-request", true, output, error, async () =>
        {
            var importer = application as IPositionImportApplication ?? throw Errors.Unsupported("IMPORT_AUTHORING_UNAVAILABLE", "No position import application configured.", "Use normal CLI composition.");
            return await importer.RunPositionImportRequestAsync(await ReadAuthoringFile(parse.GetRequiredValue(requestPath), PositionImportCompositionContract.MaximumRecipeBytes, token).ConfigureAwait(false), token).ConfigureAwait(false);
        }, renderText: null, token));
        group.Subcommands.Add(requestCommand);
        var compose = new Command("compose-recipe", "Atomically compose an exact position import with original-source draw removal.");
        var composeProject = new Option<string>("--project") { Required = true };
        var importRecipe = new Option<string>("--import-recipe") { Required = true };
        var removalRecipe = new Option<string>("--remove-recipe") { Required = true };
        var composeOutput = new Option<string>("--output") { Required = true };
        compose.Options.Add(composeProject); compose.Options.Add(importRecipe); compose.Options.Add(removalRecipe); compose.Options.Add(composeOutput);
        compose.SetAction((parse, token) => ExecuteAsync("authoring.compose-recipe", true, output, error, async () =>
        {
            var composer = application as IPositionImportCompositionApplication ?? throw Errors.Unsupported("IMPORT_COMPOSITION_UNAVAILABLE", "No composition application configured.", "Use normal CLI composition.");
            var imported = JsonDefaults.Deserialize<RecipeDocument>(await ReadAuthoringFile(parse.GetRequiredValue(importRecipe), PositionImportCompositionContract.MaximumRecipeBytes, token).ConfigureAwait(false), "Import recipe");
            var removed = JsonDefaults.Deserialize<RecipeDocument>(await ReadAuthoringFile(parse.GetRequiredValue(removalRecipe), PositionImportCompositionContract.MaximumRecipeBytes, token).ConfigureAwait(false), "Removal recipe");
            return await composer.ComposePositionImportRecipeAsync(parse.GetRequiredValue(composeProject), imported, removed, parse.GetRequiredValue(composeOutput), token).ConfigureAwait(false);
        }, renderText: null, token));
        group.Subcommands.Add(compose); return group;
    }
    private static ContentHash ReadAuthoringHash(string value) { PositionAuthoringJson.Hash(value); return new(value); }
    private static async Task<byte[]> ReadAuthoringFile(string path, int budget, CancellationToken token)
    {
        if (new FileInfo(path).Length > budget) throw PositionAuthoringJson.Invalid("IMPORT_JSON_BUDGET", "Authoring input exceeds byte budget.");
        return await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
    }
}
