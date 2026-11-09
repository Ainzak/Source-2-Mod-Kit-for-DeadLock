using System.Globalization;
using System.Text;

namespace S2ModKit.Desktop;

/// <summary>
/// A local log of one app session in <c>&lt;data&gt;/logs/s2modkit-&lt;start time&gt;.log</c>: the version and folders on start, every message
/// the window shows, and the details of errors (type, message, stack). The newest <see cref="Keep"/> logs are kept. Nothing is sent
/// anywhere; Settings > Open log / Copy log let the user attach it to a report. Logging never throws.
/// </summary>
internal static class AppLog
{
    public const int Keep = 5;
    private static readonly Lock Gate = new();
    private static string? path;
    private static string? lastMessage;

    /// <summary>The current session's log file (null before <see cref="Start"/>).</summary>
    public static string? CurrentPath => path;

    /// <summary>Starts this session's log in <paramref name="dataFolder"/> and removes the oldest logs beyond <see cref="Keep"/>.</summary>
    public static void Start(string dataFolder, string startLine)
    {
        try
        {
            var folder = Path.Combine(dataFolder, "logs");
            Directory.CreateDirectory(folder);
            var name = $"s2modkit-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log";
            lock (Gate)
            {
                path = Path.Combine(folder, name);
            }

            foreach (var old in Directory.EnumerateFiles(folder, "s2modkit-*.log").Order(StringComparer.Ordinal).Reverse().Skip(Keep - 1))
            {
                File.Delete(old);
            }

            Info(startLine);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            lock (Gate)
            {
                path = null;                                                     // no log: the app still works
            }
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    /// <summary>A message the window shows (repeats of the same text are written once).</summary>
    public static void Shown(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) || message == lastMessage)
        {
            return;
        }

        lastMessage = message;
        Write("SHOWN", message);
    }

    public static void Error(string context, Exception exception) =>
        Write("ERROR", $"{context}: {exception.GetType().FullName}: {exception.Message}{Environment.NewLine}{exception}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            if (path is null)
            {
                return;
            }

            try
            {
                File.AppendAllText(path, $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level} {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // the log is best effort
            }
        }
    }

    /// <summary>The current log's text (empty when there is none).</summary>
    public static string Read()
    {
        lock (Gate)
        {
            try
            {
                return path is not null && File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }
    }
}
