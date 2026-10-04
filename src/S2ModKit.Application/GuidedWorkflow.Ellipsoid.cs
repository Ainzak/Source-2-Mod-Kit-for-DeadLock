using S2ModKit.Domain;

namespace S2ModKit.Application;

public static partial class GuidedWorkflow
{
    private static void ValidateEllipsoidSession(GuidedWorkflowSession session)
    {
        var afterParameters = session.Step is GuidedWorkflowContract.ReviewStep or GuidedWorkflowContract.OutputSelectionStep
            or GuidedWorkflowContract.InstallSelectionStep or GuidedWorkflowContract.RollbackSelectionStep or GuidedWorkflowContract.CompleteStep;
        if (session.SelectedOperationVersion == 7 && (session.SchemaVersion != 3 || session.SelectedOperationKind != "transform_component"
            || session.SelectedIntent is not (RecipeScaffoldContract.EllipsoidScaleIntent or RecipeScaffoldContract.MirroredEllipsoidScaleIntent)))
            throw InvalidState("The local-field action requires its versioned kind, intent and acknowledgement.");
        if (session.EllipsoidParameters is { } local)
        {
            if (local.LocalTransform is null || session.SchemaVersion != 3 || session.SelectedOperationKind != "transform_component" || session.SelectedOperationVersion != 7
                || session.SelectedIntent is not (RecipeScaffoldContract.EllipsoidScaleIntent or RecipeScaffoldContract.MirroredEllipsoidScaleIntent) || local.Policy != session.ExperimentalPolicy
                || session.ExperimentalParameters is not null || session.UniformScale != local.LocalTransform.UniformScale
                || session.TranslationX is not null || session.TranslationY is not null || session.TranslationZ is not null
                || session.MaximumCollisionDisplacement is not null || session.MaximumVertexDisplacement is null)
                throw InvalidState("Local-field parameters do not match the acknowledged action and limits.");
            if ((session.SelectedIntent == RecipeScaffoldContract.MirroredEllipsoidScaleIntent) != (local.LocalTransform.Field.Kind == "mirrored_ellipsoids")) throw InvalidState("Saved action and field variant disagree.");
            EllipsoidContractValidator.ValidateLocalTransform(local.LocalTransform, session.MaximumVertexDisplacement.Value);
        }
        if (session.SelectedOperationVersion == 7 && afterParameters && session.EllipsoidParameters is null)
            throw InvalidState("The local-field action requires complete typed parameters before review.");
        if (session.EllipsoidPreview is { } preview && (session.SelectedOperationVersion != 7 || session.EllipsoidParameters is null
            || session.PlanFingerprint != preview.PlanFingerprint || preview.PreviewFingerprint.Value is not { Length: 64 }
            || preview.SummaryHash.Value is not { Length: 64 } || preview.ContactSheetHash.Value is not { Length: 64 }
            || string.IsNullOrWhiteSpace(preview.SummaryPath) || string.IsNullOrWhiteSpace(preview.ContactSheetPath)))
            throw InvalidState("The local-field preview is incomplete or bound to a different plan.");
        if (session.SelectedOperationVersion == 7 && (session.Step is GuidedWorkflowContract.OutputSelectionStep or GuidedWorkflowContract.InstallSelectionStep
            or GuidedWorkflowContract.RollbackSelectionStep or GuidedWorkflowContract.CompleteStep) && session.EllipsoidPreview is null)
            throw InvalidState("A plan-bound selection preview is required before local-field output.");
    }
}
