using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed partial class MutationPlanner
{
    private static void ValidatePairedResult(TransformPlanningResult result, TransformComponentOperation intent,
        ContentHash inputHash, Dictionary<int, ResourceBlockSnapshot> blocks)
    {
        var t = result.PairedTransformTarget;
        if (t is null || result.GeometryTargets.Count != 0 || result.DistanceFieldTargets.Count != 0 || result.DirectionalTransformTarget is not null || result.CoordinatedTransformTarget is not null
            || result.EllipsoidTransformTarget is not null || result.ExperimentalTransformTarget is not null || result.AffineTransformTarget is not null || result.CoupledTransformTarget is not null)
            throw Errors.Verification("PAIRED_RESULT_DRIFT", "The planner returned missing or mixed paired targets.", "Reject the incomplete result.");
        PairedContractValidator.ValidateTarget(t);
        if (t.InputHash != inputHash || t.RuntimeMetadataPolicy != intent.RuntimeMetadataPolicy || t.ZeroBoneBoxPolicy != intent.ZeroBoneBoxPolicy
            || t.SourceTrianglePolicy != intent.SourceTrianglePolicy || t.ProceduralInputPolicy != intent.ProceduralInputPolicy
            || t.ZeroRenderSpherePolicy != intent.ZeroRenderSpherePolicy || JsonDefaults.Serialize(t.PairedTransform) != JsonDefaults.Serialize(intent.PairedTransform)
            || JsonDefaults.Serialize(t.Selector) != JsonDefaults.Serialize(intent.Selector) || t.DisplacementLimit != intent.Limits.MaximumVertexDisplacement
            || t.SourceBlocks.Count != blocks.Count || t.SourceBlocks.Any(b => !blocks.TryGetValue(b.Index, out var actual) || actual.Type != b.Type || actual.ContentHash != b.InputHash))
            throw Errors.Verification("PAIRED_RESULT_DRIFT", "Paired facts differ from recipe or source blocks.", "Regenerate the complete source-bound plan.");
    }
}
