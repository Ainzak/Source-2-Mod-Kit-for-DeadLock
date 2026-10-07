using System.CommandLine;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private Command CreateCurrentSourceInstallCommand(TextWriter output, TextWriter error)
    {
        var command = new Command("install-current", "Reverify a reviewed minimal package and block installation on current model/dependency drift.");
        var root = RequiredStringOption("--addons-root", "Explicit addons destination.");
        var project = RequiredStringOption("--project", "Immutable project root.");
        var package = RequiredStringOption("--package", "Reviewed minimal package id.");
        var hash = RequiredStringOption("--expected-package-hash", "SHA-256 of the exact reviewed package.");
        var source = RequiredStringOption("--base-vpk", "Explicit current archive containing the model and every imported dependency.");
        var slot = new Option<string>("--slot") { Description = "Auto or explicit empty addon slot.", DefaultValueFactory = _ => "auto" };
        foreach (var option in new Option[] { root, project, package, hash, source, slot }) command.Options.Add(option);
        command.SetAction((parseResult, cancellationToken) => ExecuteAsync("addons.install-current", true, output, error,
            () => (currentSourceInstallation ?? throw Errors.Unsupported("CURRENT_SOURCE_INSTALL_UNAVAILABLE",
                "The configured application has no guarded current-source installation workflow.", "Configure the current-source reader and verification lifecycle."))
                .InstallAsync(new(parseResult.GetRequiredValue(root), parseResult.GetRequiredValue(project),
                    parseResult.GetRequiredValue(package), ParseContentHash(parseResult.GetRequiredValue(hash)),
                    parseResult.GetRequiredValue(source), parseResult.GetRequiredValue(slot)), cancellationToken),
            renderText: null, cancellationToken));
        return command;
    }
}
