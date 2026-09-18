using System.Text;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Infrastructure;

public sealed partial class FileSystemProjectWorkspace : IInstallationStore
{
    public async Task CreateInstallationAsync(string projectRoot, InstallationReceipt receipt, CancellationToken cancellationToken = default)
    {
        ValidateReceipt(receipt);
        var root = Path.GetFullPath(projectRoot);
        Directory.CreateDirectory(ResolveInside(root, "installations"));
        var path = ResolveInside(root, $"installations/{receipt.InstallationId}.json");
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw Errors.Verification("INSTALLATION_RECEIPT_EXISTS", $"Installation receipt '{receipt.InstallationId}' already exists.", "Use the existing receipt; receipts are never overwritten at creation.");
        }

        await WriteCreateNewAtomicAsync(path, JsonDefaults.SerializeToUtf8(receipt), cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateInstallationAsync(
        string projectRoot,
        InstallationReceipt receipt,
        string expectedStatus,
        CancellationToken cancellationToken = default)
    {
        ValidateReceipt(receipt);
        var current = await LoadInstallationAsync(projectRoot, receipt.InstallationId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current.Status, expectedStatus, StringComparison.Ordinal)
            || !SameInstallationIdentity(current, receipt))
        {
            throw Errors.Verification("INSTALLATION_RECEIPT_TRANSITION_INVALID", "Installation receipt status or immutable identity changed unexpectedly.", "Reload the receipt and apply only the expected prepared→active or prepared/active→rolled_back transition.");
        }

        var path = ResolveInside(Path.GetFullPath(projectRoot), $"installations/{receipt.InstallationId}.json");
        await WriteReplaceAtomicAsync(path, JsonDefaults.SerializeToUtf8(receipt), cancellationToken).ConfigureAwait(false);
    }

    public async Task<InstallationReceipt> LoadInstallationAsync(string projectRoot, string installationId, CancellationToken cancellationToken = default)
    {
        EnsureSafeSegment(installationId, "installation id");
        var path = ResolveInside(Path.GetFullPath(projectRoot), $"installations/{installationId}.json");
        if (!File.Exists(path))
        {
            throw Errors.Input("INSTALLATION_RECEIPT_NOT_FOUND", $"Installation receipt '{installationId}' was not found.", "Use an installation id emitted by addons install.");
        }

        var receipt = JsonDefaults.Deserialize<InstallationReceipt>(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), "Installation receipt");
        ValidateReceipt(receipt);
        if (!string.Equals(receipt.InstallationId, installationId, StringComparison.Ordinal))
        {
            throw Errors.Verification("INSTALLATION_RECEIPT_ID_MISMATCH", "Receipt file name and installation id differ.", "Do not use the receipt; inspect workspace integrity.");
        }

        return receipt;
    }

    public async Task<IReadOnlyList<InstallationReceipt>> ListInstallationsAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(projectRoot);
        var directory = ResolveInside(root, "installations");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var result = new List<InstallationReceipt>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(await LoadInstallationAsync(root, Path.GetFileNameWithoutExtension(path), cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    public async Task<RuntimeObservationResult> PublishRuntimeObservationAsync(
        string projectRoot,
        RuntimeObservation observation,
        string json,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        EnsureSafeSegment(observation.ObservationId, "observation id");
        var root = Path.GetFullPath(projectRoot);
        var observationRoot = ResolveInside(root, $"runtime-observations/{observation.ObservationId}");
        if (Directory.Exists(observationRoot) || File.Exists(observationRoot))
        {
            throw Errors.Verification("RUNTIME_OBSERVATION_ID_COLLISION", $"Runtime observation '{observation.ObservationId}' already exists.", "Preserve both observations under distinct ids.");
        }

        var temporaryRoot = ResolveInside(root, $"temp/runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            await File.WriteAllTextAsync(ResolveInside(temporaryRoot, "observation.json"), json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(ResolveInside(temporaryRoot, "observation.md"), markdown, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(observationRoot)!);
            Directory.Move(temporaryRoot, observationRoot);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }

        return new RuntimeObservationResult(observation, json, markdown);
    }

    private static bool SameInstallationIdentity(InstallationReceipt left, InstallationReceipt right) =>
        left.InstallationId == right.InstallationId
        && left.ProjectId == right.ProjectId
        && left.PackageId == right.PackageId
        && left.CreatedUtc == right.CreatedUtc
        && string.Equals(left.AddonsRoot, right.AddonsRoot, StringComparison.OrdinalIgnoreCase)
        && left.TargetFileName == right.TargetFileName
        && left.StagingFileName == right.StagingFileName
        && left.Slot == right.Slot
        && left.LogicalPath == right.LogicalPath
        && left.PackageHash == right.PackageHash
        && left.EntryContentHash == right.EntryContentHash
        && left.DmmStateHashBefore == right.DmmStateHashBefore;

    private static void ValidateReceipt(InstallationReceipt receipt)
    {
        if (receipt.SchemaVersion != 1
            || string.IsNullOrWhiteSpace(receipt.InstallationId)
            || string.IsNullOrWhiteSpace(receipt.ProjectId)
            || string.IsNullOrWhiteSpace(receipt.PackageId)
            || string.IsNullOrWhiteSpace(receipt.AddonsRoot)
            || string.IsNullOrWhiteSpace(receipt.TargetFileName)
            || string.IsNullOrWhiteSpace(receipt.StagingFileName)
            || string.IsNullOrWhiteSpace(receipt.LogicalPath)
            || receipt.Status is not ("prepared" or "active" or "rolled_back")
            || receipt.Slot is < 0 or > 99)
        {
            throw Errors.Verification("INSTALLATION_RECEIPT_INVALID", "Installation receipt violates schema-version-1 invariants.", "Do not use it; inspect workspace integrity.");
        }
    }

    private static async Task WriteCreateNewAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
