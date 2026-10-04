using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed partial class S2ModKitApplication
{
    public async Task<ComponentDiscoveryResultV2> DiscoverCoordinatedComponentsAsync(string projectRoot, CancellationToken token = default)
    {
        var (_, input, _) = await LoadProjectGraphAsync(projectRoot, token).ConfigureAwait(false);
        RequireInspector(input);
        var model = await inspector.InspectAsync(input, token).ConfigureAwait(false);
        return await new ExperimentalComponentDiscoveryService(componentCapabilityAnalyzer, transformPlanner).DiscoverCoordinatedAsync(input, model, token).ConfigureAwait(false);
    }

    public async Task<CoordinatedSelectionProbe> ProbeCoordinatedSelectionAsync(string projectRoot, IReadOnlyList<string> componentIds,
        CoordinatedScaffoldOptions options, CancellationToken token = default)
    {
        var (_, input, _) = await LoadProjectGraphAsync(projectRoot, token).ConfigureAwait(false);
        RequireInspector(input);
        var model = await inspector.InspectAsync(input, token).ConfigureAwait(false);
        var service = new ExperimentalComponentDiscoveryService(componentCapabilityAnalyzer, transformPlanner);
        var discovery = await service.DiscoverCoordinatedAsync(input, model, token).ConfigureAwait(false);
        var candidates = componentIds.Select(id => discovery.Candidates.SingleOrDefault(c => c.CandidateId == id)
            ?? throw CoordinatedSelection.Invalid("SCAFFOLD_COMPONENT_STALE", "A selected candidate is absent from current coordinated discovery.")).ToArray();
        var union = ComponentCandidateUnionBuilder.Create(model, candidates, 5);
        return service.Probe(input, model, union, options, discovery.DiscoveryFingerprint);
    }

    public async Task<CoordinatedSelectionPreview> PreviewCoordinatedSelectionAsync(string projectRoot, RecipeDocument recipe, CancellationToken token = default)
    {
        CoordinatedContractValidator.ValidateRecipe(recipe);
        var (_, input, dependencies) = await LoadProjectGraphAsync(projectRoot, token).ConfigureAwait(false);
        RequireInspector(input);
        if (inspector is not ICoordinatedPreviewGeometryReader reader)
            throw Errors.Unsupported("COORDINATED_PREVIEW_READER_REQUIRED", "Complete immutable coordinated geometry cannot be read by this adapter.", "Configure a compatible geometry reader.");
        var model = await inspector.InspectAsync(input, token).ConfigureAwait(false);
        var plan = CreatePlan(input, model, recipe, dependencies);
        return CoordinatedSelectionPreviewBuilder.Create(plan, await reader.ReadCoordinatedPreviewGeometryAsync(input, plan, token).ConfigureAwait(false), model);
    }
}
