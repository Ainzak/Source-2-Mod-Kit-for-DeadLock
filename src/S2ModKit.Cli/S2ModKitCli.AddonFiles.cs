using System.CommandLine;
using S2ModKit.Application;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private void AddAddonFileCommands(Command command, TextWriter output, TextWriter error)
    {
        var scan = new Command("scan", "List addon archives with their mod-manager names and resource kinds (read-only).");
        var scanRoot = RequiredStringOption("--addons-root", "Deadlock addons directory or an isolated test root.");
        var scanManager = ManagerStateOption();
        var scanCache = CacheOption();
        var scanFormat = CreateFormatOption();
        scan.Options.Add(scanRoot);
        scan.Options.Add(scanManager);
        scan.Options.Add(scanCache);
        scan.Options.Add(scanFormat);
        scan.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "addons.scan", IsJson(parseResult.GetRequiredValue(scanFormat)), output, error,
            () => AddonFileApplication.ScanAsync(
                parseResult.GetRequiredValue(scanRoot),
                ManagerStatePath(parseResult.GetValue(scanManager)),
                parseResult.GetValue(scanCache),
                cancellationToken),
            RenderAddonScan,
            cancellationToken));

        var install = new Command("install-file", "Install one finished VPK into a free slot; refuses when an enabled mod changes the same files.");
        var installRoot = RequiredStringOption("--addons-root", "Deadlock addons directory or an isolated test root.");
        var installPackage = RequiredStringOption("--package", "Finished VPK file to install (it is copied, never moved).");
        var installSlot = new Option<string>("--slot") { Description = "Two-digit slot or auto (highest free slot 90-99).", DefaultValueFactory = _ => "auto" };
        var installReceipts = RequiredStringOption("--receipts", "Folder for this installation's receipts (one installation at a time).");
        var installLabel = new Option<string>("--label") { Description = "Short name used in messages.", DefaultValueFactory = _ => "test" };
        var installManager = ManagerStateOption();
        var installCache = CacheOption();
        var installFormat = CreateFormatOption();
        foreach (var option in new Option[] { installRoot, installPackage, installSlot, installReceipts, installLabel, installManager, installCache, installFormat })
        {
            install.Options.Add(option);
        }

        install.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "addons.install-file", IsJson(parseResult.GetRequiredValue(installFormat)), output, error,
            () => AddonFileApplication.InstallAsync(
                new AddonFileInstallRequest(
                    parseResult.GetRequiredValue(installRoot),
                    parseResult.GetRequiredValue(installPackage),
                    parseResult.GetRequiredValue(installSlot),
                    parseResult.GetRequiredValue(installReceipts),
                    parseResult.GetRequiredValue(installLabel),
                    ManagerStatePath(parseResult.GetValue(installManager)),
                    parseResult.GetValue(installCache)),
                cancellationToken),
            RenderAddonFileResult,
            cancellationToken));

        var rollback = new Command("rollback-file", "Remove the file this receipts folder installed; other files are never touched.");
        var rollbackRoot = RequiredStringOption("--addons-root", "Deadlock addons directory or an isolated test root.");
        var rollbackReceipts = RequiredStringOption("--receipts", "Receipts folder used by install-file.");
        var rollbackFormat = CreateFormatOption();
        rollback.Options.Add(rollbackRoot);
        rollback.Options.Add(rollbackReceipts);
        rollback.Options.Add(rollbackFormat);
        rollback.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "addons.rollback-file", IsJson(parseResult.GetRequiredValue(rollbackFormat)), output, error,
            () => AddonFileApplication.RollbackAsync(
                parseResult.GetRequiredValue(rollbackRoot),
                parseResult.GetRequiredValue(rollbackReceipts),
                cancellationToken),
            RenderAddonFileResult,
            cancellationToken));

        command.Subcommands.Add(scan);
        command.Subcommands.Add(install);
        command.Subcommands.Add(rollback);
    }

    private static Option<string?> ManagerStateOption() => new("--manager-state")
    {
        Description = "Deadlock Mod Manager state.json (read-only; default: its standard location).",
    };

    private static Option<string?> CacheOption() => new("--cache")
    {
        Description = "Optional file that caches addon file lists by size and modification time.",
    };

    private static string? ManagerStatePath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var appData = Environment.GetEnvironmentVariable("APPDATA");
        return string.IsNullOrWhiteSpace(appData) ? null : Path.Combine(appData, "dev.stormix.deadlock-mod-manager", "state.json");
    }

    private static void RenderAddonFileResult(TextWriter output, AddonFileResult result) => output.WriteLine(result.Summary);

    private static void RenderAddonScan(TextWriter output, AddonScan scan)
    {
        var loaded = scan.Archives.Count(item => item.Loaded);
        output.WriteLine($"{scan.Archives.Count} archives, {loaded} loaded by the game ({scan.CachedArchives} from cache).");
        foreach (var archive in scan.Archives.OrderBy(item => item.Loaded ? 0 : 1).ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase))
        {
            var kinds = string.Join(", ", archive.ResourceKinds.Select(pair => $"{pair.Key} {pair.Value}"));
            var mod = archive.ModName ?? (archive.ModId is null ? "-" : "mod " + archive.ModId);
            output.WriteLine($"  {(archive.Loaded ? "loaded  " : "inactive")} {archive.FileName}  [{mod}]  {kinds}");
        }
    }
}
