using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace S2ModKit.Desktop;

/// <summary>
/// A local log of one app session in <c>&lt;data&gt;/logs/s2modkit-&lt;start time&gt;.log</c>: the version and folders on start, every message
/// the window shows, and the details of errors (type, message, stack). The newest <see cref="Keep"/> logs are kept. Nothing is sent
/// anywhere; Settings > Open log / Copy log let the user attach it to a report. Logging never throws.
/// <para>Crashes: <see cref="CaptureCrashes"/> points the process's error output at <c>logs/crash-&lt;start time&gt;.txt</c>, where the
/// runtime writes the trace of a failure no handler sees (a stack overflow, a fault in native code). The runtime writes through
/// the C runtime's <c>stderr</c>, so that stream is reopened (changing the Windows standard handle alone has no effect); <see cref="Close"/> removes
/// the file on a normal exit. A crash file left by an earlier session that no longer runs is copied into the new session's log
/// by <see cref="Start"/> and reported once through <see cref="PreviousCrash"/>.</para>
/// </summary>
internal static partial class AppLog
{
    public const int Keep = 5;
    private const int CrashTextLimit = 48 * 1024;
    private static string? crash;

    /// <summary>True when an earlier session ended without closing; its trace is in this session's log.</summary>
    public static bool PreviousCrash { get; private set; }
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
            PreviousCrash = ReportPreviousCrashes(folder);
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

    /// <summary>The line written before the setup of an update starts: the setup may end the app before it closes, which is no crash.</summary>
    public const string UpdateLine = "Closing for the update";

    public static void ClosingForUpdate() => Info(UpdateLine);

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

    /// <summary>Sends the process's error output to this session's crash file (the runtime's trace of a fatal failure lands there).</summary>
    public static void CaptureCrashes()
    {
        string? log;
        lock (Gate)
        {
            log = path;
        }

        if (log is null || crash is not null || !OperatingSystem.IsWindows())
        {
            return;
        }

        var file = Path.Combine(Path.GetDirectoryName(log)!, "crash-" + Path.GetFileNameWithoutExtension(log)["s2modkit-".Length..] + ".txt");
        try
        {
            var stream = StandardStream(2);
            if (Reopen(file, "w", stream) != 0)
            {
                _ = SetBuffer(stream, 0, 4, 0);                                 // _IONBF: a dying process flushes nothing
                crash = file;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // no crash capture: the app still works
        }
    }

    /// <summary>A normal exit: this session leaves no crash file behind.</summary>
    public static void Close()
    {
        if (crash is not { } file)
        {
            return;
        }

        crash = null;
        Info("Closed");
        try
        {
            _ = Reopen("NUL", "w", StandardStream(2));                          // lets go of the file
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // reported as an unexpected end next time; harmless
        }
    }

    /// <summary>Copies the crash files of sessions that no longer run into the current log and removes them.</summary>
    private static bool ReportPreviousCrashes(string folder)
    {
        var found = false;
        foreach (var file in Directory.EnumerateFiles(folder, "crash-*.txt").Order(StringComparer.Ordinal).ToArray())
        {
            string text;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);   // fails while that session still runs
                var bytes = new byte[Math.Min(stream.Length, CrashTextLimit)];
                stream.ReadExactly(bytes);
                text = Encoding.UTF8.GetString(bytes).Trim() + (stream.Length > CrashTextLimit ? Environment.NewLine + "(cut)" : string.Empty);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var session = "s2modkit-" + Path.GetFileNameWithoutExtension(file)["crash-".Length..] + ".log";
            if (text.Length == 0 && EndedForUpdate(Path.Combine(folder, session)))
            {
                TryDelete(file);                                               // the update's setup ended it: no crash to report
                continue;
            }

            found = true;
            Write("CRASH", $"The session of {session} ended unexpectedly. "
                + (text.Length == 0 ? "No trace was written (the app was ended from outside, or the PC shut down)." : "Trace:" + Environment.NewLine + text)
                + LastLines(Path.Combine(folder, session)));
            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // reported again next time
            }
        }

        return found;
    }

    /// <summary>Whether the session's log ends with the update line (its last few lines, as closing writes little after it).</summary>
    internal static bool EndedForUpdate(string log)
    {
        try
        {
            return File.Exists(log) && File.ReadLines(log).TakeLast(5).Any(line => line.EndsWith(UpdateLine, StringComparison.Ordinal));
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // looked at again next time
        }
    }

    private static string LastLines(string log)
    {
        try
        {
            return File.Exists(log)
                ? $"{Environment.NewLine}Its last log lines:{Environment.NewLine}{string.Join(Environment.NewLine, File.ReadLines(log).TakeLast(25))}"
                : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    [LibraryImport("ucrtbase.dll", EntryPoint = "__acrt_iob_func")]
    private static partial nint StandardStream(uint index);

    [LibraryImport("ucrtbase.dll", EntryPoint = "_wfreopen", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint Reopen(string path, string mode, nint stream);

    [LibraryImport("ucrtbase.dll", EntryPoint = "setvbuf")]
    private static partial int SetBuffer(nint stream, nint buffer, int mode, nuint size);
}
