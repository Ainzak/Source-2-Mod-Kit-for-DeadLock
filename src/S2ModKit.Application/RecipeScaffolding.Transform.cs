using System.Globalization;
using System.Text;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed partial class ComponentRecipeScaffolder
{
    private static ParsedTransform? ParseTransform(
        RecipeScaffoldRequest request,
        IEnumerable<string> lodKeys)
    {
        if (request.Affine is not null)
        {
            throw Errors.InvalidRecipe("SCAFFOLD_OPTIONS_INVALID", "Affine options require the affine intent.", "Choose --intent affine or remove the affine options.");
        }

        if (request.Intent == RecipeScaffoldContract.RemoveIntent)
        {
            if (request.UniformScale is not null
                || request.TranslationX is not null
                || request.TranslationY is not null
                || request.TranslationZ is not null
                || request.ReferenceLod is not null
                || request.MaximumVertexDisplacement is not null
                || request.MaximumCollisionDisplacement is not null)
            {
                throw Errors.InvalidRecipe(
                    "SCAFFOLD_OPTIONS_INVALID",
                    "Transform options cannot be combined with the remove intent.",
                    "Remove transform options or choose uniform-scale/translate.");
            }

            return null;
        }

        if (request.Intent is not (RecipeScaffoldContract.UniformScaleIntent or RecipeScaffoldContract.TranslateIntent))
        {
            throw Errors.InvalidRecipe(
                "SCAFFOLD_INTENT_UNSUPPORTED",
                $"Scaffold intent '{request.Intent}' is not supported.",
                "Use remove, uniform-scale, or translate.");
        }

        if (request.MaximumVertexDisplacement is null)
        {
            throw Errors.InvalidRecipe(
                "SCAFFOLD_MAX_DISPLACEMENT_REQUIRED",
                "Transform scaffolding requires an explicit maximum vertex displacement.",
                "Provide --max-displacement within the published safety limit.");
        }

        float scale;
        if (request.Intent == RecipeScaffoldContract.UniformScaleIntent)
        {
            scale = request.UniformScale
                ?? throw Errors.InvalidRecipe(
                    "SCAFFOLD_SCALE_REQUIRED",
                    "The uniform-scale intent requires an explicit scale.",
                    "Provide --scale within the supported [0.25, 4.0] range.");
        }
        else
        {
            if (request.UniformScale is not null)
            {
                throw Errors.InvalidRecipe(
                    "SCAFFOLD_OPTIONS_INVALID",
                    "The translate intent cannot include a uniform scale.",
                    "Remove --scale or choose the uniform-scale intent.");
            }

            if (request.TranslationX is null && request.TranslationY is null && request.TranslationZ is null)
            {
                throw Errors.InvalidRecipe(
                    "SCAFFOLD_TRANSLATION_REQUIRED",
                    "The translate intent requires at least one translation axis.",
                    "Provide --translate-x, --translate-y, or --translate-z.");
            }

            scale = 1f;
        }

        var lods = lodKeys.Select(key => int.Parse(key, CultureInfo.InvariantCulture)).Order().ToArray();
        var referenceLod = request.ReferenceLod ?? lods[0];
        if (!lods.Contains(referenceLod))
        {
            throw Errors.Selection(
                "SCAFFOLD_REFERENCE_LOD_INVALID",
                $"Reference LOD {referenceLod.ToString(CultureInfo.InvariantCulture)} is not present in the selected union.",
                "Choose a reference LOD reported by components list.");
        }

        return new ParsedTransform(
            scale,
            new TransformVector3
            {
                X = request.TranslationX ?? 0f,
                Y = request.TranslationY ?? 0f,
                Z = request.TranslationZ ?? 0f,
            },
            referenceLod,
            request.MaximumVertexDisplacement.Value,
            request.MaximumCollisionDisplacement);
    }

    private async Task<ComponentCapabilityAnalysis> AnalyzeTransformUnionAsync(
        ArtifactContent input,
        ModelSnapshot model,
        ComponentCandidateUnion union,
        CancellationToken cancellationToken)
    {
        if (capabilityAnalyzer is null || !capabilityAnalyzer.CanAnalyze(input, model))
        {
            throw Errors.Unsupported(
                "COMPONENT_CAPABILITY_ANALYZER_UNAVAILABLE",
                "No compatible transform capability analyzer is available for recipe scaffolding.",
                "Configure the model adapter and required geometry codec, then retry.");
        }

        var selection = new ComponentCapabilitySelection(
            "scaffold_union",
            ComponentDiscoveryV2Contract.CandidateUnionKind,
            union.MaterialPaths,
            union.SelectedDrawCalls);
        var analyses = await capabilityAnalyzer.AnalyzeAsync(
            new ComponentCapabilityAnalysisRequest(input, model, [selection]),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (analyses is null
            || analyses.Count != 1
            || analyses[0] is null
            || analyses[0].SelectionId != selection.SelectionId
            || analyses[0].OperationKind != TransformOperation
            || analyses[0].OperationVersion is not (1 or 2))
        {
            throw Errors.Selection(
                "COMPONENT_CAPABILITY_ANALYSIS_INVALID",
                "The capability analyzer did not return one exact transform assessment for the selected union.",
                "Reject the analyzer output and rerun with a compatible deterministic adapter.");
        }

        var analysis = analyses[0];
        if (analysis.Availability is not (
                ComponentDiscoveryContract.Available
                or ComponentDiscoveryContract.Blocked
                or ComponentDiscoveryContract.Unsupported
                or ComponentDiscoveryContract.Ambiguous)
            || analysis.Reasons is null
            || analysis.Reasons.Count == 0
            || analysis.Reasons.Any(item =>
                item is null
                || !IsStableReasonCode(item.Code)
                || string.IsNullOrWhiteSpace(item.Summary))
            || analysis.GeometryByLod is null)
        {
            throw Errors.Selection(
                "COMPONENT_CAPABILITY_ANALYSIS_INVALID",
                "The capability analyzer returned an invalid availability, reason, or geometry contract.",
                "Reject the analyzer output and rerun with a compatible deterministic adapter.");
        }

        if (analysis.Availability != ComponentDiscoveryContract.Available)
        {
            var reasons = string.Join(", ", analysis.Reasons.Select(item => item.Code).Order(StringComparer.Ordinal));
            var summary = $"The exact selected union is {analysis.Availability} for transform_component@{analysis.OperationVersion} ({reasons}).";
            if (analysis.Availability == ComponentDiscoveryContract.Ambiguous)
            {
                throw Errors.Selection(
                    "SCAFFOLD_TRANSFORM_AMBIGUOUS",
                    summary,
                    "Refresh component discovery and select an exact unambiguous union.");
            }

            throw Errors.Unsupported(
                analysis.Availability == ComponentDiscoveryContract.Blocked
                    ? "SCAFFOLD_TRANSFORM_BLOCKED"
                    : "SCAFFOLD_TRANSFORM_UNSUPPORTED",
                summary,
                "Inspect the stable capability reasons and choose a supported component union or configure the missing dependency.");
        }

        if (analysis.GeometryByLod.Count == 0
            || analysis.GeometryByLod.Any(item => item.SelectedVertexCount <= 0 || !item.ExclusivelyOwned)
            || analysis.GeometryByLod.GroupBy(item => item.Lod).Any(group => group.Count() != 1))
        {
            throw Errors.Selection(
                "COMPONENT_CAPABILITY_ANALYSIS_INVALID",
                "The available transform assessment contains incomplete or unsafe geometry facts.",
                "Reject the analyzer output and rerun with a compatible deterministic adapter.");
        }

        return analysis with { GeometryByLod = analysis.GeometryByLod.OrderBy(item => item.Lod).ToArray() };
    }

    private static SortedDictionary<string, int> CreateVertexCounts(
        IEnumerable<string> lodKeys,
        IReadOnlyList<ComponentGeometryLodFacts> geometry)
    {
        var byLod = geometry.ToDictionary(item => item.Lod);
        var result = new SortedDictionary<string, int>(LodKeyComparer.Instance);
        foreach (var key in lodKeys)
        {
            var lod = int.Parse(key, CultureInfo.InvariantCulture);
            if (!byLod.TryGetValue(lod, out var facts))
            {
                throw Errors.Selection(
                    "COMPONENT_CAPABILITY_ANALYSIS_INVALID",
                    $"The transform assessment omitted LOD {key}.",
                    "Reject the incomplete analyzer output.");
            }

            result.Add(key, facts.SelectedVertexCount);
        }

        if (result.Count != geometry.Count)
        {
            throw Errors.Selection(
                "COMPONENT_CAPABILITY_ANALYSIS_INVALID",
                "The transform assessment contains a LOD outside the selected union.",
                "Reject the inconsistent analyzer output.");
        }

        return result;
    }
    private sealed record ParsedTransform(
        float UniformScale,
        TransformVector3 Translation,
        int ReferenceLod,
        float MaximumVertexDisplacement,
        float? MaximumCollisionDisplacement);

    private sealed class LodKeyComparer : IComparer<string>
    {
        public static LodKeyComparer Instance { get; } = new();

        public int Compare(string? left, string? right) =>
            int.Parse(left!, CultureInfo.InvariantCulture).CompareTo(int.Parse(right!, CultureInfo.InvariantCulture));
    }
}
