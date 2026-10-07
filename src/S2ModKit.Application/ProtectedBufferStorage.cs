using S2ModKit.Domain;

namespace S2ModKit.Application;

/// <summary>Shared storage predicates, independent of the single/paired field and compatibility policy.</summary>
internal sealed record ProtectedBufferStorage(IReadOnlyList<CoordinatedMember> Members, DirectionalProtection IntentProtection,
    IReadOnlyList<PlannedCoordinatedBuffer> Buffers, IReadOnlyList<DirectionalWordAudit> WordAudits,
    PlannedDirectionalProtection Protection, IReadOnlyList<DirectionalContextBuffer> ContextBuffers,
    IReadOnlyList<DirectionalCoincidenceLod> Coincidences, IReadOnlyList<PlannedExperimentalBoxTarget> BoxTargets,
    IReadOnlyList<DirectionalBoxClosure> BoxClosures, IReadOnlyList<PlannedExperimentalPreservationTarget> PreservationTargets,
    IReadOnlyList<PlannedTargetBlock> SourceBlocks)
{
    internal static ProtectedBufferStorage From(PlannedDirectionalTransformTarget t) => new(t.DirectionalTransform.Members,
        t.DirectionalTransform.Protection, t.Buffers, t.WordAudits, t.Protection, t.ContextBuffers, t.Coincidences,
        t.BoxTargets, t.BoxClosures, t.PreservationTargets, t.SourceBlocks);
    internal static ProtectedBufferStorage From(PlannedPairedTransformTarget t) => new(t.PairedTransform.Members,
        t.PairedTransform.Protection, t.Buffers, t.WordAudits, t.Protection, t.ContextBuffers, t.Coincidences,
        t.BoxTargets, t.BoxClosures, t.PreservationTargets, t.SourceBlocks);
}
