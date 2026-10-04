using S2ModKit.Domain;

namespace S2ModKit.Application;

public static partial class GuidedWorkflow
{
    private static void ValidateCoordinatedSession(GuidedWorkflowSession session)
    {
        if (session.SchemaVersion != 4)
        {
            if (session.CoordinatedParameters is not null || session.SelectedComponentIds is not null || session.CoordinatedPreview is not null || session.SelectedOperationVersion == 8
                || session.SelectedIntent == RecipeScaffoldContract.CoordinatedFieldIntent)
                throw InvalidState("Coordinated fields and unions require a new acknowledged version-4 session.");
            return;
        }
        if (session.CoordinatedParameters is not { } options || options.Policy != session.ExperimentalPolicy
            || session.ExperimentalParameters is not null || session.EllipsoidParameters is not null || session.EllipsoidPreview is not null
            || session.TranslationX is not null || session.TranslationY is not null || session.TranslationZ is not null || session.UniformScale is not null
            || session.MaximumCollisionDisplacement is not null
            || (session.MaximumVertexDisplacement is not null && session.MaximumVertexDisplacement != options.MaximumDisplacement)
            || (session.SelectedOperationVersion is not null && (session.SelectedOperationVersion != 8 || session.SelectedOperationKind != "transform_component"
                || session.SelectedIntent != RecipeScaffoldContract.CoordinatedFieldIntent)))
            throw InvalidState("Coordinated session facts mix unrelated actions or disagree with explicit common-field options.");
        CoordinatedSelection.ValidateOptions(options);
        if (session.SelectedComponentIds is { } ids && (ids.Count == 0 || ids.Any(string.IsNullOrWhiteSpace)
            || !ids.SequenceEqual(ids.Distinct().Order(StringComparer.Ordinal)) || session.SelectedComponentId != CoordinatedChoiceId(ids)))
            throw InvalidState("Saved coordinated selection is duplicated, unsorted or differs from its action identity.");
        if (session.Step is GuidedWorkflowContract.ActionSelectionStep or GuidedWorkflowContract.ReviewStep or GuidedWorkflowContract.OutputSelectionStep
            or GuidedWorkflowContract.InstallSelectionStep or GuidedWorkflowContract.RollbackSelectionStep or GuidedWorkflowContract.CompleteStep
            && session.SelectedComponentIds is null) throw InvalidState("The exact coordinated candidate union must persist before action selection.");
        if (session.CoordinatedPreview is { } preview && (session.SelectedOperationVersion != 8 || session.PlanFingerprint != preview.PlanFingerprint
            || preview.PreviewFingerprint.Value is not { Length: 64 } || preview.SummaryHash.Value is not { Length: 64 } || preview.ContactSheetHash.Value is not { Length: 64 }
            || string.IsNullOrWhiteSpace(preview.SummaryPath) || string.IsNullOrWhiteSpace(preview.ContactSheetPath)))
            throw InvalidState("Coordinated preview is incomplete or bound to a different plan.");
        if (session.Step is GuidedWorkflowContract.OutputSelectionStep or GuidedWorkflowContract.InstallSelectionStep or GuidedWorkflowContract.RollbackSelectionStep
            or GuidedWorkflowContract.CompleteStep && session.CoordinatedPreview is null) throw InvalidState("Coordinated output requires a reviewed plan-bound preview.");
    }

    public static string CoordinatedChoiceId(IReadOnlyList<string> ids) => "union_" + ContentHash.Compute(JsonDefaults.SerializeToUtf8(ids.Order(StringComparer.Ordinal).ToArray())).Value[..24];

    public static GuidedComponentChoice CreateCoordinatedChoice(CoordinatedSelectionProbe probe)
    {
        if (probe.Capability is not { OperationKind: "transform_component", OperationVersion: 8, Availability: "available" })
            throw Errors.Unsupported("GUIDED_COORDINATED_ACTION_UNAVAILABLE", "The exact union has no verified common-field action.", "Inspect the named storage or numerical blocker.");
        return new(CoordinatedChoiceId(probe.ComponentIds), "Selected ordinary buffers under one common field", "coordinated_union",
            probe.Members[0].Lods.Select(l => l.Lod).ToArray(),
            [new("transform_component@8:coordinated-field", "Apply common field to selected buffers", RecipeScaffoldContract.CoordinatedFieldIntent, "transform_component", 8, false, false, false)],
            probe.Members.SelectMany(m => m.Lods).Sum(l => l.DrawCallIds.Count));
    }
}
