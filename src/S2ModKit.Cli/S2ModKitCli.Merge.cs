using System.CommandLine;
using S2ModKit.Adapters.Source2;
using S2ModKit.Adapters.Vpk;
using S2ModKit.Application;
using S2ModKit.Infrastructure;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private AddonMergeApplication? addonMergeApplication;

    /// <summary>Merges addon archives (defaults to the file-system implementation).</summary>
    public AddonMergeApplication AddonMergeApplication
    {
        get => addonMergeApplication ??= CreateDefaultMerge();
        init => addonMergeApplication = value;
    }

    private static AddonMergeApplication CreateDefaultMerge()
    {
        var archives = new ValvePakAddonArchiveContent();
        return new AddonMergeApplication(new ValvePakAddonTreeReader(), archives, archives, new AddonFileStore(), new Source2CompiledModelAdapter());
    }

    private void AddAddonMergeCommand(Command command, TextWriter output, TextWriter error)
    {
        var merge = new Command("merge", "Combine several addon archives into one new archive; inputs are never changed.");
        var inputs = new Option<string[]>("--input") { Description = "Input VPK file (repeat for each archive).", Required = true, AllowMultipleArgumentsPerToken = false };
        var outputPath = new Option<string?>("--output") { Description = "New merged VPK file (must not exist)." };
        var prefer = new Option<string[]>("--prefer") { Description = "Input file name whose files win every conflict (repeat to set an order)." };
        var keep = new Option<string[]>("--keep") { Description = "<logical path>=<input file name> for one conflict." };
        var dryRun = new Option<bool>("--dry-run") { Description = "List inputs, conflicts and notices without writing." };
        var format = CreateFormatOption();
        foreach (var option in new Option[] { inputs, outputPath, prefer, keep, dryRun, format })
        {
            merge.Options.Add(option);
        }

        merge.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "addons.merge", IsJson(parseResult.GetRequiredValue(format)), output, error,
            () => AddonMergeApplication.MergeAsync(
                new AddonMergeRequest(
                    parseResult.GetValue(inputs) ?? [],
                    parseResult.GetValue(outputPath),
                    parseResult.GetValue(prefer) ?? [],
                    ParseKeep(parseResult.GetValue(keep) ?? []),
                    parseResult.GetValue(dryRun)),
                cancellationToken),
            RenderAddonMerge,
            cancellationToken));
        command.Subcommands.Add(merge);
    }

    private static Dictionary<string, string> ParseKeep(IEnumerable<string> values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var separator = value.LastIndexOf('=');
            if (separator <= 0 || separator == value.Length - 1)
            {
                throw Domain.Errors.InvalidRecipe("ADDON_MERGE_KEEP_INVALID", $"'{value}' is not <logical path>=<input file name>.", "Example: --keep models/heroes_wip/wraith/wraith.vmdl_c=pak03_dir.vpk");
            }

            var path = Domain.StableIdentity.NormalizePath(value[..separator]);
            if (!result.TryAdd(path, value[(separator + 1)..]))
            {
                throw Domain.Errors.InvalidRecipe("ADDON_MERGE_KEEP_DUPLICATE", $"--keep names {path} twice.", "Give one choice per path.");
            }
        }

        return result;
    }

    private static void RenderAddonMerge(TextWriter output, AddonMergeResult result)
    {
        output.WriteLine(result.Summary);
        foreach (var conflict in result.Conflicts.Where(item => !item.IdenticalPayloads))
        {
            output.WriteLine($"  {(conflict.Chosen is null ? "?" : "=")} {conflict.LogicalPath} [{conflict.Kind}]  {string.Join(" / ", conflict.Inputs)}{(conflict.Chosen is null ? string.Empty : " -> " + conflict.Chosen)}");
        }

        foreach (var notice in result.DependencyNotices)
        {
            output.WriteLine($"  note: {notice.ModelInput} model {notice.ModelPath} uses {notice.ReferencedPath}, which {notice.ReferencedInput} replaces");
        }
    }
}
