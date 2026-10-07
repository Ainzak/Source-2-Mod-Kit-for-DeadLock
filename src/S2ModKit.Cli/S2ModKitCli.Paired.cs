using System.CommandLine;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;
using S2ModKit.Reporting;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private Command CreatePairedCommand(TextWriter output, TextWriter error)
    {
        var group = new Command("paired", "Agent-first paired source discovery, exact scaffolding and bind-space regional review.");
        foreach (var name in new[] { "discover", "inspect", "scaffold", "review" })
        {
            var command = new Command(name, name switch
            {
                "discover" => "List schema-7 structural candidates; authored fields still require exact planning.",
                "inspect" => "Publish complete selected source members, positions and root-bone protection choices.",
                "scaffold" => "Resolve current candidate IDs and write a canonical recipe only after exact planning.",
                _ => "Publish matched all-LOD surfaces and separate partner-region measurements."
            });
            var project = RequiredStringOption("--project", "Immutable imported project.");
            var acknowledge = new Option<bool>("--experimental") { Description = "Explicit acknowledgement of unqualified runtime consumers." };
            command.Options.Add(project); command.Options.Add(acknowledge);
            var components = new Option<string[]>("--component") { Required = name is "inspect" or "scaffold", Description = "Current schema-7 component ID; repeat for a deduplicated union." };
            var root = RequiredStringOption("--output-root", "Configured ignored source/comparison root.");
            var options = RequiredStringOption("--options", "Complete source-bound paired_directional_field_options@1 JSON.");
            var destination = RequiredStringOption("--output", "New canonical recipe path; existing files are refused.");
            var recipePath = RequiredStringOption("--recipe", "Canonical schema-11 paired recipe.");
            if (name is "inspect" or "scaffold") command.Options.Add(components);
            if (name is "inspect" or "review") command.Options.Add(root);
            if (name == "scaffold") { command.Options.Add(options); command.Options.Add(destination); }
            if (name == "review") command.Options.Add(recipePath);
            command.SetAction((parse, token) => ExecuteAsync("paired." + name, true, output, error, async () =>
            {
                if (!parse.GetValue(acknowledge)) throw Errors.InvalidRecipe("PAIRED_OPT_IN_REQUIRED", "Paired authoring requires explicit experimental acknowledgement.", "Use --experimental; preserved simulation/culling inputs remain unqualified.");
                var projectPath = parse.GetRequiredValue(project);
                if (name == "discover") return (object)await application.DiscoverPairedComponentsAsync(projectPath, token).ConfigureAwait(false);
                if (name == "inspect")
                {
                    var source = await application.InspectPairedSelectionAsync(projectPath, parse.GetValue(components) ?? [], token).ConfigureAwait(false);
                    var publication = await FileSystemDirectionalAuthoringPublisher.PublishPairedAsync(parse.GetRequiredValue(root), source, token).ConfigureAwait(false);
                    return new
                    {
                        source.InputHash,
                        MemberCount = source.Members.Count,
                        LodCount = source.Members[0].Lods.Count,
                        ReportPath = publication.Path,
                        ReportHash = publication.Hash,
                        Status = "source_facts_only",
                        Next = "Copy source members into explicit pair options; choose both fields and fixed assertions, then paired scaffold."
                    };
                }
                if (name == "scaffold")
                {
                    var path = parse.GetRequiredValue(options);
                    if (new FileInfo(path).Length is < 2 or > MaximumRecipeBytes) throw Errors.InvalidRecipe("PAIRED_OPTIONS_SIZE_INVALID", "CLI options must fit the 1 MiB recipe budget.", "Choose complete bounded source assertions.");
                    var intent = PairedFieldOptionsJson.Read(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false));
                    var result = await application.ScaffoldPairedRecipeAsync(projectPath, parse.GetValue(components) ?? [], intent, parse.GetRequiredValue(destination), token).ConfigureAwait(false);
                    return new
                    {
                        result.OutputPath,
                        result.RecipeContentHash,
                        result.DiscoveryFingerprint,
                        result.ComponentIds,
                        Status = "canonical_recipe_exact_planner_passed",
                        Next = "paired review, then normal build and verify"
                    };
                }
                var recipe = await ReadRecipeAsync(parse.GetRequiredValue(recipePath), token).ConfigureAwait(false);
                var preview = await application.PreviewPairedSelectionAsync(projectPath, recipe, token).ConfigureAwait(false);
                var artifact = await FileSystemEllipsoidPreviewPublisher.PublishPairedAsync(parse.GetRequiredValue(root), PairedSelectionPreviewRenderer.Render(preview), token).ConfigureAwait(false);
                return new
                {
                    preview.InputHash,
                    preview.PlanFingerprint,
                    preview.Regions,
                    PreviewPath = artifact.ContactSheetPath,
                    ProofPath = artifact.SummaryPath,
                    Status = "bind_space_prediction_runtime_untested"
                };
            }, renderText: null, token));
            group.Subcommands.Add(command);
        }
        return group;
    }
}
