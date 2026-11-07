using System.Text.Json;

namespace S2ModKit.Desktop;

/// <summary>
/// The app's settings: the Deadlock <c>game</c> folder, the data folder for journals, install receipts and caches, and
/// whether to look for a new version on start. An installed app keeps them in the user profile
/// (<c>%LocalAppData%\S2ModKit</c>, data in its <c>data</c> folder unless moved); a portable copy (a <c>portable</c> marker,
/// a settings file or a <c>data</c> folder beside the executable) keeps both beside the executable, as before.
/// </summary>
internal sealed record AppSettings(string Game, string Data, bool CheckUpdates = true)
{
    private const string FileName = "S2ModKit.settings.json";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>A portable copy: settings and data beside the executable.</summary>
    public static bool Portable => IsPortable(AppContext.BaseDirectory);

    /// <summary>The per-user folder of an installed app.</summary>
    public static string ProfileRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "S2ModKit");

    public static string FilePath => Path.Combine(Portable ? AppContext.BaseDirectory : ProfileRoot, FileName);

    /// <summary>Installed by the setup program (its uninstaller is beside the executable): updates can run the new setup in place.</summary>
    public static bool Installed => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    internal static bool IsPortable(string folder) =>
        File.Exists(Path.Combine(folder, "portable")) || File.Exists(Path.Combine(folder, FileName)) || Directory.Exists(Path.Combine(folder, "data"));

    public string AddonsRoot => Path.Combine(Game, "citadel", "addons");

    public string InstallsRoot => Path.Combine(Data, "installs");

    /// <summary>Journals of renames and deletes in the addons folder (one JSON file per operation).</summary>
    public string JournalRoot => Path.Combine(Data, "journal");

    /// <summary>Where archives are unpacked before the safe installer copies them into the addons folder; emptied after each install.</summary>
    public string StagingRoot => Path.Combine(Data, "staging");

    public string DoctorRoot => Path.Combine(Data, "doctor");

    /// <summary>The mod library (<c>mod-library@1</c>): which mods are versions of one mod.</summary>
    public string LibraryPath => Path.Combine(Data, "library.json");

    public string TreeCache => Path.Combine(Data, "addon-tree-cache.json");

    public bool GameFound => File.Exists(Path.Combine(Game, "citadel", "pak01_dir.vpk"));

    /// <summary>Deadlock Mod Manager state (read-only), when it is installed.</summary>
    public static string? ManagerState
    {
        get
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            return string.IsNullOrWhiteSpace(appData) ? null : Path.Combine(appData, "dev.stormix.deadlock-mod-manager", "state.json");
        }
    }

    public static AppSettings Load(string? gameOverride)
    {
        var stored = File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) : null;
        var found = SteamGameFolders().FirstOrDefault(folder => File.Exists(Path.Combine(folder, "citadel", "pak01_dir.vpk")));
        var game = gameOverride ?? stored?.Game ?? found ?? Path.Combine(AppContext.BaseDirectory, "game");
        var data = stored?.Data ?? Path.Combine(Portable ? AppContext.BaseDirectory : ProfileRoot, "data");
        return new AppSettings(Path.GetFullPath(game), data, stored?.CheckUpdates ?? true);
    }

    /// <summary>Deadlock's game folder in every Steam library: Steam's own folder (registry) and the libraries listed in its
    /// steamapps/libraryfolders.vdf.</summary>
    private static IEnumerable<string> SteamGameFolders()
    {
        var steam = OperatingSystem.IsWindows()
            ? Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
            : null;
        if (string.IsNullOrWhiteSpace(steam))
        {
            yield break;
        }

        var libraries = new List<string> { steam };
        var folders = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(folders))
        {
            libraries.AddRange(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(folders), "\"path\"\\s+\"([^\"]+)\"")
                .Select(match => match.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal)));
        }

        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(library, "steamapps", "common", "Deadlock", "game");
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Indented));
    }

    /// <summary>
    /// Uses <paramref name="folder"/> as the data folder: the current data is copied there first (only into an empty or new
    /// folder; nothing is overwritten or deleted, the old folder stays as it was).
    /// </summary>
    public AppSettings MoveData(string folder)
    {
        var target = Path.GetFullPath(folder);
        if (string.Equals(Path.TrimEndingDirectorySeparator(target), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Data)), StringComparison.OrdinalIgnoreCase))
        {
            return this;
        }

        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            throw new IOException($"'{target}' is not empty. Pick an empty or new folder for the app's data.");
        }

        if (Directory.Exists(Data))
        {
            foreach (var file in Directory.EnumerateFiles(Data, "*", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(target, Path.GetRelativePath(Data, file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy, overwrite: false);
            }
        }

        Directory.CreateDirectory(target);
        return this with { Data = target };
    }
}
