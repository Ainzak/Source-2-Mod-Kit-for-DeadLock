using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed partial class ModelVerifier
{
    private static VerificationResult VerifyPositionImportCompositionSnapshot(ModelSnapshot before, ModelSnapshot after, MutationPlan p)
    {
        PositionImportCompositionContract.ValidatePlan(p); var c = p.PositionImportComposition!;
        var targets = c.TargetBlocks.Select(b => b.Index).ToHashSet();
        static SelectedDrawCall[] Draws(ModelSnapshot m) => m.Lods.OrderBy(l => l.Level).SelectMany(l => l.Meshes.OrderBy(x => x.MeshOrdinal).SelectMany(x => x.DrawCalls.Select(d =>
            new SelectedDrawCall(l.Level, x.ResourcePath, x.MeshOrdinal, x.ResourceBlockIndex, d.Id, d.MaterialPath, d.DrawCallOrdinal, d.IndexStart, d.IndexCount)))).ToArray();
        var inventory = before.Artifact.Blocks.Select(b => (b.Index, b.Type)).SequenceEqual(after.Artifact.Blocks.Select(b => (b.Index, b.Type)))
            && before.Lods.SelectMany(l => l.Meshes.Select(m => (l.Level, m.MeshOrdinal, m.ResourceBlockIndex))).SequenceEqual(after.Lods.SelectMany(l => l.Meshes.Select(m => (l.Level, m.MeshOrdinal, m.ResourceBlockIndex))));
        var draws = Draws(before).SequenceEqual(c.Draws.Select(d => d.Source)) && Draws(after).SequenceEqual(c.Draws.Where(d => d.Output is not null).Select(d => d.Output!));
        var unchanged = inventory && before.Artifact.Blocks.Where(b => !targets.Contains(b.Index)).All(b => after.Artifact.Blocks.Single(a => a.Index == b.Index).ContentHash == b.ContentHash);
        var input = before.Artifact.ContentHash == p.InputHash && c.TargetBlocks.All(b => before.Artifact.Blocks.Any(a => a.Index == b.Index && a.Type == b.Type && a.ContentHash == b.InputHash));
        var boundaries = new[]{new BoundaryEvidence("composition_snapshot_inventory",inventory?"passed":"failed","Complete original mesh/block identities retained."),
            new BoundaryEvidence("composition_snapshot_draws",draws?"passed":"failed","Exact original-to-output draw identity projection checked."),
            new BoundaryEvidence("composition_snapshot_excluded",unchanged?"passed":"failed","Non-target block bytes retained."),
            new BoundaryEvidence("composition_snapshot_source",input?"passed":"failed","Original immutable source and merged targets checked.")};
        return new(boundaries.All(b => b.Status == "passed"), boundaries, []);
    }
}
