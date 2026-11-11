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

/// <summary>Sends files to the Windows Recycle Bin through the shell. A file the bin cannot hold (too large, bin disabled) asks
/// before it would be deleted for good; other shell error dialogs are off (the app reports the failure itself).</summary>
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
            WaitUntilFree(path);
        }

        foreach (var path in paths)
        {
            if (ShellRecycle(path))
            {
                continue;
            }

            // Windows sometimes refuses one particular file (seen 2026-11-11 with an addon: "in use" in the app, "invalid path"
            // elsewhere, while nothing held it and a byte-identical copy went to the bin at once). A fresh copy under the same
            // name goes to the bin instead, so restoring it gives the same file; the refused original is then removed.
            var refused = path + ".s2mk-" + Guid.NewGuid().ToString("N")[..8];
            File.Move(path, refused);
            try
            {
                File.Copy(refused, path);
                File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(refused));
                if (!ShellRecycle(path))
                {
                    throw new IOException($"Windows did not move '{Path.GetFileName(path)}' to the Recycle Bin.");
                }
            }
            catch
            {
                if (File.Exists(path))
                {
                    File.Delete(path);                                         // the copy; the original goes back
                }

                File.Move(refused, path);
                throw;
            }

            File.Delete(refused);
        }
    }

    /// <summary>Sends one file to the Recycle Bin without the shell's error dialogs; a file the bin cannot hold (too large, bin
    /// disabled) still asks before it would be deleted for good. False when Windows refused it and the file is still there.</summary>
    private static bool ShellRecycle(string path)
    {
        var operation = new ShFileOperation
        {
            Function = 3,                                                       // FO_DELETE
            From = Path.GetFullPath(path) + "\0\0",
            Flags = 0x0040 | 0x0010 | 0x0400 | 0x0004 | 0x4000,               // ALLOWUNDO, NOCONFIRMATION, NOERRORUI, SILENT, WANTNUKEWARNING
        };
        var result = SHFileOperation(ref operation);
        if (operation.Aborted)
        {
            throw new OperationCanceledException($"Deleting '{Path.GetFileName(path)}' was cancelled.");
        }

        return result == 0 && !File.Exists(path);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct ShFileOperation
    {
        public IntPtr Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public bool Aborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

#pragma warning disable SYSLIB1054, CA5392 // classic shell interop with a structure; shell32 is a system library
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOperation operation);
#pragma warning restore SYSLIB1054, CA5392

    /// <summary>
    /// Waits up to two seconds for nothing to hold <paramref name="path"/> open (an archive reader that was let go but not yet
    /// collected is closed by a collection), so the shell does not stop on its "file in use" dialog. A file still held is refused
    /// with the programs that hold it.
    /// </summary>
    private static void WaitUntilFree(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    return;
                }
            }
            catch (IOException) when (attempt < 8)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Thread.Sleep(250);
            }
            catch (IOException)
            {
                var holders = OpenFileHolders.Of(path);
                throw Errors.Verification("ADDON_FILE_IN_USE",
                    $"'{Path.GetFileName(path)}' is open in {(holders.Count == 0 ? "another program" : string.Join(", ", holders))}.",
                    holders.Any(name => name.StartsWith("S2ModKit", StringComparison.OrdinalIgnoreCase))
                        ? "Pick another mod in the list and delete again."
                        : "Close it there and delete again.");
            }
        }
    }
}

/// <summary>The programs that hold a file open, from the Windows Restart Manager (names with process ids; empty when unknown).</summary>
internal static class OpenFileHolders
{
    public static IReadOnlyList<string> Of(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            if (RmStartSession(out var session, 0, Guid.NewGuid().ToString("N")) != 0)
            {
                return [];
            }

            try
            {
                if (RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, null) != 0)
                {
                    return [];
                }

                uint needed = 0, count = 0;
                uint reasons = 0;
                var result = RmGetList(session, out needed, ref count, null, ref reasons);
                if (result != 234 || needed == 0)                           // ERROR_MORE_DATA: there are holders
                {
                    return [];
                }

                var list = new RmProcessInfo[needed];
                count = needed;
                if (RmGetList(session, out needed, ref count, list, ref reasons) != 0)
                {
                    return [];
                }

                return [.. list.Take((int)count).Select(item => $"{item.AppName} ({item.Process.ProcessId})")];
            }
            finally
            {
                _ = RmEndSession(session);
            }
        }
#pragma warning disable CA1031 // only a better message: the refusal stands without it
        catch (Exception)
#pragma warning restore CA1031
        {
            return [];
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 256)]
        public string AppName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public bool Restartable;
    }

#pragma warning disable SYSLIB1054, CA5392 // classic interop with arrays of structures; Restart Manager is a system library
    [System.Runtime.InteropServices.DllImport("rstrtmgr.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, string key);

    [System.Runtime.InteropServices.DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [System.Runtime.InteropServices.DllImport("rstrtmgr.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[] names, uint applications, IntPtr processes, uint services, string[]? serviceNames);

    [System.Runtime.InteropServices.DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [System.Runtime.InteropServices.In, System.Runtime.InteropServices.Out] RmProcessInfo[]? list, ref uint reasons);
#pragma warning restore SYSLIB1054, CA5392
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
