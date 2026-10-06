using System.CommandLine;
using S2ModKit.Application;
using S2ModKit.Infrastructure;
using S2ModKit.Reporting;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private Command CreateSelectionPreviewCommand(TextWriter output, TextWriter error)
    {
        var command = new Command("selection-preview", "Render a read-only comparison and small summary for an exact local, coordinated or directional recipe.");
        var project = RequiredStringOption("--project", "S2ModKit project with immutable imported input.");
        var recipe = RequiredStringOption("--recipe", "Acknowledged local-field schema 8, coordinated schema 9 or directional schema 10 recipe.");
        var outputRoot = RequiredStringOption("--output-root", "Configured ignored root for diagnostic artifacts.");
        command.Options.Add(project); command.Options.Add(recipe); command.Options.Add(outputRoot);
        command.SetAction((parse, token) => ExecuteAsync("selection-preview", true, output, error, async () =>
        {
            var intent = await ReadRecipeAsync(parse.GetRequiredValue(recipe), token).ConfigureAwait(false);
            if (intent.SchemaVersion == 10)
            {
                var directional = await application.PreviewDirectionalSelectionAsync(parse.GetRequiredValue(project), intent, token).ConfigureAwait(false);
                return await FileSystemEllipsoidPreviewPublisher.PublishDirectionalAsync(parse.GetRequiredValue(outputRoot), DirectionalSelectionPreviewRenderer.Render(directional), token).ConfigureAwait(false);
            }
            if (intent.SchemaVersion == 9)
            {
                var common = await application.PreviewCoordinatedSelectionAsync(parse.GetRequiredValue(project), intent, token).ConfigureAwait(false);
                return await FileSystemEllipsoidPreviewPublisher.PublishCoordinatedAsync(parse.GetRequiredValue(outputRoot), CoordinatedSelectionPreviewRenderer.Render(common), token).ConfigureAwait(false);
            }
            EllipsoidContractValidator.ValidateRecipe(intent);
            var preview = await application.PreviewEllipsoidSelectionAsync(parse.GetRequiredValue(project), intent, token).ConfigureAwait(false);
            var artifacts = EllipsoidSelectionPreviewRenderer.Render(preview);
            return await FileSystemEllipsoidPreviewPublisher.PublishAsync(parse.GetRequiredValue(outputRoot), artifacts, token).ConfigureAwait(false);
        }, renderText: null, token));
        return command;
    }
}
