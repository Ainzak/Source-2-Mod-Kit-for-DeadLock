using System.Text.Json.Nodes;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed class CurrentSourceInstallationTests
{
    [Fact]
    public async Task EqualRelevantSourceWithChangedArchiveProvenancePassesOneGuardedHandoff()
    {
        var fixture = new Fixture();
        var result = await fixture.Application.InstallAsync(fixture.Request, TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.Reads); Assert.Equal(1, fixture.Verifications); Assert.Equal(1, fixture.Installs);
        Assert.Equal(1, fixture.EvidenceWrites); Assert.Equal("active", result.Installation.Status);
        Assert.Equal(fixture.Expected, result.Preflight.Expected);
        Assert.NotEqual(result.Preflight.Initial[0].CatalogIdentity, result.Preflight.Handoff[0].CatalogIdentity);
        Assert.Equal("relevant_source_snapshot", result.Preflight.Scope);
    }

    [Theory]
    [InlineData("model", "CURRENT_SOURCE_RESOURCE_DRIFT")]
    [InlineData("dependency", "CURRENT_SOURCE_RESOURCE_DRIFT")]
    [InlineData("missing", "CURRENT_SOURCE_HANDOFF_DRIFT")]
    [InlineData("provenance", "CURRENT_SOURCE_HANDOFF_DRIFT")]
    [InlineData("read", "CURRENT_SOURCE_READ_FAILED")]
    [InlineData("ambiguous", "CURRENT_SOURCE_RESOURCE_AMBIGUOUS")]
    public async Task InitialReadFailureStopsBeforeVerificationReceiptOrAddonAccess(string defect, string code)
    {
        var fixture = new Fixture { ReadDefect = defect };
        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => fixture.Application.InstallAsync(fixture.Request, TestContext.Current.CancellationToken));
        Assert.Equal(code, exception.Error.Code);
        Assert.Equal(0, fixture.Verifications); Assert.Equal(0, fixture.Installs); Assert.Equal(0, fixture.EvidenceWrites);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("dependency")]
    [InlineData("read")]
    public async Task SourceChangeAfterCandidateVerificationStopsAtFreshHandoffRead(string defect)
    {
        var fixture = new Fixture { ReadDefect = defect, DefectAtRead = 2 };
        await Assert.ThrowsAsync<S2ModKitException>(() => fixture.Application.InstallAsync(fixture.Request, TestContext.Current.CancellationToken));
        Assert.Equal(2, fixture.Reads); Assert.Equal(1, fixture.Verifications);
        Assert.Equal(0, fixture.Installs); Assert.Equal(0, fixture.EvidenceWrites);
    }

    [Theory]
    [InlineData("package")]
    [InlineData("plan")]
    [InlineData("graph")]
    [InlineData("mode")]
    [InlineData("replacement")]
    [InlineData("build-bytes")]
    public async Task InvalidReviewedPackageOrSourceLinkageStopsBeforeSourceAndGameAccess(string defect)
    {
        var fixture = new Fixture(); fixture.Mutate(defect);
        await Assert.ThrowsAsync<S2ModKitException>(() => fixture.Application.InstallAsync(fixture.Request, TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Reads); Assert.Equal(0, fixture.Verifications); Assert.Equal(0, fixture.Installs);
    }

    [Theory]
    [InlineData("package")]
    [InlineData("plan")]
    [InlineData("graph")]
    public async Task StalePreparedHandoffCannotSurviveWorkspaceChangeDuringVerification(string defect)
    {
        var fixture = new Fixture { ChangeDuringVerification = defect };
        await Assert.ThrowsAsync<S2ModKitException>(() => fixture.Application.InstallAsync(fixture.Request, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Reads); Assert.Equal(1, fixture.Verifications); Assert.Equal(0, fixture.Installs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerificationOrReportPublicationFailureCannotReachManagedInstall(bool publication)
    {
        var fixture = new Fixture { FailVerification = !publication, FailEvidence = publication };
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Application.InstallAsync(fixture.Request, TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Installs);
    }

    [Fact]
    public async Task PreviousPassedReportIsNotReusedOnAnotherInvocation()
    {
        var fixture = new Fixture();
        await fixture.Application.InstallAsync(fixture.Request, TestContext.Current.CancellationToken);
        fixture.ReadDefect = "model"; fixture.DefectAtRead = 3;
        await Assert.ThrowsAsync<S2ModKitException>(() => fixture.Application.InstallAsync(fixture.Request, TestContext.Current.CancellationToken));
        Assert.Equal(3, fixture.Reads); Assert.Equal(1, fixture.Installs);
    }

    [Fact]
    public async Task DifferentVerifiedCandidateCannotUseThePreparedPackageBinding()
    {
        var fixture = new Fixture { WrongVerifiedCandidate = true };
        await Assert.ThrowsAsync<S2ModKitException>(() => fixture.Application.InstallAsync(fixture.Request, TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Installs); Assert.Equal(0, fixture.EvidenceWrites);
    }

    [Fact]
    public async Task CancellationAfterVerificationCannotPublishAHandoffOrInstall()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var fixture = new Fixture { CancelDuringVerification = cancellation };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Application.InstallAsync(fixture.Request, cancellation.Token));
        Assert.Equal(0, fixture.Installs); Assert.Equal(0, fixture.EvidenceWrites);
    }

    [Fact]
    public async Task PreflightSchemaAndRuntimeReadersRequireClosedCompleteObservedFacts()
    {
        var fixture = new Fixture();
        var report = (await fixture.Application.InstallAsync(fixture.Request, TestContext.Current.CancellationToken)).Preflight;
        var json = SourcePreflightJson.Write(report);
        Assert.Equal(json, SourcePreflightJson.Write(SourcePreflightJson.Read(System.Text.Encoding.UTF8.GetBytes(json))));
        var schema = await JsonSchema.FromFileAsync(SchemaPath(), TestContext.Current.CancellationToken);
        Assert.Empty(schema.Validate(json));
        foreach (var property in new[] { "schemaVersion", "handoff", "modelVerificationHash", "expected", "reportId" })
        {
            var missing = JsonNode.Parse(json)!.AsObject(); missing.Remove(property);
            Assert.NotEmpty(schema.Validate(missing.ToJsonString()));
            Assert.Throws<S2ModKitException>(() => SourcePreflightJson.Read(System.Text.Encoding.UTF8.GetBytes(missing.ToJsonString())));
            var nullValue = JsonNode.Parse(json)!.AsObject(); nullValue[property] = null;
            Assert.NotEmpty(schema.Validate(nullValue.ToJsonString()));
            Assert.Throws<S2ModKitException>(() => SourcePreflightJson.Read(System.Text.Encoding.UTF8.GetBytes(nullValue.ToJsonString())));
        }
        var unknown = JsonNode.Parse(json)!; unknown["handoff"]![0]!["passed"] = true;
        Assert.NotEmpty(schema.Validate(unknown.ToJsonString()));
        Assert.Throws<S2ModKitException>(() => SourcePreflightJson.Read(System.Text.Encoding.UTF8.GetBytes(unknown.ToJsonString())));
        Assert.Throws<S2ModKitException>(() => SourcePreflightJson.Read(System.Text.Encoding.UTF8.GetBytes(json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 9, \"schemaVersion\": 1", StringComparison.Ordinal))));
        Assert.Throws<S2ModKitException>(() => SourcePreflightJson.Validate(report with { Expected = report.Expected.Reverse().ToArray() }));
        Assert.Throws<S2ModKitException>(() => SourcePreflightJson.Validate(report with { Expected = [report.Expected[0], report.Expected[0]] }));
    }

    [Fact]
    public async Task CatalogReaderHashesActualBytesAndRejectsAFalseDeclaredHash()
    {
        var fixture = new Fixture(); var factory = new CatalogFactory(fixture.Expected, inconsistent: false);
        var observed = await new CatalogCurrentSourceReader(factory).ReadAsync("explicit-source", fixture.Expected, TestContext.Current.CancellationToken);
        Assert.Equal(fixture.Expected, observed.Select(item => item.Resource)); Assert.True(factory.Disposed);
        var wrong = new CatalogFactory(fixture.Expected, inconsistent: true);
        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => new CatalogCurrentSourceReader(wrong)
            .ReadAsync("explicit-source", fixture.Expected, TestContext.Current.CancellationToken));
        Assert.Equal("CURRENT_SOURCE_READ_INCONSISTENT", exception.Error.Code); Assert.True(wrong.Disposed);
    }

    private static string SchemaPath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "S2ModKit.slnx"))) root = root.Parent;
        return Path.Combine(root!.FullName, "schemas", "source-preflight.schema.json");
    }

    private sealed class CatalogFactory(IReadOnlyList<CurrentSourceResource> expected, bool inconsistent)
        : IResourceCatalogInventoryFactory, IResourceCatalogInventory, IDisposable
    {
        public bool Disposed { get; private set; }
        public ResourceCatalogDescriptor Descriptor => new("synthetic", "catalog-current", "runtime_provided", 100, "sha256");
        public ContentHash SourceContentHash => Hash("catalog");
        public IResourceCatalogInventory OpenReadOnly(string baseVpkPath) => this;
        public Task<ResourceCatalogEntry?> FindEntryAsync(string logicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourceCatalogEntry>> ListEntriesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceCatalogArtifact?> TryOpenAsync(string logicalPath, CancellationToken cancellationToken = default)
        {
            var item = expected.Single(item => item.LogicalPath == logicalPath);
            var bytes = System.Text.Encoding.UTF8.GetBytes(item.Role == "model" ? "model" : "dependency");
            return Task.FromResult<ResourceCatalogArtifact?>(new(new(logicalPath, inconsistent ? Hash("wrong") : ContentHash.Compute(bytes), bytes), "current-entry"));
        }
        public void Dispose() => Disposed = true;
    }

    private static ContentHash Hash(string value) => ContentHash.Compute(System.Text.Encoding.UTF8.GetBytes(value));

    private sealed class Fixture : IProjectWorkspace, IVpkPackageWorkspace, ICurrentSourceReader,
        ICurrentSourceCandidateVerifier, IAddonManagementApplication, IClock
    {
        private static readonly byte[] BuildBytes = "build"u8.ToArray();
        public Fixture()
        {
            Expected = [new("materials/test.vmat_c", "dependency", Hash("dependency"), 10), new("models/test.vmdl_c", "model", Hash("model"), 5)];
            Project = new()
            {
                ProjectId = "project",
                DependencyGraphComplete = true,
                Input = Manifest(Expected[1]),
                Dependencies = [Manifest(Expected[0])]
            };
            Plan = new("recipe", Expected[1].ContentHash, Hash("plan"), []) { Inputs = Expected.Select(item => new PlannedInput(item.LogicalPath, item.ContentHash, item.Size)).ToArray() };
            Build = new("build", Project.Input.LogicalPath, ContentHash.Compute(BuildBytes), BuildBytes.Length, Plan.Fingerprint, Hash("model-evidence"), Hash("md"));
            Package = new("package", Build.BuildId, null, null, Build.LogicalPath, null, Build.ContentHash, "candidate.vpk", Hash("package"), 99, Hash("pkg-evidence"), Hash("pkg-md")) { Mode = "minimal" };
            Request = new("addons", "project-root", Package.PackageId, Package.ContentHash, "explicit-current-source", "auto");
            Application = new(this, this, this, this, this, this);
        }
        public CurrentSourceInstallationApplication Application { get; }
        public CurrentSourceInstallationRequest Request { get; }
        public IReadOnlyList<CurrentSourceResource> Expected { get; }
        public ProjectManifest Project { get; private set; }
        public MutationPlan Plan { get; private set; }
        public PublishedBuild Build { get; }
        public PublishedVpkPackage Package { get; private set; }
        public int Reads { get; private set; }
        public int Verifications { get; private set; }
        public int Installs { get; private set; }
        public int EvidenceWrites { get; private set; }
        public string? ReadDefect { get; set; }
        public int DefectAtRead { get; set; } = 1;
        public string? ChangeDuringVerification { get; set; }
        public bool FailVerification { get; set; }
        public bool FailEvidence { get; set; }
        public bool WrongVerifiedCandidate { get; set; }
        public CancellationTokenSource? CancelDuringVerification { get; set; }
        private bool tamperedBuild;
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
        public void Mutate(string defect)
        {
            switch (defect)
            {
                case "package": Package = Package with { ContentHash = Hash("changed") }; break;
                case "plan": Plan = Plan with { Inputs = Plan.Inputs.Take(1).ToArray() }; break;
                case "graph": Project = Project with { DependencyGraphComplete = false }; break;
                case "mode": Package = Package with { Mode = "replace_source" }; break;
                case "replacement": Package = Package with { ReplacementEntryHash = Hash("wrong") }; break;
                case "build-bytes": tamperedBuild = true; break;
            }
        }
        public Task<IReadOnlyList<CurrentSourceObservation>> ReadAsync(string sourceLocator, IReadOnlyList<CurrentSourceResource> resources, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (Reads == DefectAtRead && ReadDefect == "read") throw new IOException("synthetic read failure");
            if (Reads == DefectAtRead && ReadDefect == "ambiguous") throw Errors.Input("CURRENT_SOURCE_RESOURCE_AMBIGUOUS", "ambiguous", "resolve");
            var observed = resources.Select(item => new CurrentSourceObservation(item, "synthetic", "current-entry", $"current-catalog-{Reads}", "sha256")).ToArray();
            if (Reads == DefectAtRead)
            {
                if (ReadDefect is "model" or "dependency")
                {
                    var index = Array.FindIndex(observed, item => item.Resource.Role == ReadDefect);
                    observed[index] = observed[index] with { Resource = observed[index].Resource with { ContentHash = Hash("drift") } };
                }
                else if (ReadDefect == "missing") observed = observed.Take(1).ToArray();
                else if (ReadDefect == "provenance") observed[0] = observed[0] with { CatalogIdentity = "" };
            }
            return Task.FromResult<IReadOnlyList<CurrentSourceObservation>>(observed);
        }
        public Task<CurrentSourceCandidateVerification> VerifyAsync(string projectRoot, string buildId, string packageId, CancellationToken cancellationToken = default)
        {
            Verifications++;
            if (FailVerification) throw Errors.Verification("SYNTHETIC_VERIFICATION_FAILED", "failed", "stop");
            var verified = new CurrentSourceCandidateVerification(Build, Package, Hash("verified-model"), Hash("verified-package"));
            if (WrongVerifiedCandidate) verified = verified with { Build = Build with { ContentHash = Hash("wrong verified candidate") } };
            if (ChangeDuringVerification is { } defect) Mutate(defect);
            CancelDuringVerification?.Cancel();
            return Task.FromResult(verified);
        }
        public Task<InstallationStatus> InstallAsync(string addonsRoot, string projectRoot, string packageId, string slot, CancellationToken cancellationToken = default)
        {
            Assert.Equal(1, EvidenceWrites); Installs++;
            return Task.FromResult(new InstallationStatus(new() { InstallationId = "installation", PackageId = packageId, PackageHash = Package.ContentHash }, "active", []));
        }
        public Task<ProjectManifest> LoadProjectAsync(string projectRoot, CancellationToken cancellationToken = default) => Task.FromResult(Project);
        public Task<(PublishedBuild Build, ArtifactContent Content)> LoadBuildAsync(string projectRoot, string buildId, CancellationToken cancellationToken = default) =>
            Task.FromResult((Build, new ArtifactContent(Build.LogicalPath, Build.ContentHash, tamperedBuild ? "tampered"u8.ToArray() : BuildBytes)));
        public Task<MutationPlan> LoadPlanAsync(string projectRoot, ContentHash fingerprint, CancellationToken cancellationToken = default) => Task.FromResult(Plan);
        public Task<VpkPackagePublicationResult> LoadPackageAsync(string projectRoot, string packageId, CancellationToken cancellationToken = default) => Task.FromResult(new VpkPackagePublicationResult(Package, "candidate", "{}", ""));
        public Task SaveEvidenceAsync(string projectRoot, string reportId, string json, string markdown, CancellationToken cancellationToken = default)
        {
            if (FailEvidence) throw new IOException("synthetic publication failure");
            SourcePreflightJson.Read(System.Text.Encoding.UTF8.GetBytes(json)); EvidenceWrites++; return Task.CompletedTask;
        }
        private static ProjectArtifactManifest Manifest(CurrentSourceResource item) => new() { LogicalPath = item.LogicalPath, ContentHash = item.ContentHash, Size = item.Size };
        public Task<ProjectManifest> PublishProjectAsync(ProjectPublicationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ArtifactContent> LoadInputAsync(string projectRoot, ProjectManifest project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ArtifactContent>> LoadDependenciesAsync(string projectRoot, ProjectManifest project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SavePlanAsync(string projectRoot, MutationPlan plan, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BuildPublicationResult> PublishBuildAsync(string projectRoot, BuildPublication publication, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VpkPackagePublicationResult> PublishPackageAsync(string projectRoot, VpkPackagePublication publication, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AddonInventory> InventoryAsync(string addonsRoot, string projectRoot, string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InstallationStatus> VerifyActiveAsync(string addonsRoot, string projectRoot, string installationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InstallationStatus> RollbackAsync(string addonsRoot, string projectRoot, string installationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RuntimeObservationResult> RecordRuntimeAsync(string projectRoot, string installationId, RuntimeObservationInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
