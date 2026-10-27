using System.Text.Json.Serialization;

namespace S2ModKit.Domain;

public sealed record PositionImportDrawProjection(SelectedDrawCall Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] SelectedDrawCall? Output, ContentHash EntryHash);
public sealed record PositionImportCompositionTarget(MutationPlan ImportPlan, IReadOnlyList<PositionImportDrawProjection> Draws,
    IReadOnlyList<PlannedTargetBlock> TargetBlocks);
public sealed record PositionImportCompositionEvidence(MutationPlan Plan,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ContentHash? ProjectionHash, EvidenceReport ImportEvidence);
