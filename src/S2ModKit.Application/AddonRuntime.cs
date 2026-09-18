using System.Text.Json;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed record AddonFileDescriptor(string FileName, string FullPath, long Size, bool IsReparsePoint);

public sealed record AddonRootSnapshot(
    string AddonsRoot,
    ContentHash? DmmStateHash,
    IReadOnlyList<AddonFileDescriptor> ActiveVpkFiles);

public sealed record AddonArchiveProbe(
    string ArchivePath,
    int EntryCount,
    bool ContainsLogicalPath,
    ContentHash? EntryContentHash,
    uint? EntryCrc32,
    long? EntrySize);

public sealed record AddonArchiveInventory(
    string FileName,
    string FullPath,
    int? Slot,
    int EntryCount,
    bool ContainsCandidatePath,
    ContentHash? CandidateEntryHash);

public sealed record AddonCollision(string FileName, int? Slot, string LogicalPath, ContentHash? EntryContentHash);

public sealed record AddonInventory(
    string AddonsRoot,
    string PackageId,
    ContentHash PackageHash,
    string LogicalPath,
    ContentHash? DmmStateHash,
    IReadOnlyList<AddonArchiveInventory> Archives,
    IReadOnlyList<AddonCollision> Collisions,
    IReadOnlyList<int> AvailableAutomaticSlots);

public sealed record ActiveInstallationMarker(
    int SchemaVersion,
    string InstallationId,
    string ProjectId,
    string PackageId,
    string TargetFileName,
    ContentHash PackageHash);

public sealed record InstallationReceipt
{
    public int SchemaVersion { get; init; } = 1;

    public string InstallationId { get; init; } = string.Empty;

    public string ProjectId { get; init; } = string.Empty;

    public string PackageId { get; init; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }

    public string Status { get; init; } = "prepared";

    public string AddonsRoot { get; init; } = string.Empty;

    public string TargetFileName { get; init; } = string.Empty;

    public string StagingFileName { get; init; } = string.Empty;

    public string? DisabledFileName { get; init; }

    public int Slot { get; init; }

    public string LogicalPath { get; init; } = string.Empty;

    public ContentHash PackageHash { get; init; }

    public ContentHash EntryContentHash { get; init; }

    public ContentHash? DmmStateHashBefore { get; init; }

    public ContentHash? DmmStateHashAfter { get; init; }

    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record InstallationStatus(
    InstallationReceipt Receipt,
    string Status,
    IReadOnlyList<BoundaryEvidence> Boundaries);

public sealed record RuntimeChecks(
    string TargetComponent,
    string PreservedMaterialsAndParts,
    string Animations,
    string LodTransitions,
    string MenuPreview,
    string DeathAndRespawn);

public sealed record RuntimeObservationInput
{
    public int SchemaVersion { get; init; } = 1;

    public string Status { get; init; } = string.Empty;

    public required RuntimeChecks Checks { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];

    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record RuntimeObservation
{
    public int SchemaVersion { get; init; } = 1;

    public string ObservationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; init; }

    public string ProofLevel { get; init; } = "player_observed";

    public string Status { get; init; } = string.Empty;

    public string ProjectId { get; init; } = string.Empty;

    public string PackageId { get; init; } = string.Empty;

    public string InstallationId { get; init; } = string.Empty;

    public ContentHash InstalledHash { get; init; }

    public required RuntimeChecks Checks { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];

    public IReadOnlyList<BoundaryEvidence> Boundaries { get; init; } = [];

    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record RuntimeObservationResult(RuntimeObservation Observation, string Json, string Markdown);

public interface IAddonFileSystem
{
    Task<AddonRootSnapshot> SnapshotAsync(string addonsRoot, CancellationToken cancellationToken = default);

    Task<ContentHash> ComputeFileHashAsync(string path, CancellationToken cancellationToken = default);

    Task<ActiveInstallationMarker?> LoadActiveMarkerAsync(string addonsRoot, CancellationToken cancellationToken = default);

    Task CreateActiveMarkerAsync(string addonsRoot, ActiveInstallationMarker marker, CancellationToken cancellationToken = default);

    Task RemoveActiveMarkerAsync(string addonsRoot, ActiveInstallationMarker marker, CancellationToken cancellationToken = default);

    Task StageAndActivateAsync(
        string addonsRoot,
        string sourcePath,
        string stagingFileName,
        string targetFileName,
        ContentHash expectedHash,
        CancellationToken cancellationToken = default);

