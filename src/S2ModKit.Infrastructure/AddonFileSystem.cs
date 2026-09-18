using System.Security.Cryptography;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Infrastructure;

public sealed class AddonFileSystem : IAddonFileSystem
{
    private const int BufferSize = 1024 * 1024;

    public async Task<AddonRootSnapshot> SnapshotAsync(string addonsRoot, CancellationToken cancellationToken = default)
    {
        var root = RequireSafeRoot(addonsRoot);
        var files = new List<AddonFileDescriptor>();
        foreach (var path in Directory.EnumerateFiles(root, "*.vpk", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (!IsActiveSlotName(info.Name))
            {
                continue;
            }

            var isReparsePoint = IsReparse(info.Attributes);
            files.Add(new AddonFileDescriptor(info.Name, info.FullName, isReparsePoint ? 0 : info.Length, isReparsePoint));
        }

        var dmmPath = Path.Combine(root, ".dmm.json");
        ContentHash? dmmHash = null;
        if (File.Exists(dmmPath))
        {
            var attributes = File.GetAttributes(dmmPath);
            if (IsReparse(attributes))
            {
                throw Errors.Verification("DMM_STATE_REPARSE_POINT", $"DMM state '{dmmPath}' is a reparse point.", "Use a regular .dmm.json file before managed installation.");
            }

            dmmHash = await ComputeHashAsync(dmmPath, cancellationToken).ConfigureAwait(false);
        }

        return new AddonRootSnapshot(root, dmmHash, files.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public Task<ContentHash> ComputeFileHashAsync(string path, CancellationToken cancellationToken = default) =>
        ComputeHashAsync(RequireRegularFile(path), cancellationToken);

    public async Task<ActiveInstallationMarker?> LoadActiveMarkerAsync(string addonsRoot, CancellationToken cancellationToken = default)
    {
        var path = ResolveStatePath(addonsRoot, "active-installation.json", createDirectory: false);
        if (!File.Exists(path))
        {
            return null;
        }

        _ = RequireRegularFile(path);
        var marker = JsonDefaults.Deserialize<ActiveInstallationMarker>(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), "Active installation marker");
        if (marker.SchemaVersion != 1
            || string.IsNullOrWhiteSpace(marker.InstallationId)
            || string.IsNullOrWhiteSpace(marker.ProjectId)
            || string.IsNullOrWhiteSpace(marker.PackageId)
            || string.IsNullOrWhiteSpace(marker.TargetFileName))
        {
            throw Errors.Verification("ADDON_ACTIVE_MARKER_INVALID", "The S2ModKit active marker is invalid.", "Preserve it and reconcile the prior installation before making another change.");
        }

        return marker;
    }

    public async Task CreateActiveMarkerAsync(string addonsRoot, ActiveInstallationMarker marker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marker);
        var path = ResolveStatePath(addonsRoot, "active-installation.json", createDirectory: true);
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw Errors.Verification("ADDON_INSTALLATION_ALREADY_ACTIVE", "An S2ModKit active marker already exists.", "Verify or roll back the existing installation first.");
        }

        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonDefaults.SerializeToUtf8(marker), cancellationToken).ConfigureAwait(false);
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

    public async Task RemoveActiveMarkerAsync(string addonsRoot, ActiveInstallationMarker marker, CancellationToken cancellationToken = default)
    {
        var current = await LoadActiveMarkerAsync(addonsRoot, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return;
        }

        if (current != marker)
        {
            throw Errors.Verification("ADDON_ACTIVE_MARKER_MISMATCH", "Refusing to remove an active marker owned by another receipt.", "Reconcile the marker's installation instead.");
        }

        File.Delete(ResolveStatePath(addonsRoot, "active-installation.json", createDirectory: false));
    }

    public async Task StageAndActivateAsync(
        string addonsRoot,
        string sourcePath,
        string stagingFileName,
        string targetFileName,
        ContentHash expectedHash,
        CancellationToken cancellationToken = default)
    {
        var root = RequireSafeRoot(addonsRoot);
        var source = RequireRegularFile(sourcePath);
        var staging = ResolveChild(root, stagingFileName);
        var target = ResolveChild(root, targetFileName);
        if (File.Exists(staging) || Directory.Exists(staging) || File.Exists(target) || Directory.Exists(target))
        {
            throw Errors.Verification("ADDON_INSTALL_TARGET_EXISTS", "The staging or target installation path already exists.", "Use an empty slot and reconcile any prepared receipt before retrying.");
        }

        try
        {
            await CopyNewAsync(source, staging, cancellationToken).ConfigureAwait(false);
            var stagedHash = await ComputeHashAsync(staging, cancellationToken).ConfigureAwait(false);
            if (stagedHash != expectedHash)
            {
                throw Errors.Verification("ADDON_STAGING_HASH_DRIFT", $"Staged archive hash {stagedHash} differs from package {expectedHash}.", "Do not activate the copy; inspect the filesystem and source package.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, target, overwrite: false);
        }
        catch
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }

            throw;
        }
    }

