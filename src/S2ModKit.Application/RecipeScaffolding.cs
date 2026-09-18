using System.Globalization;
using System.Text;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public static class RecipeScaffoldContract
{
    public const string RemoveIntent = "remove";

    public const string UniformScaleIntent = "uniform-scale";

    public const string TranslateIntent = "translate";
}

public sealed record RecipeScaffoldRequest(
    IReadOnlyList<string> ComponentIds,
    string Intent,
    string OutputPath,
    float? UniformScale = null,
    float? TranslationX = null,
    float? TranslationY = null,
    float? TranslationZ = null,
    int? ReferenceLod = null,
    float? MaximumVertexDisplacement = null,
    float? MaximumCollisionDisplacement = null);

public sealed record RecipeScaffoldResult(
    string OutputPath,
    ContentHash RecipeContentHash,
    ContentHash DiscoveryFingerprint,
    IReadOnlyList<string> ComponentIds,
    RecipeDocument Recipe);

public sealed partial class ComponentRecipeScaffolder(IComponentCapabilityAnalyzer? capabilityAnalyzer)
{
    private const string TransformOperation = "transform_component";

    public async Task<RecipeDocument> CreateAsync(
        ArtifactContent input,
        ModelSnapshot model,
        ComponentDiscoveryResultV2 discovery,
        RecipeScaffoldRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateModelIdentity(input, model, discovery);
        var candidates = SelectCandidates(discovery, request.ComponentIds);
        var union = ComponentCandidateUnionBuilder.Create(model, candidates);
        var selectedDrawCalls = union.SelectedDrawCalls;
        var matchesByLod = CreateLodCounts(model, selectedDrawCalls);
        var transform = ParseTransform(request, matchesByLod.Keys);

        var seed = CreateIdentitySeed(input.ContentHash, candidates, request.Intent, transform);
        var identityHash = ContentHash.Compute(Encoding.UTF8.GetBytes(seed));
        RecipeOperation operation;
        var schemaVersion = 1;
        if (transform is null)
        {
            operation = new RemoveComponentOperation
            {
                OperationId = $"remove-{identityHash.Value[..16]}",
                Selector = CreateSelector(selectedDrawCalls),
                ExpectedMatchesByLod = matchesByLod,
            };
        }
        else
        {
            var analysis = await AnalyzeTransformUnionAsync(
                input,
                model,
                union,
                cancellationToken).ConfigureAwait(false);
            schemaVersion = analysis.OperationVersion == 2 ? 3 : 2;
            if (analysis.OperationVersion == 2
                && (request.Intent != RecipeScaffoldContract.UniformScaleIntent
                    || transform.Translation.X != 0f
                    || transform.Translation.Y != 0f
                    || transform.Translation.Z != 0f
                    || transform.MaximumCollisionDisplacement is null))
            {
                throw Errors.InvalidRecipe(
                    "SCAFFOLD_COUPLED_OPTIONS_INVALID",
                    "The available coupled transform profile requires uniform-scale, zero translation, and an explicit collision displacement cap.",
                    "Use --intent uniform-scale with --scale, --max-displacement, and --max-collision-displacement.");
            }

            if (analysis.OperationVersion == 1 && transform.MaximumCollisionDisplacement is not null)
            {
                throw Errors.InvalidRecipe(
                    "SCAFFOLD_OPTIONS_INVALID",
                    "The selected transform_component@1 profile is PHYS-immutable and does not accept a collision displacement cap.",
                    "Remove --max-collision-displacement.");
            }

            operation = new TransformComponentOperation
            {
                OperationId = $"transform-{identityHash.Value[..16]}",
                Version = analysis.OperationVersion,
                Selector = CreateSelector(selectedDrawCalls),
                ExpectedMatchesByLod = matchesByLod,
                ExpectedVerticesByLod = CreateVertexCounts(matchesByLod.Keys, analysis.GeometryByLod),
                PhysicsPolicy = analysis.OperationVersion == 2 ? "transform_coupled_convex" : null,
                Transform = new ComponentTransform
                {
                    Pivot = new TransformPivot
                    {
                        Kind = "selection_bounds_center",
                        ReferenceLod = transform.ReferenceLod,
                    },
                    UniformScale = transform.UniformScale,
                    Translation = transform.Translation,
                },
                Limits = new TransformLimits
                {
                    MaximumVertexDisplacement = transform.MaximumVertexDisplacement,
                    MaximumCollisionDisplacement = transform.MaximumCollisionDisplacement,
                },
            };
        }

        var recipe = new RecipeDocument
        {
            SchemaVersion = schemaVersion,
            RecipeId = $"scaffold-{identityHash.Value[..16]}",
            InputHash = input.ContentHash,
            Operations = [operation],
        };
        RecipeValidator.Validate(recipe);
        return recipe;
    }

