using System.CommandLine;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using S2ModKit.Adapters.Source2;
using S2ModKit.Adapters.Vpk;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;
using S2ModKit.Reporting;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private const long MaximumRecipeBytes = 1024 * 1024;
    private readonly IS2ModKitApplication application;
    private readonly IVpkPackagingApplication? packagingApplication;
    private readonly IAddonManagementApplication? addonApplication;
    private readonly IResourceCatalogInventoryFactory? catalogueInventoryFactory;
    private readonly ICompatibilityScanner? compatibilityScanner;
    private readonly ICompatibilityReportPublisher? compatibilityReportPublisher;
    private readonly string adapterName;
    private readonly string adapterVersion;
    private readonly string productVersion;
    private readonly string runtimeIdentifier;
    private readonly string catalogueRevision;
    private readonly string externalVerifierStatus;
    private readonly string geometryCodecStatus;

    public S2ModKitCli(
        IS2ModKitApplication application,
        string adapterName,
        string adapterVersion,
        bool externalVerifierAvailable,
        string geometryCodecStatus = "not_configured",
        IResourceCatalogInventoryFactory? catalogueInventoryFactory = null,
        ICompatibilityScanner? compatibilityScanner = null,
        ICompatibilityReportPublisher? compatibilityReportPublisher = null)
    {
        this.application = application ?? throw new ArgumentNullException(nameof(application));
        this.adapterName = string.IsNullOrWhiteSpace(adapterName) ? throw new ArgumentException("Adapter name is required.", nameof(adapterName)) : adapterName;
        this.adapterVersion = string.IsNullOrWhiteSpace(adapterVersion) ? throw new ArgumentException("Adapter version is required.", nameof(adapterVersion)) : adapterVersion;
        productVersion = typeof(S2ModKitCli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        catalogueRevision = ReadPackagedCatalogueRevision();
        externalVerifierStatus = externalVerifierAvailable ? "available" : "skipped";
        this.geometryCodecStatus = string.IsNullOrWhiteSpace(geometryCodecStatus) ? "not_configured" : geometryCodecStatus;
        this.catalogueInventoryFactory = catalogueInventoryFactory;
        this.compatibilityScanner = compatibilityScanner;
        this.compatibilityReportPublisher = compatibilityReportPublisher;
    }

    public S2ModKitCli(
        IS2ModKitApplication application,
        IVpkPackagingApplication packagingApplication,
        string adapterName,
        string adapterVersion,
        bool externalVerifierAvailable,
        string geometryCodecStatus = "not_configured",
        IResourceCatalogInventoryFactory? catalogueInventoryFactory = null,
        ICompatibilityScanner? compatibilityScanner = null,
        ICompatibilityReportPublisher? compatibilityReportPublisher = null)
        : this(application, adapterName, adapterVersion, externalVerifierAvailable, geometryCodecStatus, catalogueInventoryFactory, compatibilityScanner, compatibilityReportPublisher)
    {
        this.packagingApplication = packagingApplication ?? throw new ArgumentNullException(nameof(packagingApplication));
    }

    public S2ModKitCli(
        IS2ModKitApplication application,
        IVpkPackagingApplication packagingApplication,
        IAddonManagementApplication addonApplication,
        string adapterName,
        string adapterVersion,
        bool externalVerifierAvailable,
        string geometryCodecStatus = "not_configured",
        IResourceCatalogInventoryFactory? catalogueInventoryFactory = null,
        ICompatibilityScanner? compatibilityScanner = null,
        ICompatibilityReportPublisher? compatibilityReportPublisher = null)
        : this(application, packagingApplication, adapterName, adapterVersion, externalVerifierAvailable, geometryCodecStatus, catalogueInventoryFactory, compatibilityScanner, compatibilityReportPublisher)
    {
        this.addonApplication = addonApplication ?? throw new ArgumentNullException(nameof(addonApplication));
    }

    private S2ModKitCli(
        IS2ModKitApplication application,
        IVpkPackagingApplication packagingApplication,
        IAddonManagementApplication addonApplication,
        string adapterName,
        string adapterVersion,
        string externalVerifierStatus,
        string geometryCodecStatus,
        IResourceCatalogInventoryFactory catalogueInventoryFactory,
        ICompatibilityScanner compatibilityScanner,
        ICompatibilityReportPublisher compatibilityReportPublisher)
    {
        this.application = application;
        this.packagingApplication = packagingApplication;
        this.addonApplication = addonApplication;
        this.adapterName = adapterName;
        this.adapterVersion = adapterVersion;
        productVersion = typeof(S2ModKitCli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        catalogueRevision = ReadPackagedCatalogueRevision();
        this.externalVerifierStatus = externalVerifierStatus;
        this.geometryCodecStatus = geometryCodecStatus;
        this.catalogueInventoryFactory = catalogueInventoryFactory;
        this.compatibilityScanner = compatibilityScanner;
        this.compatibilityReportPublisher = compatibilityReportPublisher;
    }

    public static S2ModKitCli CreateDefault()
    {
        var workspace = new FileSystemProjectWorkspace();
        var adapter = new Source2CompiledModelAdapter(Environment.GetEnvironmentVariable("S2MODKIT_MESHOPTIMIZER_PATH"));
        var (externalVerifier, externalVerifierStatus) = CreateExternalVerifier();
        var vpkVerifier = CreateVpkExternalVerifier();
        var reports = new EvidenceReportRenderer();
        var application = new S2ModKitApplication(
            workspace,
            adapter,
            adapter,
            adapter,
            externalVerifier,
            reports,
            new SystemClock(),
            new DirectoryProjectResourceSourceFactory(),
            new VpkProjectResourceSourceFactory(),
            adapter,
            adapter,
            new AtomicRecipeDocumentWriter());
        var packaging = new VpkPackagingApplication(
            workspace,
            workspace,
            new DeterministicVpkCandidateBuilder(),
            vpkVerifier,
            new AtomicVpkPackageExporter(),
            reports,
            new SystemClock());
        var addons = new AddonManagementApplication(
            workspace,
            workspace,
            workspace,
            new AddonFileSystem(),
            new ValvePakAddonArchiveInspector(),
            reports,
            new SystemClock());
        return new S2ModKitCli(
            application,
            packaging,
            addons,
            adapter.AdapterName,
            adapter.AdapterVersion,
            externalVerifierStatus,
            adapter.GeometryCodecCapability.Status,
            new VpkResourceCatalogFactory(),
            new CompatibilityScanner(adapter, adapter),
            new FileSystemCompatibilityReportPublisher());
    }

    private static (IExternalVerifier Verifier, string Status) CreateExternalVerifier()
    {
        var executablePath = Environment.GetEnvironmentVariable("S2MODKIT_SOURCE2_VIEWER_PATH");
        var scratchRoot = Environment.GetEnvironmentVariable("S2MODKIT_EXTERNAL_VERIFY_ROOT");
        if (string.IsNullOrWhiteSpace(executablePath) && string.IsNullOrWhiteSpace(scratchRoot))
        {
            return (new SkippedExternalVerifier(), "skipped");
        }

        if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(scratchRoot) || !File.Exists(executablePath))
        {
            return (new MisconfiguredExternalVerifier("Source 2 Viewer configuration is incomplete or its executable does not exist."), "misconfigured");
        }

        try
        {
            return (new Source2ViewerExternalVerifier(executablePath, scratchRoot), "available");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (new MisconfiguredExternalVerifier("Source 2 Viewer paths are invalid for this host."), "misconfigured");
        }
    }

    private static IVpkExternalVerifier CreateVpkExternalVerifier()
    {
        var executablePath = Environment.GetEnvironmentVariable("S2MODKIT_SOURCE2_VIEWER_PATH");
        var scratchRoot = Environment.GetEnvironmentVariable("S2MODKIT_EXTERNAL_VERIFY_ROOT");
        if (string.IsNullOrWhiteSpace(executablePath) && string.IsNullOrWhiteSpace(scratchRoot))
        {
            return new SkippedVpkExternalVerifier();
        }

        if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(scratchRoot) || !File.Exists(executablePath))
        {
            return new MisconfiguredVpkExternalVerifier("Source 2 Viewer configuration is incomplete or its executable does not exist.");
        }

        try
        {
            return new Source2ViewerVpkVerifier(executablePath, scratchRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new MisconfiguredVpkExternalVerifier("Source 2 Viewer paths are invalid for this host.");
        }
    }

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
        => await RunAsync(arguments, Console.In, output, error, cancellationToken).ConfigureAwait(false);

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        var root = CreateRootCommand(input, output, error);
        ParseResult parseResult;
        try
        {
            parseResult = root.Parse(arguments);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            var parseError = new S2Error("CLI_PARSE_ERROR", "cli", exception.Message, "Run s2mod --help and correct the command line.", ErrorCategory.CliOrSchema);
            WriteFailure(InferCommand(arguments), parseError, UsesJsonOutput(arguments), output, error);
            return (int)ErrorCategory.CliOrSchema;
        }

        if (parseResult.Errors.Count > 0)
        {
            var summary = string.Join(" ", parseResult.Errors.Select(item => item.Message));
            var parseError = new S2Error("CLI_PARSE_ERROR", "cli", summary, "Run s2mod --help and correct the command line.", ErrorCategory.CliOrSchema);
            WriteFailure(InferCommand(arguments), parseError, UsesJsonOutput(arguments), output, error);
            return (int)ErrorCategory.CliOrSchema;
        }

        var configuration = new InvocationConfiguration
        {
            Output = output,
            Error = error,
            EnableDefaultExceptionHandler = false,
        };
        try
        {
            return await parseResult.InvokeAsync(configuration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var canceled = new S2Error("OPERATION_CANCELED", "cli", "The command was canceled before completion.", "Run the command again; no unverified build was intentionally published.", ErrorCategory.Unexpected);
            WriteFailure(InferCommand(arguments), canceled, UsesJsonOutput(arguments), output, error);
            return (int)ErrorCategory.Unexpected;
        }
    }

    private RootCommand CreateRootCommand(TextReader input, TextWriter output, TextWriter error)
    {
        var root = new RootCommand("Deterministic Source 2 compiled-model mod workbench");
        root.Subcommands.Add(CreateDoctorCommand(output, error));
        root.Subcommands.Add(CreateInteractiveCommand(input, output, error));
        root.Subcommands.Add(CreateCatalogueCommand(output, error));
        root.Subcommands.Add(CreateCompatibilityCommand(output, error));
        root.Subcommands.Add(CreateProjectCommand(output, error));
        root.Subcommands.Add(CreateInspectCommand(output, error));
        root.Subcommands.Add(CreateComponentsCommand(output, error));
        root.Subcommands.Add(CreateRecipeCommand(output, error));
        root.Subcommands.Add(CreatePlanCommand(output, error));
        root.Subcommands.Add(CreateBuildCommand(output, error));
        root.Subcommands.Add(CreateVerifyCommand(output, error));
        root.Subcommands.Add(CreatePackageCommand(output, error));
        root.Subcommands.Add(CreateAddonsCommand(output, error));
        root.Subcommands.Add(CreateRuntimeCommand(output, error));
        return root;
    }

    private Command CreateInteractiveCommand(TextReader input, TextWriter output, TextWriter error)
    {
        var command = new Command("interactive", "Start or resume the guided terminal workflow.");
        var catalogue = new Option<string>("--catalogue")
        {
            Description = "Versioned hero catalogue JSON file; prompted when omitted.",
        };
        var session = new Option<string>("--session")
        {
            Description = "Guided session JSON checkpoint; prompted when omitted.",
        };
        var baseVpk = new Option<string[]>("--base-vpk")
        {
            Description = "Configured read-only base *_dir.vpk source; repeat as needed.",
        };
        var modVpk = new Option<string[]>("--mod-vpk")
        {
            Description = "Configured read-only mod *_dir.vpk source; repeat as needed.",
        };
        var compiledModel = new Option<string[]>("--compiled-model")
        {
            Description = "Configured compiled .vmdl_c source; repeat as needed.",
        };
        var resume = new Option<bool>("--resume")
        {
            Description = "Resume the existing checkpoint instead of creating a new session.",
        };
        var expert = new Option<bool>("--expert")
        {
            Description = "Show stable IDs, paths, and verification details.",
        };
        command.Options.Add(catalogue);
        command.Options.Add(session);
        command.Options.Add(baseVpk);
        command.Options.Add(modVpk);
        command.Options.Add(compiledModel);
        command.Options.Add(resume);
        command.Options.Add(expert);
        command.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "interactive",
            json: false,
            output,
            error,
            () => RunInteractiveEntryAsync(
                parseResult.GetValue(catalogue),
                parseResult.GetValue(session),
                parseResult.GetValue(baseVpk) ?? [],
                parseResult.GetValue(modVpk) ?? [],
                parseResult.GetValue(compiledModel) ?? [],
                parseResult.GetValue(resume),
                parseResult.GetValue(expert),
                input,
                output,
                cancellationToken),
            renderText: null,
            cancellationToken));
        return command;
    }

    private Command CreateCatalogueCommand(TextWriter output, TextWriter error)
    {
        var catalogue = new Command("catalog", "List and resolve verified Deadlock hero resources.");
        var heroes = new Command("heroes", "Browse heroes in a versioned catalogue.");
        var list = new Command("list", "List heroes and resources verified against a base VPK.");
        var listCatalogue = RequiredStringOption("--catalogue", "Versioned hero catalogue JSON file.");
        var listSourceVpk = RequiredStringOption("--source-vpk", "Read-only Deadlock base *_dir.vpk archive.");
        var listFormat = CreateFormatOption();
        list.Options.Add(listCatalogue);
        list.Options.Add(listSourceVpk);
        list.Options.Add(listFormat);
        list.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "catalog.heroes.list",
            IsJson(parseResult.GetRequiredValue(listFormat)),
            output,
            error,
            () => ListCatalogueAsync(
                parseResult.GetRequiredValue(listCatalogue),
                parseResult.GetRequiredValue(listSourceVpk),
                cancellationToken),
            RenderCatalogue,
            cancellationToken));
        heroes.Subcommands.Add(list);

        var resolve = new Command("resolve", "Resolve one hero name, ID, or alias to verified resources.");
        var resolveCatalogue = RequiredStringOption("--catalogue", "Versioned hero catalogue JSON file.");
        var resolveSourceVpk = RequiredStringOption("--source-vpk", "Read-only Deadlock base *_dir.vpk archive.");
        var hero = RequiredStringOption("--hero", "Hero ID, display name, or alias.");
        var resolveFormat = CreateFormatOption();
        resolve.Options.Add(resolveCatalogue);
        resolve.Options.Add(resolveSourceVpk);
        resolve.Options.Add(hero);
        resolve.Options.Add(resolveFormat);
        resolve.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "catalog.resolve",
            IsJson(parseResult.GetRequiredValue(resolveFormat)),
            output,
            error,
            () => ResolveCatalogueAsync(
                parseResult.GetRequiredValue(resolveCatalogue),
                parseResult.GetRequiredValue(resolveSourceVpk),
                parseResult.GetRequiredValue(hero),
                cancellationToken),
            RenderCatalogueResolution,
            cancellationToken));

        catalogue.Subcommands.Add(heroes);
        catalogue.Subcommands.Add(resolve);
        return catalogue;
    }

    private Command CreateCompatibilityCommand(TextWriter output, TextWriter error)
    {
        var compatibility = new Command("compatibility", "Scan verified hero resources and cluster capability gaps.");
        var scan = new Command("scan", "Publish a read-only compatibility scan and concise report.");
        var catalogue = RequiredStringOption("--catalogue", "Versioned hero catalogue JSON file.");
        var sourceVpk = RequiredStringOption("--source-vpk", "Read-only Deadlock base *_dir.vpk archive.");
        var outputRoot = RequiredStringOption("--output-root", "Configured root for immutable JSON and Markdown reports.");
        var compare = new Option<string?>("--compare")
        {
            Description = "Optional prior version-1 compatibility report JSON file.",
        };
        var format = CreateFormatOption();
        scan.Options.Add(catalogue);
        scan.Options.Add(sourceVpk);
        scan.Options.Add(outputRoot);
        scan.Options.Add(compare);
        scan.Options.Add(format);
        scan.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "compatibility.scan",
            IsJson(parseResult.GetRequiredValue(format)),
            output,
            error,
            () => RunCompatibilityScanAsync(
                parseResult.GetRequiredValue(catalogue),
                parseResult.GetRequiredValue(sourceVpk),
                parseResult.GetRequiredValue(outputRoot),
                parseResult.GetValue(compare),
                cancellationToken),
            RenderCompatibilityPublication,
            cancellationToken));
        compatibility.Subcommands.Add(scan);
        return compatibility;
    }

    private Command CreateComponentsCommand(TextWriter output, TextWriter error)
    {
        var command = new Command("components", "Discover deterministic mechanical model components.");
        var list = new Command("list", "List component candidates and existing-operation capabilities.");
        var project = RequiredStringOption("--project", "S2ModKit project root.");
        var format = CreateFormatOption();
        list.Options.Add(project);
        list.Options.Add(format);
        list.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "components.list",
            IsJson(parseResult.GetRequiredValue(format)),
            output,
            error,
            () => application.DiscoverComponentsAsync(
                parseResult.GetRequiredValue(project),
                cancellationToken),
            RenderComponents,
            cancellationToken));
        command.Subcommands.Add(list);
        return command;
    }

    private Command CreateRecipeCommand(TextWriter output, TextWriter error)
    {
        var command = new Command("recipe", "Create canonical recipes for existing S2ModKit operations.");
        var scaffold = new Command("scaffold", "Scaffold a recipe from current component candidate IDs.");
        var project = RequiredStringOption("--project", "S2ModKit project root.");
        var component = new Option<string[]>("--component")
        {
            Description = "Current candidate ID from components list; repeat for an explicit union.",
            Required = true,
        };
        var intent = new Option<string>("--intent")
        {
            Description = "Operation intent: remove, uniform-scale, translate, or affine.",
            Required = true,
        };
        intent.AcceptOnlyFromAmong(
            RecipeScaffoldContract.RemoveIntent,
            RecipeScaffoldContract.UniformScaleIntent,
            RecipeScaffoldContract.TranslateIntent,
            RecipeScaffoldContract.AffineIntent);
        var outputPath = RequiredStringOption("--output", "New recipe JSON path; existing files are never overwritten.");
        var scale = new Option<string?>("--scale")
        {
            Description = "Positive uniform scale for uniform-scale intent.",
        };
        var translateX = new Option<string?>("--translate-x")
        {
            Description = "Optional Source-unit X translation; at least one axis is required for translate.",
        };
        var translateY = new Option<string?>("--translate-y")
        {
            Description = "Optional Source-unit Y translation; at least one axis is required for translate.",
        };
        var translateZ = new Option<string?>("--translate-z")
        {
            Description = "Optional Source-unit Z translation; at least one axis is required for translate.",
        };
        var referenceLod = new Option<string?>("--reference-lod")
        {
            Description = "Pivot reference LOD; defaults to the lowest present selected LOD.",
        };
        var maximumDisplacement = new Option<string?>("--max-displacement")
        {
            Description = "Required transform safety cap in Source units, greater than zero and at most 256.",
        };
        var maximumCollisionDisplacement = new Option<string?>("--max-collision-displacement")
        {
            Description = "Required collision safety cap for a discovered transform_component@2 profile.",
        };
        var scaleX = new Option<string?>("--scale-x") { Description = "Affine X scale in [0.25, 4.0]; defaults to 1." };
        var scaleY = new Option<string?>("--scale-y") { Description = "Affine Y scale in [0.25, 4.0]; defaults to 1." };
        var scaleZ = new Option<string?>("--scale-z") { Description = "Affine Z scale in [0.25, 4.0]; defaults to 1." };
        var rotateAxis = new Option<string?>("--rotate-axis") { Description = "Unit rotation axis as x,y,z in the selected frame." };
        var rotateDegrees = new Option<string?>("--rotate-degrees") { Description = "Signed rotation degrees in (-180, 180]." };
        var pivot = new Option<string?>("--pivot") { Description = "Affine anchor: selection-center, point, face, or bone." };
        var pivotPoint = new Option<string?>("--pivot-point") { Description = "Explicit model-space anchor as x,y,z." };
        var pivotFace = new Option<string?>("--pivot-face") { Description = "Named bounds face: min_x, max_x, min_y, max_y, min_z, or max_z." };
        var pivotBone = new Option<string?>("--pivot-bone") { Description = "Exact influencing bone name for a bone-origin anchor." };
        var frame = new Option<string?>("--frame") { Description = "Affine axes: model or bone-bind." };
        var frameBone = new Option<string?>("--frame-bone") { Description = "Exact influencing bone name for bone-bind axes." };
        foreach (var option in new Option[]
                 {
                     project,
                     component,
                     intent,
                     outputPath,
                     scale,
                     translateX,
                     translateY,
                     translateZ,
                     referenceLod,
                     maximumDisplacement,
                     maximumCollisionDisplacement,
                     scaleX,
                     scaleY,
                     scaleZ,
                     rotateAxis,
                     rotateDegrees,
                     pivot,
                     pivotPoint,
                     pivotFace,
                     pivotBone,
                     frame,
                     frameBone,
                 })
        {
            scaffold.Options.Add(option);
        }

        scaffold.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "recipe.scaffold",
            json: true,
            output,
            error,
            () => application.ScaffoldRecipeAsync(
                parseResult.GetRequiredValue(project),
                new RecipeScaffoldRequest(
                    parseResult.GetValue(component) ?? [],
                    parseResult.GetRequiredValue(intent),
                    parseResult.GetRequiredValue(outputPath),
                    ParseOptionalSingle(parseResult.GetValue(scale), "--scale"),
                    ParseOptionalSingle(parseResult.GetValue(translateX), "--translate-x"),
                    ParseOptionalSingle(parseResult.GetValue(translateY), "--translate-y"),
                    ParseOptionalSingle(parseResult.GetValue(translateZ), "--translate-z"),
                    ParseOptionalNonNegativeInt32(parseResult.GetValue(referenceLod), "--reference-lod"),
                    ParseOptionalSingle(parseResult.GetValue(maximumDisplacement), "--max-displacement"),
                    ParseOptionalSingle(parseResult.GetValue(maximumCollisionDisplacement), "--max-collision-displacement"),
                    ParseAffineScaffoldOptions(
                        parseResult.GetRequiredValue(intent),
                        new AffineScaffoldCliOptions(
                            parseResult.GetValue(scaleX),
                            parseResult.GetValue(scaleY),
                            parseResult.GetValue(scaleZ),
                            parseResult.GetValue(rotateAxis),
                            parseResult.GetValue(rotateDegrees),
                            parseResult.GetValue(pivot),
                            parseResult.GetValue(pivotPoint),
                            parseResult.GetValue(pivotFace),
                            parseResult.GetValue(pivotBone),
                            parseResult.GetValue(frame),
                            parseResult.GetValue(frameBone)))),
                cancellationToken),
            renderText: null,
            cancellationToken));
        command.Subcommands.Add(scaffold);
        return command;
    }

    private Command CreateAddonsCommand(TextWriter output, TextWriter error)
    {
        var command = new Command("addons", "Inventory and manage one receipt-backed S2ModKit test installation.");

        var inventory = new Command("inventory", "Inspect active addon archives for collisions with a package.");
        var inventoryRoot = RequiredStringOption("--addons-root", "Deadlock addons directory or an isolated test root.");
        var inventoryProject = RequiredStringOption("--project", "S2ModKit project root.");
        var inventoryPackage = RequiredStringOption("--package", "Published package id.");
        inventory.Options.Add(inventoryRoot);
        inventory.Options.Add(inventoryProject);
        inventory.Options.Add(inventoryPackage);
        inventory.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "addons.inventory", true, output, error,
            () => RequireAddonApplication().InventoryAsync(
                parseResult.GetRequiredValue(inventoryRoot),
                parseResult.GetRequiredValue(inventoryProject),
                parseResult.GetRequiredValue(inventoryPackage),
                cancellationToken),
            renderText: null,
            cancellationToken));

        var install = new Command("install", "Install an exact package into a safe empty addon slot.");
        var installRoot = RequiredStringOption("--addons-root", "Deadlock addons directory or an isolated test root.");
        var installProject = RequiredStringOption("--project", "S2ModKit project root.");
        var installPackage = RequiredStringOption("--package", "Published package id.");
        var installSlot = new Option<string>("--slot") { Description = "Automatic slot 90-99 or an explicit two-digit slot.", DefaultValueFactory = _ => "auto" };
        install.Options.Add(installRoot);
        install.Options.Add(installProject);
        install.Options.Add(installPackage);
        install.Options.Add(installSlot);
        install.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "addons.install", true, output, error,
            () => RequireAddonApplication().InstallAsync(
                parseResult.GetRequiredValue(installRoot),
                parseResult.GetRequiredValue(installProject),
                parseResult.GetRequiredValue(installPackage),
                parseResult.GetRequiredValue(installSlot),
                cancellationToken),
            renderText: null,
            cancellationToken));

        var verify = new Command("verify-active", "Verify the exact receipt-backed package and load-order winner.");
        var verifyRoot = RequiredStringOption("--addons-root", "Deadlock addons directory or an isolated test root.");
        var verifyProject = RequiredStringOption("--project", "S2ModKit project root.");
        var verifyInstallation = RequiredStringOption("--installation", "Installation receipt id.");
        verify.Options.Add(verifyRoot);
        verify.Options.Add(verifyProject);
        verify.Options.Add(verifyInstallation);
        verify.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "addons.verify-active", true, output, error,
            () => RequireAddonApplication().VerifyActiveAsync(
                parseResult.GetRequiredValue(verifyRoot),
                parseResult.GetRequiredValue(verifyProject),
                parseResult.GetRequiredValue(verifyInstallation),
                cancellationToken),
            renderText: null,
            cancellationToken));

        var rollback = new Command("rollback", "Disable only the exact archive named by an installation receipt.");
        var rollbackRoot = RequiredStringOption("--addons-root", "Deadlock addons directory or an isolated test root.");
        var rollbackProject = RequiredStringOption("--project", "S2ModKit project root.");
        var rollbackInstallation = RequiredStringOption("--installation", "Installation receipt id.");
        rollback.Options.Add(rollbackRoot);
        rollback.Options.Add(rollbackProject);
        rollback.Options.Add(rollbackInstallation);
        rollback.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "addons.rollback", true, output, error,
            () => RequireAddonApplication().RollbackAsync(
                parseResult.GetRequiredValue(rollbackRoot),
                parseResult.GetRequiredValue(rollbackProject),
                parseResult.GetRequiredValue(rollbackInstallation),
                cancellationToken),
            renderText: null,
            cancellationToken));

        command.Subcommands.Add(inventory);
        command.Subcommands.Add(install);
        command.Subcommands.Add(verify);
        command.Subcommands.Add(rollback);
        return command;
    }

    private Command CreateRuntimeCommand(TextWriter output, TextWriter error)
    {
        var runtime = new Command("runtime", "Record player-observed evidence for an exact active installation.");
        var record = new Command("record", "Publish an immutable player observation after active-install verification.");
        var project = RequiredStringOption("--project", "S2ModKit project root.");
        var installation = RequiredStringOption("--installation", "Installation receipt id.");
        var observation = RequiredStringOption("--observation", "Runtime observation input JSON.");
        record.Options.Add(project);
        record.Options.Add(installation);
        record.Options.Add(observation);
        record.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "runtime.record", true, output, error,
            async () => await RequireAddonApplication().RecordRuntimeAsync(
                parseResult.GetRequiredValue(project),
                parseResult.GetRequiredValue(installation),
                await ReadObservationAsync(parseResult.GetRequiredValue(observation), cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false),
            renderText: null,
            cancellationToken));
        runtime.Subcommands.Add(record);
        return runtime;
    }

    private Command CreatePackageCommand(TextWriter output, TextWriter error)
    {
        var command = new Command("package", "Create and verify deterministic VPK runtime candidates.");

        var create = new Command("create", "Replace exactly one compiled-model entry in an immutable source VPK.");
        var createProject = RequiredStringOption("--project", "S2ModKit project root.");
        var createBuild = RequiredStringOption("--build", "Verified model build id.");
        var sourceVpk = RequiredStringOption("--source-vpk", "Immutable source mod VPK.");
        var sourceHash = RequiredStringOption("--source-vpk-sha256", "Expected lower-case or upper-case source VPK SHA-256.");
        var requireExternal = new Option<bool>("--require-external") { Description = "Require Source 2 Viewer verification before publication." };
        create.Options.Add(createProject);
        create.Options.Add(createBuild);
        create.Options.Add(sourceVpk);
        create.Options.Add(sourceHash);
        create.Options.Add(requireExternal);
        create.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "package.create",
            json: true,
            output,
            error,
            () => RequirePackagingApplication().CreateAsync(
                parseResult.GetRequiredValue(createProject),
                parseResult.GetRequiredValue(createBuild),
                parseResult.GetRequiredValue(sourceVpk),
                ParseContentHash(parseResult.GetRequiredValue(sourceHash)),
                parseResult.GetValue(requireExternal),
                cancellationToken),
            renderText: null,
            cancellationToken));

        var createMinimal = new Command("create-minimal", "Create a deterministic one-entry VPK from a verified model build.");
        var minimalProject = RequiredStringOption("--project", "S2ModKit project root.");
        var minimalBuild = RequiredStringOption("--build", "Verified model build id.");
        var minimalRequireExternal = new Option<bool>("--require-external") { Description = "Require Source 2 Viewer verification before publication." };
        createMinimal.Options.Add(minimalProject);
        createMinimal.Options.Add(minimalBuild);
        createMinimal.Options.Add(minimalRequireExternal);
        createMinimal.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "package.create-minimal",
            json: true,
            output,
            error,
            () => RequirePackagingApplication().CreateMinimalAsync(
                parseResult.GetRequiredValue(minimalProject),
                parseResult.GetRequiredValue(minimalBuild),
                parseResult.GetValue(minimalRequireExternal),
                cancellationToken),
            renderText: null,
            cancellationToken));

        var verify = new Command("verify", "Reopen and verify an existing minimal or replace-source package.");
        var verifyProject = RequiredStringOption("--project", "S2ModKit project root.");
        var packageId = RequiredStringOption("--package", "Published package id.");
        var verifyRequireExternal = new Option<bool>("--require-external") { Description = "Require Source 2 Viewer verification." };
        verify.Options.Add(verifyProject);
        verify.Options.Add(packageId);
        verify.Options.Add(verifyRequireExternal);
        verify.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "package.verify",
            json: true,
            output,
            error,
            () => RequirePackagingApplication().VerifyAsync(
                parseResult.GetRequiredValue(verifyProject),
                parseResult.GetRequiredValue(packageId),
                parseResult.GetValue(verifyRequireExternal),
                cancellationToken),
            renderText: null,
            cancellationToken));

        command.Subcommands.Add(create);
        command.Subcommands.Add(createMinimal);
        command.Subcommands.Add(verify);
        return command;
    }

    private Command CreateDoctorCommand(TextWriter output, TextWriter error)
    {
        var format = CreateFormatOption();
        var command = new Command("doctor", "Check the configured adapters and verifier availability.");
        command.Options.Add(format);
        command.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "doctor",
            IsJson(parseResult.GetRequiredValue(format)),
            output,
            error,
            () => Task.FromResult(new DoctorResult(
                "ready",
                productVersion,
                runtimeIdentifier,
                catalogueRevision,
                adapterName,
                adapterVersion,
                externalVerifierStatus,
                geometryCodecStatus,
                "offline_static")),
            RenderDoctor,
            cancellationToken));
        return command;
    }

    private Command CreateProjectCommand(TextWriter output, TextWriter error)
    {
        var project = new Command("project", "Create and manage immutable S2ModKit projects.");
        var create = new Command("create", "Import a compiled model into a content-addressed workspace.");
        var root = RequiredStringOption("--root", "Project workspace root.");
        var input = RequiredStringOption("--input", "Input .vmdl_c path.");
        var resourceRoot = RequiredStringOption("--resource-root", "Filesystem root for resource resolution.");
        var runtimeResourceRoot = new Option<string?>("--runtime-resource-root")
        {
            Description = "Optional extracted base-game root for dependencies that are not owned by the mod.",
        };
        create.Options.Add(root);
        create.Options.Add(input);
        create.Options.Add(resourceRoot);
        create.Options.Add(runtimeResourceRoot);
        create.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "project.create",
            json: true,
            output,
            error,
            () => string.IsNullOrWhiteSpace(parseResult.GetValue(runtimeResourceRoot))
                ? application.CreateProjectAsync(
                    parseResult.GetRequiredValue(root),
                    parseResult.GetRequiredValue(input),
                    parseResult.GetRequiredValue(resourceRoot),
                    cancellationToken)
                : application.CreateProjectAsync(
                    parseResult.GetRequiredValue(root),
                    parseResult.GetRequiredValue(input),
                    parseResult.GetRequiredValue(resourceRoot),
                    [parseResult.GetValue(runtimeResourceRoot)!],
                    cancellationToken),
            renderText: null,
            cancellationToken));
        project.Subcommands.Add(create);

        var createVpk = new Command("create-vpk", "Import one vanilla compiled model directly from a split base-game VPK.");
        var vpkRoot = RequiredStringOption("--root", "Project workspace root.");
        var baseVpk = RequiredStringOption("--base-vpk", "Read-only base-game *_dir.vpk path.");
        var entry = RequiredStringOption("--entry", "Compiled-resource logical path inside the VPK.");
        var expectedDirectoryHash = new Option<string?>("--expect-directory-sha256")
        {
            Description = "Optional expected SHA-256 of the small *_dir.vpk file.",
        };
        createVpk.Options.Add(vpkRoot);
        createVpk.Options.Add(baseVpk);
        createVpk.Options.Add(entry);
        createVpk.Options.Add(expectedDirectoryHash);
        createVpk.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "project.create-vpk",
            json: true,
            output,
            error,
            () => application.CreateVpkProjectAsync(
                parseResult.GetRequiredValue(vpkRoot),
                parseResult.GetRequiredValue(baseVpk),
                parseResult.GetRequiredValue(entry),
                string.IsNullOrWhiteSpace(parseResult.GetValue(expectedDirectoryHash))
                    ? null
                    : ParseContentHash(parseResult.GetValue(expectedDirectoryHash)!),
                cancellationToken),
            renderText: null,
            cancellationToken));
        project.Subcommands.Add(createVpk);
        return project;
    }

    private Command CreateInspectCommand(TextWriter output, TextWriter error)
    {
        var project = RequiredStringOption("--project", "S2ModKit project root.");
        var format = CreateFormatOption();
        var command = new Command("inspect", "Inventory resource blocks, LODs, meshes, and draw calls.");
        command.Options.Add(project);
        command.Options.Add(format);
        command.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "inspect",
            IsJson(parseResult.GetRequiredValue(format)),
            output,
            error,
            () => application.InspectAsync(parseResult.GetRequiredValue(project), cancellationToken),
            RenderInspection,
            cancellationToken));
        return command;
    }

    private Command CreatePlanCommand(TextWriter output, TextWriter error)
    {
        var project = RequiredStringOption("--project", "S2ModKit project root.");
        var recipe = RequiredStringOption("--recipe", "Path to a recipe.schema.json document.");
        var format = CreateFormatOption();
        var command = new Command("plan", "Resolve a recipe without producing model bytes.");
        command.Options.Add(project);
        command.Options.Add(recipe);
        command.Options.Add(format);
        command.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "plan",
            IsJson(parseResult.GetRequiredValue(format)),
            output,
            error,
            async () => await application.PlanAsync(
                parseResult.GetRequiredValue(project),
                await ReadRecipeAsync(parseResult.GetRequiredValue(recipe), cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false),
            RenderPlan,
            cancellationToken));
        return command;
    }

    private Command CreateBuildCommand(TextWriter output, TextWriter error)
    {
        var project = RequiredStringOption("--project", "S2ModKit project root.");
        var recipe = RequiredStringOption("--recipe", "Path to a recipe.schema.json document.");
        var command = new Command("build", "Rewrite, reopen, verify, and atomically publish a build.");
        command.Options.Add(project);
        command.Options.Add(recipe);
        command.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "build",
            json: true,
            output,
            error,
            async () => await application.BuildAsync(
                parseResult.GetRequiredValue(project),
                await ReadRecipeAsync(parseResult.GetRequiredValue(recipe), cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false),
            renderText: null,
            cancellationToken));
        return command;
    }

    private Command CreateVerifyCommand(TextWriter output, TextWriter error)
    {
        var project = RequiredStringOption("--project", "S2ModKit project root.");
        var build = RequiredStringOption("--build", "Previously published build id.");
        var command = new Command("verify", "Reopen and independently verify an existing build.");
        command.Options.Add(project);
        command.Options.Add(build);
        command.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "verify",
            json: true,
            output,
            error,
            () => application.VerifyAsync(parseResult.GetRequiredValue(project), parseResult.GetRequiredValue(build), cancellationToken),
            renderText: null,
            cancellationToken));
        return command;
    }

    private static async Task<int> ExecuteAsync<T>(
        string command,
        bool json,
        TextWriter output,
        TextWriter error,
        Func<Task<T>> action,
        Action<TextWriter, T>? renderText,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await action().ConfigureAwait(false);
            if (json)
            {
                output.WriteLine(JsonDefaults.Serialize(new CliSuccess<T>(command, result)));
            }
            else
            {
                renderText?.Invoke(output, result);
            }

            return 0;
        }
        catch (S2ModKitException exception)
        {
            WriteFailure(command, exception.Error, json, output, error);
            return (int)exception.Error.Category;
        }
        catch (IOException exception)
        {
            var ioError = new S2Error("FILE_IO_FAILED", "input", exception.Message, "Check the supplied paths and retry without modifying the original input.", ErrorCategory.InputOrResolution);
            WriteFailure(command, ioError, json, output, error);
            return (int)ErrorCategory.InputOrResolution;
        }
        catch (UnauthorizedAccessException exception)
        {
            var accessError = new S2Error("FILE_ACCESS_DENIED", "input", exception.Message, "Choose readable inputs and a writable configured workspace.", ErrorCategory.InputOrResolution);
            WriteFailure(command, accessError, json, output, error);
            return (int)ErrorCategory.InputOrResolution;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var unexpected = new S2Error("UNEXPECTED_INTERNAL_ERROR", "internal", "An unexpected internal error stopped the command.", "Preserve stderr diagnostics and report the failure; do not use any unverified output.", ErrorCategory.Unexpected);
            WriteFailure(command, unexpected, json, output, error);
            error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{exception.GetType().FullName}: {exception.Message}"));
            return (int)ErrorCategory.Unexpected;
        }
    }




    private static string ReadPackagedCatalogueRevision()
    {
        var path = CataloguePathResolver.TryFindPackagedCatalogue(AppContext.BaseDirectory);
        if (path is null)
        {
            return "not_packaged";
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.TryGetProperty("revision", out var revision)
                && revision.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(revision.GetString())
                ? revision.GetString()!
                : "invalid";
        }
        catch (JsonException)
        {
            return "invalid";
        }
        catch (IOException)
        {
            return "invalid";
        }
        catch (UnauthorizedAccessException)
        {
            return "invalid";
        }
    }

    private sealed record DoctorResult(
        string Status,
        string ProductVersion,
        string RuntimeIdentifier,
        string CatalogueRevision,
        string AdapterName,
        string AdapterVersion,
        string ExternalVerifier,
        string GeometryCodec,
        string ProofLevel);

    private sealed record CliSuccess<T>(int SchemaVersion, string Status, string Command, T Result)
    {
        public CliSuccess(string command, T result)
            : this(1, "success", command, result)
        {
        }
    }

    private sealed record CliFailure(int SchemaVersion, string Status, string Command, S2Error Error)
    {
        public CliFailure(string command, S2Error error)
            : this(1, "error", command, error)
        {
        }
    }
}
