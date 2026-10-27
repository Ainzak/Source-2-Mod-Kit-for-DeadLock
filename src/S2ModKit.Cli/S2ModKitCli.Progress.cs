using S2ModKit.Application;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    /// <summary>Text mode only: one self-overwriting status line on stderr while candidate parts are probed, so a
    /// long discovery never looks like a hang. JSON output stays clean for agents.</summary>
    private async Task<T> WithDiscoveryProgress<T>(bool enabled, TextWriter error, Func<Task<T>> action)
    {
        if (!enabled)
        {
            return await action().ConfigureAwait(false);
        }

        var progress = new StatusLineProgress(error);
        application.DiscoveryProgress = progress;
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            application.DiscoveryProgress = null;
            progress.Finish();
        }
    }

    private sealed class StatusLineProgress(TextWriter error) : IProgress<DiscoveryProgress>
    {
        private int width;

        public void Report(DiscoveryProgress value)
        {
            var text = $"Checking parts ({value.Stage} probe): {value.Completed + 1}/{value.Total}";
            error.Write("\r" + text.PadRight(width));
            width = Math.Max(width, text.Length);
            error.Flush();
        }

        public void Finish()
        {
            if (width > 0)
            {
                error.Write("\r" + new string(' ', width) + "\r");
                error.Flush();
            }
        }
    }
}
