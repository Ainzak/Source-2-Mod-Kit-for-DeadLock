using System.Text;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed partial class S2ModKitApplication : IPositionImportApplication
{
    private IPositionImportResourceAdapter PositionResource => rewriter as IPositionImportResourceAdapter ?? throw Errors.Unsupported("IMPORT_VERIFIER_REQUIRED", "No complete independent position-import adapter configured.", "Keep authoring data; no build is published.");
    private IPositionImportArtifactStore ImportStore => workspace as IPositionImportArtifactStore ?? throw Errors.Unsupported("IMPORT_STORE_REQUIRED", "No immutable authoring store configured.", "Use the normal CLI composition.");
    public async Task<object> RunPositionImportRequestAsync(ReadOnlyMemory<byte> request, CancellationToken token = default)
    {
        var r = PositionAuthoringJson.Read<PositionImportRequest>(request, PositionImportCompositionContract.MaximumRecipeBytes); PositionImportContract.ValidateRecipe(r.Recipe);
        if (r.SchemaVersion != 1 || r.Kind != "position_import_request" || r.Project.Length is < 1 or > 1024 || r.Action is not ("validate" or "plan")
            || r.Binding != ((ImportPositionsOperation)r.Recipe.Operations[0]).PositionImport.Binding) throw PositionAuthoringJson.Invalid("IMPORT_REQUEST_INVALID", "Require a closed bound validate/plan request.");
        if (r.Action == "plan") return await PlanAsync(r.Project, r.Recipe, token).ConfigureAwait(false);
        var assets = await ImportStore.LoadPositionImportAsync(r.Project, r.Binding, token).ConfigureAwait(false);
        _ = PositionImportContract.Selection((ImportPositionsOperation)r.Recipe.Operations[0], assets);
        return await ValidatePositionReturnAsync(r.Project, assets.Selection, assets.Bundle, r.Binding.BundleSha256, assets.Delta, token).ConfigureAwait(false);
    }
    public async Task<RecipeScaffoldResult> ScaffoldPositionImportAsync(string projectRoot, PositionImportAssets assets, ContentHash bundleHash, string outputPath, CancellationToken token = default)
    {
        var root = projectRoot;
        var selection = PositionReturnValidator.ReadSelection(assets.Selection); var canonical = PositionAuthoringJson.Canonical(selection);
        assets = assets with { Selection = canonical }; var binding = PositionImportContract.Binding(assets);
        if (binding.BundleSha256 != bundleHash) throw PositionAuthoringJson.Invalid("IMPORT_BUNDLE_HASH", "Expected trusted bundle hash differs.");
        var recipe = PositionImportContract.Recipe(selection, binding); PositionImportContract.ValidateRecipe(recipe);
        var (_, input, dependencies) = await LoadProjectGraphAsync(root, token).ConfigureAwait(false); var model = await inspector.InspectAsync(input, token).ConfigureAwait(false);
        var plan = await MakePositionImportPlan(input, model, recipe, assets, dependencies, token).ConfigureAwait(false);
        if (recipeDocumentWriter is null) throw Errors.Unsupported("RECIPE_OUTPUT_NOT_CONFIGURED", "No atomic recipe writer.", "Use normal CLI composition.");
        await ImportStore.StorePositionImportAsync(root, binding, assets, token).ConfigureAwait(false);
        var bytes = JsonDefaults.SerializeToUtf8(recipe); var path = await recipeDocumentWriter.WriteNewAsync(outputPath, bytes, token).ConfigureAwait(false);
        return new(path, ContentHash.Compute(bytes), plan.Fingerprint, selection.Members.Select(m => m.MemberId).ToArray(), recipe);
    }
    private async Task<MutationPlan> MakePositionImportPlan(ArtifactContent input, ModelSnapshot model, RecipeDocument recipe, PositionImportAssets assets, IReadOnlyList<ArtifactContent> dependencies, CancellationToken token)
    {
        PositionImportContract.ValidateRecipe(recipe);
        if (recipe.InputHash != input.ContentHash) throw PositionAuthoringJson.Invalid("IMPORT_SOURCE_DRIFT", "Recipe/source hashes differ.");
        var op = await PositionResource.PlanPositionImportAsync(input, model, (ImportPositionsOperation)recipe.Operations.Single(), assets, token).ConfigureAwait(false);
        var target = op.PositionImportTarget ?? throw PositionAuthoringJson.Invalid("IMPORT_PLAN_INVALID", "Missing import target.");
        if (target.InputHash != input.ContentHash || PositionImportContract.FactsHash(target.PositionImport) != PositionImportContract.FactsHash(((ImportPositionsOperation)recipe.Operations.Single()).PositionImport)
            || target.SourceBlocks.Count != model.Artifact.Blocks.Count || target.SourceBlocks.Any(b => !model.Artifact.Blocks.Any(a => a.Index == b.Index && a.Type == b.Type && a.ContentHash == b.InputHash)))
            throw PositionAuthoringJson.Invalid("IMPORT_PLAN_INVALID", "Planner intent or original resource inventory differs from immutable source.");
        var p = new MutationPlan(recipe.RecipeId, input.ContentHash, input.ContentHash, [op]) { SchemaVersion = 8, Inputs = dependencies.Prepend(input).OrderBy(i => i.LogicalPath, StringComparer.Ordinal).Select(i => new PlannedInput(i.LogicalPath, i.ContentHash, i.Bytes.Length)).ToArray() };
        p = p with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(p) }; PositionImportContract.ValidatePlan(p); return p;
    }
    private async Task<PlanRunResult> PlanPositionImport(string root, RecipeDocument recipe, CancellationToken token)
    {
        PositionImportContract.ValidateRecipe(recipe); var (project, input, deps) = await LoadProjectGraphAsync(root, token).ConfigureAwait(false);
        var assets = await ImportStore.LoadPositionImportAsync(root, ((ImportPositionsOperation)recipe.Operations.Single()).PositionImport.Binding, token).ConfigureAwait(false);
        var model = await inspector.InspectAsync(input, token).ConfigureAwait(false); var p = await MakePositionImportPlan(input, model, recipe, assets, deps, token).ConfigureAwait(false);
        await workspace.SavePlanAsync(root, p, token).ConfigureAwait(false);
        return new(p, CreateEvidence(project, "plan", p.Fingerprint.Value[..16], input, deps, null, model, null, p, [new("rewrite", "not_applicable", "No compiled bytes built."), new("runtime", "untested", "No live observation.")], []));
    }
    private async Task<BuildRunResult> BuildPositionImport(string root, RecipeDocument recipe, CancellationToken token)
    {
        PositionImportContract.ValidateRecipe(recipe); _ = PositionResource;
        var (project, input, deps) = await LoadProjectGraphAsync(root, token).ConfigureAwait(false);
        var assets = await ImportStore.LoadPositionImportAsync(root, ((ImportPositionsOperation)recipe.Operations.Single()).PositionImport.Binding, token).ConfigureAwait(false);
        var before = await inspector.InspectAsync(input, token).ConfigureAwait(false); var p = await MakePositionImportPlan(input, before, recipe, assets, deps, token).ConfigureAwait(false);
        await workspace.SavePlanAsync(root, p, token).ConfigureAwait(false);
        var candidate = await PositionResource.RewritePositionImportAsync(input, before, p, assets, token).ConfigureAwait(false); var output = new ArtifactContent(candidate.LogicalPath, ContentHash.Compute(candidate.Content.Span), candidate.Content);
        if (candidate.Snapshot.Artifact.ContentHash != output.ContentHash) throw Errors.Verification("CANDIDATE_HASH_MISMATCH", "Candidate bytes/snapshot differ.", "Reject candidate.");
        var snapshot = ModelVerifier.Verify(before, candidate.Snapshot, p); var verified = await PositionResource.VerifyPositionImportAsync(input, output, p, assets, token).ConfigureAwait(false);
        var external = await externalVerifier.VerifyAsync(candidate, token).ConfigureAwait(false);
        var boundaries = snapshot.Boundaries.Concat(verified.Boundaries).Append(external).Append(new BoundaryEvidence("runtime", "untested", "No live observation.")).ToArray();
        if (!snapshot.IsValid || boundaries.Any(b => b.Status == "failed")) throw Errors.Verification("POST_WRITE_VERIFICATION_FAILED", "Position import verification failed.", "No build was published.");
        var id = $"{p.Fingerprint.Value[..16]}-{output.ContentHash.Value[..12]}";
        var e = CreateEvidence(project, "build", id, input, deps, output, before, candidate.Snapshot, p, boundaries, snapshot.Warnings, positionImportVerification: verified);
        var published = await workspace.PublishBuildAsync(root, new(id, p, candidate, reports.RenderJson(e), reports.RenderMarkdown(e)), token).ConfigureAwait(false);
        return new(published.Build, JsonDefaults.Deserialize<EvidenceReport>(Encoding.UTF8.GetBytes(published.EvidenceJson), "Position import evidence"));
    }
    private async Task<VerifyRunResult> VerifyPositionImportBuild(string root, ProjectManifest project, ArtifactContent input, IReadOnlyList<ArtifactContent> deps, PublishedBuild published, ArtifactContent output, MutationPlan p, CancellationToken token)
    {
        PositionImportContract.ValidatePlan(p); ValidatePlanInputs(p, input, deps);
        var assets = await ImportStore.LoadPositionImportAsync(root, p.Operations[0].PositionImportTarget!.PositionImport.Binding, token).ConfigureAwait(false);
        var before = await inspector.InspectAsync(input, token).ConfigureAwait(false); var after = await inspector.InspectAsync(output, token).ConfigureAwait(false);
        var snapshot = ModelVerifier.Verify(before, after, p); var verified = await PositionResource.VerifyPositionImportAsync(input, output, p, assets, token).ConfigureAwait(false);
        var external = await externalVerifier.VerifyAsync(new(output.LogicalPath, output.Bytes, after), token).ConfigureAwait(false);
        var boundaries = snapshot.Boundaries.Concat(verified.Boundaries).Append(external).Append(new BoundaryEvidence("runtime", "untested", "No live observation.")).ToArray();
        if (!snapshot.IsValid || boundaries.Any(b => b.Status == "failed")) throw Errors.Verification("BUILD_VERIFICATION_FAILED", "Position import reverify failed.", "Do not use this build.");
        var id = published.BuildId + "-verify"; var e = CreateEvidence(project, "verify", id, input, deps, output, before, after, p, boundaries, snapshot.Warnings, positionImportVerification: verified);
        await workspace.SaveEvidenceAsync(root, id, reports.RenderJson(e), reports.RenderMarkdown(e), token).ConfigureAwait(false); return new(published, e);
    }
}
