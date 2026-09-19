using System.Diagnostics;

namespace S2ModKit.Cli.Tests;

public sealed class PackagedApplicationSmokeTests
{
    [Fact]
    public async Task StagedApplicationRunsFromAnIsolatedWorkingDirectory()
    {
        var executable = Environment.GetEnvironmentVariable("S2MODKIT_PACKAGED_EXE");
        if (string.IsNullOrWhiteSpace(executable))
        {
            Assert.Skip("Set S2MODKIT_PACKAGED_EXE to run the exact staged release smoke test.");
        }

        Assert.True(File.Exists(executable), $"Packaged executable was not found: {executable}");
        var workingDirectory = Path.Combine(Path.GetTempPath(), "s2modkit-packaged-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var version = await RunAsync(executable, workingDirectory, "--version");
            Assert.Equal(0, version.ExitCode);
            Assert.Contains(".", version.Stdout, StringComparison.Ordinal);

            var help = await RunAsync(executable, workingDirectory, "--help");
            Assert.Equal(0, help.ExitCode);
            Assert.Contains("interactive", help.Stdout, StringComparison.OrdinalIgnoreCase);

            var doctor = await RunAsync(executable, workingDirectory, "doctor", "--format", "json");
            Assert.Equal(0, doctor.ExitCode);
            Assert.Contains("\"status\": \"success\"", doctor.Stdout, StringComparison.Ordinal);
            Assert.Empty(doctor.Stderr);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private static async Task<ProcessResult> RunAsync(string executable, string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.Environment.Remove("S2MODKIT_MESHOPTIMIZER_PATH");
        process.StartInfo.Environment.Remove("S2MODKIT_SOURCE2_VIEWER_PATH");
        process.StartInfo.Environment.Remove("S2MODKIT_EXTERNAL_VERIFY_ROOT");
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start(), "The staged executable could not be started.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
