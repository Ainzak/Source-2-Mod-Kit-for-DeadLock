using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Infrastructure;

/// <summary>Reads the game build from <c>citadel/steam.inf</c> (read-only).</summary>
public sealed class GameBuildReader : IGameBuildReader
{
    public async Task<GameBuildInfo> ReadAsync(string gameDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        var path = Path.Combine(Path.GetFullPath(gameDirectory), "citadel", "steam.inf");
        if (!File.Exists(path))
        {
            throw Errors.Input("GAME_STEAM_INF_NOT_FOUND", $"'{path}' does not exist.", "Pass the Deadlock 'game' folder (the one that contains 'citadel').");
        }

        return await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GameBuildInfo?> TryReadForAddonsRootAsync(string addonsRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addonsRoot);
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(addonsRoot)));
        var path = parent is null ? null : Path.Combine(parent, "steam.inf");
        return path is not null && File.Exists(path)
            ? await ReadFileAsync(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private static async Task<GameBuildInfo> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length > 64 * 1024)
        {
            throw Errors.Input("GAME_STEAM_INF_INVALID", $"'{path}' is unexpectedly large.", "Verify the game files in Steam, then retry.");
        }

        return SteamInf.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), $"'{path}'");
    }
}