    Task<string> DisableAsync(
        string addonsRoot,
        string targetFileName,
        string disabledFileName,
        ContentHash expectedHash,
        CancellationToken cancellationToken = default);

    Task AbandonStagingAsync(string addonsRoot, string stagingFileName, CancellationToken cancellationToken = default);
}

public interface IAddonArchiveInspector
{
    Task<AddonArchiveProbe> ProbeAsync(
        string archivePath,
        string logicalPath,
        bool readMatchingEntry,
        CancellationToken cancellationToken = default);
}

public interface IInstallationStore
{
    Task CreateInstallationAsync(string projectRoot, InstallationReceipt receipt, CancellationToken cancellationToken = default);

    Task UpdateInstallationAsync(string projectRoot, InstallationReceipt receipt, string expectedStatus, CancellationToken cancellationToken = default);

    Task<InstallationReceipt> LoadInstallationAsync(string projectRoot, string installationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InstallationReceipt>> ListInstallationsAsync(string projectRoot, CancellationToken cancellationToken = default);

    Task<RuntimeObservationResult> PublishRuntimeObservationAsync(
        string projectRoot,
        RuntimeObservation observation,
        string json,
        string markdown,
        CancellationToken cancellationToken = default);
}

public interface IRuntimeObservationRenderer
{
    string RenderJson(RuntimeObservation observation);

    string RenderMarkdown(RuntimeObservation observation);
}

public interface IAddonManagementApplication
{
    Task<AddonInventory> InventoryAsync(string addonsRoot, string projectRoot, string packageId, CancellationToken cancellationToken = default);

    Task<InstallationStatus> InstallAsync(string addonsRoot, string projectRoot, string packageId, string slot, CancellationToken cancellationToken = default);

    Task<InstallationStatus> VerifyActiveAsync(string addonsRoot, string projectRoot, string installationId, CancellationToken cancellationToken = default);

    Task<InstallationStatus> RollbackAsync(string addonsRoot, string projectRoot, string installationId, CancellationToken cancellationToken = default);

    Task<RuntimeObservationResult> RecordRuntimeAsync(string projectRoot, string installationId, RuntimeObservationInput input, CancellationToken cancellationToken = default);
}

public sealed class AddonManagementApplication : IAddonManagementApplication
{
    private readonly IProjectWorkspace projectWorkspace;
    private readonly IVpkPackageWorkspace packageWorkspace;
    private readonly IInstallationStore installationStore;
    private readonly IAddonFileSystem fileSystem;
    private readonly IAddonArchiveInspector archiveInspector;
    private readonly IRuntimeObservationRenderer runtimeRenderer;
    private readonly IClock clock;

    public AddonManagementApplication(
        IProjectWorkspace projectWorkspace,
        IVpkPackageWorkspace packageWorkspace,
        IInstallationStore installationStore,
        IAddonFileSystem fileSystem,
        IAddonArchiveInspector archiveInspector,
        IRuntimeObservationRenderer runtimeRenderer,
        IClock clock)
    {
        this.projectWorkspace = projectWorkspace;
        this.packageWorkspace = packageWorkspace;
        this.installationStore = installationStore;
        this.fileSystem = fileSystem;
        this.archiveInspector = archiveInspector;
        this.runtimeRenderer = runtimeRenderer;
        this.clock = clock;
    }

    public async Task<AddonInventory> InventoryAsync(
        string addonsRoot,
        string projectRoot,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        var package = await packageWorkspace.LoadPackageAsync(projectRoot, packageId, cancellationToken).ConfigureAwait(false);
        var snapshot = await fileSystem.SnapshotAsync(addonsRoot, cancellationToken).ConfigureAwait(false);
        var archives = new List<AddonArchiveInventory>(snapshot.ActiveVpkFiles.Count);
        var collisions = new List<AddonCollision>();
        foreach (var file in snapshot.ActiveVpkFiles.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase))
        {
            if (file.IsReparsePoint)
            {
                throw Errors.Verification("ADDON_ARCHIVE_REPARSE_POINT", $"Active archive '{file.FullPath}' is a reparse point.", "Replace it with a regular file before managed installation.");
            }

            var probe = await archiveInspector.ProbeAsync(file.FullPath, package.Package.EntryLogicalPath, readMatchingEntry: true, cancellationToken).ConfigureAwait(false);
            var slot = TryParseSlot(file.FileName);
            archives.Add(new AddonArchiveInventory(file.FileName, file.FullPath, slot, probe.EntryCount, probe.ContainsLogicalPath, probe.EntryContentHash));
            if (probe.ContainsLogicalPath)
            {
                collisions.Add(new AddonCollision(file.FileName, slot, package.Package.EntryLogicalPath, probe.EntryContentHash));
            }
        }

