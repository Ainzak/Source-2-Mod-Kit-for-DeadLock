using System.Text;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed partial class S2ModKitApplication : IPositionImportCompositionApplication
{
    private IPositionImportCompositionAdapter CompositionResource => rewriter as IPositionImportCompositionAdapter
        ?? throw Errors.Unsupported("IMPORT_COMPOSITION_VERIFIER_REQUIRED", "No independent import/removal composition adapter configured.", "No build is published.");
    public async Task<RecipeScaffoldResult> ComposePositionImportRecipeAsync(string projectRoot, RecipeDocument import, RecipeDocument removal, string outputPath, CancellationToken token = default)
    {
        PositionImportContract.ValidateRecipe(import); RecipeValidator.Validate(removal);
        if (removal.SchemaVersion != 1 || removal.Operations is not [RemoveComponentOperation] || removal.InputHash != import.InputHash || removal.Extensions.Count != 0)
            throw PositionImportCompositionContract.Invalid("Require an original-source single removal recipe.");
        var recipe = import with { SchemaVersion = 13, RecipeId = "returned-positions-with-removal", Operations = [import.Operations[0], removal.Operations[0]] };
        PositionImportCompositionContract.ValidateRecipe(recipe);
        var (_, input, deps) = await LoadProjectGraphAsync(projectRoot, token).ConfigureAwait(false);
        var assets = await ImportStore.LoadPositionImportAsync(projectRoot, ((ImportPositionsOperation)recipe.Operations[0]).PositionImport.Binding, token).ConfigureAwait(false);
        var model = await inspector.InspectAsync(input, token).ConfigureAwait(false);
        var plan = await MakePositionImportCompositionPlan(input, model, recipe, assets, deps, token).ConfigureAwait(false);
        if (recipeDocumentWriter is null) throw Errors.Unsupported("RECIPE_OUTPUT_NOT_CONFIGURED", "No atomic recipe writer.", "Use normal CLI composition.");
        var bytes = JsonDefaults.SerializeToUtf8(recipe); var path = await recipeDocumentWriter.WriteNewAsync(outputPath, bytes, token).ConfigureAwait(false);
        return new(path, ContentHash.Compute(bytes), plan.Fingerprint, recipe.Operations.SelectMany(o => o.Selector.DrawCallIds!).ToArray(), recipe);
    }
    private async Task<MutationPlan> MakePositionImportCompositionPlan(ArtifactContent input, ModelSnapshot model, RecipeDocument recipe, PositionImportAssets assets, IReadOnlyList<ArtifactContent> deps, CancellationToken token)
    {
        PositionImportCompositionContract.ValidateRecipe(recipe); _ = CompositionResource;
        var imported = await MakePositionImportPlan(input, model, PositionImportCompositionContract.ImportRecipe(recipe), assets, deps, token).ConfigureAwait(false);
        var removed = MutationPlanner.CreatePlan(model, PositionImportCompositionContract.RemovalRecipe(recipe), deps).Operations.Single();
        var draws = await CompositionResource.PlanImportDrawProjectionAsync(input, removed, token).ConfigureAwait(false);
        return PositionImportCompositionContract.Assemble(imported, removed, draws);
    }
    private EvidenceReport CompositionEvidence(ProjectManifest project, string command, string id, ArtifactContent input, IReadOnlyList<ArtifactContent> deps,
        ModelSnapshot before, MutationPlan p, ArtifactContent? output, RewriteCandidate? candidate, PositionImportCompositionVerification? verified, IReadOnlyList<BoundaryEvidence> boundaries)
    {
        var inner = p.PositionImportComposition!.ImportPlan;
        var projection = verified is null ? null : new ArtifactContent(verified.Projection.LogicalPath, ContentHash.Compute(verified.Projection.Content.Span), verified.Projection.Content);
        var innerBoundaries = verified is null ? new[] { new BoundaryEvidence("runtime", "untested", "No live observation.") }
            : verified.ImportVerification.Boundaries.Append(new BoundaryEvidence("runtime", "untested", "No live observation.")).ToArray();
        var importEvidence = CreateEvidence(project, command, id + "-projection", input, deps, projection, before, verified?.Projection.Snapshot, inner, innerBoundaries, [], positionImportVerification: verified?.ImportVerification);
        var e = importEvidence with
        {
            SchemaVersion = 14,
            ReportId = id,
            PlanFingerprint = p.Fingerprint,
            Output = output is null ? null : new(output.LogicalPath, output.ContentHash, output.Bytes.Length, "generated", $"build:{output.ContentHash}", "sha256_and_semantic_reopen"),
            Operations = p.Operations.Select(o => new OperationEvidence(o.OperationId, o.Kind, o.Version, o.SelectedDrawCalls.Select(d => d.DrawCallId).ToArray(),
                o.SelectedDrawCalls.Select(d => d.ResourcePath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())).ToArray(),
            Blocks = CreateBlockEvidence(before, candidate?.Snapshot, p),
            Boundaries = boundaries,
            PositionImportComposition = new(p, projection?.ContentHash, importEvidence),
            Warnings = ["Coat render entries are removed; the exposed surface is the existing clothing mesh. Appearance, animated fit and preserved consumer behavior remain untested."]
        };
        PositionImportCompositionContract.ValidateEvidence(e); return e;
    }
    private async Task<PlanRunResult> PlanPositionImportComposition(string root, RecipeDocument recipe, CancellationToken token)
    {
        var (project, input, deps) = await LoadProjectGraphAsync(root, token).ConfigureAwait(false);
        PositionImportCompositionContract.ValidateRecipe(recipe);
        var assets = await ImportStore.LoadPositionImportAsync(root, ((ImportPositionsOperation)recipe.Operations[0]).PositionImport.Binding, token).ConfigureAwait(false);
        var model = await inspector.InspectAsync(input, token).ConfigureAwait(false); var p = await MakePositionImportCompositionPlan(input, model, recipe, assets, deps, token).ConfigureAwait(false);
        await workspace.SavePlanAsync(root, p, token).ConfigureAwait(false);
        var e = CompositionEvidence(project, "plan", p.Fingerprint.Value[..16], input, deps, model, p, null, null, null,
            [new("import_composition_draws", "not_applicable", "Original draw mapping planned; no output observed."), new("runtime", "untested", "No live observation.")]);
        return new(p, e);
    }
    private async Task<BuildRunResult> BuildPositionImportComposition(string root, RecipeDocument recipe, CancellationToken token)
    {
        PositionImportCompositionContract.ValidateRecipe(recipe); _ = CompositionResource;
        var (project, input, deps) = await LoadProjectGraphAsync(root, token).ConfigureAwait(false);
        var assets = await ImportStore.LoadPositionImportAsync(root, ((ImportPositionsOperation)recipe.Operations[0]).PositionImport.Binding, token).ConfigureAwait(false);
        var before = await inspector.InspectAsync(input, token).ConfigureAwait(false); var p = await MakePositionImportCompositionPlan(input, before, recipe, assets, deps, token).ConfigureAwait(false);
        await workspace.SavePlanAsync(root, p, token).ConfigureAwait(false);
        var candidate = await CompositionResource.RewritePositionImportCompositionAsync(input, before, p, assets, token).ConfigureAwait(false);
        var output = new ArtifactContent(candidate.LogicalPath, ContentHash.Compute(candidate.Content.Span), candidate.Content);
        if (candidate.Snapshot.Artifact.ContentHash != output.ContentHash) throw PositionImportCompositionContract.Invalid("Candidate snapshot/hash differs.");
        var snapshot = ModelVerifier.Verify(before, candidate.Snapshot, p);
        var verified = await CompositionResource.VerifyPositionImportCompositionAsync(input, output, p, assets, token).ConfigureAwait(false);
        var external = await externalVerifier.VerifyAsync(candidate, token).ConfigureAwait(false);
        var boundaries = snapshot.Boundaries.Concat(verified.ImportVerification.Boundaries).Concat(verified.Boundaries).Append(external).Append(new BoundaryEvidence("runtime", "untested", "No live observation.")).ToArray();
        if (!snapshot.IsValid || boundaries.Any(b => b.Status == "failed")) throw Errors.Verification("POST_WRITE_VERIFICATION_FAILED", "Composition verification failed.", "No build published.");
        var id = $"{p.Fingerprint.Value[..16]}-{output.ContentHash.Value[..12]}";
        var e = CompositionEvidence(project, "build", id, input, deps, before, p, output, candidate, verified, boundaries);
        var published = await workspace.PublishBuildAsync(root, new(id, p, candidate, reports.RenderJson(e), reports.RenderMarkdown(e)), token).ConfigureAwait(false);
        return new(published.Build, JsonDefaults.Deserialize<EvidenceReport>(Encoding.UTF8.GetBytes(published.EvidenceJson), "Composition evidence"));
    }
    private async Task<VerifyRunResult> VerifyPositionImportCompositionBuild(string root, ProjectManifest project, ArtifactContent input, IReadOnlyList<ArtifactContent> deps, PublishedBuild published, ArtifactContent output, MutationPlan p, CancellationToken token)
    {
        PositionImportCompositionContract.ValidatePlan(p); ValidatePlanInputs(p, input, deps);
        var assets = await ImportStore.LoadPositionImportAsync(root, p.Operations[0].PositionImportTarget!.PositionImport.Binding, token).ConfigureAwait(false);
        var before = await inspector.InspectAsync(input, token).ConfigureAwait(false); var after = await inspector.InspectAsync(output, token).ConfigureAwait(false);
        var candidate = new RewriteCandidate(output.LogicalPath, output.Bytes, after); var snapshot = ModelVerifier.Verify(before, after, p);
        var verified = await CompositionResource.VerifyPositionImportCompositionAsync(input, output, p, assets, token).ConfigureAwait(false);
        var external = await externalVerifier.VerifyAsync(candidate, token).ConfigureAwait(false);
        var boundaries = snapshot.Boundaries.Concat(verified.ImportVerification.Boundaries).Concat(verified.Boundaries).Append(external).Append(new BoundaryEvidence("runtime", "untested", "No live observation.")).ToArray();
        if (!snapshot.IsValid || boundaries.Any(b => b.Status == "failed")) throw Errors.Verification("BUILD_VERIFICATION_FAILED", "Composition reverify failed.", "Do not use this build.");
        var id = published.BuildId + "-verify"; var e = CompositionEvidence(project, "verify", id, input, deps, before, p, output, candidate, verified, boundaries);
        await workspace.SaveEvidenceAsync(root, id, reports.RenderJson(e), reports.RenderMarkdown(e), token).ConfigureAwait(false); return new(published, e);
    }
}