    private static void ValidateModelIdentity(
        ArtifactContent input,
        ModelSnapshot model,
        ComponentDiscoveryResultV2 discovery)
    {
        string inputPath;
        string modelPath;
        string discoveryPath;
        try
        {
            inputPath = StableIdentity.NormalizePath(input.LogicalPath);
            modelPath = StableIdentity.NormalizePath(model.Artifact.LogicalPath);
            discoveryPath = StableIdentity.NormalizePath(discovery.Model.LogicalPath);
        }
        catch (ArgumentException exception)
        {
            throw new S2ModKitException(
                new S2Error(
                    "SCAFFOLD_INPUT_IDENTITY_INVALID",
                    "selection",
                    "Recipe scaffolding received an invalid model identity.",
                    "Reload the immutable project and rerun component discovery.",
                    ErrorCategory.SelectionOrLod),
                exception);
        }

        var computedHash = ContentHash.Compute(input.Bytes.Span);
        if (discovery.SchemaVersion != ComponentDiscoveryV2Contract.SchemaVersion
            || computedHash != input.ContentHash
            || input.ContentHash != model.Artifact.ContentHash
            || input.ContentHash != discovery.Model.ContentHash
            || input.Bytes.Length != model.Artifact.Size
            || input.Bytes.Length != discovery.Model.Size
            || !string.Equals(inputPath, modelPath, StringComparison.Ordinal)
            || !string.Equals(inputPath, discoveryPath, StringComparison.Ordinal))
        {
            throw Errors.Input(
                "SCAFFOLD_INPUT_DRIFT",
                "The immutable input, inspection, and component discovery identities do not match.",
                "Reload the project and scaffold from a fresh discovery result.");
        }
    }

    private static ComponentCandidateV2[] SelectCandidates(
        ComponentDiscoveryResultV2 discovery,
        IReadOnlyList<string> requestedIds)
    {
        if (requestedIds is null || requestedIds.Count == 0 || requestedIds.Any(string.IsNullOrWhiteSpace))
        {
            throw Errors.InvalidRecipe(
                "SCAFFOLD_COMPONENT_REQUIRED",
                "At least one non-empty component candidate ID is required.",
                "Copy one or more candidate IDs from the current components list output.");
        }

        var orderedIds = requestedIds.Order(StringComparer.Ordinal).ToArray();
        if (orderedIds.Distinct(StringComparer.Ordinal).Count() != orderedIds.Length)
        {
            throw Errors.Selection(
                "SCAFFOLD_COMPONENT_DUPLICATE",
                "The same component candidate was selected more than once.",
                "Provide every candidate ID exactly once.");
        }

        var candidatesById = discovery.Candidates
            .GroupBy(item => item.CandidateId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var selected = new List<ComponentCandidateV2>(orderedIds.Length);
        foreach (var id in orderedIds)
        {
            if (!candidatesById.TryGetValue(id, out var matches) || matches.Length != 1)
            {
                throw Errors.Selection(
                    "SCAFFOLD_COMPONENT_STALE",
                    $"Component candidate '{id}' does not identify exactly one current component.",
                    "Run components list again and use IDs from the current immutable project input.");
            }

            var candidate = matches[0];
            if (candidate.Model != discovery.Model)
            {
                throw Errors.Selection(
                    "SCAFFOLD_COMPONENT_STALE",
                    $"Component candidate '{id}' does not match the current discovery model.",
                    "Discard the inconsistent discovery result and inspect the project again.");
            }

            selected.Add(candidate);
        }

        return [.. selected];
    }

    private static SortedDictionary<string, int> CreateLodCounts(
        ModelSnapshot model,
        IReadOnlyList<SelectedDrawCall> selected)
    {
        var presentLods = model.Lods.Select(item => item.Level).Order().ToArray();
        if (presentLods.Length == 0
            || presentLods.Any(item => item < 0)
            || presentLods.Distinct().Count() != presentLods.Length)
        {
            throw Errors.Selection(
                "SCAFFOLD_LOD_SET_INVALID",
                "The inspected model has an empty or duplicate LOD set.",
                "Reject the inconsistent inspection and recreate the project.");
        }

        var result = new SortedDictionary<string, int>(LodKeyComparer.Instance);
        foreach (var lod in presentLods)
        {
            var count = selected.Count(item => item.Lod == lod);
            if (count == 0)
            {
                throw Errors.Selection(
                    "COMPONENT_INCOMPLETE_LOD_COVERAGE",
                    $"The exact selected union has no draw call in LOD {lod.ToString(CultureInfo.InvariantCulture)}.",
                    "Choose candidates whose exact union covers every present LOD.");
            }

            result.Add(lod.ToString(CultureInfo.InvariantCulture), count);
        }

        if (selected.Any(item => !presentLods.Contains(item.Lod)))
        {
            throw Errors.Selection(
                "SCAFFOLD_COMPONENT_STALE",
                "The selected candidates contain a draw call outside the current model LOD set.",
                "Run components list again and use the current candidate IDs.");
        }

        return result;
    }

    private static ComponentSelector CreateSelector(IReadOnlyList<SelectedDrawCall> selected) => new()
    {
        Kind = "draw_call_ids",
        DrawCallIds = selected.Select(item => item.DrawCallId).ToArray(),
    };

    private static bool IsStableReasonCode(string? code) =>
        !string.IsNullOrWhiteSpace(code)
        && code.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');

    private static string CreateIdentitySeed(
        ContentHash inputHash,
        IReadOnlyList<ComponentCandidateV2> candidates,
        string intent,
        ParsedTransform? transform)
    {
        var text = new StringBuilder("s2modkit.recipe_scaffold@1\n")
            .Append(inputHash).Append('\n')
            .Append(intent).Append('\n');
        foreach (var candidate in candidates.OrderBy(item => item.CandidateId, StringComparer.Ordinal))
        {
            text.Append(candidate.CandidateId).Append('\n');
        }

        if (transform is not null)
        {
            text.Append(transform.UniformScale.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(transform.Translation.X.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(transform.Translation.Y.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(transform.Translation.Z.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(transform.ReferenceLod.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(transform.MaximumVertexDisplacement.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(transform.MaximumCollisionDisplacement?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty).Append('\n');
        }

        return text.ToString();
    }

}
