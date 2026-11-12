using System.Globalization;
using System.IO.Pipes;
using System.Text;

namespace S2ModKit.Desktop;

/// <summary>
/// A GameBanana 1-click install link: <c>s2modkit:https://gamebanana.com/mmdl/&lt;file&gt;,Mod,&lt;mod&gt;</c>. Windows starts the app with
/// the link (the installer registers the link type for the user); a second copy hands the link to the running app over a named
/// pipe and ends, so one window installs it.
/// </summary>
internal sealed record OneClickLink(long FileId, long ModId)
{
    public const string Scheme = "s2modkit";
    private static readonly string PipeName = "S2ModKit-1click-" + Environment.UserName;

    /// <summary>The link's file and mod, or null for anything else (another site, another item type, missing numbers).</summary>
    public static OneClickLink? Parse(string? text)
    {
        var value = text?.Trim();
        if (value is null || !value.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = value[(Scheme.Length + 1)..].TrimStart('/');
        var parts = rest.Split(',');
        if (parts.Length != 3 || !string.Equals(parts[1], "Mod", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(parts[0], UriKind.Absolute, out var download) || download.Scheme != Uri.UriSchemeHttps
            || !(download.Host.Equals("gamebanana.com", StringComparison.OrdinalIgnoreCase) || download.Host.EndsWith(".gamebanana.com", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var segments = download.AbsolutePath.Trim('/').Split('/');
        return segments is ["mmdl", var file] && long.TryParse(file, NumberStyles.None, CultureInfo.InvariantCulture, out var fileId) && fileId > 0
            && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var modId) && modId > 0
            ? new OneClickLink(fileId, modId)
            : null;
    }

    /// <summary>Gives the link to an app that already runs; false when none answers (this copy then opens and installs it).</summary>
    public static bool SendToRunningApp(string link)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(400);
            var bytes = Encoding.UTF8.GetBytes(link);
            pipe.Write(bytes);
            pipe.Flush();
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static readonly CancellationTokenSource Listening = new();

    /// <summary>Takes links from later copies until <see cref="StopListening"/>; each is passed to <paramref name="received"/>.</summary>
    public static void StartListening(Action<OneClickLink> received) => _ = ListenAsync(received, Listening.Token);

    public static void StopListening() => Listening.Cancel();

    private static async Task ListenAsync(Action<OneClickLink> received, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(received);
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (IOException exception)
            {
                AppLog.Error("1-click link: another copy of the app takes the links", exception);
                return;
            }

            try
            {
                await using var owned = pipe;
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                if (Parse(text) is { } link)
                {
                    received(link);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException exception)
            {
                AppLog.Error("1-click link", exception);                       // a broken hand-off; the next one gets a new pipe
            }
        }
    }
}
