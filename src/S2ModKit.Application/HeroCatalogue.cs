using System.Text;
using System.Text.Json;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public static class HeroCatalogueContract
{
    public const int SchemaVersion = 1;

    public const int MaximumHeroes = 128;

    public const int MaximumResourcesPerHero = 64;

    public const int MaximumAliasesPerHero = 32;

    public const string GameId = "deadlock";

    public const string BaseVpkSourceKind = "base_vpk";

    public const string ActiveRosterStatus = "active";

    public const string ExperimentalRosterStatus = "experimental";

    public const string UnavailableRosterStatus = "unavailable";

    public const string PrimaryModelRole = "primary_model";

    public const string AccessoryModelRole = "accessory_model";

    public const string VariantModelRole = "variant_model";

    public const string Unqualified = "unqualified";

    public const string ReadOnlyQualified = "read_only_qualified";

    public const string OfflineStatic = "offline_static";

    public const string RuntimeQualified = "runtime_qualified";

    public static bool IsRosterStatus(string value) =>
        value is ActiveRosterStatus or ExperimentalRosterStatus or UnavailableRosterStatus;

    public static bool IsResourceRole(string value) =>
        value is PrimaryModelRole or AccessoryModelRole or VariantModelRole;

    public static bool IsQualificationStatus(string value) =>
        value is Unqualified or ReadOnlyQualified or OfflineStatic or RuntimeQualified;
}

public sealed record HeroCatalogueDocument
{
    public int SchemaVersion { get; init; } = HeroCatalogueContract.SchemaVersion;

    public string CatalogueId { get; init; } = string.Empty;

    public string Revision { get; init; } = string.Empty;

    public required HeroCatalogueSourceProvenance Source { get; init; }

    public IReadOnlyList<HeroCatalogueEntry> Heroes { get; init; } = [];

    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } =
        new Dictionary<string, JsonElement>();
}

public sealed record HeroCatalogueSourceProvenance
{
    public string GameId { get; init; } = HeroCatalogueContract.GameId;

    public string SourceKind { get; init; } = HeroCatalogueContract.BaseVpkSourceKind;

    public string? GameBuild { get; init; }

    public ContentHash? ExpectedDirectoryHash { get; init; }
}

public sealed record HeroCatalogueEntry
{
    public string HeroId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public string RosterStatus { get; init; } = HeroCatalogueContract.ActiveRosterStatus;

    public IReadOnlyList<HeroResourceLocator> Resources { get; init; } = [];

    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } =
        new Dictionary<string, JsonElement>();
}

public sealed record HeroResourceLocator
{
    public string ResourceId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public string LogicalPath { get; init; } = string.Empty;

    public bool Optional { get; init; }

    public string QualificationStatus { get; init; } = HeroCatalogueContract.Unqualified;

    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } =
        new Dictionary<string, JsonElement>();
}

public static class HeroCatalogueValidator
{
    private const int MaximumDisplayTextLength = 128;
    private const int MaximumRevisionLength = 128;

    public static void Validate(HeroCatalogueDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != HeroCatalogueContract.SchemaVersion)
        {
            throw Invalid(
                "CATALOGUE_SCHEMA_UNSUPPORTED",
                $"Hero catalogue schema version must be {HeroCatalogueContract.SchemaVersion}.");
        }

        RequirePortableId(document.CatalogueId, "CATALOGUE_ID_INVALID", "catalogueId");
        RequireRevision(document.Revision);
        ValidateSource(document.Source);
        if (document.Heroes is null
            || document.Heroes.Count is 0 or > HeroCatalogueContract.MaximumHeroes)
        {
            throw Invalid(
                "CATALOGUE_HERO_COUNT_INVALID",
                $"A hero catalogue must contain 1-{HeroCatalogueContract.MaximumHeroes} heroes.");
        }

