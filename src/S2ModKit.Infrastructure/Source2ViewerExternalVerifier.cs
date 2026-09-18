using System.Diagnostics;
using System.Text;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Infrastructure;

public sealed class Source2ViewerExternalVerifier : IExternalVerifier
{
    private const int MaximumCapturedCharacters = 64 * 1024;
    private readonly string executablePath;
    private readonly string scratchRoot;
    private readonly TimeSpan timeout;

    public Source2ViewerExternalVerifier(string executablePath, string scratchRoot, TimeSpan? timeout = null)
    {
        this.executablePath = Path.GetFullPath(executablePath);
        this.scratchRoot = Path.GetFullPath(scratchRoot);
        this.timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (!File.Exists(this.executablePath))
        {
            throw new FileNotFoundException("The configured Source 2 Viewer CLI executable does not exist.", this.executablePath);
        }

        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Verifier timeout must be greater than zero and no more than five minutes.");
        }
    }

    public string VerifierName => "source2_viewer";

    public string VerifierVersion => FileVersionInfo.GetVersionInfo(executablePath).ProductVersion ?? "unknown";

    public bool IsAvailable => true;

    public async Task<BoundaryEvidence> VerifyAsync(RewriteCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Directory.CreateDirectory(scratchRoot);
        var runRoot = Path.Combine(scratchRoot, $"verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);
        try
        {
            var fileName = Path.GetFileName(candidate.LogicalPath);
            if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase))
            {
                return new BoundaryEvidence("external_verifier", "failed", "Source 2 Viewer verification requires a candidate logical path ending in .vmdl_c.");
            }

            var candidatePath = Path.Combine(runRoot, fileName);
            await File.WriteAllBytesAsync(candidatePath, candidate.Content.ToArray(), cancellationToken).ConfigureAwait(false);
            if (ContentHash.Compute(await File.ReadAllBytesAsync(candidatePath, cancellationToken).ConfigureAwait(false)) != candidate.Snapshot.Artifact.ContentHash)
            {
                return new BoundaryEvidence("external_verifier", "failed", "The temporary candidate hash changed before Source 2 Viewer could read it.");
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    WorkingDirectory = runRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            process.StartInfo.Environment["TEMP"] = runRoot;
            process.StartInfo.Environment["TMP"] = runRoot;
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add(candidatePath);
            if (!process.Start())
            {
                return new BoundaryEvidence("external_verifier", "failed", "Source 2 Viewer did not start.");
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            var standardOutput = ReadBoundedAsync(process.StandardOutput, timeoutSource.Token);
            var standardError = ReadBoundedAsync(process.StandardError, timeoutSource.Token);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
                var outputText = await standardOutput.ConfigureAwait(false);
                var errorText = await standardError.ConfigureAwait(false);
                var diagnosticText = string.IsNullOrWhiteSpace(errorText) ? outputText : errorText;
                return process.ExitCode == 0
                    ? new BoundaryEvidence("external_verifier", "passed", $"Source 2 Viewer {VerifierVersion} reopened the candidate successfully (exit code 0).")
                    : new BoundaryEvidence("external_verifier", "failed", $"Source 2 Viewer returned exit code {process.ExitCode}: {Summarize(diagnosticText)}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryTerminate(process);
                return new BoundaryEvidence("external_verifier", "failed", $"Source 2 Viewer exceeded the configured timeout of {timeout.TotalSeconds:0} seconds.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new BoundaryEvidence("external_verifier", "failed", $"Source 2 Viewer verification could not complete: {exception.GetType().Name}.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(runRoot))
                {
                    Directory.Delete(runRoot, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            var remaining = MaximumCapturedCharacters - result.Length;
            if (remaining > 0)
            {
                result.Append(buffer, 0, Math.Min(remaining, read));
            }
        }

        return result.ToString();
    }

    private static string Summarize(string value)
    {
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length == 0 ? "no diagnostic text" : normalized[..Math.Min(normalized.Length, 512)];
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
