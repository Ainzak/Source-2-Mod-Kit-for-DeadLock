using System.Globalization;
using S2ModKit.Domain;

namespace S2ModKit.Application;

internal static class ExperimentalTransformPlanValidator
{
    public static void Validate(TransformPlanningResult result, IReadOnlyList<SelectedDrawCall> selected,
        IReadOnlyDictionary<int, ResourceBlockSnapshot> blocks, TransformComponentOperation intent)
    {
        var target = result.ExperimentalTransformTarget;
        if (target is null || result.GeometryTargets is not { Count: 0 } || result.DistanceFieldTargets is not { Count: 0 }
            || result.CoupledTransformTarget is not null || result.AffineTransformTarget is not null
            || target.SourceBlocks is null || target.SourceBlocks.Any(block => block is null) || target.SourceBlocks.Count != blocks.Count
            || target.SourceBlocks.Select(block => block.Index).Distinct().Count() != blocks.Count
            || target.SourceBlocks.Any(block => !blocks.TryGetValue(block.Index, out var original)
                || original.Type != block.Type || original.ContentHash != block.InputHash))
            throw Invalid("The experimental planner did not freeze the complete immutable source inventory.");
        var selector = intent.Selector.Kind == "material_exact"
            ? intent.Selector with { MaterialPath = StableIdentity.NormalizePath(intent.Selector.MaterialPath!) }
            : intent.Selector with { DrawCallIds = intent.Selector.DrawCallIds!.Order(StringComparer.Ordinal).ToArray() };
        if (JsonDefaults.Serialize(selector) != JsonDefaults.Serialize(target.Selector)
            || JsonDefaults.Serialize(intent.Transform.Pivot) != JsonDefaults.Serialize(target.PivotIntent)
            || target.RuntimeMetadataPolicy != intent.RuntimeMetadataPolicy || target.UniformScale != intent.Transform.UniformScale
            || JsonDefaults.Serialize(target.Region?.Selection) != JsonDefaults.Serialize(intent.Region)
            || target.DisplacementLimit != intent.Limits.MaximumVertexDisplacement || target.Pivot is null || target.GeometryTargets is null
            || target.GeometryTargets.Any(item => item is null)
            || !target.GeometryTargets.Select(item => item.Lod).Order().SequenceEqual(selected.Select(item => item.Lod).Distinct().Order())
            || target.GeometryTargets.Any(item => item.SelectedVertexCount != intent.ExpectedVerticesByLod[item.Lod.ToString(CultureInfo.InvariantCulture)]
                || item.FrozenPivot != target.Pivot.Point || item.UniformScale != target.UniformScale
                || item.Translation is not { X: 0, Y: 0, Z: 0 } || item.BoneBoundsTargets is not { Count: 0 }
                || item.ConnectedComponentIds is not { Count: 0 }
                || item.AllowedChangedAttributes is null || !item.AllowedChangedAttributes.SequenceEqual(intent.Version == 6 ? ["normal_tangent", "position"] : ["position"], StringComparer.Ordinal)
                || !selected.Any(call => call.Lod == item.Lod && call.MeshOrdinal == item.MeshOrdinal && call.ResourceBlockIndex == item.ResourceBlockIndex)))
            throw Invalid("Experimental frozen intent or geometry does not match the validated recipe and selection.");
        if (intent.Version == 6) MutationPlanJson.ValidateRegion(target);
        var required = target.GeometryTargets.SelectMany(item => new[] { item.ResourceBlockIndex, item.VertexResourceBlockIndex }).Distinct().Order().ToArray();
        if (result.TargetBlocks is null || !result.TargetBlocks.Select(block => block.Index).Order().SequenceEqual(required))
            throw Invalid("Experimental mutation blocks do not exactly match the selected positions and characterized boxes.");
    }

    private static S2ModKitException Invalid(string message) => Errors.Verification("EXPERIMENTAL_PLAN_CONTRACT_INVALID", message,
        "Reject the planner result and regenerate it from immutable source facts.");
}