        var heroIds = new HashSet<string>(StringComparer.Ordinal);
        var lookupOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var resourceIds = new HashSet<string>(StringComparer.Ordinal);
        var resourcePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hero in document.Heroes)
        {
            ValidateHero(hero, heroIds, lookupOwners, resourceIds, resourcePaths);
        }
    }

    public static string NormalizeLookupKey(string value)
    {
        var normalized = NormalizeDisplayText(value, "lookup key").ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        var previousWasSpace = false;
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasSpace)
                {
                    builder.Append(character);
                }

                previousWasSpace = true;
                continue;
            }

            previousWasSpace = false;
            builder.Append(character);
        }

        return builder.ToString();
    }

    private static void ValidateSource(HeroCatalogueSourceProvenance source)
    {
        if (source is null
            || !string.Equals(source.GameId, HeroCatalogueContract.GameId, StringComparison.Ordinal)
            || !string.Equals(source.SourceKind, HeroCatalogueContract.BaseVpkSourceKind, StringComparison.Ordinal))
        {
            throw Invalid(
                "CATALOGUE_SOURCE_INVALID",
                "Catalogue source must identify the portable Deadlock base-VPK source contract.");
        }

        if (source.GameBuild is not null)
        {
            var gameBuild = NormalizeDisplayText(source.GameBuild, "gameBuild");
            if (!string.Equals(gameBuild, source.GameBuild, StringComparison.Ordinal))
            {
                throw Invalid("CATALOGUE_GAME_BUILD_INVALID", "Catalogue gameBuild is not canonical.");
            }
        }

        if (source.ExpectedDirectoryHash is ContentHash expectedDirectoryHash
            && string.IsNullOrEmpty(expectedDirectoryHash.Value))
        {
            throw Invalid("CATALOGUE_SOURCE_INVALID", "Expected directory hash is not a valid content identity.");
        }
    }

    private static void ValidateHero(
        HeroCatalogueEntry hero,
        HashSet<string> heroIds,
        Dictionary<string, string> lookupOwners,
        HashSet<string> resourceIds,
        HashSet<string> resourcePaths)
    {
        if (hero is null)
        {
            throw Invalid("CATALOGUE_HERO_INVALID", "Hero catalogue entries cannot be null.");
        }

        RequirePortableId(hero.HeroId, "CATALOGUE_HERO_ID_INVALID", "heroId");
        if (!heroIds.Add(hero.HeroId))
        {
            throw Invalid("CATALOGUE_HERO_ID_DUPLICATE", $"Hero ID '{hero.HeroId}' appears more than once.");
        }

        var displayName = NormalizeDisplayText(hero.DisplayName, "displayName");
        if (!string.Equals(displayName, hero.DisplayName, StringComparison.Ordinal))
        {
            throw Invalid("CATALOGUE_HERO_NAME_INVALID", $"Hero '{hero.HeroId}' display name is not canonical.");
        }

        if (!HeroCatalogueContract.IsRosterStatus(hero.RosterStatus))
        {
            throw Invalid("CATALOGUE_ROSTER_STATUS_INVALID", $"Hero '{hero.HeroId}' has an unknown roster status.");
        }

        AddLookupKey(lookupOwners, hero.HeroId, hero.HeroId);
        AddLookupKey(lookupOwners, NormalizeLookupKey(hero.DisplayName), hero.HeroId);
        ValidateAliases(hero, lookupOwners);
        ValidateResources(hero, resourceIds, resourcePaths);
    }

    private static void ValidateAliases(HeroCatalogueEntry hero, Dictionary<string, string> lookupOwners)
    {
        if (hero.Aliases is null || hero.Aliases.Count > HeroCatalogueContract.MaximumAliasesPerHero)
        {
            throw Invalid(
                "CATALOGUE_ALIAS_COUNT_INVALID",
                $"Hero '{hero.HeroId}' exceeds the {HeroCatalogueContract.MaximumAliasesPerHero}-alias limit.");
        }

        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var ownKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            hero.HeroId,
            NormalizeLookupKey(hero.DisplayName),
        };
        foreach (var alias in hero.Aliases)
        {
            var normalized = NormalizeLookupKey(alias);
            if (!string.Equals(alias, normalized, StringComparison.Ordinal))
            {
                throw Invalid("CATALOGUE_ALIAS_INVALID", $"Alias '{alias}' for hero '{hero.HeroId}' is not canonical.");
            }

            if (!aliases.Add(normalized) || ownKeys.Contains(normalized))
            {
                throw Invalid("CATALOGUE_ALIAS_DUPLICATE", $"Alias '{alias}' is redundant for hero '{hero.HeroId}'.");
            }

            AddLookupKey(lookupOwners, normalized, hero.HeroId);
        }
    }

    private static void ValidateResources(
        HeroCatalogueEntry hero,
        HashSet<string> resourceIds,
        HashSet<string> resourcePaths)
    {
        if (hero.Resources is null
            || hero.Resources.Count is 0 or > HeroCatalogueContract.MaximumResourcesPerHero)
        {
            throw Invalid(
                "CATALOGUE_RESOURCE_COUNT_INVALID",
                $"Hero '{hero.HeroId}' must contain 1-{HeroCatalogueContract.MaximumResourcesPerHero} resources.");
        }

        var primaryCount = 0;
        foreach (var resource in hero.Resources)
        {
            if (resource is null)
            {
                throw Invalid("CATALOGUE_RESOURCE_INVALID", $"Hero '{hero.HeroId}' contains a null resource.");
            }

            RequirePortableId(resource.ResourceId, "CATALOGUE_RESOURCE_ID_INVALID", "resourceId");
            if (!resourceIds.Add(resource.ResourceId))
            {
                throw Invalid(
                    "CATALOGUE_RESOURCE_ID_DUPLICATE",
                    $"Resource ID '{resource.ResourceId}' appears more than once.");
            }

            var displayName = NormalizeDisplayText(resource.DisplayName, "resource displayName");
            if (!string.Equals(displayName, resource.DisplayName, StringComparison.Ordinal))
            {
                throw Invalid(
                    "CATALOGUE_RESOURCE_NAME_INVALID",
                    $"Resource '{resource.ResourceId}' display name is not canonical.");
            }

            if (!HeroCatalogueContract.IsResourceRole(resource.Role))
            {
                throw Invalid("CATALOGUE_RESOURCE_ROLE_INVALID", $"Resource '{resource.ResourceId}' has an unknown role.");
            }

            ValidateLogicalPath(resource.ResourceId, resource.LogicalPath);
            if (!resourcePaths.Add(resource.LogicalPath))
            {
                throw Invalid(
                    "CATALOGUE_RESOURCE_PATH_DUPLICATE",
                    $"Logical path '{resource.LogicalPath}' appears more than once.");
            }

            if (!HeroCatalogueContract.IsQualificationStatus(resource.QualificationStatus))
            {
                throw Invalid(
                    "CATALOGUE_QUALIFICATION_STATUS_INVALID",
                    $"Resource '{resource.ResourceId}' has an unknown qualification status.");
            }

            if (string.Equals(resource.Role, HeroCatalogueContract.PrimaryModelRole, StringComparison.Ordinal))
            {
                primaryCount++;
                if (resource.Optional)
                {
                    throw Invalid(
                        "CATALOGUE_PRIMARY_MODEL_INVALID",
                        $"Primary model '{resource.ResourceId}' cannot be optional.");
                }
            }
        }

        if (primaryCount != 1)
        {
            throw Invalid(
                "CATALOGUE_PRIMARY_MODEL_INVALID",
                $"Hero '{hero.HeroId}' must declare exactly one non-optional primary model.");
        }
    }

    private static void AddLookupKey(Dictionary<string, string> lookupOwners, string key, string heroId)
    {
        if (lookupOwners.TryGetValue(key, out var existingOwner)
            && !string.Equals(existingOwner, heroId, StringComparison.Ordinal))
        {
            throw Invalid(
                "CATALOGUE_LOOKUP_COLLISION",
                $"Lookup key '{key}' maps to both '{existingOwner}' and '{heroId}'.");
        }

        lookupOwners[key] = heroId;
    }

    private static void ValidateLogicalPath(string resourceId, string logicalPath)
    {
        if (string.IsNullOrWhiteSpace(logicalPath))
        {
            throw Invalid(
                "CATALOGUE_RESOURCE_PATH_INVALID",
                $"Resource '{resourceId}' must use a canonical relative .vmdl_c logical path.");
        }

        var normalized = StableIdentity.NormalizePath(logicalPath);
        var segments = normalized.Split('/');
        if (!string.Equals(logicalPath, normalized, StringComparison.Ordinal)
            || logicalPath.Contains(':', StringComparison.Ordinal)
            || segments.Any(static segment => segment is "" or "." or "..")
            || !logicalPath.EndsWith(".vmdl_c", StringComparison.Ordinal))
        {
            throw Invalid(
                "CATALOGUE_RESOURCE_PATH_INVALID",
                $"Resource '{resourceId}' must use a canonical relative .vmdl_c logical path.");
        }
    }

    private static void RequirePortableId(string value, string code, string fieldName)
    {
        var invalid = string.IsNullOrEmpty(value)
            || value.Length > 64
            || !char.IsAsciiLetter(value[0])
            || value.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
            || !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal);
        var previousWasSeparator = false;
        for (var index = 0; !invalid && index < value.Length; index++)
        {
            var isSeparator = value[index] is '.' or '_' or '-';
            invalid = isSeparator && (previousWasSeparator || index == value.Length - 1);
            previousWasSeparator = isSeparator;
        }

        if (invalid)
        {
            throw Invalid(code, $"{fieldName} must be a canonical lower-case portable ID.");
        }
    }

    private static void RequireRevision(string revision)
    {
        if (string.IsNullOrWhiteSpace(revision)
            || revision.Length > MaximumRevisionLength
            || !char.IsAsciiLetterOrDigit(revision[0])
            || revision.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw Invalid("CATALOGUE_REVISION_INVALID", "Catalogue revision is not a portable revision identifier.");
        }
    }

    private static string NormalizeDisplayText(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid("CATALOGUE_TEXT_INVALID", $"{fieldName} cannot be empty.");
        }

        string normalized;
        try
        {
            normalized = value.Trim().Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException exception)
        {
            throw Invalid("CATALOGUE_TEXT_INVALID", $"{fieldName} is not valid Unicode.", exception);
        }

        if (normalized.Length is 0 or > MaximumDisplayTextLength || normalized.Any(char.IsControl))
        {
            throw Invalid(
                "CATALOGUE_TEXT_INVALID",
                $"{fieldName} must contain 1-{MaximumDisplayTextLength} non-control characters.");
        }

        return normalized;
    }

    private static S2ModKitException Invalid(string code, string summary, Exception? innerException = null) =>
        new(
            new S2Error(
                code,
                "catalogue",
                summary,
                "Correct the catalogue document and validate it against hero-catalogue.schema.json.",
                ErrorCategory.CliOrSchema),
            innerException);
}
