using System.Globalization;
using System.Text;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public static class CompatibilityReportContract
{
    public const int SchemaVersion = 1;
}

public sealed record CompatibilityGapCluster(
    string ClusterId,
    string SignatureId,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> ResourceIds);

public sealed record CompatibilityScanComparison(
    string PriorReportId,
    IReadOnlyList<string> AddedResourceIds,
    IReadOnlyList<string> RemovedResourceIds,
    IReadOnlyList<string> ChangedResourceIds,
    IReadOnlyList<string> UnchangedResourceIds);

public sealed record CompatibilityReport(
    int SchemaVersion,
    string ReportId,
    CompatibilityScanResult Scan,
    IReadOnlyList<CompatibilityGapCluster> Clusters,
    CompatibilityScanComparison? Comparison);

public sealed record CompatibilityReportPublication(
    CompatibilityReport Report,
    string JsonPath,
    string MarkdownPath);

public interface ICompatibilityReportPublisher
{
    Task<CompatibilityReportPublication> PublishAsync(
        string outputRoot,
        CompatibilityReport report,
        string json,
        string markdown,
        CancellationToken cancellationToken = default);
}

public static class CompatibilityReports
{
    public static CompatibilityReport Create(
        CompatibilityScanResult scan,
        CompatibilityReport? prior = null)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (scan.SchemaVersion != CompatibilityScanContract.SchemaVersion)
        {
            throw new ArgumentException("Compatibility scan schema version is unsupported.", nameof(scan));
        }

