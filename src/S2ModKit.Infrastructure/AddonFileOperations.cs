using System.Diagnostics;
using System.Text.RegularExpressions;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Infrastructure;

/// <summary>In-place file operations in the addons folder: rename, copy of a new file (staging name, then an atomic move that
/// never overwrites), delete of a file the same operation created, and the operation journal.</summary>
public sealed partial class AddonFileOperations : IAddonFileOperations
{
    public Task<IReadOnlyList<AddonFileStat>> ListFilesAsync(string addonsRoot, CancellationToken cancellationToken = default)
    {
        var root = AddonFileStore.RequireSafeRoot(addonsRoot);
        var files = new List<AddonFileStat>();
        foreach (var path in Directory.EnumerateFiles(root, "*.vpk", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            var reparse = (info.Attributes & FileAttributes.ReparsePoint) != 0;
            files.Add(new AddonFileStat(info.Name, info.FullName, reparse ? 0 : info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), reparse));
        }

        return Task.FromResult<IReadOnlyList<AddonFileStat>>(files.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public Task RenameAsync(string addonsRoot, string fromName, string toName, CancellationToken cancellationToken = default)
    {
        var root = AddonFileStore.RequireSafeRoot(addonsRoot);
        var from = AddonFileStore.RequireRegularFile(AddonFileStore.ResolveChild(root, fromName));
        var to = AddonFileStore.ResolveChild(root, toName);
        if (File.Exists(to) || Directory.Exists(to))
        {
            throw Errors.Verification("ADDON_RENAME_TARGET_EXISTS", $"'{toName}' already exists.", "S2ModKit never overwrites an existing file.");
        }

        File.Move(from, to, overwrite: false);                            // same folder and volume: a rename, no data is rewritten
        return Task.CompletedTask;
    }

    public Task CopyNewAsync(string sourcePath, string addonsRoot, string targetName, CancellationToken cancellationToken = default)
    {
        var root = AddonFileStore.RequireSafeRoot(addonsRoot);
        var source = AddonFileStore.RequireRegularFile(sourcePath);
        var target = AddonFileStore.ResolveChild(root, targetName);
        var staging = AddonFileStore.ResolveChild(root, $".{targetName}.{Guid.NewGuid():N}.s2modkit-staging");
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw Errors.Verification("ADDON_INSTALL_TARGET_EXISTS", $"'{targetName}' already exists.", "Use an empty slot.");
        }

        try
        {
            File.Copy(source, staging, overwrite: false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, target, overwrite: false);
        }
        finally
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteCreatedAsync(string addonsRoot, string fileName, CancellationToken cancellationToken = default)
    {
        var path = AddonFileStore.ResolveChild(AddonFileStore.RequireSafeRoot(addonsRoot), fileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<string> FindChunksBeside(string packagePath)
    {
        var full = Path.GetFullPath(packagePath);
        var name = Path.GetFileName(full);
        if (!name.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(full) is not { } folder)
        {
            return [];
        }

        var stem = name[..^"_dir.vpk".Length];
        return [.. Directory.EnumerateFiles(folder, "*.vpk")
            .Where(path => ChunkPattern().IsMatch(Path.GetFileName(path)) && Path.GetFileName(path)[..^"_000.vpk".Length].Equals(stem, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    public async Task WriteJournalAsync(string journalRoot, AddonOperationJournal journal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var root = Path.GetFullPath(journalRoot);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"{journal.Operation.Split('@')[0]}-{journal.StartedUtc:yyyyMMdd-HHmmss}-{journal.Id}.json");
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonDefaults.SerializeToUtf8(journal), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    [GeneratedRegex(@"_\d{3}\.vpk$", RegexOptions.IgnoreCase)]
    private static partial Regex ChunkPattern();
}

/// <summary>Deadlock's game process (<c>deadlock.exe</c>): while it runs the game holds its addon archives open.</summary>
public sealed class SystemGameProcessProbe : IGameProcessProbe
{
    public bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName("deadlock");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}

/// <summary>Sends files to the Windows Recycle Bin through the shell (the file system class in Microsoft.VisualBasic, part of the
/// base runtime). Shell dialogs stay enabled so that a file the bin cannot hold (too large, bin disabled) asks before it
/// would be deleted for good.</summary>
public sealed class WindowsRecycleBin : IRecycleBin
{
    public void Recycle(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!OperatingSystem.IsWindows())
        {
            throw Errors.Unsupported("RECYCLE_BIN_UNSUPPORTED", "The Recycle Bin is only available on Windows.", "Delete the files by hand.");
        }

        foreach (var path in paths)
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.AllDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
    }
}

public sealed class ModLibraryStore : IModLibraryStore
{
    public async Task<ModLibraryDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new ModLibraryDocument();
        }

        var document = JsonDefaults.Deserialize<ModLibraryDocument>(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), "Mod library");
        return document.Schema == ModLibraryDocument.CurrentSchema
            ? document
            : throw Errors.Verification("MOD_LIBRARY_SCHEMA", $"The mod library '{path}' has schema '{document.Schema}'.", "Use a build of S2ModKit that reads this library, or move the file away.");
    }

    public async Task SaveAsync(string path, ModLibraryDocument document, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonDefaults.SerializeToUtf8(document), cancellationToken).ConfigureAwait(false);
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
}
