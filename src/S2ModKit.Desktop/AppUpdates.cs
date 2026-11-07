using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace S2ModKit.Desktop;

/// <summary>A newer release on GitHub: its version, the setup program to download (with its SHA-256 when GitHub reports one) and its page.</summary>
internal sealed record AvailableUpdate(Version Version, Uri Page, Uri? Setup, string? SetupName, long SetupSize, string? Sha256);

/// <summary>
/// Looks for a newer S2ModKit release on GitHub (one request when asked; nothing runs in the background) and, for an installed
/// app, downloads its setup program into the data folder, checks its size and SHA-256 and runs it silently into the same
/// folder; the setup closes the app and starts it again. A portable copy only opens the release page.
/// </summary>
internal static class AppUpdates
{
    public const string Repository = "Ainzak/Source-2-Mod-Kit-for-DeadLock";

    public static Version Current { get; } = ParseVersion(typeof(AppUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? typeof(AppUpdates).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>The latest release when it is newer than <see cref="Current"/>; null when it is not, or GitHub cannot be reached.</summary>
    public static async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var http = Client();
        try
        {
            var json = await http.GetStringAsync(new Uri($"https://api.github.com/repos/{Repository}/releases/latest"), cancellationToken).ConfigureAwait(false);
            return Parse(json, Current);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;                                                     // offline or rate-limited: try again next start
        }
    }

    /// <summary>Reads a GitHub "latest release" response: the tag (<c>v1.2.3</c>), the page and the <c>*-setup.exe</c> asset.</summary>
    internal static AvailableUpdate? Parse(string json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() || root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()
            || ParseVersion(root.GetProperty("tag_name").GetString()) is not { } version || version <= current)
        {
            return null;
        }

        var page = new Uri(root.GetProperty("html_url").GetString()!);
        foreach (var asset in root.TryGetProperty("assets", out var assets) ? assets.EnumerateArray() : default)
        {
            var name = asset.GetProperty("name").GetString() ?? string.Empty;
            if (!name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;   // "sha256:<hex>"
            return new AvailableUpdate(version, page, new Uri(asset.GetProperty("browser_download_url").GetString()!), name, asset.GetProperty("size").GetInt64(),
                digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest["sha256:".Length..].ToLowerInvariant() : null);
        }

        return new AvailableUpdate(version, page, null, null, 0, null);
    }

    /// <summary>Downloads the setup into <paramref name="folder"/>, verifies it and starts it to update this installation in place.</summary>
    public static async Task InstallAsync(AvailableUpdate update, string folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Setup is null || update.SetupName is null)
        {
            throw new InvalidOperationException("This release has no setup program.");
        }

        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, Path.GetFileName(update.SetupName));
        using (var http = Client())
        {
            var bytes = await http.GetByteArrayAsync(update.Setup, cancellationToken).ConfigureAwait(false);
            if (bytes.Length != update.SetupSize)
            {
                throw new InvalidDataException($"The download has {bytes.Length} bytes, GitHub lists {update.SetupSize}.");
            }

            if (update.Sha256 is { } expected && !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), expected, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The download's SHA-256 does not match the one GitHub lists.");
            }

            await File.WriteAllBytesAsync(file, bytes, cancellationToken).ConfigureAwait(false);
        }

        var install = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        Process.Start(new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            ArgumentList = { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/RESTARTAPPLICATIONS", $"/DIR={install}" },
        })?.Dispose();
    }

    public static void OpenPage(Uri page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }

    internal static Version? ParseVersion(string? text)
    {
        var core = (text ?? string.Empty).Trim().TrimStart('v', 'V').Split('+', '-')[0];
        return Version.TryParse(core, out var version) ? new Version(version.Major, version.Minor, Math.Max(0, version.Build)) : null;
    }

    private static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"S2ModKit/{Current.ToString(3)}");   // GitHub requires a user agent; no personal data
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }
}
