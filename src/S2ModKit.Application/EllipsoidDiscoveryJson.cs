using System.Text.Json;
using S2ModKit.Domain;

namespace S2ModKit.Application;

internal static class EllipsoidDiscoveryJson
{
    internal static void ValidateShape(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes.ToArray());
        var root = document.RootElement;
        MutationPlanJson.RequireUniqueProperties(root);
        MutationPlanJson.RequireProperties(root, "schemaVersion", "model", "discoveryFingerprint", "analyzer", "candidates", "lineageDiagnostics", "extensions");
        if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number || !root.GetProperty("schemaVersion").TryGetInt32(out var version) || version is not (2 or 3 or 4 or 5 or 6))
            throw Invalid("Unknown discovery schema version.");
        if (version is 3 or 4 or 5 or 6) MutationPlanJson.RequireProperties(root, "experimentalPolicy");
        else if (root.TryGetProperty("experimentalPolicy", out _)) throw Invalid("Strict discovery cannot claim experimental acknowledgement.");
        if (version is not (4 or 5 or 6)) return;
        MutationPlanJson.RequireProperties(root.GetProperty("model"), "logicalPath", "contentHash", "size");
        MutationPlanJson.RequireProperties(root.GetProperty("analyzer"), "name", "version", "componentVersions");
        MutationPlanJson.RequireProperties(root.GetProperty("experimentalPolicy"), "kind", "version");
        if (root.GetProperty("candidates").ValueKind != JsonValueKind.Array || root.GetProperty("lineageDiagnostics").ValueKind != JsonValueKind.Array)
            throw Invalid("Discovery candidates and diagnostics must be arrays.");
        foreach (var candidate in root.GetProperty("candidates").EnumerateArray())
        {
            MutationPlanJson.RequireProperties(candidate, "kind", "candidateId", "model", "displayLabel", "lods", "capabilities", "extensions");
            MutationPlanJson.RequireProperties(candidate.GetProperty("model"), "logicalPath", "contentHash", "size");
            if (candidate.GetProperty("capabilities").ValueKind != JsonValueKind.Array) throw Invalid("Capabilities must be an array.");
            foreach (var capability in candidate.GetProperty("capabilities").EnumerateArray())
                MutationPlanJson.RequireProperties(capability, "operationKind", "operationVersion", "availability", "reasons", "geometryByLod");
        }
    }

    internal static void Validate(ComponentDiscoveryResultV2 result)
    {
        if (result.SchemaVersion is not (2 or 3 or 4 or 5 or 6) || result.Candidates is null
            || (result.SchemaVersion is 3 or 4 or 5 or 6 && result.ExperimentalPolicy is not { Kind: "preserve_unverified", Version: 1 })
            || (result.SchemaVersion == 2 && result.ExperimentalPolicy is not null)
            || result.Candidates.Any(c => c is null || c.Capabilities is null || c.Capabilities.Any(cap => cap is null
                || cap.OperationVersion is not (1 or 2 or 4 or 5 or 6 or 7 or 8 or 9)
                || (result.SchemaVersion == 2 && cap.OperationVersion > 4) || (result.SchemaVersion == 3 && cap.OperationVersion > 6) || (result.SchemaVersion == 4 && cap.OperationVersion > 7) || (result.SchemaVersion == 5 && cap.OperationVersion > 8))))
            throw Invalid("Discovery capabilities do not match their versioned opt-in contract.");
    }

    private static S2ModKitException Invalid(string message) => Errors.InvalidRecipe("COMPONENT_DISCOVERY_VERSION_INVALID", message, "Regenerate discovery through the correct explicit versioned route.");
}
