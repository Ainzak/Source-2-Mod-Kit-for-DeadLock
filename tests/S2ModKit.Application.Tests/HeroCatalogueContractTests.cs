using System.Text;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed class HeroCatalogueContractTests
{
    [Fact]
    public async Task ValidCatalogueConformsToSchemaAndRuntimeValidator()
    {
        var document = ValidCatalogue();
        var schema = await JsonSchema.FromFileAsync(GetSchemaPath(), TestContext.Current.CancellationToken);
        var json = JsonDefaults.Serialize(document);

        HeroCatalogueValidator.Validate(document);

        Assert.Empty(schema.Validate(json));
        var parsed = JsonDefaults.Deserialize<HeroCatalogueDocument>(Encoding.UTF8.GetBytes(json), "Hero catalogue");
        HeroCatalogueValidator.Validate(parsed);
        Assert.Equal("deadlock.heroes", parsed.CatalogueId);
        Assert.Equal(2, parsed.Heroes.Count);
    }

    [Fact]
    public async Task SchemaRejectsUnknownPropertiesAndQualificationValues()
    {
        var schema = await JsonSchema.FromFileAsync(GetSchemaPath(), TestContext.Current.CancellationToken);
        var json = JsonDefaults.Serialize(ValidCatalogue());
        var unknown = json.Replace(
            "\"extensions\": {}",
            "\"unexpected\": true, \"extensions\": {}",
            StringComparison.Ordinal);
        var unsupportedStatus = json.Replace(
            HeroCatalogueContract.RuntimeQualified,
            "probably_works",
            StringComparison.Ordinal);

        Assert.NotEmpty(schema.Validate(unknown));
        Assert.NotEmpty(schema.Validate(unsupportedStatus));
    }

    [Fact]
    public void ValidatorRejectsCrossHeroLookupCollision()
    {
        var document = ValidCatalogue();
        var second = document.Heroes[1] with { Aliases = ["haze"] };

        var exception = Assert.Throws<S2ModKitException>(() =>
            HeroCatalogueValidator.Validate(document with { Heroes = [document.Heroes[0], second] }));

        Assert.Equal("CATALOGUE_LOOKUP_COLLISION", exception.Error.Code);
    }

    [Fact]
    public void ValidatorRejectsMissingRequiredPrimaryModelAndUnsafePath()
    {
        var document = ValidCatalogue();
        var hero = document.Heroes[0];
        var accessoryOnly = hero with
        {
            Resources =
            [
                hero.Resources[0] with
                {
                    Role = HeroCatalogueContract.AccessoryModelRole,
                    Optional = true,
                    LogicalPath = "../haze.vmdl_c",
                },
            ],
        };

        var exception = Assert.Throws<S2ModKitException>(() =>
            HeroCatalogueValidator.Validate(document with { Heroes = [accessoryOnly, document.Heroes[1]] }));

        Assert.Equal("CATALOGUE_RESOURCE_PATH_INVALID", exception.Error.Code);

        accessoryOnly = accessoryOnly with
        {
            Resources =
            [
                accessoryOnly.Resources[0] with
                {
                    LogicalPath = "models/staging/haze/haze_accessory.vmdl_c",
                },
            ],
        };
        exception = Assert.Throws<S2ModKitException>(() =>
            HeroCatalogueValidator.Validate(document with { Heroes = [accessoryOnly, document.Heroes[1]] }));
        Assert.Equal("CATALOGUE_PRIMARY_MODEL_INVALID", exception.Error.Code);

        var optionalPrimary = hero with
        {
            Resources = [hero.Resources[0] with { Optional = true }],
        };
        exception = Assert.Throws<S2ModKitException>(() =>
            HeroCatalogueValidator.Validate(document with { Heroes = [optionalPrimary, document.Heroes[1]] }));
        Assert.Equal("CATALOGUE_PRIMARY_MODEL_INVALID", exception.Error.Code);
    }

    [Fact]
    public void ValidatorRejectsNonPortableIdentityAliasSourceAndQualification()
    {
        var document = ValidCatalogue();

        var exception = Assert.Throws<S2ModKitException>(() =>
            HeroCatalogueValidator.Validate(document with { CatalogueId = "Deadlock.Heroes" }));
        Assert.Equal("CATALOGUE_ID_INVALID", exception.Error.Code);

        var secondHero = document.Heroes[1] with { Aliases = [" Astro "] };
        exception = Assert.Throws<S2ModKitException>(() =>
            HeroCatalogueValidator.Validate(document with { Heroes = [document.Heroes[0], secondHero] }));
        Assert.Equal("CATALOGUE_ALIAS_INVALID", exception.Error.Code);

        exception = Assert.Throws<S2ModKitException>(() => HeroCatalogueValidator.Validate(document with
        {
            Source = document.Source with { SourceKind = "directory" },
        }));
        Assert.Equal("CATALOGUE_SOURCE_INVALID", exception.Error.Code);

        var firstHero = document.Heroes[0] with
        {
            Resources = [document.Heroes[0].Resources[0] with { QualificationStatus = "probably_works" }],
        };
        exception = Assert.Throws<S2ModKitException>(() =>
            HeroCatalogueValidator.Validate(document with { Heroes = [firstHero, document.Heroes[1]] }));
        Assert.Equal("CATALOGUE_QUALIFICATION_STATUS_INVALID", exception.Error.Code);
    }

    [Fact]
    public void ValidatorRejectsDuplicateResourceIdentityAndPath()
    {
        var document = ValidCatalogue();
        var firstResource = document.Heroes[0].Resources[0];
        var secondHero = document.Heroes[1] with
        {
            Resources = [document.Heroes[1].Resources[0] with { ResourceId = firstResource.ResourceId }],
        };

        var duplicateId = Assert.Throws<S2ModKitException>(() =>
            HeroCatalogueValidator.Validate(document with { Heroes = [document.Heroes[0], secondHero] }));
        Assert.Equal("CATALOGUE_RESOURCE_ID_DUPLICATE", duplicateId.Error.Code);

        secondHero = document.Heroes[1] with
        {
            Resources = [document.Heroes[1].Resources[0] with { LogicalPath = firstResource.LogicalPath }],
        };
        var duplicatePath = Assert.Throws<S2ModKitException>(() =>
            HeroCatalogueValidator.Validate(document with { Heroes = [document.Heroes[0], secondHero] }));
        Assert.Equal("CATALOGUE_RESOURCE_PATH_DUPLICATE", duplicatePath.Error.Code);
    }

    [Fact]
    public async Task CatalogueQueryEnvelopesConformToStrictPublishedSchema()
    {
        var schema = await JsonSchema.FromFileAsync(
            GetSchemaPath("hero-catalogue-query.schema.json"),
            TestContext.Current.CancellationToken);
        var hash = ContentHash.Compute("directory"u8);
        var resource = new VerifiedHeroResource(
            "haze.primary",
            "Primary model",
            HeroCatalogueContract.PrimaryModelRole,
            "models/heroes_staging/haze/haze.vmdl_c",
            false,
            HeroCatalogueContract.RuntimeQualified,
            HeroCatalogueVerificationContract.ResourceVerified,
            123,
            []);
        var hero = new VerifiedHeroCatalogueEntry(
            "haze",
            "Haze",
            ["mist"],
            HeroCatalogueContract.ActiveRosterStatus,
            [resource]);
        var source = new HeroCatalogueSourceVerification(
            HeroCatalogueVerificationContract.SourceVerified,
            hash,
            hash);
        var list = new HeroCatalogueVerification(1, "deadlock.heroes", "test.1", source, [hero]);
        var resolve = new HeroCatalogueResolution(1, "deadlock.heroes", "test.1", source, hero);
        var listJson = JsonDefaults.Serialize(new
        {
            SchemaVersion = 1,
            Status = "success",
            Command = "catalog.heroes.list",
            Result = list,
        });
        var resolveJson = JsonDefaults.Serialize(new
        {
            SchemaVersion = 1,
            Status = "success",
            Command = "catalog.resolve",
            Result = resolve,
        });

        Assert.Empty(schema.Validate(listJson));
        Assert.Empty(schema.Validate(resolveJson));
        Assert.NotEmpty(schema.Validate(listJson.Replace(
            "\"candidateLogicalPaths\": []",
            "\"unknown\": true, \"candidateLogicalPaths\": []",
            StringComparison.Ordinal)));
    }

    private static HeroCatalogueDocument ValidCatalogue() => new()
    {
        CatalogueId = "deadlock.heroes",
        Revision = "2026-09-14.1",
        Source = new HeroCatalogueSourceProvenance
        {
            GameBuild = "synthetic-test-build",
            ExpectedDirectoryHash = ContentHash.Compute("directory"u8),
        },
        Heroes =
        [
            new HeroCatalogueEntry
            {
                HeroId = "haze",
                DisplayName = "Haze",
                Aliases = [],
                Resources =
                [
                    new HeroResourceLocator
                    {
                        ResourceId = "haze.primary",
                        DisplayName = "Primary model",
                        Role = HeroCatalogueContract.PrimaryModelRole,
                        LogicalPath = "models/staging/haze/haze.vmdl_c",
                        QualificationStatus = HeroCatalogueContract.RuntimeQualified,
                    },
                ],
            },
            new HeroCatalogueEntry
            {
                HeroId = "holliday",
                DisplayName = "Holliday",
                Aliases = ["astro"],
                Resources =
                [
                    new HeroResourceLocator
                    {
                        ResourceId = "holliday.primary",
                        DisplayName = "Primary model",
                        Role = HeroCatalogueContract.PrimaryModelRole,
                        LogicalPath = "models/staging/astro/astro.vmdl_c",
                        QualificationStatus = HeroCatalogueContract.ReadOnlyQualified,
                    },
                    new HeroResourceLocator
                    {
                        ResourceId = "holliday.hat",
                        DisplayName = "Hat",
                        Role = HeroCatalogueContract.AccessoryModelRole,
                        LogicalPath = "models/staging/astro/astro_hat.vmdl_c",
                        Optional = true,
                        QualificationStatus = HeroCatalogueContract.ReadOnlyQualified,
                    },
                ],
            },
        ],
    };

    private static string GetSchemaPath(string fileName = "hero-catalogue.schema.json")
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "schemas", fileName);
    }
}
