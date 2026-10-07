using S2ModKit.Domain;

namespace S2ModKit.Application;

public interface IPairedAuthoringSourceReader
{
    Task<DirectionalAuthoringSource> ReadPairedAuthoringSourceAsync(ArtifactContent input, IReadOnlyList<CoordinatedMember> members, CancellationToken token = default);
}

public static class PairedAuthoring
{
    public static RecipeDocument CreateRecipe(DirectionalAuthoringSource source, PairedFieldOptions options)
    {
        if (source.InputHash != options.InputHash || JsonDefaults.Serialize(source.Members) != JsonDefaults.Serialize(options.PairedTransform.Members))
            throw Errors.InvalidRecipe("PAIRED_AUTHORING_SOURCE_DRIFT", "Options do not match complete current source members.", "Copy exact members from paired inspect; do not edit draw-call identities.");
        var recipe = PairedFieldOptionsJson.ToRecipe(options);
        var identity = ContentHash.Compute(JsonDefaults.SerializeToUtf8(options)).Value[..16];
        return recipe with { RecipeId = "scaffold-" + identity, Operations = [((TransformComponentOperation)recipe.Operations[0]) with { OperationId = "pair-" + identity }] };
    }

    public static async Task<ComponentDiscoveryResultV2> DiscoverAsync(ArtifactContent input, ModelSnapshot model, CancellationToken token = default)
    {
        // Do not invent a field/protection pair to turn a structural mapping into claimed admission.
        var strict = await new PreciseComponentDiscoveryService(null).DiscoverAsync(input, model, token).ConfigureAwait(false);
        var candidates = strict.Candidates.Select(candidate =>
        {
            ComponentCapability capability;
            try
            {
                var union = ComponentCandidateUnionBuilder.Create(model, [candidate], 2);
                _ = CoordinatedSelection.ResolveMembers(model, union, 1);
                capability = new("transform_component", 10, "unsupported", [new("PAIRED_OPTIONS_REQUIRED", "Complete structural mapping found; authored pair/protection and exact planner admission are required.")], []);
            }
            catch (S2ModKitException e) { capability = new("transform_component", 10, "unsupported", [new(e.Error.Code, e.Error.Summary)], []); }
            var id = candidate switch
            {
                MaterialGroupComponentCandidateV2 m => ComponentCandidateIdentity.ComputeMaterialCandidateId(m.Model, m.MaterialPath, m.Lods.ToArray(), 7),
                MeshLineageComponentCandidateV2 m => ComponentCandidateIdentity.ComputeLineageCandidateId(m.Model, m.LineageKey, m.SourceLabel, m.Lods.ToArray(), 7),
                _ => throw new InvalidOperationException("Unknown candidate kind."),
            };
            return candidate with { CandidateId = id, Capabilities = [capability] };
        }).ToArray();
        return strict with
        {
            SchemaVersion = 7,
            Candidates = candidates,
            ExperimentalPolicy = new("preserve_unverified", 1),
            DiscoveryFingerprint = ComponentCandidateIdentity.ComputeDiscoveryFingerprint(strict.Model, strict.Analyzer, candidates, strict.LineageDiagnostics, 7)
        };
    }
}
