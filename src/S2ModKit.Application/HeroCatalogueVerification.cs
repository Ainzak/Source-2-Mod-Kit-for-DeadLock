using S2ModKit.Domain;

namespace S2ModKit.Application;

public static class HeroCatalogueVerificationContract
{
    public const int SchemaVersion = 1;

    public const string SourceVerified = "verified";

    public const string SourceUnpinned = "unpinned";

    public const string SourceStale = "stale";

    public const string ResourceVerified = "verified";

    public const string ResourceMissing = "missing";

    public const string ResourceMoved = "moved";

    public const string ResourceAmbiguous = "ambiguous";

    public const string ResourceDuplicate = "duplicate";
}

public sealed record HeroCatalogueSourceVerification(
    string Status,
    ContentHash ActualDirectoryHash,
    ContentHash? ExpectedDirectoryHash);

public sealed record VerifiedHeroResource(
    string ResourceId,
    string DisplayName,
    string Role,
    string LogicalPath,
    bool Optional,
    string QualificationStatus,
    string VerificationStatus,
    long? Size,
    IReadOnlyList<string> CandidateLogicalPaths);

public sealed record VerifiedHeroCatalogueEntry(
    string HeroId,
    string DisplayName,
    IReadOnlyList<string> Aliases,
    string RosterStatus,
    IReadOnlyList<VerifiedHeroResource> Resources);

public sealed record HeroCatalogueVerification(
    int SchemaVersion,
    string CatalogueId,
    string Revision,
    HeroCatalogueSourceVerification Source,
    IReadOnlyList<VerifiedHeroCatalogueEntry> Heroes);

public sealed record HeroCatalogueResolution(
    int SchemaVersion,
    string CatalogueId,
    string Revision,
    HeroCatalogueSourceVerification Source,
    VerifiedHeroCatalogueEntry Hero);

