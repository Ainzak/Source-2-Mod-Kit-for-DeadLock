using S2ModKit.Adapters.Vpk;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;
using S2ModKit.Reporting;

namespace S2ModKit.Vpk.Tests;

public sealed class AddonManagementApplicationTests
{
    [Fact]
    public async Task InstallRecordAndRollbackPreserveForeignArchivesAndDmmState()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CreateFixtureAsync(directory, cancellationToken);
        var foreignPath = Path.Combine(fixture.AddonsRoot, "pak98_dir.vpk");
        await CompactVpkArchiveIO.WriteAsync(foreignPath, new Dictionary<string, ReadOnlyMemory<byte>>
        {
            [fixture.TargetPath] = "foreign-model"u8.ToArray(),
        }, cancellationToken);
        var foreignBefore = ContentHash.Compute(await File.ReadAllBytesAsync(foreignPath, cancellationToken));
        var dmmPath = Path.Combine(fixture.AddonsRoot, ".dmm.json");
        await File.WriteAllTextAsync(dmmPath, "{\"stable\":true}", cancellationToken);
        var dmmBefore = ContentHash.Compute(await File.ReadAllBytesAsync(dmmPath, cancellationToken));

        var installed = await fixture.Application.InstallAsync(fixture.AddonsRoot, fixture.ProjectRoot, fixture.PackageId, "auto", cancellationToken);
        var verified = await fixture.Application.VerifyActiveAsync(fixture.AddonsRoot, fixture.ProjectRoot, installed.Receipt.InstallationId, cancellationToken);
        var observed = await fixture.Application.RecordRuntimeAsync(
            fixture.ProjectRoot,
            installed.Receipt.InstallationId,
            new RuntimeObservationInput
            {
                Status = "passed",
                Checks = new RuntimeChecks("passed", "passed", "passed", "passed", "not_checked", "not_checked"),
                Notes = ["Synthetic player observation."],
            },
            cancellationToken);
        var rolledBack = await fixture.Application.RollbackAsync(fixture.AddonsRoot, fixture.ProjectRoot, installed.Receipt.InstallationId, cancellationToken);
        var repeated = await fixture.Application.RollbackAsync(fixture.AddonsRoot, fixture.ProjectRoot, installed.Receipt.InstallationId, cancellationToken);