    public async Task<string> DisableAsync(
        string addonsRoot,
        string targetFileName,
        string disabledFileName,
        ContentHash expectedHash,
        CancellationToken cancellationToken = default)
    {
        var root = RequireSafeRoot(addonsRoot);
        var target = ResolveChild(root, targetFileName);
        var disabled = ResolveChild(root, disabledFileName);
        if (!File.Exists(target) || Directory.Exists(target) || File.Exists(disabled) || Directory.Exists(disabled))
        {
            throw Errors.Verification("ADDON_ROLLBACK_PATH_INVALID", "The active source is missing or the disabled destination already exists.", "Do not alter either path; inspect the receipt and addon directory.");
        }

        _ = RequireRegularFile(target);
        var currentHash = await ComputeHashAsync(target, cancellationToken).ConfigureAwait(false);
        if (currentHash != expectedHash)
        {
            throw Errors.Verification("ADDON_ROLLBACK_HASH_DRIFT", $"Active archive hash {currentHash} differs from receipt {expectedHash}.", "S2ModKit will not move an altered or foreign file.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        File.Move(target, disabled, overwrite: false);
        return disabled;
    }

    public Task AbandonStagingAsync(string addonsRoot, string stagingFileName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var staging = ResolveChild(RequireSafeRoot(addonsRoot), stagingFileName);
        if (Directory.Exists(staging))
        {
            throw Errors.Verification("ADDON_STAGING_PATH_INVALID", "The receipt staging path is a directory.", "Inspect it manually; S2ModKit will not remove it.");
        }

        if (File.Exists(staging))
        {
            var attributes = File.GetAttributes(staging);
            if (IsReparse(attributes))
            {
                throw Errors.Verification("ADDON_STAGING_REPARSE_POINT", "The receipt staging path became a reparse point.", "Inspect it manually; S2ModKit will not remove it.");
            }

            File.Delete(staging);
        }

        return Task.CompletedTask;
    }

    private static string RequireSafeRoot(string addonsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addonsRoot);
        var root = Path.GetFullPath(addonsRoot);
        if (!Directory.Exists(root))
        {
            throw Errors.Input("ADDON_ROOT_NOT_FOUND", $"Addon root '{root}' does not exist.", "Provide the existing Deadlock addons directory or a test fixture root.");
        }

        if (IsReparse(File.GetAttributes(root)))
        {
            throw Errors.Verification("ADDON_ROOT_REPARSE_POINT", $"Addon root '{root}' is a reparse point.", "Use a regular directory for managed installation.");
        }

        return root;
    }

    private static string ResolveStatePath(string addonsRoot, string fileName, bool createDirectory)
    {
        var root = RequireSafeRoot(addonsRoot);
        var stateRoot = Path.Combine(root, ".s2modkit");
        if (Directory.Exists(stateRoot) && IsReparse(File.GetAttributes(stateRoot)))
        {
            throw Errors.Verification("ADDON_STATE_REPARSE_POINT", $"State root '{stateRoot}' is a reparse point.", "Replace it with a regular S2ModKit-owned directory.");
        }

        if (createDirectory)
        {
            Directory.CreateDirectory(stateRoot);
        }

        return ResolveChild(stateRoot, fileName);
    }

    private static string ResolveChild(string root, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || fileName is "." or "..")
        {
            throw Errors.Input("ADDON_FILE_NAME_INVALID", $"'{fileName}' is not a safe addon file name.", "Use a single file-name segment.");
        }

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(fullRoot, fileName));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw Errors.Input("ADDON_PATH_ESCAPE", $"Addon path '{fileName}' escapes '{root}'.", "Use a single safe file name.");
        }

        return path;
    }

    private static string RequireRegularFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || IsReparse(File.GetAttributes(fullPath)))
        {
            throw Errors.Input("REGULAR_FILE_REQUIRED", $"'{fullPath}' is missing or is not a regular file.", "Provide an existing regular file.");
        }

        return fullPath;
    }

    private static bool IsReparse(FileAttributes attributes) => (attributes & FileAttributes.ReparsePoint) != 0;

    private static bool IsActiveSlotName(string fileName) =>
        fileName.Length == 13
        && fileName.StartsWith("pak", StringComparison.OrdinalIgnoreCase)
        && fileName.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase)
        && char.IsAsciiDigit(fileName[3])
        && char.IsAsciiDigit(fileName[4]);

    private static async Task CopyNewAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        await input.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ContentHash> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new ContentHash(Convert.ToHexStringLower(hash));
    }
}
