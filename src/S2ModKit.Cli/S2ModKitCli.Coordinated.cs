using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;
using S2ModKit.Reporting;
using System.Globalization;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private static GuidedComponentChoice[] CreateCoordinatedCandidateChoices(ComponentDiscoveryResultV2 discovery) =>
        discovery.Candidates.Select(c => new GuidedComponentChoice(c.CandidateId, c.DisplayLabel, c.Kind,
            c switch { MaterialGroupComponentCandidateV2 m => m.Lods.Select(l => l.Lod).ToArray(), MeshLineageComponentCandidateV2 m => m.Lods.Select(l => l.Lod).ToArray(), _ => [] },
            [], c switch { MaterialGroupComponentCandidateV2 m => m.Lods.Sum(l => l.DrawCallCount), MeshLineageComponentCandidateV2 m => m.Lods.Sum(l => l.DrawCallCount), _ => 0 }))
        .OrderBy(c => c.DisplayLabel, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.CandidateId, StringComparer.Ordinal).ToArray();

    private static async Task<int[]?> ReadGuidedUnionChoicesAsync(TextReader input, TextWriter output, int count, CancellationToken token)
    {
        output.WriteLine("Select exact component numbers separated by commas. Overlapping views are deduplicated; every complete member must map across all LODs. No anatomical parts are inferred.");
        while (true)
        {
            output.Write("Component numbers: ");
            var text = (await input.ReadLineAsync(token).ConfigureAwait(false))?.Trim();
            if (text is null or "cancel" or "quit" or "q") return null;
            if (text is "0" or "back" or "previous") return [GuidedBackChoice];
            var parts = text.Split(',', StringSplitOptions.TrimEntries);
            var indices = new List<int>();
            foreach (var part in parts)
            {
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 || number > count || indices.Contains(number - 1)) break;
                indices.Add(number - 1);
            }
            if (indices.Count == parts.Length) return indices.ToArray();
            output.WriteLine("Use distinct current menu numbers, 'back', or 'cancel'.");
        }
    }
    private Task<ComponentDiscoveryResultV2> DiscoverCoordinatedCliAsync(string project, bool experimental, CancellationToken token)
    {
        if (!experimental) throw Errors.InvalidRecipe("COORDINATED_OPT_IN_REQUIRED", "Coordinated discovery requires explicit experimental acknowledgement.", "Use --experimental --coordinated.");
        return application.DiscoverCoordinatedComponentsAsync(project, token);
    }

    private static async Task<CoordinatedScaffoldOptions?> ReadCoordinatedOptionsAsync(string? path, bool experimental, string intent, CancellationToken token)
    {
        if (intent != RecipeScaffoldContract.CoordinatedFieldIntent)
        {
            if (path is not null) throw Errors.InvalidRecipe("SCAFFOLD_COORDINATED_OPTIONS_INVALID", "Common-field options require coordinated-field intent.", "Remove ignored options or select the coordinated action.");
            return null;
        }
        if (!experimental || string.IsNullOrWhiteSpace(path)) throw Errors.InvalidRecipe("COORDINATED_OPT_IN_REQUIRED", "Coordination requires --experimental and --coordinated-options with every policy and field fact.", "Declare the explicit typed common-field options.");
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw Errors.InvalidRecipe("COORDINATED_OPTIONS_TOO_LARGE", "Common-field options exceed the 2 MiB input limit.", "Supply a bounded typed options document.");
        var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        return CoordinatedSelection.ReadOptions(bytes);
    }

    private async Task<GuidedEllipsoidPreview> CreateGuidedCoordinatedPreviewAsync(GuidedWorkflowSession session, RecipeDocument recipe,
        string sessionPath, ContentHash? expectedPlan, TextWriter output, CancellationToken token)
    {
        var preview = await application.PreviewCoordinatedSelectionAsync(session.ProjectRoot!, recipe, token).ConfigureAwait(false);
        if (preview.PlanFingerprint != expectedPlan) throw Errors.Verification("GUIDED_PREVIEW_PLAN_STALE", "Common-field preview differs from the reviewed plan.", "Restore the exact input and recipe.");
        var artifacts = CoordinatedSelectionPreviewRenderer.Render(preview);
        var root = Path.Combine(Path.GetDirectoryName(sessionPath)!, Path.GetFileNameWithoutExtension(sessionPath) + ".previews");
        var published = await FileSystemEllipsoidPreviewPublisher.PublishCoordinatedAsync(root, artifacts, token).ConfigureAwait(false);
        var identity = new GuidedEllipsoidPreview(preview.PreviewFingerprint, preview.PlanFingerprint, artifacts.SummaryHash, artifacts.ContactSheetHash, published.SummaryPath, published.ContactSheetPath);
        if (session.CoordinatedPreview is not null && session.CoordinatedPreview != identity)
            throw Errors.Verification("GUIDED_PREVIEW_STALE", "Saved coordinated preview identity or paths differ from current immutable artifacts.", "Restore the exact session and previews.");
        output.WriteLine($"Common-field selection sheet: {published.ContactSheetPath}");
        output.WriteLine($"Mechanical member and excluded-buffer summary: {published.SummaryPath}");
        output.WriteLine("Bind-space prediction only. Inspect the listed buffers and excluded context before choosing output; labels do not select anatomy or procedural parts.");
        return identity;
    }
}
