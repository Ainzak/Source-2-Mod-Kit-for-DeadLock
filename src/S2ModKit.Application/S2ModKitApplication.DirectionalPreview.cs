using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed partial class S2ModKitApplication
{
    public async Task<DirectionalSelectionPreview> PreviewDirectionalSelectionAsync(string projectRoot, RecipeDocument recipe, CancellationToken token = default)
    {
        DirectionalContractValidator.ValidateRecipe(recipe);
        var (_, input, dependencies) = await LoadProjectGraphAsync(projectRoot, token).ConfigureAwait(false);
        RequireInspector(input);
        if (inspector is not IDirectionalPreviewGeometryReader reader)
            throw Errors.Unsupported("DIRECTIONAL_PREVIEW_READER_REQUIRED", "Complete immutable directional geometry cannot be read by this adapter.", "Configure a compatible geometry reader.");
        var model = await inspector.InspectAsync(input, token).ConfigureAwait(false);
        var plan = CreatePlan(input, model, recipe, dependencies);
        return DirectionalSelectionPreviewBuilder.Create(plan, await reader.ReadDirectionalPreviewGeometryAsync(input, plan, token).ConfigureAwait(false));
    }
}
