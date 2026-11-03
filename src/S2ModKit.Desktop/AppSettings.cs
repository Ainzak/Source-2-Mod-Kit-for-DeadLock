using System.Text.Json;

namespace S2ModKit.Desktop;

/// <summary>
/// The app's settings beside the executable (portable): the Deadlock <c>game</c> folder and the data folder for install
/// receipts and caches (default <c>data</c> beside the executable, never a fixed C: path).
/// </summary>
internal sealed record AppSettings(string Game, string Data)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "S2ModKit.settings.json");

    public string AddonsRoot => Path.Combine(Game, "citadel", "addons");

    public string InstallsRoot => Path.Combine(Data, "installs");

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
        return new AppSettings(Path.GetFullPath(game), stored?.Data ?? Path.Combine(AppContext.BaseDirectory, "data"));
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

    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Indented));
}