        var occupiedSlots = archives.Where(item => item.Slot is not null).Select(item => item.Slot!.Value).ToHashSet();
        var highestCollision = collisions.Where(item => item.Slot is not null).Select(item => item.Slot!.Value).DefaultIfEmpty(-1).Max();
        var automaticSlots = Enumerable.Range(90, 10)
            .Where(candidate => !occupiedSlots.Contains(candidate) && candidate > highestCollision)
            .OrderDescending()
            .ToArray();
        return new AddonInventory(
            snapshot.AddonsRoot,
            package.Package.PackageId,
            package.Package.ContentHash,
            package.Package.EntryLogicalPath,
            snapshot.DmmStateHash,
            archives,
            collisions,
            automaticSlots);
    }

    public async Task<InstallationStatus> InstallAsync(
        string addonsRoot,
        string projectRoot,
        string packageId,
        string slot,
        CancellationToken cancellationToken = default)
    {
        var project = await projectWorkspace.LoadProjectAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        var package = await packageWorkspace.LoadPackageAsync(projectRoot, packageId, cancellationToken).ConfigureAwait(false);
        await RequirePackageEntryAsync(package, cancellationToken).ConfigureAwait(false);
        var existingMarker = await fileSystem.LoadActiveMarkerAsync(addonsRoot, cancellationToken).ConfigureAwait(false);
        if (existingMarker is not null)
        {
            throw Errors.Verification("ADDON_INSTALLATION_ALREADY_ACTIVE", $"Installation '{existingMarker.InstallationId}' already owns the S2ModKit active marker.", "Verify or roll back the existing installation before installing another package.");
        }

        var pending = await installationStore.ListInstallationsAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (pending.Any(item => item.Status is "prepared" or "active"))
        {
            throw Errors.Verification("ADDON_PROJECT_INSTALLATION_PENDING", "This project already has a prepared or active installation receipt.", "Verify or roll back that receipt before installing another package.");
        }

        var inventory = await InventoryAsync(addonsRoot, projectRoot, packageId, cancellationToken).ConfigureAwait(false);
        var selectedSlot = SelectSlot(slot, inventory);
        var targetFileName = $"pak{selectedSlot:00}_dir.vpk";
        if (inventory.Archives.Any(item => string.Equals(item.FileName, targetFileName, StringComparison.OrdinalIgnoreCase)))
        {
            throw Errors.Verification("ADDON_SLOT_OCCUPIED", $"Addon slot '{targetFileName}' is occupied.", "Choose an empty slot; S2ModKit never overwrites an existing archive.");
        }

        RequireWinningSlot(inventory.Collisions, selectedSlot);
        var now = clock.UtcNow.ToUniversalTime();
        var installationId = $"install-{package.Package.ContentHash.Value[..12]}-{Guid.NewGuid():N}"[..29];
        var stagingFileName = $".{targetFileName}.{installationId}.s2modkit-staging";
        var receipt = new InstallationReceipt
        {
            InstallationId = installationId,
            ProjectId = project.ProjectId,
            PackageId = packageId,
            CreatedUtc = now,
            UpdatedUtc = now,
            AddonsRoot = inventory.AddonsRoot,
            TargetFileName = targetFileName,
            StagingFileName = stagingFileName,
            Slot = selectedSlot,
            LogicalPath = package.Package.EntryLogicalPath,
            PackageHash = package.Package.ContentHash,
            EntryContentHash = package.Package.ReplacementEntryHash,
            DmmStateHashBefore = inventory.DmmStateHash,
        };
        await installationStore.CreateInstallationAsync(projectRoot, receipt, cancellationToken).ConfigureAwait(false);
        var marker = ToMarker(receipt);
        await fileSystem.CreateActiveMarkerAsync(addonsRoot, marker, cancellationToken).ConfigureAwait(false);
        await fileSystem.StageAndActivateAsync(
            addonsRoot,
            package.PackagePath,
            stagingFileName,
            targetFileName,
            package.Package.ContentHash,
            cancellationToken).ConfigureAwait(false);
        return await VerifyActiveAsync(addonsRoot, projectRoot, installationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InstallationStatus> VerifyActiveAsync(
        string addonsRoot,
        string projectRoot,
        string installationId,
        CancellationToken cancellationToken = default)
    {
        var receipt = await installationStore.LoadInstallationAsync(projectRoot, installationId, cancellationToken).ConfigureAwait(false);
        RequireRoot(receipt, addonsRoot);
        if (receipt.Status == "rolled_back")
        {
            throw Errors.Verification("ADDON_INSTALLATION_ROLLED_BACK", $"Installation '{installationId}' has already been rolled back.", "Install a package again to create a new receipt.");
        }

        var marker = await fileSystem.LoadActiveMarkerAsync(addonsRoot, cancellationToken).ConfigureAwait(false);
        if (marker != ToMarker(receipt))
        {
            throw Errors.Verification("ADDON_ACTIVE_MARKER_MISMATCH", "The addon-root active marker does not identify this receipt.", "Do not modify any archive; reconcile or roll back the prepared receipt.");
        }

        var snapshot = await fileSystem.SnapshotAsync(addonsRoot, cancellationToken).ConfigureAwait(false);
        if (snapshot.DmmStateHash != receipt.DmmStateHashBefore)
        {
            throw Errors.Verification("DMM_STATE_CHANGED", ".dmm.json changed during or after managed installation.", "Roll back the S2ModKit installation and let the mod manager reach a stable state before retrying.");
        }

        var target = snapshot.ActiveVpkFiles.SingleOrDefault(item => string.Equals(item.FileName, receipt.TargetFileName, StringComparison.OrdinalIgnoreCase));
        if (target is null || target.IsReparsePoint)
        {
            throw Errors.Verification("ADDON_INSTALLED_FILE_MISSING", $"Installed archive '{receipt.TargetFileName}' is missing or unsafe.", "Do not claim the package is active; restore or roll back using the receipt.");
        }

        var installedHash = await fileSystem.ComputeFileHashAsync(target.FullPath, cancellationToken).ConfigureAwait(false);
        if (installedHash != receipt.PackageHash)
        {
            throw Errors.Verification("ADDON_INSTALLED_HASH_DRIFT", $"Installed archive hash {installedHash} differs from receipt {receipt.PackageHash}.", "Do not modify the file through S2ModKit; identify the external change first.");
        }

        var probe = await archiveInspector.ProbeAsync(target.FullPath, receipt.LogicalPath, readMatchingEntry: true, cancellationToken).ConfigureAwait(false);
        if (!probe.ContainsLogicalPath || probe.EntryContentHash != receipt.EntryContentHash)
        {
            throw Errors.Verification("ADDON_INSTALLED_ENTRY_DRIFT", "Installed archive does not contain the exact expected compiled-model entry.", "Roll back only if the archive hash still matches the receipt; otherwise inspect the external modification.");
        }

        var package = await packageWorkspace.LoadPackageAsync(projectRoot, receipt.PackageId, cancellationToken).ConfigureAwait(false);
        var inventory = await InventoryAsync(addonsRoot, projectRoot, receipt.PackageId, cancellationToken).ConfigureAwait(false);
        RequireWinningSlot(inventory.Collisions.Where(item => !string.Equals(item.FileName, receipt.TargetFileName, StringComparison.OrdinalIgnoreCase)), receipt.Slot);
        if (package.Package.ContentHash != installedHash)
        {
            throw Errors.Verification("ADDON_PACKAGE_RECEIPT_DRIFT", "The project package no longer matches the installed receipt.", "Preserve the installation and investigate workspace corruption before rollback.");
        }

        var boundaries = new[]
        {
            new BoundaryEvidence("installation_hash", "passed", $"Installed archive matches package {installedHash}."),
            new BoundaryEvidence("installation_entry", "passed", $"Logical path {receipt.LogicalPath} matches build entry {receipt.EntryContentHash}."),
            new BoundaryEvidence("addon_priority", "passed", $"Slot {receipt.Slot:00} is above every known colliding numbered archive."),
            new BoundaryEvidence("dmm_state", "passed", ".dmm.json is byte-identical to the pre-install state."),
        };
        if (receipt.Status == "prepared")
        {
            var active = receipt with { Status = "active", UpdatedUtc = clock.UtcNow.ToUniversalTime(), DmmStateHashAfter = snapshot.DmmStateHash };
            await installationStore.UpdateInstallationAsync(projectRoot, active, "prepared", cancellationToken).ConfigureAwait(false);
            receipt = active;
        }

        return new InstallationStatus(receipt, "active", boundaries);
    }

    public async Task<InstallationStatus> RollbackAsync(
        string addonsRoot,
        string projectRoot,
        string installationId,
        CancellationToken cancellationToken = default)
    {
        var receipt = await installationStore.LoadInstallationAsync(projectRoot, installationId, cancellationToken).ConfigureAwait(false);
        RequireRoot(receipt, addonsRoot);
        if (receipt.Status == "rolled_back")
        {
            return new InstallationStatus(receipt, "rolled_back", [new BoundaryEvidence("rollback", "passed", "The receipt was already rolled back; no file was changed.")]);
        }

        var snapshot = await fileSystem.SnapshotAsync(addonsRoot, cancellationToken).ConfigureAwait(false);
        var target = snapshot.ActiveVpkFiles.SingleOrDefault(item => string.Equals(item.FileName, receipt.TargetFileName, StringComparison.OrdinalIgnoreCase));
        var disabledName = $"{receipt.TargetFileName}.s2modkit-disabled-{receipt.InstallationId}";
        if (target is not null)
        {
            _ = await fileSystem.DisableAsync(addonsRoot, receipt.TargetFileName, disabledName, receipt.PackageHash, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await fileSystem.AbandonStagingAsync(addonsRoot, receipt.StagingFileName, cancellationToken).ConfigureAwait(false);
        }

        await fileSystem.RemoveActiveMarkerAsync(addonsRoot, ToMarker(receipt), cancellationToken).ConfigureAwait(false);
        var rolledBack = receipt with
        {
            Status = "rolled_back",
            UpdatedUtc = clock.UtcNow.ToUniversalTime(),
            DisabledFileName = target is null ? null : disabledName,
            DmmStateHashAfter = snapshot.DmmStateHash,
        };
        await installationStore.UpdateInstallationAsync(projectRoot, rolledBack, receipt.Status, cancellationToken).ConfigureAwait(false);
        return new InstallationStatus(
            rolledBack,
            "rolled_back",
            [new BoundaryEvidence("rollback", "passed", target is null ? "Prepared staging was abandoned; no active VPK existed." : $"The exact owned VPK was disabled as {disabledName}.")]);
    }

    public async Task<RuntimeObservationResult> RecordRuntimeAsync(
        string projectRoot,
        string installationId,
        RuntimeObservationInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.SchemaVersion != 1)
        {
            throw Errors.InvalidRecipe("RUNTIME_OBSERVATION_VERSION_UNSUPPORTED", $"Runtime observation schema version {input.SchemaVersion} is unsupported.", "Use schemaVersion 1.");
        }

        var receipt = await installationStore.LoadInstallationAsync(projectRoot, installationId, cancellationToken).ConfigureAwait(false);
        var verification = await VerifyActiveAsync(receipt.AddonsRoot, projectRoot, installationId, cancellationToken).ConfigureAwait(false);
        var computedStatus = ComputeObservationStatus(input.Checks);
        if (!string.Equals(input.Status, computedStatus, StringComparison.Ordinal))
        {
            throw Errors.InvalidRecipe("RUNTIME_OBSERVATION_STATUS_MISMATCH", $"Declared status '{input.Status}' does not match check-derived status '{computedStatus}'.", "Use failed when any check failed, passed when all required checks passed, otherwise partial.");
        }

        var now = clock.UtcNow.ToUniversalTime();
        var observationId = $"runtime-{Guid.NewGuid():N}"[..29];
        var observation = new RuntimeObservation
        {
            ObservationId = observationId,
            CreatedUtc = now,
            Status = computedStatus,
            ProjectId = receipt.ProjectId,
            PackageId = receipt.PackageId,
            InstallationId = installationId,
            InstalledHash = receipt.PackageHash,
            Checks = input.Checks,
            Notes = input.Notes,
            Boundaries = verification.Boundaries.Concat([new BoundaryEvidence("player_observation", "passed", "The observation was recorded while the exact installation was verified active.")]).ToArray(),
            Extensions = input.Extensions,
        };
        var json = runtimeRenderer.RenderJson(observation);
        var markdown = runtimeRenderer.RenderMarkdown(observation);
        return await installationStore.PublishRuntimeObservationAsync(projectRoot, observation, json, markdown, cancellationToken).ConfigureAwait(false);
    }

    private async Task RequirePackageEntryAsync(VpkPackagePublicationResult package, CancellationToken cancellationToken)
    {
        var probe = await archiveInspector.ProbeAsync(package.PackagePath, package.Package.EntryLogicalPath, readMatchingEntry: true, cancellationToken).ConfigureAwait(false);
        if (!probe.ContainsLogicalPath
            || probe.EntryContentHash != package.Package.ReplacementEntryHash
            || (package.Package.Mode == "minimal" && probe.EntryCount != 1))
        {
            throw Errors.Verification("ADDON_PACKAGE_ENTRY_INVALID", "The package archive does not contain the exact expected model entry.", "Run package verify and recreate the package before installation.");
        }
    }

    private static int SelectSlot(string requested, AddonInventory inventory)
    {
        if (string.Equals(requested, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return inventory.AvailableAutomaticSlots.Count > 0
                ? inventory.AvailableAutomaticSlots[0]
                : throw Errors.Verification("ADDON_NO_SAFE_SLOT", "No empty slot from 90 through 99 can be proven above every collision.", "Disable the conflicting mod or explicitly reorganize it outside S2ModKit.");
        }

        if (requested.Length != 2 || !int.TryParse(requested, out var explicitSlot) || explicitSlot is < 0 or > 99)
        {
            throw Errors.InvalidRecipe("ADDON_SLOT_INVALID", $"Slot '{requested}' is not 'auto' or a two-digit number.", "Use --slot auto or --slot NN.");
        }

        return explicitSlot;
    }

    private static void RequireWinningSlot(IEnumerable<AddonCollision> collisions, int candidateSlot)
    {
        var collisionArray = collisions.ToArray();
        var unnumbered = collisionArray.FirstOrDefault(item => item.Slot is null);
        if (unnumbered is not null)
        {
            throw Errors.Verification("ADDON_COLLISION_PRIORITY_UNKNOWN", $"Archive '{unnumbered.FileName}' contains the target path but has no recognized pakNN_dir.vpk slot.", "Disable or rename the conflicting archive outside S2ModKit before installing.");
        }

        var shadowing = collisionArray.FirstOrDefault(item => item.Slot >= candidateSlot);
        if (shadowing is not null)
        {
            throw Errors.Verification("ADDON_PACKAGE_WOULD_BE_SHADOWED", $"Colliding archive '{shadowing.FileName}' is not below candidate slot {candidateSlot:00}.", "Choose a higher empty slot or disable the conflict outside S2ModKit.");
        }
    }

    private static int? TryParseSlot(string fileName)
    {
        if (fileName.Length == 13
            && fileName.StartsWith("pak", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(fileName.AsSpan(3, 2), out var slot))
        {
            return slot;
        }

        return null;
    }

    private static string ComputeObservationStatus(RuntimeChecks checks)
    {
        var values = new[] { checks.TargetComponent, checks.PreservedMaterialsAndParts, checks.Animations, checks.LodTransitions, checks.MenuPreview, checks.DeathAndRespawn };
        if (values.Any(value => value is not ("passed" or "failed" or "not_checked")))
        {
            throw Errors.InvalidRecipe("RUNTIME_CHECK_STATUS_INVALID", "Every runtime check must be passed, failed, or not_checked.", "Correct the observation document.");
        }

        if (values.Any(value => value == "failed"))
        {
            return "failed";
        }

        var required = values[..4];
        return required.All(value => value == "passed") ? "passed" : "partial";
    }

    private static ActiveInstallationMarker ToMarker(InstallationReceipt receipt) =>
        new(1, receipt.InstallationId, receipt.ProjectId, receipt.PackageId, receipt.TargetFileName, receipt.PackageHash);

    private static void RequireRoot(InstallationReceipt receipt, string addonsRoot)
    {
        if (!string.Equals(Path.GetFullPath(addonsRoot), receipt.AddonsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw Errors.Verification("ADDON_ROOT_RECEIPT_MISMATCH", "The supplied addon root differs from the installation receipt.", "Use the exact addon root recorded by install.");
        }
    }
}
