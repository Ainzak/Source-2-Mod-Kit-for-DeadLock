using System.Text.Json.Nodes;
using S2ModKit.Adapters.Vpk;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;
using S2ModKit.Reporting;

namespace S2ModKit.Vpk.Tests;

public sealed class VpkPackagingApplicationTests
{
    [Fact]
    public async Task CreateAndVerifyPublishCanonicalPackageEvidence()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CreateFixtureAsync(directory, cancellationToken);
        var application = CreateApplication(fixture.Workspace);

        var created = await application.CreateAsync(
            fixture.ProjectRoot,
            fixture.BuildId,
            fixture.SourceVpkPath,
            fixture.SourceVpkHash,
            requireExternalVerifier: false,
            cancellationToken);
        var verified = await application.VerifyAsync(
            fixture.ProjectRoot,
            created.Package.PackageId,
            requireExternalVerifier: false,
            cancellationToken);

        var packagePath = Path.Combine(fixture.ProjectRoot, created.Package.PackageRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(packagePath));
        Assert.Equal(created.Package.ContentHash, ContentHash.Compute(await File.ReadAllBytesAsync(packagePath, cancellationToken)));
        Assert.Equal(fixture.BuildId, created.Package.BuildId);
        Assert.Equal(fixture.TargetPath, created.Package.EntryLogicalPath);
        Assert.Equal(1, created.Evidence.UnchangedEntryCount);
        Assert.Contains(created.Evidence.Boundaries, boundary => boundary.Name == "vpk_external_verifier" && boundary.Status == "skipped");
        Assert.Contains(verified.Evidence.Boundaries, boundary => boundary.Name == "vpk_internal_checksums" && boundary.Status == "passed");
        Assert.True(File.Exists(Path.Combine(fixture.ProjectRoot, "reports", $"{created.Package.PackageId}-verify.json")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.ProjectRoot, "temp")));
    }

    [Fact]
    public async Task RequiredExternalVerifierFailsBeforePackagePublication()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CreateFixtureAsync(directory, cancellationToken);
        var application = CreateApplication(fixture.Workspace);

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => application.CreateAsync(
            fixture.ProjectRoot,
            fixture.BuildId,
            fixture.SourceVpkPath,
            fixture.SourceVpkHash,
            requireExternalVerifier: true,
            cancellationToken));

        Assert.Equal("VPK_EXTERNAL_VERIFICATION_FAILED", exception.Error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.ProjectRoot, "packages")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.ProjectRoot, "temp")));
    }

    [Fact]
    public async Task CreateMinimalPublishesDeterministicOneEntryPackage()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CreateFixtureAsync(directory, cancellationToken);
        var application = CreateApplication(fixture.Workspace);

        var first = await application.CreateMinimalAsync(fixture.ProjectRoot, fixture.BuildId, false, cancellationToken);
        var second = await application.CreateMinimalAsync(fixture.ProjectRoot, fixture.BuildId, false, cancellationToken);
        var verified = await application.VerifyAsync(fixture.ProjectRoot, first.Package.PackageId, false, cancellationToken);
        var packagePath = Path.Combine(fixture.ProjectRoot, first.Package.PackageRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var archive = await CompactVpkArchiveIO.ReadAndVerifyAsync(packagePath, cancellationToken);

        Assert.Equal("minimal", first.Package.Mode);
        Assert.Equal(2, first.Package.SchemaVersion);
        Assert.Null(first.Package.SourceVpkPath);
        Assert.Equal(first.Package.PackageId, second.Package.PackageId);
        Assert.Equal(first.Package.ContentHash, second.Package.ContentHash);
        Assert.Single(archive.Entries);
        Assert.Equal(fixture.TargetPath, archive.Entries[0].LogicalPath);
        Assert.Equal("minimal", first.Evidence.Mode);
        Assert.NotNull(first.Evidence.PackagedEntry);
        Assert.Equal("minimal", verified.Evidence.Mode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.ProjectRoot, "temp")));
    }

    [Fact]
    public async Task ExportPublishesNewVerifiedCopyWithoutReplacingExistingFile()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CreateFixtureAsync(directory, cancellationToken);
        var application = CreateApplication(fixture.Workspace);
        var created = await application.CreateMinimalAsync(fixture.ProjectRoot, fixture.BuildId, false, cancellationToken);
        var outputPath = directory.PathOf("exported.vpk");

        var exported = await application.ExportAsync(fixture.ProjectRoot, created.Package.PackageId, outputPath, cancellationToken);
        var exception = await Assert.ThrowsAsync<S2ModKitException>(() =>
            application.ExportAsync(fixture.ProjectRoot, created.Package.PackageId, outputPath, cancellationToken));

        Assert.Equal(created.Package.PackageId, exported.PackageId);
        Assert.Equal(created.Package.ContentHash, exported.ContentHash);
        Assert.Equal(created.Package.ContentHash, ContentHash.Compute(await File.ReadAllBytesAsync(outputPath, cancellationToken)));
        Assert.Equal("VPK_EXPORT_PATH_INVALID", exception.Error.Code);
        Assert.Empty(Directory.EnumerateFiles(directory.RootPath, "*.s2modkit-export", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LoadPackageRejectsContentTampering()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CreateFixtureAsync(directory, cancellationToken);
        var application = CreateApplication(fixture.Workspace);
        var created = await application.CreateAsync(
            fixture.ProjectRoot,
            fixture.BuildId,
            fixture.SourceVpkPath,
            fixture.SourceVpkHash,
            requireExternalVerifier: false,
            cancellationToken);
        var packagePath = Path.Combine(fixture.ProjectRoot, created.Package.PackageRelativePath.Replace('/', Path.DirectorySeparatorChar));
        await File.AppendAllTextAsync(packagePath, "tamper", cancellationToken);

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => fixture.Workspace.LoadPackageAsync(
            fixture.ProjectRoot,
            created.Package.PackageId,
            cancellationToken));

        Assert.Equal("VPK_PACKAGE_CONTENT_DRIFT", exception.Error.Code);
    }

    [Fact]
    public async Task LoadAndVerifyAcceptLegacyReplaceSourcePackageManifest()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CreateFixtureAsync(directory, cancellationToken);
        var application = CreateApplication(fixture.Workspace);
        var created = await application.CreateAsync(
            fixture.ProjectRoot,
            fixture.BuildId,
            fixture.SourceVpkPath,
            fixture.SourceVpkHash,
            requireExternalVerifier: false,
            cancellationToken);
        var manifestPath = Path.Combine(fixture.ProjectRoot, "packages", created.Package.PackageId, "package.s2mod.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken))!.AsObject();
        manifest.Remove("schemaVersion");
        manifest.Remove("mode");
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(JsonDefaults.Options), cancellationToken);

        var loaded = await fixture.Workspace.LoadPackageAsync(fixture.ProjectRoot, created.Package.PackageId, cancellationToken);
        var verified = await application.VerifyAsync(fixture.ProjectRoot, created.Package.PackageId, false, cancellationToken);

        Assert.Equal(1, loaded.Package.SchemaVersion);
        Assert.Equal("replace_source", loaded.Package.Mode);
        Assert.Equal("replace_source", verified.Evidence.Mode);
    }

    private static VpkPackagingApplication CreateApplication(FileSystemProjectWorkspace workspace) =>
        new(
            workspace,
            workspace,
            new DeterministicVpkCandidateBuilder(),
            new SkippedVpkExternalVerifier(),
            new AtomicVpkPackageExporter(),
            new EvidenceReportRenderer(),
            new FixedClock());

    private static async Task<Fixture> CreateFixtureAsync(TestDirectory directory, CancellationToken cancellationToken)
    {
        const string targetPath = "models/heroes/test/hero.vmdl_c";
        const string buildId = "verified-build";
        var sourceRoot = directory.CreateSubdirectory("source-root");
        var inputPath = Path.Combine(sourceRoot, targetPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
        var inputBytes = "source-model"u8.ToArray();
        var replacementBytes = "replacement-model"u8.ToArray();
        await File.WriteAllBytesAsync(inputPath, inputBytes, cancellationToken);
        var projectRoot = directory.PathOf("project");
        var workspace = new FileSystemProjectWorkspace();
        var project = await new ProjectImporter(workspace, new EmptyDependencyReader(), new DirectoryProjectResourceSourceFactory())
            .CreateProjectAsync(
                new ProjectCreationRequest(projectRoot, inputPath, sourceRoot, DateTimeOffset.UnixEpoch),
                cancellationToken);
        var planFingerprint = ContentHash.Compute("plan"u8);
        var plan = new MutationPlan("recipe", project.Input.ContentHash, planFingerprint, []);
        var replacementHash = ContentHash.Compute(replacementBytes);
        var candidate = new RewriteCandidate(
            targetPath,
            replacementBytes,
            new ModelSnapshot(new ArtifactSnapshot(targetPath, replacementHash, replacementBytes.Length, []), []));
        await workspace.PublishBuildAsync(
            projectRoot,
            new BuildPublication(buildId, plan, candidate, "{}", "# evidence"),
            cancellationToken);
        var sourceVpkPath = directory.PathOf("source.vpk");
        await CompactVpkArchiveIO.WriteAsync(sourceVpkPath, new Dictionary<string, ReadOnlyMemory<byte>>
        {
            [targetPath] = inputBytes,
            ["materials/hero.vmat_c"] = "material"u8.ToArray(),
        }, cancellationToken);
        var sourceVpk = await CompactVpkArchiveIO.ReadAndVerifyAsync(sourceVpkPath, cancellationToken);
        return new Fixture(workspace, projectRoot, buildId, sourceVpkPath, sourceVpk.ContentHash, targetPath);
    }

    private sealed record Fixture(
        FileSystemProjectWorkspace Workspace,
        string ProjectRoot,
        string BuildId,
        string SourceVpkPath,
        ContentHash SourceVpkHash,
        string TargetPath);

    private sealed class EmptyDependencyReader : IResourceDependencyReader
    {
        public bool CanReadDependencies(ArtifactContent artifact) => true;

        public Task<IReadOnlyList<ResourceDependency>> ReadDependenciesAsync(ArtifactContent artifact, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ResourceDependency>>([]);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "s2modkit-vpk-app-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        private string Root { get; }

        public string RootPath => Root;

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
}
