using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using S2ModKit.Application;
using S2ModKit.Cli;
using S2ModKit.Domain;

namespace S2ModKit.Cli.Tests;

public sealed partial class CliContractTests
{
    private static async Task<string> WriteCatalogueAsync(ContentHash directoryHash)
    {
        var path = Path.Combine(Path.GetTempPath(), $"s2modkit-catalogue-{Guid.NewGuid():N}.json");
        var catalogue = new HeroCatalogueDocument
        {
            CatalogueId = "deadlock.heroes",
            Revision = "test.1",
            Source = new HeroCatalogueSourceProvenance
            {
                ExpectedDirectoryHash = directoryHash,
            },
            Heroes =
            [
                new HeroCatalogueEntry
                {
                    HeroId = "haze",
                    DisplayName = "Haze",
                    Aliases = ["mist"],
                    Resources =
                    [
                        new HeroResourceLocator
                        {
                            ResourceId = "haze.primary",
                            DisplayName = "Primary model",
                            Role = HeroCatalogueContract.PrimaryModelRole,
                            LogicalPath = "models/heroes_staging/haze/haze.vmdl_c",
                            QualificationStatus = HeroCatalogueContract.RuntimeQualified,
                        },
                    ],
                },
            ],
        };
        await File.WriteAllTextAsync(path, JsonDefaults.Serialize(catalogue), TestContext.Current.CancellationToken);
        return path;
    }

    private sealed class FakeCatalogueInventoryFactory(ContentHash directoryHash) : IResourceCatalogInventoryFactory
    {
        public int OpenCount { get; private set; }

        public IResourceCatalogInventory OpenReadOnly(string baseVpkPath)
        {
            OpenCount++;
            return new FakeCatalogueInventory(directoryHash);
        }
    }

    private sealed class FakeCatalogueInventory(ContentHash directoryHash) : IResourceCatalogInventory, IDisposable
    {
        public ResourceCatalogDescriptor Descriptor { get; } = new("vpk", "test", "runtime_provided", 100, "metadata_only");

        public ContentHash SourceContentHash { get; } = directoryHash;

        public Task<IReadOnlyList<ResourceCatalogEntry>> ListEntriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ResourceCatalogEntry>>(
                [new ResourceCatalogEntry("models/heroes_staging/haze/haze.vmdl_c", "test::haze", 100)]);

