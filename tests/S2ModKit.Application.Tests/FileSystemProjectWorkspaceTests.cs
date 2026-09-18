using System.Text.Json.Nodes;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;

namespace S2ModKit.Application.Tests;

public sealed class FileSystemProjectWorkspaceTests
{
    [Fact]
    public async Task AtomicRecipeWriterCreatesNewFileAndNeverOverwritesIt()
    {
        using var directory = new TestDirectory();
        var path = directory.PathOf("recipes/scaffold.json");
        var original = "{\"schemaVersion\":1}"u8.ToArray();
        var writer = new AtomicRecipeDocumentWriter();

        var published = await writer.WriteNewAsync(path, original, TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<S2ModKitException>(() =>
            writer.WriteNewAsync(path, "{\"changed\":true}"u8.ToArray(), TestContext.Current.CancellationToken));

        Assert.Equal(Path.GetFullPath(path), published);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal("RECIPE_OUTPUT_EXISTS", exception.Error.Code);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.s2modkit-staging"));
    }

    [Fact]
    public async Task CreateProjectStoresImmutableContentAddressedCopy()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceRoot = directory.CreateSubdirectory("source");
        var modelRoot = Path.Combine(sourceRoot, "Models");
        Directory.CreateDirectory(modelRoot);
        var inputPath = Path.Combine(modelRoot, "Hero.VMDL_C");
        var original = "immutable-model"u8.ToArray();
        await File.WriteAllBytesAsync(inputPath, original, cancellationToken);
        var projectRoot = directory.PathOf("project");
        var workspace = new FileSystemProjectWorkspace();

        var manifest = await CreateProjectAsync(
            workspace,
            new ProjectCreationRequest(projectRoot, inputPath, sourceRoot, new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero)),
            new TestDependencyReader(),
            cancellationToken);
        await File.WriteAllBytesAsync(inputPath, "changed-source!"u8.ToArray(), cancellationToken);
        var loaded = await workspace.LoadInputAsync(projectRoot, manifest, cancellationToken);

        Assert.Equal(ContentHash.Compute(original), manifest.Input.ContentHash);
        Assert.Equal(original, loaded.Bytes.ToArray());
        Assert.Equal("models/hero.vmdl_c", manifest.Input.LogicalPath);
        Assert.Equal($"objects/sha256/{manifest.Input.ContentHash}/content", manifest.Input.ObjectRelativePath);
        Assert.True(File.Exists(Path.Combine(projectRoot, manifest.Input.ObjectRelativePath.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task LoadInputRejectsObjectHashDrift()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceRoot = directory.CreateSubdirectory("source");
        var inputPath = Path.Combine(sourceRoot, "model.vmdl_c");
        await File.WriteAllBytesAsync(inputPath, "original"u8.ToArray(), cancellationToken);
        var projectRoot = directory.PathOf("project");
        var workspace = new FileSystemProjectWorkspace();
        var manifest = await CreateProjectAsync(
            workspace,
            new ProjectCreationRequest(projectRoot, inputPath, sourceRoot, DateTimeOffset.UnixEpoch),
            new TestDependencyReader(),
            cancellationToken);
        var objectPath = Path.Combine(projectRoot, manifest.Input.ObjectRelativePath.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllBytesAsync(objectPath, "tampered"u8.ToArray(), cancellationToken);

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => workspace.LoadInputAsync(projectRoot, manifest, cancellationToken));

        Assert.Equal("OBJECT_HASH_DRIFT", exception.Error.Code);
        Assert.Equal(ErrorCategory.RewriteOrVerification, exception.Error.Category);
    }

    [Fact]
    public async Task CreateProjectRejectsInputOutsideResourceRootBeforeWritingWorkspace()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceRoot = directory.CreateSubdirectory("source");
        var unrelatedRoot = directory.CreateSubdirectory("other-root");
        var inputPath = Path.Combine(sourceRoot, "hero.vmdl_c");
        await File.WriteAllBytesAsync(inputPath, "model"u8.ToArray(), cancellationToken);
        var projectRoot = directory.PathOf("project");
        var workspace = new FileSystemProjectWorkspace();

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => CreateProjectAsync(
            workspace,
            new ProjectCreationRequest(projectRoot, inputPath, unrelatedRoot, DateTimeOffset.UnixEpoch),
            new TestDependencyReader(),
            cancellationToken));

        Assert.Equal("INPUT_OUTSIDE_RESOURCE_ROOT", exception.Error.Code);
        Assert.False(Directory.Exists(projectRoot));
    }

