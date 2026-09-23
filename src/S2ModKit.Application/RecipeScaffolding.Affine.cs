using System.Globalization;
using System.Text;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed partial class ComponentRecipeScaffolder
{
    private async Task<RecipeDocument> CreateAffineRecipeAsync(
        ArtifactContent input,
        ModelSnapshot model,
        IReadOnlyList<ComponentCandidateV2> candidates,
        ComponentCandidateUnion union,
        IReadOnlyDictionary<string, int> matchesByLod,
        RecipeScaffoldRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Affine is not { } affine
            || request.UniformScale is not null
            || request.MaximumCollisionDisplacement is not null
            || request.MaximumVertexDisplacement is null)
        {
            throw Errors.InvalidRecipe(
                "SCAFFOLD_AFFINE_OPTIONS_INVALID",
                "Affine scaffolding requires typed affine options and a vertex displacement cap, without legacy scale or collision options.",
                "Provide scale, rotation, pivot, frame, and --max-displacement with --intent affine.");
        }

        var analysis = await AnalyzeAffineUnionAsync(input, model, union, cancellationToken).ConfigureAwait(false);
        var referenceLod = int.Parse(matchesByLod.Keys.First(), CultureInfo.InvariantCulture);
        var pivot = affine.Pivot.Kind == "explicit_point"
            ? affine.Pivot
            : affine.Pivot with { ReferenceLod = request.ReferenceLod ?? affine.Pivot.ReferenceLod ?? referenceLod };
        var transform = new ComponentTransform
        {
            Pivot = pivot,
            Scale = affine.Scale,
            Rotation = affine.Rotation,
            Frame = affine.Frame,
            Translation = new TransformVector3
            {
                X = request.TranslationX ?? 0f,
                Y = request.TranslationY ?? 0f,
                Z = request.TranslationZ ?? 0f,
            },
        };
        var seed = new StringBuilder("s2modkit.recipe_scaffold_affine@1\n")
            .Append(input.ContentHash).Append('\n');
        foreach (var candidate in candidates.OrderBy(item => item.CandidateId, StringComparer.Ordinal))
        {
            seed.Append(candidate.CandidateId).Append('\n');
        }

        seed.Append(JsonDefaults.Serialize(transform)).Append('\n')
            .Append(request.MaximumVertexDisplacement.Value.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        var identity = ContentHash.Compute(Encoding.UTF8.GetBytes(seed.ToString())).Value[..16];
        var operation = new TransformComponentOperation
        {
            OperationId = $"transform-{identity}",
            Version = 4,
            Granularity = "draw_call_vertices",
            Selector = CreateSelector(union.SelectedDrawCalls),
            ExpectedMatchesByLod = matchesByLod,
            ExpectedVerticesByLod = CreateVertexCounts(matchesByLod.Keys, analysis.GeometryByLod),
            Transform = transform,
            Limits = new TransformLimits { MaximumVertexDisplacement = request.MaximumVertexDisplacement.Value },
        };
        var recipe = new RecipeDocument
        {
            SchemaVersion = 5,
            RecipeId = $"scaffold-{identity}",
            InputHash = input.ContentHash,
            Operations = [operation],
        };
        RecipeValidator.Validate(recipe);
        return recipe;
    }

    private async Task<ComponentCapabilityAnalysis> AnalyzeAffineUnionAsync(
        ArtifactContent input,
        ModelSnapshot model,
        ComponentCandidateUnion union,
        CancellationToken cancellationToken)
    {
        if (capabilityAnalyzer is not IAffineComponentCapabilityAnalyzer affineAnalyzer
            || !capabilityAnalyzer.CanAnalyze(input, model))
        {
            throw Errors.Unsupported(
                "COMPONENT_CAPABILITY_ANALYZER_UNAVAILABLE",
                "No configured analyzer can assess the selected affine component union.",
                "Configure the Source 2 model adapter and its geometry codec.");
        }

        var selection = new ComponentCapabilitySelection(
            "scaffold_union",
            ComponentDiscoveryV2Contract.CandidateUnionKind,
            union.MaterialPaths,
            union.SelectedDrawCalls);
        var returned = await affineAnalyzer.AnalyzeAffineAsync(
            new ComponentCapabilityAnalysisRequest(input, model, [selection]),
            cancellationToken).ConfigureAwait(false);
        if (returned is not [{ SelectionId: "scaffold_union", OperationKind: "transform_component", OperationVersion: 4 } analysis]
            || analysis.Reasons is null
            || analysis.Reasons.Count == 0
            || analysis.Reasons.Any(reason => reason is null
                || !IsStableReasonCode(reason.Code)
                || string.IsNullOrWhiteSpace(reason.Summary))
            || analysis.GeometryByLod is null)
        {
            throw Errors.Selection(
                "COMPONENT_CAPABILITY_ANALYSIS_INVALID",
                "The affine analyzer returned an incomplete or inconsistent union assessment.",
                "Reject the analyzer output and refresh component discovery.");
        }

        if (analysis.Availability != ComponentDiscoveryContract.Available)
        {
            var reasons = string.Join(", ", analysis.Reasons.Select(reason => reason.Code).Order(StringComparer.Ordinal));
            throw Errors.Unsupported(
                "SCAFFOLD_TRANSFORM_UNSUPPORTED",
                $"The selected union is {analysis.Availability} for transform_component@4 ({reasons}).",
                "Choose an affine-capable component or inspect the stable capability reasons.");
        }

        if (analysis.GeometryByLod.Count == 0
            || analysis.GeometryByLod.Any(fact => fact.SelectedVertexCount <= 0 || !fact.ExclusivelyOwned)
            || analysis.GeometryByLod.GroupBy(fact => fact.Lod).Any(group => group.Count() != 1))
        {
            throw Errors.Selection(
                "COMPONENT_CAPABILITY_ANALYSIS_INVALID",
                "The available affine assessment lacks complete exclusively owned vertex facts.",
                "Reject the incomplete analyzer output.");
        }

        return analysis with { GeometryByLod = analysis.GeometryByLod.OrderBy(fact => fact.Lod).ToArray() };
    }
}
