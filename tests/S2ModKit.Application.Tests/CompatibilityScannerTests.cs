using System.Text;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed class CompatibilityScannerTests
{
    [Fact]
    public async Task ScannerVerifiesThenOpensOnlyCatalogueResourcesAndUsesExistingDiscovery()
    {
        var sourceHash = ContentHash.Compute("directory"u8);
        var catalogue = Catalogue(sourceHash);
        var inventory = new FakeInventory(sourceHash, catalogue);
        var scanner = new CompatibilityScanner(new FakeInspector());

        var first = await scanner.ScanAsync(catalogue, inventory, TestContext.Current.CancellationToken);
        var second = await scanner.ScanAsync(catalogue, new FakeInventory(sourceHash, catalogue), TestContext.Current.CancellationToken);

        Assert.Equal(["alpha.primary", "beta.primary"], first.Resources.Select(resource => resource.ResourceId));
        Assert.Equal(
            first.Resources.Select(resource => resource.Signature?.SignatureId),
            second.Resources.Select(resource => resource.Signature?.SignatureId));
        Assert.Equal(first.Resources[0].Signature?.SignatureId, first.Resources[1].Signature?.SignatureId);
        Assert.All(first.Resources, resource =>
        {
            Assert.Equal(CompatibilityScanContract.Supported, resource.Status);
            Assert.Single(resource.Candidates);
            Assert.Contains(resource.Candidates[0].Capabilities, capability =>
                capability.OperationKind == "remove_component"
                && capability.Availability == CapabilityAvailability.Available);
        });
        Assert.Equal(2, inventory.OpenedPaths.Count);
        Assert.Equal(
            ["models/heroes/alpha/alpha.vmdl_c", "models/heroes/beta/beta.vmdl_c"],
            inventory.OpenedPaths);
    }

    private static HeroCatalogueDocument Catalogue(ContentHash sourceHash) => new()
    {
        CatalogueId = "deadlock.heroes",
        Revision = "test.1",
        Source = new HeroCatalogueSourceProvenance { ExpectedDirectoryHash = sourceHash },
        Heroes =
        [
            Hero("beta", "Beta", "models/heroes/beta/beta.vmdl_c"),
            Hero("alpha", "Alpha", "models/heroes/alpha/alpha.vmdl_c"),
        ],
    };

    private static HeroCatalogueEntry Hero(string id, string displayName, string path) => new()
    {
        HeroId = id,
        DisplayName = displayName,
        Resources =
        [
            new HeroResourceLocator
            {
                ResourceId = $"{id}.primary",
                DisplayName = "Primary model",
                Role = HeroCatalogueContract.PrimaryModelRole,
                LogicalPath = path,
            },
        ],
    };

    private sealed class FakeInventory(ContentHash sourceHash, HeroCatalogueDocument catalogue)
        : IResourceCatalogInventory
    {
        private readonly Dictionary<string, byte[]> bytes = catalogue.Heroes
            .SelectMany(hero => hero.Resources)
            .ToDictionary(
                resource => resource.LogicalPath,
                resource => Encoding.UTF8.GetBytes(resource.ResourceId),
                StringComparer.Ordinal);

        public ResourceCatalogDescriptor Descriptor { get; } = new("vpk", "test", "runtime_provided", 100, "crc_sha256");

        public ContentHash SourceContentHash { get; } = sourceHash;

        public List<string> OpenedPaths { get; } = [];

        public Task<IReadOnlyList<ResourceCatalogEntry>> ListEntriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ResourceCatalogEntry>>(bytes
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new ResourceCatalogEntry(item.Key, $"test::{item.Key}", item.Value.Length))
                .ToArray());

        public Task<ResourceCatalogEntry?> FindEntryAsync(string logicalPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(bytes.TryGetValue(logicalPath, out var value)
                ? new ResourceCatalogEntry(logicalPath, $"test::{logicalPath}", value.Length)
                : null);

        public Task<ResourceCatalogArtifact?> TryOpenAsync(string logicalPath, CancellationToken cancellationToken = default)
        {
            if (!bytes.TryGetValue(logicalPath, out var value))
            {
                return Task.FromResult<ResourceCatalogArtifact?>(null);
            }

            OpenedPaths.Add(logicalPath);
            return Task.FromResult<ResourceCatalogArtifact?>(new ResourceCatalogArtifact(
                new ArtifactContent(logicalPath, ContentHash.Compute(value), value),
                $"test::{logicalPath}"));
        }
    }

    private sealed class FakeInspector : IModelInspector
    {
        public string AdapterName => "synthetic";

        public string AdapterVersion => "1";

        public IReadOnlyDictionary<string, string> ComponentVersions { get; } =
            new SortedDictionary<string, string>(StringComparer.Ordinal) { ["synthetic.reader"] = "1" };

        public bool CanInspect(ArtifactContent artifact) => true;

        public Task<ModelSnapshot> InspectAsync(ArtifactContent artifact, CancellationToken cancellationToken = default)
        {
            var drawCall = DrawCallSnapshot.Create(
                artifact.LogicalPath,
                0,
                0,
                0,
                "materials/test.vmat_c",
                0,
                3);
            var mesh = new MeshSnapshot(
                artifact.LogicalPath,
                0,
                1,
                ContentHash.Compute("mesh"u8),
                [drawCall]);
            var model = new ModelSnapshot(
                new ArtifactSnapshot(artifact.LogicalPath, artifact.ContentHash, artifact.Bytes.Length, []),
                [new LodSnapshot(0, [mesh])]);
            return Task.FromResult(model);
        }
    }
}