    [Fact]
    public async Task PublishBuildFailureLeavesNoBuildOrTemporaryContent()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectRoot = directory.CreateSubdirectory("project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "builds"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "reports"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "temp"));
        var workspace = new FileSystemProjectWorkspace();
        var candidateBytes = "candidate"u8.ToArray();
        var publication = CreatePublication(candidateBytes, ContentHash.Compute("different"u8), "failed-build");

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => workspace.PublishBuildAsync(projectRoot, publication, cancellationToken));

        Assert.Equal("PUBLISHED_CONTENT_HASH_MISMATCH", exception.Error.Code);
        Assert.False(Directory.Exists(Path.Combine(projectRoot, "builds", publication.BuildId)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(projectRoot, "temp")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(projectRoot, "reports")));
    }

    [Fact]
    public async Task PublishBuildIsIdempotentAndLoadVerifiesContent()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectRoot = directory.CreateSubdirectory("project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "builds"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "reports"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "temp"));
        var workspace = new FileSystemProjectWorkspace();
        var candidateBytes = "candidate"u8.ToArray();
        var candidateHash = ContentHash.Compute(candidateBytes);
        var publication = CreatePublication(candidateBytes, candidateHash, "verified-build");

        var first = await workspace.PublishBuildAsync(projectRoot, publication, cancellationToken);
        var second = await workspace.PublishBuildAsync(projectRoot, publication, cancellationToken);
        var loaded = await workspace.LoadBuildAsync(projectRoot, publication.BuildId, cancellationToken);

        Assert.Equal(first, second);
        Assert.Equal(candidateBytes, loaded.Content.Bytes.ToArray());
        Assert.Equal(candidateHash, loaded.Build.ContentHash);
        Assert.Equal(ContentHash.Compute("{}"u8), loaded.Build.EvidenceJsonHash);
        Assert.Equal(ContentHash.Compute("# evidence"u8), loaded.Build.EvidenceMarkdownHash);
        Assert.Equal("{}", await File.ReadAllTextAsync(Path.Combine(projectRoot, "reports", "verified-build.json"), cancellationToken));
        Assert.Equal("# evidence", await File.ReadAllTextAsync(Path.Combine(projectRoot, "reports", "verified-build.md"), cancellationToken));
    }

    [Fact]
    public async Task LoadBuildRejectsTamperedEvidence()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectRoot = directory.CreateSubdirectory("project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "builds"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "reports"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "temp"));
        var workspace = new FileSystemProjectWorkspace();
        var publication = CreatePublication("candidate"u8.ToArray(), ContentHash.Compute("candidate"u8), "tampered-evidence");
        _ = await workspace.PublishBuildAsync(projectRoot, publication, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "reports", "tampered-evidence.json"), "{\"tampered\":true}", cancellationToken);

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => workspace.LoadBuildAsync(projectRoot, publication.BuildId, cancellationToken));

        Assert.Equal("BUILD_EVIDENCE_HASH_DRIFT", exception.Error.Code);
    }

    [Fact]
    public async Task PublishBuildRejectsOrphanedReportPathBeforeCommit()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectRoot = directory.CreateSubdirectory("project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "builds"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "reports"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "temp"));
        var blockedReportPath = Path.Combine(projectRoot, "reports", "orphaned-build.json");
        Directory.CreateDirectory(blockedReportPath);
        var workspace = new FileSystemProjectWorkspace();
        var candidateBytes = "candidate"u8.ToArray();
        var publication = CreatePublication(candidateBytes, ContentHash.Compute(candidateBytes), "orphaned-build");

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => workspace.PublishBuildAsync(projectRoot, publication, cancellationToken));

        Assert.Equal("BUILD_REPORT_COLLISION", exception.Error.Code);
        Assert.False(Directory.Exists(Path.Combine(projectRoot, "builds", publication.BuildId)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(projectRoot, "temp")));
        Assert.True(Directory.Exists(blockedReportPath));
        Assert.False(File.Exists(Path.Combine(projectRoot, "reports", "orphaned-build.md")));
    }

    [Fact]
    public async Task CreateProjectImportsDirectDependencyGraph()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceRoot = directory.CreateSubdirectory("source");
        var inputPath = Path.Combine(sourceRoot, "models", "hero.vmdl_c");
        var dependencyPath = Path.Combine(sourceRoot, "materials", "hero.vmat_c");
        Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(dependencyPath)!);
        await File.WriteAllBytesAsync(inputPath, "root"u8.ToArray(), cancellationToken);
        await File.WriteAllBytesAsync(dependencyPath, "material"u8.ToArray(), cancellationToken);
        var projectRoot = directory.PathOf("project");
        var workspace = new FileSystemProjectWorkspace();
        var dependencyReader = new TestDependencyReader(new ResourceDependency("materials/hero.vmat_c", "0000000000000042"));

        var manifest = await CreateProjectAsync(
            workspace,
            new ProjectCreationRequest(projectRoot, inputPath, sourceRoot, DateTimeOffset.UnixEpoch),
            dependencyReader,
            cancellationToken);

        var dependency = Assert.Single(manifest.Dependencies);
        var edge = Assert.Single(manifest.DependencyEdges);
        Assert.Equal("materials/hero.vmat_c", dependency.LogicalPath);
        Assert.Equal(ContentHash.Compute("material"u8), dependency.ContentHash);
        Assert.Equal("models/hero.vmdl_c", edge.FromLogicalPath);
        Assert.Equal(dependency.LogicalPath, edge.ToLogicalPath);
        Assert.Equal("0000000000000042", edge.ReferenceId);
        Assert.True(File.Exists(Path.Combine(projectRoot, dependency.ObjectRelativePath.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task CreateProjectClassifiesDependencyFromExplicitRuntimeRoot()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceRoot = directory.CreateSubdirectory("source");
        var runtimeRoot = directory.CreateSubdirectory("runtime");
        var inputPath = Path.Combine(sourceRoot, "models", "hero.vmdl_c");
        var dependencyPath = Path.Combine(runtimeRoot, "materials", "base.vmat_c");
        Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(dependencyPath)!);
        await File.WriteAllBytesAsync(inputPath, "root"u8.ToArray(), cancellationToken);
        await File.WriteAllBytesAsync(dependencyPath, "base-material"u8.ToArray(), cancellationToken);
        var workspace = new FileSystemProjectWorkspace();

        var manifest = await CreateProjectAsync(
            workspace,
            new ProjectCreationRequest(
                directory.PathOf("project"),
                inputPath,
                sourceRoot,
                DateTimeOffset.UnixEpoch,
                [runtimeRoot]),
            new TestDependencyReader(new ResourceDependency("materials/base.vmat_c", "0000000000000044")),
            cancellationToken);

        var dependency = Assert.Single(manifest.Dependencies);
        Assert.Equal("runtime_provided", dependency.ProvenanceKind);
        Assert.Equal("owned", manifest.Input.ProvenanceKind);
        Assert.Equal([sourceRoot, runtimeRoot], manifest.ResourceRoots);
        Assert.Equal([runtimeRoot], manifest.RuntimeResourceRoots);
    }

    [Fact]
    public async Task ExplicitRuntimeDependencyIsAHashedCatalogLeafRatherThanModOwnedTraversal()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceRoot = directory.CreateSubdirectory("source");
        var runtimeRoot = directory.CreateSubdirectory("runtime");
        var inputPath = Path.Combine(sourceRoot, "models", "hero.vmdl_c");
        var runtimePath = Path.Combine(runtimeRoot, "animgraphs", "hero.vnmgraph_c");
        Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(runtimePath)!);
        await File.WriteAllBytesAsync(inputPath, "root"u8.ToArray(), cancellationToken);
        await File.WriteAllBytesAsync(runtimePath, "runtime"u8.ToArray(), cancellationToken);
        var reader = new MappedDependencyReader(new Dictionary<string, ResourceDependency[]>(StringComparer.Ordinal)
        {
            ["models/hero.vmdl_c"] = [new ResourceDependency("animgraphs/hero.vnmgraph_c", "0000000000000046")],
            ["animgraphs/hero.vnmgraph_c"] = [new ResourceDependency("clips/should-not-be-imported.vanim_c", "0000000000000047")],
        });
        var workspace = new FileSystemProjectWorkspace();

        var manifest = await CreateProjectAsync(
            workspace,
            new ProjectCreationRequest(directory.PathOf("project"), inputPath, sourceRoot, DateTimeOffset.UnixEpoch, [runtimeRoot]),
            reader,
            cancellationToken);

        var dependency = Assert.Single(manifest.Dependencies);
        Assert.Equal("animgraphs/hero.vnmgraph_c", dependency.LogicalPath);
        Assert.Equal("runtime_provided", dependency.ProvenanceKind);
        Assert.Single(manifest.DependencyEdges);
    }

    [Fact]
    public async Task CreateProjectRejectsAmbiguousRuntimeDependency()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceRoot = directory.CreateSubdirectory("source");
        var firstRuntimeRoot = directory.CreateSubdirectory("runtime-one");
        var secondRuntimeRoot = directory.CreateSubdirectory("runtime-two");
        var inputPath = Path.Combine(sourceRoot, "models", "hero.vmdl_c");
        Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
        await File.WriteAllBytesAsync(inputPath, "root"u8.ToArray(), cancellationToken);
        foreach (var root in new[] { firstRuntimeRoot, secondRuntimeRoot })
        {
            var dependencyPath = Path.Combine(root, "materials", "base.vmat_c");
            Directory.CreateDirectory(Path.GetDirectoryName(dependencyPath)!);
            await File.WriteAllBytesAsync(dependencyPath, "duplicate"u8.ToArray(), cancellationToken);
        }

        var workspace = new FileSystemProjectWorkspace();
        var projectRoot = directory.PathOf("project");
        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => CreateProjectAsync(
            workspace,
            new ProjectCreationRequest(projectRoot, inputPath, sourceRoot, DateTimeOffset.UnixEpoch, [firstRuntimeRoot, secondRuntimeRoot]),
            new TestDependencyReader(new ResourceDependency("materials/base.vmat_c", "0000000000000045")),
            cancellationToken));

        Assert.Equal("RUNTIME_RESOURCE_AMBIGUOUS", exception.Error.Code);
        Assert.False(File.Exists(Path.Combine(projectRoot, "project.s2mod.json")));
    }

    [Fact]
    public async Task CreateProjectPrefersOwnedCatalogOverRuntimeDuplicate()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceRoot = directory.CreateSubdirectory("source");
        var runtimeRoot = directory.CreateSubdirectory("runtime");
        var inputPath = Path.Combine(sourceRoot, "models", "hero.vmdl_c");
        var ownedDependencyPath = Path.Combine(sourceRoot, "materials", "shared.vmat_c");
        var runtimeDependencyPath = Path.Combine(runtimeRoot, "materials", "shared.vmat_c");
        foreach (var path in new[] { inputPath, ownedDependencyPath, runtimeDependencyPath })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }

        await File.WriteAllBytesAsync(inputPath, "root"u8.ToArray(), cancellationToken);
        await File.WriteAllBytesAsync(ownedDependencyPath, "owned"u8.ToArray(), cancellationToken);
        await File.WriteAllBytesAsync(runtimeDependencyPath, "runtime"u8.ToArray(), cancellationToken);
        var workspace = new FileSystemProjectWorkspace();

        var manifest = await CreateProjectAsync(
            workspace,
            new ProjectCreationRequest(directory.PathOf("project"), inputPath, sourceRoot, DateTimeOffset.UnixEpoch, [runtimeRoot]),
            new TestDependencyReader(new ResourceDependency("materials/shared.vmat_c", "0000000000000048")),
            cancellationToken);

        var dependency = Assert.Single(manifest.Dependencies);
        Assert.Equal("owned", dependency.ProvenanceKind);
        Assert.Equal(ContentHash.Compute("owned"u8), dependency.ContentHash);
        Assert.Equal(ownedDependencyPath, dependency.SourcePath);
    }

    [Fact]
    public async Task DirectoryCatalogRejectsParentTraversal()
    {
        using var directory = new TestDirectory();
        var catalog = new DirectoryResourceCatalog(directory.CreateSubdirectory("source"), "owned", 0);

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => catalog.TryOpenAsync(
            "../outside.vmdl_c",
            TestContext.Current.CancellationToken));

        Assert.Equal("RESOURCE_LOGICAL_PATH_INVALID", exception.Error.Code);
    }

    [Fact]
    public async Task CreateProjectRejectsUnresolvedDirectDependencyWithoutManifest()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceRoot = directory.CreateSubdirectory("source");
        var inputPath = Path.Combine(sourceRoot, "models", "hero.vmdl_c");
        Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
        await File.WriteAllBytesAsync(inputPath, "root"u8.ToArray(), cancellationToken);
        var projectRoot = directory.PathOf("project");
        var workspace = new FileSystemProjectWorkspace();
        var dependencyReader = new TestDependencyReader(new ResourceDependency("materials/missing.vmat_c", "0000000000000043"));

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => CreateProjectAsync(
            workspace,
            new ProjectCreationRequest(projectRoot, inputPath, sourceRoot, DateTimeOffset.UnixEpoch),
            dependencyReader,
            cancellationToken));

        Assert.Equal("RESOURCE_DEPENDENCY_UNRESOLVED", exception.Error.Code);
        Assert.False(File.Exists(Path.Combine(projectRoot, "project.s2mod.json")));
    }

    [Fact]
    public async Task CreateProjectTraversesTransitiveDependencyGraph()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceRoot = directory.CreateSubdirectory("source");
        var inputPath = Path.Combine(sourceRoot, "models", "hero.vmdl_c");
        var materialPath = Path.Combine(sourceRoot, "materials", "hero.vmat_c");
        var texturePath = Path.Combine(sourceRoot, "textures", "hero.vtex_c");
        foreach (var path in new[] { inputPath, materialPath, texturePath })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(path)), cancellationToken);
        }

        var reader = new MappedDependencyReader(new Dictionary<string, ResourceDependency[]>(StringComparer.Ordinal)
        {
            ["models/hero.vmdl_c"] = [new ResourceDependency("materials/hero.vmat_c", "0000000000000001")],
            ["materials/hero.vmat_c"] = [new ResourceDependency("textures/hero.vtex_c", "0000000000000002")],
            ["textures/hero.vtex_c"] = [],
        });
        var workspace = new FileSystemProjectWorkspace();
        var projectRoot = directory.PathOf("project");

        var manifest = await CreateProjectAsync(
            workspace,
            new ProjectCreationRequest(projectRoot, inputPath, sourceRoot, DateTimeOffset.UnixEpoch),
            reader,
            cancellationToken);

        Assert.Equal(["materials/hero.vmat_c", "textures/hero.vtex_c"], manifest.Dependencies.Select(item => item.LogicalPath));
        Assert.Collection(
            manifest.DependencyEdges,
            edge => Assert.Equal(("materials/hero.vmat_c", "textures/hero.vtex_c"), (edge.FromLogicalPath, edge.ToLogicalPath)),
            edge => Assert.Equal(("models/hero.vmdl_c", "materials/hero.vmat_c"), (edge.FromLogicalPath, edge.ToLogicalPath)));
    }

    [Fact]
    public async Task LoadProjectRejectsLegacyManifestWithoutCompleteDependencyGraph()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectRoot = directory.CreateSubdirectory("project");
        var inputHash = ContentHash.Compute("input"u8);
        var manifest = new ProjectManifest
        {
            ProjectId = "legacy-project",
            CreatedUtc = DateTimeOffset.UnixEpoch,
            Input = new ProjectArtifactManifest
            {
                LogicalPath = "models/hero.vmdl_c",
                ContentHash = inputHash,
                Size = 5,
                ObjectRelativePath = $"objects/sha256/{inputHash}/content",
                SourcePath = "H:\\legacy\\hero.vmdl_c",
            },
            DependencyGraphComplete = false,
            ResourceRoots = ["H:\\legacy"],
        };
        await File.WriteAllBytesAsync(
            Path.Combine(projectRoot, "project.s2mod.json"),
            JsonDefaults.SerializeToUtf8(manifest),
            cancellationToken);
        var workspace = new FileSystemProjectWorkspace();

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => workspace.LoadProjectAsync(projectRoot, cancellationToken));

        Assert.Equal("PROJECT_MANIFEST_INVALID", exception.Error.Code);
    }

    [Fact]
    public async Task LoadProjectAcceptsSchemaVersionOneWithoutVersionTwoProvenanceFields()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceRoot = directory.CreateSubdirectory("source");
        var inputPath = Path.Combine(sourceRoot, "hero.vmdl_c");
        await File.WriteAllBytesAsync(inputPath, "model"u8.ToArray(), cancellationToken);
        var projectRoot = directory.PathOf("project");
        var workspace = new FileSystemProjectWorkspace();
        var created = await CreateProjectAsync(
            workspace,
            new ProjectCreationRequest(projectRoot, inputPath, sourceRoot, DateTimeOffset.UnixEpoch),
            new TestDependencyReader(),
            cancellationToken);
        var manifestPath = Path.Combine(projectRoot, "project.s2mod.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken))!.AsObject();
        manifest["schemaVersion"] = 1;
        RemoveVersionTwoProvenance(manifest["input"]!.AsObject());
        foreach (var dependency in manifest["dependencies"]!.AsArray())
        {
            RemoveVersionTwoProvenance(dependency!.AsObject());
        }

        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(JsonDefaults.Options), cancellationToken);

        var loaded = await workspace.LoadProjectAsync(projectRoot, cancellationToken);

        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal(created.ProjectId, loaded.ProjectId);
        Assert.Equal(created.Input.ContentHash, loaded.Input.ContentHash);
    }

    private static void RemoveVersionTwoProvenance(JsonObject artifact)
    {
        artifact.Remove("sourceKind");
        artifact.Remove("catalogIdentity");
        artifact.Remove("verificationMode");
    }

    private static BuildPublication CreatePublication(byte[] candidateBytes, ContentHash snapshotHash, string buildId)
    {
        var inputHash = ContentHash.Compute("input"u8);
        var planFingerprint = ContentHash.Compute("plan"u8);
        var plan = new MutationPlan("recipe", inputHash, planFingerprint, []);
        var snapshot = new ModelSnapshot(
            new ArtifactSnapshot("models/hero.vmdl_c", snapshotHash, candidateBytes.Length, []),
            []);
        var candidate = new RewriteCandidate("models/hero.vmdl_c", candidateBytes, snapshot);
        return new BuildPublication(buildId, plan, candidate, "{}", "# evidence");
    }

    private static Task<ProjectManifest> CreateProjectAsync(
        FileSystemProjectWorkspace workspace,
        ProjectCreationRequest request,
        IResourceDependencyReader dependencyReader,
        CancellationToken cancellationToken) =>
        new ProjectImporter(workspace, dependencyReader, new DirectoryProjectResourceSourceFactory())
            .CreateProjectAsync(request, cancellationToken);

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "s2modkit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        private string Root { get; }

        public string CreateSubdirectory(string relativePath)
        {
            var path = PathOf(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public string PathOf(string relativePath) => Path.Combine(Root, relativePath);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class TestDependencyReader(params ResourceDependency[] dependencies) : IResourceDependencyReader
    {
        public bool CanReadDependencies(ArtifactContent artifact) => true;

        public Task<IReadOnlyList<ResourceDependency>> ReadDependenciesAsync(ArtifactContent artifact, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ResourceDependency>>(
                artifact.LogicalPath.EndsWith(".vmdl_c", StringComparison.Ordinal) ? dependencies : []);
    }

    private sealed class MappedDependencyReader(IReadOnlyDictionary<string, ResourceDependency[]> dependencies) : IResourceDependencyReader
    {
        public bool CanReadDependencies(ArtifactContent artifact) => dependencies.ContainsKey(artifact.LogicalPath);

        public Task<IReadOnlyList<ResourceDependency>> ReadDependenciesAsync(ArtifactContent artifact, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ResourceDependency>>(dependencies[artifact.LogicalPath]);
    }
}
