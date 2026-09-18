using S2ModKit.Domain;

namespace S2ModKit.Application;

public static class GuidedWorkflow
{
    public static GuidedWorkflowSession CreateSession(
        string cataloguePath,
        IReadOnlyList<GuidedSourceOption> sources,
        bool expert)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cataloguePath);
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw Errors.Input(
                "GUIDED_SOURCE_REQUIRED",
                "The guided workflow has no configured source.",
                "Provide at least one --base-vpk, --mod-vpk, or --compiled-model option.");
        }

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.SourceId)
                || !GuidedWorkflowContract.IsSourceKind(source.Kind)
                || string.IsNullOrWhiteSpace(source.DisplayName)
                || string.IsNullOrWhiteSpace(source.Path)
                || !sourceIds.Add(source.SourceId))
            {
                throw Errors.Input(
                    "GUIDED_SOURCE_INVALID",
                    "A configured guided source is invalid or duplicated.",
                    "Use distinct, non-empty base-VPK, mod-VPK, or compiled-model paths.");
            }
        }

        return new GuidedWorkflowSession
        {
            SessionId = $"guided-{Guid.NewGuid():N}",
            CataloguePath = cataloguePath,
            Expert = expert,
            Sources = sources.ToArray(),
        };
    }

    public static void ValidateSession(GuidedWorkflowSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.SchemaVersion != GuidedWorkflowContract.SchemaVersion
            || string.IsNullOrWhiteSpace(session.SessionId)
            || string.IsNullOrWhiteSpace(session.CataloguePath)
            || session.Status is not (GuidedWorkflowContract.ActiveStatus or GuidedWorkflowContract.PausedStatus or GuidedWorkflowContract.CompleteStatus)
            || !GuidedWorkflowContract.IsStep(session.Step)
            || session.Sources is null
            || session.Sources.Count == 0
            || session.Sources.Any(source => source is null
                || string.IsNullOrWhiteSpace(source.SourceId)
                || !GuidedWorkflowContract.IsSourceKind(source.Kind)
                || string.IsNullOrWhiteSpace(source.DisplayName)
                || string.IsNullOrWhiteSpace(source.Path))
            || session.Sources.Select(source => source.SourceId).Distinct(StringComparer.Ordinal).Count() != session.Sources.Count)
        {
            throw Errors.InvalidRecipe(
                "GUIDED_SESSION_INVALID",
                "The guided session does not satisfy the version-1 interaction contract.",
                "Start a new session or restore an intact version-1 session file.");
        }


        if ((session.Status == GuidedWorkflowContract.CompleteStatus) != (session.Step == GuidedWorkflowContract.CompleteStep))
        {
            throw InvalidState("Only the complete step may use complete status.");
        }

        var selectedSource = session.SelectedSourceId is null
            ? null
            : session.Sources.SingleOrDefault(source => string.Equals(source.SourceId, session.SelectedSourceId, StringComparison.Ordinal));
        if (session.Step != GuidedWorkflowContract.SourceSelectionStep && selectedSource is null)
        {
            throw InvalidState("A selected source is required after source selection.");
        }

        if (session.Step is GuidedWorkflowContract.ResourceSelectionStep or GuidedWorkflowContract.ComponentSelectionStep
            && string.IsNullOrWhiteSpace(session.SelectedHeroId))
        {
            throw InvalidState("A selected hero is required after hero selection.");
        }

        if (session.Step == GuidedWorkflowContract.ComponentSelectionStep
            && (string.IsNullOrWhiteSpace(session.SelectedResourceId)
                || string.IsNullOrWhiteSpace(session.SelectedLogicalPath)))
        {
            throw InvalidState("A selected resource is required before component selection.");
        }

        if (session.Step is GuidedWorkflowContract.ActionSelectionStep
                or GuidedWorkflowContract.ReviewStep
                or GuidedWorkflowContract.OutputSelectionStep
                or GuidedWorkflowContract.InstallSelectionStep
                or GuidedWorkflowContract.RollbackSelectionStep
                or GuidedWorkflowContract.CompleteStep
            && (string.IsNullOrWhiteSpace(session.ProjectRoot)
                || !session.ProjectReady
                || string.IsNullOrWhiteSpace(session.SelectedComponentId)))
        {
            throw InvalidState("A ready project and selected component are required after component selection.");
        }

        if (session.Step is GuidedWorkflowContract.ReviewStep
                or GuidedWorkflowContract.OutputSelectionStep
                or GuidedWorkflowContract.InstallSelectionStep
                or GuidedWorkflowContract.RollbackSelectionStep
                or GuidedWorkflowContract.CompleteStep
            && (string.IsNullOrWhiteSpace(session.SelectedIntent)
                || string.IsNullOrWhiteSpace(session.SelectedOperationKind)
                || session.SelectedOperationVersion is null
                || string.IsNullOrWhiteSpace(session.RecipePath)
                || session.RecipeContentHash is null))
        {
            throw InvalidState("A scaffolded recipe and selected action are required before dry-run review.");
        }

        if (session.Step is GuidedWorkflowContract.OutputSelectionStep
                or GuidedWorkflowContract.InstallSelectionStep
                or GuidedWorkflowContract.RollbackSelectionStep
                or GuidedWorkflowContract.CompleteStep
            && (session.PlanFingerprint is null
                || session.PlannedDrawCallCount is null
                || session.PlannedLodCount is null
                || session.PlannedTargetBlockCount is null
                || session.PlannedVertexCount is null
                || session.PlannedCoupledCollision is null))
        {
            throw InvalidState("A complete dry-run summary is required before output selection.");
        }


        if (session.Step is GuidedWorkflowContract.InstallSelectionStep or GuidedWorkflowContract.RollbackSelectionStep
            && (!GuidedWorkflowContract.IsOutputChoice(session.OutputChoice ?? string.Empty)
                || session.OutputChoice is GuidedWorkflowContract.PlanOnlyOutput or GuidedWorkflowContract.BuildOnlyOutput
                || string.IsNullOrWhiteSpace(session.BuildId)
                || session.BuildContentHash is null
                || string.IsNullOrWhiteSpace(session.PackageId)
                || session.PackageContentHash is null))
        {
            throw InvalidState("A verified guided package is required before installation selection.");
        }

        if (session.Step == GuidedWorkflowContract.RollbackSelectionStep
            && (string.IsNullOrWhiteSpace(session.AddonsRoot)
                || string.IsNullOrWhiteSpace(session.InstallationId)
                || string.IsNullOrWhiteSpace(session.InstallationTargetFileName)
                || session.InstallationSlot is null
                || session.InstallationStatus != "active"))
        {
            throw InvalidState("An active verified installation receipt is required before rollback selection.");
        }

        if (session.Step == GuidedWorkflowContract.CompleteStep
            && !GuidedWorkflowContract.IsOutputChoice(session.OutputChoice ?? string.Empty))
        {
            throw InvalidState("A completed guided session requires a recorded output choice.");
        }

        if (session.Step == GuidedWorkflowContract.CompleteStep
            && session.OutputChoice != GuidedWorkflowContract.PlanOnlyOutput
            && (string.IsNullOrWhiteSpace(session.BuildId) || session.BuildContentHash is null))
        {
            throw InvalidState("The completed output choice requires a verified build identity.");
        }

        if (session.Step == GuidedWorkflowContract.CompleteStep
            && session.OutputChoice is GuidedWorkflowContract.MinimalPackageOutput or GuidedWorkflowContract.ExportPackageOutput
            && (string.IsNullOrWhiteSpace(session.PackageId) || session.PackageContentHash is null))
        {
            throw InvalidState("The completed output choice requires a verified package identity.");
        }

        if (session.Step == GuidedWorkflowContract.CompleteStep
            && session.OutputChoice == GuidedWorkflowContract.ExportPackageOutput
            && (string.IsNullOrWhiteSpace(session.ExportPath) || session.ExportContentHash is null))
        {
            throw InvalidState("The completed export choice requires a verified export identity.");
        }

        if (session.Step == GuidedWorkflowContract.CompleteStep
            && session.InstallationId is not null
            && (string.IsNullOrWhiteSpace(session.AddonsRoot)
                || string.IsNullOrWhiteSpace(session.InstallationTargetFileName)
                || session.InstallationSlot is null
                || session.InstallationStatus is not ("active" or "rolled_back")))
        {
            throw InvalidState("A completed installation lifecycle requires a final receipt status.");
        }
    }

    public static IReadOnlyList<GuidedComponentChoice> CreateComponentChoices(
        ComponentDiscoveryResultV2 discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        var result = new List<GuidedComponentChoice>();
        foreach (var candidate in discovery.Candidates)
        {
            var actions = CreateActions(candidate.Capabilities);
            if (actions.Count == 0)
            {
                continue;
            }

            var lods = candidate switch
            {
                MaterialGroupComponentCandidateV2 material => material.Lods.Select(item => item.Lod),
                MeshLineageComponentCandidateV2 lineage => lineage.Lods.Select(item => item.Lod),
                _ => [],
            };
            result.Add(new GuidedComponentChoice(
                candidate.CandidateId,
                candidate.DisplayLabel,
                candidate.Kind,
                lods.Distinct().Order().ToArray(),
                actions));
        }

        if (result.Count == 0)
        {
            throw Errors.Unsupported(
                "GUIDED_COMPONENT_ACTION_UNAVAILABLE",
                "Discovery found no component with an action supported by guided recipe scaffolding.",
                "Inspect component capability reasons in expert output or use the non-interactive workflow.");
        }

        return result
            .OrderBy(item => item.DisplayLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.CandidateId, StringComparer.Ordinal)
            .ToArray();
    }

    public static GuidedDryRunReview CreateDryRunReview(PlanRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var operations = result.Plan.Operations;
        return new GuidedDryRunReview(
            result.Plan.Fingerprint,
            operations.Count,
            operations.Sum(operation => operation.SelectedDrawCalls.Count),
            operations
                .SelectMany(operation => operation.SelectedDrawCalls)
                .Select(drawCall => drawCall.Lod)
                .Distinct()
                .Count(),
            operations
                .SelectMany(operation => operation.TargetBlocks)
                .Select(block => block.Index)
                .Distinct()
                .Count(),
            operations
                .SelectMany(operation => operation.GeometryTargets)
                .Sum(target => target.SelectedVertexCount)
                + operations
                    .Where(operation => operation.CoupledTransformTarget is not null)
                    .Sum(operation => operation.CoupledTransformTarget!.Visual.VertexCount),
            operations.Any(operation => operation.CoupledTransformTarget is not null));
    }

    public static async Task<GuidedCatalogueSelection> SelectFromVpkAsync(
        HeroCatalogueDocument catalogue,
        IResourceCatalogInventory inventory,
        bool requireBaseSourceIdentity,
        CancellationToken cancellationToken = default)
    {
        var verification = await HeroCatalogueVerifier.VerifyAsync(catalogue, inventory, cancellationToken)
            .ConfigureAwait(false);
        if (requireBaseSourceIdentity
            && string.Equals(verification.Source.Status, HeroCatalogueVerificationContract.SourceStale, StringComparison.Ordinal))
        {
            throw Errors.Input(
                "CATALOGUE_SOURCE_STALE",
                "The selected base VPK does not match the catalogue's expected directory identity.",
                "Use the matching game build or deliberately update and re-verify the catalogue revision.");
        }

        var result = ToSelection(
            verification,
            requireBaseSourceIdentity ? verification.Source.Status : "not_applicable");
        RequireAnySelectable(result);
        return result;
    }

    public static GuidedCatalogueSelection SelectFromCompiledModel(
        HeroCatalogueDocument catalogue,
        string compiledModelPath)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentException.ThrowIfNullOrWhiteSpace(compiledModelPath);
        HeroCatalogueValidator.Validate(catalogue);
        var fileName = Path.GetFileName(compiledModelPath);
        if (!fileName.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase))
        {
            throw Errors.Input(
                "GUIDED_COMPILED_MODEL_INVALID",
                "The configured compiled model is not a .vmdl_c file.",
                "Choose an existing compiled Source 2 model ending in .vmdl_c.");
        }

        var heroes = catalogue.Heroes
            .OrderBy(hero => hero.DisplayName, StringComparer.Ordinal)
            .ThenBy(hero => hero.HeroId, StringComparer.Ordinal)
            .Select(hero => new GuidedHeroChoice(
                hero.HeroId,
                hero.DisplayName,
                hero.RosterStatus,
                hero.Resources
                    .OrderBy(resource => resource.DisplayName, StringComparer.Ordinal)
                    .ThenBy(resource => resource.ResourceId, StringComparer.Ordinal)
                    .Select(resource => new GuidedResourceChoice(
                        resource.ResourceId,
                        resource.DisplayName,
                        resource.Role,
                        resource.LogicalPath,
                        string.Equals(Path.GetFileName(resource.LogicalPath), fileName, StringComparison.OrdinalIgnoreCase)
                            ? HeroCatalogueVerificationContract.ResourceVerified
                            : HeroCatalogueVerificationContract.ResourceMissing,
                        []))
                    .ToArray()))
            .ToArray();
        var result = new GuidedCatalogueSelection(catalogue.CatalogueId, catalogue.Revision, "not_applicable", heroes);
        RequireAnySelectable(result);
        return result;
    }

    private static GuidedCatalogueSelection ToSelection(
        HeroCatalogueVerification verification,
        string sourceStatus) =>
        new(
            verification.CatalogueId,
            verification.Revision,
            sourceStatus,
            verification.Heroes.Select(hero => new GuidedHeroChoice(
                hero.HeroId,
                hero.DisplayName,
                hero.RosterStatus,
                hero.Resources.Select(resource => new GuidedResourceChoice(
                    resource.ResourceId,
                    resource.DisplayName,
                    resource.Role,
                    resource.LogicalPath,
                    resource.VerificationStatus,
                    resource.CandidateLogicalPaths)).ToArray())).ToArray());

    private static List<GuidedActionChoice> CreateActions(
        IReadOnlyList<ComponentCapability> capabilities)
    {
        var actions = new List<GuidedActionChoice>();
        foreach (var capability in capabilities
            .Where(item => item.Availability == ComponentDiscoveryContract.Available)
            .OrderBy(item => item.OperationKind, StringComparer.Ordinal)
            .ThenBy(item => item.OperationVersion))
        {
            if (capability.OperationKind == "remove_component" && capability.OperationVersion == 1)
            {
                actions.Add(new GuidedActionChoice(
                    "remove_component@1:remove",
                    "Remove",
                    RecipeScaffoldContract.RemoveIntent,
                    capability.OperationKind,
                    capability.OperationVersion,
                    false,
                    false,
                    false));
            }
            else if (capability.OperationKind == "transform_component" && capability.OperationVersion is 1 or 2)
            {
                actions.Add(new GuidedActionChoice(
                    $"transform_component@{capability.OperationVersion}:uniform-scale",
                    "Scale uniformly",
                    RecipeScaffoldContract.UniformScaleIntent,
                    capability.OperationKind,
                    capability.OperationVersion,
                    true,
                    false,
                    capability.OperationVersion == 2));
                if (capability.OperationVersion == 1)
                {
                    actions.Add(new GuidedActionChoice(
                        "transform_component@1:translate",
                        "Move",
                        RecipeScaffoldContract.TranslateIntent,
                        capability.OperationKind,
                        capability.OperationVersion,
                        false,
                        true,
                        false));
                }
            }
        }

        return actions;
    }

    private static void RequireAnySelectable(GuidedCatalogueSelection selection)
    {
        if (!selection.Heroes.Any(hero => hero.Selectable))
        {
            throw Errors.Input(
                "GUIDED_SOURCE_HAS_NO_CATALOGUE_RESOURCE",
                "The selected source contains no exact resource locator from this catalogue.",
                "Choose another source or update and review the catalogue; moved or ambiguous paths are never guessed.");
        }
    }

    private static S2ModKitException InvalidState(string summary) => Errors.InvalidRecipe(
        "GUIDED_SESSION_STATE_INVALID",
        summary,
        "Start a new session or restore an intact checkpoint.");
}
