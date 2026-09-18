using System.Globalization;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public static class CompatibilityScanContract
{
    public const int SchemaVersion = 1;

    public const string Supported = "supported";

    public const string Unsupported = "unsupported";

    public const string InspectionFailed = "inspection_failed";
}

public sealed record CompatibilityScanAnalyzer(
    string InspectorName,
    string InspectorVersion,
    string CapabilityAnalyzerName,
    string CapabilityAnalyzerVersion,
    IReadOnlyDictionary<string, string> ComponentVersions);

public sealed record CompatibilityCandidateResult(
    string CandidateId,
    string CandidateKind,
    string DisplayLabel,
    IReadOnlyList<string> MaterialPaths,
    IReadOnlyList<int> Lods,
    IReadOnlyList<CompatibilityCapabilityResult> Capabilities);

public sealed record CompatibilityCapabilityResult(
    string OperationKind,
    int OperationVersion,
    string Availability,
    IReadOnlyList<StructuralCompatibilityReason> Reasons);

public sealed record CompatibilityResourceResult(
    string HeroId,
    string HeroDisplayName,
    string ResourceId,
    string ResourceDisplayName,
    string Role,
    string LogicalPath,
    ContentHash ContentHash,
    long Size,
    string Status,
    StructuralSignature? Signature,
    IReadOnlyList<CompatibilityCandidateResult> Candidates,
    IReadOnlyList<StructuralCompatibilityReason> Reasons);

public sealed record CompatibilityScanResult(
    int SchemaVersion,
    string CatalogueId,
    string CatalogueRevision,
    ContentHash SourceDirectoryHash,
    CompatibilityScanAnalyzer Analyzer,
    IReadOnlyList<CompatibilityResourceResult> Resources);

public interface ICompatibilityScanner
{
    Task<CompatibilityScanResult> ScanAsync(
        HeroCatalogueDocument catalogue,
        IResourceCatalogInventory inventory,
        CancellationToken cancellationToken = default);
}

