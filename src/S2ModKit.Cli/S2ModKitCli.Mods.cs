using System.CommandLine;
using System.Globalization;
using S2ModKit.Adapters.Vpk;
using S2ModKit.Application;
using S2ModKit.Infrastructure;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private IModStatusApplication? modStatusApplication;

    /// <summary>Reports which loaded addons a game update may have broken (defaults to the file-system implementation).</summary>
    public IModStatusApplication ModStatusApplication
    {
        get => modStatusApplication ??= new ModStatusApplication(
            new AddonFileStore(),
            new ValvePakAddonTreeReader(),
            new ModManagerStateReader(),
            new GameBuildReader(),
            new ValvePakBaseGameArchiveFactory(),
            new ModStatusStore(),
            new SystemClock());
        init => modStatusApplication = value;
    }

    private Command CreateModsCommand(TextWriter output, TextWriter error)
    {
        var command = new Command("mods", "Show which loaded mods a game update may have broken (read-only; nothing is repaired).");

        var status = new Command("status", "List every loaded addon with its mod-manager name, resource kinds and update status.");
        var statusGame = RequiredStringOption("--game", "Deadlock 'game' folder (contains 'citadel').");
        var statusRoot = RequiredStringOption("--addons-root", "Deadlock addons directory or an isolated test root.");
        var statusState = RequiredStringOption("--state", "S2ModKit state folder for game-build records and acknowledgements (never defaulted).");
        var statusReceipts = new Option<string[]>("--receipts")
        {
            Description = "Receipts folder written by `addons install-file` (repeatable).",
            Arity = ArgumentArity.OneOrMore,
        };
        var statusManager = ManagerStateOption();
        var statusFormat = CreateFormatOption();
        foreach (var option in new Option[] { statusGame, statusRoot, statusState, statusReceipts, statusManager, statusFormat })
        {
            status.Options.Add(option);
        }

        status.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "mods.status", IsJson(parseResult.GetRequiredValue(statusFormat)), output, error,
            () => ModStatusApplication.StatusAsync(
                new ModStatusRequest(
                    parseResult.GetRequiredValue(statusGame),
                    parseResult.GetRequiredValue(statusRoot),
                    parseResult.GetRequiredValue(statusState),
                    parseResult.GetValue(statusReceipts) ?? [],
                    ManagerStatePath(parseResult.GetValue(statusManager))),
                cancellationToken),
            RenderModStatus,
            cancellationToken));

        var acknowledge = new Command("ack", "Dismiss the update notice of one loaded addon or of all of them for the current game build.");
        var ackGame = RequiredStringOption("--game", "Deadlock 'game' folder (contains 'citadel').");
        var ackState = RequiredStringOption("--state", "S2ModKit state folder used by `mods status`.");
        var ackFile = new Option<string?>("--file") { Description = "Loaded addon file name, for example pak03_dir.vpk." };
        var ackAll = new Option<bool>("--all") { Description = "Acknowledge every loaded addon." };
        var ackRoot = new Option<string?>("--addons-root") { Description = "Addons directory (default: citadel/addons below --game)." };
        var ackFormat = CreateFormatOption();
        foreach (var option in new Option[] { ackGame, ackState, ackFile, ackAll, ackRoot, ackFormat })
        {
            acknowledge.Options.Add(option);
        }

        acknowledge.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "mods.ack", IsJson(parseResult.GetRequiredValue(ackFormat)), output, error,
            () =>
            {
                var game = parseResult.GetRequiredValue(ackGame);
                var root = parseResult.GetValue(ackRoot);
                return ModStatusApplication.AcknowledgeAsync(
                    new ModAckRequest(
                        game,
                        string.IsNullOrWhiteSpace(root) ? Path.Combine(game, "citadel", "addons") : root,
                        parseResult.GetRequiredValue(ackState),
                        parseResult.GetValue(ackFile),
                        parseResult.GetValue(ackAll)),
                    cancellationToken);
            },
            RenderModAck,
            cancellationToken));

        command.Subcommands.Add(status);
        command.Subcommands.Add(acknowledge);
        return command;
    }

    private static void RenderModStatus(TextWriter output, ModStatusReport report)
    {
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Game build {report.Build} ({report.VersionDate}){(report.NewBuildRecorded ? "; new build recorded" : string.Empty)}"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{report.Mods.Count} loaded mods, {report.NoticeCount} with a notice."));
        foreach (var mod in report.Mods.Where(item => !item.Acknowledged))
        {
            var name = mod.ModName ?? "-";
            var kinds = string.Join(", ", mod.ResourceKinds.Select(pair => $"{pair.Key} {pair.Value}"));
            output.WriteLine($"  {(mod.Notice ? "!" : " ")} {mod.FileName}  [{name}]  {kinds}  - {mod.Message}");
        }

        if (report.AcknowledgedCount > 0)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{report.AcknowledgedCount} acknowledged (hidden)."));
        }
    }

    private static void RenderModAck(TextWriter output, ModAckResult result) =>
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Acknowledged {result.Acknowledged.Count} mod(s) for build {result.Build}."));
}
