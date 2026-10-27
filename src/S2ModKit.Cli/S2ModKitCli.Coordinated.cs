using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private Task<ComponentDiscoveryResultV2> DiscoverCoordinatedCliAsync(string project, bool experimental, CancellationToken token)
    {
        if (!experimental) throw Errors.InvalidRecipe("COORDINATED_OPT_IN_REQUIRED", "Coordinated discovery requires explicit experimental acknowledgement.", "Use --experimental --coordinated.");
        return application.DiscoverCoordinatedComponentsAsync(project, token);
    }

    private static async Task<CoordinatedScaffoldOptions?> ReadCoordinatedOptionsAsync(string? path, bool experimental, string intent, CancellationToken token)
    {
        if (intent != RecipeScaffoldContract.CoordinatedFieldIntent)
        {
            if (path is not null) throw Errors.InvalidRecipe("SCAFFOLD_COORDINATED_OPTIONS_INVALID", "Common-field options require coordinated-field intent.", "Remove ignored options or select the coordinated action.");
            return null;
        }
        if (!experimental || string.IsNullOrWhiteSpace(path)) throw Errors.InvalidRecipe("COORDINATED_OPT_IN_REQUIRED", "Coordination requires --experimental and --coordinated-options with every policy and field fact.", "Declare the explicit typed common-field options.");
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw Errors.InvalidRecipe("COORDINATED_OPTIONS_TOO_LARGE", "Common-field options exceed the 2 MiB input limit.", "Supply a bounded typed options document.");
        var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        return CoordinatedSelection.ReadOptions(bytes);
    }
}
