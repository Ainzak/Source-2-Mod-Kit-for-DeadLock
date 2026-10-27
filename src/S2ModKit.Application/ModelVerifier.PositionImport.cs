using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed partial class ModelVerifier
{
    private static VerificationResult VerifyPositionImportSnapshot(ModelSnapshot before, ModelSnapshot after, MutationPlan p)
    {
        PositionImportContract.ValidatePlan(p);
        var targets = p.Operations[0].TargetBlocks.ToDictionary(b => b.Index);
        var inventory = before.Artifact.Blocks.Select(b => (b.Index, b.Type)).SequenceEqual(after.Artifact.Blocks.Select(b => (b.Index, b.Type)));
        var draws = PositionImportContract.FactsHash(before.Lods.SelectMany(l => l.Meshes.SelectMany(m => m.DrawCalls))) == PositionImportContract.FactsHash(after.Lods.SelectMany(l => l.Meshes.SelectMany(m => m.DrawCalls)));
        var unchanged = inventory && before.Artifact.Blocks.Where(b => !targets.ContainsKey(b.Index)).All(b => after.Artifact.Blocks.Single(a => a.Index == b.Index).ContentHash == b.ContentHash);
        var input = before.Artifact.ContentHash == p.InputHash && targets.Values.All(b => before.Artifact.Blocks.Any(a => a.Index == b.Index && a.Type == b.Type && a.ContentHash == b.InputHash));
        var boundaries = new[]{new BoundaryEvidence("import_snapshot_inventory",inventory?"passed":"failed","Complete compiled block identities retained."),
            new BoundaryEvidence("import_snapshot_draws",draws?"passed":"failed","Draw-call identities and material/index entries retained."),
            new BoundaryEvidence("import_snapshot_excluded",unchanged?"passed":"failed","Non-target block bytes retained."),
            new BoundaryEvidence("import_snapshot_source",input?"passed":"failed","Immutable source and target hashes checked.")};
        return new(boundaries.All(b => b.Status == "passed"), boundaries, []);
    }
}