public static class HeroCatalogueVerifier
{
    public static async Task<HeroCatalogueVerification> VerifyAsync(
        HeroCatalogueDocument catalogue,
        IResourceCatalogInventory inventory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(inventory);
        HeroCatalogueValidator.Validate(catalogue);
        if (!string.Equals(inventory.Descriptor.SourceKind, "vpk", StringComparison.Ordinal))
        {
            throw Errors.Input(
                "CATALOGUE_SOURCE_KIND_MISMATCH",
                $"Hero catalogue verification requires a VPK inventory, not '{inventory.Descriptor.SourceKind}'.",
                "Provide the Deadlock base *_dir.vpk archive.");
        }

        var indexedEntries = await inventory.ListEntriesAsync(cancellationToken).ConfigureAwait(false);
        var entriesByPath = indexedEntries
            .GroupBy(entry => entry.LogicalPath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var entriesByFileName = indexedEntries
            .GroupBy(entry => GetFileName(entry.LogicalPath), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(entry => entry.LogicalPath, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        var heroes = catalogue.Heroes
            .OrderBy(hero => hero.DisplayName, StringComparer.Ordinal)
            .ThenBy(hero => hero.HeroId, StringComparer.Ordinal)
            .Select(hero => VerifyHero(hero, entriesByPath, entriesByFileName))
            .ToArray();
        var sourceStatus = catalogue.Source.ExpectedDirectoryHash switch
        {
            null => HeroCatalogueVerificationContract.SourceUnpinned,
            { } expected when expected == inventory.SourceContentHash => HeroCatalogueVerificationContract.SourceVerified,
            _ => HeroCatalogueVerificationContract.SourceStale,
        };

        return new HeroCatalogueVerification(
            HeroCatalogueVerificationContract.SchemaVersion,
            catalogue.CatalogueId,
            catalogue.Revision,
            new HeroCatalogueSourceVerification(
                sourceStatus,
                inventory.SourceContentHash,
                catalogue.Source.ExpectedDirectoryHash),
            heroes);
    }

    public static void RequireCurrent(HeroCatalogueVerification verification)
    {
        ArgumentNullException.ThrowIfNull(verification);
        if (string.Equals(verification.Source.Status, HeroCatalogueVerificationContract.SourceStale, StringComparison.Ordinal))
        {
            throw Errors.Input(
                "CATALOGUE_SOURCE_STALE",
                "The selected base VPK does not match the catalogue's expected directory identity.",
                "Use the matching game build or deliberately update and re-verify the catalogue revision.");
        }

        var problem = verification.Heroes
            .SelectMany(hero => hero.Resources.Select(resource => (Hero: hero, Resource: resource)))
            .Where(item => !string.Equals(
                item.Resource.VerificationStatus,
                HeroCatalogueVerificationContract.ResourceVerified,
                StringComparison.Ordinal))
            .OrderBy(item => VerificationOrder(item.Resource.VerificationStatus))
            .ThenBy(item => item.Hero.DisplayName, StringComparer.Ordinal)
            .ThenBy(item => item.Resource.DisplayName, StringComparer.Ordinal)
            .FirstOrDefault();
        if (problem == default)
        {
            return;
        }

        var candidateSuffix = problem.Resource.CandidateLogicalPaths.Count == 0
            ? string.Empty
            : $" Candidates: {string.Join(", ", problem.Resource.CandidateLogicalPaths)}.";
        var (code, remediation) = problem.Resource.VerificationStatus switch
        {
            HeroCatalogueVerificationContract.ResourceDuplicate => (
                "CATALOGUE_RESOURCE_DUPLICATE",
                "Use an intact base VPK containing one normalized entry per logical path."),
            HeroCatalogueVerificationContract.ResourceAmbiguous => (
                "CATALOGUE_RESOURCE_AMBIGUOUS",
                "Choose the intended logical path and update the catalogue revision."),
            HeroCatalogueVerificationContract.ResourceMoved => (
                "CATALOGUE_RESOURCE_MOVED",
                "Review the suggested path and update the catalogue revision if it is the same resource."),
            _ => (
                "CATALOGUE_RESOURCE_MISSING",
                "Use the matching game build or update the catalogue with the verified current locator."),
        };
        throw Errors.Input(
            code,
            $"{problem.Hero.DisplayName} resource '{problem.Resource.DisplayName}' is {problem.Resource.VerificationStatus} at '{problem.Resource.LogicalPath}'.{candidateSuffix}",
            remediation);
    }

    private static VerifiedHeroCatalogueEntry VerifyHero(
        HeroCatalogueEntry hero,
        IReadOnlyDictionary<string, ResourceCatalogEntry[]> entriesByPath,
        IReadOnlyDictionary<string, ResourceCatalogEntry[]> entriesByFileName)
    {
        var resources = hero.Resources
            .OrderBy(resource => resource.DisplayName, StringComparer.Ordinal)
            .ThenBy(resource => resource.ResourceId, StringComparer.Ordinal)
            .Select(resource => VerifyResource(resource, entriesByPath, entriesByFileName))
            .ToArray();
        return new VerifiedHeroCatalogueEntry(
            hero.HeroId,
            hero.DisplayName,
            hero.Aliases.OrderBy(alias => alias, StringComparer.Ordinal).ToArray(),
            hero.RosterStatus,
            resources);
    }

    private static VerifiedHeroResource VerifyResource(
        HeroResourceLocator resource,
        IReadOnlyDictionary<string, ResourceCatalogEntry[]> entriesByPath,
        IReadOnlyDictionary<string, ResourceCatalogEntry[]> entriesByFileName)
    {
        entriesByPath.TryGetValue(resource.LogicalPath, out var exactEntries);
        string status;
        long? size = null;
        string[] candidates = [];
        if (exactEntries is { Length: 1 })
        {
            status = HeroCatalogueVerificationContract.ResourceVerified;
            size = exactEntries[0].Size;
        }
        else if (exactEntries is { Length: > 1 })
        {
            status = HeroCatalogueVerificationContract.ResourceDuplicate;
            candidates = exactEntries.Select(entry => entry.LogicalPath).ToArray();
        }
        else
        {
            entriesByFileName.TryGetValue(GetFileName(resource.LogicalPath), out var movedEntries);
            candidates = movedEntries?
                .Select(entry => entry.LogicalPath)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray() ?? [];
            status = candidates.Length switch
            {
                0 => HeroCatalogueVerificationContract.ResourceMissing,
                1 => HeroCatalogueVerificationContract.ResourceMoved,
                _ => HeroCatalogueVerificationContract.ResourceAmbiguous,
            };
        }

        return new VerifiedHeroResource(
            resource.ResourceId,
            resource.DisplayName,
            resource.Role,
            resource.LogicalPath,
            resource.Optional,
            resource.QualificationStatus,
            status,
            size,
            candidates);
    }

    private static string GetFileName(string logicalPath)
    {
        var separator = logicalPath.LastIndexOf('/');
        return separator < 0 ? logicalPath : logicalPath[(separator + 1)..];
    }

    private static int VerificationOrder(string status) => status switch
    {
        HeroCatalogueVerificationContract.ResourceDuplicate => 0,
        HeroCatalogueVerificationContract.ResourceAmbiguous => 1,
        HeroCatalogueVerificationContract.ResourceMoved => 2,
        _ => 3,
    };
}

public static class HeroCatalogueQueries
{
    public static async Task<HeroCatalogueVerification> ListAsync(
        HeroCatalogueDocument catalogue,
        IResourceCatalogInventory inventory,
        CancellationToken cancellationToken = default)
    {
        var verification = await HeroCatalogueVerifier.VerifyAsync(catalogue, inventory, cancellationToken)
            .ConfigureAwait(false);
        HeroCatalogueVerifier.RequireCurrent(verification);
        return verification;
    }

    public static async Task<HeroCatalogueResolution> ResolveAsync(
        HeroCatalogueDocument catalogue,
        IResourceCatalogInventory inventory,
        string heroQuery,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heroQuery);
        var verification = await HeroCatalogueVerifier.VerifyAsync(catalogue, inventory, cancellationToken)
            .ConfigureAwait(false);
        HeroCatalogueVerifier.RequireCurrent(verification);
        var lookupKey = HeroCatalogueValidator.NormalizeLookupKey(heroQuery);
        var sourceHero = catalogue.Heroes.SingleOrDefault(hero =>
            string.Equals(hero.HeroId, lookupKey, StringComparison.Ordinal)
            || string.Equals(HeroCatalogueValidator.NormalizeLookupKey(hero.DisplayName), lookupKey, StringComparison.Ordinal)
            || hero.Aliases.Contains(lookupKey, StringComparer.Ordinal));
        if (sourceHero is null)
        {
            throw Errors.Input(
                "CATALOGUE_HERO_NOT_FOUND",
                $"No hero matches '{heroQuery}'.",
                "Run 's2mod catalog heroes list' and use a listed hero ID, display name, or alias.");
        }

        var resolved = verification.Heroes.Single(hero =>
            string.Equals(hero.HeroId, sourceHero.HeroId, StringComparison.Ordinal));
        return new HeroCatalogueResolution(
            HeroCatalogueVerificationContract.SchemaVersion,
            verification.CatalogueId,
            verification.Revision,
            verification.Source,
            resolved);
    }
}
