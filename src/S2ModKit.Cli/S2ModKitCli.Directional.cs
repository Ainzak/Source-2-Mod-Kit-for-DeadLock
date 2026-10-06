using System.CommandLine;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;
using S2ModKit.Reporting;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private async Task<object> ScaffoldCliAsync(string project, RecipeScaffoldRequest request, CancellationToken token)
    {
        var result = await application.ScaffoldRecipeAsync(project, request, token).ConfigureAwait(false);
        if (request.Directional is null) return result;
        return new
        {
            result.OutputPath,
            result.RecipeContentHash,
            result.DiscoveryFingerprint,
            result.ComponentIds,
            Scale = request.Directional.Field.Scale,
            ProtectionAssertions = request.Directional.Protection.Assertions.Count,
            Status = "canonical_recipe_exact_planner_passed",
            Next = "directional review --project <project> --recipe <outputPath> --experimental --output-root <ignored-root>"
        };
    }

    private Task<ComponentDiscoveryResultV2> DiscoverDirectionalCliAsync(string project, bool experimental, bool coordinated, CancellationToken token)
    {
        RequireDirectionalOptIn(experimental);
        if (coordinated) throw Errors.InvalidRecipe("DIRECTIONAL_ROUTE_AMBIGUOUS", "Choose one discovery profile.", "Remove --coordinated when using --directional.");
        return application.DiscoverDirectionalComponentsAsync(project, token);
    }

    private static void RequireDirectionalOptIn(bool experimental)
    {
        if (!experimental) throw Errors.InvalidRecipe("DIRECTIONAL_OPT_IN_REQUIRED", "Directional authoring requires explicit experimental acknowledgement.", "Use --experimental; runtime consumers remain unverified.");
    }

    private static async Task<DirectionalScaffoldOptions?> ReadDirectionalOptionsAsync(string? path, bool experimental, string intent, CancellationToken token)
    {
        if (intent != RecipeScaffoldContract.DirectionalFieldIntent)
        {
            if (path is not null) throw Errors.InvalidRecipe("SCAFFOLD_DIRECTIONAL_OPTIONS_INVALID", "Directional options require directional-field intent.", "Remove ignored options or choose the matching intent.");
            return null;
        }
        RequireDirectionalOptIn(experimental);
        if (string.IsNullOrWhiteSpace(path)) throw Errors.InvalidRecipe("SCAFFOLD_DIRECTIONAL_OPTIONS_INVALID", "Explicit source-bound field/protection options are required.", "Supply --directional-options.");
        if (new FileInfo(path).Length > 1024 * 1024) throw Errors.InvalidRecipe("DIRECTIONAL_OPTIONS_TOO_LARGE", "Options exceed the 1 MiB recipe input budget.", "Choose a bounded complete protection assertion.");
        return DirectionalAuthoring.ReadOptions(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false));
    }

    private Command CreateDirectionalCommand(TextWriter output, TextWriter error)
    {
        var command = new Command("directional", "Non-interactive source inspection and compact bind-space review.");
        var inspect = new Command("inspect", "Inspect selected complete buffers and explicit root-bone assertion candidates; no field is admitted.");
        var project = RequiredStringOption("--project", "Immutable imported project.");
        var components = new Option<string[]>("--component") { Required = true, Description = "Current schema-6 component ID; repeat for a deduplicated union." };
        var root = RequiredStringOption("--output-root", "Configured ignored source-report root.");
        var experimental = new Option<bool>("--experimental");
        inspect.Options.Add(project); inspect.Options.Add(components); inspect.Options.Add(root); inspect.Options.Add(experimental);
        inspect.SetAction((parse, token) => ExecuteAsync("directional.inspect", true, output, error, async () =>
        {
            RequireDirectionalOptIn(parse.GetValue(experimental));
            var source = await application.InspectDirectionalSelectionAsync(parse.GetRequiredValue(project), parse.GetValue(components) ?? [], token).ConfigureAwait(false);
            var report = await FileSystemDirectionalAuthoringPublisher.PublishAsync(parse.GetRequiredValue(root), source, token).ConfigureAwait(false);
            return new
            {
                source.InputHash,
                MemberCount = source.Members.Count,
                LodCount = source.Members[0].Lods.Count,
                SelectedRecords = source.Buffers.Sum(b => b.Points.Count),
                ContextBuffers = source.Context.Count,
                RootBoneChoices = source.Bones.Count(b => b.Assertion is not null),
                ReportPath = report.Path,
                ReportHash = report.Hash,
                Status = "source_facts_only",
                Next = "Choose explicit field and keep-fixed assertions; scaffold then review."
            };
        }, renderText: null, token));
        command.Subcommands.Add(inspect);

        var review = new Command("review", "Plan and publish a matched surface comparison with one compact summary.");
        var reviewProject = RequiredStringOption("--project", "Immutable imported project.");
        var recipe = RequiredStringOption("--recipe", "Canonical schema-10 recipe.");
        var previewRoot = RequiredStringOption("--output-root", "Configured ignored comparison root.");
        var reviewExperimental = new Option<bool>("--experimental");
        review.Options.Add(reviewProject); review.Options.Add(recipe); review.Options.Add(previewRoot); review.Options.Add(reviewExperimental);
        review.SetAction((parse, token) => ExecuteAsync("directional.review", true, output, error, async () =>
        {
            RequireDirectionalOptIn(parse.GetValue(reviewExperimental));
            var intent = await ReadRecipeAsync(parse.GetRequiredValue(recipe), token).ConfigureAwait(false);
            var preview = await application.PreviewDirectionalSelectionAsync(parse.GetRequiredValue(reviewProject), intent, token).ConfigureAwait(false);
            var publication = await FileSystemEllipsoidPreviewPublisher.PublishDirectionalAsync(parse.GetRequiredValue(previewRoot), DirectionalSelectionPreviewRenderer.Render(preview), token).ConfigureAwait(false);
            return new
            {
                preview.InputHash,
                preview.PlanFingerprint,
                Scale = preview.Transform.Field.Scale,
                MemberCount = preview.Transform.Members.Count,
                ProtectionAssertions = preview.Transform.Protection.Assertions.Count,
                Lods = preview.Measurements,
                PreviewPath = publication.ContactSheetPath,
                ProofPath = publication.SummaryPath,
                Status = "bind_space_prediction_runtime_untested"
            };
        }, renderText: null, token));
        command.Subcommands.Add(review);
        return command;
    }
}