        Assert.Equal(99, installed.Receipt.Slot);
        Assert.Equal("active", verified.Status);
        Assert.Equal("passed", observed.Observation.Status);
        Assert.Equal("player_observed", observed.Observation.ProofLevel);
        Assert.True(Directory.Exists(Path.Combine(fixture.ProjectRoot, "runtime-observations", observed.Observation.ObservationId)));
        Assert.Equal("rolled_back", rolledBack.Status);
        Assert.Equal("rolled_back", repeated.Status);
        Assert.False(File.Exists(Path.Combine(fixture.AddonsRoot, "pak99_dir.vpk")));
        Assert.True(File.Exists(Path.Combine(fixture.AddonsRoot, rolledBack.Receipt.DisabledFileName!)));
        Assert.False(File.Exists(Path.Combine(fixture.AddonsRoot, ".s2modkit", "active-installation.json")));
        Assert.Equal(foreignBefore, ContentHash.Compute(await File.ReadAllBytesAsync(foreignPath, cancellationToken)));
        Assert.Equal(dmmBefore, ContentHash.Compute(await File.ReadAllBytesAsync(dmmPath, cancellationToken)));
    }

    [Fact]
    public async Task AutoInstallRejectsCollisionInHighestSlotWithoutWritingMarker()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CreateFixtureAsync(directory, cancellationToken);
        await CompactVpkArchiveIO.WriteAsync(Path.Combine(fixture.AddonsRoot, "pak99_dir.vpk"), new Dictionary<string, ReadOnlyMemory<byte>>
        {
            [fixture.TargetPath] = "winner"u8.ToArray(),
        }, cancellationToken);

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => fixture.Application.InstallAsync(
            fixture.AddonsRoot,
            fixture.ProjectRoot,
            fixture.PackageId,
            "auto",
            cancellationToken));

        Assert.Equal("ADDON_NO_SAFE_SLOT", exception.Error.Code);
        Assert.False(Directory.Exists(Path.Combine(fixture.AddonsRoot, ".s2modkit")));
        Assert.Empty(await fixture.Workspace.ListInstallationsAsync(fixture.ProjectRoot, cancellationToken));
    }

    [Fact]
    public async Task ExplicitInstallRejectsOccupiedNonCollidingSlotWithoutWritingReceipt()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CreateFixtureAsync(directory, cancellationToken);
        var foreignPath = Path.Combine(fixture.AddonsRoot, "pak95_dir.vpk");
        await CompactVpkArchiveIO.WriteAsync(foreignPath, new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["materials/unrelated.vmat_c"] = "foreign"u8.ToArray(),
        }, cancellationToken);
        var foreignHash = ContentHash.Compute(await File.ReadAllBytesAsync(foreignPath, cancellationToken));

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => fixture.Application.InstallAsync(
            fixture.AddonsRoot,
            fixture.ProjectRoot,
            fixture.PackageId,
            "95",
            cancellationToken));

        Assert.Equal("ADDON_SLOT_OCCUPIED", exception.Error.Code);
        Assert.Equal(foreignHash, ContentHash.Compute(await File.ReadAllBytesAsync(foreignPath, cancellationToken)));
        Assert.Empty(await fixture.Workspace.ListInstallationsAsync(fixture.ProjectRoot, cancellationToken));
    }

    [Fact]
    public async Task InventoryRejectsReparsePointReportedByFilesystemBoundary()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CreateFixtureAsync(directory, cancellationToken);
        var application = CreateApplication(fixture.Workspace, new ReparseReportingFileSystem(fixture.FileSystem));

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => application.InventoryAsync(
            fixture.AddonsRoot,
            fixture.ProjectRoot,
            fixture.PackageId,
            cancellationToken));

        Assert.Equal("ADDON_ARCHIVE_REPARSE_POINT", exception.Error.Code);
        Assert.Empty(await fixture.Workspace.ListInstallationsAsync(fixture.ProjectRoot, cancellationToken));
    }

    [Fact]
    public async Task PreparedReceiptCanBeReconciledAfterActivationInterruption()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CreateFixtureAsync(directory, cancellationToken);
        var package = await fixture.Workspace.LoadPackageAsync(fixture.ProjectRoot, fixture.PackageId, cancellationToken);
        var project = await fixture.Workspace.LoadProjectAsync(fixture.ProjectRoot, cancellationToken);
        var snapshot = await fixture.FileSystem.SnapshotAsync(fixture.AddonsRoot, cancellationToken);
        const string installationId = "install-000000000000-recovery";
        const string targetName = "pak99_dir.vpk";
        const string stagingName = ".pak99_dir.vpk.install-recovery.s2modkit-staging";
        var receipt = new InstallationReceipt
        {
            InstallationId = installationId,
            ProjectId = project.ProjectId,
            PackageId = fixture.PackageId,
            CreatedUtc = DateTimeOffset.UnixEpoch,
            UpdatedUtc = DateTimeOffset.UnixEpoch,
            AddonsRoot = snapshot.AddonsRoot,
            TargetFileName = targetName,
            StagingFileName = stagingName,
            Slot = 99,
            LogicalPath = package.Package.EntryLogicalPath,
            PackageHash = package.Package.ContentHash,
            EntryContentHash = package.Package.ReplacementEntryHash,
            DmmStateHashBefore = snapshot.DmmStateHash,
        };
        await fixture.Workspace.CreateInstallationAsync(fixture.ProjectRoot, receipt, cancellationToken);
        await fixture.FileSystem.CreateActiveMarkerAsync(
            fixture.AddonsRoot,
            new ActiveInstallationMarker(1, installationId, project.ProjectId, fixture.PackageId, targetName, package.Package.ContentHash),
            cancellationToken);
        await fixture.FileSystem.StageAndActivateAsync(
            fixture.AddonsRoot,
            package.PackagePath,
            stagingName,
            targetName,
            package.Package.ContentHash,
            cancellationToken);

        var reconciled = await fixture.Application.VerifyActiveAsync(fixture.AddonsRoot, fixture.ProjectRoot, installationId, cancellationToken);

        Assert.Equal("active", reconciled.Receipt.Status);
        await fixture.Application.RollbackAsync(fixture.AddonsRoot, fixture.ProjectRoot, installationId, cancellationToken);
    }

    [Fact]
    public async Task DmmReactionLeavesRecoverablePreparedReceiptAndRollbackDisablesOwnedFile()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CreateFixtureAsync(directory, cancellationToken);
        var reacting = new DmmReactingFileSystem(fixture.FileSystem);
        var application = CreateApplication(fixture.Workspace, reacting);

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => application.InstallAsync(
            fixture.AddonsRoot,
            fixture.ProjectRoot,
            fixture.PackageId,
            "auto",
            cancellationToken));
        var receipt = Assert.Single(await fixture.Workspace.ListInstallationsAsync(fixture.ProjectRoot, cancellationToken));

        Assert.Equal("DMM_STATE_CHANGED", exception.Error.Code);
        Assert.Equal("prepared", receipt.Status);
        Assert.True(File.Exists(Path.Combine(fixture.AddonsRoot, receipt.TargetFileName)));
        var rollback = await application.RollbackAsync(fixture.AddonsRoot, fixture.ProjectRoot, receipt.InstallationId, cancellationToken);
        Assert.Equal("rolled_back", rollback.Status);
    }

    [Fact]
    public async Task RollbackRefusesAlteredInstalledArchive()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CreateFixtureAsync(directory, cancellationToken);
        var installed = await fixture.Application.InstallAsync(fixture.AddonsRoot, fixture.ProjectRoot, fixture.PackageId, "auto", cancellationToken);
        var target = Path.Combine(fixture.AddonsRoot, installed.Receipt.TargetFileName);
        await File.AppendAllTextAsync(target, "tamper", cancellationToken);

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => fixture.Application.RollbackAsync(
            fixture.AddonsRoot,
            fixture.ProjectRoot,
            installed.Receipt.InstallationId,
            cancellationToken));

        Assert.Equal("ADDON_ROLLBACK_HASH_DRIFT", exception.Error.Code);
        Assert.True(File.Exists(target));
    }

    private static AddonManagementApplication CreateApplication(FileSystemProjectWorkspace workspace, IAddonFileSystem? fileSystem = null) =>
        new(workspace, workspace, workspace, fileSystem ?? new AddonFileSystem(), new ValvePakAddonArchiveInspector(), new EvidenceReportRenderer(), new FixedClock());

    private static async Task<Fixture> CreateFixtureAsync(TestDirectory directory, CancellationToken cancellationToken)
    {
        const string targetPath = "models/heroes/test/hero.vmdl_c";
        var sourceRoot = directory.CreateSubdirectory("source-root");
        var inputPath = Path.Combine(sourceRoot, targetPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
        await File.WriteAllBytesAsync(inputPath, "source-model"u8.ToArray(), cancellationToken);
        var projectRoot = directory.PathOf("project");
        var workspace = new FileSystemProjectWorkspace();
        var project = await new ProjectImporter(workspace, new EmptyDependencyReader(), new DirectoryProjectResourceSourceFactory())
            .CreateProjectAsync(new ProjectCreationRequest(projectRoot, inputPath, sourceRoot, DateTimeOffset.UnixEpoch), cancellationToken);
        var replacementBytes = "replacement-model"u8.ToArray();
        var replacementHash = ContentHash.Compute(replacementBytes);
        const string buildId = "verified-build";
        await workspace.PublishBuildAsync(
            projectRoot,
            new BuildPublication(
                buildId,
                new MutationPlan("recipe", project.Input.ContentHash, ContentHash.Compute("plan"u8), []),
                new RewriteCandidate(targetPath, replacementBytes, new ModelSnapshot(new ArtifactSnapshot(targetPath, replacementHash, replacementBytes.Length, []), [])),
                "{}",
                "# evidence"),
            cancellationToken);
        var packaging = new VpkPackagingApplication(workspace, workspace, new DeterministicVpkCandidateBuilder(), new SkippedVpkExternalVerifier(), new AtomicVpkPackageExporter(), new EvidenceReportRenderer(), new FixedClock());
        var package = await packaging.CreateMinimalAsync(projectRoot, buildId, false, cancellationToken);
        var addonsRoot = directory.CreateSubdirectory("addons");
        var fileSystem = new AddonFileSystem();
        return new Fixture(workspace, fileSystem, CreateApplication(workspace, fileSystem), projectRoot, addonsRoot, package.Package.PackageId, targetPath);
    }

    private sealed record Fixture(
        FileSystemProjectWorkspace Workspace,
        AddonFileSystem FileSystem,
        AddonManagementApplication Application,
        string ProjectRoot,
        string AddonsRoot,
        string PackageId,
        string TargetPath);

    private sealed class DmmReactingFileSystem(IAddonFileSystem inner) : IAddonFileSystem
    {
        public Task<AddonRootSnapshot> SnapshotAsync(string addonsRoot, CancellationToken cancellationToken = default) => inner.SnapshotAsync(addonsRoot, cancellationToken);
        public Task<ContentHash> ComputeFileHashAsync(string path, CancellationToken cancellationToken = default) => inner.ComputeFileHashAsync(path, cancellationToken);
        public Task<ActiveInstallationMarker?> LoadActiveMarkerAsync(string addonsRoot, CancellationToken cancellationToken = default) => inner.LoadActiveMarkerAsync(addonsRoot, cancellationToken);
        public Task CreateActiveMarkerAsync(string addonsRoot, ActiveInstallationMarker marker, CancellationToken cancellationToken = default) => inner.CreateActiveMarkerAsync(addonsRoot, marker, cancellationToken);
        public Task RemoveActiveMarkerAsync(string addonsRoot, ActiveInstallationMarker marker, CancellationToken cancellationToken = default) => inner.RemoveActiveMarkerAsync(addonsRoot, marker, cancellationToken);
        public Task<string> DisableAsync(string addonsRoot, string targetFileName, string disabledFileName, ContentHash expectedHash, CancellationToken cancellationToken = default) => inner.DisableAsync(addonsRoot, targetFileName, disabledFileName, expectedHash, cancellationToken);
        public Task AbandonStagingAsync(string addonsRoot, string stagingFileName, CancellationToken cancellationToken = default) => inner.AbandonStagingAsync(addonsRoot, stagingFileName, cancellationToken);

        public async Task StageAndActivateAsync(string addonsRoot, string sourcePath, string stagingFileName, string targetFileName, ContentHash expectedHash, CancellationToken cancellationToken = default)
        {
            await inner.StageAndActivateAsync(addonsRoot, sourcePath, stagingFileName, targetFileName, expectedHash, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(addonsRoot, ".dmm.json"), "{\"reacted\":true}", cancellationToken);
        }
    }

    private sealed class ReparseReportingFileSystem(IAddonFileSystem inner) : IAddonFileSystem
    {
        public async Task<AddonRootSnapshot> SnapshotAsync(string addonsRoot, CancellationToken cancellationToken = default)
        {
            var snapshot = await inner.SnapshotAsync(addonsRoot, cancellationToken);
            return snapshot with
            {
                ActiveVpkFiles =
                [
                    new AddonFileDescriptor("pak90_dir.vpk", Path.Combine(snapshot.AddonsRoot, "pak90_dir.vpk"), 1, IsReparsePoint: true),
                ],
            };
        }

        public Task<ContentHash> ComputeFileHashAsync(string path, CancellationToken cancellationToken = default) => inner.ComputeFileHashAsync(path, cancellationToken);
        public Task<ActiveInstallationMarker?> LoadActiveMarkerAsync(string addonsRoot, CancellationToken cancellationToken = default) => inner.LoadActiveMarkerAsync(addonsRoot, cancellationToken);
        public Task CreateActiveMarkerAsync(string addonsRoot, ActiveInstallationMarker marker, CancellationToken cancellationToken = default) => inner.CreateActiveMarkerAsync(addonsRoot, marker, cancellationToken);
        public Task RemoveActiveMarkerAsync(string addonsRoot, ActiveInstallationMarker marker, CancellationToken cancellationToken = default) => inner.RemoveActiveMarkerAsync(addonsRoot, marker, cancellationToken);
        public Task StageAndActivateAsync(string addonsRoot, string sourcePath, string stagingFileName, string targetFileName, ContentHash expectedHash, CancellationToken cancellationToken = default) => inner.StageAndActivateAsync(addonsRoot, sourcePath, stagingFileName, targetFileName, expectedHash, cancellationToken);
        public Task<string> DisableAsync(string addonsRoot, string targetFileName, string disabledFileName, ContentHash expectedHash, CancellationToken cancellationToken = default) => inner.DisableAsync(addonsRoot, targetFileName, disabledFileName, expectedHash, cancellationToken);
        public Task AbandonStagingAsync(string addonsRoot, string stagingFileName, CancellationToken cancellationToken = default) => inner.AbandonStagingAsync(addonsRoot, stagingFileName, cancellationToken);
    }

    private sealed class EmptyDependencyReader : IResourceDependencyReader
    {
        public bool CanReadDependencies(ArtifactContent artifact) => true;
        public Task<IReadOnlyList<ResourceDependency>> ReadDependenciesAsync(ArtifactContent artifact, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResourceDependency>>([]);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class TestDirectory : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "s2modkit-addon-tests", Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(root);
        public string CreateSubdirectory(string path) { var full = PathOf(path); Directory.CreateDirectory(full); return full; }
        public string PathOf(string path) => Path.Combine(root, path);
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
