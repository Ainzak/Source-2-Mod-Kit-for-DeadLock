using System.Text.Json;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public static class PositionImportCompositionContract
{
    public const int MaximumRecipeBytes = 16 * 1024 * 1024;
    public static RecipeDocument ImportRecipe(RecipeDocument r) => r with { SchemaVersion = 12, Operations = [r.Operations[0]] };
    public static RecipeDocument RemovalRecipe(RecipeDocument r) => r with { SchemaVersion = 1, Operations = [r.Operations[1]] };
    public static void ValidateRecipe(RecipeDocument r)
    {
        RecipeValidator.Validate(r);
        if (r.SchemaVersion != 13) throw Invalid("Composition requires recipe 13.");
        PositionImportContract.ValidateRecipe(ImportRecipe(r));
        var removal = (RemoveComponentOperation)r.Operations[1];
        if (removal.Extensions.Count != 0 || removal.Selector.DrawCallIds!.Count == 0
            || removal.Selector.DrawCallIds!.Distinct(StringComparer.Ordinal).Count() != removal.Selector.DrawCallIds!.Count)
            throw Invalid("Removal requires unique original draw IDs and no extensions.");
    }
    public static MutationPlan Assemble(MutationPlan import, PlannedOperation removal, IReadOnlyList<PositionImportDrawProjection> draws)
    {
        PositionImportContract.ValidatePlan(import);
        var blocks = import.Operations[0].TargetBlocks.Concat(removal.TargetBlocks).Distinct().OrderBy(b => b.Index).ToArray();
        var p = new MutationPlan(import.RecipeId, import.InputHash, import.InputHash, [import.Operations[0], removal])
        { SchemaVersion = 9, Inputs = import.Inputs, PositionImportComposition = new(import, draws, blocks) };
        p = p with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(p) }; ValidatePlan(p); return p;
    }
    public static void ValidatePlan(MutationPlan p)
    {
        if (p.SchemaVersion != 9 || p.PositionImportComposition is not { } c || p.Operations is not [{ Kind: "import_positions", Version: 1 }, { Kind: "remove_component", Version: 1 } removal]
            || c.ImportPlan.PositionImportComposition is not null) throw Invalid("Require one explicitly composed import and removal.");
        PositionImportContract.ValidatePlan(c.ImportPlan);
        var source = c.ImportPlan.Operations[0].PositionImportTarget!;
        if (p.Fingerprint != MutationPlanJson.ComputeExperimentalFingerprint(p) || p.InputHash != c.ImportPlan.InputHash || p.RecipeId != c.ImportPlan.RecipeId
            || PositionImportContract.FactsHash(p.Inputs) != PositionImportContract.FactsHash(c.ImportPlan.Inputs)
            || PositionImportContract.FactsHash(p.Operations[0]) != PositionImportContract.FactsHash(c.ImportPlan.Operations[0])
            || p.Operations[0].OperationId == removal.OperationId || removal.SelectedDrawCalls.Count == 0
            || removal.PositionImportTarget is not null || removal.GeometryTargets.Count != 0 || removal.DistanceFieldTargets.Count != 0
            || removal.ExperimentalTransformTarget is not null || removal.EllipsoidTransformTarget is not null || removal.CoordinatedTransformTarget is not null
            || removal.DirectionalTransformTarget is not null || removal.PairedTransformTarget is not null || removal.CoupledTransformTarget is not null || removal.AffineTransformTarget is not null
            || removal.TargetBlocks.Any(b => b.Type != "MDAT" || !source.SourceBlocks.Contains(b) || !source.ContextBuffers.Any(x => x.ResourceBlockIndex == b.Index)))
            throw Invalid("Source binding, original operation or compatible metadata closure differs.");
        var removed = removal.SelectedDrawCalls.Select(d => d.DrawCallId).ToHashSet(StringComparer.Ordinal);
        if (removed.Count != removal.SelectedDrawCalls.Count || p.Operations[0].SelectedDrawCalls.Any(d => removed.Contains(d.DrawCallId))
            || !removal.SelectedDrawCalls.Select(d => d.Lod).Distinct().Order().SequenceEqual(p.Operations[0].SelectedDrawCalls.Select(d => d.Lod).Distinct().Order())
            || !removal.TargetBlocks.Select(b => b.Index).Order().SequenceEqual(removal.SelectedDrawCalls.Select(d => d.ResourceBlockIndex).Distinct().Order())
            || !c.TargetBlocks.SequenceEqual(p.Operations.SelectMany(o => o.TargetBlocks).Distinct().OrderBy(b => b.Index)))
            throw Invalid("Overlapping selections, incomplete LODs or merged targets differ.");
        if (c.Draws.Count is < 1 or > 65536 || !c.Draws.SequenceEqual(c.Draws.OrderBy(d => d.Source.Lod).ThenBy(d => d.Source.MeshOrdinal).ThenBy(d => d.Source.DrawCallOrdinal))
            || c.Draws.Select(d => d.Source.DrawCallId).Distinct(StringComparer.Ordinal).Count() != c.Draws.Count
            || c.Draws.Where(d => d.Output is not null).Select(d => d.Output!.DrawCallId).Distinct(StringComparer.Ordinal).Count() != c.Draws.Count(d => d.Output is not null)
            || !c.Draws.Where(d => d.Output is null).Select(d => d.Source).OrderBy(d => d.DrawCallId, StringComparer.Ordinal).SequenceEqual(removal.SelectedDrawCalls.OrderBy(d => d.DrawCallId, StringComparer.Ordinal)))
            throw Invalid("Complete canonical source draw projection differs.");
        foreach (var group in c.Draws.GroupBy(d => (d.Source.Lod, d.Source.MeshOrdinal)))
        {
            var outputOrdinal = 0; var sourceOrdinal = 0;
            foreach (var d in group)
            {
                PositionAuthoringJson.Hash(d.EntryHash.Value);
                if (d.Source.DrawCallOrdinal != sourceOrdinal++ || d.Source != Draw(d.Source, d.Source.DrawCallOrdinal)) throw Invalid("Source draw identity is inconsistent.");
                if (d.Output is { } output && output != Draw(d.Source, outputOrdinal++)) throw Invalid("Output draw identity does not follow original removal order.");
            }
        }
        if (!p.Operations[0].SelectedDrawCalls.All(d => c.Draws.Any(x => x.Source == d && x.Output is not null))) throw Invalid("Imported draws are missing from the retained projection.");
    }
    public static SelectedDrawCall Draw(SelectedDrawCall source, int ordinal)
    {
        var d = DrawCallSnapshot.Create(source.ResourcePath, source.Lod, source.MeshOrdinal, ordinal, source.MaterialPath, source.IndexStart, source.IndexCount);
        return source with { DrawCallId = d.Id, DrawCallOrdinal = ordinal };
    }
    public static void ValidateEvidence(EvidenceReport e)
    {
        if (e.SchemaVersion != 14 || e.PositionImportComposition is not { } c || e.ProofLevel != "offline_static" || e.Status != "passed"
            || e.Extensions.Count != 0 || e.Command is not ("plan" or "build" or "verify")) throw Invalid("Require passed closed offline composition evidence.");
        ValidatePlan(c.Plan); PositionImportContract.ValidateEvidence(c.ImportEvidence);
        if (e.Input != c.ImportEvidence.Input || e.Command != c.ImportEvidence.Command || e.PlanFingerprint != c.Plan.Fingerprint || e.Input.ContentHash != c.Plan.InputHash
            || PositionImportContract.FactsHash(e.Dependencies) != PositionImportContract.FactsHash(c.ImportEvidence.Dependencies)
            || c.ImportEvidence.PlanFingerprint != c.Plan.PositionImportComposition!.ImportPlan.Fingerprint
            || e.Operations.Count != 2 || !e.Operations.Select(o => (o.OperationId, o.Kind, o.Version)).SequenceEqual(c.Plan.Operations.Select(o => (o.OperationId, o.Kind, o.Version)))
            || e.Operations.Where((o, i) => !o.SelectedDrawCallIds.SequenceEqual(c.Plan.Operations[i].SelectedDrawCalls.Select(d => d.DrawCallId))).Any()
            || e.Operations.Any(o => o.PositionImport is not null || o.GeometryChanges.Count != 0 || o.PairedTransform is not null || o.DirectionalTransform is not null
                || o.CoordinatedTransform is not null || o.EllipsoidTransform is not null || o.ExperimentalTransform is not null || o.AffineTransform is not null || o.CoupledTransform is not null)
            || e.Boundaries.Any(b => b.Status == "failed")) throw Invalid("Composition evidence/source/operation bindings differ.");
        if (e.Output is null)
        {
            if (e.Command != "plan" || c.ProjectionHash is not null || c.ImportEvidence.Output is not null) throw Invalid("Dry-run cannot claim observed output.");
            return;
        }
        if (e.Command is not ("build" or "verify") || c.ProjectionHash is null || c.ImportEvidence.Output?.ContentHash != c.ProjectionHash
            || e.Output.LogicalPath != e.Input.LogicalPath || c.ImportEvidence.Output.LogicalPath != e.Output.LogicalPath
            || e.Boundaries.Count(b => b.Name == "import_composition_draws" && b.Status == "passed") != 1
            || e.Boundaries.Count(b => b.Name == "import_composition_envelope" && b.Status == "passed") != 1
            || e.Boundaries.Count(b => b.Name == "runtime" && b.Status == "untested") != 1)
            throw Invalid("Actual output and independently audited projection are not completely bound.");
    }
    internal static bool RecipeShape(JsonElement root, int size)
    {
        if (!EllipsoidContractJson.HasVersion(root, 13)) return false;
        if (size > MaximumRecipeBytes) throw Invalid("Composition recipe exceeds 16 MiB.");
        PositionImportContractJson.Keys(root, "schemaVersion", "recipeId", "inputHash", "operations", "extensions");
        if (root.GetProperty("operations").GetArrayLength() != 2) throw Invalid("Require exactly two composition operations.");
        PositionImportContractJson.Keys(root.GetProperty("operations")[0], "operationId", "kind", "version", "granularity", "selector", "lodPolicy", "expectedMatchesByLod", "expectedVerticesByLod", "ownershipPolicy", "runtimeMetadataPolicy", "limits", "extensions", "zeroBoneBoxPolicy", "zeroRenderSpherePolicy", "sourceTrianglePolicy", "proceduralInputPolicy", "positionImport");
        PositionImportContractJson.Keys(root.GetProperty("operations")[1], "operationId", "kind", "version", "granularity", "selector", "lodPolicy", "expectedMatchesByLod", "extensions");
        return true;
    }
    internal static MutationPlan ReadPlan(ReadOnlySpan<byte> bytes)
    {
        var p = PositionAuthoringJson.Read<MutationPlan>(bytes.ToArray());
        using var doc = JsonDocument.Parse(bytes.ToArray());
        PositionImportContractJson.Keys(doc.RootElement, "schemaVersion", "recipeId", "inputHash", "fingerprint", "operations", "inputs", "positionImportComposition");
        var operations = doc.RootElement.GetProperty("operations");
        if (operations.GetArrayLength() != 2) throw Invalid("Exactly two original operations required.");
        PositionImportContractJson.Keys(operations[0], "operationId", "kind", "version", "selectedDrawCalls", "targetBlocks", "geometryTargets", "distanceFieldTargets", "positionImportTarget");
        PositionImportContractJson.Keys(operations[1], "operationId", "kind", "version", "selectedDrawCalls", "targetBlocks", "geometryTargets", "distanceFieldTargets");
        _ = MutationPlanJson.Read(System.Text.Encoding.UTF8.GetBytes(doc.RootElement.GetProperty("positionImportComposition").GetProperty("importPlan").GetRawText()));
        ValidatePlan(p); return p;
    }
    internal static void EvidenceShape(JsonElement root)
    {
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Null) throw Invalid("Omit unobserved output; explicit null does not match the evidence schema.");
        var keys = new[] { "schemaVersion", "reportId", "createdUtc", "proofLevel", "command", "status", "input", "dependencies", "planFingerprint", "operations", "boundaries", "blocks", "toolVersions", "warnings", "extensions", "positionImportComposition" };
        PositionImportContractJson.Keys(root, root.TryGetProperty("output", out _) ? keys.Append("output").ToArray() : keys);
        var operations = root.GetProperty("operations");
        if (operations.GetArrayLength() != 2) throw Invalid("Require two composition evidence operations.");
        foreach (var op in operations.EnumerateArray()) PositionImportContractJson.Keys(op, "operationId", "kind", "version", "selectedDrawCallIds", "changedResources", "geometryChanges");
        var c = root.GetProperty("positionImportComposition"); PositionImportContractJson.Keys(c, "plan", "projectionHash", "importEvidence");
        _ = ReadPlan(System.Text.Encoding.UTF8.GetBytes(c.GetProperty("plan").GetRawText()));
        ExperimentalEvidenceValidator.ValidateShape(System.Text.Encoding.UTF8.GetBytes(c.GetProperty("importEvidence").GetRawText()));
    }
    internal static S2ModKitException Invalid(string message) => PositionAuthoringJson.Invalid("IMPORT_COMPOSITION_INVALID", message);
}