        var canonicalScan = scan with
        {
            Resources = scan.Resources
                .OrderBy(resource => resource.HeroDisplayName, StringComparer.Ordinal)
                .ThenBy(resource => resource.ResourceDisplayName, StringComparer.Ordinal)
                .ThenBy(resource => resource.ResourceId, StringComparer.Ordinal)
                .ToArray(),
        };
        var scanHash = ContentHash.Compute(JsonDefaults.SerializeToUtf8(canonicalScan));
        var reportId = $"compat_{scanHash.Value[..24]}";
        return new CompatibilityReport(
            CompatibilityReportContract.SchemaVersion,
            reportId,
            canonicalScan,
            CreateClusters(canonicalScan.Resources),
            prior is null ? null : Compare(reportId, canonicalScan.Resources, prior));
    }

    public static string RenderJson(CompatibilityReport report) => JsonDefaults.Serialize(report);

    public static string RenderMarkdown(CompatibilityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.Append("# Compatibility scan ").Append(report.ReportId).AppendLine().AppendLine();
        AppendSummary(builder, report);
        builder.AppendLine().AppendLine("## Resources").AppendLine();
        builder.AppendLine("| Hero | Resource | Status | Candidates | Gaps |");
        builder.AppendLine("|---|---|---:|---:|---|");
        foreach (var resource in report.Scan.Resources)
        {
            builder.Append("| ").Append(Escape(resource.HeroDisplayName))
                .Append(" | ").Append(Escape(resource.ResourceDisplayName))
                .Append(" | ").Append(resource.Status)
                .Append(" | ").Append(resource.Candidates.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(Escape(string.Join(", ", resource.Reasons.Select(reason => reason.Code))))
                .AppendLine(" |");
        }

        builder.AppendLine().AppendLine("## Unsupported clusters").AppendLine();
        if (report.Clusters.Count == 0)
        {
            builder.AppendLine("No unsupported capability clusters were found.");
        }
        else
        {
            foreach (var cluster in report.Clusters)
            {
                builder.Append("- **").Append(cluster.ResourceIds.Count.ToString(CultureInfo.InvariantCulture))
                    .Append(" resource(s)** — ").Append(string.Join(", ", cluster.ReasonCodes))
                    .Append(" (`").Append(cluster.SignatureId).AppendLine("`)");
            }
        }

        if (report.Comparison is { } comparison)
        {
            builder.AppendLine().AppendLine("## Comparison").AppendLine();
            builder.Append("Prior report: `").Append(comparison.PriorReportId).AppendLine("`");
            builder.Append("- Added: ").AppendLine(JoinOrNone(comparison.AddedResourceIds));
            builder.Append("- Removed: ").AppendLine(JoinOrNone(comparison.RemovedResourceIds));
            builder.Append("- Changed: ").AppendLine(JoinOrNone(comparison.ChangedResourceIds));
        }

        return builder.ToString();
    }

    public static string RenderText(CompatibilityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.Append("Compatibility scan ").Append(report.ReportId).AppendLine();
        AppendSummary(builder, report);
        foreach (var cluster in report.Clusters)
        {
            builder.Append("Gap cluster: ").Append(cluster.ResourceIds.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" resource(s); ").AppendLine(string.Join(", ", cluster.ReasonCodes));
        }

        if (report.Comparison is { } comparison)
        {
            builder.Append("Compared with ").Append(comparison.PriorReportId)
                .Append(": added ").Append(comparison.AddedResourceIds.Count.ToString(CultureInfo.InvariantCulture))
                .Append(", removed ").Append(comparison.RemovedResourceIds.Count.ToString(CultureInfo.InvariantCulture))
                .Append(", changed ").Append(comparison.ChangedResourceIds.Count.ToString(CultureInfo.InvariantCulture))
                .AppendLine();
        }

        return builder.ToString();
    }

    private static CompatibilityGapCluster[] CreateClusters(IReadOnlyList<CompatibilityResourceResult> resources) =>
        resources
            .Where(resource => resource.Reasons.Count > 0)
            .GroupBy(resource => new
            {
                SignatureId = resource.Signature?.SignatureId ?? "inspection_failed",
                Reasons = string.Join("+", resource.Reasons.Select(reason => reason.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
            })
            .Select(group =>
            {
                var resourceIds = group.Select(resource => resource.ResourceId).Order(StringComparer.Ordinal).ToArray();
                var reasonCodes = group.SelectMany(resource => resource.Reasons)
                    .Select(reason => reason.Code)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var seed = $"compatibility_cluster@1\n{group.Key.SignatureId}\n{string.Join("\n", reasonCodes)}\n";
                var hash = ContentHash.Compute(Encoding.UTF8.GetBytes(seed));
                return new CompatibilityGapCluster(
                    $"gap_{hash.Value[..24]}",
                    group.Key.SignatureId,
                    reasonCodes,
                    resourceIds);
            })
            .OrderBy(cluster => cluster.ReasonCodes.Count == 0 ? string.Empty : cluster.ReasonCodes[0], StringComparer.Ordinal)
            .ThenBy(cluster => cluster.SignatureId, StringComparer.Ordinal)
            .ThenBy(cluster => cluster.ClusterId, StringComparer.Ordinal)
            .ToArray();

    private static CompatibilityScanComparison Compare(
        string currentReportId,
        IReadOnlyList<CompatibilityResourceResult> current,
        CompatibilityReport prior)
    {
        if (prior.SchemaVersion != CompatibilityReportContract.SchemaVersion)
        {
            throw Errors.Input(
                "COMPATIBILITY_PRIOR_SCHEMA_UNSUPPORTED",
                $"Prior compatibility report schema version {prior.SchemaVersion} is unsupported.",
                "Provide a version-1 compatibility report.");
        }

        if (string.Equals(currentReportId, prior.ReportId, StringComparison.Ordinal))
        {
            var ids = current.Select(resource => resource.ResourceId).Order(StringComparer.Ordinal).ToArray();
            return new CompatibilityScanComparison(prior.ReportId, [], [], [], ids);
        }

        var currentById = current.ToDictionary(resource => resource.ResourceId, StringComparer.Ordinal);
        var priorById = prior.Scan.Resources.ToDictionary(resource => resource.ResourceId, StringComparer.Ordinal);
        var added = currentById.Keys.Except(priorById.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var removed = priorById.Keys.Except(currentById.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var changed = currentById.Keys.Intersect(priorById.Keys, StringComparer.Ordinal)
            .Where(id => !string.Equals(ResourceIdentity(currentById[id]), ResourceIdentity(priorById[id]), StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var unchanged = currentById.Keys.Intersect(priorById.Keys, StringComparer.Ordinal)
            .Except(changed, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new CompatibilityScanComparison(prior.ReportId, added, removed, changed, unchanged);
    }

    private static string ResourceIdentity(CompatibilityResourceResult resource) =>
        string.Join(
            "|",
            resource.ContentHash.Value,
            resource.Status,
            resource.Signature?.SignatureId ?? "none",
            string.Join(",", resource.Reasons.Select(reason => reason.Code).Order(StringComparer.Ordinal)),
            string.Join(",", resource.Candidates.SelectMany(candidate => candidate.Capabilities)
                .Select(capability => $"{capability.OperationKind}@{capability.OperationVersion}:{capability.Availability}")
                .Order(StringComparer.Ordinal)));

    private static void AppendSummary(StringBuilder builder, CompatibilityReport report)
    {
        builder.Append("Resources: ").Append(report.Scan.Resources.Count.ToString(CultureInfo.InvariantCulture))
            .Append("; supported: ").Append(report.Scan.Resources.Count(resource => resource.Status == CompatibilityScanContract.Supported).ToString(CultureInfo.InvariantCulture))
            .Append("; unsupported: ").Append(report.Scan.Resources.Count(resource => resource.Status == CompatibilityScanContract.Unsupported).ToString(CultureInfo.InvariantCulture))
            .Append("; inspection failed: ").Append(report.Scan.Resources.Count(resource => resource.Status == CompatibilityScanContract.InspectionFailed).ToString(CultureInfo.InvariantCulture))
            .Append("; gap clusters: ").Append(report.Clusters.Count.ToString(CultureInfo.InvariantCulture))
            .AppendLine();
    }

    private static string JoinOrNone(IReadOnlyList<string> values) => values.Count == 0 ? "none" : string.Join(", ", values);

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
