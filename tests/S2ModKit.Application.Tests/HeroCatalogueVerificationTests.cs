using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed class HeroCatalogueVerificationTests
{
    [Fact]
    public async Task VerificationUsesMetadataOnlyAndSortsHeroesAndResources()
    {
        var inventory = new FakeInventory(
            ContentHash.Compute("directory"u8),
            [
                Entry("models/heroes/beta/beta.vmdl_c", 20),
                Entry("models/heroes/alpha/alpha.vmdl_c", 10),
            ]);
        var catalogue = Catalogue(
            Hero("beta", "Beta", Resource("beta.primary", "Primary model", "models/heroes/beta/beta.vmdl_c")),
            Hero("alpha", "Alpha", Resource("alpha.primary", "Primary model", "models/heroes/alpha/alpha.vmdl_c")));

        var result = await HeroCatalogueVerifier.VerifyAsync(
            catalogue,
            inventory,
            TestContext.Current.CancellationToken);

        Assert.Equal(HeroCatalogueVerificationContract.SourceUnpinned, result.Source.Status);
        Assert.Equal(["alpha", "beta"], result.Heroes.Select(hero => hero.HeroId));
        Assert.All(result.Heroes.SelectMany(hero => hero.Resources), resource =>
            Assert.Equal(HeroCatalogueVerificationContract.ResourceVerified, resource.VerificationStatus));
        Assert.Equal(0, inventory.OpenCount);
        HeroCatalogueVerifier.RequireCurrent(result);
    }

    [Fact]
    public async Task VerificationDetectsMissingMovedAmbiguousAndDuplicateLocators()
    {
        var directoryHash = ContentHash.Compute("directory"u8);
        var inventory = new FakeInventory(
            directoryHash,
            [
                Entry("models/heroes/test/test.vmdl_c", 10),
                Entry("models/heroes/test/duplicate.vmdl_c", 11),
                Entry("models/heroes/test/duplicate.vmdl_c", 12),
                Entry("models/moved/moved.vmdl_c", 13),
                Entry("models/first/ambiguous.vmdl_c", 14),
                Entry("models/second/ambiguous.vmdl_c", 15),
            ]);
        var catalogue = Catalogue(
            Hero(
                "test",
                "Test",
                Resource("test.primary", "Primary", "models/heroes/test/test.vmdl_c"),
                Resource("test.duplicate", "Duplicate", "models/heroes/test/duplicate.vmdl_c", HeroCatalogueContract.AccessoryModelRole),
                Resource("test.moved", "Moved", "models/heroes/test/moved.vmdl_c", HeroCatalogueContract.AccessoryModelRole),
                Resource("test.ambiguous", "Ambiguous", "models/heroes/test/ambiguous.vmdl_c", HeroCatalogueContract.AccessoryModelRole),
                Resource("test.missing", "Missing", "models/heroes/test/missing.vmdl_c", HeroCatalogueContract.AccessoryModelRole)));

        var result = await HeroCatalogueVerifier.VerifyAsync(
            catalogue,
            inventory,
            TestContext.Current.CancellationToken);
        var resources = result.Heroes.Single().Resources.ToDictionary(resource => resource.ResourceId, StringComparer.Ordinal);

        Assert.Equal(HeroCatalogueVerificationContract.ResourceDuplicate, resources["test.duplicate"].VerificationStatus);
        Assert.Equal(HeroCatalogueVerificationContract.ResourceMoved, resources["test.moved"].VerificationStatus);
        Assert.Equal(["models/moved/moved.vmdl_c"], resources["test.moved"].CandidateLogicalPaths);
        Assert.Equal(HeroCatalogueVerificationContract.ResourceAmbiguous, resources["test.ambiguous"].VerificationStatus);
        Assert.Equal(2, resources["test.ambiguous"].CandidateLogicalPaths.Count);
        Assert.Equal(HeroCatalogueVerificationContract.ResourceMissing, resources["test.missing"].VerificationStatus);
        Assert.Equal(0, inventory.OpenCount);

        var exception = Assert.Throws<S2ModKitException>(() => HeroCatalogueVerifier.RequireCurrent(result));
        Assert.Equal("CATALOGUE_RESOURCE_DUPLICATE", exception.Error.Code);
        Assert.Equal(ErrorCategory.InputOrResolution, exception.Error.Category);
    }

    [Fact]
    public async Task VerificationRejectsStalePinnedSource()
    {
        var inventory = new FakeInventory(
            ContentHash.Compute("actual"u8),
            [Entry("models/heroes/test/test.vmdl_c", 10)]);
        var catalogue = Catalogue(Hero(
            "test",
            "Test",
            Resource("test.primary", "Primary", "models/heroes/test/test.vmdl_c"))) with
        {
            Source = new HeroCatalogueSourceProvenance
            {
                ExpectedDirectoryHash = ContentHash.Compute("expected"u8),
            },
        };

        var result = await HeroCatalogueVerifier.VerifyAsync(
            catalogue,
            inventory,
            TestContext.Current.CancellationToken);

        Assert.Equal(HeroCatalogueVerificationContract.SourceStale, result.Source.Status);
        var exception = Assert.Throws<S2ModKitException>(() => HeroCatalogueVerifier.RequireCurrent(result));
        Assert.Equal("CATALOGUE_SOURCE_STALE", exception.Error.Code);
        Assert.Equal(ErrorCategory.InputOrResolution, exception.Error.Category);
    }

    private static HeroCatalogueDocument Catalogue(params HeroCatalogueEntry[] heroes) => new()
    {
        CatalogueId = "deadlock.heroes",
        Revision = "synthetic.1",
        Source = new HeroCatalogueSourceProvenance(),
        Heroes = heroes,
    };

    private static HeroCatalogueEntry Hero(string id, string displayName, params HeroResourceLocator[] resources) => new()
    {
        HeroId = id,
        DisplayName = displayName,
        Resources = resources,
    };

    private static HeroResourceLocator Resource(
        string id,
        string displayName,
        string logicalPath,
        string role = HeroCatalogueContract.PrimaryModelRole) => new()
        {
            ResourceId = id,
            DisplayName = displayName,
            Role = role,
            LogicalPath = logicalPath,
        };

    private static ResourceCatalogEntry Entry(string path, long size) => new(path, $"test::{path}", size);

    private sealed class FakeInventory(ContentHash sourceContentHash, IReadOnlyList<ResourceCatalogEntry> entries)
        : IResourceCatalogInventory
    {
        public ResourceCatalogDescriptor Descriptor { get; } = new("vpk", "test", "runtime_provided", 100, "metadata_only");

        public ContentHash SourceContentHash { get; } = sourceContentHash;

        public int OpenCount { get; private set; }

        public Task<IReadOnlyList<ResourceCatalogEntry>> ListEntriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(entries);

        public Task<ResourceCatalogEntry?> FindEntryAsync(string logicalPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<ResourceCatalogEntry?>(entries.FirstOrDefault(entry => entry.LogicalPath == logicalPath));

        public Task<ResourceCatalogArtifact?> TryOpenAsync(string logicalPath, CancellationToken cancellationToken = default)
        {
            OpenCount++;
            throw new InvalidOperationException("Verification must not open payload bytes.");
        }
    }
}
