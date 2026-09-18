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
    private async Task<HeroCatalogueVerification> ListCatalogueAsync(
        string cataloguePath,
        string sourceVpkPath,
        CancellationToken cancellationToken)
    {
        var catalogue = await ReadCatalogueAsync(cataloguePath, cancellationToken).ConfigureAwait(false);
        var inventory = RequireCatalogueInventoryFactory().OpenReadOnly(sourceVpkPath);
        try
        {
            return await HeroCatalogueQueries.ListAsync(catalogue, inventory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            (inventory as IDisposable)?.Dispose();
        }
    }

    private async Task<CompatibilityReportPublication> RunCompatibilityScanAsync(
        string cataloguePath,
        string sourceVpkPath,
        string outputRoot,
        string? comparePath,
        CancellationToken cancellationToken)
    {
        var catalogue = await ReadCatalogueAsync(cataloguePath, cancellationToken).ConfigureAwait(false);
        var prior = comparePath is null
            ? null
            : await ReadCompatibilityReportAsync(comparePath, cancellationToken).ConfigureAwait(false);
        var inventory = RequireCatalogueInventoryFactory().OpenReadOnly(sourceVpkPath);
        try
        {
            var scan = await RequireCompatibilityScanner()
                .ScanAsync(catalogue, inventory, cancellationToken)
                .ConfigureAwait(false);
            var report = CompatibilityReports.Create(scan, prior);
            var json = CompatibilityReports.RenderJson(report);
            var markdown = CompatibilityReports.RenderMarkdown(report);
            return await RequireCompatibilityReportPublisher()
                .PublishAsync(outputRoot, report, json, markdown, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            (inventory as IDisposable)?.Dispose();
        }
    }

    private async Task<HeroCatalogueResolution> ResolveCatalogueAsync(
        string cataloguePath,
        string sourceVpkPath,
        string hero,
        CancellationToken cancellationToken)
    {
        var catalogue = await ReadCatalogueAsync(cataloguePath, cancellationToken).ConfigureAwait(false);
        var inventory = RequireCatalogueInventoryFactory().OpenReadOnly(sourceVpkPath);
        try
        {
            return await HeroCatalogueQueries.ResolveAsync(catalogue, inventory, hero, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            (inventory as IDisposable)?.Dispose();
        }
    }

    private static async Task<HeroCatalogueDocument> ReadCatalogueAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw Errors.Input(
                "CATALOGUE_NOT_FOUND",
                $"Hero catalogue file '{fullPath}' does not exist.",
                "Provide an existing hero catalogue JSON file.");
        }

        var length = new FileInfo(fullPath).Length;
        if (length is < 2 or > MaximumRecipeBytes)
        {
            throw Errors.InvalidRecipe(
                "CATALOGUE_SIZE_UNSUPPORTED",
                $"Hero catalogue size {length} is outside the supported range.",
                "Provide a catalogue JSON document between 2 bytes and 1 MiB.");
        }

        var document = JsonDefaults.Deserialize<HeroCatalogueDocument>(
            await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false),
            "Hero catalogue");
        HeroCatalogueValidator.Validate(document);
        return document;
    }

    private static async Task<CompatibilityReport> ReadCompatibilityReportAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw Errors.Input(
                "COMPATIBILITY_PRIOR_NOT_FOUND",
                $"Prior compatibility report '{fullPath}' does not exist.",
                "Provide an existing version-1 compatibility report JSON file.");
        }

        var length = new FileInfo(fullPath).Length;
        if (length is < 2 or > 64L * 1024 * 1024)
        {
            throw Errors.Input(
                "COMPATIBILITY_PRIOR_SIZE_UNSUPPORTED",
                $"Prior compatibility report size {length} is outside the supported range.",
                "Provide a report between 2 bytes and 64 MiB.");
        }

        return JsonDefaults.Deserialize<CompatibilityReport>(
            await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false),
            "Prior compatibility report");
    }

    private static async Task<RecipeDocument> ReadRecipeAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw Errors.Input("RECIPE_NOT_FOUND", $"Recipe file '{fullPath}' does not exist.", "Provide an existing recipe JSON file.");
        }

        var length = new FileInfo(fullPath).Length;
        if (length is < 2 or > MaximumRecipeBytes)
        {
            throw Errors.InvalidRecipe("RECIPE_SIZE_UNSUPPORTED", $"Recipe size {length} is outside the supported range.", "Provide a JSON recipe between 2 bytes and 1 MiB.");
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return JsonDefaults.Deserialize<RecipeDocument>(bytes, "Recipe");
    }

    private static async Task<RuntimeObservationInput> ReadObservationAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw Errors.Input("RUNTIME_OBSERVATION_NOT_FOUND", $"Observation file '{fullPath}' does not exist.", "Provide an existing runtime observation JSON file.");
        }

        var length = new FileInfo(fullPath).Length;
        if (length is < 2 or > MaximumRecipeBytes)
        {
            throw Errors.InvalidRecipe("RUNTIME_OBSERVATION_SIZE_UNSUPPORTED", $"Observation size {length} is outside the supported range.", "Provide a JSON document between 2 bytes and 1 MiB.");
        }

        return JsonDefaults.Deserialize<RuntimeObservationInput>(await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false), "Runtime observation input");
    }

    private static Option<string> CreateFormatOption()
    {
        var option = new Option<string>("--format")
        {
            Description = "Output format: text or json.",
            DefaultValueFactory = _ => "text",
        };
        option.AcceptOnlyFromAmong("text", "json");
        return option;
    }

    private static Option<string> RequiredStringOption(string name, string description) => new(name)
    {
        Description = description,
        Required = true,
    };

    private static bool IsJson(string format) => string.Equals(format, "json", StringComparison.Ordinal);

    private static bool UsesJsonOutput(IReadOnlyList<string> arguments)
    {
        if (arguments.Count > 0 && arguments[0] == "interactive")
        {
            return false;
        }

        if (arguments.Count > 0 && arguments[0] is "project" or "package" or "addons" or "runtime" or "recipe")
        {
            return true;
        }

        if (arguments.Count > 0 && arguments[0] is "build" or "verify")
        {
            return true;
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], "--format=json", StringComparison.Ordinal)
                || (string.Equals(arguments[index], "--format", StringComparison.Ordinal)
                    && index + 1 < arguments.Count
                    && string.Equals(arguments[index + 1], "json", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static string InferCommand(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return "root";
        }

        if (arguments.Count > 2 && arguments[0] == "catalog" && arguments[1] == "heroes")
        {
            return $"catalog.heroes.{arguments[2]}";
        }

        return arguments[0] is "project" or "package" or "addons" or "runtime" or "components" or "recipe" or "catalog" or "compatibility" && arguments.Count > 1
            ? $"{arguments[0]}.{arguments[1]}"
            : arguments[0];
    }

    private IVpkPackagingApplication RequirePackagingApplication() =>
        packagingApplication
        ?? throw Errors.Unsupported(
            "VPK_PACKAGING_NOT_CONFIGURED",
            "This CLI instance has no VPK packaging application.",
            "Use the default composition root or inject the VPK packaging application.");

    private IAddonManagementApplication RequireAddonApplication() =>
        addonApplication
        ?? throw Errors.Unsupported(
            "ADDON_MANAGEMENT_NOT_CONFIGURED",
            "This CLI instance has no addon management application.",
            "Use the default composition root or inject the addon management application.");

    private IResourceCatalogInventoryFactory RequireCatalogueInventoryFactory() =>
        catalogueInventoryFactory
        ?? throw Errors.Unsupported(
            "CATALOGUE_INVENTORY_NOT_CONFIGURED",
            "This CLI instance has no base-VPK catalogue inventory factory.",
            "Use the default composition root or inject the catalogue inventory factory.");

    private ICompatibilityScanner RequireCompatibilityScanner() =>
        compatibilityScanner
        ?? throw Errors.Unsupported(
            "COMPATIBILITY_SCANNER_NOT_CONFIGURED",
            "This CLI instance has no compatibility scanner.",
            "Use the default composition root or inject the compatibility scanner.");

    private ICompatibilityReportPublisher RequireCompatibilityReportPublisher() =>
        compatibilityReportPublisher
        ?? throw Errors.Unsupported(
            "COMPATIBILITY_REPORT_OUTPUT_NOT_CONFIGURED",
            "This CLI instance has no compatibility report publisher.",
            "Use the default composition root or inject the compatibility report publisher.");

    private static ContentHash ParseContentHash(string value)
    {
        try
        {
            return new ContentHash(value);
        }
        catch (ArgumentException exception)
        {
            throw new S2ModKitException(
                new S2Error("CONTENT_HASH_INVALID", "schema", exception.Message, "Provide a 64-character SHA-256 hexadecimal value.", ErrorCategory.CliOrSchema),
                exception);
        }
    }

    private static float? ParseOptionalSingle(string? value, string optionName)
    {
        if (value is null)
        {
            return null;
        }

        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || !float.IsFinite(parsed))
        {
            throw Errors.InvalidRecipe(
                "CLI_NUMBER_INVALID",
                $"Option '{optionName}' value '{value}' is not a finite invariant-culture number.",
                $"Provide {optionName} using a decimal point, for example 1.25 or -3.5.");
        }

        return parsed;
    }

    private static int? ParseOptionalNonNegativeInt32(string? value, string optionName)
    {
        if (value is null)
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            throw Errors.InvalidRecipe(
                "CLI_NUMBER_INVALID",
                $"Option '{optionName}' value '{value}' is not a non-negative invariant-culture integer.",
                $"Provide {optionName} as 0 or a positive integer.");
        }

        return parsed;
    }
}
