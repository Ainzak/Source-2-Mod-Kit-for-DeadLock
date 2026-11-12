using System.Net;

namespace S2ModKit.Desktop.Tests;

public sealed class AppLogTests
{
    [Fact]
    public void ALogKeepsTheNewestSessionsAndWritesShownMessagesOnceAndErrorDetails()
    {
        var data = Path.Combine(Path.GetTempPath(), "s2mk-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var logs = Path.Combine(data, "logs");
            Directory.CreateDirectory(logs);
            for (var i = 0; i < 7; i++)
            {
                File.WriteAllText(Path.Combine(logs, $"s2modkit-2020010{i}-000000.log"), "old");
            }

            AppLog.Start(data, "S2ModKit test start");
            AppLog.Shown("Installed 'Mod'.");
            AppLog.Shown("Installed 'Mod'.");                                   // a repeat is written once
            AppLog.Error("Install", new InvalidOperationException("broken archive"));

            Assert.Equal(AppLog.Keep, Directory.GetFiles(logs, "s2modkit-*.log").Length);   // the current one and the four newest old ones
            var text = AppLog.Read();
            Assert.Contains("S2ModKit test start", text, StringComparison.Ordinal);
            Assert.Single(text.Split('\n'), line => line.Contains("Installed 'Mod'.", StringComparison.Ordinal));
            Assert.Contains("ERROR Install: System.InvalidOperationException: broken archive", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(data, true);
        }
    }

    [Fact]
    public void ASessionEndedByTheUpdateSetupIsNoCrashButATraceAfterItStillIs()
    {
        var data = Path.Combine(Path.GetTempPath(), "s2mk-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var logs = Path.Combine(data, "logs");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "s2modkit-20200101-000000.log"), $"00:00:01.000 INFO  {AppLog.UpdateLine}\n");
            File.WriteAllText(Path.Combine(logs, "crash-20200101-000000.txt"), string.Empty);

            AppLog.Start(data, "S2ModKit test start");

            Assert.False(AppLog.PreviousCrash);
            Assert.False(File.Exists(Path.Combine(logs, "crash-20200101-000000.txt")));

            File.WriteAllText(Path.Combine(logs, "s2modkit-20200102-000000.log"), $"00:00:01.000 INFO  {AppLog.UpdateLine}\n");
            File.WriteAllText(Path.Combine(logs, "crash-20200102-000000.txt"), "Stack overflow.\n   at Some.Method()");
            AppLog.Start(data, "S2ModKit test start again");

            Assert.True(AppLog.PreviousCrash);                                  // a real trace is still reported
        }
        finally
        {
            Directory.Delete(data, true);
        }
    }

    [Fact]
    public void ACrashFileOfAnEndedSessionIsReportedOnceAndOneStillRunningIsLeftAlone()
    {
        var data = Path.Combine(Path.GetTempPath(), "s2mk-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var logs = Path.Combine(data, "logs");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "s2modkit-20200101-000000.log"), "00:00:01.000 SHOWN Opening the preview\n");
            File.WriteAllText(Path.Combine(logs, "crash-20200101-000000.txt"), "Stack overflow.\n   at Some.Method()");
            var running = Path.Combine(logs, "crash-20200102-000000.txt");
            using (new FileStream(running, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete))
            {
                AppLog.Start(data, "S2ModKit test start");

                Assert.True(AppLog.PreviousCrash);
                var text = AppLog.Read();
                Assert.Contains("CRASH The session of s2modkit-20200101-000000.log ended unexpectedly. Trace:", text, StringComparison.Ordinal);
                Assert.Contains("at Some.Method()", text, StringComparison.Ordinal);
                Assert.Contains("Opening the preview", text, StringComparison.Ordinal);
                Assert.False(File.Exists(Path.Combine(logs, "crash-20200101-000000.txt")));
                Assert.True(File.Exists(running));                              // that session still runs
            }

            File.Delete(running);
            AppLog.Start(data, "S2ModKit test start again");
            Assert.False(AppLog.PreviousCrash);                                 // reported once
        }
        finally
        {
            Directory.Delete(data, true);
        }
    }

    [Fact]
    public void FailedRequestsSayWhatHappened()
    {
        Assert.Contains("HTTP 504", AppUpdates.Describe(new HttpRequestException("x", null, HttpStatusCode.GatewayTimeout)), StringComparison.Ordinal);
        Assert.Contains("limiting requests", AppUpdates.Describe(new HttpRequestException("x", null, HttpStatusCode.Forbidden)), StringComparison.Ordinal);
        Assert.Contains("no connection", AppUpdates.Describe(new HttpRequestException("x")), StringComparison.Ordinal);
        Assert.Contains("no answer in time", AppUpdates.Describe(new TaskCanceledException()), StringComparison.Ordinal);
    }
}
