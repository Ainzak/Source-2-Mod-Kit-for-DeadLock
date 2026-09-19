using System.CommandLine;
using System.Globalization;
using S2ModKit.Adapters.Source2;
using S2ModKit.Adapters.Vpk;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;
using S2ModKit.Reporting;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private const int GuidedBackChoice = -1;

    private async Task<GuidedWorkflowSession?> RunInteractiveEntryAsync(
        string? cataloguePath,
        string? sessionPath,
        string[] baseVpkPaths,
        string[] modVpkPaths,
        string[] compiledModelPaths,
        bool resume,
        bool expert,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var promptedForSession = string.IsNullOrWhiteSpace(sessionPath);
        if (promptedForSession)
        {
            output.WriteLine("S2ModKit interactive setup");
            sessionPath = await ReadGuidedTextAsync(input, output, "Session JSON path: ", cancellationToken).ConfigureAwait(false);
            if (sessionPath is null)
            {
                output.WriteLine("Canceled; no session was created.");
                return null;
            }
        }

        var fullSessionPath = NormalizeGuidedPath(sessionPath!, "GUIDED_SESSION_PATH_INVALID");
        if (promptedForSession && File.Exists(fullSessionPath) && !resume)
        {
            resume = true;
            output.WriteLine("Existing guided session found; resuming it.");
        }

        if (resume && string.IsNullOrWhiteSpace(cataloguePath))
        {
            var saved = await ReadGuidedSessionAsync(fullSessionPath, cancellationToken).ConfigureAwait(false);
            cataloguePath = saved.CataloguePath;
        }
        else if (string.IsNullOrWhiteSpace(cataloguePath))
        {
            cataloguePath = CataloguePathResolver.TryFindPackagedCatalogue(AppContext.BaseDirectory);
            if (cataloguePath is not null)
            {
                output.WriteLine($"Using packaged hero catalogue: {cataloguePath}");
            }
            else
            {
                cataloguePath = await ReadGuidedTextAsync(input, output, "Hero catalogue JSON path: ", cancellationToken).ConfigureAwait(false);
                if (cataloguePath is null)
                {
                    output.WriteLine("Canceled; no session was created.");
                    return null;
                }
            }
        }

        if (!resume && baseVpkPaths.Length == 0 && modVpkPaths.Length == 0 && compiledModelPaths.Length == 0)
        {
            output.WriteLine("Choose a source type:");
            output.WriteLine("  1. Base VPK");
            output.WriteLine("  2. Mod VPK");
            output.WriteLine("  3. Compiled model");
            var sourceType = await ReadGuidedChoiceAsync(input, output, 3, cancellationToken).ConfigureAwait(false);
            if (sourceType is null)
            {
                output.WriteLine("Canceled; no session was created.");
                return null;
            }

            var sourcePath = await ReadGuidedTextAsync(input, output, "Source path: ", cancellationToken).ConfigureAwait(false);
            if (sourcePath is null)
            {
                output.WriteLine("Canceled; no session was created.");
                return null;
            }

            switch (sourceType.Value)
            {
                case 0:
                    baseVpkPaths = [sourcePath];
                    break;
                case 1:
                    modVpkPaths = [sourcePath];
                    break;
                default:
                    compiledModelPaths = [sourcePath];
                    break;
            }
        }

        return await RunInteractiveAsync(
            cataloguePath!,
            fullSessionPath,
            baseVpkPaths,
            modVpkPaths,
            compiledModelPaths,
            resume,
            expert,
            input,
            output,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GuidedWorkflowSession> RunInteractiveAsync(
        string cataloguePath,
        string sessionPath,
        string[] baseVpkPaths,
        string[] modVpkPaths,
        string[] compiledModelPaths,
        bool resume,
        bool expert,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var fullCataloguePath = NormalizeGuidedPath(cataloguePath, "GUIDED_CATALOGUE_PATH_INVALID");
        var fullSessionPath = NormalizeGuidedPath(sessionPath, "GUIDED_SESSION_PATH_INVALID");
        GuidedWorkflowSession session;
        if (resume)
        {
            if (baseVpkPaths.Length != 0 || modVpkPaths.Length != 0 || compiledModelPaths.Length != 0)
            {
                throw Errors.Input(
                    "GUIDED_RESUME_SOURCES_FORBIDDEN",
                    "Source options cannot be changed while resuming a guided session.",
                    "Use only --catalogue, --session, and --resume, or start a new session file.");
            }

            session = await ReadGuidedSessionAsync(fullSessionPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(session.CataloguePath, fullCataloguePath, StringComparison.OrdinalIgnoreCase))
            {
                throw Errors.Input(
                    "GUIDED_RESUME_CATALOGUE_MISMATCH",
                    "The supplied catalogue is not the catalogue saved in this session.",
                    "Resume with the original catalogue path or start a new session.");
            }

            if (session.Status == GuidedWorkflowContract.CompleteStatus)
            {
                output.WriteLine("Guided session is already complete.");
                return session;
            }

            session = session with
            {
                Status = GuidedWorkflowContract.ActiveStatus,
                Expert = session.Expert || expert,
            };
            await WriteGuidedSessionAsync(fullSessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
            output.WriteLine("Resumed guided session.");
        }
        else
        {
            if (File.Exists(fullSessionPath) || Directory.Exists(fullSessionPath))
            {
                throw Errors.Input(
                    "GUIDED_SESSION_EXISTS",
                    $"Guided session '{fullSessionPath}' already exists.",
                    "Use --resume or choose a new --session path; existing files are never replaced by a new session.");
            }

            var sources = CreateGuidedSources(baseVpkPaths, modVpkPaths, compiledModelPaths);
            session = GuidedWorkflow.CreateSession(fullCataloguePath, sources, expert);
            await WriteGuidedSessionAsync(fullSessionPath, session, overwrite: false, cancellationToken).ConfigureAwait(false);
            output.WriteLine("S2ModKit guided workflow");
            output.WriteLine("Enter a number, or 'cancel' to save and stop.");
        }

        var catalogue = await ReadCatalogueAsync(fullCataloguePath, cancellationToken).ConfigureAwait(false);
        if (session.Step == GuidedWorkflowContract.SourceSelectionStep)
        {
            output.WriteLine("Choose a source:");
            for (var index = 0; index < session.Sources.Count; index++)
            {
                var source = session.Sources[index];
                output.WriteLine(session.Expert
                    ? string.Create(CultureInfo.InvariantCulture, $"  {index + 1}. {source.DisplayName} [{source.Kind}; {source.SourceId}] {source.Path}")
                    : string.Create(CultureInfo.InvariantCulture, $"  {index + 1}. {source.DisplayName} ({FormatSourceKind(source.Kind)})"));
            }

            var selectedIndex = await ReadGuidedChoiceAsync(input, output, session.Sources.Count, cancellationToken).ConfigureAwait(false);
            if (selectedIndex is null)
            {
                return await PauseGuidedSessionAsync(fullSessionPath, session, output, cancellationToken).ConfigureAwait(false);
            }

            session = session with
            {
                SelectedSourceId = session.Sources[selectedIndex.Value].SourceId,
                Step = GuidedWorkflowContract.HeroSelectionStep,
            };
            await WriteGuidedSessionAsync(fullSessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
        }

        var selectedSource = session.Sources.Single(source =>
            string.Equals(source.SourceId, session.SelectedSourceId, StringComparison.Ordinal));
        var selection = await CreateGuidedSelectionAsync(catalogue, selectedSource, cancellationToken).ConfigureAwait(false);
        ReportUnavailableGuidedResources(selection, session.Expert, output);
        ValidateGuidedSelectionCheckpoint(session, selection);

        IReadOnlyList<GuidedComponentChoice>? components = null;
        while (session.Step is GuidedWorkflowContract.HeroSelectionStep
                or GuidedWorkflowContract.ResourceSelectionStep
                or GuidedWorkflowContract.ComponentSelectionStep)
        {
            while (session.Step is GuidedWorkflowContract.HeroSelectionStep or GuidedWorkflowContract.ResourceSelectionStep)
            {
                if (session.Step == GuidedWorkflowContract.HeroSelectionStep)
                {
                    var heroes = selection.Heroes.Where(hero => hero.Selectable).ToArray();
                    output.WriteLine("Choose a hero:");
                    for (var index = 0; index < heroes.Length; index++)
                    {
                        output.WriteLine(session.Expert
                            ? string.Create(CultureInfo.InvariantCulture, $"  {index + 1}. {heroes[index].DisplayName} [{heroes[index].HeroId}; {heroes[index].RosterStatus}]")
                            : string.Create(CultureInfo.InvariantCulture, $"  {index + 1}. {heroes[index].DisplayName}"));
                    }

                    var selectedHeroIndex = await ReadGuidedChoiceAsync(input, output, heroes.Length, cancellationToken).ConfigureAwait(false);
                    if (selectedHeroIndex is null)
                    {
                        return await PauseGuidedSessionAsync(fullSessionPath, session, output, cancellationToken).ConfigureAwait(false);
                    }

                    session = session with
                    {
                        SelectedHeroId = heroes[selectedHeroIndex.Value].HeroId,
                        Step = GuidedWorkflowContract.ResourceSelectionStep,
                    };
                    await WriteGuidedSessionAsync(fullSessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var hero = selection.Heroes.Single(item =>
                    string.Equals(item.HeroId, session.SelectedHeroId, StringComparison.Ordinal));
                var resources = hero.Resources.Where(resource => resource.Selectable).ToArray();
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Choose a {hero.DisplayName} resource:"));
                for (var index = 0; index < resources.Length; index++)
                {
                    output.WriteLine(session.Expert
                        ? string.Create(CultureInfo.InvariantCulture, $"  {index + 1}. {resources[index].DisplayName} [{resources[index].ResourceId}; {resources[index].Role}] {resources[index].LogicalPath}")
                        : string.Create(CultureInfo.InvariantCulture, $"  {index + 1}. {resources[index].DisplayName} ({FormatResourceRole(resources[index].Role)})"));
                }

                output.WriteLine("  0. Back to hero selection");
                var selectedResourceIndex = await ReadGuidedChoiceAsync(input, output, resources.Length, cancellationToken, allowBack: true).ConfigureAwait(false);
                if (selectedResourceIndex is null)
                {
                    return await PauseGuidedSessionAsync(fullSessionPath, session, output, cancellationToken).ConfigureAwait(false);
                }

                if (selectedResourceIndex.Value == GuidedBackChoice)
                {
                    session = session with
                    {
                        SelectedHeroId = null,
                        SelectedResourceId = null,
                        SelectedLogicalPath = null,
                        Step = GuidedWorkflowContract.HeroSelectionStep,
                    };
                    await WriteGuidedSessionAsync(fullSessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var resource = resources[selectedResourceIndex.Value];
                session = session with
                {
                    SelectedResourceId = resource.ResourceId,
                    SelectedLogicalPath = resource.LogicalPath,
                    Step = GuidedWorkflowContract.ComponentSelectionStep,
                };
                await WriteGuidedSessionAsync(fullSessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
            }

            session = await EnsureGuidedProjectAsync(
                fullSessionPath,
                session,
                selectedSource,
                catalogue,
                cancellationToken).ConfigureAwait(false);
            var discovery = await application
                .DiscoverComponentsAsync(session.ProjectRoot!, cancellationToken)
                .ConfigureAwait(false);
            components = GuidedWorkflow.CreateComponentChoices(discovery);
            var hiddenComponentCount = discovery.Candidates.Count - components.Count;
            if (hiddenComponentCount > 0)
            {
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{hiddenComponentCount} component(s) have no guided action and are not selectable."));
            }

            output.WriteLine("Choose a component:");
            for (var index = 0; index < components.Count; index++)
            {
                var component = components[index];
                var actions = string.Join(", ", component.Actions.Select(action => action.DisplayName));
                output.WriteLine(session.Expert
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"  {index + 1}. {component.DisplayLabel} [{component.Kind}; {component.CandidateId}; LODs={string.Join(",", component.Lods)}] — {actions}")
                    : string.Create(CultureInfo.InvariantCulture, $"  {index + 1}. {component.DisplayLabel} — {actions}"));
            }

            output.WriteLine("  0. Back to resource selection");
            var selectedIndex = await ReadGuidedChoiceAsync(input, output, components.Count, cancellationToken, allowBack: true).ConfigureAwait(false);
            if (selectedIndex is null)
            {
                return await PauseGuidedSessionAsync(fullSessionPath, session, output, cancellationToken).ConfigureAwait(false);
            }

            if (selectedIndex.Value == GuidedBackChoice)
            {
                session = ResetGuidedResourceSelection(session);
                await WriteGuidedSessionAsync(fullSessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
                components = null;
                continue;
            }

            var chosenComponent = components[selectedIndex.Value];
            session = session with
            {
                SelectedComponentId = chosenComponent.CandidateId,
                SelectedComponentLabel = chosenComponent.DisplayLabel,
                Step = GuidedWorkflowContract.ActionSelectionStep,
            };
            await WriteGuidedSessionAsync(fullSessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
        }

        RecipeDocument? currentRecipe = null;
        if (session.Step is GuidedWorkflowContract.ActionSelectionStep or GuidedWorkflowContract.ReviewStep)
        {
            session = await EnsureGuidedProjectAsync(
                fullSessionPath,
                session,
                selectedSource,
                catalogue,
                cancellationToken).ConfigureAwait(false);
            if (components is null)
            {
                var discovery = await application
                    .DiscoverComponentsAsync(session.ProjectRoot!, cancellationToken)
                    .ConfigureAwait(false);
                components = GuidedWorkflow.CreateComponentChoices(discovery);
            }

            var selectedComponent = components.SingleOrDefault(component =>
                string.Equals(component.CandidateId, session.SelectedComponentId, StringComparison.Ordinal))
                ?? throw Errors.Selection(
                    "GUIDED_COMPONENT_STALE",
                    "The saved component is no longer present with a guided action.",
                    "Start a new session and select from current discovery results.");

            if (session.Step == GuidedWorkflowContract.ActionSelectionStep)
            {
                GuidedActionChoice action;
                if (session.SelectedIntent is null)
                {
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Choose an action for {selectedComponent.DisplayLabel}:"));
                    for (var index = 0; index < selectedComponent.Actions.Count; index++)
                    {
                        var item = selectedComponent.Actions[index];
                        output.WriteLine(session.Expert
                            ? string.Create(CultureInfo.InvariantCulture, $"  {index + 1}. {item.DisplayName} [{item.OperationKind}@{item.OperationVersion}]")
                            : string.Create(CultureInfo.InvariantCulture, $"  {index + 1}. {item.DisplayName}"));
                    }

                    var selectedIndex = await ReadGuidedChoiceAsync(input, output, selectedComponent.Actions.Count, cancellationToken).ConfigureAwait(false);
                    if (selectedIndex is null)
                    {
                        return await PauseGuidedSessionAsync(fullSessionPath, session, output, cancellationToken).ConfigureAwait(false);
                    }

                    action = selectedComponent.Actions[selectedIndex.Value];
                    session = session with
                    {
                        SelectedIntent = action.Intent,
                        SelectedOperationKind = action.OperationKind,
                        SelectedOperationVersion = action.OperationVersion,
                    };
                    await WriteGuidedSessionAsync(fullSessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    action = selectedComponent.Actions.SingleOrDefault(item =>
                        string.Equals(item.Intent, session.SelectedIntent, StringComparison.Ordinal)
                        && string.Equals(item.OperationKind, session.SelectedOperationKind, StringComparison.Ordinal)
                        && item.OperationVersion == session.SelectedOperationVersion)
                        ?? throw Errors.Selection(
                            "GUIDED_ACTION_STALE",
                            "The saved action is no longer available for the selected component.",
                            "Start a new session and select from current capability results.");
                }

                var parameters = await ReadGuidedActionParametersAsync(action, input, output, cancellationToken).ConfigureAwait(false);
                if (parameters is null)
                {
                    return await PauseGuidedSessionAsync(fullSessionPath, session, output, cancellationToken).ConfigureAwait(false);
                }

                var recipePath = session.RecipePath ?? CreateGuidedRecipePath(fullSessionPath);
                session = session with
                {
                    UniformScale = parameters.UniformScale,
                    TranslationX = parameters.TranslationX,
                    TranslationY = parameters.TranslationY,
                    TranslationZ = parameters.TranslationZ,
                    MaximumVertexDisplacement = parameters.MaximumVertexDisplacement,
                    MaximumCollisionDisplacement = parameters.MaximumCollisionDisplacement,
                    RecipePath = recipePath,
                };
                await WriteGuidedSessionAsync(fullSessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
                var scaffold = await application.ScaffoldRecipeAsync(
                    session.ProjectRoot!,
                    new RecipeScaffoldRequest(
                        [session.SelectedComponentId!],
                        action.Intent,
                        recipePath,
                        parameters.UniformScale,
                        parameters.TranslationX,
                        parameters.TranslationY,
                        parameters.TranslationZ,
                        ReferenceLod: null,
                        parameters.MaximumVertexDisplacement,
                        parameters.MaximumCollisionDisplacement),
                    cancellationToken).ConfigureAwait(false);
                session = session with
                {
                    RecipePath = scaffold.OutputPath,
                    RecipeContentHash = scaffold.RecipeContentHash,
                    Step = GuidedWorkflowContract.ReviewStep,
                };
                currentRecipe = scaffold.Recipe;
                await WriteGuidedSessionAsync(fullSessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
            }

            if (session.Step == GuidedWorkflowContract.ReviewStep)
            {
                var recipe = currentRecipe
                    ?? await ReadGuidedRecipeAsync(session, cancellationToken).ConfigureAwait(false);
                var plan = await application.PlanAsync(session.ProjectRoot!, recipe, cancellationToken).ConfigureAwait(false);
                var review = GuidedWorkflow.CreateDryRunReview(plan);
                session = session with
                {
                    PlanFingerprint = review.PlanFingerprint,
                    PlannedDrawCallCount = review.SelectedDrawCallCount,
                    PlannedLodCount = review.LodCount,
                    PlannedTargetBlockCount = review.TargetBlockCount,
                    PlannedVertexCount = review.SelectedVertexCount,
                    PlannedCoupledCollision = review.IncludesCoupledCollision,
                    Step = GuidedWorkflowContract.OutputSelectionStep,
                };
                await WriteGuidedSessionAsync(fullSessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
            }
        }

        if (session.Step == GuidedWorkflowContract.OutputSelectionStep)
        {
            RenderGuidedDryRun(output, session);
            if (session.OutputChoice is null)
            {
                output.WriteLine("Choose output:");
                output.WriteLine("  1. Keep recipe and plan only");
                output.WriteLine("  2. Build verified model");
                output.WriteLine("  3. Build verified model and minimal VPK");
                output.WriteLine("  4. Build, package, and export VPK");
                var selected = await ReadGuidedChoiceAsync(input, output, 4, cancellationToken).ConfigureAwait(false);
                if (selected is null)
                {
                    return await PauseGuidedSessionAsync(fullSessionPath, session, output, cancellationToken).ConfigureAwait(false);
                }

                session = session with
                {
                    OutputChoice = selected.Value switch
                    {
                        0 => GuidedWorkflowContract.PlanOnlyOutput,
                        1 => GuidedWorkflowContract.BuildOnlyOutput,
                        2 => GuidedWorkflowContract.MinimalPackageOutput,
                        _ => GuidedWorkflowContract.ExportPackageOutput,
                    },
                };
                await WriteGuidedSessionAsync(fullSessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
            }

            if (session.OutputChoice == GuidedWorkflowContract.PlanOnlyOutput)
            {
                session = await CompleteGuidedSessionAsync(fullSessionPath, session, output, "Recipe and plan kept; no model, package, or installation was created.", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var recipe = await ReadGuidedRecipeAsync(session, cancellationToken).ConfigureAwait(false);
                if (session.BuildId is null)
                {
                    var built = await application.BuildAsync(session.ProjectRoot!, recipe, cancellationToken).ConfigureAwait(false);
                    session = session with
                    {
                        BuildId = built.Build.BuildId,
                        BuildContentHash = built.Build.ContentHash,
                    };
                    await WriteGuidedSessionAsync(fullSessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var verified = await application.VerifyAsync(session.ProjectRoot!, session.BuildId, cancellationToken).ConfigureAwait(false);
                    if (verified.Build.ContentHash != session.BuildContentHash)
                    {
                        throw Errors.Verification("GUIDED_BUILD_STALE", "The saved build no longer matches its checkpointed content identity.", "Restore the verified build or start a new guided session.");
                    }
                }

                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Verified build: {session.BuildId} ({session.BuildContentHash})"));
                if (session.OutputChoice == GuidedWorkflowContract.BuildOnlyOutput)
                {
                    session = await CompleteGuidedSessionAsync(fullSessionPath, session, output, "Verified model build complete; no package or installation was created.", cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var packaging = RequirePackagingApplication();
                    if (session.PackageId is null)
                    {
                        var packaged = await packaging.CreateMinimalAsync(session.ProjectRoot!, session.BuildId, requireExternalVerifier: false, cancellationToken).ConfigureAwait(false);
                        session = session with
                        {
                            PackageId = packaged.Package.PackageId,
                            PackageContentHash = packaged.Package.ContentHash,
                        };
                        await WriteGuidedSessionAsync(fullSessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var verified = await packaging.VerifyAsync(session.ProjectRoot!, session.PackageId, requireExternalVerifier: false, cancellationToken).ConfigureAwait(false);
                        if (verified.Package.ContentHash != session.PackageContentHash)
                        {
                            throw Errors.Verification("GUIDED_PACKAGE_STALE", "The saved package no longer matches its checkpointed content identity.", "Restore the verified package or recreate it from the guided build.");
                        }
                    }

                    output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Verified minimal package: {session.PackageId} ({session.PackageContentHash})"));
                    if (session.OutputChoice == GuidedWorkflowContract.ExportPackageOutput && session.ExportPath is null)
                    {
                        var exportPath = await ReadGuidedTextAsync(input, output, "New export .vpk path: ", cancellationToken).ConfigureAwait(false);
                        if (exportPath is null)
                        {
                            return await PauseGuidedSessionAsync(fullSessionPath, session, output, cancellationToken).ConfigureAwait(false);
                        }

                        var exported = await packaging.ExportAsync(
                            session.ProjectRoot!,
                            session.PackageId,
                            NormalizeGuidedPath(exportPath, "GUIDED_EXPORT_PATH_INVALID"),
                            cancellationToken).ConfigureAwait(false);
                        session = session with
                        {
                            ExportPath = exported.OutputPath,
                            ExportContentHash = exported.ContentHash,
                        };
                        await WriteGuidedSessionAsync(fullSessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
                        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Exported verified package: {exported.OutputPath}"));
                    }

                    session = session with { Step = GuidedWorkflowContract.InstallSelectionStep };
                    await WriteGuidedSessionAsync(fullSessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (session.Step == GuidedWorkflowContract.InstallSelectionStep)
        {
            if (session.InstallationId is not null)
            {
                var verified = await RequireAddonApplication().VerifyActiveAsync(session.AddonsRoot!, session.ProjectRoot!, session.InstallationId, cancellationToken).ConfigureAwait(false);
                session = session with
                {
                    InstallationStatus = verified.Status,
                    Step = GuidedWorkflowContract.RollbackSelectionStep,
                };
                await WriteGuidedSessionAsync(fullSessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
                WriteGuidedRollbackCommand(output, session);
            }
            else
            {
                output.WriteLine("Choose installation:");
                output.WriteLine("  1. Do not install");
                output.WriteLine("  2. Install to an automatically verified slot");
                output.WriteLine("  3. Install to an explicit verified slot");
                var selected = await ReadGuidedChoiceAsync(input, output, 3, cancellationToken).ConfigureAwait(false);
                if (selected is null)
                {
                    return await PauseGuidedSessionAsync(fullSessionPath, session, output, cancellationToken).ConfigureAwait(false);
                }

                if (selected.Value == 0)
                {
                    session = await CompleteGuidedSessionAsync(fullSessionPath, session, output, "Verified package kept; nothing was installed.", cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var addonsRoot = await ReadGuidedTextAsync(input, output, "Deadlock addons directory: ", cancellationToken).ConfigureAwait(false);
                    if (addonsRoot is null)
                    {
                        return await PauseGuidedSessionAsync(fullSessionPath, session, output, cancellationToken).ConfigureAwait(false);
                    }

                    var fullAddonsRoot = NormalizeGuidedPath(addonsRoot, "GUIDED_ADDONS_PATH_INVALID");
                    var inventory = await RequireAddonApplication().InventoryAsync(fullAddonsRoot, session.ProjectRoot!, session.PackageId!, cancellationToken).ConfigureAwait(false);
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Colliding archives: {inventory.Collisions.Count}"));
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Verified automatic slots: {(inventory.AvailableAutomaticSlots.Count == 0 ? "none" : string.Join(", ", inventory.AvailableAutomaticSlots.Select(slot => slot.ToString("00", CultureInfo.InvariantCulture))))}"));

                    string slot;
                    if (selected.Value == 1)
                    {
                        slot = "auto";
                    }
                    else
                    {
                        var explicitSlot = await ReadGuidedSlotAsync(input, output, cancellationToken).ConfigureAwait(false);
                        if (explicitSlot is null)
                        {
                            return await PauseGuidedSessionAsync(fullSessionPath, session, output, cancellationToken).ConfigureAwait(false);
                        }

                        slot = explicitSlot.Value.ToString("00", CultureInfo.InvariantCulture);
                    }

                    var targetSlot = slot == "auto"
                        ? inventory.AvailableAutomaticSlots.FirstOrDefault(-1)
                        : int.Parse(slot, CultureInfo.InvariantCulture);
                    if (targetSlot < 0)
                    {
                        throw Errors.Verification("ADDON_NO_SAFE_SLOT", "No verified automatic addon slot is available.", "Choose no install or resolve addon collisions before retrying.");
                    }

                    output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Package: {session.PackageId} ({session.PackageContentHash})"));
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Destination: {Path.Combine(fullAddonsRoot, $"pak{targetSlot:00}_dir.vpk")}"));
                    output.Write("Type 'install' to authorize this exact write: ");
                    var confirmation = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(confirmation?.Trim(), "install", StringComparison.Ordinal))
                    {
                        session = await CompleteGuidedSessionAsync(fullSessionPath, session, output, "Installation was not authorized; the verified package was kept.", cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var installed = await RequireAddonApplication().InstallAsync(fullAddonsRoot, session.ProjectRoot!, session.PackageId!, slot, cancellationToken).ConfigureAwait(false);
                        session = session with
                        {
                            AddonsRoot = fullAddonsRoot,
                            InstallationId = installed.Receipt.InstallationId,
                            InstallationTargetFileName = installed.Receipt.TargetFileName,
                            InstallationSlot = installed.Receipt.Slot,
                            InstallationStatus = "installed_unverified",
                        };
                        await WriteGuidedSessionAsync(fullSessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
                        WriteGuidedRollbackCommand(output, session);
                        var verified = await RequireAddonApplication().VerifyActiveAsync(fullAddonsRoot, session.ProjectRoot!, session.InstallationId, cancellationToken).ConfigureAwait(false);
                        session = session with
                        {
                            InstallationStatus = verified.Status,
                            Step = GuidedWorkflowContract.RollbackSelectionStep,
                        };
                        await WriteGuidedSessionAsync(fullSessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
                        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Active installation verified: {session.InstallationTargetFileName}"));
                    }
                }
            }
        }

        if (session.Step == GuidedWorkflowContract.RollbackSelectionStep)
        {
            WriteGuidedRollbackCommand(output, session);
            output.WriteLine("Choose next step:");
            output.WriteLine("  1. Leave the verified installation active for player testing");
            output.WriteLine("  2. Roll back now");
            var selected = await ReadGuidedChoiceAsync(input, output, 2, cancellationToken).ConfigureAwait(false);
            if (selected is null)
            {
                return await PauseGuidedSessionAsync(fullSessionPath, session, output, cancellationToken).ConfigureAwait(false);
            }

            if (selected.Value == 1)
            {
                var rolledBack = await RequireAddonApplication().RollbackAsync(session.AddonsRoot!, session.ProjectRoot!, session.InstallationId!, cancellationToken).ConfigureAwait(false);
                session = session with { InstallationStatus = rolledBack.Status };
                session = await CompleteGuidedSessionAsync(fullSessionPath, session, output, "Receipt-backed rollback completed.", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                session = await CompleteGuidedSessionAsync(fullSessionPath, session, output, "Verified installation left active for player testing. Keep the rollback command above.", cancellationToken).ConfigureAwait(false);
            }
        }

        if (session.Status != GuidedWorkflowContract.CompleteStatus)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Resume: s2mod interactive --catalogue \"{session.CataloguePath}\" --session \"{fullSessionPath}\" --resume"));
        }

        return session;
    }

    private async Task<GuidedWorkflowSession> EnsureGuidedProjectAsync(
        string sessionPath,
        GuidedWorkflowSession session,
        GuidedSourceOption source,
        HeroCatalogueDocument catalogue,
        CancellationToken cancellationToken)
    {
        if (session.ProjectReady)
        {
            return session;
        }

        var projectRoot = session.ProjectRoot ?? CreateGuidedProjectRoot(
            sessionPath,
            session.SessionId,
            source.SourceId,
            session.SelectedLogicalPath!);

        session = session with { ProjectRoot = projectRoot };
        await WriteGuidedSessionAsync(sessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
        if (Directory.Exists(projectRoot))
        {
            await application.DiscoverComponentsAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        }
        else if (source.Kind == GuidedWorkflowContract.CompiledModelSource)
        {
            await application.CreateProjectAsync(
                projectRoot,
                source.Path,
                InferCompiledResourceRoot(source.Path, session.SelectedLogicalPath!),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await application.CreateVpkProjectAsync(
                projectRoot,
                source.Path,
                session.SelectedLogicalPath!,
                source.Kind == GuidedWorkflowContract.BaseVpkSource
                    ? catalogue.Source.ExpectedDirectoryHash
                    : null,
                cancellationToken).ConfigureAwait(false);
        }

        session = session with { ProjectReady = true };
        await WriteGuidedSessionAsync(sessionPath, session, overwrite: true, cancellationToken).ConfigureAwait(false);
        return session;
    }

    private static async Task<GuidedActionParameters?> ReadGuidedActionParametersAsync(
        GuidedActionChoice action,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!action.RequiresScale && !action.RequiresTranslation)
        {
            return new GuidedActionParameters(null, null, null, null, null, null);
        }

        float? scale = null;
        float? translationX = null;
        float? translationY = null;
        float? translationZ = null;
        if (action.RequiresScale)
        {
            var result = await ReadGuidedNumberAsync(
                input,
                output,
                "Uniform scale [0.25..4.0, not 1]: ",
                value => value is >= 0.25f and <= 4f && value != 1f,
                "Enter a finite value from 0.25 through 4.0 other than 1, or 'cancel'.",
                blankValue: null,
                cancellationToken).ConfigureAwait(false);
            if (result.Canceled)
            {
                return null;
            }

            scale = result.Value;
        }

        if (action.RequiresTranslation)
        {
            while (true)
            {
                var x = await ReadGuidedNumberAsync(
                    input,
                    output,
                    "Move X [-256..256] (blank=0): ",
                    value => value is >= -256f and <= 256f,
                    "Enter a finite value from -256 through 256, blank for 0, or 'cancel'.",
                    0f,
                    cancellationToken).ConfigureAwait(false);
                if (x.Canceled)
                {
                    return null;
                }

                var y = await ReadGuidedNumberAsync(
                    input,
                    output,
                    "Move Y [-256..256] (blank=0): ",
                    value => value is >= -256f and <= 256f,
                    "Enter a finite value from -256 through 256, blank for 0, or 'cancel'.",
                    0f,
                    cancellationToken).ConfigureAwait(false);
                if (y.Canceled)
                {
                    return null;
                }

                var z = await ReadGuidedNumberAsync(
                    input,
                    output,
                    "Move Z [-256..256] (blank=0): ",
                    value => value is >= -256f and <= 256f,
                    "Enter a finite value from -256 through 256, blank for 0, or 'cancel'.",
                    0f,
                    cancellationToken).ConfigureAwait(false);
                if (z.Canceled)
                {
                    return null;
                }

                if (x.Value != 0f || y.Value != 0f || z.Value != 0f)
                {
                    translationX = x.Value;
                    translationY = y.Value;
                    translationZ = z.Value;
                    break;
                }

                output.WriteLine("At least one movement axis must be non-zero.");
            }
        }

        var maximumVertexDisplacement = await ReadGuidedNumberAsync(
            input,
            output,
            "Maximum vertex displacement (0..256]: ",
            value => value is > 0f and <= 256f,
            "Enter a finite value greater than 0 and at most 256, or 'cancel'.",
            blankValue: null,
            cancellationToken).ConfigureAwait(false);
        if (maximumVertexDisplacement.Canceled)
        {
            return null;
        }

        float? maximumCollisionDisplacement = null;
        if (action.RequiresCollisionLimit)
        {
            var collision = await ReadGuidedNumberAsync(
                input,
                output,
                "Maximum collision displacement (0..256]: ",
                value => value is > 0f and <= 256f,
                "Enter a finite value greater than 0 and at most 256, or 'cancel'.",
                blankValue: null,
                cancellationToken).ConfigureAwait(false);
            if (collision.Canceled)
            {
                return null;
            }

            maximumCollisionDisplacement = collision.Value;
        }

        return new GuidedActionParameters(
            scale,
            translationX,
            translationY,
            translationZ,
            maximumVertexDisplacement.Value,
            maximumCollisionDisplacement);
    }

    private static async Task<GuidedNumberResult> ReadGuidedNumberAsync(
        TextReader input,
        TextWriter output,
        string prompt,
        Func<float, bool> accepts,
        string invalidMessage,
        float? blankValue,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            output.Write(prompt);
            var value = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (value is null || value.Trim() is "cancel" or "quit" or "q")
            {
                return new GuidedNumberResult(true, 0f);
            }

            if (string.IsNullOrWhiteSpace(value) && blankValue is { } defaultValue)
            {
                return new GuidedNumberResult(false, defaultValue);
            }

            if (float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && float.IsFinite(parsed)
                && accepts(parsed))
            {
                return new GuidedNumberResult(false, parsed);
            }

            output.WriteLine(invalidMessage);
        }
    }

    private static async Task<RecipeDocument> ReadGuidedRecipeAsync(
        GuidedWorkflowSession session,
        CancellationToken cancellationToken)
    {
        var path = session.RecipePath!;
        if (!File.Exists(path))
        {
            throw Errors.Input(
                "GUIDED_RECIPE_NOT_FOUND",
                $"Guided recipe '{path}' does not exist.",
                "Restore the session-owned recipe or start a new session.");
        }

        var length = new FileInfo(path).Length;
        if (length is < 2 or > MaximumRecipeBytes)
        {
            throw Errors.InvalidRecipe(
                "GUIDED_RECIPE_SIZE_INVALID",
                "The guided recipe size is outside the supported range.",
                "Restore the intact session-owned recipe or start a new session.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var actualHash = ContentHash.Compute(bytes);
        if (actualHash != session.RecipeContentHash)
        {
            throw Errors.Input(
                "GUIDED_RECIPE_STALE",
                "The guided recipe no longer matches the checkpointed content identity.",
                "Do not use the changed recipe; restore it or start a new session.");
        }

        var recipe = JsonDefaults.Deserialize<RecipeDocument>(bytes, "Guided recipe");
        RecipeValidator.Validate(recipe);
        return recipe;
    }

    private static void RenderGuidedDryRun(TextWriter output, GuidedWorkflowSession session)
    {
        if (session.Step != GuidedWorkflowContract.OutputSelectionStep)
        {
            return;
        }

        output.WriteLine("Dry-run passed:");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Component: {session.SelectedComponentLabel}"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Action: {FormatGuidedIntent(session.SelectedIntent!)} ({session.SelectedOperationKind}@{session.SelectedOperationVersion})"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Selection: {session.PlannedDrawCallCount} draw call(s) across {session.PlannedLodCount} LOD(s)"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Planned changes: {session.PlannedTargetBlockCount} compiled-resource block(s)"));
        if (session.PlannedVertexCount > 0)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Selected vertices: {session.PlannedVertexCount}"));
        }

        if (session.PlannedCoupledCollision == true)
        {
            output.WriteLine("  Collision: coupled convex transform included");
        }

        output.WriteLine("  Model bytes built: no");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Recipe: {session.RecipePath}"));
        if (session.Expert)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Plan fingerprint: {session.PlanFingerprint}"));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Project: {session.ProjectRoot}"));
        }

        output.WriteLine("Ready for output selection.");
    }

    private static string CreateGuidedProjectRoot(
        string sessionPath,
        string sessionId,
        string sourceId,
        string logicalPath)
    {
        var parent = Path.GetDirectoryName(sessionPath)!;
        var identity = ContentHash.Compute(System.Text.Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"{sessionId}\n{sourceId}\n{logicalPath}")));
        return Path.Combine(
            parent,
            $"{Path.GetFileNameWithoutExtension(sessionPath)}.projects",
            identity.Value[..16]);
    }

    private static string CreateGuidedRecipePath(string sessionPath)
    {
        var parent = Path.GetDirectoryName(sessionPath)!;
        return Path.Combine(parent, $"{Path.GetFileNameWithoutExtension(sessionPath)}.recipe.json");
    }

    private static string InferCompiledResourceRoot(string sourcePath, string logicalPath)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);
        var logicalSuffix = logicalPath.Replace('/', Path.DirectorySeparatorChar);
        if (fullSourcePath.EndsWith(logicalSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var root = fullSourcePath[..^logicalSuffix.Length].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.IsNullOrWhiteSpace(root))
            {
                return root;
            }
        }

        return Path.GetDirectoryName(fullSourcePath)
            ?? throw Errors.Input(
                "GUIDED_COMPILED_MODEL_ROOT_INVALID",
                "The compiled model has no usable resource root.",
                "Place it below a resource directory and start a new session.");
    }

    private static string FormatGuidedIntent(string intent) => intent switch
    {
        RecipeScaffoldContract.RemoveIntent => "Remove",
        RecipeScaffoldContract.UniformScaleIntent => "Scale uniformly",
        _ => "Move",
    };

    private async Task<GuidedCatalogueSelection> CreateGuidedSelectionAsync(
        HeroCatalogueDocument catalogue,
        GuidedSourceOption source,
        CancellationToken cancellationToken)
    {
        if (source.Kind == GuidedWorkflowContract.CompiledModelSource)
        {
            if (!File.Exists(source.Path))
            {
                throw Errors.Input("GUIDED_SOURCE_NOT_FOUND", $"Compiled model '{source.Path}' does not exist.", "Choose an existing configured .vmdl_c file.");
            }

            return GuidedWorkflow.SelectFromCompiledModel(catalogue, source.Path);
        }

        var inventory = RequireCatalogueInventoryFactory().OpenReadOnly(source.Path);
        try
        {
            return await GuidedWorkflow.SelectFromVpkAsync(
                catalogue,
                inventory,
                source.Kind == GuidedWorkflowContract.BaseVpkSource,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            (inventory as IDisposable)?.Dispose();
        }
    }

    private static List<GuidedSourceOption> CreateGuidedSources(
        string[] baseVpkPaths,
        string[] modVpkPaths,
        string[] compiledModelPaths)
    {
        var sources = new List<GuidedSourceOption>();
        Add(baseVpkPaths, GuidedWorkflowContract.BaseVpkSource);
        Add(modVpkPaths, GuidedWorkflowContract.ModVpkSource);
        Add(compiledModelPaths, GuidedWorkflowContract.CompiledModelSource);
        return sources;

        void Add(string[] paths, string kind)
        {
            foreach (var path in paths)
            {
                var fullPath = NormalizeGuidedPath(path, "GUIDED_SOURCE_PATH_INVALID");
                var name = Path.GetFileName(fullPath);
                sources.Add(new GuidedSourceOption(
                    string.Create(CultureInfo.InvariantCulture, $"source-{sources.Count + 1}"),
                    kind,
                    string.IsNullOrWhiteSpace(name) ? fullPath : name,
                    fullPath));
            }
        }
    }

    private static async Task<int?> ReadGuidedChoiceAsync(
        TextReader input,
        TextWriter output,
        int choiceCount,
        CancellationToken cancellationToken,
        bool allowBack = false)
    {
        while (true)
        {
            output.Write("> ");
            var value = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            var normalized = value?.Trim();
            if (normalized is null or "cancel" or "quit" or "q")
            {
                return null;
            }

            if (allowBack && normalized is "0" or "back" or "previous")
            {
                return GuidedBackChoice;
            }

            if (int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var selected)
                && selected >= 1
                && selected <= choiceCount)
            {
                return selected - 1;
            }

            output.WriteLine(allowBack
                ? string.Create(CultureInfo.InvariantCulture, $"Enter 0-{choiceCount}, or 'cancel'.")
                : string.Create(CultureInfo.InvariantCulture, $"Enter 1-{choiceCount}, or 'cancel'."));
        }
    }

    private static GuidedWorkflowSession ResetGuidedResourceSelection(GuidedWorkflowSession session) => session with
    {
        Step = GuidedWorkflowContract.ResourceSelectionStep,
        SelectedResourceId = null,
        SelectedLogicalPath = null,
        ProjectRoot = null,
        ProjectReady = false,
        SelectedComponentId = null,
        SelectedComponentLabel = null,
        SelectedIntent = null,
        SelectedOperationKind = null,
        SelectedOperationVersion = null,
        UniformScale = null,
        TranslationX = null,
        TranslationY = null,
        TranslationZ = null,
        MaximumVertexDisplacement = null,
        MaximumCollisionDisplacement = null,
        RecipePath = null,
        RecipeContentHash = null,
        PlanFingerprint = null,
        PlannedDrawCallCount = null,
        PlannedLodCount = null,
        PlannedTargetBlockCount = null,
        PlannedVertexCount = null,
        PlannedCoupledCollision = null,
        OutputChoice = null,
        BuildId = null,
        BuildContentHash = null,
        PackageId = null,
        PackageContentHash = null,
        ExportPath = null,
        ExportContentHash = null,
        AddonsRoot = null,
        InstallationId = null,
        InstallationTargetFileName = null,
        InstallationSlot = null,
        InstallationStatus = null,
    };

    private static async Task<string?> ReadGuidedTextAsync(
        TextReader input,
        TextWriter output,
        string prompt,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            output.Write(prompt);
            var value = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (value is null || value.Trim() is "cancel" or "quit" or "q")
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            output.WriteLine("Enter a value, or 'cancel'.");
        }
    }

    private static async Task<int?> ReadGuidedSlotAsync(
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            output.Write("Explicit addon slot [00..99]: ");
            var value = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (value is null || value.Trim() is "cancel" or "quit" or "q")
            {
                return null;
            }

            if (value.Trim().Length == 2
                && int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var slot)
                && slot is >= 0 and <= 99)
            {
                return slot;
            }

            output.WriteLine("Enter a two-digit slot from 00 through 99, or 'cancel'.");
        }
    }

    private static void ReportUnavailableGuidedResources(
        GuidedCatalogueSelection selection,
        bool expert,
        TextWriter output)
    {
        var unavailable = selection.Heroes
            .SelectMany(hero => hero.Resources.Select(resource => (Hero: hero, Resource: resource)))
            .Where(item => !item.Resource.Selectable)
            .ToArray();
        if (unavailable.Length == 0)
        {
            return;
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{unavailable.Length} catalogue resource(s) are unavailable and cannot be selected{(expert ? ":" : "; use --expert for details.")}"));
        if (!expert)
        {
            return;
        }

        foreach (var item in unavailable)
        {
            var candidates = item.Resource.CandidateLogicalPaths.Count == 0
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture, $"; candidates={string.Join(", ", item.Resource.CandidateLogicalPaths)}");
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {item.Hero.DisplayName} / {item.Resource.DisplayName}: {item.Resource.VerificationStatus}; locator={item.Resource.LogicalPath}{candidates}"));
        }
    }

    private static void ValidateGuidedSelectionCheckpoint(
        GuidedWorkflowSession session,
        GuidedCatalogueSelection selection)
    {
        if (session.Step is GuidedWorkflowContract.SourceSelectionStep or GuidedWorkflowContract.HeroSelectionStep)
        {
            return;
        }

        var hero = selection.Heroes.SingleOrDefault(item =>
            string.Equals(item.HeroId, session.SelectedHeroId, StringComparison.Ordinal));
        if (hero is null || !hero.Selectable)
        {
            throw Errors.Input(
                "GUIDED_SELECTION_STALE",
                "The selected hero no longer has an exact selectable resource in the configured source.",
                "Start a new session after reviewing the source and catalogue changes.");
        }

        if (session.Step == GuidedWorkflowContract.ResourceSelectionStep)
        {
            return;
        }

        var resource = hero.Resources.SingleOrDefault(item =>
            string.Equals(item.ResourceId, session.SelectedResourceId, StringComparison.Ordinal));
        if (resource is null
            || !resource.Selectable
            || !string.Equals(resource.LogicalPath, session.SelectedLogicalPath, StringComparison.Ordinal))
        {
            throw Errors.Input(
                "GUIDED_SELECTION_STALE",
                "The saved resource is no longer an exact selectable catalogue locator in the configured source.",
                "Start a new session after reviewing the source and catalogue changes.");
        }
    }

    private static async Task<GuidedWorkflowSession> PauseGuidedSessionAsync(
        string sessionPath,
        GuidedWorkflowSession session,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var paused = session with { Status = GuidedWorkflowContract.PausedStatus };
        await WriteGuidedSessionAsync(sessionPath, paused, overwrite: true, cancellationToken).ConfigureAwait(false);
        var detail = session.InstallationId is not null
            ? "The receipt-backed installation may be active; use the rollback command shown above."
            : session.PackageId is not null
                ? "The verified package remains; nothing was installed."
                : session.BuildId is not null
                    ? "The verified build remains; no package or installation was created."
                    : session.RecipeContentHash is not null
                        ? "The project and recipe remain; no build, package, or installation was created."
                        : session.ProjectReady
                            ? "The imported project remains; no recipe, build, package, or installation was created."
                            : "No project, recipe, package, or installation was created.";
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Session saved. {detail}"));
        return paused;
    }

    private static async Task<GuidedWorkflowSession> CompleteGuidedSessionAsync(
        string sessionPath,
        GuidedWorkflowSession session,
        TextWriter output,
        string summary,
        CancellationToken cancellationToken)
    {
        var completed = session with
        {
            Status = GuidedWorkflowContract.CompleteStatus,
            Step = GuidedWorkflowContract.CompleteStep,
        };
        await WriteGuidedSessionAsync(sessionPath, completed, overwrite: true, cancellationToken).ConfigureAwait(false);
        output.WriteLine(summary);
        return completed;
    }

    private static void WriteGuidedRollbackCommand(TextWriter output, GuidedWorkflowSession session)
    {
        if (session.InstallationId is null || session.AddonsRoot is null || session.ProjectRoot is null)
        {
            return;
        }

        var applicationDirectory = AppContext.BaseDirectory;
        var appHostPath = Path.Combine(applicationDirectory, "s2mod.exe");
        var assemblyPath = Path.Combine(applicationDirectory, "s2mod.dll");
        var invocation = File.Exists(appHostPath)
            ? string.Create(CultureInfo.InvariantCulture, $"& \"{appHostPath}\"")
            : string.Create(CultureInfo.InvariantCulture, $"& dotnet \"{assemblyPath}\"");
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Rollback command: {invocation} addons rollback --addons-root \"{session.AddonsRoot}\" --project \"{session.ProjectRoot}\" --installation \"{session.InstallationId}\""));
    }

    private static async Task<GuidedWorkflowSession> ReadGuidedSessionAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw Errors.Input("GUIDED_SESSION_NOT_FOUND", $"Guided session '{path}' does not exist.", "Start a new session or provide its existing checkpoint path.");
        }

        var info = new FileInfo(path);
        if (info.Length is < 2 or > MaximumRecipeBytes)
        {
            throw Errors.InvalidRecipe("GUIDED_SESSION_SIZE_INVALID", "The guided session size is outside the supported range.", "Restore an intact version-1 session below 1 MiB.");
        }

        var session = JsonDefaults.Deserialize<GuidedWorkflowSession>(
            await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
            "Guided session");
        GuidedWorkflow.ValidateSession(session);
        return session;
    }

    private static async Task WriteGuidedSessionAsync(
        string path,
        GuidedWorkflowSession session,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        GuidedWorkflow.ValidateSession(session);
        if (!overwrite && (File.Exists(path) || Directory.Exists(path)))
        {
            throw Errors.Input("GUIDED_SESSION_EXISTS", $"Guided session '{path}' already exists.", "Use --resume or choose a new --session path.");
        }

        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw Errors.Input("GUIDED_SESSION_PATH_INVALID", "The guided session path has no parent directory.", "Provide a complete session path.");
        }

        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.s2modkit-staging");
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonDefaults.SerializeToUtf8(session), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string NormalizeGuidedPath(string path, string code)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new S2ModKitException(
                new S2Error(code, "input", "A guided workflow path is invalid.", "Provide a valid absolute or workspace-relative path.", ErrorCategory.InputOrResolution),
                exception);
        }
    }

    private static string FormatSourceKind(string kind) => kind switch
    {
        GuidedWorkflowContract.BaseVpkSource => "base VPK",
        GuidedWorkflowContract.ModVpkSource => "mod VPK",
        _ => "compiled model",
    };

    private static string FormatResourceRole(string role) => role.Replace('_', ' ');

    private sealed record GuidedActionParameters(
        float? UniformScale,
        float? TranslationX,
        float? TranslationY,
        float? TranslationZ,
        float? MaximumVertexDisplacement,
        float? MaximumCollisionDisplacement);

    private readonly record struct GuidedNumberResult(bool Canceled, float Value);
}
