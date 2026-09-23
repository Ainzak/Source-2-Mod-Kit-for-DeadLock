using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed class S2ModKitApplication : IS2ModKitApplication
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

    public async Task<RecipeScaffoldResult> ScaffoldRecipeAsync(
        string projectRoot,
        RecipeScaffoldRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (_, input, dependencies) = await LoadProjectGraphAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        RequireInspector(input);
        var model = await inspector.InspectAsync(input, cancellationToken).ConfigureAwait(false);
        var discovery = await new PreciseComponentDiscoveryService(componentCapabilityAnalyzer)
            .DiscoverAsync(input, model, cancellationToken)
            .ConfigureAwait(false);
        var recipe = await new ComponentRecipeScaffolder(componentCapabilityAnalyzer)
            .CreateAsync(input, model, discovery, request, cancellationToken)
            .ConfigureAwait(false);
        var canonicalJson = JsonDefaults.SerializeToUtf8(recipe);
        var reparsed = JsonDefaults.Deserialize<RecipeDocument>(canonicalJson, "Scaffolded recipe");
        RecipeValidator.Validate(reparsed);
        if (request.Intent == RecipeScaffoldContract.AffineIntent)
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
        var externalBoundary = await externalVerifier.VerifyAsync(candidate, cancellationToken).ConfigureAwait(false);
        var boundaries = new List<BoundaryEvidence>
        {
            new("input_hash", "passed", $"Input hash {input.ContentHash} matches the recipe."),
            new("rewrite", "passed", $"Candidate {computedOutputHash} was produced without overwriting the input."),
        };
        boundaries.AddRange(internalVerification.Boundaries);
        boundaries.Add(externalBoundary);
        boundaries.Add(new BoundaryEvidence("runtime", "untested", "No live Deadlock runtime validation was performed."));

        if (!internalVerification.IsValid || externalBoundary.Status == "failed")
        {
            var failures = boundaries.Where(boundary => boundary.Status == "failed").Select(boundary => boundary.Name);
            throw Errors.Verification("POST_WRITE_VERIFICATION_FAILED", $"Candidate failed: {string.Join(", ", failures)}.", "Inspect the failed temporary run; no build was published.");
        }

        var buildId = $"{plan.Fingerprint.Value[..16]}-{computedOutputHash.Value[..12]}";
        var evidence = CreateEvidence(project, "build", buildId, input, dependencies, new ArtifactContent(candidate.LogicalPath, computedOutputHash, candidate.Content), before, candidate.Snapshot, plan, boundaries, internalVerification.Warnings);
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
        var candidate = new RewriteCandidate(candidateContent.LogicalPath, candidateContent.Bytes, after);
        var externalBoundary = await externalVerifier.VerifyAsync(candidate, cancellationToken).ConfigureAwait(false);
        var boundaries = internalVerification.Boundaries.Concat([externalBoundary, new BoundaryEvidence("runtime", "untested", "No live Deadlock runtime validation was performed.")]).ToArray();

        if (!internalVerification.IsValid || externalBoundary.Status == "failed")
        {
            throw Errors.Verification("BUILD_VERIFICATION_FAILED", $"Published build '{buildId}' failed verification.", "Do not use this build; inspect its evidence and recreate it from immutable input.");
        }

        var reportId = $"{buildId}-verify";
        var evidence = CreateEvidence(project, "verify", reportId, input, dependencies, candidateContent, before, after, plan, boundaries, internalVerification.Warnings);
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
        IReadOnlyList<ArtifactContent> dependencies) =>
        transformPlanner is null
            ? MutationPlanner.CreatePlan(model, recipe, dependencies)
            : MutationPlanner.CreatePlan(model, recipe, input, transformPlanner, dependencies);

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
        IReadOnlyList<string> warnings)
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

        var evidenceWarnings = warnings.Concat(operationWarnings)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new EvidenceReport
        {
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
            Operations = plan.Operations.Select(operation => CreateOperationEvidence(operation, after)).ToArray(),
            Boundaries = boundaries,
            Blocks = CreateBlockEvidence(before, after, plan),
            ToolVersions = toolVersions,
            Warnings = evidenceWarnings,
        };
    }

    private static ArtifactEvidence CreateArtifactEvidence(ArtifactContent content, ProjectArtifactManifest manifest) =>
        new(
            content.LogicalPath,
            content.ContentHash,
            content.Bytes.Length,
            manifest.SourceKind,
            string.IsNullOrWhiteSpace(manifest.CatalogIdentity) ? manifest.SourcePath : manifest.CatalogIdentity,
            manifest.VerificationMode);

    private static OperationEvidence CreateOperationEvidence(PlannedOperation operation, ModelSnapshot? after)
    {
        var outputBlocks = after?.Artifact.Blocks.ToDictionary(block => block.Index);
        return new OperationEvidence(
            operation.OperationId,
            operation.Kind,
            operation.Version,
            operation.SelectedDrawCalls.Select(item => item.DrawCallId).ToArray(),
            operation.SelectedDrawCalls.Select(item => item.ResourcePath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())
        {
            GeometryChanges = operation.GeometryTargets.Select(target => new GeometryChangeEvidence(
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
