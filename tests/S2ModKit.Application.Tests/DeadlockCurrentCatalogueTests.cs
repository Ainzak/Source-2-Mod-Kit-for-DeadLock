using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed class DeadlockCurrentCatalogueTests
{
    private const string Revision = "2026-10-07.1";
    private static readonly ContentHash PinnedDirectoryHash =
        new("76bc769d1c56e995bb535e4684c33646b55cc62d7a9198be5b0d075baf09f6b6");

    private static readonly (string HeroId, string DisplayName, string Alias, string LogicalPath, int SourceHeroId)[] AnnouncedHeroes =
    [
        ("baba", "Baba", "", "models/heroes_wip/baba/baba.vmdl_c", 88),
        ("deadman-danny", "Deadman Danny", "deadpack", "models/heroes_wip/deadpack/deadpack.vmdl_c", 78),
        ("nurse-harrow", "Nurse Harrow", "nurse", "models/heroes_wip/nurse/nurse.vmdl_c", 87),
        ("rat-king", "Rat King", "ratking", "models/heroes_wip/ratking/ratking.vmdl_c", 84),
        ("solomon", "Solomon", "chessmaster", "models/heroes_wip/chessmaster/chessmaster.vmdl_c", 85),
        ("violet", "Violet", "artist", "models/heroes_wip/artist/artist.vmdl_c", 86),
    ];

    private static readonly string[] PreviouslyPublishedResourceIds =
    [
        "abrams.primary", "apollo.primary", "bebop.primary", "billy.primary", "calico.primary",
        "doorman.primary", "drifter.primary", "dynamo.primary", "familiar.primary", "graves.primary",
        "grey-talon.primary", "haze.primary", "holliday.hat", "holliday.primary", "infernus.primary",
        "ivy.primary", "kelvin.primary", "lady-geist.primary", "lash.primary", "mcginnis.primary",
        "mina.primary", "mirage.primary", "mo-and-krill.primary", "paige.primary", "paradox.primary",
        "pocket.primary", "priest.primary", "seven.primary", "shiv.primary", "sinclair.primary",
        "unicorn.primary", "victor.primary", "vindicta.primary", "viscous.primary", "vyper.primary",
        "warden.primary", "werewolf.primary", "wraith.primary", "yamato.primary",
    ];

    [Fact]
    public async Task PackagedCatalogueValidatesAgainstSchemaAndRuntimeValidator()
    {
        var document = LoadCatalogue();
        var schema = await JsonSchema.FromFileAsync(
            GetSchemaPath(), TestContext.Current.CancellationToken);

        HeroCatalogueValidator.Validate(document);

        Assert.Empty(schema.Validate(JsonDefaults.Serialize(document)));
        Assert.Equal(HeroCatalogueContract.SchemaVersion, document.SchemaVersion);
        Assert.Equal("deadlock.current", document.CatalogueId);
        Assert.Equal(Revision, document.Revision);
        Assert.Equal(PinnedDirectoryHash, document.Source.ExpectedDirectoryHash);
    }

    [Fact]
    public void CatalogueCountsRemainFortyFourHeroesAndFortyFiveResources()
    {
        var document = LoadCatalogue();

        Assert.Equal(44, document.Heroes.Count);
        Assert.Equal(45, document.Heroes.Sum(hero => hero.Resources.Count));
        Assert.Equal(39, document.Heroes.Count(hero => hero.RosterStatus == HeroCatalogueContract.ActiveRosterStatus));
    }

    [Fact]
    public void AnnouncedHeroesKeepExactMappingsAndRatKingIsNowActive()
    {
        var document = LoadCatalogue();

        foreach (var (heroId, displayName, alias, logicalPath, sourceHeroId) in AnnouncedHeroes)
        {
            var hero = Assert.Single(document.Heroes, candidate => candidate.HeroId == heroId);
            Assert.Equal(displayName, hero.DisplayName);
            Assert.Equal(heroId == "rat-king" ? HeroCatalogueContract.ActiveRosterStatus
                : HeroCatalogueContract.ExperimentalRosterStatus, hero.RosterStatus);
            Assert.Equal(sourceHeroId, hero.Extensions["sourceHeroId"].GetInt32());

            var resource = Assert.Single(hero.Resources);
            Assert.Equal($"{heroId}.primary", resource.ResourceId);
            Assert.Equal(displayName, resource.DisplayName);
            Assert.Equal(HeroCatalogueContract.PrimaryModelRole, resource.Role);
            Assert.Equal(logicalPath, resource.LogicalPath);
            Assert.False(resource.Optional);
            Assert.Equal(HeroCatalogueContract.Unqualified, resource.QualificationStatus);

            if (alias.Length == 0)
            {
                Assert.Empty(hero.Aliases);
            }
            else
            {
                Assert.Equal([alias], hero.Aliases);
            }
        }
    }

    [Fact]
    public void AnnouncedHeroAliasesResolveThroughTheCatalogueLookupRule()
    {
        var document = LoadCatalogue();

        foreach (var (heroId, displayName, alias, _, _) in AnnouncedHeroes)
        {
            Assert.Equal(heroId, Resolve(document, heroId)!.HeroId);
            Assert.Equal(heroId, Resolve(document, displayName)!.HeroId);
            if (alias.Length > 0)
            {
                Assert.Equal(heroId, Resolve(document, alias)!.HeroId);
            }
        }

        Assert.Null(Resolve(document, "not-a-catalogued-hero"));
    }

    [Fact]
    public void QualificationStatusesReflectCurrentBuildEvidenceOnly()
    {
        var document = LoadCatalogue();
        var resources = document.Heroes.SelectMany(hero => hero.Resources).ToArray();

        Assert.All(resources, resource =>
            Assert.Equal(HeroCatalogueContract.Unqualified, resource.QualificationStatus));
    }

    [Fact]
    public void PreviouslyPublishedResourceIdsRemainPresent()
    {
        var document = LoadCatalogue();
        var resourceIds = document.Heroes
            .SelectMany(hero => hero.Resources)
            .Select(resource => resource.ResourceId)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Subset(resourceIds, PreviouslyPublishedResourceIds.ToHashSet(StringComparer.Ordinal));
        Assert.Equal(45, resourceIds.Count);
    }

    private static HeroCatalogueDocument LoadCatalogue()
    {
        return JsonDefaults.Deserialize<HeroCatalogueDocument>(
            File.ReadAllBytes(GetRepositoryPath("catalogues", "deadlock-current.json")),
            "Deadlock current catalogue");
    }

    private static HeroCatalogueEntry? Resolve(HeroCatalogueDocument document, string heroQuery)
    {
        var lookupKey = HeroCatalogueValidator.NormalizeLookupKey(heroQuery);
        return document.Heroes.SingleOrDefault(hero =>
            string.Equals(hero.HeroId, lookupKey, StringComparison.Ordinal)
            || string.Equals(HeroCatalogueValidator.NormalizeLookupKey(hero.DisplayName), lookupKey, StringComparison.Ordinal)
            || hero.Aliases.Contains(lookupKey, StringComparer.Ordinal));
    }

    private static string GetSchemaPath()
    {
        var directory = FindRepositoryRoot();
        return Path.Combine(directory, "schemas", "hero-catalogue.schema.json");
    }

    private static string GetRepositoryPath(params string[] segments)
    {
        return Path.Combine([FindRepositoryRoot(), .. segments]);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
