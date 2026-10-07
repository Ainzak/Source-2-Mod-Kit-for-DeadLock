using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed partial class S2ModKitApplication
{
    public async Task<ComponentDiscoveryResultV2> DiscoverPairedComponentsAsync(string projectRoot, CancellationToken token = default)
    {
        var (_, input, _) = await LoadProjectGraphAsync(projectRoot, token).ConfigureAwait(false);
        RequireInspector(input);
        return await PairedAuthoring.DiscoverAsync(input, await inspector.InspectAsync(input, token).ConfigureAwait(false), token).ConfigureAwait(false);
    }

    public async Task<DirectionalAuthoringSource> InspectPairedSelectionAsync(string projectRoot, IReadOnlyList<string> componentIds, CancellationToken token = default)
    {
        var (_, input, _) = await LoadProjectGraphAsync(projectRoot, token).ConfigureAwait(false);
        RequireInspector(input);
        if (inspector is not IPairedAuthoringSourceReader reader)
            throw Errors.Unsupported("PAIRED_AUTHORING_READER_REQUIRED", "Complete paired source facts are unavailable.", "Configure a characterized source reader.");
        var model = await inspector.InspectAsync(input, token).ConfigureAwait(false);
        var discovery = await PairedAuthoring.DiscoverAsync(input, model, token).ConfigureAwait(false);
        var members = ResolvePairedAuthoringMembers(model, discovery, componentIds);
        var source = await reader.ReadPairedAuthoringSourceAsync(input, members, token).ConfigureAwait(false);
        if (source.InputHash != input.ContentHash || JsonDefaults.Serialize(source.Members) != JsonDefaults.Serialize(members))
            throw Errors.Verification("PAIRED_AUTHORING_SOURCE_DRIFT", "Source authoring identities differ.", "Reject the report; inspect immutable input again.");
        return source;
    }

    public async Task<RecipeScaffoldResult> ScaffoldPairedRecipeAsync(string projectRoot, IReadOnlyList<string> componentIds,
        PairedFieldOptions options, string outputPath, CancellationToken token = default)
    {
        var (_, input, dependencies) = await LoadProjectGraphAsync(projectRoot, token).ConfigureAwait(false);
        RequireInspector(input);
        var model = await inspector.InspectAsync(input, token).ConfigureAwait(false);
        var discovery = await PairedAuthoring.DiscoverAsync(input, model, token).ConfigureAwait(false);
        var members = ResolvePairedAuthoringMembers(model, discovery, componentIds);
        var recipe = PairedAuthoring.CreateRecipe(new(input.ContentHash, members, [], [], []), options);
        _ = CreatePlan(input, model, recipe, dependencies);
        var canonical = JsonDefaults.SerializeToUtf8(recipe);
        var reopened = JsonDefaults.Deserialize<RecipeDocument>(canonical, "Paired scaffold");
        if (!canonical.AsSpan().SequenceEqual(JsonDefaults.SerializeToUtf8(reopened)))
            throw Errors.Verification("PAIRED_SCAFFOLD_DRIFT", "Canonical recipe did not roundtrip.", "Reject output.");
        if (recipeDocumentWriter is null) throw Errors.Unsupported("RECIPE_OUTPUT_NOT_CONFIGURED", "No atomic recipe writer.", "Configure the normal CLI composition.");
        token.ThrowIfCancellationRequested();
        var destination = await recipeDocumentWriter.WriteNewAsync(outputPath, canonical, token).ConfigureAwait(false);
        return new(destination, ContentHash.Compute(canonical), discovery.DiscoveryFingerprint, componentIds.Order(StringComparer.Ordinal).ToArray(), recipe);
    }

    private static IReadOnlyList<CoordinatedMember> ResolvePairedAuthoringMembers(ModelSnapshot model, ComponentDiscoveryResultV2 discovery, IReadOnlyList<string> ids)
    {
        if (ids.Count == 0 || ids.Count != ids.Distinct(StringComparer.Ordinal).Count())
            throw Errors.Selection("PAIRED_COMPONENTS_INVALID", "Choose distinct current schema-7 component IDs.", "Use paired discover.");
        var candidates = ids.Select(id => discovery.Candidates.SingleOrDefault(c => c.CandidateId == id)
            ?? throw Errors.Selection("PAIRED_COMPONENTS_INVALID", "A component ID is stale or unknown.", "Use current paired discovery.")).ToArray();
        return CoordinatedSelection.ResolveMembers(model, ComponentCandidateUnionBuilder.Create(model, candidates, 7), 1);
    }

    public async Task<PairedSelectionPreview> PreviewPairedSelectionAsync(string projectRoot, RecipeDocument recipe, CancellationToken token = default)
    {
        PairedContractValidator.ValidateRecipe(recipe);
        var (_, input, dependencies) = await LoadProjectGraphAsync(projectRoot, token).ConfigureAwait(false);
        RequireInspector(input);
        if (inspector is not IPairedPreviewGeometryReader reader)
            throw Errors.Unsupported("PAIRED_PREVIEW_READER_REQUIRED", "Complete paired preview geometry is unavailable.", "Configure a compatible source reader.");
        var plan = CreatePlan(input, await inspector.InspectAsync(input, token).ConfigureAwait(false), recipe, dependencies);
        return PairedSelectionPreviewBuilder.Create(plan, await reader.ReadPairedPreviewGeometryAsync(input, plan, token).ConfigureAwait(false));
    }
}