        public Task<ResourceCatalogEntry?> FindEntryAsync(string logicalPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<ResourceCatalogEntry?>(null);

        public Task<ResourceCatalogArtifact?> TryOpenAsync(string logicalPath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Catalogue commands must not open model payloads.");

        public void Dispose()
        {
        }
    }

    private sealed class FakeCompatibilityScanner(ContentHash directoryHash) : ICompatibilityScanner
    {
        public Task<CompatibilityScanResult> ScanAsync(
            HeroCatalogueDocument catalogue,
            IResourceCatalogInventory inventory,
            CancellationToken cancellationToken = default)
        {
            var signature = StructuralSignature.Create(
                "component-discovery-v2",
                [new StructuralFact("lod.count", "1")]);
            var resource = new CompatibilityResourceResult(
                "haze",
                "Haze",
                "haze.primary",
                "Primary model",
                HeroCatalogueContract.PrimaryModelRole,
                "models/heroes_staging/haze/haze.vmdl_c",
                ContentHash.Compute("model"u8),
                5,
                CompatibilityScanContract.Supported,
                signature,
                [],
                []);
            return Task.FromResult(new CompatibilityScanResult(
                1,
                catalogue.CatalogueId,
                catalogue.Revision,
                directoryHash,
                new CompatibilityScanAnalyzer("test", "1", "test", "1", new Dictionary<string, string>()),
                [resource]));
        }
    }

    private sealed class FakeCompatibilityReportPublisher : ICompatibilityReportPublisher
    {
        public CompatibilityReport? PublishedReport { get; private set; }

        public Task<CompatibilityReportPublication> PublishAsync(
            string outputRoot,
            CompatibilityReport report,
            string json,
            string markdown,
            CancellationToken cancellationToken = default)
        {
            PublishedReport = report;
            return Task.FromResult(new CompatibilityReportPublication(
                report,
                Path.Combine(outputRoot, "result.json"),
                Path.Combine(outputRoot, "result.md")));
        }
    }

    private sealed class FakeApplication : IS2ModKitApplication
    {
        private readonly Exception? failure;
        private readonly ModelSnapshot snapshot;
        private readonly bool writeScaffoldedRecipe;
        private readonly bool affineAvailable;

        public FakeApplication(Exception? failure = null, ModelSnapshot? snapshot = null, bool writeScaffoldedRecipe = false, bool affineAvailable = false)
        {
            this.failure = failure;
            this.snapshot = snapshot ?? TestSnapshot;
            this.writeScaffoldedRecipe = writeScaffoldedRecipe;
            this.affineAvailable = affineAvailable;
        }

        public ContentHash? LastExpectedDirectoryHash { get; private set; }

        public string? LastVpkEntry { get; private set; }

        public RecipeDocument? LastRecipe { get; private set; }

        public RecipeScaffoldRequest? LastScaffoldRequest { get; private set; }

        public static ContentHash TestInputHash => TestHash;

        public Task<ProjectManifest> CreateProjectAsync(string projectRoot, string inputPath, string resourceRoot, CancellationToken cancellationToken = default) =>
            failure is null
                ? Task.FromResult(new ProjectManifest
                {
                    ProjectId = "test",
                    CreatedUtc = DateTimeOffset.UnixEpoch,
                    Input = new ProjectArtifactManifest
                    {
                        LogicalPath = "models/test.vmdl_c",
                        ContentHash = TestHash,
                        Size = 1,
                        ObjectRelativePath = $"objects/sha256/{TestHash}/content",
                        SourcePath = inputPath,
                    },
                    DependencyGraphComplete = true,
                    ResourceRoots = [resourceRoot],
                })
                : Task.FromException<ProjectManifest>(failure);

        public Task<ProjectManifest> CreateProjectAsync(
            string projectRoot,
            string inputPath,
            string resourceRoot,
            IReadOnlyList<string> runtimeResourceRoots,
            CancellationToken cancellationToken = default) =>
            CreateProjectAsync(projectRoot, inputPath, resourceRoot, cancellationToken);

        public Task<ProjectManifest> CreateVpkProjectAsync(
            string projectRoot,
            string baseVpkPath,
            string inputLogicalPath,
            ContentHash? expectedDirectoryHash = null,
            CancellationToken cancellationToken = default)
        {
            LastExpectedDirectoryHash = expectedDirectoryHash;
            LastVpkEntry = inputLogicalPath;
            return CreateProjectAsync(projectRoot, baseVpkPath, baseVpkPath, cancellationToken);
        }

        public Task<InspectionRunResult> InspectAsync(string projectRoot, CancellationToken cancellationToken = default) =>
            failure is null
                ? Task.FromResult(new InspectionRunResult(CreateManifest(projectRoot), snapshot))
                : Task.FromException<InspectionRunResult>(failure);

        public Task<ComponentDiscoveryResultV2> DiscoverComponentsAsync(
            string projectRoot,
            CancellationToken cancellationToken = default) =>
            failure is null
                ? Task.FromResult(affineAvailable ? CreateAffineDiscovery() : TestDiscovery)
                : Task.FromException<ComponentDiscoveryResultV2>(failure);

        public Task<RecipeScaffoldResult> ScaffoldRecipeAsync(
            string projectRoot,
            RecipeScaffoldRequest request,
            CancellationToken cancellationToken = default)
        {
            LastScaffoldRequest = request;
            if (failure is not null)
            {
                return Task.FromException<RecipeScaffoldResult>(failure);
            }

            RecipeOperation operation = request.Intent == RecipeScaffoldContract.RemoveIntent
                ? new RemoveComponentOperation
                {
                    OperationId = "remove-test",
                    Selector = new ComponentSelector
                    {
                        Kind = "draw_call_ids",
                        DrawCallIds = ["dc_0123456789abcdef01234567"],
                    },
                    ExpectedMatchesByLod = new Dictionary<string, int> { ["0"] = 1 },
                }
                : new TransformComponentOperation
                {
                    OperationId = "transform-test",
                    Version = request.Intent == RecipeScaffoldContract.AffineIntent ? 4 : 1,
                    Granularity = request.Intent == RecipeScaffoldContract.AffineIntent ? "draw_call_vertices" : "draw_call_owned_vertices",
                    Selector = new ComponentSelector
                    {
                        Kind = "draw_call_ids",
                        DrawCallIds = ["dc_0123456789abcdef01234567"],
                    },
                    ExpectedMatchesByLod = new Dictionary<string, int> { ["0"] = 1 },
                    ExpectedVerticesByLod = new Dictionary<string, int> { ["0"] = 12 },
                    Transform = new ComponentTransform
                    {
                        Pivot = request.Affine?.Pivot is { } affinePivot
                            ? affinePivot.Kind == "explicit_point" ? affinePivot : affinePivot with { ReferenceLod = 0 }
                            : new TransformPivot { Kind = "selection_bounds_center", ReferenceLod = 0 },
                        UniformScale = request.Intent == RecipeScaffoldContract.AffineIntent ? 0f : request.UniformScale ?? 1f,
                        Scale = request.Affine?.Scale,
                        Rotation = request.Affine?.Rotation,
                        Frame = request.Affine?.Frame,
                        Translation = new TransformVector3
                        {
                            X = request.TranslationX ?? 0f,
                            Y = request.TranslationY ?? 0f,
                            Z = request.TranslationZ ?? 0f,
                        },
                    },
                    Limits = new TransformLimits { MaximumVertexDisplacement = request.MaximumVertexDisplacement ?? 64f },
                };
            var recipe = new RecipeDocument
            {
                SchemaVersion = request.Intent == RecipeScaffoldContract.RemoveIntent ? 1 : request.Intent == RecipeScaffoldContract.AffineIntent ? 5 : 2,
                RecipeId = "scaffold-test",
                InputHash = TestHash,
                Operations = [operation],
            };
            var recipeBytes = JsonDefaults.SerializeToUtf8(recipe);
            if (writeScaffoldedRecipe)
            {
                File.WriteAllBytes(request.OutputPath, recipeBytes);
            }

            return Task.FromResult(new RecipeScaffoldResult(
                request.OutputPath,
                ContentHash.Compute(recipeBytes),
                TestDiscovery.DiscoveryFingerprint,
                request.ComponentIds,
                recipe));
        }

        public Task<PlanRunResult> PlanAsync(string projectRoot, RecipeDocument recipe, CancellationToken cancellationToken = default)
        {
            LastRecipe = recipe;
            if (failure is not null)
            {
                return Task.FromException<PlanRunResult>(failure);
            }

            var selected = new SelectedDrawCall(
                0,
                "models/test.vmdl_c",
                0,
                1,
                "dc_0123456789abcdef01234567",
                "materials/accessory.vmat",
                0,
                0,
                3);
            var operation = new PlannedOperation(
                recipe.Operations.Single().OperationId,
                recipe.Operations.Single().OperationKind,
                recipe.Operations.Single().Version,
                [selected],
                [new PlannedTargetBlock(1, "MDAT", TestHash)]);
            var plan = new MutationPlan(recipe.RecipeId, recipe.InputHash, TestHash, [operation]);
            var evidence = new EvidenceReport
            {
                ReportId = "plan",
                CreatedUtc = DateTimeOffset.UnixEpoch,
                Command = "plan",
                Status = "passed",
                Input = new ArtifactEvidence("models/test.vmdl_c", TestHash, 1),
                PlanFingerprint = plan.Fingerprint,
            };
            return Task.FromResult(new PlanRunResult(plan, evidence));
        }

        public Task<BuildRunResult> BuildAsync(string projectRoot, RecipeDocument recipe, CancellationToken cancellationToken = default) =>
            failure is null
                ? Task.FromResult(new BuildRunResult(CreateBuild(), CreateBuildEvidence()))
                : Task.FromException<BuildRunResult>(failure);

        public Task<VerifyRunResult> VerifyAsync(string projectRoot, string buildId, CancellationToken cancellationToken = default) =>
            failure is null
                ? Task.FromResult(new VerifyRunResult(CreateBuild(), CreateBuildEvidence()))
                : Task.FromException<VerifyRunResult>(failure);

        private static PublishedBuild CreateBuild() =>
            new(
                "build-test",
                "models/test.vmdl_c",
                TestHash,
                1,
                TestHash,
                ContentHash.Compute("build-json"u8),
                ContentHash.Compute("build-markdown"u8));

        private static EvidenceReport CreateBuildEvidence() =>
            new()
            {
                ReportId = "build-test",
                CreatedUtc = DateTimeOffset.UnixEpoch,
                Command = "build",
                Status = "passed",
                Input = new ArtifactEvidence("models/test.vmdl_c", TestHash, 1),
                Output = new ArtifactEvidence("models/test.vmdl_c", TestHash, 1),
                PlanFingerprint = TestHash,
            };

        private static ContentHash TestHash { get; } = ContentHash.Compute([1]);

        private static ProjectManifest CreateManifest(string projectRoot) => new()
        {
            ProjectId = "test",
            CreatedUtc = DateTimeOffset.UnixEpoch,
            Input = new ProjectArtifactManifest
            {
                LogicalPath = "models/test.vmdl_c",
                ContentHash = TestHash,
                Size = 1,
                ObjectRelativePath = $"objects/sha256/{TestHash}/content",
                SourcePath = projectRoot,
            },
            DependencyGraphComplete = true,
            ResourceRoots = [projectRoot],
        };

        private static ModelSnapshot TestSnapshot { get; } = new(
            new ArtifactSnapshot("models/test.vmdl_c", TestHash, 1, []),
            []);

        private static ComponentDiscoveryResultV2 TestDiscovery { get; } = CreateDiscovery();

        private static ComponentDiscoveryResultV2 CreateAffineDiscovery()
        {
            var affine = new ComponentCapability(
                "transform_component",
                4,
                ComponentDiscoveryContract.Available,
                [new ComponentCapabilityReason("COMPONENT_CAPABILITY_AVAILABLE", "Affine geometry is available.")],
                [new ComponentGeometryLodFacts(0, 12, ContentHash.Compute("vertices"u8), true)]);
            return TestDiscovery with
            {
                Candidates = TestDiscovery.Candidates
                    .Select(candidate => candidate with { Capabilities = [.. candidate.Capabilities, affine] })
                    .ToArray(),
            };
        }

        private static ComponentDiscoveryResultV2 CreateDiscovery()
        {
            var model = new ComponentModelIdentity("models/test.vmdl_c", TestHash, 1);
            var vertices = ContentHash.Compute("vertices"u8);
            var capabilities = new ComponentCapability[]
            {
                new(
                    "remove_component",
                    1,
                    ComponentDiscoveryContract.Available,
                    [new ComponentCapabilityReason("COMPONENT_CAPABILITY_AVAILABLE", "Complete LOD coverage.")],
                    []),
                new(
                    "transform_component",
                    1,
                    ComponentDiscoveryContract.Available,
                    [new ComponentCapabilityReason("COMPONENT_CAPABILITY_AVAILABLE", "Exclusive geometry.")],
                    [new ComponentGeometryLodFacts(0, 12, vertices, true)]),
            };
            return new ComponentDiscoveryResultV2(
                ComponentDiscoveryV2Contract.SchemaVersion,
                model,
                ContentHash.Compute("discovery"u8),
                new ComponentCapabilityAnalyzerIdentity("test-analyzer", "1", new Dictionary<string, string>()),
                [
                    new MaterialGroupComponentCandidateV2(
                        "cmp_0123456789abcdef01234567",
                        model,
                        "materials/accessory.vmat",
                        "accessory",
                        [new ComponentCandidateLod(0, ["dc_0123456789abcdef01234567"], 1)],
                        capabilities),
                    new MeshLineageComponentCandidateV2(
                        "cmp_111111111111111111111111",
                        model,
                        "accessory_mesh",
                        "accessory_mesh",
                        "accessory_mesh",
                        ["materials/accessory.vmat"],
                        [new MeshLineageCandidateLod(
                            0,
                            "models/test.vmdl_c",
                            0,
                            1,
                            ContentHash.Compute("mesh"u8),
                            "accessory_mesh",
                            ["materials/accessory.vmat"],
                            ["dc_0123456789abcdef01234567"],
                            1)],
                        capabilities),
                ],
                []);
        }
    }

    private static ModelSnapshot GeometrySnapshot { get; } = CreateGeometrySnapshot();

    private static ModelSnapshot CreateGeometrySnapshot()
    {
        var drawCall = DrawCallSnapshot.Create("models/test.vmdl_c", 0, 0, 0, "materials/test.vmat", 0, 3);
        var geometryHash = ContentHash.Compute("geometry"u8);
        var mesh = new MeshSnapshot("models/test.vmdl_c", 0, 1, geometryHash, [drawCall])
        {
            Geometry = new MeshGeometrySnapshot(
                "ready",
                "synthetic geometry",
                [new VertexBufferSnapshot(0, 2, 3, 16, geometryHash, geometryHash, new PositionLayout("R32G32B32_FLOAT", 0, 16))],
                [new IndexBufferSnapshot(0, 3, 3, 2, geometryHash, geometryHash)],
                [new DrawCallGeometrySnapshot(
                    drawCall.Id,
                    0,
                    0,
                    0,
                    3,
                    3,
                    geometryHash,
                    new GeometryBounds(new TransformVector3 { X = 0, Y = 1, Z = 2 }, new TransformVector3 { X = 3, Y = 4, Z = 5 }),
                    true)],
                new GeometryCodecIdentity("fake", "test", "test", geometryHash, "1")),
        };
        return new ModelSnapshot(
            new ArtifactSnapshot("models/test.vmdl_c", FakeApplication.TestInputHash, 1, []),
            [new LodSnapshot(0, [mesh])]);
    }

    private sealed class FakePackagingApplication : IVpkPackagingApplication
    {
        public ContentHash LastSourceHash { get; private set; }

        public bool LastRequireExternal { get; private set; }

        public Task<VpkPackageRunResult> CreateAsync(
            string projectRoot,
            string buildId,
            string sourceVpkPath,
            ContentHash expectedSourceVpkHash,
            bool requireExternalVerifier,
            CancellationToken cancellationToken = default)
        {
            LastSourceHash = expectedSourceVpkHash;
            LastRequireExternal = requireExternalVerifier;
            return Task.FromResult(CreateResult(buildId, sourceVpkPath, expectedSourceVpkHash));
        }

        public Task<VpkPackageRunResult> CreateMinimalAsync(
            string projectRoot,
            string buildId,
            bool requireExternalVerifier,
            CancellationToken cancellationToken = default)
        {
            LastRequireExternal = requireExternalVerifier;
            return Task.FromResult(CreateResult(buildId, "minimal.vpk", ContentHash.Compute("minimal-source"u8)));
        }

        public Task<VpkPackageRunResult> VerifyAsync(
            string projectRoot,
            string packageId,
            bool requireExternalVerifier,
            CancellationToken cancellationToken = default)
        {
            LastRequireExternal = requireExternalVerifier;
            return Task.FromResult(CreateResult("build", "source.vpk", ContentHash.Compute("source-vpk"u8)));
        }

        public Task<VpkPackageExportResult> ExportAsync(
            string projectRoot,
            string packageId,
            string outputPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new VpkPackageExportResult(packageId, Path.GetFullPath(outputPath), ContentHash.Compute("output"u8), 100));

        private static VpkPackageRunResult CreateResult(string buildId, string sourcePath, ContentHash sourceHash)
        {
            const string packageId = "vpk-0123456789abcdef0123";
            var outputHash = ContentHash.Compute("output"u8);
            var sourceEntryHash = ContentHash.Compute("source-entry"u8);
            var outputEntryHash = ContentHash.Compute("output-entry"u8);
            var package = new PublishedVpkPackage(
                packageId,
                buildId,
                sourcePath,
                sourceHash,
                "models/hero.vmdl_c",
                sourceEntryHash,
                outputEntryHash,
                $"packages/{packageId}/candidate.vpk",
                outputHash,
                100,
                ContentHash.Compute("json"u8),
                ContentHash.Compute("markdown"u8));
            var evidence = new VpkPackageEvidence
            {
                ReportId = packageId,
                CreatedUtc = DateTimeOffset.UnixEpoch,
                Command = "package.create",
                PackageId = packageId,
                BuildId = buildId,
                SourceArchive = new VpkArchiveEvidence(sourcePath, sourceHash, 100, 1),
                OutputArchive = new VpkArchiveEvidence("candidate.vpk", outputHash, 100, 1),
                ReplacedEntry = new VpkEntryEvidence("models/hero.vmdl_c", sourceEntryHash, outputEntryHash, 1, 2, 10, 10),
            };
            return new VpkPackageRunResult(package, evidence);
        }
    }

    private sealed class FakeAddonManagementApplication : IAddonManagementApplication
    {
        public string? LastInstallSlot { get; private set; }

        public bool RolledBack { get; private set; }

        public Task<AddonInventory> InventoryAsync(string addonsRoot, string projectRoot, string packageId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AddonInventory(addonsRoot, packageId, ContentHash.Compute("package"u8), "models/hero.vmdl_c", null, [], [], [99]));

        public Task<InstallationStatus> InstallAsync(string addonsRoot, string projectRoot, string packageId, string slot, CancellationToken cancellationToken = default)
        {
            LastInstallSlot = slot;
            return Task.FromResult(CreateStatus("active", slot == "auto" ? 99 : int.Parse(slot, CultureInfo.InvariantCulture), addonsRoot));
        }

        public Task<InstallationStatus> VerifyActiveAsync(string addonsRoot, string projectRoot, string installationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateStatus("active"));

        public Task<InstallationStatus> RollbackAsync(string addonsRoot, string projectRoot, string installationId, CancellationToken cancellationToken = default)
        {
            RolledBack = true;
            return Task.FromResult(CreateStatus("rolled_back", 99, addonsRoot));
        }

        public Task<RuntimeObservationResult> RecordRuntimeAsync(string projectRoot, string installationId, RuntimeObservationInput input, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeObservationResult(
                new RuntimeObservation
                {
                    ObservationId = "runtime-test",
                    CreatedUtc = DateTimeOffset.UnixEpoch,
                    Status = input.Status,
                    ProjectId = "test",
                    PackageId = "vpk-0123456789abcdef0123",
                    InstallationId = installationId,
                    InstalledHash = ContentHash.Compute("package"u8),
                    Checks = input.Checks,
                    Notes = input.Notes,
                },
                "{}",
                "# observation"));

        private static InstallationStatus CreateStatus(string status, int slot = 99, string addonsRoot = "addons") =>
            new(
                new InstallationReceipt
                {
                    InstallationId = "install-test",
                    ProjectId = "test",
                    PackageId = "vpk-0123456789abcdef0123",
                    CreatedUtc = DateTimeOffset.UnixEpoch,
                    UpdatedUtc = DateTimeOffset.UnixEpoch,
                    Status = status,
                    AddonsRoot = addonsRoot,
                    TargetFileName = $"pak{slot:00}_dir.vpk",
                    StagingFileName = $".pak{slot:00}_dir.vpk.install-test.s2modkit-staging",
                    Slot = slot,
                    LogicalPath = "models/hero.vmdl_c",
                    PackageHash = ContentHash.Compute("package"u8),
                    EntryContentHash = ContentHash.Compute("entry"u8),
                },
                status,
                [new BoundaryEvidence("test", "passed", "test")]);
    }

    private sealed class NarrowTextWriter(int width) : TextWriter
    {
        private readonly StringBuilder content = new();
        private int column;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\r')
            {
                return;
            }

            if (value == '\n')
            {
                content.Append(value);
                column = 0;
                return;
            }

            if (column == width)
            {
                content.Append('\n');
                column = 0;
            }

            content.Append(value);
            column++;
        }

        public override void Write(string? value)
        {
            if (value is null)
            {
                return;
            }

            foreach (var character in value)
            {
                Write(character);
            }
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }

        public override void WriteLine() => Write('\n');

        public override string ToString() => content.ToString();
    }
}
