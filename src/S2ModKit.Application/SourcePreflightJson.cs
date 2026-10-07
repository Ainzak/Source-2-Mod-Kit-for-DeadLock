using System.Text.Json;
using S2ModKit.Domain;

namespace S2ModKit.Application;

/// <summary>Strict diagnostic contract; a serialized report cannot authorize an install.</summary>
public static class SourcePreflightJson
{
    private const int MaximumJsonBytes = 16 * 1024 * 1024;

    public static SourcePreflightReport Read(ReadOnlySpan<byte> json)
    {
        if (json.Length is 0 or > MaximumJsonBytes) throw Invalid("Source-preflight JSON exceeds the bounded artifact size.");
        using var document = JsonDocument.Parse(json.ToArray());
        RejectDuplicates(document.RootElement);
        var result = JsonDefaults.Deserialize<SourcePreflightReport>(json, "source preflight");
        Validate(result);
        return result;
    }

    public static string Write(SourcePreflightReport report)
    {
        Validate(report);
        var json = JsonDefaults.Serialize(report);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes) throw Invalid("Source-preflight JSON exceeds the bounded artifact size.");
        return json;
    }

    public static void Validate(SourcePreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.SchemaVersion != 1 || report.Status != "passed" || report.Scope != "relevant_source_snapshot"
            || string.IsNullOrWhiteSpace(report.ReportId) || !report.ReportId.StartsWith("source-preflight-", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(report.ProjectId)
            || string.IsNullOrWhiteSpace(report.BuildId) || string.IsNullOrWhiteSpace(report.PackageId)
            || report.ObservedUtc.Offset != TimeSpan.Zero)
            throw Invalid("Invalid source-preflight identity, version or snapshot disposition.");
        ValidateResources(report.Expected);
        CurrentSourceInstallationApplication.RequireCurrent(report.Expected, report.Initial);
        CurrentSourceInstallationApplication.RequireCurrent(report.Expected, report.Handoff);
        foreach (var hash in new[] { report.PlanFingerprint, report.PackageHash, report.EntryHash, report.ModelVerificationHash, report.PackageVerificationHash })
            if (string.IsNullOrWhiteSpace(hash.Value)) throw Invalid("Missing source-preflight linkage hash.");
    }

    internal static void ValidateResources(IReadOnlyList<CurrentSourceResource> resources)
    {
        if (resources is null || resources.Count is < 1 or > 65536 || resources.Count(item => item?.Role == "model") != 1)
            throw Invalid("Require one model and a bounded complete dependency inventory.");
        string? previous = null;
        foreach (var item in resources)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.LogicalPath) || StableIdentity.NormalizePath(item.LogicalPath) != item.LogicalPath
                || item.LogicalPath.Split('/').Any(part => part is "" or "." or ".." || part.Contains(':', StringComparison.Ordinal))
                || item.Size <= 0 || string.IsNullOrWhiteSpace(item.ContentHash.Value) || item.Role is not ("model" or "dependency")
                || (previous is not null && string.CompareOrdinal(previous, item.LogicalPath) >= 0))
                throw Invalid("Source resources must be sorted, unique, normalized and complete.");
            previous = item.LogicalPath;
        }
    }

    private static void RejectDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid("Duplicate source-preflight property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) RejectDuplicates(item);
    }

    private static S2ModKitException Invalid(string summary) => Errors.InvalidRecipe("SOURCE_PREFLIGHT_INVALID", summary,
        "Use the complete version-1 source-preflight contract; rerun the guarded workflow for a new observation.");
}
