using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed partial class MutationPlanner
{
    private static void ValidateCoordinatedResult(TransformPlanningResult result, TransformComponentOperation intent,
        ContentHash inputHash, Dictionary<int, ResourceBlockSnapshot> blocks)
    {
        var t = result.CoordinatedTransformTarget;
        if (t is null || result.GeometryTargets.Count != 0 || result.DistanceFieldTargets.Count != 0 || result.EllipsoidTransformTarget is not null
            || result.ExperimentalTransformTarget is not null || result.AffineTransformTarget is not null || result.CoupledTransformTarget is not null)
            throw Errors.Verification("COORDINATED_RESULT_DRIFT", "The planner returned missing or mixed coordinated targets.", "Reject the incomplete result.");
        CoordinatedContractValidator.ValidateTarget(t);
        if (t.InputHash != inputHash || t.RuntimeMetadataPolicy != intent.RuntimeMetadataPolicy || t.ZeroBoneBoxPolicy != intent.ZeroBoneBoxPolicy
            || t.ZeroRenderSpherePolicy != intent.ZeroRenderSpherePolicy || JsonDefaults.Serialize(t.CoordinatedTransform) != JsonDefaults.Serialize(intent.CoordinatedTransform)
            || JsonDefaults.Serialize(t.Selector) != JsonDefaults.Serialize(intent.Selector) || t.DisplacementLimit != intent.Limits.MaximumVertexDisplacement
            || t.SourceBlocks.Count != blocks.Count || t.SourceBlocks.Any(b => !blocks.TryGetValue(b.Index, out var actual) || actual.Type != b.Type || actual.ContentHash != b.InputHash))
            throw Errors.Verification("COORDINATED_RESULT_DRIFT", "Frozen coordinated facts differ from recipe or source blocks.", "Reject the stale result.");
    }
}
