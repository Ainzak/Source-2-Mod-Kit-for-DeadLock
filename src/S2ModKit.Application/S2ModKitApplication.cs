using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed partial class S2ModKitApplication : IS2ModKitApplication
{
    private readonly IProjectWorkspace workspace;
    private readonly ProjectImporter projectImporter;
    private readonly IModelInspector inspector;
    private readonly IModelRewriter rewriter;
    private readonly ITransformOperationPlanner? transformPlanner;
    private readonly IComponentCapabilityAnalyzer? componentCapabilityAnalyzer;
    private readonly IRecipeDocumentWriter? recipeDocumentWriter;
    private readonly IExternalVerifier externalVerifier;
    private readonly IReportRenderer reports;
    private readonly IClock clock;

    public S2ModKitApplication(
        IProjectWorkspace workspace,
        IModelInspector inspector,
        IResourceDependencyReader dependencyReader,
        IModelRewriter rewriter,
        IExternalVerifier externalVerifier,
        IReportRenderer reports,
        IClock clock,
        IProjectResourceSourceFactory resourceSourceFactory,
        IVpkProjectResourceSourceFactory? vpkResourceSourceFactory = null,
        ITransformOperationPlanner? transformPlanner = null,
        IComponentCapabilityAnalyzer? componentCapabilityAnalyzer = null,
        IRecipeDocumentWriter? recipeDocumentWriter = null)
    {
        this.workspace = workspace;
        projectImporter = new ProjectImporter(workspace, dependencyReader, resourceSourceFactory, vpkResourceSourceFactory);
        this.inspector = inspector;
        this.rewriter = rewriter;
        this.transformPlanner = transformPlanner;
        this.componentCapabilityAnalyzer = componentCapabilityAnalyzer;
        this.recipeDocumentWriter = recipeDocumentWriter;
        this.externalVerifier = externalVerifier;
        this.reports = reports;
        this.clock = clock;
    }

    public Task<ProjectManifest> CreateProjectAsync(string projectRoot, string inputPath, string resourceRoot, CancellationToken cancellationToken = default) =>
        projectImporter.CreateProjectAsync(new ProjectCreationRequest(projectRoot, inputPath, resourceRoot, clock.UtcNow), cancellationToken);

    public Task<ProjectManifest> CreateProjectAsync(
        string projectRoot,
        string inputPath,
        string resourceRoot,
        IReadOnlyList<string> runtimeResourceRoots,
        CancellationToken cancellationToken = default) =>
        projectImporter.CreateProjectAsync(new ProjectCreationRequest(projectRoot, inputPath, resourceRoot, clock.UtcNow, runtimeResourceRoots), cancellationToken);

    public Task<ProjectManifest> CreateVpkProjectAsync(
        string projectRoot,
        string baseVpkPath,
        string inputLogicalPath,
        ContentHash? expectedDirectoryHash = null,
        CancellationToken cancellationToken = default) =>
        projectImporter.CreateVpkProjectAsync(new VpkProjectCreationRequest(projectRoot, baseVpkPath, inputLogicalPath, clock.UtcNow, expectedDirectoryHash), cancellationToken);

    public async Task<InspectionRunResult> InspectAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var (project, input, _) = await LoadProjectGraphAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        RequireInspector(input);
        var model = await inspector.InspectAsync(input, cancellationToken).ConfigureAwait(false);
        return new InspectionRunResult(project, model);
    }

    public async Task<ComponentDiscoveryResultV2> DiscoverComponentsAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        var (_, input, _) = await LoadProjectGraphAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        RequireInspector(input);
        var model = await inspector.InspectAsync(input, cancellationToken).ConfigureAwait(false);
        return await new PreciseComponentDiscoveryService(componentCapabilityAnalyzer)
            .DiscoverAsync(input, model, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ComponentDiscoveryResultV2> DiscoverExperimentalComponentsAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var (_, input, _) = await LoadProjectGraphAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        RequireInspector(input);
        var model = await inspector.InspectAsync(input, cancellationToken).ConfigureAwait(false);
        return await new ExperimentalComponentDiscoveryService(componentCapabilityAnalyzer, transformPlanner)
            .DiscoverAsync(input, model, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ComponentDiscoveryResultV2> DiscoverEllipsoidComponentsAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var (_, input, _) = await LoadProjectGraphAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        RequireInspector(input);
        var model = await inspector.InspectAsync(input, cancellationToken).ConfigureAwait(false);
        return await new ExperimentalComponentDiscoveryService(componentCapabilityAnalyzer, transformPlanner)
            .DiscoverEllipsoidAsync(input, model, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RecipeScaffoldResult> ScaffoldRecipeAsync(
        string projectRoot,
        RecipeScaffoldRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (_, input, dependencies) = await LoadProjectGraphAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        RequireInspector(input);
        var model = await inspector.InspectAsync(input, cancellationToken).ConfigureAwait(false);
        if (request.ExperimentalDiscoverySchemaVersion is not (3 or 4 or 5))
            throw Errors.InvalidRecipe("SCAFFOLD_DISCOVERY_VERSION_INVALID", "Unknown experimental discovery identity version.", "Use schema 3 for historical sessions or schema 4 for local fields.");
        var discovery = request.Coordinated is not null || (request.ExperimentalDiscovery && request.ExperimentalDiscoverySchemaVersion == 5)
            ? await new ExperimentalComponentDiscoveryService(componentCapabilityAnalyzer, transformPlanner).DiscoverCoordinatedAsync(input, model, cancellationToken).ConfigureAwait(false)
            : request.Ellipsoid is not null || (request.ExperimentalDiscovery && request.ExperimentalDiscoverySchemaVersion == 4)
            ? await new ExperimentalComponentDiscoveryService(componentCapabilityAnalyzer, transformPlanner).DiscoverEllipsoidAsync(input, model, cancellationToken).ConfigureAwait(false)
            : request.Experimental is not null || request.ExperimentalDiscovery
            ? await new ExperimentalComponentDiscoveryService(componentCapabilityAnalyzer, transformPlanner).DiscoverAsync(input, model, cancellationToken).ConfigureAwait(false)
            : await new PreciseComponentDiscoveryService(componentCapabilityAnalyzer).DiscoverAsync(input, model, cancellationToken).ConfigureAwait(false);
        var recipe = await new ComponentRecipeScaffolder(componentCapabilityAnalyzer)
            .CreateAsync(input, model, discovery, request, cancellationToken)
            .ConfigureAwait(false);
        var canonicalJson = JsonDefaults.SerializeToUtf8(recipe);
        var reparsed = JsonDefaults.Deserialize<RecipeDocument>(canonicalJson, "Scaffolded recipe");
        RecipeValidator.Validate(reparsed);
        if (request.Intent == RecipeScaffoldContract.AffineIntent || request.Experimental is not null || request.Ellipsoid is not null || request.Coordinated is not null)
        {
            _ = CreatePlan(input, model, reparsed, dependencies);
        }

        if (!canonicalJson.AsSpan().SequenceEqual(JsonDefaults.SerializeToUtf8(reparsed)))
        {
            throw Errors.Verification(
                "SCAFFOLD_CANONICAL_JSON_DRIFT",
                "The scaffolded recipe did not survive a canonical JSON round trip.",
                "Do not use the output; report the serializer contract regression.");
        }

        if (recipeDocumentWriter is null)
        {
            throw Errors.Unsupported(
                "RECIPE_OUTPUT_NOT_CONFIGURED",
                "No atomic recipe output writer is configured.",
                "Use the default CLI composition root or configure an IRecipeDocumentWriter.");
        }

        var outputPath = await recipeDocumentWriter
            .WriteNewAsync(request.OutputPath, canonicalJson, cancellationToken)
            .ConfigureAwait(false);
        return new RecipeScaffoldResult(
            outputPath,
            ContentHash.Compute(canonicalJson),
            discovery.DiscoveryFingerprint,
            request.ComponentIds.Order(StringComparer.Ordinal).ToArray(),
            recipe);
    }

    public async Task<PlanRunResult> PlanAsync(string projectRoot, RecipeDocument recipe, CancellationToken cancellationToken = default)
    {
        var (project, input, dependencies) = await LoadProjectGraphAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        RequireInspector(input);
        var model = await inspector.InspectAsync(input, cancellationToken).ConfigureAwait(false);
        var plan = CreatePlan(input, model, recipe, dependencies);
        await workspace.SavePlanAsync(projectRoot, plan, cancellationToken).ConfigureAwait(false);
        var evidence = CreateEvidence(project, "plan", plan.Fingerprint.Value[..16], input, dependencies, null, model, null, plan,
        [
            new BoundaryEvidence("input_hash", "passed", $"Input hash {input.ContentHash} matches the recipe."),
            new BoundaryEvidence("selection", "passed", $"Selected {plan.Operations.Sum(operation => operation.SelectedDrawCalls.Count)} complete draw calls."),
            new BoundaryEvidence("lod_coverage", "passed", "Every present LOD has explicit expected cardinality."),
            new BoundaryEvidence("rewrite", "not_applicable", "Dry-run did not create candidate bytes."),
            new BoundaryEvidence("runtime", "untested", "No live Deadlock runtime validation was performed."),
        ], []);
        return new PlanRunResult(plan, evidence);
    }

    public async Task<EllipsoidSelectionPreview> PreviewEllipsoidSelectionAsync(string projectRoot, RecipeDocument recipe, CancellationToken cancellationToken = default)
    {
        EllipsoidContractValidator.ValidateRecipe(recipe);
        var (_, input, dependencies) = await LoadProjectGraphAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        RequireInspector(input);
        if (inspector is not IEllipsoidPreviewGeometryReader reader)
            throw Errors.Unsupported("ELLIPSOID_PREVIEW_READER_REQUIRED", "The configured adapter cannot read complete contact-sheet geometry.", "Use a configured Source 2 geometry reader.");
        var snapshot = await inspector.InspectAsync(input, cancellationToken).ConfigureAwait(false);
        var plan = CreatePlan(input, snapshot, recipe, dependencies);
        var geometry = await reader.ReadEllipsoidPreviewGeometryAsync(input, plan, cancellationToken).ConfigureAwait(false);
        return EllipsoidSelectionPreviewBuilder.Create(plan, geometry);
    }

    public async Task<BuildRunResult> BuildAsync(string projectRoot, RecipeDocument recipe, CancellationToken cancellationToken = default)
    {
        var (project, input, dependencies) = await LoadProjectGraphAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        RequireInspector(input);
        var before = await inspector.InspectAsync(input, cancellationToken).ConfigureAwait(false);
        var plan = CreatePlan(input, before, recipe, dependencies);
        await workspace.SavePlanAsync(projectRoot, plan, cancellationToken).ConfigureAwait(false);

        if (!rewriter.CanRewrite(before, plan))
        {
            throw Errors.Unsupported("REWRITE_CAPABILITY_UNAVAILABLE", "The configured adapter cannot safely rewrite this model layout.", "Use inspect output to select a supported layout or install a compatible adapter.");
        }

        var candidate = await rewriter.RewriteAsync(input, before, plan, cancellationToken).ConfigureAwait(false);
        var computedOutputHash = ContentHash.Compute(candidate.Content.Span);
        if (candidate.Snapshot.Artifact.ContentHash != computedOutputHash)
        {
            throw Errors.Verification("CANDIDATE_HASH_MISMATCH", "The rewrite adapter returned a snapshot whose hash does not match candidate bytes.", "Reject the adapter output and inspect its serialization path.");
        }

        var internalVerification = ModelVerifier.Verify(before, candidate.Snapshot, plan);
        var experimentalVerification = await VerifyExperimentalAsync(input,
            new ArtifactContent(candidate.LogicalPath, computedOutputHash, candidate.Content), plan, cancellationToken).ConfigureAwait(false);
        var ellipsoidVerification = await VerifyEllipsoidAsync(input,
            new ArtifactContent(candidate.LogicalPath, computedOutputHash, candidate.Content), plan, cancellationToken).ConfigureAwait(false);
        var coordinatedVerification = await VerifyCoordinatedAsync(input,
            new ArtifactContent(candidate.LogicalPath, computedOutputHash, candidate.Content), plan, cancellationToken).ConfigureAwait(false);
        var externalBoundary = await externalVerifier.VerifyAsync(candidate, cancellationToken).ConfigureAwait(false);
        var boundaries = new List<BoundaryEvidence>
        {
            new("input_hash", "passed", $"Input hash {input.ContentHash} matches the recipe."),
            new("rewrite", "passed", $"Candidate {computedOutputHash} was produced without overwriting the input."),
        };
        boundaries.AddRange(internalVerification.Boundaries);
        if (experimentalVerification is not null) boundaries.AddRange(experimentalVerification.Boundaries);
        if (ellipsoidVerification is not null) boundaries.AddRange(ellipsoidVerification.Boundaries);
        if (coordinatedVerification is not null) boundaries.AddRange(coordinatedVerification.Boundaries);
        boundaries.Add(externalBoundary);
        boundaries.Add(new BoundaryEvidence("runtime", "untested", "No live Deadlock runtime validation was performed."));

        if (!internalVerification.IsValid || externalBoundary.Status == "failed")
        {
            var failures = boundaries.Where(boundary => boundary.Status == "failed").Select(boundary => boundary.Name);
            throw Errors.Verification("POST_WRITE_VERIFICATION_FAILED", $"Candidate failed: {string.Join(", ", failures)}.", "Inspect the failed temporary run; no build was published.");
        }

        var buildId = $"{plan.Fingerprint.Value[..16]}-{computedOutputHash.Value[..12]}";
        var evidence = CreateEvidence(project, "build", buildId, input, dependencies, new ArtifactContent(candidate.LogicalPath, computedOutputHash, candidate.Content), before, candidate.Snapshot, plan, boundaries, internalVerification.Warnings, experimentalVerification, ellipsoidVerification, coordinatedVerification);
        var publication = new BuildPublication(buildId, plan, candidate, reports.RenderJson(evidence), reports.RenderMarkdown(evidence));
        var published = await workspace.PublishBuildAsync(projectRoot, publication, cancellationToken).ConfigureAwait(false);
        var publishedEvidence = JsonDefaults.Deserialize<EvidenceReport>(Encoding.UTF8.GetBytes(published.EvidenceJson), "Published build evidence");
        return new BuildRunResult(published.Build, publishedEvidence);
    }

    public async Task<VerifyRunResult> VerifyAsync(string projectRoot, string buildId, CancellationToken cancellationToken = default)
    {
        var (project, input, dependencies) = await LoadProjectGraphAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        var (published, candidateContent) = await workspace.LoadBuildAsync(projectRoot, buildId, cancellationToken).ConfigureAwait(false);
        var plan = await workspace.LoadPlanAsync(projectRoot, published.PlanFingerprint, cancellationToken).ConfigureAwait(false);
        ValidatePlanInputs(plan, input, dependencies);
        RequireInspector(input);
        RequireInspector(candidateContent);
        var before = await inspector.InspectAsync(input, cancellationToken).ConfigureAwait(false);
        var after = await inspector.InspectAsync(candidateContent, cancellationToken).ConfigureAwait(false);
        var internalVerification = ModelVerifier.Verify(before, after, plan);
        var experimentalVerification = await VerifyExperimentalAsync(input, candidateContent, plan, cancellationToken).ConfigureAwait(false);
        var ellipsoidVerification = await VerifyEllipsoidAsync(input, candidateContent, plan, cancellationToken).ConfigureAwait(false);
        var coordinatedVerification = await VerifyCoordinatedAsync(input, candidateContent, plan, cancellationToken).ConfigureAwait(false);
        var candidate = new RewriteCandidate(candidateContent.LogicalPath, candidateContent.Bytes, after);
        var externalBoundary = await externalVerifier.VerifyAsync(candidate, cancellationToken).ConfigureAwait(false);
        var boundaries = internalVerification.Boundaries.Concat(experimentalVerification?.Boundaries ?? [])
            .Concat(ellipsoidVerification?.Boundaries ?? [])
            .Concat(coordinatedVerification?.Boundaries ?? [])
            .Concat([externalBoundary, new BoundaryEvidence("runtime", "untested", "No live Deadlock runtime validation was performed.")]).ToArray();

        if (!internalVerification.IsValid || externalBoundary.Status == "failed")
        {
            throw Errors.Verification("BUILD_VERIFICATION_FAILED", $"Published build '{buildId}' failed verification.", "Do not use this build; inspect its evidence and recreate it from immutable input.");
        }

        var reportId = $"{buildId}-verify";
        var evidence = CreateEvidence(project, "verify", reportId, input, dependencies, candidateContent, before, after, plan, boundaries, internalVerification.Warnings, experimentalVerification, ellipsoidVerification, coordinatedVerification);
        await workspace.SaveEvidenceAsync(projectRoot, reportId, reports.RenderJson(evidence), reports.RenderMarkdown(evidence), cancellationToken).ConfigureAwait(false);
        return new VerifyRunResult(published, evidence);
    }

    private async Task<(ProjectManifest Project, ArtifactContent Input, IReadOnlyList<ArtifactContent> Dependencies)> LoadProjectGraphAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var project = await workspace.LoadProjectAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        var input = await workspace.LoadInputAsync(projectRoot, project, cancellationToken).ConfigureAwait(false);
        if (input.ContentHash != project.Input.ContentHash || input.Bytes.Length != project.Input.Size)
        {
            throw Errors.Input("IMPORTED_OBJECT_DRIFT", "The imported object no longer matches the project manifest.", "Restore the immutable object or create a new project from the intended input.");
        }

        var dependencies = await workspace.LoadDependenciesAsync(projectRoot, project, cancellationToken).ConfigureAwait(false);
        return (project, input, dependencies);
    }

    private MutationPlan CreatePlan(
        ArtifactContent input,
        ModelSnapshot model,
        RecipeDocument recipe,
        IReadOnlyList<ArtifactContent> dependencies)
    {
        return transformPlanner is null
            ? MutationPlanner.CreatePlan(model, recipe, dependencies)
            : MutationPlanner.CreatePlan(model, recipe, input, transformPlanner, dependencies);
    }

    private async Task<ExperimentalTransformVerification?> VerifyExperimentalAsync(
        ArtifactContent input, ArtifactContent output, MutationPlan plan, CancellationToken cancellationToken)
    {
        if (plan.SchemaVersion is not (2 or 3)) return null;
        if (rewriter is not IExperimentalTransformVerifier verifier)
            throw Errors.Unsupported("EXPERIMENTAL_VERIFIER_REQUIRED", "The configured adapter has no independent experimental resource verifier.", "Do not publish this experimental build.");
        return await verifier.VerifyExperimentalTransformAsync(input, output, plan, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EllipsoidTransformVerification?> VerifyEllipsoidAsync(
        ArtifactContent input, ArtifactContent output, MutationPlan plan, CancellationToken cancellationToken)
    {
        if (plan.SchemaVersion != 4) return null;
        if (rewriter is not IEllipsoidTransformVerifier verifier)
            throw Errors.Unsupported("ELLIPSOID_VERIFIER_REQUIRED", "The configured adapter has no independent ellipsoid resource verifier.", "Do not publish this build.");
        return await verifier.VerifyEllipsoidTransformAsync(input, output, plan, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CoordinatedTransformVerification?> VerifyCoordinatedAsync(
        ArtifactContent input, ArtifactContent output, MutationPlan plan, CancellationToken cancellationToken)
    {
        if (plan.SchemaVersion != 5) return null;
        if (rewriter is not ICoordinatedTransformVerifier verifier)
            throw Errors.Unsupported("COORDINATED_VERIFIER_REQUIRED", "The configured adapter has no independent coordinated resource verifier.", "Do not publish this build.");
        return await verifier.VerifyCoordinatedTransformAsync(input, output, plan, cancellationToken).ConfigureAwait(false);
    }

    private void RequireInspector(ArtifactContent input)
    {
        if (!inspector.CanInspect(input))
        {
            throw Errors.Unsupported("INSPECTION_CAPABILITY_UNAVAILABLE", $"Adapter '{inspector.AdapterName}' cannot inspect '{input.LogicalPath}'.", "Use a supported compiled resource or configure the matching adapter.");
        }
    }

    private EvidenceReport CreateEvidence(
        ProjectManifest project,
        string command,
        string reportId,
        ArtifactContent input,
        IReadOnlyList<ArtifactContent> dependencies,
        ArtifactContent? output,
        ModelSnapshot before,
        ModelSnapshot? after,
        MutationPlan plan,
        IReadOnlyList<BoundaryEvidence> boundaries,
        IReadOnlyList<string> warnings,
        ExperimentalTransformVerification? experimentalVerification = null,
        EllipsoidTransformVerification? ellipsoidVerification = null,
        CoordinatedTransformVerification? coordinatedVerification = null)
    {
        var toolVersions = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["dotnet.runtime"] = RuntimeInformation.FrameworkDescription,
            ["operating_system"] = RuntimeInformation.OSDescription,
            ["process.architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["s2modkit"] = typeof(S2ModKitApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(S2ModKitApplication).Assembly.GetName().Version?.ToString()
                ?? "unknown",
            [inspector.AdapterName] = inspector.AdapterVersion,
            [externalVerifier.VerifierName] = externalVerifier.VerifierVersion,
        };
        foreach (var component in inspector.ComponentVersions.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            toolVersions[component.Key] = component.Value;
        }

        var operationWarnings = new List<string>();
        if (plan.Operations.Any(operation => operation.Kind == "remove_component"))
        {
            operationWarnings.Add("remove_component@1 removes draw-call entries only; vertex/index payloads are intentionally preserved and may still contain unused geometry bytes.");
        }

        if (plan.Operations.Any(operation => operation.Kind == "transform_component" && operation.Version == 1))
        {
            operationWarnings.Add("transform_component@1 changes only selected position bytes and explicitly planned bounds metadata; offline checks do not prove animated runtime behavior.");
        }

        if (plan.Operations.Any(operation => operation.Kind == "transform_component" && operation.Version == 2))
        {
            operationWarnings.Add("transform_component@2 atomically changes the planned visual and convex-collision fields; offline checks do not prove live runtime behavior.");
        }

        if (plan.Operations.Any(operation => operation.Kind == "transform_component" && operation.Version == 3))
        {
            operationWarnings.Add("transform_component@3 changes only explicitly selected connected-component position bytes; offline checks do not prove live runtime behavior.");
        }

        if (plan.Operations.Any(operation => operation.Kind == "transform_component" && operation.Version == 4))
        {
            var changesPackedFrames = plan.Operations
                .Where(operation => operation.Kind == "transform_component" && operation.Version == 4)
                .SelectMany(operation => operation.AffineTransformTarget?.GeometryTargets ?? [])
                .Any(target => target.AllowedChangedAttributes.Contains("normal_tangent", StringComparer.Ordinal));
            operationWarnings.Add(changesPackedFrames
                ? "transform_component@4 changes planned positions, packed normal/tangent frames, and reproduced bounds; offline checks do not prove live runtime behavior."
                : "transform_component@4 routes a typed-pivot uniform transform through the proven position path and reproduces required bounds; offline checks do not prove live runtime behavior.");
        }

        if (plan.SchemaVersion is 2 or 3)
        {
            operationWarnings.Add("Experimental visual-only edit: boxes are conservatively updated; spheres, occlusion proxies and collision are preserved but unverified. Player testing is required.");
            if (after is null)
                boundaries = boundaries.Concat(ExperimentalEvidenceValidator.PlannedBoundaries()).ToArray();
            else
            {
                var target = plan.Operations.Single().ExperimentalTransformTarget!;
                if (experimentalVerification is null
                    || JsonDefaults.Serialize(experimentalVerification.Boxes.Select(box => box.Target).ToArray()) != JsonDefaults.Serialize(target.BoxTargets)
                    || JsonDefaults.Serialize(experimentalVerification.PreservedMetadata.Select(field => field.Target).ToArray()) != JsonDefaults.Serialize(target.PreservationTargets))
                    throw Errors.Verification("EXPERIMENTAL_EVIDENCE_INVALID", "Observed evidence does not cover every frozen target exactly.", "Do not publish this output.");
            }
        }
        if (plan.SchemaVersion == 4)
        {
            operationWarnings.Add("Experimental ellipsoid visual edit: bind-space geometry and boxes are verified offline; sphere containment, occlusion coherence, collision correspondence and runtime remain untested.");
            if (after is null) boundaries = boundaries.Concat(EllipsoidContractValidator.PlannedBoundaries().Where(b => b.Name != "runtime")).ToArray();
            else
            {
                var target = plan.Operations.Single().EllipsoidTransformTarget!;
                if (ellipsoidVerification is null
                    || JsonDefaults.Serialize(ellipsoidVerification.Boxes.Select(b => b.Target).ToArray()) != JsonDefaults.Serialize(target.BoxTargets)
                    || JsonDefaults.Serialize(ellipsoidVerification.PreservedMetadata.Select(p => p.Target).ToArray()) != JsonDefaults.Serialize(target.PreservationTargets))
                    throw Errors.Verification("ELLIPSOID_RESULT_DRIFT", "Observed evidence does not cover every frozen target exactly.", "Do not publish this output.");
            }
        }
        if (plan.SchemaVersion == 5)
        {
            operationWarnings.Add("Experimental coordinated visual edit: complete member geometry and positive boxes are verified offline; preserved zero boxes/spheres, other culling consumers, clothing/pose fit and runtime remain untested.");
            if (after is null) boundaries = boundaries.Concat(CoordinatedContractValidator.PlannedBoundaries().Where(b => b.Name != "runtime")).ToArray();
            else
            {
                var target = plan.Operations.Single().CoordinatedTransformTarget!;
                if (coordinatedVerification is null
                    || JsonDefaults.Serialize(coordinatedVerification.Boxes.Select(b => b.Target).ToArray()) != JsonDefaults.Serialize(target.BoxTargets)
                    || JsonDefaults.Serialize(coordinatedVerification.ZeroBoxes.Select(b => b.Target).ToArray()) != JsonDefaults.Serialize(target.ZeroBoxTargets)
                    || JsonDefaults.Serialize(coordinatedVerification.ZeroRenderSpheres.Select(b => b.Target).ToArray()) != JsonDefaults.Serialize(target.ZeroRenderSphereTargets)
                    || JsonDefaults.Serialize(coordinatedVerification.PreservedMetadata.Select(p => p.Target).ToArray()) != JsonDefaults.Serialize(target.PreservationTargets))
                    throw Errors.Verification("COORDINATED_RESULT_DRIFT", "Observed evidence does not cover every coordinated target exactly.", "Do not publish this output.");
            }
        }
        var evidenceWarnings = warnings.Concat(operationWarnings)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var report = new EvidenceReport
        {
            SchemaVersion = plan.SchemaVersion == 5 ? 10 : plan.SchemaVersion == 4 ? 9 : plan.SchemaVersion == 3 ? 8 : plan.SchemaVersion == 2 ? 7 : 6,
            ReportId = reportId,
            CreatedUtc = clock.UtcNow,
            Command = command,
            Status = boundaries.Any(boundary => boundary.Status == "failed") ? "failed" : "passed",
            Input = CreateArtifactEvidence(input, project.Input),
            Dependencies = dependencies.OrderBy(dependency => dependency.LogicalPath, StringComparer.Ordinal)
                .Select(dependency => CreateArtifactEvidence(
                    dependency,
                    project.Dependencies.Single(item => string.Equals(item.LogicalPath, dependency.LogicalPath, StringComparison.Ordinal))))
                .ToArray(),
            Output = output is null ? null : new ArtifactEvidence(output.LogicalPath, output.ContentHash, output.Bytes.Length, "generated", $"build:{output.ContentHash}", "sha256_and_semantic_reopen"),
            PlanFingerprint = plan.Fingerprint,
            Operations = plan.Operations.Select(operation => CreateOperationEvidence(operation, after, experimentalVerification, ellipsoidVerification, coordinatedVerification)).ToArray(),
            Boundaries = boundaries,
            Blocks = CreateBlockEvidence(before, after, plan),
            ToolVersions = toolVersions,
            Warnings = evidenceWarnings,
        };
        ExperimentalEvidenceValidator.Validate(report);
        return report;
    }

    private static ArtifactEvidence CreateArtifactEvidence(ArtifactContent content, ProjectArtifactManifest manifest) =>
        new(
            content.LogicalPath,
            content.ContentHash,
            content.Bytes.Length,
            manifest.SourceKind,
            string.IsNullOrWhiteSpace(manifest.CatalogIdentity) ? manifest.SourcePath : manifest.CatalogIdentity,
            manifest.VerificationMode);

    private static OperationEvidence CreateOperationEvidence(PlannedOperation operation, ModelSnapshot? after,
        ExperimentalTransformVerification? experimentalVerification, EllipsoidTransformVerification? ellipsoidVerification,
        CoordinatedTransformVerification? coordinatedVerification)
    {
        var outputBlocks = after?.Artifact.Blocks.ToDictionary(block => block.Index);
        return new OperationEvidence(
            operation.OperationId,
            operation.Kind,
            operation.Version,
            operation.CoordinatedTransformTarget is not null
                ? operation.SelectedDrawCalls.Select(item => item.DrawCallId).Order(StringComparer.Ordinal).ToArray()
                : operation.SelectedDrawCalls.Select(item => item.DrawCallId).ToArray(),
            operation.SelectedDrawCalls.Select(item => item.ResourcePath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())
        {
            GeometryChanges = (operation.ExperimentalTransformTarget?.GeometryTargets ?? operation.GeometryTargets).Select(target => new GeometryChangeEvidence(
                target.Lod,
                target.ResourcePath,
                target.MeshOrdinal,
                target.ResourceBlockIndex,
                target.VertexSetHash,
                target.SelectedVertexCount,
                target.BeforeBounds,
                target.ExpectedAfterBounds,
                target.FrozenPivot,
                target.UniformScale,
                target.Translation,
                target.MaximumDisplacement,
                target.AllowedChangedAttributes,
                target.VertexBlockInputHash,
                outputBlocks is not null && outputBlocks.TryGetValue(target.VertexResourceBlockIndex, out var block)
                    ? block.ContentHash
                    : null,
                target.Codec)
            {
                InputDecodedVertexBufferHash = target.DecodedVertexBufferHash,
                ExpectedDecodedVertexBufferHash = target.ExpectedDecodedVertexBufferHash,
            }).ToArray(),
            CoupledTransform = CreateCoupledTransformEvidence(operation.CoupledTransformTarget),
            AffineTransform = CreateAffineTransformEvidence(operation.AffineTransformTarget, outputBlocks),
            ExperimentalTransform = operation.ExperimentalTransformTarget is { } visual ? new(
                visual.StructuralProfileId, visual.StructuralProfileVersion, visual.BoundsPolicyId, visual.BoundsPolicyVersion,
                visual.RuntimeMetadataPolicy, visual.Pivot, visual.UniformScale, visual.DisplacementLimit,
                experimentalVerification?.Boxes ?? visual.BoxTargets.Select(box => new ExperimentalBoxEvidence(box, null, "planned")).ToArray(),
                experimentalVerification?.PreservedMetadata ?? visual.PreservationTargets.Select(field => new ExperimentalPreservationEvidence(field, null, null, "planned")).ToArray())
            { Region = visual.Region } : null,
            EllipsoidTransform = operation.EllipsoidTransformTarget is { } ellipsoid ? new(ellipsoid,
                ellipsoidVerification?.Buffers,
                ellipsoidVerification?.Boxes ?? ellipsoid.BoxTargets.Select(b => new ExperimentalBoxEvidence(b, null, "planned")).ToArray(),
                ellipsoidVerification?.PreservedMetadata ?? ellipsoid.PreservationTargets.Select(p => new ExperimentalPreservationEvidence(p, null, null, "planned")).ToArray()) : null,
            CoordinatedTransform = operation.CoordinatedTransformTarget is { } coordinated ? new(coordinated,
                coordinatedVerification?.Buffers,
                coordinatedVerification?.Boxes ?? coordinated.BoxTargets.Select(b => new ExperimentalBoxEvidence(b, null, "planned")).ToArray(),
                coordinatedVerification?.ZeroBoxes ?? coordinated.ZeroBoxTargets.Select(b => new ZeroBoneBoxPreservationEvidence(b, null, "planned", "untested", "untested")).ToArray(),
                coordinatedVerification?.ZeroRenderSpheres ?? coordinated.ZeroRenderSphereTargets.Select(s => new ZeroRenderSpherePreservationEvidence(s, null, null, "planned", "untested", "untested")).ToArray(),
                coordinatedVerification?.PreservedMetadata ?? coordinated.PreservationTargets.Select(p => new ExperimentalPreservationEvidence(p, null, null, "planned")).ToArray()) : null,
        };
    }

    private static AffineTransformEvidence? CreateAffineTransformEvidence(
        PlannedAffineTransformTarget? target,
        Dictionary<int, ResourceBlockSnapshot>? outputBlocks)
    {
        if (target is null)
        {
            return null;
        }

        return new AffineTransformEvidence(
            target.SelectionKind,
            target.StructuralProfileId,
            target.StructuralProfileVersion,
            target.Pivot,
            target.Frame,
            target.Scale,
            target.Rotation,
            target.Translation,
            target.LinearMap,
            target.MaximumDisplacement,
            target.DisplacementLimit,
            target.GeometryTargets.Select(geometry => new AffineGeometryChangeEvidence(
                geometry.Lod,
                geometry.ResourcePath,
                geometry.MeshOrdinal,
                geometry.ResourceBlockIndex,
                geometry.VertexSetHash,
                geometry.SelectedVertexCount,
                geometry.SelectionBeforeBounds,
                geometry.SelectionExpectedAfterBounds,
                geometry.MeshBeforeBounds,
                geometry.MeshExpectedAfterBounds,
                geometry.VertexBlockInputHash,
                outputBlocks is not null && outputBlocks.TryGetValue(geometry.VertexResourceBlockIndex, out var block)
                    ? block.ContentHash
                    : null,
                geometry.DecodedVertexBufferHash,
                geometry.ExpectedDecodedVertexBufferHash,
                geometry.InputPackedFrameHash,
                geometry.ExpectedPackedFrameHash,
                geometry.MaximumDisplacement,
                geometry.AllowedChangedAttributes,
                geometry.Codec)).ToArray());
    }

    private static CoupledTransformEvidence? CreateCoupledTransformEvidence(PlannedCoupledTransformTarget? target)
    {
        if (target is null)
        {
            return null;
        }

        return new CoupledTransformEvidence(
            "transform_coupled_convex",
            target.Visual.FrozenPivot,
            target.Visual.UniformScale,
            new CoupledTransformHalfEvidence(
                target.Visual.MbufResourceBlockIndex,
                target.Visual.VertexCount,
                target.Visual.DecodedVertexBufferHash,
                target.Visual.ExpectedDecodedVertexBufferHash,
                target.Visual.BeforeBounds,
                target.Visual.ExpectedAfterBounds,
                target.Visual.MaximumDisplacement,
                target.Visual.DisplacementLimit),
            new CoupledTransformHalfEvidence(
                target.Collision.ResourceBlockIndex,
                target.Collision.VertexCount,
                target.Collision.PositionInputHash,
                target.Collision.ExpectedPositionHash,
                target.Collision.Before.Bounds,
                target.Collision.ExpectedAfter.Bounds,
                target.Collision.MaximumDisplacement,
                target.Collision.DisplacementLimit),
            target.Visual.AllowedByteClasses.Select(value => $"visual:{value}")
                .Concat(target.Collision.AllowedByteClasses.Select(value => $"collision:{value}"))
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    private static void ValidatePlanInputs(
        MutationPlan plan,
        ArtifactContent input,
        IReadOnlyList<ArtifactContent> dependencies)
    {
        var current = dependencies.Append(input)
            .Select(artifact => new PlannedInput(StableIdentity.NormalizePath(artifact.LogicalPath), artifact.ContentHash, artifact.Bytes.Length))
            .OrderBy(artifact => artifact.LogicalPath, StringComparer.Ordinal)
            .ToArray();
        if (!plan.Inputs.SequenceEqual(current))
        {
            throw Errors.Input("PLAN_INPUT_GRAPH_DRIFT", "The immutable project input graph no longer matches the stored mutation plan.", "Recreate the plan from the current verified project graph.");
        }
    }

    private static ResourceBlockEvidence[] CreateBlockEvidence(ModelSnapshot before, ModelSnapshot? after, MutationPlan plan)
    {
        var targetBlocks = plan.Operations.SelectMany(operation => operation.TargetBlocks)
            .Select(block => (block.Index, block.Type))
            .ToHashSet();
        var afterBlocks = after?.Artifact.Blocks.ToDictionary(block => (block.Index, block.Type));
        return before.Artifact.Blocks
            .OrderBy(block => block.Index)
            .ThenBy(block => block.Type, StringComparer.Ordinal)
            .Select(block =>
            {
                var key = (block.Index, block.Type);
                var isTarget = targetBlocks.Contains(key);
                var outputHash = afterBlocks is not null && afterBlocks.TryGetValue(key, out var outputBlock)
                    ? outputBlock.ContentHash
                    : (ContentHash?)null;
                var disposition = after is null
                    ? isTarget ? "planned_change" : "planned_unchanged"
                    : isTarget ? "changed" : "unchanged";
                return new ResourceBlockEvidence(block.Index, block.Type, block.ContentHash, outputHash, disposition);
            })
            .ToArray();
    }
}
