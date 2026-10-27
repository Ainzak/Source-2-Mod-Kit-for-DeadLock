using System.Security.Cryptography;
using System.Text.Json;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Infrastructure;

public sealed class AddonFileStore : IAddonFileStore
{
    private const int BufferSize = 1024 * 1024;
    private const string ReceiptPrefix = "addon-install-";

    public Task<IReadOnlyList<AddonFileInfo>> ListArchivesAsync(string addonsRoot, CancellationToken cancellationToken = default)
    {
        var root = RequireSafeRoot(addonsRoot);
        var files = new List<AddonFileInfo>();
        foreach (var path in Directory.EnumerateFiles(root, "*.vpk", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (System.Text.RegularExpressions.Regex.IsMatch(info.Name, @"_\d{3}\.vpk$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                continue;                                                 // numbered data chunks of split archives
            }

            var reparse = (info.Attributes & FileAttributes.ReparsePoint) != 0;
            files.Add(new AddonFileInfo(info.Name, info.FullName, reparse ? 0 : info.Length, info.LastWriteTimeUtc.Ticks, reparse));
        }

        return Task.FromResult<IReadOnlyList<AddonFileInfo>>(files.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<IReadOnlyDictionary<string, AddonTreeCacheEntry>> LoadTreeCacheAsync(string? cachePath, CancellationToken cancellationToken = default)
    {
        var empty = new Dictionary<string, AddonTreeCacheEntry>(StringComparer.OrdinalIgnoreCase);
        if (cachePath is null || !File.Exists(cachePath))
        {
            return empty;
        }

        try
        {
            var document = JsonSerializer.Deserialize<TreeCacheDocument>(await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false), JsonDefaults.Options);
            return document is { Version: TreeCacheDocument.CurrentVersion, Entries: not null }
                ? new Dictionary<string, AddonTreeCacheEntry>(document.Entries, StringComparer.OrdinalIgnoreCase)
                : empty;
        }
        catch (JsonException)
        {
            return empty;                                                 // a damaged cache is rebuilt, never trusted
        }
    }

    public async Task SaveTreeCacheAsync(string? cachePath, IReadOnlyDictionary<string, AddonTreeCacheEntry> entries, CancellationToken cancellationToken = default)
    {
        if (cachePath is null)
        {
            return;
        }

        var full = Path.GetFullPath(cachePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var document = new TreeCacheDocument(TreeCacheDocument.CurrentVersion, entries.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(document, JsonDefaults.Options), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<ContentHash> HashFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = RequireRegularFile(path);
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new ContentHash(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)));
    }

    public async Task<ContentHash> CopyHashedAndActivateAsync(string sourcePath, string addonsRoot, string stagingFileName, string targetFileName, CancellationToken cancellationToken = default)
    {
        var root = RequireSafeRoot(addonsRoot);
        var source = RequireRegularFile(sourcePath);
        var staging = ResolveChild(root, stagingFileName);
        var target = ResolveChild(root, targetFileName);
        if (File.Exists(staging) || Directory.Exists(staging) || File.Exists(target) || Directory.Exists(target))
        {
            throw Errors.Verification("ADDON_INSTALL_TARGET_EXISTS", "The staging or target file already exists.", "Use an empty slot.");
        }

        try
        {
            ContentHash hash;
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                hash = new ContentHash(Convert.ToHexStringLower(sha.GetHashAndReset()));
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, target, overwrite: false);
            return hash;
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

    public async Task DeleteOwnedAsync(string addonsRoot, string fileName, ContentHash expectedHash, CancellationToken cancellationToken = default)
    {
        var path = ResolveChild(RequireSafeRoot(addonsRoot), fileName);
        var actual = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (actual != expectedHash)
        {
            throw Errors.Verification("ADDON_FILE_HASH_DRIFT", $"{fileName} no longer matches the installed package.", "S2ModKit leaves a changed or foreign file in place; inspect it.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(path);
    }

    public async Task<IReadOnlyList<AddonFileInstallReceipt>> ListReceiptsAsync(string receiptsRoot, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(receiptsRoot);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var receipts = new List<AddonFileInstallReceipt>();
        foreach (var path in Directory.EnumerateFiles(root, ReceiptPrefix + "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            var receipt = JsonDefaults.Deserialize<AddonFileInstallReceipt>(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), "Addon install receipt");
            if (receipt.Operation != "addon_file_install@1" || receipt.Id.Length != 32)
            {
                throw Errors.Verification("ADDON_FILE_RECEIPT_INVALID", $"Receipt '{path}' is not an S2ModKit addon install receipt.", "Move unrelated files out of the receipts folder.");
            }

            receipts.Add(receipt);
        }

        return receipts;
    }

    public async Task WriteReceiptAsync(string receiptsRoot, AddonFileInstallReceipt receipt, bool createNew, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(receiptsRoot);
        Directory.CreateDirectory(root);
        var path = ResolveChild(root, ReceiptPrefix + receipt.Id + ".json");
        if (createNew && File.Exists(path))
        {
            throw Errors.Verification("ADDON_FILE_RECEIPT_EXISTS", "The receipt already exists.", "Retry the install.");
        }

        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonDefaults.SerializeToUtf8(receipt), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: !createNew);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string RequireSafeRoot(string addonsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addonsRoot);
        var root = Path.GetFullPath(addonsRoot);
        if (!Directory.Exists(root))
        {
            throw Errors.Input("ADDON_ROOT_NOT_FOUND", $"Addon root '{root}' does not exist.", "Provide the Deadlock addons directory or a test root.");
        }

        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw Errors.Verification("ADDON_ROOT_REPARSE_POINT", $"Addon root '{root}' is a reparse point.", "Use a regular directory.");
        }

        return root;
    }

    private static string ResolveChild(string root, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || fileName is "." or "..")
        {
            throw Errors.Input("ADDON_FILE_NAME_INVALID", $"'{fileName}' is not a safe file name.", "Use a single file-name segment.");
        }

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(fullRoot, fileName));
        return path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
            ? path
            : throw Errors.Input("ADDON_PATH_ESCAPE", $"'{fileName}' escapes '{root}'.", "Use a single safe file name.");
    }

    private static string RequireRegularFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
        {
            throw Errors.Input("REGULAR_FILE_REQUIRED", $"'{full}' is missing or is not a regular file.", "Provide an existing regular file.");
        }

        return full;
    }

    private sealed record TreeCacheDocument(int Version, Dictionary<string, AddonTreeCacheEntry>? Entries)
    {
        public const int CurrentVersion = 1;
    }
}

/// <summary>Reads Deadlock Mod Manager records without changing them: <c>.dmm.json</c> in the addons folder (mod id to
/// file names) and the manager's <c>state.json</c> (mod names, installed files).</summary>
public sealed class ModManagerStateReader : IModManagerStateReader
{
    public async Task<ModManagerView> ReadAsync(string addonsRoot, string? managerStatePath, CancellationToken cancellationToken = default)
    {
        var mods = new Dictionary<string, ManagedModInfo>(StringComparer.OrdinalIgnoreCase);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var found = false;
        using (var state = await TryReadAsync(managerStatePath, cancellationToken).ConfigureAwait(false))
        {
            if (state is not null && TryGetLocalMods(state.RootElement, out var localMods))
            {
                found = true;
                foreach (var mod in localMods.EnumerateArray())
                {
                    var name = Text(mod, "name");
                    var remoteId = Text(mod, "remoteId");
                    if (remoteId is not null && name is not null)
                    {
                        names[remoteId] = name;
                    }

                    foreach (var file in Strings(mod, "installedVpks"))
                    {
                        reserved.Add(file);
                        if (Text(mod, "status") == "installed")
                        {
                            mods[file] = new ManagedModInfo(remoteId ?? Text(mod, "id") ?? "?", name, true);
                        }
                    }
                }
            }
        }

        using (var dmm = await TryReadAsync(Path.Combine(Path.GetFullPath(addonsRoot), ".dmm.json"), cancellationToken).ConfigureAwait(false))
        {
            if (dmm is not null && dmm.RootElement.TryGetProperty("mods", out var dmmMods) && dmmMods.ValueKind == JsonValueKind.Object)
            {
                found = true;
                foreach (var entry in dmmMods.EnumerateObject())
                {
                    names.TryGetValue(entry.Name, out var name);
                    var enabled = entry.Value.TryGetProperty("enabled", out var flag) && flag.ValueKind == JsonValueKind.True;
                    foreach (var file in Strings(entry.Value, "currentVpks").Concat(Strings(entry.Value, "disabledVpks")))
                    {
                        mods[file] = new ManagedModInfo(entry.Name, name, enabled);
                    }

                    foreach (var file in Strings(entry.Value, "currentVpks").Concat(Strings(entry.Value, "originalVpkNames")))
                    {
                        reserved.Add(file);
                    }
                }
            }
        }

        return new ModManagerView(mods, reserved, found);
    }

    private static bool TryGetLocalMods(JsonElement root, out JsonElement localMods)
    {
        localMods = default;
        if (!root.TryGetProperty("local-config", out var config))
        {
            return false;
        }

        if (config.ValueKind == JsonValueKind.String)
        {
            try
            {
                config = JsonDocument.Parse(config.GetString()!).RootElement.Clone();
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return config.TryGetProperty("state", out var state)
            && state.TryGetProperty("localMods", out localMods)
            && localMods.ValueKind == JsonValueKind.Array;
    }

    private static async Task<JsonDocument?> TryReadAsync(string? path, CancellationToken cancellationToken)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;                                                  // the manager may be writing it; names are optional
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string[] Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
            : [];
}