public sealed class CompatibilityScanner(
    IModelInspector inspector,
    IComponentCapabilityAnalyzer? capabilityAnalyzer = null) : ICompatibilityScanner
{
    public async Task<CompatibilityScanResult> ScanAsync(
        HeroCatalogueDocument catalogue,
        IResourceCatalogInventory inventory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(inventory);
        var verified = await HeroCatalogueQueries.ListAsync(catalogue, inventory, cancellationToken)
            .ConfigureAwait(false);
        var resources = new List<CompatibilityResourceResult>();
        foreach (var hero in verified.Heroes)
        {
            foreach (var resource in hero.Resources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var artifact = await inventory.TryOpenAsync(resource.LogicalPath, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw Errors.Input(
                        "CATALOGUE_RESOURCE_DISAPPEARED",
                        $"Verified resource '{resource.LogicalPath}' disappeared before inspection.",
                        "Retry from one immutable base VPK installation.");
                resources.Add(await ScanResourceAsync(hero, resource, artifact.Content, cancellationToken)
                    .ConfigureAwait(false));
            }
        }

        var components = inspector.ComponentVersions
            .Concat(capabilityAnalyzer?.ComponentVersions ?? new Dictionary<string, string>())
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Value).OrderBy(value => value, StringComparer.Ordinal).Last(),
                StringComparer.Ordinal);
        return new CompatibilityScanResult(
            CompatibilityScanContract.SchemaVersion,
            verified.CatalogueId,
            verified.Revision,
            verified.Source.ActualDirectoryHash,
            new CompatibilityScanAnalyzer(
                inspector.AdapterName,
                inspector.AdapterVersion,
                capabilityAnalyzer?.AnalyzerName ?? "not_configured",
                capabilityAnalyzer?.AnalyzerVersion ?? "not_configured",
                new SortedDictionary<string, string>(components, StringComparer.Ordinal)),
            resources
                .OrderBy(resource => resource.HeroDisplayName, StringComparer.Ordinal)
                .ThenBy(resource => resource.ResourceDisplayName, StringComparer.Ordinal)
                .ThenBy(resource => resource.ResourceId, StringComparer.Ordinal)
                .ToArray());
    }

    private async Task<CompatibilityResourceResult> ScanResourceAsync(
        VerifiedHeroCatalogueEntry hero,
        VerifiedHeroResource resource,
        ArtifactContent input,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!inspector.CanInspect(input))
            {
                return FailedResource(
                    hero,
                    resource,
                    input,
                    "INSPECTION_CAPABILITY_UNAVAILABLE",
                    $"Inspector '{inspector.AdapterName}' cannot inspect this compiled resource.");
            }

            var model = await inspector.InspectAsync(input, cancellationToken).ConfigureAwait(false);
            var discovery = await new PreciseComponentDiscoveryService(capabilityAnalyzer)
                .DiscoverAsync(input, model, cancellationToken)
                .ConfigureAwait(false);
            var candidates = discovery.Candidates
                .Select(CreateCandidate)
                .OrderBy(candidate => candidate.CandidateKind, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.DisplayLabel, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
                .ToArray();
            var reasons = candidates
                .SelectMany(candidate => candidate.Capabilities)
                .Where(capability => !string.Equals(capability.Availability, CapabilityAvailability.Available, StringComparison.Ordinal))
                .SelectMany(capability => capability.Reasons)
                .Select(reason => new StructuralCompatibilityReason(reason.Code, reason.Summary))
                .Concat(discovery.LineageDiagnostics.Select(diagnostic =>
                    new StructuralCompatibilityReason(diagnostic.Code, diagnostic.Summary)))
                .GroupBy(reason => reason.Code, StringComparer.Ordinal)
                .Select(group => group.OrderBy(reason => reason.Summary, StringComparer.Ordinal).First())
                .OrderBy(reason => reason.Code, StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length == 0)
            {
                reasons =
                [
                    .. reasons,
                    new StructuralCompatibilityReason(
                        "COMPATIBILITY_NO_CANDIDATES",
                        "No complete material or mesh-lineage candidate was discovered."),
                ];
            }

            var status = candidates.SelectMany(candidate => candidate.Capabilities)
                .Any(capability => string.Equals(capability.Availability, CapabilityAvailability.Available, StringComparison.Ordinal))
                ? CompatibilityScanContract.Supported
                : CompatibilityScanContract.Unsupported;
            return new CompatibilityResourceResult(
                hero.HeroId,
                hero.DisplayName,
                resource.ResourceId,
                resource.DisplayName,
                resource.Role,
                resource.LogicalPath,
                input.ContentHash,
                input.Bytes.Length,
                status,
                CreateSignature(model, candidates, discovery.LineageDiagnostics.Count),
                candidates,
                reasons);
        }
        catch (S2ModKitException exception)
        {
            return FailedResource(hero, resource, input, exception.Error.Code, exception.Error.Summary);
        }
    }

    private static CompatibilityCandidateResult CreateCandidate(ComponentCandidateV2 candidate)
    {
        var materials = candidate switch
        {
            MaterialGroupComponentCandidateV2 material => new[] { material.MaterialPath },
            MeshLineageComponentCandidateV2 lineage => lineage.MaterialPaths.ToArray(),
            _ => [],
        };
        var lods = candidate switch
        {
            MaterialGroupComponentCandidateV2 material => material.Lods.Select(lod => lod.Lod),
            MeshLineageComponentCandidateV2 lineage => lineage.Lods.Select(lod => lod.Lod),
            _ => [],
        };
        var capabilities = candidate.Capabilities
            .OrderBy(capability => capability.OperationKind, StringComparer.Ordinal)
            .ThenBy(capability => capability.OperationVersion)
            .Select(capability => new CompatibilityCapabilityResult(
                capability.OperationKind,
                capability.OperationVersion,
                capability.Availability,
                capability.Reasons
                    .OrderBy(reason => reason.Code, StringComparer.Ordinal)
                    .ThenBy(reason => reason.Summary, StringComparer.Ordinal)
                    .Select(reason => new StructuralCompatibilityReason(reason.Code, reason.Summary))
                    .ToArray()))
            .ToArray();
        return new CompatibilityCandidateResult(
            candidate.CandidateId,
            candidate.Kind,
            candidate.DisplayLabel,
            materials.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            lods.Distinct().Order().ToArray(),
            capabilities);
    }

    private static StructuralSignature CreateSignature(
        ModelSnapshot model,
        IReadOnlyList<CompatibilityCandidateResult> candidates,
        int lineageDiagnosticCount)
    {
        var lods = model.Lods.OrderBy(lod => lod.Level).ToArray();
        return StructuralSignature.Create(
            "component-discovery-v2",
            [
                new StructuralFact("candidate.lineage.count", candidates.Count(candidate => candidate.CandidateKind == ComponentDiscoveryV2Contract.MeshLineageKind).ToString(CultureInfo.InvariantCulture)),
                new StructuralFact("candidate.material.count", candidates.Count(candidate => candidate.CandidateKind == ComponentDiscoveryV2Contract.MaterialGroupKind).ToString(CultureInfo.InvariantCulture)),
                new StructuralFact("draw-call.count", lods.SelectMany(lod => lod.Meshes).Sum(mesh => mesh.DrawCalls.Count).ToString(CultureInfo.InvariantCulture)),
                new StructuralFact("lineage-diagnostic.count", lineageDiagnosticCount.ToString(CultureInfo.InvariantCulture)),
                new StructuralFact("lod.count", lods.Length.ToString(CultureInfo.InvariantCulture)),
                new StructuralFact("lod.levels", string.Join(",", lods.Select(lod => lod.Level.ToString(CultureInfo.InvariantCulture)))),
                new StructuralFact("mesh.count", lods.Sum(lod => lod.Meshes.Count).ToString(CultureInfo.InvariantCulture)),
            ]);
    }

    private static CompatibilityResourceResult FailedResource(
        VerifiedHeroCatalogueEntry hero,
        VerifiedHeroResource resource,
        ArtifactContent input,
        string reasonCode,
        string summary) => new(
            hero.HeroId,
            hero.DisplayName,
            resource.ResourceId,
            resource.DisplayName,
            resource.Role,
            resource.LogicalPath,
            input.ContentHash,
            input.Bytes.Length,
            CompatibilityScanContract.InspectionFailed,
            null,
            [],
            [new StructuralCompatibilityReason(reasonCode, summary)]);
}
