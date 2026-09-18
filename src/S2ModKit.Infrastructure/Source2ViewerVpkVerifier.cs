using System.Diagnostics;
using System.Text;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Infrastructure;

public sealed class Source2ViewerVpkVerifier : IVpkExternalVerifier
{
    private const int MaximumCapturedCharacters = 128 * 1024;
    private readonly string executablePath;
    private readonly string scratchRoot;
    private readonly TimeSpan timeout;

    public Source2ViewerVpkVerifier(string executablePath, string scratchRoot, TimeSpan? timeout = null)
    {
        this.executablePath = Path.GetFullPath(executablePath);
        this.scratchRoot = Path.GetFullPath(scratchRoot);
        this.timeout = timeout ?? TimeSpan.FromMinutes(2);
        if (!File.Exists(this.executablePath))
        {
            throw new FileNotFoundException("The configured Source 2 Viewer CLI executable does not exist.", this.executablePath);
        }

        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Verifier timeout must be greater than zero and no more than ten minutes.");
        }
    }

    public string VerifierName => "source2_viewer";

    public string VerifierVersion => FileVersionInfo.GetVersionInfo(executablePath).ProductVersion ?? "unknown";

    public bool IsAvailable => true;

    public async Task<BoundaryEvidence> VerifyAsync(
        string candidateVpkPath,
        string entryLogicalPath,
        CancellationToken cancellationToken = default)
    {
        var candidatePath = Path.GetFullPath(candidateVpkPath);
        if (!File.Exists(candidatePath) || !candidatePath.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
        {
            return new BoundaryEvidence("vpk_external_verifier", "failed", "Source 2 Viewer requires an existing .vpk candidate path.");
        }

        Directory.CreateDirectory(scratchRoot);
        var runRoot = Path.Combine(scratchRoot, $"vpk-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);
        try
        {
            var checksum = await RunAsync(["-i", candidatePath, "--vpk_verify"], runRoot, cancellationToken).ConfigureAwait(false);
            if (checksum.ExitCode != 0)
            {
                return new BoundaryEvidence("vpk_external_verifier", "failed", $"Source 2 Viewer checksum verification returned {checksum.ExitCode}: {Summarize(checksum.Diagnostics)}");
            }

            var listing = await RunAsync(["-i", candidatePath, "-l", "-f", entryLogicalPath], runRoot, cancellationToken).ConfigureAwait(false);
            var normalizedEntry = entryLogicalPath.Replace('\\', '/');
            if (listing.ExitCode != 0 || !listing.Diagnostics.Contains(normalizedEntry, StringComparison.OrdinalIgnoreCase))
            {
                return new BoundaryEvidence("vpk_external_verifier", "failed", $"Source 2 Viewer did not confirm target entry '{normalizedEntry}': {Summarize(listing.Diagnostics)}");
            }

            return new BoundaryEvidence(
                "vpk_external_verifier",
                "passed",
                $"Source 2 Viewer {VerifierVersion} passed VPK checksum verification and listed '{normalizedEntry}'.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new BoundaryEvidence("vpk_external_verifier", "failed", $"Source 2 Viewer exceeded the {timeout.TotalSeconds:0}-second timeout.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new BoundaryEvidence("vpk_external_verifier", "failed", $"Source 2 Viewer VPK verification could not complete: {exception.GetType().Name}.");
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

    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.Environment["TEMP"] = workingDirectory;
        process.StartInfo.Environment["TMP"] = workingDirectory;
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("Source 2 Viewer did not start.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var standardOutput = ReadBoundedAsync(process.StandardOutput, timeoutSource.Token);
        var standardError = ReadBoundedAsync(process.StandardError, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            var output = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, string.Concat(output, Environment.NewLine, error));
        }
        catch
        {
            TryTerminate(process);
            throw;
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

    private sealed record ProcessResult(int ExitCode, string Diagnostics);
}
