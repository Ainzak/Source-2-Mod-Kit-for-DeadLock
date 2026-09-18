using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class SchemaContractTests
{
    [Fact]
    public async Task SerializedVpkPackageEvidenceConformsToPublishedSchema()
    {
        var sourceHash = ContentHash.Compute("source-vpk"u8);
        var outputHash = ContentHash.Compute("output-vpk"u8);
        var sourceEntryHash = ContentHash.Compute("source-entry"u8);
        var outputEntryHash = ContentHash.Compute("output-entry"u8);
        var report = new VpkPackageEvidence
        {
            ReportId = "vpk-0123456789abcdef0123",
            CreatedUtc = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero),
            Command = "package.create",
            PackageId = "vpk-0123456789abcdef0123",
            BuildId = "build",
            SourceArchive = new VpkArchiveEvidence("H:\\source.vpk", sourceHash, 100, 2),
            OutputArchive = new VpkArchiveEvidence("H:\\candidate.vpk", outputHash, 101, 2),
            ReplacedEntry = new VpkEntryEvidence("models/hero.vmdl_c", sourceEntryHash, outputEntryHash, 1, 2, 10, 11),
            PackagedEntry = new VpkPackagedEntryEvidence("models/hero.vmdl_c", outputEntryHash, 2, 11),
            UnchangedEntryCount = 1,
            Boundaries = [new BoundaryEvidence("runtime", "untested", "No runtime validation was performed.")],
            ToolVersions = new Dictionary<string, string>(StringComparer.Ordinal) { ["s2modkit"] = "1" },
            Warnings = [],
        };
        var schema = await JsonSchema.FromFileAsync(GetSchemaPath("package.schema.json"), TestContext.Current.CancellationToken);

        var errors = schema.Validate(JsonDefaults.Serialize(report));

        Assert.Empty(errors);
    }

    [Fact]
    public async Task SerializedInstallationReceiptConformsToPublishedSchema()
    {
        var hash = ContentHash.Compute("package"u8);
        var receipt = new InstallationReceipt
        {
            InstallationId = "install-0123456789ab-test",
            ProjectId = "project",
            PackageId = "vpk-0123456789abcdef0123",
            CreatedUtc = DateTimeOffset.UnixEpoch,
            UpdatedUtc = DateTimeOffset.UnixEpoch,
            Status = "active",
            AddonsRoot = "H:\\fixtures\\addons",
            TargetFileName = "pak99_dir.vpk",
            StagingFileName = ".pak99_dir.vpk.install-test.s2modkit-staging",
            Slot = 99,
            LogicalPath = "models/hero.vmdl_c",
            PackageHash = hash,
            EntryContentHash = ContentHash.Compute("entry"u8),
        };
        var schema = await JsonSchema.FromFileAsync(GetSchemaPath("installation.schema.json"), TestContext.Current.CancellationToken);

        Assert.Empty(schema.Validate(JsonDefaults.Serialize(receipt)));
    }

    [Fact]
    public async Task SerializedRuntimeObservationConformsToPublishedSchema()
    {
        var observation = new RuntimeObservation
        {
            ObservationId = "runtime-0123456789ab-test",
            CreatedUtc = DateTimeOffset.UnixEpoch,
            Status = "passed",
            ProjectId = "project",
            PackageId = "vpk-0123456789abcdef0123",
            InstallationId = "install-0123456789ab-test",
            InstalledHash = ContentHash.Compute("package"u8),
            Checks = new RuntimeChecks("passed", "passed", "passed", "passed", "not_checked", "not_checked"),
            Boundaries = [new BoundaryEvidence("installation_hash", "passed", "Exact hash verified.")],
        };
        var schema = await JsonSchema.FromFileAsync(GetSchemaPath("runtime-observation.schema.json"), TestContext.Current.CancellationToken);

        Assert.Empty(schema.Validate(JsonDefaults.Serialize(observation)));
    }
}
