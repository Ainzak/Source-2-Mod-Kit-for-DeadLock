using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed partial class S2ModKitApplication
{
    public async Task<ComponentDiscoveryResultV2> DiscoverDirectionalComponentsAsync(string projectRoot, CancellationToken token = default)
    {
        var (_, input, _) = await LoadProjectGraphAsync(projectRoot, token).ConfigureAwait(false);
        RequireInspector(input);
        var model = await inspector.InspectAsync(input, token).ConfigureAwait(false);
        return await DirectionalAuthoring.DiscoverAsync(input, model, token).ConfigureAwait(false);
    }

    public async Task<DirectionalAuthoringSource> InspectDirectionalSelectionAsync(string projectRoot, IReadOnlyList<string> componentIds, CancellationToken token = default)
    {
        var (_, input, _) = await LoadProjectGraphAsync(projectRoot, token).ConfigureAwait(false);
        RequireInspector(input);
        if (inspector is not IDirectionalAuthoringSourceReader reader)
            throw Errors.Unsupported("DIRECTIONAL_AUTHORING_READER_REQUIRED", "Complete source authoring facts are unavailable.", "Configure a characterized source reader.");
        var model = await inspector.InspectAsync(input, token).ConfigureAwait(false);
        var discovery = await DirectionalAuthoring.DiscoverAsync(input, model, token).ConfigureAwait(false);
        if (componentIds.Count == 0 || componentIds.Distinct(StringComparer.Ordinal).Count() != componentIds.Count)
            throw Errors.Selection("DIRECTIONAL_COMPONENTS_INVALID", "Choose distinct current component IDs.", "Read schema-6 directional discovery first.");
        var candidates = componentIds.Select(id => discovery.Candidates.SingleOrDefault(c => c.CandidateId == id)
            ?? throw Errors.Selection("DIRECTIONAL_COMPONENTS_INVALID", "A component ID is stale or unknown.", "Read current source discovery.")).ToArray();
        var union = ComponentCandidateUnionBuilder.Create(model, candidates, 6);
        var members = CoordinatedSelection.ResolveMembers(model, union, 1);
        var source = await reader.ReadDirectionalAuthoringSourceAsync(input, members, token).ConfigureAwait(false);
        if (source.InputHash != input.ContentHash || JsonDefaults.Serialize(source.Members) != JsonDefaults.Serialize(members))
            throw Errors.Verification("DIRECTIONAL_AUTHORING_SOURCE_DRIFT", "Source authoring identities differ.", "Reject the report and inspect the immutable input again.");
        return source;
    }
}
